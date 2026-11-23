using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using UnityEngine;

namespace CloudpunkVR;

// The promo's VRStage set rebuilt inside the game for a film capture (Film job "stage" / "actor"), so the performer and the
// set are rendered by the game together with the city (its lights, fog, rain, bloom), not composited afterwards.
// Stage space = VRStage world space (metres, Unity axes; the performer stands near (0.13, 0, 1.3) facing +Z); "origin" and
// "yaw" place it in the game world. Everything is driven by the film time t (seconds from the capture's start).
//
//   "actor": { "dir": "D:/…/avatar", "start": 0 }   VRStage AvatarExport output, played from film time "start"; holds
//                                                     its last frame afterwards. Lit by the game (MyShaders/SimpleStandard).
//              "scale": 1.0                          stage space scaled about "origin" (with "stage", the stage's "scale")
//              "freezeAt": 24.2                      film time from which the performer holds that frame
//              "matte": ["Hood", "Tech Pants"],      parts drawn with no specular and no reflection at all (Standard
//                                                     specular setup, black specular, smoothness 0: plain Lambert)
//              "exit": { "t": 5.0, "dur": 1.6, "sweep": [0,0,1], "jitter": 0.18, "particles": 24000,
//                        "size": 0.018, "life": [1.0, 2.0], "drift": [0,0.6,0], "gain": 1.6, "flash": [0.6,0.95,1.2],
//                        "tint": 0.5 }   (Particles/Additive tint; it doubles: 1.0 lets the sparks bloom)
//                                                     the performer breaks into glowing particles: a front sweeps across
//                                                     him along "sweep" (stage space) in "dur" seconds; every triangle
//                                                     it passes is dropped and some of them leave a particle (the
//                                                     texture's colour, flashing "flash" at birth) that drifts away with
//                                                     acceleration "drift" (stage space, m/s²) and fades over its life
//   "stage": { "origin": [x,y,z], "yaw": 0,
//              "radius": 9, "height": 14, "ledPitch": 0.08, "panelPitch": 0.42, "floorPitch": 0.42,
//              "video": { "file": "D:/…/wall.bin", "fps": 30, "start": 0, "gain": 0.9, "rect": [x0,x1,y0,y1] },
//              "dissolve": { "t": 2.0, "speed": 5, "width": 1.2, "panelDelay": 0.35, "centre": [0.13,0,1.3] },
//              "lights": { "key": 1.0, "rim": 1.0, "screen": 1.0, "after": 0.35 } }
// The wall is an LED screen of ledPitch tiles over the video rect (each tile one pixel of the frames in wall.bin: header
// int32 width, height, count, then RGB24 frames bottom row first, already sampled one pixel per tile) in front of dark
// panels; the floor is dark tiles with cyan light in the seams. The grid dissolve: a sphere grows from the performer's
// feet; every LED tile, panel and floor tile it passes flashes and shrinks away (panels a beat after the LEDs), leaving the
// real street and city.
internal static class FilmStage
{
    static GameObject root;
    static Transform rootT;
    static readonly List<Light> lights = new();
    static readonly List<float> lightBase = new();
    static float lightsAfter = 0.35f;
    static Light screenLight;

    sealed class Tiles
    {
        public Mesh mesh;
        public Vector3[] centre, normal;   // per tile
        public Vector3[] du, dv;            // half extents
        public float[] dist;                // from the dissolve centre
        public float delay;
        public Vector3[] verts;
        public Color32[] cols;
        public Color32[] baseCol;
        public bool flashOnly;              // additive overlay: colour comes from the flash
        public Color32 flashCol;
        public GameObject go;
        public bool done;                   // every tile gone: switched off, never placed again
    }

    // Per-call cost of the stage's parts (Stopwatch ticks, summed until Film reads and clears them).
    internal static long tActor, tExit, tTiles, tPeople;
    static Vector3[][] partNrm;
    static bool ledPlaced;
    static int peopleTick;

    /// <summary>Upload a tile set's placement for front radius rho; once the front has passed every tile the set is
    /// switched off and skipped from then on (it was re-uploaded every sub-frame for the rest of the film: ~0.5 M
    /// vertices and colours for the LED wall).</summary>
    static void Upload(Tiles T, float rho, float[] delays = null, bool colours = true, bool verts = true)
    {
        if (T == null || T.done) return;
        bool alive = Place(T, rho, delays);
        if (rho >= 0 && !alive) { T.done = true; T.go.SetActive(false); return; }
        if (verts) T.mesh.vertices = FilmNative.Of(T.verts);
        if (colours) T.mesh.colors32 = FilmNative.Of(T.cols);
    }
    static Tiles led, panels, floor, flash;
    static float dissolveT = float.MaxValue, speed = 5f, width = 1.2f, panelDelay = 0.35f;
    static Vector3 dissolveCentre = new(0.13f, 0f, 1.3f);

    // video
    static FileStream video;
    static int vw, vh, vcount, vrowOffset;
    static float vfps = 30f, vstart, vgain = 0.9f;
    static byte[] vframe;
    static int vLoaded = -1;
    static int ledCols, ledRows;

    // actor
    static FileStream frames;
    static int[] partVerts;
    static Mesh[] partMeshes;
    static long frameBytes;
    static int frameCount, frameLoaded = -1;
    static float actorFps = 30f, actorStart;
    static float actorFreeze = float.MaxValue;   // actor "freezeAt": film time the performance holds still from
    static byte[] fbuf;
    static Vector3[][] partPos;
    static int[][][] partTris;                 // [part][submesh] triangles as drawn
    static Vector2[][] partUv;
    static (Color32[] px, int w, int h, Color tint)?[][] subTex;

    // actor exit
    static JsonObject exitJ;
    static float exitT = float.MaxValue, exitDur = 1.6f, exitJitter = 0.18f;
    static float[][][] triTau;                 // [part][submesh][tri] front position (0…1+jitter) at which it goes
    static float exitP = -1f;
    static Vector3 exitDrift = new(0, 0.6f, 0);
    static float exitSize = 0.018f, exitGain = 1.6f, lifeMin = 1.0f, lifeMax = 2.0f;
    static Color exitFlash = new(0.6f, 0.95f, 1.2f);
    // particles: one per chosen triangle
    static int pCount;
    static int[] pPart, pSub, pTri;
    static float[] pTau, pBorn, pLife, pSize, pSeed;
    static Vector3[] pPos0, pVel;
    static Color[] pCol;
    static bool[] pSeen;
    static Mesh pMesh;
    static Vector3[] pVerts;
    static Color32[] pCols;

    public static bool Active => root != null;
    /// <summary>The stage/actor root (FilmCockpit seats the performer in the car interior by re-parenting it).</summary>
    internal static Transform Root => rootT;

    public static void Start(JsonObject job)
    {
        Stop();
        var st = job["stage"] as JsonObject;
        var ac = job["actor"] as JsonObject;
        if (st == null && ac == null) return;
        var o = (st ?? ac);
        root = new GameObject("CloudpunkVR_FilmStage");
        rootT = root.transform;
        var origin = o["origin"] is JsonArray oa ? V3(oa) : Vector3.zero;
        rootT.SetPositionAndRotation(origin, Quaternion.Euler(0, (float?)o["yaw"] ?? 0f, 0));
        // "scale": the whole stage space scaled about the origin (the game's furniture is ~1.3× real size: a 0.66 m bench)
        float scale = (float?)o["scale"] ?? 1f;
        if (scale != 1f) rootT.localScale = new Vector3(scale, scale, scale);
        try
        {
            if (st != null) BuildStage(st);
            if (ac != null) BuildActor(ac);
            Plugin.L.LogInfo($"film stage: built at {Diag.V(origin)} yaw {rootT.eulerAngles.y:F1}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError("film stage: " + e);
            Stop();
        }
    }

    public static void Stop()
    {
        foreach (var (r, on) in intruders) if (r != null) r.enabled = on;
        foreach (var go in hiddenPeople) if (go != null) go.SetActive(true);
        hiddenPeople.Clear();
        intruders.Clear();
        if (root != null) UnityEngine.Object.Destroy(root);
        root = null;
        video?.Dispose(); video = null;
        frames?.Dispose(); frames = null;
        led = panels = floor = flash = null;
        partPos = null; partTris = null; partUv = null; subTex = null; triTau = null; exitJ = null; exitT = float.MaxValue; exitP = -1f;
        pCount = 0; pMesh = null;
        lights.Clear(); lightBase.Clear(); screenLight = null;
        vLoaded = frameLoaded = -1;
        placedOnce = false;
        ledPlaced = false; peopleTick = 0; pCleared = false;
        partNrm = null;
        FilmNative.Clear();
        partMeshes = null; frameBytes = 0;
        dissolveT = float.MaxValue;
    }

    // ---------------------------------------------------------------- set

    static Shader Find(params string[] names)
    {
        foreach (var n in names) { var s = Shader.Find(n); if (s != null) return s; }
        throw new InvalidOperationException("no shader among " + string.Join(", ", names));
    }

    static void BuildStage(JsonObject st)
    {
        float R = (float?)st["radius"] ?? 9f, H = (float?)st["height"] ?? 14f;
        wallR = R;
        seamGain = (float?)st["seam"] ?? 0.12f;
        clearSet = (bool?)st["clearSet"] ?? false;
        clearRadius = (float?)st["clearRadius"] ?? R + 0.3f;
        flashGain = (float?)st["flash"] ?? 0.45f;
        CollectIntruders(R, H);
        float ledPitch = (float?)st["ledPitch"] ?? 0.08f, panelPitch = (float?)st["panelPitch"] ?? 0.42f, floorPitch = (float?)st["floorPitch"] ?? 0.42f;
        if (st["dissolve"] is JsonObject d)
        {
            dissolveT = (float?)d["t"] ?? float.MaxValue;
            speed = (float?)d["speed"] ?? speed;
            width = (float?)d["width"] ?? width;
            panelDelay = (float?)d["panelDelay"] ?? panelDelay;
            if (d["centre"] is JsonArray c) dissolveCentre = V3(c);
        }
        var additive = Find("Legacy Shaders/Particles/Additive", "Mobile/Particles/Additive");
        var lit = Find("MyShaders/SimpleStandard", "Standard");
        var white = new Texture2D(4, 4) { name = "CloudpunkVR_White" };
        var px = new Color32[16]; for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
        white.SetPixels32(px); white.Apply();

        Material Add(float tint) { var m = new Material(additive) { mainTexture = white }; m.SetColor("_TintColor", new Color(tint, tint, tint, 0.5f)); return m; }
        Material Dark(Color c) { var m = new Material(lit) { mainTexture = white }; if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c); if (m.HasProperty("_Color")) m.SetColor("_Color", c); return m; }

        // LED tiles over the video rect
        var vrect = new Vector4(-16, 16, -2, 16);
        if (st["video"] is JsonObject vj)
        {
            if (vj["rect"] is JsonArray ra) vrect = new Vector4((float)ra[0], (float)ra[1], (float)ra[2], (float)ra[3]);
            vfps = (float?)vj["fps"] ?? 30f; vstart = (float?)vj["start"] ?? 0f; vgain = (float?)vj["gain"] ?? 0.9f;
            video = File.OpenRead((string)vj["file"]);
            var hdr = new byte[12]; video.Read(hdr, 0, 12);
            vw = BitConverter.ToInt32(hdr, 0); vh = BitConverter.ToInt32(hdr, 4); vcount = BitConverter.ToInt32(hdr, 8);
            vframe = new byte[vw * vh * 3];
        }
        ledCols = Mathf.RoundToInt((vrect.y - vrect.x) / ledPitch);
        float y0 = Mathf.Max(0, vrect.z), y1 = Mathf.Min(H, vrect.w);
        ledRows = Mathf.RoundToInt((y1 - y0) / ledPitch);
        vrowOffset = Mathf.RoundToInt((y0 - vrect.z) / ledPitch);
        var lc = new List<Vector3>(); var ln = new List<Vector3>(); var lu = new List<Vector3>(); var lv = new List<Vector3>();
        for (int r = 0; r < ledRows; r++)
            for (int c = 0; c < ledCols; c++)
            {
                float ax = vrect.x + (c + 0.5f) * ledPitch, y = y0 + (r + 0.5f) * ledPitch;
                Wall(R - 0.03f, ax, y, ledPitch * 0.45f, ledPitch * 0.45f, lc, ln, lu, lv);
            }
        led = MakeTiles("led", lc, ln, lu, lv, Add(vgain), 0f, new Color32(0, 0, 0, 255));
        // the wall is drawn bright (gain); the front reaches the whole wall at nearly the same moment, so a flash on every
        // LED tile turns the wall white for a few frames: stage "ledFlash" scales it (0 = the tiles only shrink)
        float lf = (float?)st["ledFlash"] ?? 1f;
        led.flashCol = new Color32((byte)(40 * lf), (byte)(85 * lf), (byte)(100 * lf), 255);

        // backing panels: the whole cylinder plus a cap
        var pc = new List<Vector3>(); var pn = new List<Vector3>(); var pu = new List<Vector3>(); var pv = new List<Vector3>();
        int pcols = Mathf.RoundToInt(2 * Mathf.PI * R / panelPitch);
        float pAx = 2 * Mathf.PI * R / pcols;
        int prows = Mathf.CeilToInt(H / panelPitch);
        for (int r = 0; r < prows; r++)
            for (int c = 0; c < pcols; c++)
                Wall(R, -Mathf.PI * R + (c + 0.5f) * pAx, (r + 0.5f) * panelPitch, pAx * 0.505f, panelPitch * 0.505f, pc, pn, pu, pv);   // overlapping: no city light through the seams
        float cap = panelPitch * 2;
        for (float x = -R; x <= R; x += cap)
            for (float z = -R; z <= R; z += cap)
                if (new Vector2(x, z).magnitude < R + cap)
                { pc.Add(new Vector3(x, H, z)); pn.Add(Vector3.down); pu.Add(new Vector3(cap * 0.505f, 0, 0)); pv.Add(new Vector3(0, 0, -cap * 0.505f)); }
        panels = MakeTiles("panels", pc, pn, pu, pv, Dark(new Color(0.03f, 0.035f, 0.05f, 1f)), panelDelay, new Color32(255, 255, 255, 255));

        // floor: dark tiles, cyan light in the seams (an additive glow under them)
        var fc = new List<Vector3>(); var fn = new List<Vector3>(); var fu = new List<Vector3>(); var fv = new List<Vector3>();
        int nf = Mathf.CeilToInt(R / floorPitch);
        for (int ix = -nf; ix <= nf; ix++)
            for (int iz = -nf; iz <= nf; iz++)
            {
                var c = new Vector3(ix * floorPitch, 0.015f, iz * floorPitch);
                if (new Vector2(c.x, c.z).magnitude > R - floorPitch * 0.5f) continue;
                fc.Add(c); fn.Add(Vector3.up); fu.Add(new Vector3(floorPitch * 0.45f, 0, 0)); fv.Add(new Vector3(0, 0, floorPitch * 0.45f));
            }
        floor = MakeTiles("floor", fc, fn, fu, fv, Dark(new Color(0.035f, 0.04f, 0.055f, 1f)), 0f, new Color32(255, 255, 255, 255));

        // flash overlay: one additive quad per panel and floor tile, lit only at the dissolve front
        var xc = new List<Vector3>(); var xn = new List<Vector3>(); var xu = new List<Vector3>(); var xv = new List<Vector3>();
        for (int i = 0; i < pc.Count; i++) { xc.Add(pc[i] + pn[i] * 0.012f); xn.Add(pn[i]); xu.Add(pu[i]); xv.Add(pv[i]); }
        for (int i = 0; i < fc.Count; i++) { xc.Add(fc[i] + Vector3.up * 0.006f); xn.Add(fn[i]); xu.Add(fu[i]); xv.Add(fv[i]); }
        var outline = new Texture2D(64, 64, TextureFormat.RGBA32, true) { name = "CloudpunkVR_TileOutline", wrapMode = TextureWrapMode.Clamp };
        var op = new Color32[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int e = Math.Min(Math.Min(x, 63 - x), Math.Min(y, 63 - y));
                byte v = (byte)(e < 2 ? 255 : e < 4 ? 120 : e < 7 ? 35 : 10);
                op[y * 64 + x] = new Color32(v, v, v, 255);
            }
        outline.SetPixels32(op); outline.Apply(true);
        var flashMat = Add(flashGain);
        flashMat.mainTexture = outline;
        flash = MakeTiles("flash", xc, xn, xu, xv, flashMat, 0f, new Color32(0, 0, 0, 255));
        flash.flashOnly = true;
        flash.flashCol = new Color32(90, 230, 255, 255);
        // the flash of a panel follows the panel's own delay
        flash.delay = 0;
        flashDelays = new float[xc.Count];
        for (int i = 0; i < pc.Count; i++) flashDelays[i] = panelDelay;

        // seam glow under the floor tiles: cyan, fading out from the centre (set.dotColor, set.fade 1.5 → R)
        var glowTex = new Texture2D(128, 128, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var g = new Color32[128 * 128];
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                float rr = new Vector2(x - 63.5f, y - 63.5f).magnitude / 63.5f * R;
                float k = 1f - Mathf.SmoothStep(1.5f, R, rr);
                g[y * 128 + x] = new Color32((byte)(56 * k), (byte)(232 * k), (byte)(255 * k), 255);
            }
        glowTex.SetPixels32(g); glowTex.Apply();
        var glowMat = new Material(additive) { mainTexture = glowTex };
        glowMat.SetColor("_TintColor", new Color(seamGain, seamGain, seamGain, 0.5f));
        var glow = GameObject.CreatePrimitive(PrimitiveType.Quad);
        UnityEngine.Object.Destroy(glow.GetComponent<Collider>());
        glow.name = "seam glow";
        glow.transform.SetParent(rootT, false);
        glow.transform.localPosition = new Vector3(0, 0.006f, 0);
        glow.transform.localRotation = Quaternion.Euler(90, 0, 0);
        glow.transform.localScale = Vector3.one * R * 2;
        glow.GetComponent<MeshRenderer>().sharedMaterial = glowMat;
        seamGlow = glowMat;

        // lights: keynote-style key and rims on the performer, and the wall's own glow
        var lj = st["lights"] as JsonObject;
        float key = (float?)lj?["key"] ?? 1f, rim = (float?)lj?["rim"] ?? 1f, scr = (float?)lj?["screen"] ?? 1f;
        lightsAfter = (float?)lj?["after"] ?? 0.35f;
        var chest = new Vector3(0.13f, 1.2f, 1.3f);
        AddSpot("key", chest + new Vector3(0.55f, 0.75f, 0.7f).normalized * 4.5f, chest, new Color(1f, 0.95f, 0.9f), 2.2f * key, 38f, true);
        AddSpot("rim A", chest + new Vector3(-0.7f, 0.6f, -0.8f).normalized * 4f, chest, new Color(0.45f, 0.85f, 1f), 2.5f * rim, 40f, false);
        AddSpot("rim B", chest + new Vector3(0.8f, 0.5f, -0.7f).normalized * 4f, chest, new Color(0.75f, 0.6f, 1f), 1.6f * rim, 40f, false);
        var sgo = new GameObject("screen light");
        sgo.transform.SetParent(rootT, false);
        sgo.transform.localPosition = new Vector3(0, 2.4f, -4.5f);
        screenLight = sgo.AddComponent<Light>();
        screenLight.type = LightType.Point; screenLight.range = 11f; screenLight.intensity = 0; screenLight.shadows = LightShadows.None;
        screenScale = scr;
    }

    static float[] flashDelays;
    static Material seamGlow;
    static float screenScale = 1f;

    static void AddSpot(string name, Vector3 at, Vector3 target, Color c, float intensity, float angle, bool shadows)
    {
        var go = new GameObject(name);
        go.transform.SetParent(rootT, false);
        go.transform.localPosition = at;
        go.transform.localRotation = Quaternion.LookRotation(target - at);
        var l = go.AddComponent<Light>();
        l.type = LightType.Spot; l.spotAngle = angle; l.range = 12f; l.color = c; l.intensity = intensity;
        l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        l.renderMode = LightRenderMode.ForcePixel;
        lights.Add(l); lightBase.Add(intensity);
    }

    // A quad on the inside of the cylinder at arc ax (0 = behind the performer, + = the viewer's right), height y.
    static void Wall(float r, float ax, float y, float hu, float hv, List<Vector3> c, List<Vector3> n, List<Vector3> u, List<Vector3> v)
    {
        float th = ax / wallR;   // arc measured on the wall itself (VRStage set.radius), whatever r this layer sits at
        var p = new Vector3(-r * Mathf.Sin(th), y, -r * Mathf.Cos(th));
        var inward = new Vector3(-p.x, 0, -p.z).normalized;
        var t = new Vector3(-Mathf.Cos(th), 0, Mathf.Sin(th));
        c.Add(p); n.Add(inward); u.Add(t * hu); v.Add(Vector3.up * hv);
    }

    static Tiles MakeTiles(string name, List<Vector3> c, List<Vector3> n, List<Vector3> u, List<Vector3> v, Material mat, float delay, Color32 col)
    {
        int count = c.Count;
        var T = new Tiles { centre = c.ToArray(), normal = n.ToArray(), du = u.ToArray(), dv = v.ToArray(), delay = delay, dist = new float[count] };
        T.verts = new Vector3[count * 4];
        T.cols = new Color32[count * 4];
        T.baseCol = new Color32[count];
        var nrm = new Vector3[count * 4];
        var uv = new Vector2[count * 4];
        var tri = new int[count * 6];
        for (int i = 0; i < count; i++)
        {
            T.dist[i] = Vector3.Distance(T.centre[i], dissolveCentre);
            T.baseCol[i] = col;
            for (int k = 0; k < 4; k++) { nrm[i * 4 + k] = T.normal[i]; T.cols[i * 4 + k] = col; }
            uv[i * 4] = new Vector2(0, 0); uv[i * 4 + 1] = new Vector2(1, 0); uv[i * 4 + 2] = new Vector2(0, 1); uv[i * 4 + 3] = new Vector2(1, 1);
            // front face toward the normal: (u × v) · n > 0 keeps Unity's clockwise winding
            bool flip = Vector3.Dot(Vector3.Cross(T.du[i], T.dv[i]), T.normal[i]) > 0;
            int b = i * 4, t = i * 6;
            if (!flip) { tri[t] = b; tri[t + 1] = b + 2; tri[t + 2] = b + 1; tri[t + 3] = b + 2; tri[t + 4] = b + 3; tri[t + 5] = b + 1; }
            else { tri[t] = b; tri[t + 1] = b + 1; tri[t + 2] = b + 2; tri[t + 3] = b + 2; tri[t + 4] = b + 1; tri[t + 5] = b + 3; }
        }
        Place(T, -1f);
        T.mesh = new Mesh { name = "CloudpunkVR_Film_" + name };
        T.mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        T.mesh.MarkDynamic();
        T.mesh.vertices = T.verts;
        T.mesh.normals = nrm;
        T.mesh.uv = uv;
        T.mesh.colors32 = T.cols;
        T.mesh.triangles = tri;
        T.mesh.bounds = new Bounds(new Vector3(0, 7, 0), new Vector3(40, 30, 40));
        T.go = new GameObject(name);
        T.go.transform.SetParent(rootT, false);
        T.go.AddComponent<MeshFilter>().sharedMesh = T.mesh;
        var mr = T.go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return T;
    }

    // Positions (and colours) for front radius rho: scale 1 before the front, a flash at it, gone after it.
    // Plain float math on the struct fields: in this IL2CPP mod every Mathf call, Vector3 operator and struct constructor is
    // a call into the game's runtime (~35 ns), and the LED wall's ~124k tiles made ~3 M of them per upload (~120 ms).
    static bool Place(Tiles T, float rho, float[] delays = null)
    {
        bool alive = false;
        bool colours = T.flashOnly || T == led;
        var fc = T.flashCol;
        float fr = fc.r, fg = fc.g, fb = fc.b;
        float invW = 1f / width, fOff = width * 0.15f, fInv = 1f / (width * 0.45f);
        var verts = T.verts; var cols = T.cols;
        for (int i = 0; i < T.centre.Length; i++)
        {
            float d = T.dist[i];
            float r = rho < 0 ? -1e9f : rho - (delays != null ? delays[i] : T.delay) * speed;
            float p = (r - d) * invW;
            p = p < 0f ? 0f : p > 1f ? 1f : p;
            float s = 1f - p * p * (3 - 2 * p);
            if (s > 0.001f) alive = true;
            ref var c0 = ref T.centre[i]; ref var nn = ref T.normal[i]; ref var du = ref T.du[i]; ref var dv = ref T.dv[i];
            float k = p * 0.25f;
            float cx = c0.x - nn.x * k, cy = c0.y - nn.y * k, cz = c0.z - nn.z * k;
            float ux = du.x * s, uy = du.y * s, uz = du.z * s, vx = dv.x * s, vy = dv.y * s, vz = dv.z * s;
            int b = i * 4;
            ref var v0 = ref verts[b]; v0.x = cx - ux - vx; v0.y = cy - uy - vy; v0.z = cz - uz - vz;
            ref var v1 = ref verts[b + 1]; v1.x = cx + ux - vx; v1.y = cy + uy - vy; v1.z = cz + uz - vz;
            ref var v2 = ref verts[b + 2]; v2.x = cx - ux + vx; v2.y = cy - uy + vy; v2.z = cz - uz + vz;
            ref var v3 = ref verts[b + 3]; v3.x = cx + ux + vx; v3.y = cy + uy + vy; v3.z = cz + uz + vz;
            if (colours)
            {
                byte R = 0, G = 0, B = 0;
                if (s > 0.001f)
                {
                    float e = (r - d + fOff) * fInv;
                    float f = MathF.Exp(-e * e);
                    ref var bc = ref T.baseCol[i];
                    R = (byte)MathF.Min(255f, bc.r + fr * f); G = (byte)MathF.Min(255f, bc.g + fg * f); B = (byte)MathF.Min(255f, bc.b + fb * f);
                }
                for (int j = 0; j < 4; j++) { ref var c = ref cols[b + j]; c.r = R; c.g = G; c.b = B; c.a = 255; }
            }
        }
        return alive;
    }

    // ---------------------------------------------------------------- actor

    static void BuildActor(JsonObject ac)
    {
        string dir = (string)ac["dir"];
        actorStart = (float?)ac["start"] ?? 0f;
        actorFreeze = (float?)ac["freezeAt"] ?? float.MaxValue;
        float tint = (float?)ac["tint"] ?? 1f;
        var skip = new HashSet<string>();
        if (ac["skip"] is JsonArray sk) foreach (var n in sk) skip.Add((string)n);
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "mesh.json"))).AsObject();
        actorFps = (float?)j["fps"] ?? 30f;
        frameCount = (int)j["frames"];
        var idxBytes = File.ReadAllBytes(Path.Combine(dir, "indices.bin"));
        var uvBytes = File.ReadAllBytes(Path.Combine(dir, "uv.bin"));
        var allIdx = MemoryMarshal.Cast<byte, int>(idxBytes);
        var allUv = MemoryMarshal.Cast<byte, Vector2>(uvBytes);
        var mats = new List<Material>();
        var opaque = Find("MyShaders/SimpleStandard", "Standard");
        var emissive = Shader.Find("MyShaders/SimpleMetallicEmissive");
        var cutout = Shader.Find("Unlit/Transparent Cutout");
        var blend = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
        var texCache = new Dictionary<string, Texture2D>();
        var pxCache = new Dictionary<string, (Color32[] px, int w, int h)>();
        Texture2D Tex(string file)
        {
            if (file == null) return null;
            if (texCache.TryGetValue(file, out var t)) return t;
            var d = CloudpunkVR.Coo.Png.Decode(File.ReadAllBytes(Path.Combine(dir, file)));
            t = new Texture2D(d.w, d.h, TextureFormat.RGBA32, true, false) { name = file, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var px = new Color32[d.w * d.h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(d.rgba[i * 4], d.rgba[i * 4 + 1], d.rgba[i * 4 + 2], d.rgba[i * 4 + 3]);
            t.SetPixels32(px);
            t.Apply(true);
            texCache[file] = t;
            pxCache[file] = (px, d.w, d.h);
            return t;
        }
        var matteParts = new HashSet<string>();
        if (ac["matte"] is JsonArray mp) foreach (var n in mp) matteParts.Add((string)n);
        var lambert = Shader.Find("Standard (Specular setup)");
        if (matteParts.Count > 0 && lambert == null) Plugin.L.LogWarning("film stage: Standard (Specular setup) not in this build; matte parts keep their material");
        var matInfo = new List<(string tex, Color col)>();
        foreach (var mn in j["materials"].AsArray())
        {
            var m = mn.AsObject();
            string mode = (string)m["mode"] ?? "opaque";
            string mname = ((string)m["name"] ?? "").ToLowerInvariant(), shname = ((string)m["shader"] ?? "").ToLowerInvariant();
            int queue = (int?)m["queue"] ?? 2000;
            var col = m["color"] is JsonArray ca ? new Color((float)ca[0], (float)ca[1], (float)ca[2], (float)ca[3]) : Color.white;
            // effect-only materials (depth tricks, tears that only show with a blend shape) are not drawn
            if (queue >= 4000 || mname.Contains("tear") || shname.Contains("shadowdepth") || skip.Contains((string)m["name"])) { mats.Add(null); matInfo.Add((null, col)); continue; }
            col = new Color(col.r * tint, col.g * tint, col.b * tint, col.a);
            matInfo.Add(((string)m["texture"], col));
            if (queue >= 2900 || shname.Contains("transparent") || shname.Contains("crystal")) mode = "transparent";

            var tex = Tex((string)m["texture"]);
            Material mat;
            if (mode == "transparent" && blend != null)
            {
                mat = new Material(blend);
                // unlit in the game: keep decals dim; a lens without a texture is a faint sheen
                var tc = m["texture"] == null ? new Color(0.5f, 0.6f, 0.7f, 0.06f) : new Color(col.r * 0.22f, col.g * 0.22f, col.b * 0.22f, col.a * 0.5f);
                mat.SetColor("_TintColor", tc);
            }
            else if (mode == "cutout" && cutout != null) { mat = new Material(cutout); mat.SetFloat("_Cutoff", (float?)m["cutoff"] ?? 0.5f); }
            else if (m["emission"] is JsonArray ea && emissive != null)
            {
                mat = new Material(emissive);
                mat.SetColor("_TintColor", col);
                var em = Tex((string)m["emissionTexture"]) ?? tex;
                if (em != null) mat.SetTexture("_EmissionMap", em);
                mat.SetColor("_EmissionTint", new Color((float)ea[0], (float)ea[1], (float)ea[2], 1));
                mat.SetFloat("_EmissivePower", 1f);
            }
            else { mat = new Material(opaque); if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", col); if (mat.HasProperty("_Color")) mat.SetColor("_Color", col); }
            if (tex != null) mat.mainTexture = tex;
            mat.name = "CloudpunkVR_Actor_" + (string)m["name"];
            mats.Add(mat);
        }
        var parts = j["parts"].AsArray();
        partVerts = new int[parts.Count];
        partMeshes = new Mesh[parts.Count];
        partPos = new Vector3[parts.Count][];
        partTris = new int[parts.Count][][];
        partUv = new Vector2[parts.Count][];
        subTex = new (Color32[] px, int w, int h, Color tint)?[parts.Count][];
        var matteMats = new Dictionary<int, Material>();
        int uvAt = 0;
        var actor = new GameObject("actor");
        actor.transform.SetParent(rootT, false);
        for (int p = 0; p < parts.Count; p++)
        {
            var pj = parts[p].AsObject();
            int nv = (int)pj["vertices"];
            partVerts[p] = nv;
            frameBytes += nv * 16L;
            var mesh = new Mesh { name = "CloudpunkVR_Actor_" + (string)pj["name"] };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.MarkDynamic();
            mesh.vertices = new Vector3[nv];
            partUv[p] = allUv.Slice(uvAt, nv).ToArray();
            mesh.uv = partUv[p];
            uvAt += nv;
            var subs = pj["submeshes"].AsArray();
            mesh.subMeshCount = subs.Count;
            var rmats = new Material[subs.Count];
            partTris[p] = new int[subs.Count][];
            subTex[p] = new (Color32[] px, int w, int h, Color tint)?[subs.Count];
            bool matte = matteParts.Contains((string)pj["name"]) && lambert != null;
            for (int s = 0; s < subs.Count; s++)
            {
                var sj = subs[s].AsObject();
                int start = (int)(long)sj["start"], count = (int)sj["count"];
                int mi = (int)sj["material"];
                var mat = mats[mi];
                partTris[p][s] = mat != null ? allIdx.Slice(start, count).ToArray() : new int[0];
                mesh.SetTriangles(partTris[p][s], s);
                if (mat != null && matte)
                {
                    if (!matteMats.TryGetValue(mi, out var mm))
                    {
                        mm = new Material(lambert) { name = mat.name + " (matte)" };
                        mm.SetColor("_Color", matInfo[mi].col);
                        mm.SetColor("_SpecColor", Color.black);
                        mm.SetFloat("_Glossiness", 0f);
                        mm.SetFloat("_GlossMapScale", 0f);
                        mm.SetColor("_EmissionColor", Color.black);
                        if (mat.mainTexture != null) mm.mainTexture = mat.mainTexture;
                        matteMats[mi] = mm;
                    }
                    mat = mm;
                }
                rmats[s] = mat ?? mats.Find(x => x != null);
                var tf = matInfo[mi].tex;
                if (mat != null && tf != null && pxCache.TryGetValue(tf, out var pc)) subTex[p][s] = (pc.px, pc.w, pc.h, matInfo[mi].col);
                else if (mat != null) subTex[p][s] = (null, 0, 0, matInfo[mi].col);
            }
            var go = new GameObject((string)pj["name"]);
            go.transform.SetParent(actor.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = rmats;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            partMeshes[p] = mesh;
        }
        frames = File.OpenRead(Path.Combine(dir, "frames.bin"));
        fbuf = new byte[frameBytes];
        LoadActorFrame(0);
        exitJ = ac["exit"] as JsonObject;
        if (exitJ != null)
        {
            exitT = (float?)exitJ["t"] ?? float.MaxValue;
            exitDur = Mathf.Max(0.05f, (float?)exitJ["dur"] ?? exitDur);
            exitJitter = (float?)exitJ["jitter"] ?? exitJitter;
            exitSize = (float?)exitJ["size"] ?? exitSize;
            exitGain = (float?)exitJ["gain"] ?? exitGain;
            if (exitJ["drift"] is JsonArray dj) exitDrift = V3(dj);
            if (exitJ["life"] is JsonArray lj) { lifeMin = (float)lj[0]; lifeMax = (float)lj[1]; }
            if (exitJ["flash"] is JsonArray fj) exitFlash = new Color((float)fj[0], (float)fj[1], (float)fj[2]);
        }
        Plugin.L.LogInfo($"film stage: actor {parts.Count} parts, {mats.Count} materials ({matteMats.Count} made matte), {frameCount} frames ({frameBytes / 1024} KiB each)" + (exitJ != null ? $", exit at {exitT:F2} s" : ""));
    }

    static void LoadActorFrame(int f)
    {
        if (frames == null || f == frameLoaded) return;
        frameLoaded = f;
        frames.Seek(f * frameBytes, SeekOrigin.Begin);
        int got = 0;
        while (got < fbuf.Length) { int n = frames.Read(fbuf, got, fbuf.Length - got); if (n <= 0) break; got += n; }
        long at = 0;
        for (int p = 0; p < partMeshes.Length; p++)
        {
            int nv = partVerts[p];
            var pos = partPos[p] != null && partPos[p].Length == nv ? partPos[p] : (partPos[p] = new Vector3[nv]);
            MemoryMarshal.Cast<byte, Vector3>(new ReadOnlySpan<byte>(fbuf, (int)at, nv * 12)).CopyTo(pos);
            at += nv * 12;
            partNrm ??= new Vector3[partMeshes.Length][];
            var nrm = partNrm[p] != null && partNrm[p].Length == nv ? partNrm[p] : (partNrm[p] = new Vector3[nv]);
            for (int v = 0; v < nv; v++, at += 4)
                nrm[v] = new Vector3((sbyte)fbuf[at] / 127f, (sbyte)fbuf[at + 1] / 127f, (sbyte)fbuf[at + 2] / 127f);
            var m = partMeshes[p];
            m.vertices = FilmNative.Of(pos);
            m.normals = FilmNative.Of(nrm);
            m.RecalculateBounds();
        }
    }

    // ---------------------------------------------------------------- per frame

    public static void Update(double t, Camera cam)
    {
        if (root == null) return;
        float ft = (float)t;
        long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (partMeshes != null)
            LoadActorFrame(Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(ft, actorFreeze) - actorStart) * actorFps + 0.001f), 0, frameCount - 1));
        long c1 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (partMeshes != null && ft >= exitT) ActorExit(ft, cam);
        long c2 = System.Diagnostics.Stopwatch.GetTimestamp();
        tActor += c1 - c0; tExit += c2 - c1;
        float rho = ft >= dissolveT ? (ft - dissolveT) * speed : -1f;
        bool newVideo = false;
        if (led != null && !led.done)
        {
            // video frame → tile colours
            if (video != null)
            {
                int f = Mathf.Clamp(Mathf.FloorToInt((ft - vstart) * vfps + 0.001f), 0, vcount - 1);
                if (f != vLoaded)
                {
                    vLoaded = f;
                    newVideo = true;
                    video.Seek(12 + (long)f * vframe.Length, SeekOrigin.Begin);
                    int got = 0;
                    while (got < vframe.Length) { int n = video.Read(vframe, got, vframe.Length - got); if (n <= 0) break; got += n; }
                    long sr = 0, sg = 0, sb = 0;
                    for (int r = 0; r < ledRows; r++)
                    {
                        int vr = Mathf.Clamp(r + vrowOffset, 0, vh - 1);
                        for (int c = 0; c < ledCols; c++)
                        {
                            int vc = Mathf.Clamp(c * vw / Mathf.Max(1, ledCols), 0, vw - 1);
                            int o = (vr * vw + vc) * 3;
                            led.baseCol[r * ledCols + c] = new Color32(vframe[o], vframe[o + 1], vframe[o + 2], 255);
                            sr += vframe[o]; sg += vframe[o + 1]; sb += vframe[o + 2];
                        }
                    }
                    float n2 = Mathf.Max(1, ledRows * ledCols) * 255f;
                    var avg = new Color(sr / n2, sg / n2, sb / n2);
                    float lum = avg.maxColorComponent;
                    if (screenLight != null)
                    {
                        screenLight.color = lum > 1e-3f ? avg / lum : Color.black;
                        screenLight.intensity = Mathf.Min(4f, lum * 6f * screenScale);
                    }
                }
            }
            // standing, the wall only changes with the video (one upload per video frame, not per sub-frame)
            if (rho >= 0 || newVideo || !ledPlaced) { Upload(led, rho, null, true, rho >= 0 || !ledPlaced); ledPlaced = true; }
        }
        if (panels != null && (rho >= 0 || !placedOnce))
        {
            Upload(panels, rho, null, false);
            Upload(floor, rho, null, false);
            Upload(flash, rho, flashDelays);
            placedOnce = true;
        }
        long c3 = System.Diagnostics.Stopwatch.GetTimestamp();
        tTiles += c3 - c2;
        // stage lights and the seam glow fade as the set dissolves
        float k = rho < 0 ? 1f : Mathf.Lerp(1f, lightsAfter, Mathf.Clamp01(rho / 12f));
        for (int i = 0; i < lights.Count; i++) if (lights[i] != null) lights[i].intensity = lightBase[i] * k;
        if (screenLight != null && rho >= 0) screenLight.intensity *= Mathf.Clamp01(1f - rho / 9f);
        if (seamGlow != null) seamGlow.SetColor("_TintColor", new Color(seamGain, seamGain, seamGain, 0.5f) * (rho < 0 ? 1f : Mathf.Clamp01(1f - rho / 6f)));
        HideIntruders(rho);
        tPeople += System.Diagnostics.Stopwatch.GetTimestamp() - c3;
    }

    // ---------------------------------------------------------------- actor exit

    static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }
    static float Rand(uint a, uint b) => (Hash(a * 0x9E3779B1u + b * 0x85EBCA77u + 0x27d4eb2fu) & 0xFFFFFF) / 16777216f;

    static void PrepareExit()
    {
        var sweep = exitJ["sweep"] is JsonArray sj ? V3(sj).normalized : Vector3.up;
        if (sweep.sqrMagnitude < 1e-6f) sweep = Vector3.up;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int p = 0; p < partPos.Length; p++)
            if (partPos[p] != null) foreach (var v in partPos[p]) { float d = Vector3.Dot(v, sweep); lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d); }
        float span = Mathf.Max(0.05f, hi - lo);
        int total = 0;
        triTau = new float[partPos.Length][][];
        for (int p = 0; p < partPos.Length; p++)
        {
            triTau[p] = new float[partTris[p].Length][];
            for (int s = 0; s < partTris[p].Length; s++)
            {
                var tri = partTris[p][s];
                var tau = triTau[p][s] = new float[tri.Length / 3];
                var pos = partPos[p];
                for (int i = 0; i < tau.Length; i++)
                {
                    var c = (pos[tri[i * 3]] + pos[tri[i * 3 + 1]] + pos[tri[i * 3 + 2]]) / 3f;
                    // a ragged front: noise in 4 cm cells, plus a little per triangle
                    uint cx = (uint)Mathf.FloorToInt(c.x / 0.04f + 10000), cy = (uint)Mathf.FloorToInt(c.y / 0.04f + 10000), cz = (uint)Mathf.FloorToInt(c.z / 0.04f + 10000);
                    float n = Rand(cx * 73856093u ^ cy * 19349663u ^ cz * 83492791u, 7) * 0.8f + Rand((uint)(p * 131 + s), (uint)i) * 0.2f;
                    tau[i] = (Vector3.Dot(c, sweep) - lo) / span + n * exitJitter;
                }
                total += tau.Length;
            }
        }
        int want = (int?)exitJ["particles"] ?? 24000;
        int stride = Mathf.Max(1, Mathf.CeilToInt(total / (float)Mathf.Max(1, want)));
        var lp = new List<(int p, int s, int i)>();
        int k = 0;
        for (int p = 0; p < triTau.Length; p++)
            for (int s = 0; s < triTau[p].Length; s++)
                for (int i = 0; i < triTau[p][s].Length; i++, k++)
                    if (k % stride == 0 && subTex[p][s].HasValue) lp.Add((p, s, i));
        pCount = lp.Count;
        pPart = new int[pCount]; pSub = new int[pCount]; pTri = new int[pCount];
        pTau = new float[pCount]; pBorn = new float[pCount]; pLife = new float[pCount]; pSize = new float[pCount]; pSeed = new float[pCount];
        pPos0 = new Vector3[pCount]; pVel = new Vector3[pCount]; pCol = new Color[pCount]; pSeen = new bool[pCount];
        for (int q = 0; q < pCount; q++)
        {
            var (p, s, i) = lp[q];
            pPart[q] = p; pSub[q] = s; pTri[q] = i;
            pTau[q] = triTau[p][s][i];
            pBorn[q] = exitT + pTau[q] / (1f + exitJitter) * exitDur;
            pLife[q] = Mathf.Lerp(lifeMin, lifeMax, Rand((uint)q, 11));
            pSize[q] = exitSize * Mathf.Lerp(0.55f, 1.5f, Rand((uint)q, 12));
            pSeed[q] = Rand((uint)q, 13);
        }
        // one camera-facing quad per particle
        pVerts = new Vector3[pCount * 4];
        pCols = new Color32[pCount * 4];
        var uv = new Vector2[pCount * 4];
        var idx = new int[pCount * 6];
        for (int q = 0; q < pCount; q++)
        {
            uv[q * 4] = new Vector2(0, 0); uv[q * 4 + 1] = new Vector2(1, 0); uv[q * 4 + 2] = new Vector2(0, 1); uv[q * 4 + 3] = new Vector2(1, 1);
            int b = q * 4, t = q * 6;
            idx[t] = b; idx[t + 1] = b + 2; idx[t + 2] = b + 1; idx[t + 3] = b + 2; idx[t + 4] = b + 3; idx[t + 5] = b + 1;
        }
        pMesh = new Mesh { name = "CloudpunkVR_Film_ActorParticles" };
        pMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        pMesh.MarkDynamic();
        pMesh.vertices = pVerts;
        pMesh.uv = uv;
        pMesh.colors32 = pCols;
        pMesh.triangles = idx;
        pMesh.bounds = new Bounds(new Vector3(0, 4, 0), new Vector3(80, 40, 80));
        var dot = new Texture2D(32, 32, TextureFormat.RGBA32, true) { name = "CloudpunkVR_Spark", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[32 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                float r = new Vector2(x - 15.5f, y - 15.5f).magnitude / 15.5f;
                float v = Mathf.Clamp01(1f - r);
                v = v * v * (0.35f + 0.65f * Mathf.Exp(-r * r * 9f));
                byte b8 = (byte)(255 * v);
                px[y * 32 + x] = new Color32(b8, b8, b8, 255);
            }
        dot.SetPixels32(px); dot.Apply(true);
        var mat = new Material(Find("Legacy Shaders/Particles/Additive", "Mobile/Particles/Additive")) { mainTexture = dot };
        float tint = (float?)exitJ["tint"] ?? 0.5f;    // the shader doubles it: above 0.5 the sparks go past white and bloom
        mat.SetColor("_TintColor", new Color(tint, tint, tint, 0.5f));
        var go = new GameObject("actor particles");
        go.transform.SetParent(rootT, false);
        go.AddComponent<MeshFilter>().sharedMesh = pMesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Plugin.L.LogInfo($"film stage: actor exit prepared, {total} triangles, {pCount} particles");
    }

    static Color TexColour(int p, int s, int i)
    {
        var st = subTex[p][s].Value;
        var tri = partTris[p][s];
        var uv = (partUv[p][tri[i * 3]] + partUv[p][tri[i * 3 + 1]] + partUv[p][tri[i * 3 + 2]]) / 3f;
        Color c = Color.white;
        if (st.px != null)
        {
            int x = ((int)(uv.x * st.w) % st.w + st.w) % st.w, y = ((int)(uv.y * st.h) % st.h + st.h) % st.h;
            c = st.px[y * st.w + x];
        }
        return new Color(c.r * st.tint.r, c.g * st.tint.g, c.b * st.tint.b, 1);
    }

    static void ActorExit(float t, Camera cam)
    {
        if (triTau == null) PrepareExit();
        float prog = Mathf.Clamp((t - exitT) / exitDur, 0f, 1.01f) * (1f + exitJitter);
        if (prog != exitP)
        {
            exitP = prog;
            for (int p = 0; p < partMeshes.Length; p++)
                for (int s = 0; s < partTris[p].Length; s++)
                {
                    var tri = partTris[p][s]; var tau = triTau[p][s];
                    if (tri.Length == 0) continue;
                    var keep = new List<int>(tri.Length);
                    for (int i = 0; i < tau.Length; i++) if (tau[i] > prog) { keep.Add(tri[i * 3]); keep.Add(tri[i * 3 + 1]); keep.Add(tri[i * 3 + 2]); }
                    partMeshes[p].SetTriangles(FilmNative.Fresh<int>(CollectionsMarshal.AsSpan(keep)), s);
                }
        }
        var camT = cam != null ? cam.transform : null;
        var right = camT != null ? rootT.InverseTransformDirection(camT.right) : Vector3.right;
        var up = camT != null ? rootT.InverseTransformDirection(camT.up) : Vector3.up;
        // the per-particle loop in plain float math (see Place): ~29k particles × dozens of runtime calls was ~170 ms a frame
        float rx = right.x, ry = right.y, rz = right.z, ux0 = up.x, uy0 = up.y, uz0 = up.z;
        float dx = exitDrift.x, dy = exitDrift.y, dz = exitDrift.z;
        float flR = exitFlash.r, flG = exitFlash.g, flB = exitFlash.b;
        if (t > exitT + exitDur + lifeMax + 0.1f)
        {
            // every particle is gone: one last empty upload, then nothing to do
            if (!pCleared) { Array.Clear(pVerts, 0, pVerts.Length); Array.Clear(pCols, 0, pCols.Length); pMesh.vertices = FilmNative.Of(pVerts); pMesh.colors32 = FilmNative.Of(pCols); pCleared = true; }
            return;
        }
        for (int q = 0; q < pCount; q++)
        {
            int b = q * 4;
            float age = t - pBorn[q];
            if (age < 0f || age > pLife[q])
            {
                pVerts[b] = pVerts[b + 1] = pVerts[b + 2] = pVerts[b + 3] = default;
                pCols[b] = pCols[b + 1] = pCols[b + 2] = pCols[b + 3] = default;
                continue;
            }
            if (!pSeen[q])
            {
                // born now: from the triangle where it stands in this frame
                pSeen[q] = true;
                int p = pPart[q], s = pSub[q], i = pTri[q];
                var tri = partTris[p][s]; var pos = partPos[p];
                Vector3 a = pos[tri[i * 3]], bb = pos[tri[i * 3 + 1]], c = pos[tri[i * 3 + 2]];
                pPos0[q] = (a + bb + c) / 3f;
                var n = Vector3.Cross(bb - a, c - a);
                n = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
                var rnd = new Vector3(Rand((uint)q, 21) - 0.5f, Rand((uint)q, 22) - 0.5f, Rand((uint)q, 23) - 0.5f);
                pVel[q] = n * 0.35f + rnd * 0.6f + new Vector3(0, 0.15f, 0);
                pCol[q] = TexColour(p, s, i);
            }
            float sd = pSeed[q] * 6.2832f;
            float sw = 0.07f * age, hh = 0.5f * age * age;
            ref var p0 = ref pPos0[q]; ref var pv = ref pVel[q];
            float x = p0.x + pv.x * age + dx * hh + MathF.Sin(age * 3.1f + sd) * sw;
            float y = p0.y + pv.y * age + dy * hh + MathF.Sin(age * 2.3f + sd * 1.7f) * sw;
            float z = p0.z + pv.z * age + dz * hh + MathF.Cos(age * 2.7f + sd * 0.6f) * sw;
            float life = age / pLife[q];
            float sz = pSize[q] * (age < 0.06f ? 1.8f - age / 0.06f * 0.8f : 1f) * MathF.Pow(1f - life, 0.6f);
            float Rx = rx * sz, Ry = ry * sz, Rz = rz * sz, Ux = ux0 * sz, Uy = uy0 * sz, Uz = uz0 * sz;
            ref var q0 = ref pVerts[b]; q0.x = x - Rx - Ux; q0.y = y - Ry - Uy; q0.z = z - Rz - Uz;
            ref var q1 = ref pVerts[b + 1]; q1.x = x + Rx - Ux; q1.y = y + Ry - Uy; q1.z = z + Rz - Uz;
            ref var q2 = ref pVerts[b + 2]; q2.x = x - Rx + Ux; q2.y = y - Ry + Uy; q2.z = z - Rz + Uz;
            ref var q3 = ref pVerts[b + 3]; q3.x = x + Rx + Ux; q3.y = y + Ry + Uy; q3.z = z + Rz + Uz;
            float fl = MathF.Exp(-age / 0.12f);
            float fade = (1f - life) * (1f - life);
            ref var pc = ref pCol[q];
            float cr = ((pc.r * exitGain + 0.05f) + (flR - (pc.r * exitGain + 0.05f)) * fl) * fade;
            float cg = ((pc.g * exitGain + 0.18f) + (flG - (pc.g * exitGain + 0.18f)) * fl) * fade;
            float cb = ((pc.b * exitGain + 0.24f) + (flB - (pc.b * exitGain + 0.24f)) * fl) * fade;
            byte R8 = (byte)Math.Clamp(cr * 255f, 0f, 255f), G8 = (byte)Math.Clamp(cg * 255f, 0f, 255f), B8 = (byte)Math.Clamp(cb * 255f, 0f, 255f);
            for (int j = 0; j < 4; j++) { ref var c = ref pCols[b + j]; c.r = R8; c.g = G8; c.b = B8; c.a = 255; }
        }
        pMesh.vertices = FilmNative.Of(pVerts);
        pMesh.colors32 = FilmNative.Of(pCols);
    }

    static bool placedOnce, pCleared;
    static float seamGain = 0.12f, flashGain = 0.45f;

    // City renderers inside the set (people walking the street, props): hidden until the dissolve reaches them.
    static readonly List<(Renderer r, bool on)> intruders = new();
    static void CollectIntruders(float R, float H)
    {
        intruders.Clear();
        var inv = rootT.worldToLocalMatrix;
        foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            if (r == null || r.transform.IsChildOf(rootT)) continue;
            var c = (Vector3)(inv * new Vector4(r.bounds.center.x, r.bounds.center.y, r.bounds.center.z, 1));
            if (new Vector2(c.x, c.z).magnitude > Mathf.Max(R + 6f, clearRadius + 4f) || c.y < -3f || c.y > H + 3f) continue;
            intruders.Add((r, r.enabled));
        }
        Plugin.L.LogInfo($"film stage: {intruders.Count} city renderers near the set");
    }

    // People (game Characters) on the set are switched off until the dissolve has passed where they stand; ones that walk
    // in while the set stands too. Put back when the capture stops.
    static readonly List<GameObject> hiddenPeople = new();
    static void HidePeople(float rho)
    {
        var inv = rootT.worldToLocalMatrix;
        foreach (var ch in UnityEngine.Object.FindObjectsOfType<global::Character>())
        {
            if (ch == null || !ch.gameObject.activeSelf) continue;
            if (Global.Instance != null && Global.Instance.player != null && ch.gameObject == Global.Instance.player.gameObject) continue;
            var p = ch.transform.position;
            var c = (Vector3)(inv * new Vector4(p.x, p.y, p.z, 1));
            if (new Vector2(c.x, c.z).magnitude > Mathf.Max(wallR + 1.5f, clearRadius) || c.y < -3f || c.y > 16f) continue;
            if (!clearSet && rho >= 0 && rho - 1.5f > Vector3.Distance(c, dissolveCentre)) continue;
            ch.gameObject.SetActive(false);
            hiddenPeople.Add(ch.gameObject);
        }
    }

    static void HideIntruders(float rho)
    {
        if (peopleTick++ % 4 == 0) HidePeople(rho);   // a scan of every Character: once per output frame is plenty
        var inv = rootT.worldToLocalMatrix;
        foreach (var (r, on) in intruders)
        {
            if (r == null || (!on && !clearSet)) continue;   // clearing: also the ones the game had culled when the set was built
            var c = (Vector3)(inv * new Vector4(r.bounds.center.x, r.bounds.center.y, r.bounds.center.z, 1));
            bool inside = new Vector2(c.x, c.z).magnitude < clearRadius && c.y < 14.5f;
            bool reached = !clearSet && rho >= 0 && rho - 1.5f > Vector3.Distance(c, dissolveCentre);
            r.enabled = !inside || reached;
        }
    }
    static float wallR = 9f;
    static bool clearSet;   // stage "clearSet": props and people inside the set stay away after it dissolves
    static float clearRadius = 9.3f;   // stage "clearRadius": how far round the set that applies (default: the wall)

    static Vector3 V3(JsonArray a) => new((float)a[0], (float)a[1], (float)a[2]);
}
