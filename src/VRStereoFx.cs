using System;
using UnityEngine;

namespace CloudpunkVR;

// The sky texture behind the fog (_GlobalFogSkybox): the fog blends a small screen-space picture of the sky into
// distant pixels. The game draws it once per frame, and the plugin used to draw it with the left eye's projection,
// so the right eye sampled clouds and sky glow meant for the left eye: on a headset whose eyes have mirrored
// asymmetric frusta (Quest 3) the fogged city glows differently in each eye. The game's sky camera now draws one
// picture over a frustum that covers both eyes (VRCameras.OnPreCull), and right after it each eye's part is cut out
// into a texture of its own (one small blit each), which that eye's render then uses. Drawing the sky twice instead
// (a camera render per eye from the render callbacks) cost 0.7 ms of GPU and stalled the main thread on the render
// thread: 12% of its time.
// Hx volumetric lights get each eye's matrices in VRHx (the right eye's build starts from OnPostRender here).
internal static class VRStereoFx
{
    internal static string CaptureWhich = "vol"; // dev: vol (VolumetricTexture at each eye's post-render) or temporal
    internal static bool SkyPerEye = true;  // dev A/B ("stereofx sky 0|1")

    static readonly int SkyId = Shader.PropertyToID("_GlobalFogSkybox");
    static RenderTexture[] sky = new RenderTexture[3];
    static ViewToGlobalTexture skySource;
    static float nextSkySearch;
    static bool loggedSky;

    public static void Apply()
    {
        Camera.onPreRender = Camera.onPreRender + (Camera.CameraCallback)new Action<Camera>(OnPreRender);
        Camera.onPostRender = Camera.onPostRender + (Camera.CameraCallback)new Action<Camera>(OnPostRender);
    }

    static bool IsHmdMain(Camera cam)
    {
        var g = Global.Instance;
        return g != null && g.mainCamera != null && cam != null && cam.GetInstanceID() == g.mainCamera.GetInstanceID()
               && cam.targetTexture == null && cam.stereoTargetEye != StereoTargetEyeMask.None && UnityEngine.XR.XRSettings.enabled;
    }

    static bool IsSpectator(Camera cam)
    {
        var g = Global.Instance;
        return Coo.CooView.Rendering && g != null && g.mainCamera != null && cam != null && cam.GetInstanceID() == g.mainCamera.GetInstanceID();
    }

    static void OnPreRender(Camera cam)
    {
        using var __pt = PluginTiming.Measure("cb:stereofx.pre");
        try
        {
            if (cam != null && cam.stereoActiveEye != Camera.MonoOrStereoscopicEye.Mono
                && cam == VRCameras.WorldCamera(Global.Instance != null ? Global.Instance.mainCamera : null))
            {
                if (HxLogFrame == Time.frameCount) LogHxMatrices(cam, "pre");
            }
            bool spectator = IsSpectator(cam);
            if (!spectator && !IsHmdMain(cam)) return;
            var active = cam.stereoActiveEye;
            if (!spectator && active == Camera.MonoOrStereoscopicEye.Mono) return;
            bool right = !spectator && active == Camera.MonoOrStereoscopicEye.Right;
            if (SkyPerEye) UseSky(spectator ? 0 : right ? 1 : 0);
            if (right && CaptureFrame == Time.frameCount && CaptureWhich == "temporal") Grab(cam, 0, true);
        }
        catch (Exception e)
        {
            if (!reported) { reported = true; Plugin.L.LogError("stereo fx: " + e); }
        }
    }
    static bool reported;

    // Dev (hxcapture): copies of Hx's volumetric buffer as each eye finished rendering, in the same frame.
    internal static int CaptureFrame = -1;
    internal static RenderTexture[] Captured = new RenderTexture[2];

    static void OnPostRender(Camera cam)
    {
        using var __pt = PluginTiming.Measure("cb:stereofx.post");
        if (SkyPerEye && skySource != null && skySource.skycam != null && cam != null && cam.GetInstanceID() == skySource.skycam.GetInstanceID())
            CutSky(cam);
        if (cam != null && cam.stereoActiveEye != Camera.MonoOrStereoscopicEye.Mono
            && cam == VRCameras.WorldCamera(Global.Instance != null ? Global.Instance.mainCamera : null))
        {
            if (HxLogFrame == Time.frameCount) LogHxMatrices(cam, "post");
            if (cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left) VRHx.RebuildForEye(cam, 1);
        }
        if (CaptureFrame == Time.frameCount && cam != null && cam == VRCameras.WorldCamera(Global.Instance != null ? Global.Instance.mainCamera : null)
            && cam.stereoActiveEye != Camera.MonoOrStereoscopicEye.Mono && CaptureWhich == "vol")
            Grab(cam, cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0, false);
    }

    // Dev (hxmatrices): the matrices Hx's command buffers left in the shader globals, read as each eye starts and
    // finishes rendering, against that eye's own matrices. Compact form: m00 m02 m11 m12 m22 m23 (m03 m13 for views).
    internal static int HxLogFrame = -1;

    static string P(Matrix4x4 m) => $"[{m.m00:F3} {m.m02:F3} {m.m11:F3} {m.m12:F3} {m.m22:F4} {m.m23:F4}]";
    static string V(Matrix4x4 m) => $"[t {m.m03:F3} {m.m13:F3} {m.m23:F3} r {m.m00:F3} {m.m02:F3}]";

    static void LogHxMatrices(Camera cam, string when)
    {
        var e = cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
        var p = cam.GetStereoProjectionMatrix(e);
        var ip = Shader.GetGlobalMatrix(HxVolumetricCamera.InverseProjectionMatrixPID);
        var ip2 = Shader.GetGlobalMatrix(HxVolumetricCamera.InverseProjectionMatrix2PID);
        var iv = Shader.GetGlobalMatrix(HxVolumetricCamera.InverseViewMatrixPID);
        Plugin.L.LogInfo($"hx {when} {e}: invProj^-1 {P(ip.inverse)} invProj2^-1 {P(ip2.inverse)} | eye {P(p)} gpu(rt) {P(GL.GetGPUProjectionMatrix(p, true))} gpu {P(GL.GetGPUProjectionMatrix(p, false))} cam.proj {P(cam.projectionMatrix)}");
        Plugin.L.LogInfo($"hx {when} {e}: invView^-1 {V(iv.inverse)} | eye view {V(cam.GetStereoViewMatrix(e))} cam.w2c {V(cam.worldToCameraMatrix)}");
        var c2w = Shader.GetGlobalMatrix(Shader.PropertyToID("hxCameraToWorld"));
        var ivp = Shader.GetGlobalMatrix(Shader.PropertyToID("_InvViewProj"));
        Plugin.L.LogInfo($"hx {when} {e}: hxCameraToWorld^-1 {V(c2w.inverse)} _InvViewProj^-1 {P(ivp.inverse)} want {P(GL.GetGPUProjectionMatrix(p, false) * cam.GetStereoViewMatrix(e))}");
        var tl = DevCommands.TestLight;
        if (tl != null && tl.activeInHierarchy)
        {
            var c = p * cam.GetStereoViewMatrix(e) * new Vector4(tl.transform.position.x, tl.transform.position.y, tl.transform.position.z, 1);
            Plugin.L.LogInfo($"hx {when} {e}: test light at u={(c.x / c.w + 1) / 2:F4} v={(c.y / c.w + 1) / 2:F4} (eye image, v up)");
        }
        var hx = cam.GetComponent<HxVolumetricCamera>();
        if (hx != null)
            Plugin.L.LogInfo($"hx {when} {e}: MatrixVP {P(hx.MatrixVP)} want {P(GL.GetGPUProjectionMatrix(p, true) * cam.GetStereoViewMatrix(e))} | lastVP {P(Shader.GetGlobalMatrix(Shader.PropertyToID("hxLastVP")))} | history {(hx.TemporalTexture != null ? hx.TemporalTexture.name + " " + hx.TemporalTexture.GetInstanceID() : "-")}");
    }

    internal static void Grab(Camera cam, int slot, bool temporal)
    {
        var hxc = cam != null ? cam.GetComponent<HxVolumetricCamera>() : null;
        var src = hxc == null ? null : temporal ? hxc.TemporalTexture : hxc.VolumetricTexture;
        if (src == null) return;
        if (Captured[slot] == null || Captured[slot].width != src.width) Captured[slot] = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, Captured[slot]);
    }

    static Vector4 union;   // tangents of the both-eye frustum the sky was drawn with: left, right, bottom, top

    // The frustum around both eyes (the eyes share the head's orientation; only their frusta differ).
    public static Matrix4x4 SkyUnionProjection(Camera main, float near, float far)
    {
        var l = Tangents(main.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left));
        var r = Tangents(main.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right));
        union = new Vector4(Mathf.Min(l.x, r.x), Mathf.Max(l.y, r.y), Mathf.Min(l.z, r.z), Mathf.Max(l.w, r.w));
        return VRCameras.Frustum(union.x, union.y, union.z, union.w, near, far);
    }

    // left, right, bottom, top tangents of a projection (ndc = m00 * x/-z - m02)
    static Vector4 Tangents(Matrix4x4 p) =>
        new Vector4((-1 + p.m02) / p.m00, (1 + p.m02) / p.m00, (-1 + p.m12) / p.m11, (1 + p.m12) / p.m11);

    // Right after the sky camera rendered: each eye's part of its picture into that eye's texture.
    static void CutSky(Camera skycam)
    {
        var g = Global.Instance;
        var main = g != null ? g.mainCamera : null;
        var src = skycam.targetTexture != null ? skycam.targetTexture : skySource.skyboxRT;
        if (main == null || src == null || union.y <= union.x || union.w <= union.z) return;
        var prev = RenderTexture.active;
        for (int e = 0; e < 2; e++)
        {
            var t = Tangents(main.GetStereoProjectionMatrix(e == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right));
            float uw = union.y - union.x, uh = union.w - union.z;
            var scale = new Vector2((t.y - t.x) / uw, (t.w - t.z) / uh);
            var offset = new Vector2((t.x - union.x) / uw, (t.z - union.z) / uh);
            int w = Mathf.Max(32, Mathf.RoundToInt(src.width * scale.x)), h = Mathf.Max(32, Mathf.RoundToInt(src.height * scale.y));
            var rt = sky[e];
            if (rt == null || rt.width != w || rt.height != h)
            {
                if (rt != null) rt.Release();
                rt = sky[e] = new RenderTexture(w, h, 0, src.format) { name = "CloudpunkVR_Sky" + e };
                rt.Create();
            }
            Graphics.Blit(src, rt, scale, offset);
        }
        RenderTexture.active = prev;
        skyCutFrame = Time.frameCount;
        if (!loggedSky && DevCommands.Enabled)
        {
            loggedSky = true;
            Plugin.L.LogInfo($"stereo fx: sky cut per eye from {src.width}x{src.height} over tangents x[{union.x:F2},{union.y:F2}] y[{union.z:F2},{union.w:F2}] -> {sky[0].width}x{sky[0].height}");
        }
    }

    static int skyCutFrame = -1;

    static void UseSky(int eye)
    {
        if (skySource == null || skySource.skycam == null)
        {
            if (Time.unscaledTime < nextSkySearch) return;
            nextSkySearch = Time.unscaledTime + 2f;
            skySource = null;
            foreach (var v in UnityEngine.Object.FindObjectsOfType<ViewToGlobalTexture>())
                if (v.skycam != null) { skySource = v; break; }
            return;
        }
        if (skyCutFrame != Time.frameCount || sky[eye] == null) return;
        Shader.SetGlobalTexture(SkyId, sky[eye]);
    }

}
