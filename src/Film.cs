using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace CloudpunkVR;

// Film capture for the promo (VRStage workspaces/cloudpunk): drive one game camera along a keyframed path at a fixed
// time step and save every frame — colour without the UI, a depth+normals pass for compositing, and (from ui.from on)
// the UI layer on transparent black — plus a per-frame camera sidecar (pose, fov, clip planes) so another renderer
// can match the camera exactly. Dev channel only: `film <job.json>`, `filmstop`, `campose [name]`.
//
// Job (positions/rotations in game world space; times in seconds from the start of the capture):
//   { "out": "D:/…/title_path", "fps": 30, "width": 1920, "height": 1080, "camera": "menu" | "main" | "<path>",
//     "keys": [ { "t": 0, "position": [x,y,z], "rotation": [x,y,z,w] | [pitch,yaw,roll], "fov": 50 }, … ],
//     "hold": 3.0,            // keep capturing this long after the last key (camera parked on it)
//     "offscreen": true,      // render into a width×height target instead of the window (frames bigger than the desktop: 4K)
//     "ui": { "from": 13.0 },  // UI hidden in colour frames before this time; UI layer frames saved from then on
//     "depth": true, "warmup": 30,
//     "exclusive": true,      // only the filmed camera renders (main camera, cockpit and HUD off); default on
//     "rig": { "paths": ["Global/UI/Menu/Objects/Logo v2", …], "pivot": [x,y,z], "to": [x,y,z], "yaw": 0 },
//                             // move these objects (activated, inactive ones too) rigidly: rotate by yaw about pivot,
//                             // then carry pivot to "to"; a key with "rig": true is given in the pre-move space
//     "hide": ["Global/…", …],  // objects switched off for the capture
//     "layers": { "from": "main", "remove": ["PlayerCar", "UI"], "add": [] },
//     "motionBlur": { "samples": 8, "shutter": 0.5 },  // render this many sub-frames per frame (time step
//                             // 1/(fps·samples)); the first shutter·samples of them are averaged (0.5 = a 180° shutter,
//                             // the film look; 1 = the whole frame interval, default) and saved as
//                             // average (in linear light): a 360° shutter, the blur a real camera gets at speed
//     "fx": { … },            // our own full-screen shader pass in the game's frame (FilmFx)
//     "traffic": { … },       // copies of the game's traffic cars on lanes of our own (FilmTraffic)
//     "rain": { "from": 2.7,   // the game's rain forced off before this film time and on after it
//               "ramp": [[2.7, 0], [5.5, 1]],  // how much it rains from "from" on (0–1, linear between keys; default
//                             // 1): scales the game's rain and splash intensities, the share of streaks shown and the
//                             // extra Hx scattering, so the rain starts as a few drops and fills in
//               "streaks": true,  // our world-space streaks (VRRain, normally only in the headset); default on
//               "across": 5,  // their extent in 6 m cells per side (5 = within ~15 m of the camera)
//               "opacity": 0.4,   // the streaks' opacity at full rain
//               "width": 0.014, "length": 0.55,  // streak width and length in metres (the headset's defaults)
//               "clear": 1.1,  // streaks within this of the camera are not drawn (a near one is a pale wedge)
//               "screen": false,  // also the game's screen-space rain sheets (the far rain). Default off: forced on in a flat
//                             // capture they sometimes render the whole frame white (11-08, 5 sessions of ~12: a fog that
//                             // never clears), likely when the game's own weather is not raining
//               "lens": false,    // drips running down the lens (SplashesAndDrips); default off: black blotches all over
//                             // the frame in some sessions, like the white of "screen"
//               "anchor": [x,y,z], "anchorUntil": 30,  // the streak pattern centred on this point (not the camera) until then
//               "glow": [[t, 0..1], …], "lattice": [[t, 0..1], …],  // the explainer: streaks glowing, the cell lattice
//               "latticeWidth": 0.12, "glowColor": [r,g,b], "latticeColor": [r,g,b],
//               "shatter": [[t, 0..1], …], "shatterSpread": 6,  // the lattice breaks into pieces flying apart
//               "latticeHoldFrom": t,  // the lattice stops falling from t (a close-up with no line through him)
//               "latticeThick": [[t, x], …] },  // the lattice's line width times x (the flash at the snap)
//     "clock": 0,             // added to the film time for the traffic and the rain's fall: two captures cut together
//                             // continue each other when the second's clock starts where the first's ended
//     "exposure": { "ev": [[0, 0.5], [3, 0.5], [6, 0]],  // film time → exposure offset in stops, linear between keys
//                   "mode": "grading" },  // "grading": added to PPv2 ColorGrading.postExposure (before tone mapping,
//                             // so bloom and neon follow); "frame": the saved frames scaled in linear light (always
//                             // works; highlights clip). "grading" falls back to "frame" if no profile has ColorGrading
//     "hero": { … }, "cockpit": { … },  // a car of our own and the cockpit interior drawn inside it (FilmCockpit)
//     "layerCull": { "Vehicle": 900 },  // per-layer cull distances on the filmed camera while filming
//     "hideCanvases": ["CharHUD", "ParkUI*"],  // world-space canvases switched off by object name (NPC name tags,
//                             // interaction prompts; a trailing * matches a prefix); names of other world-space canvases
//                             // near the camera are logged as they turn up
//     "exposureSpeed": 0.15 }  // slow the game's eye adaptation (PPv2 AutoExposure) while filming: a scene that darkens
//                             // for a moment (the promo's set dissolving) must not pump the exposure up
//                             // the filmed camera also draws the main camera's layers (the title MenuCamera alone draws
//                             // only the title set), minus the cockpit and HUD layers
// The camera path is the same Hermite/Catmull-Rom spline VRStage's "path" camera uses (zero velocity at both ends).
// Capture: the back buffer is read into one persistent RGB24 texture whose CPU copy is used in place (nothing is
// allocated in the game's heap per frame), motion-blur sub-frames are summed in linear light with lookup tables on the
// thread pool, and PNGs are encoded on background threads (FilmPng) while the game renders on. Every 30 frames the log
// gives where the time went: the game's own update and render, the read-back, our blur and the queue to the encoders.
// "camera" may name an inactive camera (the title MenuCamera during play): it and its parents are switched on for the
// capture, its CinemachineBrain is paused, and everything is put back afterwards.
internal static class Film
{
    sealed class Key { public double t; public Vector3 p; public Vector3 fwd, up; public float fov; }

    static List<Key> keys;
    static string outDir, cameraSpec;
    static int fps = 30, width = 1920, height = 1080, frame, totalFrames, warmup, samples = 1, exposed = 1, tick;
    static Texture2D grab;
    static RenderTexture offRT;     // "offscreen": the filmed camera renders here (frames bigger than the window can be)
    static uint[] accSum;
    static FilmPng png;
    static readonly ushort[] Lin16 = MakeLin16();
    static readonly byte[] ToSrgb = MakeToSrgb();
    // timing (Stopwatch ticks, summed over the current 30-frame window)
    static long lastEnd, winStart, tGame, tPose, tRead, tBlur, tQueue, tCanvas, tTraffic, tFx;
    static int winFrames;
    static bool sizeWarned;
    static readonly HashSet<string> seenCanvases = new();
    static float uiFrom = float.MaxValue;
    static bool depth;
    static Camera cam;
    static int poseFrame = -1;
    static float savedCaptureRate;
    static readonly List<Behaviour> hiddenUi = new();
    static readonly System.Text.StringBuilder sidecar = new();
    static Shader depthShader;
    static Vector3 restorePos;
    static Quaternion restoreRot;
    static float restoreFov;
    static readonly List<(GameObject go, bool active)> restoreActive = new();
    static readonly List<(Behaviour b, bool enabled)> restoreEnabled = new();
    static readonly List<(Transform t, Vector3 p, Quaternion r)> restorePose = new();
    static Vector3 rigPivot, rigTo;
    static int restoreMask = int.MinValue;
    static bool exclusive;
    static readonly List<(UnityEngine.Rendering.PostProcessing.AutoExposure ae, float up, float down, bool ou, bool od)> restoreExposure = new();
    static int wantMask = int.MinValue;
    // "rain": { "from": t } — the game's rain is a post effect (RainAndSheets, with SplashesAndDrips on the lens) that is
    // only on while the current weather has it: off before film time "from" (the set is indoors), forced on after it
    static float rainFrom = float.NaN;
    static readonly List<(PostProcessEffectSettings s, bool active, bool on, bool ov)> restoreRain = new();
    // rain.ramp and its intensity parameters (value, overrideState) as the game had them
    static float[][] rainRamp;
    static readonly List<(FloatParameter p, float v, bool ov, bool lens)> rainLevels = new();
    static VRRain filmRain;
    static float rainOpacity = 0.4f, savedOpacity, savedWidth, savedLength, savedLatticeWidth, savedClear;
    static float[][] rainGlow, rainLattice, rainShatter, rainThick;
    static float rainHoldFrom = float.MaxValue;
    static Vector3? rainAnchor;
    static float rainAnchorUntil = float.MaxValue, clock;
    static bool rainLens, savedKeepDrips, savedScreenRain, rainGate;
    // exposure: ev curve, the ColorGrading settings it is added to, or the frame gain
    static float[][] exposureEv;
    static bool exposureFrame;
    static readonly List<(ColorGrading cg, float v, bool ov)> restoreGrading = new();
    static double filmTime;
    /// <summary>The film time of the frame being rendered.</summary>
    internal static double Now => filmTime;
    // "layerCull": { "Vehicle": 900, … } — per-layer cull distances on the filmed camera (the game culls traffic at 300 m)
    static float[] restoreCull;
    // "hideCanvases": ["CharHUD"] — world-space canvases switched off while filming, by object name (NPC name tags share
    // the Text layer with the title logo, so the layer cannot simply be dropped); checked again as NPCs spawn
    static string[] hideCanvasNames = Array.Empty<string>();
    static readonly List<Canvas> hiddenCanvases = new();
    static Quaternion rigRot = Quaternion.identity;

    public static bool Active => keys != null;

    public static void Start(string jobPath)
    {
        if (Active) { Plugin.L.LogWarning("film: already running"); return; }
        var j = JsonNode.Parse(File.ReadAllText(jobPath)).AsObject();
        outDir = (string)j["out"] ?? Path.Combine(Plugin.Dir, "film", Path.GetFileNameWithoutExtension(jobPath));
        fps = (int?)j["fps"] ?? 30;
        width = (int?)j["width"] ?? 1920;
        height = (int?)j["height"] ?? 1080;
        cameraSpec = (string)j["camera"] ?? "menu";
        depth = (bool?)j["depth"] ?? true;
        warmup = (int?)j["warmup"] ?? 30;
        samples = Math.Max(1, j["motionBlur"] is JsonObject mb ? (int?)mb["samples"] ?? 1 : 1);
        float shutter = j["motionBlur"] is JsonObject mb2 ? Mathf.Clamp((float?)mb2["shutter"] ?? 1f, 0.05f, 1f) : 1f;
        exposed = Math.Clamp((int)Math.Round(samples * shutter), 1, samples);
        uiFrom = j["ui"] is JsonObject ui && ui["from"] != null ? (float)ui["from"] : float.MaxValue;
        rigPivot = rigTo = Vector3.zero; rigRot = Quaternion.identity;
        var rigPaths = new List<string>();
        if (j["rig"] is JsonObject rig)
        {
            rigPivot = V3(rig["pivot"].AsArray());
            rigTo = rig["to"] != null ? V3(rig["to"].AsArray()) : rigPivot;
            rigRot = Quaternion.Euler(0, (float?)rig["yaw"] ?? 0f, 0);
            foreach (var pn in rig["paths"].AsArray()) rigPaths.Add((string)pn);
        }
        var list = new List<Key>();
        foreach (var kn in j["keys"].AsArray())
        {
            var k = kn.AsObject();
            var r = Rot(k["rotation"].AsArray());
            var p = V3(k["position"].AsArray());
            if ((bool?)k["rig"] == true) { p = RigPoint(p); r = rigRot * r; }
            list.Add(new Key { t = (double)k["t"], p = p, fwd = r * Vector3.forward, up = r * Vector3.up, fov = (float?)k["fov"] ?? float.NaN });
        }
        list.Sort((a, b) => a.t.CompareTo(b.t));
        for (int i = 0; i < list.Count; i++) if (float.IsNaN(list[i].fov)) list[i].fov = i > 0 ? list[i - 1].fov : 50f;
        if (list.Count < 2) { Plugin.L.LogError("film: needs at least two keys"); return; }
        restoreActive.Clear(); restoreEnabled.Clear(); restorePose.Clear();
        cam = FindCamera(cameraSpec);
        if (cam == null) { Plugin.L.LogError($"film: camera '{cameraSpec}' not found (enabled cameras: {string.Join(", ", Camera.allCameras.Select(c => Diag.PathOf(c.transform)))})"); return; }
        Activate(cam.gameObject);
        SetEnabled(cam, true);
        foreach (var b in cam.GetComponents<Behaviour>())
            if (b.GetIl2CppType().Name == "CinemachineBrain") SetEnabled(b, false);
        if (j["layers"] is JsonObject lay)
        {
            int mask = cam.cullingMask;
            if ((string)lay["from"] == "main" && Global.Instance != null && Global.Instance.mainCamera != null) mask |= Global.Instance.mainCamera.cullingMask;
            if (lay["add"] is JsonArray add) foreach (var n in add) { int l = LayerMask.NameToLayer((string)n); if (l >= 0) mask |= 1 << l; }
            if (lay["remove"] is JsonArray rem) foreach (var n in rem) { int l = LayerMask.NameToLayer((string)n); if (l >= 0) mask &= ~(1 << l); }
            restoreMask = cam.cullingMask;
            cam.cullingMask = mask;
            wantMask = mask;
            Plugin.L.LogInfo($"film: culling mask 0x{restoreMask:X8} -> 0x{mask:X8}");
        }
        if ((bool?)j["exclusive"] ?? true)
            foreach (var c in Camera.allCameras)
                if (c != cam && c.targetTexture == null && !UiOnly(c) && !FilmCockpit.Owns(c)) SetEnabled(c, false);
        if (j["hide"] is JsonArray hide)
            foreach (var pn in hide)
            {
                var t = FindAny((string)pn);
                if (t == null) { Plugin.L.LogWarning("film: hide object not found: " + (string)pn); continue; }
                restoreActive.Add((t.gameObject, t.gameObject.activeSelf));
                t.gameObject.SetActive(false);
            }
        foreach (var path in rigPaths)
        {
            var t = FindAny(path);
            if (t == null) { Plugin.L.LogWarning("film: rig object not found: " + path); continue; }
            Activate(t.gameObject);
            restorePose.Add((t, t.position, t.rotation));
            t.SetPositionAndRotation(RigPoint(t.position), rigRot * t.rotation);
        }
        // the cockpit interior is drawn by this component in the main camera's pass: off, unless the job draws a cockpit
        bool wantCockpit = j["cockpit"] is JsonObject;
        if (((bool?)j["exclusive"] ?? true) || wantCockpit)
            foreach (var b in cam.GetComponents<Behaviour>())
                if (b.GetIl2CppType().Name == "MainCameraVehicleRenderer") SetEnabled(b, wantCockpit);
        double hold = (double?)j["hold"] ?? 0;
        totalFrames = (int)Math.Round((list[^1].t + hold) * fps) + 1;
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir, "*.png")) File.Delete(f);
        restorePos = cam.transform.position; restoreRot = cam.transform.rotation; restoreFov = cam.fieldOfView;
        Screen.SetResolution(width, height, false);
        // "offscreen": true — render the filmed camera into a width×height target and read that back, not the window's
        // back buffer: Windows clamps a window to the desktop (3840×2160 came out 3840×1773 on this machine, 11-08)
        offRT = null;
        if ((bool?)j["offscreen"] ?? false)
        {
            offRT = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "CloudpunkVR film", antiAliasing = 1 };
            offRT.Create();
            cam.targetTexture = offRT;
        }
        savedCaptureRate = Time.captureFramerate;
        Time.captureFramerate = fps * samples;
        depthShader = depth ? Shader.Find("Hidden/Internal-DepthNormalsTexture") : null;
        if (depth && depthShader == null) Plugin.L.LogWarning("film: Hidden/Internal-DepthNormalsTexture is not in this build; no depth pass");
        keys = list;
        frame = -1;
        tick = -warmup;
        lastEnd = winStart = 0; winFrames = 0; tGame = tPose = tRead = tBlur = tQueue = 0; sizeWarned = false;
        seenCanvases.Clear();
        sidecar.Clear();
        sidecar.Append("{\"fps\":").Append(fps).Append(",\"width\":").Append(width).Append(",\"height\":").Append(height)
            .Append(",\"camera\":\"").Append(Diag.PathOf(cam.transform)).Append("\",\"uiFrom\":").Append(uiFrom == float.MaxValue ? "null" : uiFrom.ToString("R"))
            .Append(",\"depthEncoding\":\"unity DepthNormals: RG = view normal (stereographic), BA = linear01 depth (EncodeFloatRG) × far\",\"frames\":[\n");
        exclusive = (bool?)j["exclusive"] ?? true;
        clock = (float?)j["clock"] ?? 0f;
        rainGlow = rainLattice = rainShatter = rainThick = null; rainAnchor = null; rainAnchorUntil = float.MaxValue; rainHoldFrom = float.MaxValue;
        restoreRain.Clear();
        rainFrom = j["rain"] is JsonObject rj ? (float?)rj["from"] ?? 0f : float.NaN;
        rainLevels.Clear();
        rainRamp = null;
        var rjo = j["rain"] as JsonObject;
        if (!float.IsNaN(rainFrom))
        {
            rainRamp = Curve(rjo["ramp"]);
            rainLens = (bool?)rjo["lens"] ?? false;
            foreach (var s in Resources.FindObjectsOfTypeAll<PostProcessEffectSettings>())
            {
                if (s == null) continue;
                var tn = s.GetIl2CppType().Name;
                if (tn != "RainAndSheets" && tn != "SplashesAndDrips") continue;
                restoreRain.Add((s, s.active, s.enabled.value, s.enabled.overrideState));
                // the levels a ramp scales (and the lens drips that "lens": false turns off)
                var ras = s.TryCast<RainAndSheets>();
                if (ras != null) { Level(ras.rainIntensity, false); Level(ras.sheetIntensity, false); }
                var sad = s.TryCast<SplashesAndDrips>();
                if (sad != null) { Level(sad.splashItensity, false); Level(sad.dripsItensity, true); }
            }
            // which of the game's screen effects may draw: the gate (VRRain's IsEnabledAndSupported patch) is on for every
            // rain job, so "screen" and "lens" mean the same with or without the streaks
            VRRain.Patch(Plugin.Harmony);
            rainGate = true;
            savedKeepDrips = VRRain.KeepDrips; savedScreenRain = VRRain.ScreenRain;
            VRRain.ScreenRain = (bool?)rjo["screen"] ?? false;
            VRRain.KeepDrips = true;           // the lens drips: FilmLens below ("lens")
            VRRain.FilmLens = rainLens;
            VRRain.FilmOn = true;
            // our world-space streaks, flat (VR has them already)
            filmRain = null;
            if (((bool?)rjo["streaks"] ?? true) && !UnityEngine.XR.XRSettings.enabled)
            {
                filmRain = new VRRain((int?)rjo["across"] ?? 5);
                savedOpacity = VRRain.Opacity;
                savedWidth = VRRain.StreakWidth; savedLength = VRRain.StreakLength; savedClear = VRRain.ClearRadius;
                rainOpacity = (float?)rjo["opacity"] ?? VRRain.Opacity;
                // a flat frame at 480–1080p needs thicker streaks than a headset (0.014 m is under a pixel at 3 m)
                VRRain.StreakWidth = (float?)rjo["width"] ?? VRRain.StreakWidth;
                VRRain.StreakLength = (float?)rjo["length"] ?? VRRain.StreakLength;
                VRRain.ClearRadius = (float?)rjo["clear"] ?? VRRain.ClearRadius;   // streaks this close to the camera are not drawn
                rainGlow = Curve(rjo["glow"]);
                rainLattice = Curve(rjo["lattice"]);
                rainShatter = Curve(rjo["shatter"]);
                rainThick = Curve(rjo["latticeThick"]);
                rainHoldFrom = (float?)rjo["latticeHoldFrom"] ?? float.MaxValue;
                VRRain.ShatterSpread = (float?)rjo["shatterSpread"] ?? 6f;
                rainAnchor = rjo["anchor"] is JsonArray an ? V3(an) : null;
                rainAnchorUntil = (float?)rjo["anchorUntil"] ?? float.MaxValue;
                savedLatticeWidth = VRRain.LatticeWidth;
                VRRain.LatticeWidth = (float?)rjo["latticeWidth"] ?? VRRain.LatticeWidth;
                if (rjo["glowColor"] is JsonArray gc) VRRain.GlowColor = new Color((float)gc[0], (float)gc[1], (float)gc[2], 1f);
                if (rjo["latticeColor"] is JsonArray lcol) VRRain.LatticeColor = new Color((float)lcol[0], (float)lcol[1], (float)lcol[2], 1f);
            }
            Plugin.L.LogInfo($"film: rain from {rainFrom:F2} s, {restoreRain.Count} effect settings, ramp {(rainRamp != null ? rainRamp.Length + " keys" : "none")}, " +
                             $"streaks {(filmRain != null ? "on" : "off")}, screen rain {(VRRain.ScreenRain ? "on" : "off")}, lens {(rainLens ? "on" : "off")}");
            if (filmRain == null && !VRRain.ScreenRain && !UnityEngine.XR.XRSettings.enabled)
                Plugin.L.LogWarning("film: rain with neither streaks nor \"screen\": true draws no falling rain");
        }
        // exposure offset over film time
        restoreGrading.Clear();
        exposureEv = null; exposureFrame = false;
        if (j["exposure"] is JsonObject ej && (exposureEv = Curve(ej["ev"])) != null)
        {
            exposureFrame = (string)ej["mode"] == "frame";
            if (!exposureFrame)
            {
                var modes = new List<string>();
                foreach (var s in Resources.FindObjectsOfTypeAll<PostProcessEffectSettings>())
                {
                    var cg = s != null ? s.TryCast<ColorGrading>() : null;
                    if (cg == null) continue;
                    restoreGrading.Add((cg, cg.postExposure.value, cg.postExposure.overrideState));
                    modes.Add($"{cg.name}:{cg.gradingMode.value}{(cg.active ? "" : " (inactive)")}");
                }
                if (restoreGrading.Count == 0) { exposureFrame = true; Plugin.L.LogWarning("film: no ColorGrading in any profile; exposure scales the saved frames instead"); }
                else Plugin.L.LogInfo($"film: exposure on {restoreGrading.Count} ColorGrading settings ({string.Join(", ", modes)}; LowDefinitionRange ignores postExposure: use \"mode\": \"frame\" then)");
            }
            if (exposureFrame) Plugin.L.LogInfo("film: exposure scales the saved frames in linear light");
        }
        restoreCull = null;
        hideCanvasNames = j["hideCanvases"] is JsonArray hc ? hc.Select(n => (string)n).ToArray() : Array.Empty<string>();
        hiddenCanvases.Clear();
        if (j["layerCull"] is JsonObject lc)
        {
            var d = cam.layerCullDistances;
            restoreCull = d.ToArray();
            var nd = d.ToArray();
            foreach (var kv in lc) { int l = LayerMask.NameToLayer(kv.Key); if (l >= 0) nd[l] = (float)kv.Value; }
            cam.layerCullDistances = nd;
        }
        restoreExposure.Clear();
        if (j["exposureSpeed"] != null)
        {
            float sp = (float)j["exposureSpeed"];
            foreach (var s in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessEffectSettings>())
            {
                var ae = s.TryCast<UnityEngine.Rendering.PostProcessing.AutoExposure>();
                if (ae == null) continue;
                restoreExposure.Add((ae, ae.speedUp.value, ae.speedDown.value, ae.speedUp.overrideState, ae.speedDown.overrideState));
                ae.speedUp.overrideState = true; ae.speedUp.value = sp;
                ae.speedDown.overrideState = true; ae.speedDown.value = sp;
            }
            Plugin.L.LogInfo($"film: eye adaptation speed {sp} on {restoreExposure.Count} AutoExposure settings");
        }
        FilmStage.Start(j);
        FilmTraffic.Start(j);
        FilmFx.Start(j, cam);
        FilmCockpit.Start(j, cam);          // after the stage: it may seat the performer in the car
        Plugin.L.LogInfo($"film: {totalFrames} frames at {fps} fps" + (samples > 1 ? $" ({samples} sub-frames each, {exposed} exposed)" : "") + $", {width}x{height}, camera {Diag.PathOf(cam.transform)} → {outDir}");
    }

    public static void Stop(string why)
    {
        if (!Active) return;
        keys = null;
        ShowUi();
        FilmCockpit.Stop();
        FilmFx.Stop();
        FilmTraffic.Stop();
        FilmStage.Stop();
        Time.captureFramerate = (int)savedCaptureRate;
        if (offRT != null)
        {
            if (cam != null && cam.targetTexture == offRT) cam.targetTexture = null;
            offRT.Release();
            UnityEngine.Object.Destroy(offRT);
            offRT = null;
        }
        if (cam != null) { cam.transform.SetPositionAndRotation(restorePos, restoreRot); cam.fieldOfView = restoreFov; if (restoreMask != int.MinValue) cam.cullingMask = restoreMask; }
        restoreMask = int.MinValue;
        wantMask = int.MinValue;
        for (int i = restorePose.Count - 1; i >= 0; i--) if (restorePose[i].t != null) restorePose[i].t.SetPositionAndRotation(restorePose[i].p, restorePose[i].r);
        for (int i = restoreEnabled.Count - 1; i >= 0; i--) if (restoreEnabled[i].b != null) restoreEnabled[i].b.enabled = restoreEnabled[i].enabled;
        for (int i = restoreActive.Count - 1; i >= 0; i--) if (restoreActive[i].go != null) restoreActive[i].go.SetActive(restoreActive[i].active);
        restorePose.Clear(); restoreEnabled.Clear(); restoreActive.Clear();
        foreach (var (ae, up, down, ou, od) in restoreExposure)
            if (ae != null) { ae.speedUp.value = up; ae.speedDown.value = down; ae.speedUp.overrideState = ou; ae.speedDown.overrideState = od; }
        restoreExposure.Clear();
        foreach (var (s, active, on, ov) in restoreRain) if (s != null) { s.active = active; s.enabled.value = on; s.enabled.overrideState = ov; }
        restoreRain.Clear();
        foreach (var (p, v, ov, _) in rainLevels) if (p != null) { p.value = v; p.overrideState = ov; }
        rainLevels.Clear();
        if (rainGate)
        {
            rainGate = false;
            VRRain.FilmOn = false; VRRain.KeepDrips = savedKeepDrips; VRRain.ScreenRain = savedScreenRain;
            VRRain.FilmDry = false; VRRain.FilmLens = true;
        }
        if (filmRain != null)
        {
            filmRain.Shutdown();
            filmRain = null;
            VRRain.Opacity = savedOpacity;
            VRRain.StreakWidth = savedWidth; VRRain.StreakLength = savedLength; VRRain.ClearRadius = savedClear;
            VRRain.Density = 1f; VRRain.Wet = 1f;
            VRRain.Anchor = null; VRRain.Glow = 0f; VRRain.Lattice = 0f; VRRain.FilmClock = double.NaN; VRRain.Shatter = 0f; VRRain.LatticeHold = false; VRRain.LatticeThick = 1f;
            VRRain.LatticeWidth = savedLatticeWidth;
        }
        foreach (var (cg, v, ov) in restoreGrading) if (cg != null) { cg.postExposure.value = v; cg.postExposure.overrideState = ov; }
        restoreGrading.Clear();
        exposureEv = null; exposureFrame = false;
        if (restoreCull != null && cam != null) cam.layerCullDistances = restoreCull;
        foreach (var cv in hiddenCanvases) if (cv != null) cv.enabled = true;
        hiddenCanvases.Clear();
        restoreCull = null;
        if (sidecar.Length > 0)
        {
            var s = sidecar.ToString().TrimEnd('\n', ',') + "\n]}\n";
            File.WriteAllText(Path.Combine(outDir, "camera.json"), s);
        }
        if (png != null)
        {
            png.Dispose();                                   // every queued frame on disk before "stopped" is logged
            var (encMs, written, mb) = png.Stats();
            Plugin.L.LogInfo($"film: {written} PNGs, {encMs:F0} ms each on {png.Threads} threads, {mb:F1} MB" + (png.Error != null ? " · ERROR " + png.Error : ""));
            png = null;
        }
        if (grab != null) { UnityEngine.Object.Destroy(grab); grab = null; }
        accSum = null;
        Plugin.L.LogInfo($"film: stopped ({why}) after {Math.Max(0, frame + 1)} frames");
    }

    static Camera FindCamera(string spec)
    {
        if (spec == "main") return Global.Instance != null ? Global.Instance.mainCamera : Camera.main;
        var t = FindAny(spec == "menu" ? "Global/UI/Menu/MenuCamera" : spec);
        return t != null ? t.GetComponent<Camera>() : null;
    }

    // A path from a root object, inactive objects included (GameObject.Find skips them).
    internal static Transform FindAny(string path)
    {
        int slash = path.IndexOf('/');
        var root = GameObject.Find(slash < 0 ? path : path.Substring(0, slash));
        if (root == null) return null;
        return slash < 0 ? root.transform : root.transform.Find(path.Substring(slash + 1));
    }

    // Switch a game object and its parents on, remembering what to switch back off.
    static void Activate(GameObject go)
    {
        for (var t = go.transform; t != null; t = t.parent)
            if (!t.gameObject.activeSelf) { restoreActive.Add((t.gameObject, false)); t.gameObject.SetActive(true); }
    }

    static void SetEnabled(Behaviour b, bool on)
    {
        if (b.enabled == on) return;
        restoreEnabled.Add((b, b.enabled));
        b.enabled = on;
    }

    static bool UiOnly(Camera c)
    {
        int ui = LayerMask.NameToLayer("UI");
        return ui >= 0 && (c.cullingMask & (1 << ui)) != 0 && (c.cullingMask & ~(1 << ui)) == 0;
    }

    static Vector3 RigPoint(Vector3 p) => rigTo + rigRot * (p - rigPivot);

    /// <summary>The rig transform of the running job (identity without one): title-space → world.</summary>
    internal static Vector3 RigToWorld(Vector3 p) => RigPoint(p);

    // Before culling the first camera each frame (after every script's LateUpdate): pose the filmed camera.
    public static void BeforeRender()
    {
        if (!Active || cam == null || poseFrame == Time.frameCount) return;
        poseFrame = Time.frameCount;
        double t = Math.Max(0, tick) / (double)(fps * samples);
        filmTime = t;
        Pose(t, out var p, out var r, out float fov);
        cam.transform.SetPositionAndRotation(p, r);
        cam.fieldOfView = fov;
        if (t < uiFrom) HideUi(); else ShowUi();
        // the game switches cameras back on as the player state updates (the cockpit's): keep only ours
        if (exclusive)
            foreach (var c in Camera.allCameras)
                if (c != cam && c.targetTexture == null && !UiOnly(c) && !FilmCockpit.Owns(c)) SetEnabled(c, false);
        if (wantMask != int.MinValue) cam.cullingMask = wantMask;   // the game rewrites the main camera's mask (cockpit view)
        if (offRT != null && cam.targetTexture != offRT) cam.targetTexture = offRT;
        long p0 = Stopwatch.GetTimestamp();
        if (hideCanvasNames.Length > 0 && (tick % 10 == 0 || tick < 0))
            HideCanvases(tick % 150 == 0);
        long p1 = Stopwatch.GetTimestamp();
        FilmStage.Update(t, cam);
        long p2 = Stopwatch.GetTimestamp();
        FilmTraffic.Eye = cam.transform.position;
        FilmTraffic.Update(t + clock);
        long p3 = Stopwatch.GetTimestamp();
        FilmFx.Update(t);
        long p4 = Stopwatch.GetTimestamp();
        if (tick >= 0) { tPose += p4 - p0; tCanvas += p1 - p0; tTraffic += p3 - p2; tFx += p4 - p3; }
        if (restoreRain.Count > 0)
        {
            bool wet = t >= rainFrom;
            VRRain.FilmDry = !wet;
            foreach (var (s, _, _, _) in restoreRain) if (s != null) { s.active = wet; s.enabled.overrideState = true; s.enabled.value = wet; }
            // never exactly 0 once wet: a zero-intensity first frame of the screen rain is a suspect for the all-white frames (11-08)
            float k = wet ? Mathf.Clamp(At(rainRamp, (float)t, 1f), 0.05f, 1f) : 0f;
            if (rainRamp != null || !rainLens)
                foreach (var (lv, v, _, lens) in rainLevels)
                    if (lv != null) { lv.overrideState = true; lv.value = lens && !rainLens ? 0f : v * (rainRamp != null ? k : 1f); }
            if (filmRain != null)
            {
                float glow = Mathf.Max(0f, At(rainGlow, (float)t, 0f)), lat = Mathf.Max(0f, At(rainLattice, (float)t, 0f));   // >1: a flash
                VRRain.Glow = glow; VRRain.Lattice = lat;
                VRRain.Shatter = Mathf.Clamp01(At(rainShatter, (float)t, 0f));
                VRRain.LatticeHold = t >= rainHoldFrom;
                VRRain.LatticeThick = Mathf.Max(0.05f, At(rainThick, (float)t, 1f));
                VRRain.Density = glow > 0f ? 1f : k; VRRain.Wet = k; VRRain.Opacity = rainOpacity;
                VRRain.Anchor = rainAnchor.HasValue && t < rainAnchorUntil ? rainAnchor : null;
                VRRain.FilmClock = t + clock;
                filmRain.BeforeRender(cam);
            }
        }
        if (exposureEv != null && !exposureFrame)
        {
            float ev = At(exposureEv, (float)t, 0f);
            foreach (var (cg, v, _) in restoreGrading) if (cg != null) { cg.postExposure.overrideState = true; cg.postExposure.value = v + ev; }
        }
        FilmCockpit.Update(t, cam);
    }

    static void Level(FloatParameter p, bool lens)
    {
        if (p != null) rainLevels.Add((p, p.value, p.overrideState, lens));
    }

    // A keyed curve: [[t, v], …] (or a single number) → sorted keys; null when absent.
    static float[][] Curve(JsonNode n)
    {
        if (n == null) return null;
        if (n is JsonValue) return new[] { new[] { 0f, (float)n } };
        var list = new List<float[]>();
        foreach (var k in n.AsArray()) { var a = k.AsArray(); list.Add(new[] { (float)a[0], (float)a[1] }); }
        list.Sort((x, y) => x[0].CompareTo(y[0]));
        return list.Count > 0 ? list.ToArray() : null;
    }

    static float At(float[][] c, float t, float dflt)
    {
        if (c == null) return dflt;
        if (t <= c[0][0]) return c[0][1];
        if (t >= c[^1][0]) return c[^1][1];
        int s = 0;
        while (s < c.Length - 2 && t > c[s + 1][0]) s++;
        float x = (t - c[s][0]) / Math.Max(1e-6f, c[s + 1][0] - c[s][0]);
        return c[s][1] + (c[s + 1][1] - c[s][1]) * x;
    }

    static void HideCanvases(bool survey)
    {
        var camPos = cam.transform.position;
        Dictionary<string, int> found = null;
        foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
        {
            if (cv == null || !cv.enabled) continue;
            string n = cv.gameObject.name;
            if (Matches(n)) { cv.enabled = false; hiddenCanvases.Add(cv); continue; }
            if (!survey || cv.renderMode != RenderMode.WorldSpace || seenCanvases.Contains(n)) continue;
            if ((cv.transform.position - camPos).sqrMagnitude > 250f * 250f) continue;
            found ??= new Dictionary<string, int>();
            found[n] = found.TryGetValue(n, out int k) ? k + 1 : 1;
        }
        if (found == null) return;
        foreach (var n in found.Keys) seenCanvases.Add(n);
        Plugin.L.LogInfo("film: world-space canvases near the camera (kept): " + string.Join(", ", found.Select(kv => $"{kv.Key}×{kv.Value}")));
    }

    static bool Matches(string n)
    {
        foreach (var h in hideCanvasNames)
            if (h.EndsWith("*") ? n.StartsWith(h.Substring(0, h.Length - 1), StringComparison.Ordinal) : n == h) return true;
        return false;
    }

    public static void EndOfFrame()
    {
        if (!Active) return;
        if (cam == null) { Stop("camera destroyed"); return; }
        long now = Stopwatch.GetTimestamp();
        if (tick > 0 && lastEnd != 0) tGame += now - lastEnd;
        if (tick >= 0)
        {
            int f = tick / samples, s = tick % samples;
            bool last = s == samples - 1;
            if (offRT == null && (Screen.width != width || Screen.height != height) && !sizeWarned)
            {
                sizeWarned = true;
                Plugin.L.LogWarning($"film: window is {Screen.width}x{Screen.height}, not {width}x{height}");
            }
            int w = offRT != null ? width : Math.Min(width, Screen.width), h = offRT != null ? height : Math.Min(height, Screen.height);
            if (grab == null || grab.width != w || grab.height != h)
            {
                if (grab != null) UnityEngine.Object.Destroy(grab);
                grab = new Texture2D(w, h, TextureFormat.RGB24, false);
                png?.Dispose();
                png = new FilmPng(w, h, Math.Clamp(Environment.ProcessorCount / 3, 2, 6));
                accSum = null;
            }
            if (s < exposed)
            {
                long t0 = Stopwatch.GetTimestamp();
                var prevActive = RenderTexture.active;
                if (offRT != null) RenderTexture.active = offRT;
                grab.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);    // the back buffer (end of frame: no render target bound), or offRT
                if (offRT != null) RenderTexture.active = prevActive;
                IntPtr src = grab.GetWritableImageData(0);              // its CPU copy, bottom row first, tightly packed RGB
                long t1 = Stopwatch.GetTimestamp();
                tRead += t1 - t0;
                string path = Path.Combine(outDir, $"color_{f:D5}.png");
                float gain = exposureFrame ? MathF.Pow(2f, At(exposureEv, f / (float)fps, 0f)) : 1f;
                if (exposed == 1) { var o = png.Rent(); CopyFlipped(src, o, w, h); if (gain != 1f) Gain(o, gain); Enqueue(path, o); }
                else
                {
                    Accumulate(src, w * h * 3, s == 0);
                    if (s == exposed - 1) { var o = png.Rent(); Average(o, w, h, gain); Enqueue(path, o); }
                }
                tBlur += Stopwatch.GetTimestamp() - t1;
            }
            if (last)
            {
                frame = f;
                double t = f / (double)fps;
                if (depthShader != null) SaveDepth(Path.Combine(outDir, $"depth_{f:D5}.png"));
                if (t >= uiFrom) SaveUi(Path.Combine(outDir, $"ui_{f:D5}.png"));
                var tr = cam.transform;
                sidecar.Append("{\"i\":").Append(f).Append(",\"t\":").Append(t.ToString("R"))
                    .Append(",\"position\":").Append(Arr(tr.position)).Append(",\"rotation\":").Append(Arr(tr.rotation))
                    .Append(",\"fov\":").Append(cam.fieldOfView.ToString("R")).Append(",\"near\":").Append(cam.nearClipPlane.ToString("R"))
                    .Append(",\"far\":").Append(cam.farClipPlane.ToString("R")).Append(",\"aspect\":").Append(cam.aspect.ToString("R")).Append("},\n");
                winFrames++;
                if (f % 30 == 0) LogTiming(f);
            }
        }
        tick++;
        if (tick >= totalFrames * samples) Stop("done");
        lastEnd = Stopwatch.GetTimestamp();
    }

    static void Enqueue(string path, byte[] rgb)
    {
        long q0 = Stopwatch.GetTimestamp();
        png.Enqueue(path, rgb);
        long dq = Stopwatch.GetTimestamp() - q0;
        tQueue += dq;
        tBlur -= dq;
    }

    static double Ms(long ticks, int n) => ticks * 1000.0 / Stopwatch.Frequency / Math.Max(1, n);

    static void LogTiming(int f)
    {
        long now = Stopwatch.GetTimestamp();
        int n = winFrames;
        var (encMs, written, mb) = png != null ? png.Stats() : (0, 0, 0);
        string line = $"film: frame {f}/{totalFrames}";
        if (winStart != 0 && n > 0)
            line += $" · {Ms(now - winStart, n):F0} ms/frame: game {Ms(tGame - tPose, n):F0} (update+render, {samples}×), ours {Ms(tPose, n):F0}, " +
                    $"read-back {Ms(tRead, n):F0}, blur {Ms(tBlur, n):F0}, waiting for encoders {Ms(tQueue, n):F0} · png {encMs:F0} ms on {png?.Threads} threads, {mb:F1} MB" +
                    $" · ours: actor {Ms(FilmStage.tActor, n):F0}, exit {Ms(FilmStage.tExit, n):F0}, set {Ms(FilmStage.tTiles, n):F0}, " +
                    $"props {Ms(FilmStage.tPeople, n):F0}, traffic {Ms(tTraffic, n):F0}, fx {Ms(tFx, n):F0}, canvases {Ms(tCanvas, n):F0}";
        if (FilmFx.Active) line += " · fx " + FilmFx.Status;
        if (png?.Error != null) line += " · PNG ERROR " + png.Error;
        Plugin.L.LogInfo(line);
        winStart = now; winFrames = 0; tGame = tPose = tRead = tBlur = tQueue = tCanvas = tTraffic = tFx = 0;
        FilmStage.tActor = FilmStage.tExit = FilmStage.tTiles = FilmStage.tPeople = 0;
    }

    // Motion blur: sub-frames summed in linear light (the capture is sRGB-encoded 8-bit; 16-bit linear lookup), saved as
    // their mean (lookup back to sRGB), rows flipped top-down for PNG.
    static ushort[] MakeLin16()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++) t[i] = (ushort)Math.Round(Math.Pow(i / 255.0, 2.2) * 65535.0);
        return t;
    }

    static byte[] MakeToSrgb()
    {
        var t = new byte[65536];
        for (int i = 0; i < 65536; i++) t[i] = (byte)Math.Min(255, Math.Round(Math.Pow(i / 65535.0, 1 / 2.2) * 255.0));
        return t;
    }

    const int Bands = 16;

    static unsafe void Accumulate(IntPtr src, int n, bool first)
    {
        if (accSum == null || accSum.Length != n) { accSum = new uint[n]; first = true; }
        var a = accSum;
        var lut = Lin16;
        Parallel.For(0, Bands, b =>
        {
            byte* p = (byte*)src;
            int i0 = (int)((long)n * b / Bands), i1 = (int)((long)n * (b + 1) / Bands);
            if (first) for (int i = i0; i < i1; i++) a[i] = lut[p[i]];
            else for (int i = i0; i < i1; i++) a[i] += lut[p[i]];
        });
    }

    // exposure "mode": "frame": one sRGB → linear × gain → sRGB lookup for the frame
    static void Gain(byte[] o, float gain)
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = ToSrgb[(int)Math.Min(65535.0, Math.Round(Lin16[i] * (double)gain))];
        Parallel.For(0, Bands, b =>
        {
            int i0 = (int)((long)o.Length * b / Bands), i1 = (int)((long)o.Length * (b + 1) / Bands);
            for (int i = i0; i < i1; i++) o[i] = lut[o[i]];
        });
    }

    static void Average(byte[] o, int w, int h, float gain = 1f)
    {
        var a = accSum;
        var lut = ToSrgb;
        ulong mul = (ulong)Math.Round((double)(1UL << 32) / exposed * gain);
        int stride = w * 3;
        Parallel.For(0, Bands, b =>
        {
            int y0 = h * b / Bands, y1 = h * (b + 1) / Bands;
            for (int y = y0; y < y1; y++)
            {
                int si = (h - 1 - y) * stride, di = y * stride;
                for (int i = 0; i < stride; i++) o[di + i] = lut[Math.Min(65535u, (uint)((a[si + i] * mul + (1UL << 31)) >> 32))];
            }
        });
    }

    static unsafe void CopyFlipped(IntPtr src, byte[] o, int w, int h)
    {
        int stride = w * 3;
        fixed (byte* d = o)
        {
            byte* s = (byte*)src;
            for (int y = 0; y < h; y++) Buffer.MemoryCopy(s + (long)(h - 1 - y) * stride, d + (long)y * stride, stride, stride);
        }
    }

    static void SaveDepth(string path)
    {
        var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        var go = new GameObject("CloudpunkVR_FilmDepth");
        try
        {
            var d = go.AddComponent<Camera>();
            d.CopyFrom(cam);
            d.enabled = false;
            d.stereoTargetEye = StereoTargetEyeMask.None;
            d.targetTexture = rt;
            d.clearFlags = CameraClearFlags.SolidColor;
            d.backgroundColor = new Color(0.5f, 0.5f, 1f, 1f); // decodes to depth 1 (far) facing the camera
            d.allowHDR = false;
            d.allowMSAA = false;
            d.RenderWithShader(depthShader, "RenderType");
            Read(rt, path, false);
        }
        finally
        {
            UnityEngine.Object.Destroy(go);
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // The UI cameras (everything that renders only the UI layer) into a transparent target.
    static void SaveUi(string path)
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        try
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0, 0, 0, 0));
            RenderTexture.active = prev;
            foreach (var c in Camera.allCameras.OrderBy(c => c.depth))
            {
                if (c == cam || uiLayer < 0 || (c.cullingMask & ~(1 << uiLayer)) != 0 || (c.cullingMask & (1 << uiLayer)) == 0) continue;
                var target = c.targetTexture;
                var flags = c.clearFlags;
                c.targetTexture = rt;
                c.clearFlags = CameraClearFlags.Nothing;
                c.Render();
                c.targetTexture = target;
                c.clearFlags = flags;
            }
            Read(rt, path, true);
        }
        finally { RenderTexture.ReleaseTemporary(rt); }
    }

    static void Read(RenderTexture rt, string path, bool alpha)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var t2d = new Texture2D(rt.width, rt.height, alpha ? TextureFormat.RGBA32 : TextureFormat.RGBA32, false);
        t2d.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        t2d.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(t2d));
        UnityEngine.Object.Destroy(t2d);
    }

    static void HideUi()
    {
        if (hiddenUi.Count > 0) return;
        int uiLayer = LayerMask.NameToLayer("UI");
        foreach (var c in Camera.allCameras)
            if (c != cam && uiLayer >= 0 && (c.cullingMask & (1 << uiLayer)) != 0 && (c.cullingMask & ~(1 << uiLayer)) == 0) { c.enabled = false; hiddenUi.Add(c); }
        foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
            if (cv.isRootCanvas && cv.renderMode == RenderMode.ScreenSpaceOverlay && cv.enabled) { cv.enabled = false; hiddenUi.Add(cv); }
    }

    static void ShowUi()
    {
        foreach (var b in hiddenUi) if (b != null) b.enabled = true;
        hiddenUi.Clear();
    }

    static void Pose(double t, out Vector3 p, out Quaternion r, out float fov)
    {
        int n = keys.Count;
        if (t <= keys[0].t) { Set(keys[0], out p, out r, out fov); return; }
        if (t >= keys[n - 1].t) { Set(keys[n - 1], out p, out r, out fov); return; }
        int s = 0;
        while (s < n - 2 && t > keys[s + 1].t) s++;
        p = H(k => k.p, s, t);
        var f = H(k => k.fwd, s, t);
        var u = H(k => k.up, s, t);
        r = Quaternion.LookRotation(f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.forward, u.sqrMagnitude > 1e-8f ? u.normalized : Vector3.up);
        fov = H(k => new Vector3(k.fov, 0, 0), s, t).x;
    }

    static void Set(Key k, out Vector3 p, out Quaternion r, out float fov) { p = k.p; r = Quaternion.LookRotation(k.fwd, k.up); fov = k.fov; }

    // Cubic Hermite with Catmull-Rom tangents over time; zero velocity at the first and last key (same as VRStage).
    static Vector3 H(Func<Key, Vector3> v, int s, double time)
    {
        Vector3 T(int i)
        {
            if (i == 0 || i == keys.Count - 1) return Vector3.zero;
            return (v(keys[i + 1]) - v(keys[i - 1])) / (float)Math.Max(1e-6, keys[i + 1].t - keys[i - 1].t);
        }
        float h = (float)Math.Max(1e-6, keys[s + 1].t - keys[s].t);
        float x = Mathf.Clamp01((float)((time - keys[s].t) / h)), x2 = x * x, x3 = x2 * x;
        return (2 * x3 - 3 * x2 + 1) * v(keys[s]) + (x3 - 2 * x2 + x) * h * T(s) + (-2 * x3 + 3 * x2) * v(keys[s + 1]) + (x3 - x2) * h * T(s + 1);
    }

    static Vector3 V3(JsonArray a) => new((float)a[0], (float)a[1], (float)a[2]);
    static Quaternion Rot(JsonArray a) => a.Count >= 4 ? new Quaternion((float)a[0], (float)a[1], (float)a[2], (float)a[3]) : Quaternion.Euler((float)a[0], (float)a[1], (float)a[2]);
    static string Arr(Vector3 v) => $"[{v.x:R},{v.y:R},{v.z:R}]";
    static string Arr(Quaternion q) => $"[{q.x:R},{q.y:R},{q.z:R},{q.w:R}]";

    // campose [name]: every enabled camera's pose and lens, as JSON (pick the menu camera's for the last key).
    public static void DumpCameras(string name)
    {
        var sb = new System.Text.StringBuilder("{\"scene\":\"").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name).Append("\",\"cameras\":[\n");
        foreach (var c in Camera.allCameras)
        {
            var tr = c.transform;
            sb.Append("{\"path\":\"").Append(Diag.PathOf(tr)).Append("\",\"position\":").Append(Arr(tr.position))
              .Append(",\"rotation\":").Append(Arr(tr.rotation)).Append(",\"euler\":").Append(Arr(tr.eulerAngles))
              .Append(",\"fov\":").Append(c.fieldOfView.ToString("R")).Append(",\"near\":").Append(c.nearClipPlane.ToString("R"))
              .Append(",\"far\":").Append(c.farClipPlane.ToString("R")).Append(",\"depth\":").Append(c.depth.ToString("R"))
              .Append(",\"cullingMask\":").Append(c.cullingMask).Append(",\"target\":").Append(c.targetTexture != null ? "\"rt\"" : "null").Append("},\n");
        }
        var s = sb.ToString().TrimEnd('\n', ',') + "\n]}\n";
        string dir = Path.Combine(Plugin.Dir, "film");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, (name ?? "campose") + ".json");
        File.WriteAllText(path, s);
        Plugin.L.LogInfo("campose: " + path + "\n" + s);
    }
}
