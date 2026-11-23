using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;

namespace CloudpunkVR;

// Single hidden MonoBehaviour that owns all VR behaviour. Lives on a DontDestroyOnLoad object created by BepInEx.
// Frame hooks: LateUpdate (aux camera throttling), the first Camera.onPreCull of a screen/HMD camera each frame
// (head pose; Application.onBeforeRender is stripped from this build), WaitForEndOfFrame (restore game poses).
public class VRHost : MonoBehaviour
{
    public VRHost(IntPtr ptr) : base(ptr) { }

    static readonly HashSet<string> ComfortEffects = new() { "DepthOfField", "MotionBlur", "ChromaticAberration", "LensDistortion", "Vignette", "Grain" };

    VirtualPad pad;
    DevCommands dev;
    Coo.CooHost coo;
    Perf perf;
    bool vr;
    VRCameras cams;
    VRUI ui;
    SteamVRInput input;
    VRControls controls;
    VROptions options;
    internal static VRGlows Glows;
    VRGlows glows;
    readonly VRFarView farView = new();
    readonly VRAltitude altitude = new();
    readonly VRRain rain = new();
    internal static VRBody Body;
    internal static VRUI UI;
    internal static RenderTexture UITexture;
    int lastMode = -1;
    bool wasLoading = true;
    float recenterAt = -1;
    float nextOptionsCheck;
    readonly HashSet<string> reported = new();
    string lastState = "";
    readonly List<float> scenePassAt = new();

    void RequestScenePass(float delay)
    {
        scenePassAt.Add(Time.unscaledTime + delay);
        scenePassAt.Sort();
    }
    float stateChangedAt = -1, nextComfortPass, nextStateCheck;
    int headFrame = -1, cooFrame = -1;

    public void Start()
    {
        Plugin.L.LogInfo($"XR enabled={XRSettings.enabled} device='{XRSettings.loadedDeviceName}' " +
                         $"supported=[{string.Join(",", XRSettings.supportedDevices)}] present={XRDevice.isPresent} model='{XRDevice.model}'");
        pad = new VirtualPad();
        perf = new Perf();
        vr = XRSettings.enabled && XRSettings.loadedDeviceName == "OpenVR";
        if (vr) Guard("vr start", StartVR);
        if (Plugin.Cfg.CooEnabled.Value) Guard("coo start", () => coo = new Coo.CooHost(pad));
        if (DevCommands.Enabled)
        {
            dev = new DevCommands(pad, perf);
            Application.runInBackground = true;
            Plugin.L.LogWarning("dev command channel enabled (-cloudpunkvr-dev)");
        }
        Camera.onPreCull = Camera.onPreCull + (Camera.CameraCallback)new Action<Camera>(OnPreCull);
        this.StartCoroutine(EndOfFrame());
    }

    void StartVR()
    {
        Application.runInBackground = true;
        XRDevice.SetTrackingSpaceType(TrackingSpaceType.Stationary);
        Resolution.Init();
        Resolution.Apply(Plugin.Cfg.ResolutionPercent.Value);
        cams = new VRCameras();
        ui = new VRUI();
        ui.Setup();
        UI = ui;
        UITexture = ui.Texture;
        input = new SteamVRInput();
        input.Init(Path.Combine(Plugin.Dir, "bindings", "actions.json"));
        controls = new VRControls(input, pad, cams);
        Patches.ApplyVR(Plugin.Harmony, input);
        options = new VROptions();
        VRControlsPage.Apply(Plugin.Harmony);
        VRPrompts.Apply(Plugin.Harmony);
        Perf.ApplyVRShadows(Plugin.Cfg.VRShadows.Value);
        VRPostFx.Apply(Plugin.Harmony);
        Plugin.Cfg.DisableComfortEffects.SettingChanged += (_, _) => RequestScenePass(0f);
        Plugin.Cfg.WindshieldBlur.SettingChanged += (_, _) => RequestScenePass(0f);
        RequestScenePass(1f);
        VRStereoFx.Apply();
        VRFarView.Patch(Plugin.Harmony);
        VRRain.Patch(Plugin.Harmony);
        VRHx.Patch(Plugin.Harmony);
        glows = new VRGlows();
        glows.Setup();
        VRGlows.Patch(Plugin.Harmony);
        Glows = glows;
        Body = new VRBody(cams);
        VRBody.Patch(Plugin.Harmony);
        Plugin.L.LogInfo($"VR mode on: {XRDevice.model} {XRDevice.refreshRate}Hz {XRSettings.stereoRenderingMode}, " +
                         $"{SystemInfo.graphicsDeviceVersion}, {GfxThreadingMode()}");
    }

    // How the engine threads rendering, read from UnityPlayer.dll's own setting (the address is this engine build's,
    // 2018.4.29f1; there is no scripting API for it in 2018). "legacy graphics jobs" is what boot.config's
    // gfx-enable-gfx-jobs gives on Direct3D 11.
    static string GfxThreadingMode()
    {
        if (Application.unityVersion != "2018.4.29f1") return "threading mode unknown";
        foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
        {
            if (!string.Equals(m.ModuleName, "UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (m.ModuleMemorySize < 0x15c34cc) break;
            int mode = System.Runtime.InteropServices.Marshal.ReadInt32(m.BaseAddress + 0x15c34c8);
            return mode switch
            {
                0 => "rendering on the main thread", 1 => "rendering single-threaded", 2 => "render thread",
                3 => "render thread + legacy graphics jobs", 4 => "native graphics jobs", 5 => "native graphics jobs, no render thread",
                _ => "threading mode " + mode,
            };
        }
        return "threading mode unknown";
    }

    public void Update()
    {
        using var __pt = PluginTiming.Measure("host.update");
        if (Plugin.Cfg.DiagnosticsKeys.Value)
        {
            if (Input.GetKeyDown(KeyCode.F9)) Diag.DumpAll("F9");
            if (Input.GetKeyDown(KeyCode.F11)) Diag.DumpXrefs();
        }
        PluginTiming.Tick();
        Guard("dev", () => dev?.Update());
        Guard("perf scan", perf.Scan);

        if (vr)
        {
            // The compositor paces VR frames; the game's own 60 fps cap would halve the headset rate.
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
            Guard("controls", () => { controls.Update(); controls.LevelFirstPersonPitch(); });
            Guard("frame timing", VRFrameTiming.Update);
            if (coo != null) Guard("coo", () => { coo.Update(); coo.AfterControls(); });
            Guard("altitude", () => altitude.Update(input));
            Guard("body", Body.Update);
            Guard("recenter", AutoRecenter);
            Guard("ui", () =>
            {
                var g = Global.Instance;
                bool menu = g != null && (g.IsInMenu() || g.HasBigWindowOpen() || g.IsInDialog(false));
                ui.Update(cams, menu, onFoot: g != null && !Global.IsInVehicle && !SceneLoader.IsLoading());
            });
            if (Time.unscaledTime >= nextOptionsCheck)
            {
                nextOptionsCheck = Time.unscaledTime + 0.25f;
                Guard("options", options.Update);
                Guard("controls page", VRControlsPage.Update);
            }
            if (Time.unscaledTime >= nextComfortPass)
            {
                nextComfortPass = Time.unscaledTime + 2f;
                Guard("shadows", () => Perf.ApplyVRShadows(Plugin.Cfg.VRShadows.Value));
                Guard("blends", CutCameraBlends);
            }
            // These search the whole scene (6–14 ms each in Midtown: run every 2 s they dropped a frame every 2 s), so
            // they run when what they adjust can have appeared: after a change of scene, vehicle, view or menu (1 s and
            // 5 s after it) and when one of their options changes.
            if (scenePassAt.Count > 0 && Time.unscaledTime >= scenePassAt[0])
            {
                scenePassAt.RemoveAt(0);
                if (Plugin.Cfg.DisableComfortEffects.Value) Guard("comfort", DisableComfortEffects);
                else Guard("comfort restore", RestoreComfortEffects);
                Guard("camera rig", Perf.TrimVRCameraRig);
                Guard("sky camera", Perf.MonoSkyCamera);
                Guard("vr fx", VRPostFx.Refresh);
                Guard("trails", Perf.TuneTrails);
            }
        }

        if (!vr && coo != null)
        {
            Guard("coo", () => { coo.Update(); coo.AfterControls(); });
            Guard("altitude", () => altitude.Update(null));
        }

        // The state line costs a handful of IL2CPP calls and a string; four checks a second are plenty for the log.
        if (Time.unscaledTime >= nextStateCheck)
        {
            nextStateCheck = Time.unscaledTime + 0.25f;
            var state = Diag.StateLine();
            if (state != lastState)
            {
                Plugin.L.LogInfo("state: " + state);
                lastState = state;
                stateChangedAt = Time.unscaledTime;
                RequestScenePass(1f);
                RequestScenePass(5f);
            }
        }
        if (stateChangedAt > 0 && Time.unscaledTime - stateChangedAt > 3f)
        {
            stateChangedAt = -1;
            if (DevCommands.Enabled) Diag.DumpAll("state settled");
        }
    }

    public void LateUpdate() => Guard("perf", perf.BeforeRender);

    void OnPreCull(Camera cam)
    {
        using var __pt = PluginTiming.Measure("cb:precull");
        if (Film.Active) Guard("film pose", Film.BeforeRender);
        if (!vr)
        {
            if (coo != null && cam.targetTexture == null && cooFrame != Time.frameCount) { cooFrame = Time.frameCount; Guard("coo render", () => coo.BeforeRender(cams)); }
            return;
        }
        Guard("precull", () =>
        {
            // Offscreen cameras can be rendered from scripts mid-frame; the render phase starts with the first
            // HMD camera or a camera riding on one (the skybox camera under Main Camera renders before it).
            if (headFrame != Time.frameCount && (cam.targetTexture == null || VRCameras.RidesOnCamera(cam)))
            {
                headFrame = Time.frameCount;
                bool cockpit = Global.Instance != null && Global.IsInVehicle && Global.IsInVehicleCockpit;
                cams.BeforeRender(cockpit);
                ui.BeforeRender(cams);
                var main = Global.Instance != null ? Global.Instance.mainCamera : null;
                Guard("glows", () => glows.BeforeRender(main));
                Guard("far view", () => farView.BeforeRender(main));
                Guard("rain", () => rain.BeforeRender(main));
                Guard("body pose", () => Body.BeforeRender(main));
                if (coo != null) Guard("coo render", () => coo.BeforeRender(cams));
            }
            cams.OnPreCull(cam);
        });
    }

    IEnumerator EndOfFrame()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            if (Film.Active) Guard("film capture", Film.EndOfFrame);
            if (coo != null && vr) Guard("spectator", () => coo.View.EndOfFrame(cams));
            if (coo != null) Guard("coo end", coo.EndOfFrame);
            if (DevCommands.PendingCapture != null)
            {
                var capture = DevCommands.PendingCapture;
                DevCommands.PendingCapture = null;
                Guard("pose capture", capture);
            }
            if (vr) Guard("body restore", Body.AfterRender);
            if (vr) Guard("restore", cams.AfterRender);
            Guard("far view restore", farView.AfterRender);
            Guard("perf restore", perf.AfterRender);
            if (dev != null) Guard("gpu timer", GpuTimer.EndOfFrame);
        }
    }

    // Recenter when gameplay starts after a loading screen and when switching between cockpit, chase view and on foot:
    // the moments the player settles into a new seat or stance. Menus and loading screens in between do not count.
    void AutoRecenter()
    {
        var g = Global.Instance;
        if (g == null) return;
        bool loading = SceneLoader.IsLoading();
        bool menu = g.IsInMenu() || g.HasBigWindowOpen() || g.IsInDialog(false);
        if (loading) { wasLoading = true; return; }
        int mode = menu ? 0 : Global.IsInVehicle ? (Global.IsInVehicleCockpit ? 1 : 2) : 3;
        if (mode == 0) return;
        float now = Time.unscaledTime;
        if (wasLoading || mode != lastMode)
        {
            if (Plugin.Cfg.AutoRecenter.Value) recenterAt = now + 0.6f;
            wasLoading = false;
            lastMode = mode;
        }
        if (recenterAt > 0 && now >= recenterAt)
        {
            recenterAt = -1;
            cams.Recenter();
            ui.Snap();
        }
    }

    // Cinemachine flies the camera between views over a second or two (car to on foot, chase to cockpit); in a
    // headset that is a forced camera move, so in VR views change with a cut.
    static void CutCameraBlends()
    {
        var brain = Global.Instance != null ? Global.Instance.cameraBrain : null;
        if (brain == null) return;
        if (brain.m_DefaultBlend.m_Style != Cinemachine.CinemachineBlendDefinition.Style.Cut || brain.m_CustomBlends != null)
        {
            brain.m_DefaultBlend = new Cinemachine.CinemachineBlendDefinition(Cinemachine.CinemachineBlendDefinition.Style.Cut, 0);
            brain.m_CustomBlends = null;
            Plugin.L.LogInfo("camera blends: cut");
        }
    }

    // What the comfort filter switched off, so turning the option off again gives the effects back.
    static readonly List<PostProcessEffectSettings> comfortSettings = new();
    static readonly List<Behaviour> comfortBehaviours = new();

    static void DisableComfortEffects()
    {
        foreach (var s in Resources.FindObjectsOfTypeAll<PostProcessEffectSettings>())
            if (s.active && ComfortEffects.Contains(s.GetIl2CppType().Name))
            {
                s.active = false;
                comfortSettings.Add(s);
                Plugin.L.LogInfo($"comfort: disabled {s.GetIl2CppType().Name} in {s.name}");
            }
        var main = Global.Instance != null ? Global.Instance.mainCamera : null;
        if (main == null) return;
        foreach (var b in main.GetComponents<Behaviour>())
            if (b.enabled && b.GetIl2CppType().Name.StartsWith("VideoGlitch"))
            {
                b.enabled = false;
                comfortBehaviours.Add(b);
            }
    }

    static void RestoreComfortEffects()
    {
        foreach (var s in comfortSettings) if (s != null) s.active = true;
        foreach (var b in comfortBehaviours) if (b != null) b.enabled = true;
        if (comfortSettings.Count + comfortBehaviours.Count > 0) Plugin.L.LogInfo("comfort: effects restored");
        comfortSettings.Clear();
        comfortBehaviours.Clear();
    }

    // Per-frame hooks must never throw into Unity; report each distinct failure once.
    void Guard(string what, Action a)
    {
        using var __pt = PluginTiming.Measure(what);
        try { a(); }
        catch (Exception e)
        {
            if (reported.Add(what + e.Message)) Plugin.L.LogError($"{what}: {e}");
        }
    }
}
