using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Rendering;

namespace CloudpunkVR;

// Film "fx": a pixel shader of our own run inside the game's frame. The game is a Unity 2018 IL2CPP build, which cannot
// load shaders compiled by another Unity version, so the pass goes around Unity: a plugin event in a command buffer on
// the filmed camera (CameraEvent.BeforeImageEffects: everything drawn, the game's post-processing still to come, so its
// bloom, fog and grading apply on top) copies the camera's bound colour and depth targets and draws one full-screen
// triangle back into the colour target with HLSL compiled at capture start (D3DCompile, vs_5_0/ps_5_0, entry points
// VSMain/PSMain). At AfterForwardAlpha this game has no colour target bound, even after SetRenderTarget(CameraTarget).
// The shader gets each pixel's world position from depth, so one pass can restyle the whole city.
//
//   "fx": { "shader": "D:/…/framework.hlsl",
//           "centre": [x,y,z] | [[t,x,y,z], …],      // world; keyed: linear between keys (two keys a frame apart = a jump)
//           "radius": [[t, r], …],                   // film time → radius, linear between keys
//           "params": [ [x,y,z,w] | [[t,x,y,z,w], …], … ],   // c7… in order; keyed ones interpolate linearly
//           "intoTexture": true,                     // the camera renders into a render texture (flipped on D3D)
//           "event": "BeforeImageEffects" }          // the camera event to draw at
// Constant buffer b0, float4 each: c0–c3 rows of the inverse view-projection (ndc x, ndc y, device depth, 1 → world),
// c4 centre.xyz + radius, c5 camera position + film time, c6 target width, height, 1/width, 1/height, c7–c15 params.
// t0 = colour copy (the target's own format), t1 = depth copy (device depth, reversed Z), s0 point, s1 linear clamp.
internal static unsafe class FilmFx
{
    const int Slots = 16, SlotFloats = 64, ReleaseId = 1_900_000;

    static IntPtr dev, ctx, callback;
    static IntPtr vs, ps, cbuf, sPoint, sLinear, rast, dss;
    static IntPtr colTex, colSrv, depTex, depSrv;
    static uint colKey0, colKey1, colKey2, depKey0, depKey1, depKey2;
    static float* ring;
    static int slot;
    static CommandBuffer cb;
    static Camera cam;
    static bool camMsaa, intoTexture = true, bindTarget = true;
    static CameraEvent evt = CameraEvent.BeforeImageEffects;
    static float[][] centres = { new[] { 0f, 0f, 0f, 0f } };
    static float[] radT = Array.Empty<float>(), radR = Array.Empty<float>();
    static readonly List<float[][]> pars = new();
    static volatile string renderError;
    static volatile int drawn;
    static string loggedError;

    public static bool Active => cb != null;

    public static void Start(JsonObject job, Camera camera)
    {
        Stop();
        if (job["fx"] is not JsonObject fx) return;
        try
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) throw new InvalidOperationException("needs Direct3D 11, running on " + SystemInfo.graphicsDeviceType);
            string path = (string)fx["shader"];
            var src = File.ReadAllText(path);
            centres = new[] { new[] { 0f, 0f, 0f, 0f } };
            if (fx["centre"] is JsonArray c && c.Count > 0)
            {
                if (c[0] is JsonArray)
                {
                    centres = new float[c.Count][];
                    for (int i = 0; i < c.Count; i++) { var k = c[i].AsArray(); centres[i] = new[] { (float)k[0], (float)k[1], (float)k[2], (float)k[3] }; }
                }
                else centres = new[] { new[] { 0f, (float)c[0], (float)c[1], (float)c[2] } };
            }
            var rt = new List<float>(); var rr = new List<float>();
            if (fx["radius"] is JsonArray ra) foreach (var k in ra) { rt.Add((float)k[0]); rr.Add((float)k[1]); }
            radT = rt.ToArray(); radR = rr.ToArray();
            pars.Clear();
            if (fx["params"] is JsonArray pa)
                foreach (var p in pa)
                {
                    var arr = p.AsArray();
                    if (arr.Count > 0 && arr[0] is JsonArray)
                    {
                        var keys = new float[arr.Count][];
                        for (int i = 0; i < arr.Count; i++) { var kk = arr[i].AsArray(); keys[i] = new[] { (float)kk[0], (float)kk[1], (float)kk[2], (float)kk[3], (float)kk[4] }; }
                        pars.Add(keys);
                    }
                    else pars.Add(new[] { new[] { 0f, (float)arr[0], arr.Count > 1 ? (float)arr[1] : 0f, arr.Count > 2 ? (float)arr[2] : 0f, arr.Count > 3 ? (float)arr[3] : 0f } });
                }
            if (pars.Count > 9) throw new InvalidOperationException("at most 9 params (c7–c15)");
            intoTexture = (bool?)fx["intoTexture"] ?? true;
            bindTarget = (bool?)fx["bindTarget"] ?? true;
            evt = fx["event"] != null ? Enum.Parse<CameraEvent>((string)fx["event"]) : CameraEvent.BeforeImageEffects;
            EnsureDevice();
            ReleaseShaders();
            vs = Compile(src, path, "VSMain", "vs_5_0", true);
            ps = Compile(src, path, "PSMain", "ps_5_0", false);
            if (ring == null) ring = (float*)NativeMemory.AllocZeroed((nuint)(Slots * SlotFloats * sizeof(float)));
            callback = (IntPtr)(delegate* unmanaged<int, void>)&OnEvent;
            cam = camera;
            camMsaa = cam.allowMSAA;
            cam.allowMSAA = false;      // the copies are plain textures
            cb = new CommandBuffer { name = "CloudpunkVR film fx" };
            cam.AddCommandBuffer(evt, cb);
            renderError = null; loggedError = null; drawn = 0;
            Plugin.L.LogInfo($"film fx: {Path.GetFileName(path)} compiled, {pars.Count} params, radius keys {radT.Length}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError("film fx: " + e.Message);
            Stop();
        }
    }

    public static void Stop()
    {
        if (cb != null && cam != null) { cam.RemoveCommandBuffer(evt, cb); cam.allowMSAA = camMsaa; }
        cb = null;
        cam = null;
        if (dev != IntPtr.Zero && callback != IntPtr.Zero) GL.IssuePluginEvent(callback, ReleaseId);   // after the frames still queued
    }

    // Main thread, once per rendered frame before culling: this frame's constants into the next ring slot, and the
    // command buffer re-recorded to draw with that slot.
    public static void Update(double t)
    {
        if (cb == null || cam == null) return;
        if (renderError != null && renderError != loggedError) { loggedError = renderError; Plugin.L.LogError("film fx: " + renderError); }
        float ft = (float)t;
        float* d = ring + slot * SlotFloats;
        var proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, intoTexture);
        var inv = (proj * cam.worldToCameraMatrix).inverse;
        for (int r = 0; r < 4; r++) { var row = inv.GetRow(r); d[r * 4] = row.x; d[r * 4 + 1] = row.y; d[r * 4 + 2] = row.z; d[r * 4 + 3] = row.w; }
        var ce = CentreAt(ft);
        d[16] = ce.x; d[17] = ce.y; d[18] = ce.z; d[19] = Curve(radT, radR, ft);
        var cp = cam.transform.position;
        d[20] = cp.x; d[21] = cp.y; d[22] = cp.z; d[23] = ft;
        // c6 is filled on the render thread from the target's real size
        for (int i = 0; i < pars.Count; i++)
        {
            var keys = pars[i];
            float* o = d + 28 + i * 4;
            if (keys.Length == 1 || ft <= keys[0][0]) { for (int c = 0; c < 4; c++) o[c] = keys[0][c + 1]; continue; }
            if (ft >= keys[^1][0]) { for (int c = 0; c < 4; c++) o[c] = keys[^1][c + 1]; continue; }
            int s = 0;
            while (s < keys.Length - 2 && ft > keys[s + 1][0]) s++;
            float x = (ft - keys[s][0]) / Math.Max(1e-6f, keys[s + 1][0] - keys[s][0]);
            for (int c = 0; c < 4; c++) o[c] = keys[s][c + 1] + (keys[s + 1][c + 1] - keys[s][c + 1]) * x;
        }
        cb.Clear();
        // make Unity bind the camera's colour and depth targets now (it may have unbound them after the pass)
        if (bindTarget) cb.SetRenderTarget(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
        cb.IssuePluginEvent(callback, slot);
        slot = (slot + 1) % Slots;
    }

    public static float RadiusAt(float t) => Curve(radT, radR, t);

    public static Vector3 CentreAt(float t)
    {
        var k = centres;
        if (k.Length == 1 || t <= k[0][0]) return new Vector3(k[0][1], k[0][2], k[0][3]);
        if (t >= k[^1][0]) return new Vector3(k[^1][1], k[^1][2], k[^1][3]);
        int s = 0;
        while (s < k.Length - 2 && t > k[s + 1][0]) s++;
        float x = (t - k[s][0]) / Math.Max(1e-6f, k[s + 1][0] - k[s][0]);
        return new Vector3(k[s][1] + (k[s + 1][1] - k[s][1]) * x, k[s][2] + (k[s + 1][2] - k[s][2]) * x, k[s][3] + (k[s + 1][3] - k[s][3]) * x);
    }

    static float Curve(float[] ts, float[] vs_, float t)
    {
        if (ts.Length == 0) return 0f;
        if (t <= ts[0]) return vs_[0];
        if (t >= ts[^1]) return vs_[^1];
        int s = 0;
        while (s < ts.Length - 2 && t > ts[s + 1]) s++;
        float x = (t - ts[s]) / Math.Max(1e-6f, ts[s + 1] - ts[s]);
        return vs_[s] + (vs_[s + 1] - vs_[s]) * x;
    }

    // ---------------------------------------------------------------- setup (main thread; the device is free-threaded)

    static IntPtr* DV => *(IntPtr**)dev;
    static IntPtr* CV => *(IntPtr**)ctx;
    static void Release(IntPtr o) { if (o != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)o)[2])(o); }
    static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})"); }

    static void EnsureDevice()
    {
        if (dev != IntPtr.Zero) return;
        var rt = new RenderTexture(4, 4, 0);
        rt.Create();
        IntPtr tex = rt.GetNativeTexturePtr(), d = IntPtr.Zero, c = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, IntPtr*, void>)(*(IntPtr**)tex)[3])(tex, &d);    // ID3D11DeviceChild::GetDevice
        rt.Release();
        UnityEngine.Object.Destroy(rt);
        if (d == IntPtr.Zero) throw new InvalidOperationException("no D3D11 device");
        ((delegate* unmanaged<IntPtr, IntPtr*, void>)(*(IntPtr**)d)[40])(d, &c);       // ID3D11Device::GetImmediateContext
        dev = d; ctx = c;

        // constant buffer: 16 float4, default usage, updated with UpdateSubresource
        uint* bd = stackalloc uint[6] { (uint)(SlotFloats * 4), 0, 4, 0, 0, 0 };
        IntPtr b = IntPtr.Zero;
        Check(((delegate* unmanaged<IntPtr, uint*, void*, IntPtr*, int>)DV[3])(dev, bd, null, &b), "CreateBuffer");
        cbuf = b;
        // samplers: point and linear, clamp
        foreach (bool linear in new[] { false, true })
        {
            uint* sd = stackalloc uint[13];
            sd[0] = linear ? 0x15u : 0u; sd[1] = sd[2] = sd[3] = 3; ((float*)sd)[4] = 0; sd[5] = 1; sd[6] = 1;
            ((float*)sd)[11] = 0; ((float*)sd)[12] = float.MaxValue;
            IntPtr s = IntPtr.Zero;
            Check(((delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)DV[23])(dev, sd, &s), "CreateSamplerState");
            if (linear) sLinear = s; else sPoint = s;
        }
        // rasterizer: solid, no culling, depth clip on
        uint* rd = stackalloc uint[10] { 3, 1, 0, 0, 0, 0, 1, 0, 0, 0 };
        IntPtr r = IntPtr.Zero;
        Check(((delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)DV[22])(dev, rd, &r), "CreateRasterizerState");
        rast = r;
        // depth-stencil: off
        byte* ds = stackalloc byte[52];
        for (int i = 0; i < 52; i++) ds[i] = 0;
        ((uint*)ds)[2] = 8;                      // DepthFunc ALWAYS
        ds[16] = 0xFF; ds[17] = 0xFF;            // stencil masks
        for (int f = 0; f < 2; f++) { uint* op = (uint*)(ds + 20 + f * 16); op[0] = op[1] = op[2] = 1; op[3] = 8; }
        IntPtr z = IntPtr.Zero;
        Check(((delegate* unmanaged<IntPtr, byte*, IntPtr*, int>)DV[21])(dev, ds, &z), "CreateDepthStencilState");
        dss = z;
    }

    static IntPtr compileFn;

    static IntPtr Compile(string src, string name, string entry, string target, bool vertex)
    {
        if (compileFn == IntPtr.Zero) compileFn = NativeLibrary.GetExport(NativeLibrary.Load("d3dcompiler_47.dll"), "D3DCompile");
        var s = Encoding.UTF8.GetBytes(src);
        var n = Encoding.ASCII.GetBytes(Path.GetFileName(name) + "\0");
        var e = Encoding.ASCII.GetBytes(entry + "\0");
        var t = Encoding.ASCII.GetBytes(target + "\0");
        IntPtr code = IntPtr.Zero, errs = IntPtr.Zero;
        int hr;
        fixed (byte* ps_ = s, pn = n, pe = e, pt = t)
            hr = ((delegate* unmanaged<void*, nuint, byte*, void*, void*, byte*, byte*, uint, uint, IntPtr*, IntPtr*, int>)compileFn)
                (ps_, (nuint)s.Length, pn, null, null, pe, pt, 1u << 15, 0, &code, &errs);
        string msg = null;
        if (errs != IntPtr.Zero)
        {
            var p = ((delegate* unmanaged<IntPtr, byte*>)(*(IntPtr**)errs)[3])(errs);
            var len = ((delegate* unmanaged<IntPtr, nuint>)(*(IntPtr**)errs)[4])(errs);
            msg = Encoding.UTF8.GetString(p, (int)len).TrimEnd('\0', '\n');
            Release(errs);
        }
        if (hr < 0) { Release(code); throw new InvalidOperationException($"{entry}: {msg}"); }
        if (!string.IsNullOrWhiteSpace(msg)) Plugin.L.LogWarning($"film fx {entry}: {msg}");
        var bp = ((delegate* unmanaged<IntPtr, void*>)(*(IntPtr**)code)[3])(code);
        var bl = ((delegate* unmanaged<IntPtr, nuint>)(*(IntPtr**)code)[4])(code);
        IntPtr sh = IntPtr.Zero;
        int slotIdx = vertex ? 12 : 15;     // CreateVertexShader / CreatePixelShader
        int hr2 = ((delegate* unmanaged<IntPtr, void*, nuint, IntPtr, IntPtr*, int>)DV[slotIdx])(dev, bp, bl, IntPtr.Zero, &sh);
        Release(code);
        Check(hr2, vertex ? "CreateVertexShader" : "CreatePixelShader");
        return sh;
    }

    static void ReleaseShaders() { Release(vs); Release(ps); vs = ps = IntPtr.Zero; }

    // ---------------------------------------------------------------- render thread

    [UnmanagedCallersOnly]
    static void OnEvent(int id)
    {
        try
        {
            if (id == ReleaseId)
            {
                Release(colSrv); Release(colTex); Release(depSrv); Release(depTex);
                colSrv = colTex = depSrv = depTex = IntPtr.Zero;
                colKey0 = depKey0 = 0;
                return;
            }
            if (id < 0 || id >= Slots || vs == IntPtr.Zero || ps == IntPtr.Zero) return;
            Draw(ring + id * SlotFloats);
        }
        catch (Exception e) { renderError = e.Message; }
    }

    static uint ColourRead(uint f) => f switch
    {
        1 => 2, 9 => 10, 23 => 24, 27 => 28, 90 => 87, _ => f,     // typeless → a typed view of the same bits
    };
    static uint DepthCopy(uint f) => f switch { 45 => 44, 40 => 39, 20 => 19, 55 => 53, _ => f };
    static uint DepthRead(uint f) => f switch { 44 => 46, 39 => 41, 19 => 21, 53 => 56, _ => 0 };

    static void Draw(float* consts)
    {
        IntPtr* rtvs = stackalloc IntPtr[8];
        IntPtr dsv = IntPtr.Zero, cres = IntPtr.Zero, dres = IntPtr.Zero;
        for (int i = 0; i < 8; i++) rtvs[i] = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr*, void>)CV[89])(ctx, 8, rtvs, &dsv);            // OMGetRenderTargets
        try
        {
            if (rtvs[0] == IntPtr.Zero || dsv == IntPtr.Zero) { renderError = $"targets bound at {evt}: colour {(rtvs[0] != IntPtr.Zero ? "yes" : "no")}, depth {(dsv != IntPtr.Zero ? "yes" : "no")}"; return; }
            ((delegate* unmanaged<IntPtr, IntPtr*, void>)(*(IntPtr**)rtvs[0])[7])(rtvs[0], &cres);          // ID3D11View::GetResource
            ((delegate* unmanaged<IntPtr, IntPtr*, void>)(*(IntPtr**)dsv)[7])(dsv, &dres);
            uint dimC = 0, dimD = 0;
            ((delegate* unmanaged<IntPtr, uint*, void>)(*(IntPtr**)cres)[7])(cres, &dimC);                  // ID3D11Resource::GetType
            ((delegate* unmanaged<IntPtr, uint*, void>)(*(IntPtr**)dres)[7])(dres, &dimD);
            if (dimC != 3 || dimD != 3) { renderError = $"targets are not 2D textures ({dimC}, {dimD})"; return; }
            uint* cd = stackalloc uint[11]; uint* dd = stackalloc uint[11]; uint* vd = stackalloc uint[8];
            ((delegate* unmanaged<IntPtr, uint*, void>)(*(IntPtr**)cres)[10])(cres, cd);                    // ID3D11Texture2D::GetDesc
            ((delegate* unmanaged<IntPtr, uint*, void>)(*(IntPtr**)dres)[10])(dres, dd);
            ((delegate* unmanaged<IntPtr, uint*, void>)(*(IntPtr**)rtvs[0])[8])(rtvs[0], vd);              // ID3D11RenderTargetView::GetDesc
            if (cd[5] > 1 || dd[5] > 1) { renderError = $"multisampled targets ({cd[5]}x, {dd[5]}x)"; return; }
            if (DepthRead(DepthCopy(dd[4])) == 0) { renderError = $"depth format {dd[4]} not readable"; return; }
            Ensure(ref colTex, ref colSrv, ref colKey0, ref colKey1, ref colKey2, cd, cd[4], vd[0] != 0 ? vd[0] : ColourRead(cd[4]));
            Ensure(ref depTex, ref depSrv, ref depKey0, ref depKey1, ref depKey2, dd, DepthCopy(dd[4]), DepthRead(DepthCopy(dd[4])));
            var copy = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)CV[47];                         // CopyResource
            copy(ctx, colTex, cres);
            copy(ctx, depTex, dres);
            consts[24] = cd[0]; consts[25] = cd[1]; consts[26] = 1f / cd[0]; consts[27] = 1f / cd[1];
            Pass(rtvs, dsv, cd[0], cd[1], consts);
            drawn++;
        }
        finally
        {
            for (int i = 0; i < 8; i++) Release(rtvs[i]);
            Release(dsv); Release(cres); Release(dres);
        }
    }

    // A copy texture matching the source (same size, mips, array, bits), readable through an SRV of format srvFmt.
    static void Ensure(ref IntPtr tex, ref IntPtr srv, ref uint k0, ref uint k1, ref uint k2, uint* src, uint fmt, uint srvFmt)
    {
        uint key0 = src[0] | (src[1] << 16), key1 = fmt | (srvFmt << 16), key2 = src[2] | (src[3] << 16);
        if (tex != IntPtr.Zero && k0 == key0 && k1 == key1 && k2 == key2) return;
        Release(srv); Release(tex); srv = tex = IntPtr.Zero;
        uint* d = stackalloc uint[11];
        for (int i = 0; i < 11; i++) d[i] = src[i];
        d[4] = fmt; d[5] = 1; d[6] = 0; d[7] = 0; d[8] = 8; d[9] = 0; d[10] = 0;    // default usage, shader resource only
        IntPtr t = IntPtr.Zero, v = IntPtr.Zero;
        Check(((delegate* unmanaged<IntPtr, uint*, void*, IntPtr*, int>)DV[5])(dev, d, null, &t), $"CreateTexture2D (format {fmt})");
        uint* sd = stackalloc uint[6] { srvFmt, 4, 0, 1, 0, 0 };                  // TEXTURE2D, mip 0 only
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, uint*, IntPtr*, int>)DV[7])(dev, t, sd, &v);
        if (hr < 0) { Release(t); Check(hr, $"CreateShaderResourceView (format {srvFmt})"); }
        tex = t; srv = v; k0 = key0; k1 = key1; k2 = key2;
    }

    static void Pass(IntPtr* rtvs, IntPtr dsv, uint w, uint h, float* consts)
    {
        var C = CV;
        // save what the pass changes
        IntPtr oVS = IntPtr.Zero, oPS = IntPtr.Zero, oGS = IntPtr.Zero, oHS = IntPtr.Zero, oDS = IntPtr.Zero, oIL = IntPtr.Zero,
               oRS = IntPtr.Zero, oBS = IntPtr.Zero, oDSS = IntPtr.Zero, oCB = IntPtr.Zero;
        IntPtr* oSrv = stackalloc IntPtr[2] { IntPtr.Zero, IntPtr.Zero }; IntPtr* oSmp = stackalloc IntPtr[2] { IntPtr.Zero, IntPtr.Zero };
        uint zero = 0, topo = 0, sref = 0, mask = 0, nvp = 16;
        float* bf = stackalloc float[4];
        float* vps = stackalloc float[6 * 16];
        ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr*, uint*, void>)C[76])(ctx, &oVS, null, &zero); zero = 0;
        ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr*, uint*, void>)C[74])(ctx, &oPS, null, &zero); zero = 0;
        ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr*, uint*, void>)C[82])(ctx, &oGS, null, &zero); zero = 0;
        ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr*, uint*, void>)C[98])(ctx, &oHS, null, &zero); zero = 0;
        ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr*, uint*, void>)C[102])(ctx, &oDS, null, &zero); zero = 0;
        ((delegate* unmanaged<IntPtr, IntPtr*, void>)C[78])(ctx, &oIL);
        ((delegate* unmanaged<IntPtr, uint*, void>)C[83])(ctx, &topo);
        ((delegate* unmanaged<IntPtr, IntPtr*, void>)C[94])(ctx, &oRS);
        ((delegate* unmanaged<IntPtr, IntPtr*, float*, uint*, void>)C[91])(ctx, &oBS, bf, &mask);
        ((delegate* unmanaged<IntPtr, IntPtr*, uint*, void>)C[92])(ctx, &oDSS, &sref);
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)C[73])(ctx, 0, 2, oSrv);
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)C[75])(ctx, 0, 2, oSmp);
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)C[77])(ctx, 0, 1, &oCB);
        ((delegate* unmanaged<IntPtr, uint*, float*, void>)C[95])(ctx, &nvp, vps);

        // the pass
        IntPtr rtv0 = rtvs[0];
        ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)C[33])(ctx, 1, &rtv0, IntPtr.Zero);
        float* vp = stackalloc float[6] { 0, 0, w, h, 0, 1 };
        ((delegate* unmanaged<IntPtr, uint, float*, void>)C[44])(ctx, 1, vp);
        ((delegate* unmanaged<IntPtr, IntPtr, void>)C[43])(ctx, rast);
        ((delegate* unmanaged<IntPtr, IntPtr, float*, uint, void>)C[35])(ctx, IntPtr.Zero, null, 0xFFFFFFFF);
        ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)C[36])(ctx, dss, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, void>)C[17])(ctx, IntPtr.Zero);
        ((delegate* unmanaged<IntPtr, uint, void>)C[24])(ctx, 4);                      // TRIANGLELIST
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[11])(ctx, vs, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[9])(ctx, ps, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[23])(ctx, IntPtr.Zero, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[60])(ctx, IntPtr.Zero, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[64])(ctx, IntPtr.Zero, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)C[48])(ctx, cbuf, 0, null, consts, 0, 0);
        IntPtr b = cbuf;
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)C[16])(ctx, 0, 1, &b);
        IntPtr* srv = stackalloc IntPtr[2] { colSrv, depSrv };
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)C[8])(ctx, 0, 2, srv);
        IntPtr* smp = stackalloc IntPtr[2] { sPoint, sLinear };
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)C[10])(ctx, 0, 2, smp);
        ((delegate* unmanaged<IntPtr, uint, uint, void>)C[13])(ctx, 3, 0);             // Draw

        // put everything back
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)C[8])(ctx, 0, 2, oSrv);
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)C[10])(ctx, 0, 2, oSmp);
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)C[16])(ctx, 0, 1, &oCB);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[11])(ctx, oVS, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[9])(ctx, oPS, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[23])(ctx, oGS, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[60])(ctx, oHS, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)C[64])(ctx, oDS, null, 0);
        ((delegate* unmanaged<IntPtr, IntPtr, void>)C[17])(ctx, oIL);
        ((delegate* unmanaged<IntPtr, uint, void>)C[24])(ctx, topo);
        ((delegate* unmanaged<IntPtr, IntPtr, void>)C[43])(ctx, oRS);
        ((delegate* unmanaged<IntPtr, IntPtr, float*, uint, void>)C[35])(ctx, oBS, bf, mask);
        ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)C[36])(ctx, oDSS, sref);
        if (nvp > 0) ((delegate* unmanaged<IntPtr, uint, float*, void>)C[44])(ctx, nvp, vps);
        ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)C[33])(ctx, 8, rtvs, dsv);
        Release(oVS); Release(oPS); Release(oGS); Release(oHS); Release(oDS); Release(oIL); Release(oRS); Release(oBS); Release(oDSS); Release(oCB);
        Release(oSrv[0]); Release(oSrv[1]); Release(oSmp[0]); Release(oSmp[1]);
    }

    public static string Status => cb == null ? "off" : $"on, {drawn} passes" + (renderError != null ? ", error: " + renderError : "");
}
