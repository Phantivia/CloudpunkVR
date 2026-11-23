using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace CloudpunkVR;

// Rain with depth for the headset. The game draws its rain as post effects over the finished picture: RainAndSheets
// (procedural rain curtains from the view direction) and SplashesAndDrips (splashes on surfaces and drips running
// down the lens). In a headset both eyes get the same curtain at the same angles, so the rain hangs at infinity like
// part of the sky, and the drips sit on your eyes. In VR both are off and real streaks fall around the player instead:
// a mesh of thin streaks laid out in a 6 m cell that repeats 5 x 5 across and 3 times up, moved so the pattern is
// fixed to the world (snapped to the cell) while it falls. Walking or driving through it passes drops by; every drop
// has its own depth in each eye. One mesh, one draw per eye, no simulation. It shows while the game has its rain on
// (RainAndSheets enabled in the active profile) and in the car too, behind the windshield (the cockpit is drawn over
// it by the interior camera). A flat film capture can have it too (Film job "rain": { "streaks": true }): Film owns its
// own instance then, with its own cell count, and drives Density / Wet / Opacity over film time.
internal sealed class VRRain
{
    const float Cell = 6f;            // the streak pattern repeats every Cell metres horizontally
    readonly int Across = 5;          // cells per side: rain within about 15 m (a film may ask for more)
    const float Height = 7f;          // vertical period
    const int Layers = 3;             // periods stacked: about 10 m above and below the eyes
    const int PerCell = 34;           // streaks per cell and layer
    public static float FallSpeed = 9f, StreakLength = 0.55f, StreakWidth = 0.014f, Opacity = 0.4f;
    public static bool Enabled = true;   // dev A/B (rain3d 0|1)
    public static bool KeepDrips;        // dev A/B (drips 0|1): leave SplashesAndDrips on
    // Film (flat capture): the streaks are wanted without a headset; ScreenRain keeps the game's own screen-space rain
    // as well (the streaks reach ~15 m, the game's curtain gives the far rain); Density is the share of streaks shown
    // (each streak has a fixed random rank, so rain "starts" as sparse drops and fills in); Wet scales the extra Hx
    // scattering in rain (0 = the game's density, 1 = RainDensityScale).
    public static bool FilmOn, ScreenRain;
    // FilmDry: before the film's rain starts both effects are off whatever the weather (the profiles the weather blends
    // are clones the film's own switch may not reach); FilmLens: the lens drips stay (else SplashesAndDrips is off)
    public static bool FilmDry, FilmLens = true;
    public static float Density = 1f, Wet = 1f;
    // Film's explainer of the mesh itself: Anchor centres the pattern on a point (the performer) instead of the camera;
    // Glow (0-1) draws the streaks additive in GlowColor instead of rain grey; Lattice (0-1) draws the pattern's cells as
    // glowing lines (they fall with the streaks, so the block reads as one mesh moving down); FilmClock: the fall
    // phase from the film's clock (repeatable, and continuous across two captures given the same clock).
    public static Vector3? Anchor;
    public static float Glow, Lattice, LatticeWidth = 0.12f;
    public static Color GlowColor = new(0.30f, 0.85f, 1.0f, 1f), LatticeColor = new(0.25f, 0.8f, 1.0f, 1f);
    public static double FilmClock = double.NaN;
    // Shatter (0-1): the lattice breaks into short pieces that fly apart, shrink to glowing points and fall (the snap
    // that turns the explainer into real rain). From the first frame above 0 the lattice stops following the falling
    // pattern (it stays where it was), so the pieces do not jump when the pattern wraps.
    public static float Shatter;
    public static bool LatticeHold;       // film: the lattice stops falling from now on (stays where it is)
    public static float LatticeThick = 1f;   // film: the lattice's lines this many times their width (a flash)
    public static float ShatterSpread = 6f;

    public VRRain() { }
    public VRRain(int across) { Across = Mathf.Clamp(across, 1, 15); }

    static bool gameRainOn;
    static int gameRainFrame = -1;

    GameObject root, lattice;
    Mesh latticeMesh;
    Vector3[] pA, pB, pVel, pSide;             // per piece: ends and velocity; per vertex: its offset from its end
    bool[] pToCam;
    byte[] latBaseK;                           // per vertex: the line's brightness (outer frame brighter)
    Vector3[] latRest;                         // per vertex: its place before the shatter (the height fade's)
    bool extraLayers;                          // film: one more streak layer below and above (FadeStreaks)
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> streakCols;
    bool streakFaded;
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> latCols;
    bool aimed;
    float thickApplied = 1f;
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> latLive;
    float shatterApplied;
    bool latticeDetached;
    Material material, glowMat, latticeMat;
    MeshRenderer streakRenderer;
    Mesh mesh;
    float fall;
    // Streak segments in the mesh's space, the full vertex set, and which streaks are hidden right now: a streak that
    // passes within ClearRadius of the eye would fill the view as a pale bar, so it is collapsed while it is that close.
    public static float ClearRadius = 1.1f;
    Vector3[] heads, tails;
    float[] rank;
    Vector3[] fullVerts;
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> liveVerts;
    readonly System.Collections.Generic.HashSet<int> hidden = new(), nowHidden = new();

    // Neither effect overrides IsEnabledAndSupported, so the base method is patched and the two are picked out by
    // their native class (a pointer compare per call; the stack asks every effect of the profile each render).
    static System.IntPtr rainClass, dripsClass;

    static bool patched;

    public static void Patch(Harmony h)
    {
        if (patched) return;
        patched = true;
        rainClass = Il2CppInterop.Runtime.Il2CppClassPointerStore<RainAndSheets>.NativeClassPtr;
        dripsClass = Il2CppInterop.Runtime.Il2CppClassPointerStore<SplashesAndDrips>.NativeClassPtr;
        h.Patch(AccessTools.Method(typeof(PostProcessEffectSettings), nameof(PostProcessEffectSettings.IsEnabledAndSupported)),
            postfix: new HarmonyMethod(typeof(VRRain), nameof(NoScreenRain)));
    }

    // The profile says it rains: remember that, and draw ours instead of the screen-space curtain and lens drips.
    static bool loggedAsk;

    static void NoScreenRain(PostProcessEffectSettings __instance, ref bool __result)
    {
        using var __pt = PluginTiming.Measure("patch:IsEnabledAndSupported");
        if (!Enabled || __instance == null || !(UnityEngine.XR.XRSettings.enabled || FilmOn)) return;
        var cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(__instance.Pointer);
        if (FilmOn && (cls == rainClass || cls == dripsClass) && (FilmDry || (cls == dripsClass && !FilmLens))) { __result = false; return; }
        if (cls == rainClass)
        {
            if (!loggedAsk && DevCommands.Enabled) { loggedAsk = true; Plugin.L.LogInfo($"rain: the stack asks RainAndSheets '{__instance.name}' (enabled {__result})"); }
            if (!__result) return;
            if (!gameRainOn) Plugin.L.LogInfo("rain: the game's rain is on, drawn as 3D streaks");
            gameRainOn = true; gameRainFrame = Time.frameCount;
            if (!(FilmOn && ScreenRain)) __result = false;
        }
        else if (cls == dripsClass && !KeepDrips && !(FilmOn && ScreenRain)) __result = false;
    }

    public void BeforeRender(Camera main)
    {
        main = VRCameras.WorldCamera(main);
        // the profile is asked every rendered frame while the effect is in it; a few frames without asking means no rain
        // (no loading check: the title screen counts as loading, and while a level loads no world camera is on)
        bool raining = Enabled && main != null && gameRainOn && Time.frameCount - gameRainFrame < 5;
        WetAir(raining);
        bool on = raining || (Enabled && main != null && (Glow > 0f || Lattice > 0f));
        if (!on)
        {
            if (root != null && root.activeSelf) root.SetActive(false);
            return;
        }
        if (root == null && !Build()) return;
        if (!root.activeSelf) root.SetActive(true);

        fall = double.IsNaN(FilmClock) ? Mathf.Repeat(fall + FallSpeed * Time.deltaTime, Height)
                                        : (float)(FilmClock * FallSpeed % Height);
        var eye = Anchor ?? main.transform.position;
        // snap to the pattern's period so the drops stay put in the world while the player moves
        float half = Cell * Across * 0.5f, halfY = Height * Layers * 0.5f;
        var p = new Vector3(Mathf.Floor(eye.x / Cell) * Cell - half + Cell * 0.5f,
                            Mathf.Floor(eye.y / Height) * Height - halfY + Height * 0.5f - fall,
                            Mathf.Floor(eye.z / Cell) * Cell - half + Cell * 0.5f);
        root.transform.position = p;
        material.color = new Color(0.78f, 0.86f, 0.95f, Opacity);
        if (Glow > 0f)
        {
            var g = GlowColor * Glow;
            g.a = 1f;
            glowMat.SetColor("_TintColor", g);
            if (streakRenderer.sharedMaterial != glowMat) streakRenderer.sharedMaterial = glowMat;
        }
        else if (streakRenderer.sharedMaterial != material) streakRenderer.sharedMaterial = material;
        if (Lattice > 0f && lattice == null) BuildLattice();
        if (lattice != null)
        {
            if ((Shatter > 0f || LatticeHold) && !latticeDetached) { lattice.transform.SetParent(null, true); latticeDetached = true; }
            if (Shatter != shatterApplied || LatticeThick != thickApplied) ShatterLattice(Shatter, main.transform.position);
            if (Lattice > 0f) FadeLattice(eye.y, main.transform.position);
            if (lattice.activeSelf != Lattice > 0f) lattice.SetActive(Lattice > 0f);
            var lc = LatticeColor * Lattice;
            lc.a = 1f;
            latticeMat.SetColor("_TintColor", lc);
        }
        if (extraLayers) FadeStreaks(Anchor.HasValue ? eye.y - p.y : float.NaN);
        // anchored, the streaks near the camera are cleared too (it flies through the block)
        ClearAroundEye(eye - p, Anchor.HasValue ? main.transform.position - p : (Vector3?)null);
    }

    // Anchored (the film's explainer, and its rain until the anchor lets go) the block falls and wraps in plain view:
    // the streaks fade by height around the anchor like the lattice, so no layer pops in at the top. centre is the
    // anchor's height in the mesh's space; NaN puts every streak back to full.
    void FadeStreaks(float centre)
    {
        if (streakCols == null) return;
        if (float.IsNaN(centre))
        {
            if (!streakFaded) return;
            streakFaded = false;
            for (int i = 0; i < streakCols.Length; i++) streakCols[i] = new Color32(255, 255, 255, 255);
            mesh.colors32 = streakCols;
            return;
        }
        streakFaded = true;
        for (int i = 0; i < heads.Length; i++)
        {
            byte a = (byte)(255f * HeightWeight(heads[i].y - centre));
            var c = new Color32(255, 255, 255, a);
            for (int k = 0; k < 4; k++) streakCols[i * 4 + k] = c;
        }
        mesh.colors32 = streakCols;
    }

    // 1 from 8 m below to 4 m above the anchor, 0 from 13 m below and 7 m above, smooth between
    static float HeightWeight(float dy)
    {
        float x = dy > 0f ? Mathf.Clamp01((dy - 4f) / 3f) : Mathf.Clamp01((-dy - 8f) / 5f);
        return 1f - x * x * (3f - 2f * x);
    }

    bool Build()
    {
        var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
        if (shader == null) shader = Shader.Find("Legacy Shaders/Particles/Additive");
        if (shader == null) { Enabled = false; Plugin.L.LogWarning("rain: no particle shader, 3D rain off"); return false; }
        material = new Material(shader) { name = "CloudpunkVR_Rain", mainTexture = MakeStreakTexture(), renderQueue = 3100 };
        material.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var add = Shader.Find("Legacy Shaders/Particles/Additive") ?? shader;
        glowMat = new Material(add) { name = "CloudpunkVR_RainGlow", mainTexture = material.mainTexture, renderQueue = 3100 };
        glowMat.hideFlags = HideFlags.DontUnloadUnusedAsset;

        var rng = new System.Random(1234);
        float R() => (float)rng.NextDouble();
        // one cell's streaks, then the same set in every cell and layer, so the whole is periodic
        var cell = new (Vector3 pos, float yaw, float len)[PerCell];
        for (int i = 0; i < PerCell; i++) cell[i] = (new Vector3(R() * Cell, R() * Height, R() * Cell), R() * 360f, StreakLength * (0.7f + 0.6f * R()));

        // an anchored film adds a layer below and above (appended, so the first layers' streaks and ranks stay as they
        // were): FadeStreaks needs the band around the anchor covered whatever the fall phase. Unanchored jobs keep 3.
        extraLayers = FilmOn && Anchor.HasValue;
        int[] lys = extraLayers ? new[] { 0, 1, 2, -1, 3 } : new[] { 0, 1, 2 };
        int count = Across * Across * lys.Length * PerCell;
        var verts = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(count * 4);
        var uvs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2>(count * 4);
        var tris = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(count * 12);
        int v = 0, t = 0;
        var tilt = Quaternion.Euler(0, 0, 4f);   // a little wind
        foreach (int ly in lys)
            for (int cx = 0; cx < Across; cx++)
                for (int cz = 0; cz < Across; cz++)
                    foreach (var s in cell)
                    {
                        var basePos = new Vector3(cx * Cell, ly * Height, cz * Cell) + s.pos;
                        var across = Quaternion.Euler(0, s.yaw, 0) * Vector3.right * (StreakWidth * 0.5f);
                        var along = tilt * Vector3.up * s.len;
                        verts[v + 0] = basePos - across; verts[v + 1] = basePos + across;
                        verts[v + 2] = basePos + across + along; verts[v + 3] = basePos - across + along;
                        uvs[v + 0] = new Vector2(0, 0); uvs[v + 1] = new Vector2(1, 0); uvs[v + 2] = new Vector2(1, 1); uvs[v + 3] = new Vector2(0, 1);
                        // both sides (the particle shaders cull back faces in some versions)
                        tris[t++] = v; tris[t++] = v + 2; tris[t++] = v + 1; tris[t++] = v; tris[t++] = v + 3; tris[t++] = v + 2;
                        tris[t++] = v; tris[t++] = v + 1; tris[t++] = v + 2; tris[t++] = v; tris[t++] = v + 2; tris[t++] = v + 3;
                        v += 4;
                    }
        heads = new Vector3[count];
        tails = new Vector3[count];
        // a fixed random rank per streak (its own generator, so the layout above is unchanged): Density < 1 shows the
        // streaks ranked below it
        rank = new float[count];
        var rr = new System.Random(4321);
        for (int i = 0; i < count; i++) rank[i] = (float)rr.NextDouble();
        for (int i = 0; i < count; i++) { heads[i] = (verts[i * 4] + verts[i * 4 + 1]) * 0.5f; tails[i] = (verts[i * 4 + 2] + verts[i * 4 + 3]) * 0.5f; }
        fullVerts = new Vector3[count * 4];
        for (int i = 0; i < count * 4; i++) fullVerts[i] = verts[i];
        liveVerts = verts;
        mesh = new Mesh { name = "CloudpunkVR_RainStreaks" };
        if (count * 4 > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        if (extraLayers)
        {
            streakCols = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(count * 4);
            for (int i = 0; i < count * 4; i++) streakCols[i] = new Color32(255, 255, 255, 255);
            mesh.colors32 = streakCols;
        }
        mesh.bounds = new Bounds(new Vector3(Cell * Across, Height * Layers, Cell * Across) * 0.5f, new Vector3(Cell * Across, Height * (Layers + (extraLayers ? 3 : 1)), Cell * Across));
        mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;

        root = new GameObject("CloudpunkVR_Rain");
        Object.DontDestroyOnLoad(root);
        // TransparentFX: drawn by the main camera and by the title screen's menu camera (Default is not in the latter)
        root.layer = LayerMask.NameToLayer("TransparentFX");
        root.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = root.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        streakRenderer = r;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        Plugin.L.LogInfo($"rain: 3D rain ready ({count} streaks, {count * 4} vertices, shader {shader.name})");
        return true;
    }

    // In rain the volumetric lights (Hx: street lamps, searchlights, headlights) scatter a little more, so halos and
    // beams read as light in wet air. Only a parameter of the pass Hx already runs: measured 5.08 against 5.25 ms of
    // GPU for density 0.1 and 0.3. The game's own value comes back when the rain stops.
    public static float RainDensityScale = 1.5f;
    readonly System.Collections.Generic.Dictionary<int, (HxVolumetricCamera cam, float density)> wet = new();
    float nextWetScan, wetApplied = 1f;

    void WetAir(bool raining)
    {
        float scale = 1f + (RainDensityScale - 1f) * Mathf.Clamp01(Wet);
        if (raining && scale != wetApplied)
        {
            // a film ramps the rain in: the scattering follows (from the game's own value each time, no drift)
            foreach (var kv in wet) if (kv.Value.cam != null) kv.Value.cam.Density = kv.Value.density * scale;
            wetApplied = scale;
        }
        if (raining && Time.unscaledTime >= nextWetScan)
        {
            nextWetScan = Time.unscaledTime + 2f;
            foreach (var hx in Object.FindObjectsOfType<HxVolumetricCamera>())
            {
                int id = hx.GetInstanceID();
                if (wet.ContainsKey(id)) continue;
                wet[id] = (hx, hx.Density);
                hx.Density *= scale;
            }
        }
        if (raining || wet.Count == 0) return;
        foreach (var kv in wet)
            if (kv.Value.cam != null) kv.Value.cam.Density = kv.Value.density;
        wet.Clear();
    }

    // The pattern's cells as lines: verticals at every cell corner over the full height, horizontals at every layer
    // boundary; each line two crossed quads (visible from any side), the outer columns brighter. Child of the streak
    // root, so it snaps and falls with the streaks. One period more below and above than the streaks (LatticeExtra), so
    // FadeLattice's window around the anchor is always covered and the wrap is not seen.
    const int LatticeExtra = 1;

    void BuildLattice()
    {
        var add = Shader.Find("Legacy Shaders/Particles/Additive");
        if (add == null) return;
        latticeMat = new Material(add) { name = "CloudpunkVR_RainLattice", mainTexture = Texture2D.whiteTexture, renderQueue = 3101 };
        latticeMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var segs = new System.Collections.Generic.List<(Vector3 a, Vector3 b, float k)>();
        float W = Cell * Across, H = Height * Layers;
        for (int i = 0; i <= Across; i++)
            for (int j = 0; j <= Across; j++)
            {
                bool edge = (i == 0 || i == Across) && (j == 0 || j == Across);
                segs.Add((new Vector3(i * Cell, -Height * LatticeExtra, j * Cell), new Vector3(i * Cell, H + Height * LatticeExtra, j * Cell), edge ? 1f : 0.45f));
            }
        // the brighter frame on every level (v3 had it on the top and bottom level only, which swapped at the wrap)
        for (int k = -LatticeExtra; k <= Layers + LatticeExtra; k++)
            for (int i = 0; i <= Across; i++)
            {
                bool edgeI = i == 0 || i == Across;
                float kk = edgeI ? 1f : 0.45f;
                segs.Add((new Vector3(i * Cell, k * Height, 0), new Vector3(i * Cell, k * Height, W), kk));
                segs.Add((new Vector3(0, k * Height, i * Cell), new Vector3(W, k * Height, i * Cell), kk));
            }
        // cut every line into ~0.75 m pieces (they come apart on Shatter)
        var pieces = new System.Collections.Generic.List<(Vector3 a, Vector3 b, float k)>();
        foreach (var (a0, b0, k0) in segs)
        {
            int np = Mathf.Max(1, Mathf.RoundToInt((b0 - a0).magnitude / 0.75f));
            for (int q = 0; q < np; q++) pieces.Add((Vector3.Lerp(a0, b0, q / (float)np), Vector3.Lerp(a0, b0, (q + 1) / (float)np), k0));
        }
        segs = pieces;
        int n = segs.Count;
        var verts = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(n * 8);
        var cols = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(n * 8);
        var uvs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2>(n * 8);
        var tris = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(n * 24);
        int v = 0, t = 0;
        float w = LatticeWidth * 0.5f;
        foreach (var (a, b, k) in segs)
        {
            var d = (b - a).normalized;
            var s1 = Mathf.Abs(d.y) > 0.5f ? Vector3.right : Vector3.up;
            var s2 = Vector3.Cross(d, s1).normalized;
            byte c = (byte)(255 * k);
            foreach (var side in new[] { s1 * w, s2 * w })
            {
                verts[v] = a - side; verts[v + 1] = a + side; verts[v + 2] = b + side; verts[v + 3] = b - side;
                for (int q = 0; q < 4; q++) { cols[v + q] = new Color32(c, c, c, 255); uvs[v + q] = new Vector2(0.5f, 0.5f); }
                tris[t++] = v; tris[t++] = v + 1; tris[t++] = v + 2; tris[t++] = v; tris[t++] = v + 2; tris[t++] = v + 3;
                tris[t++] = v; tris[t++] = v + 2; tris[t++] = v + 1; tris[t++] = v; tris[t++] = v + 3; tris[t++] = v + 2;
                v += 4;
            }
        }
        // piece data for Shatter: each piece flies out from the block's centre, a little random, and falls; one in
        // seven is aimed past the camera (set on the first shattered frame, when the camera's place is known)
        pA = new Vector3[n]; pB = new Vector3[n]; pVel = new Vector3[n]; pToCam = new bool[n];
        pSide = new Vector3[n * 8];
        var rng = new System.Random(777);
        var mid = new Vector3(W, H, W) * 0.5f;
        for (int i = 0; i < n; i++)
        {
            var (a, b, _) = segs[i];
            pA[i] = a; pB[i] = b;
            var c = (a + b) * 0.5f;
            var outward = c - mid;
            outward.y *= 0.5f;
            var dir = outward.sqrMagnitude > 1e-4f ? outward.normalized : Vector3.up;
            var jitter = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f);
            pVel[i] = dir * (0.6f + 0.8f * (float)rng.NextDouble()) + jitter * 0.9f;
            pToCam[i] = rng.NextDouble() < 0.14;
            for (int q = 0; q < 8; q++)
            {
                var end = q % 4 >= 2 ? b : a;
                pSide[i * 8 + q] = verts[i * 8 + q] - end;
            }
        }
        aimed = false;
        latLive = verts;
        latCols = cols;
        latBaseK = new byte[n * 8];
        latRest = new Vector3[n * 8];
        for (int i = 0; i < n * 8; i++) { latBaseK[i] = cols[i].r; latRest[i] = verts[i]; }
        var m = new Mesh { name = "CloudpunkVR_RainLattice" };
        if (n * 8 > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.vertices = verts; m.colors32 = cols; m.uv = uvs; m.triangles = tris;
        latticeMesh = m;
        m.bounds = new Bounds(new Vector3(W, H, W) * 0.5f, new Vector3(W, H + Height * (1 + 2 * LatticeExtra), W) + Vector3.one);
        lattice = new GameObject("CloudpunkVR_RainLattice");
        lattice.layer = root.layer;
        lattice.transform.SetParent(root.transform, false);
        lattice.AddComponent<MeshFilter>().sharedMesh = m;
        var r = lattice.AddComponent<MeshRenderer>();
        r.sharedMaterial = latticeMat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        Plugin.L.LogInfo($"rain: lattice {n} lines ({Across}x{Across}x{Layers} cells of {Cell} x {Height} m)");
    }

    // Shatter s (0-1) and the thickness factor. Each piece flies out along its velocity (eased: fast, then slowing)
    // and falls; within s 0-0.2 it turns from its place in the line to a streak along its motion (a trail), which
    // at the end points straight down, like the rain it becomes. One in seven was aimed past the camera.
    void ShatterLattice(float s, Vector3 camWorld)
    {
        shatterApplied = s; thickApplied = LatticeThick;
        if (latticeMesh == null) return;
        if (s > 0f && !aimed)
        {
            aimed = true;
            var cam = lattice.transform.InverseTransformPoint(camWorld);
            var rng = new System.Random(4242);
            for (int i = 0; i < pA.Length; i++)
            {
                if (!pToCam[i]) continue;
                var c = (pA[i] + pB[i]) * 0.5f;
                // past the lens, 0.5-1.6 m to the side, reached at about 60 % of the way out
                var side = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).normalized;
                var target = cam + side * (1.0f + 1.2f * (float)rng.NextDouble());   // 0.5 m whited out the frame (v3b)
                pVel[i] = (target - c) / (ShatterSpread * 0.6f);
            }
        }
        float e = 1f - (1f - s) * (1f - s);
        float de = 2f * (1f - s);
        const float drop = 5f;
        float w = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(s / 0.2f));
        float thick = LatticeThick * Mathf.Lerp(1f, 0.7f, s);
        for (int i = 0; i < pA.Length; i++)
        {
            var a = pA[i]; var b = pB[i];
            var c = (a + b) * 0.5f;
            var half0 = (b - a) * 0.5f;
            var off = pVel[i] * (ShatterSpread * e);
            off.y -= drop * s * s;
            var mv = pVel[i] * (ShatterSpread * de);
            mv.y -= 2f * drop * s;
            var dir = mv.sqrMagnitude > 1e-6f ? mv.normalized : Vector3.down;
            var trail = dir * 0.6f;
            var endA = c + off - Vector3.Lerp(half0, trail, w);
            var endB = c + off + half0 * (1f - w);
            int v = i * 8;
            for (int q = 0; q < 8; q++)
                latLive[v + q] = (q % 4 >= 2 ? endB : endA) + pSide[v + q] * thick;
        }
        latticeMesh.vertices = latLive;
        latticeMesh.RecalculateBounds();
    }

    // The pattern repeats every Height and wraps as it falls: at the wrap the lowest level drops out and a new one appears
    // on top (v3: a level popping in 3 m under the wide camera every 0.78 s). Faded by height around the anchor
    // (HeightWeight: full from 8 m below to 4 m above, gone 13 m below and 7 m above), which the mesh always covers:
    // the levels fade in and out instead of popping, and none comes near a camera looking down from above. By each
    // piece's place before the shatter, so a piece keeps its brightness as it flies. Lines within 1.2 m of the camera
    // fade out too (gone at 0.4 m): flying through the block, a line at the lens was a bar across the frame (v3, 84.6 s).
    void FadeLattice(float centreY, Vector3 camWorld)
    {
        if (latCols == null || lattice == null) return;
        var lp = lattice.transform.position;
        var cam = camWorld - lp;
        for (int i = 0; i < latBaseK.Length; i++)
        {
            float w = HeightWeight(lp.y + latRest[i].y - centreY);
            float d2 = (latLive[i] - cam).sqrMagnitude;
            if (d2 < 1.44f)
            {
                float u = Mathf.Clamp01((Mathf.Sqrt(d2) - 0.4f) / 0.8f);
                w *= u * u * (3f - 2f * u);
            }
            byte c = (byte)(latBaseK[i] * w);
            latCols[i] = new Color32(c, c, c, 255);
        }
        latticeMesh.colors32 = latCols;
    }

    /// <summary>Hide the streaks and put the Hx densities back (a film capture ending).</summary>
    public void Shutdown()
    {
        WetAir(false);
        if (root != null) { Object.Destroy(root); root = null; }
    }

    void ClearAroundEye(Vector3 eye, Vector3? cam = null)
    {
        nowHidden.Clear();
        float r2 = ClearRadius * ClearRadius;
        for (int i = 0; i < heads.Length; i++)
        {
            // distance from the eye to the streak's segment
            var a = heads[i]; var ab = tails[i] - a;
            if (rank[i] >= Density) { nowHidden.Add(i); continue; }
            float t = Mathf.Clamp01(Vector3.Dot(eye - a, ab) / ab.sqrMagnitude);
            if ((a + ab * t - eye).sqrMagnitude < r2) { nowHidden.Add(i); continue; }
            if (cam is Vector3 c)
            {
                t = Mathf.Clamp01(Vector3.Dot(c - a, ab) / ab.sqrMagnitude);
                if ((a + ab * t - c).sqrMagnitude < r2) nowHidden.Add(i);
            }
        }
        if (nowHidden.SetEquals(hidden)) return;
        foreach (var i in hidden) if (!nowHidden.Contains(i)) for (int k = 0; k < 4; k++) liveVerts[i * 4 + k] = fullVerts[i * 4 + k];
        foreach (var i in nowHidden) for (int k = 0; k < 4; k++) liveVerts[i * 4 + k] = fullVerts[i * 4];
        hidden.Clear();
        hidden.UnionWith(nowHidden);
        mesh.vertices = liveVerts;
    }

    // A thin streak: bright along the middle, fading to nothing at both ends and edges.
    static Texture2D MakeStreakTexture()
    {
        const int w = 8, h = 64;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w * 2 - 1, vv = (y + 0.5f) / h;
                float across = Mathf.Clamp01(1 - u * u);
                float along = Mathf.Sin(vv * Mathf.PI) * Mathf.Lerp(0.5f, 1f, vv);   // brighter toward the head of the drop
                t.SetPixel(x, y, new Color(1, 1, 1, across * along));
            }
        t.Apply();
        return t;
    }
}
