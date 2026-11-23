using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CloudpunkVR;

// Render-cost trimming that applies with or without VR. Auxiliary cameras (minimap, rear mirror, windshield rain
// masks, parking display) render every frame at full scene cost although their output barely changes; they are
// skipped on most frames by switching them off just before rendering and back on at end of frame, so the game's
// own logic still sees them enabled.
internal sealed class Perf
{
    sealed class Rule
    {
        public string Key;
        public System.Func<string, bool> Match;
        public int Every;
    }

    readonly List<Rule> rules = new();
    readonly Dictionary<int, (Camera cam, Rule rule)> tracked = new();
    readonly List<Camera> skipped = new();
    readonly Il2CppReferenceArray<Camera> all = new(64);
    float nextScan;

    public readonly HashSet<string> Disabled = new();

    // Dev (every <key> <n>): change a throttle rule's interval while running.
    public void SetEvery(string key, int every)
    {
        foreach (var r in rules) if (r.Key == key) r.Every = every;
        foreach (var id in new List<int>(tracked.Keys)) if (tracked[id].rule.Key == key && every <= 1) tracked.Remove(id);
        nextScan = 0;
        Plugin.L.LogInfo($"perf: {key} every {every}");
    }

    public Perf()
    {
        var c = Plugin.Cfg;
        rules.Add(new Rule { Key = "minimap", Match = p => p.EndsWith("/MiniMapCamera"), Every = c.MinimapEvery.Value });
        rules.Add(new Rule { Key = "mirror", Match = p => p.EndsWith("/Mirror_Camera"), Every = c.MirrorEvery.Value });
        rules.Add(new Rule { Key = "rain", Match = p => p.Contains("Camera_Rain_Effect"), Every = c.RainMaskEvery.Value });
        rules.Add(new Rule { Key = "parking", Match = p => p.EndsWith("/Parking_Camera"), Every = c.ParkingEvery.Value });
    }

    public void Scan()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 2f;
        int n = Camera.GetAllCameras(all);
        for (int i = 0; i < n; i++)
        {
            var cam = all[i];
            int id = cam.GetInstanceID();
            if (tracked.ContainsKey(id) || cam.targetTexture == null) continue;
            var path = Diag.PathOf(cam.transform);
            foreach (var r in rules)
                if (r.Every > 1 && r.Match(path))
                {
                    tracked[id] = (cam, r);
                    Plugin.L.LogInfo($"perf: {path} renders every {r.Every} frames");
                    break;
                }
        }
    }

    public void BeforeRender()
    {
        int frame = Time.frameCount;
        foreach (var (cam, rule) in tracked.Values)
        {
            if (Disabled.Contains(rule.Key) || cam == null || !cam.enabled) continue;
            if (((frame + cam.GetInstanceID()) % rule.Every + rule.Every) % rule.Every == 0) continue;
            cam.enabled = false;
            skipped.Add(cam);
        }
    }

    static float gameShadowDistance = -1;
    static int gameShadowCascades, gameQualityLevel = -1;

    // Shadow maps are rendered for each eye in multi-pass VR, so the shadow range costs twice. Called again
    // periodically: picking another quality level in the game's options resets the shadow settings.
    public static void ApplyVRShadows(int level)
    {
        int quality = QualitySettings.GetQualityLevel();
        if (quality != gameQualityLevel)
        {
            gameQualityLevel = quality;
            gameShadowDistance = QualitySettings.shadowDistance;
            gameShadowCascades = QualitySettings.shadowCascades;
        }
        (float dist, int cascades) = level switch { 1 => (120f, 2), 2 => (60f, 2), _ => (gameShadowDistance, gameShadowCascades) };
        dist = Mathf.Min(dist, gameShadowDistance);
        cascades = Mathf.Min(cascades, gameShadowCascades);
        if (QualitySettings.shadowDistance == dist && QualitySettings.shadowCascades == cascades) return;
        QualitySettings.shadowDistance = dist;
        QualitySettings.shadowCascades = cascades;
        Plugin.L.LogInfo($"perf: VR shadows level {level} -> {dist} m, {cascades} cascades (quality level {quality})");
    }

    // In VR the head decides where the camera is: Cinemachine's collision extension would only shove the base pose
    // around (a jolt in the headset) and the third-person rigs are never shown, yet both raycast every frame, and each
    // raycast first syncs every moved collider. Called periodically to catch rigs that activate later.
    public static bool TrimRig = true;   // dev A/B switch ("perf rig 0|1")
    static readonly List<(Cinemachine.CinemachineVirtualCameraBase vcam, Cinemachine.CinemachineVirtualCameraBase.StandbyUpdateMode mode)> standby = new();
    static readonly List<Cinemachine.CinemachineCollider> colliders = new();

    public static void TrimVRCameraRig()
    {
        if (!TrimRig)
        {
            foreach (var (vcam, mode) in standby) if (vcam != null) vcam.m_StandbyUpdate = mode;
            foreach (var c in colliders) if (c != null) c.enabled = true;
            standby.Clear();
            colliders.Clear();
            return;
        }
        foreach (var vcam in Object.FindObjectsOfType<Cinemachine.CinemachineVirtualCameraBase>())
        {
            var mode = vcam.m_StandbyUpdate;
            if (mode == Cinemachine.CinemachineVirtualCameraBase.StandbyUpdateMode.Never) continue;
            standby.Add((vcam, mode));
            vcam.m_StandbyUpdate = Cinemachine.CinemachineVirtualCameraBase.StandbyUpdateMode.Never;
        }
        foreach (var c in Object.FindObjectsOfType<Cinemachine.CinemachineCollider>())
        {
            if (!c.enabled) continue;
            c.enabled = false;
            colliders.Add(c);
            Plugin.L.LogInfo($"perf: camera collision off on {Diag.PathOf(c.transform)}");
        }
    }

    // The skybox camera renders the sky into a small screen-space texture (_GlobalFogSkybox, a quarter of the window)
    // that the fog samples. Left a stereo camera, VR renders it once per eye at full eye resolution: about 30% of the
    // GPU frame. The sky is at infinity and the plugin already gives it the left-eye projection, so render it once.
    // Traffic cars leave light trails (TrailRenderer, a second long, facing the camera). A trail facing the camera is
    // rebuilt for every camera that draws it, so in VR twice a frame, and the game adds a point every 10 cm: some 3000
    // points for the trails in view in Midtown. One point every 60 cm draws the same streak with a sixth of them.
    public const float TrailPointSpacing = 0.6f;
    public static void TuneTrails()
    {
        int n = 0;
        // all loaded ones, including the cars waiting in the traffic pool
        foreach (var tr in Resources.FindObjectsOfTypeAll<TrailRenderer>())
            if (tr.minVertexDistance < TrailPointSpacing) { tr.minVertexDistance = TrailPointSpacing; n++; }
        if (n > 0) Plugin.L.LogInfo($"perf: {n} trails add a point every {TrailPointSpacing} m");
    }

    public static bool MonoSky = true;   // dev A/B switch ("perf sky 0|1")
    public static void MonoSkyCamera()
    {
        foreach (var v in Object.FindObjectsOfType<ViewToGlobalTexture>())
        {
            var cam = v.skycam;
            if (cam == null) continue;
            var want = MonoSky ? StereoTargetEyeMask.None : StereoTargetEyeMask.Both;
            if (cam.stereoTargetEye == want) continue;
            cam.stereoTargetEye = want;
            Plugin.L.LogInfo($"perf: {Diag.PathOf(cam.transform)} renders {(MonoSky ? "once per frame" : "per eye")}");
        }
    }

    public void AfterRender()
    {
        foreach (var cam in skipped)
            if (cam != null) cam.enabled = true;
        skipped.Clear();
    }
}
