using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InControl;
using UnityEngine;

namespace CloudpunkVR;

// Dev/test channel: with -cloudpunkvr-dev the plugin polls BepInEx/plugins/CloudpunkVR/cmd.txt and runs its lines.
// Lets the game be driven (load save, switch cameras, hold pad buttons, screenshot) without touching the desktop.
internal sealed class DevCommands
{
    internal static readonly bool Enabled = Environment.GetCommandLineArgs()
        .Contains("-cloudpunkvr-dev", StringComparer.OrdinalIgnoreCase);

    public static bool WristTest;
    public static Vector2 CockpitLook;
    readonly string cmdPath = Path.Combine(Plugin.Dir, "cmd.txt");
    readonly string shotDir = Path.Combine(Plugin.Dir, "shots");
    readonly string profileDir = Path.Combine(Plugin.Dir, "profiles");
    string profileLabel = "run", gpuLabel = "run";
    readonly Queue<string> queue = new();
    readonly List<(InputControlType control, float until)> held = new();
    readonly Dictionary<string, List<MonoBehaviour>> switchedOff = new();
    readonly VirtualPad pad;
    readonly Perf perf;
    float nextPoll, waitUntil, waitForDeadline;
    string waitFor;
    bool uncapped;
    bool audible = Environment.GetCommandLineArgs().Contains("-cloudpunkvr-audible", StringComparer.OrdinalIgnoreCase);
    float nextMute;
    int cullSaved = int.MinValue;
    string burstName;
    float carLogUntil, nextCarLog, carLogEvery = 0.5f;
    int burstLeft, burstIndex;

    // bench <seconds> <label>: frame-time statistics, the first second is warm-up
    string benchLabel;
    float benchStart, benchUntil;
    TimeSpan benchCpuStart;
    System.Diagnostics.Stopwatch benchWall;
    int fastCoreFrames, focusedFrames;
    readonly List<float> frameMs = new();
    readonly List<float> gpuMs = new();

    public DevCommands(VirtualPad pad, Perf perf)
    {
        this.pad = pad;
        this.perf = perf;
        CpuSampler.CaptureMainThread();
    }

    void SaveProfile(string file, string report)
    {
        Plugin.L.LogInfo(report);
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(Path.Combine(profileDir, file), report);
    }

    void BenchFrame(float now)
    {
        if (now >= benchStart + 1f)
        {
            if (benchWall == null)
            {
                benchCpuStart = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                benchWall = System.Diagnostics.Stopwatch.StartNew();
            }
            frameMs.Add(Time.unscaledDeltaTime * 1000f);
            if (UnityEngine.XR.XRStats.TryGetGPUTimeLastFrame(out float gpuLast)) gpuMs.Add(gpuLast);
            if (CpuTopology.OnFastCore()) fastCoreFrames++;
            if (Application.isFocused) focusedFrames++;
        }
        if (now < benchUntil || benchWall == null) return;
        frameMs.Sort();
        float avg = frameMs.Count > 0 ? frameMs.Average() : 0;
        float P(float q) => frameMs.Count > 0 ? frameMs[Math.Min(frameMs.Count - 1, (int)(q * frameMs.Count))] : 0;
        string gpu = gpuMs.Count > 0 ? $" gpu={gpuMs.Average():F2}ms" : "";
        // Whole-process CPU use over the measured part: how many cores the game keeps busy.
        double cores = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - benchCpuStart).TotalMilliseconds / benchWall.Elapsed.TotalMilliseconds;
        benchWall = null;
        string placement = CpuTopology.Hybrid ? $" mainOnFastCore={100 * fastCoreFrames / Math.Max(1, frameMs.Count)}%" : "";
        Plugin.L.LogInfo($"BENCH {benchLabel}: frames={frameMs.Count} avg={avg:F2}ms ({1000 / Math.Max(avg, 0.01f):F1} fps) " +
                         $"p50={P(0.5f):F2} p95={P(0.95f):F2} p99={P(0.99f):F2}{gpu} cpu={cores:F2} cores{placement} " +
                         $"focused={100 * focusedFrames / Math.Max(1, frameMs.Count)}% | {Diag.StateLine()}");
        fastCoreFrames = focusedFrames = 0;
        frameMs.Clear();
        gpuMs.Clear();
        benchLabel = null;
    }

    public void Update()
    {
        float now = Time.unscaledTime;
        DevWindow.Update();
        // The game re-applies its own fullscreen resolution on load; keep test sessions in a small window.
        if ((Screen.fullScreen || Screen.width != 1280) && !Film.Active)
            Screen.SetResolution(1280, 720, false);
        // Test sessions are silent (the game's settings script sets the listener volume back, so every frame).
        if (!audible)
        {
            AudioListener.volume = 0;
            if (now >= nextMute)
            {
                nextMute = now + 2f;
                foreach (var s in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                    if (s.ignoreListenerVolume && !s.mute) s.mute = true;
            }
        }
        if (PadInfoFrames > 0)
        {
            PadInfoFrames--;
            var dev = InputManager.ActiveDevice;
            var es = UnityEngine.EventSystems.EventSystem.current;
            Plugin.L.LogInfo($"pad: f={Time.frameCount} {VRControls.DebugLine} ours.A={(pad.Device != null ? pad.Device.Action1.IsPressed : false)} active='{(dev != null ? dev.Name : "-")}' active.A={(dev != null && dev.Action1.IsPressed)} " +
                             $"devices={InputManager.Devices.Count} selected={(es != null && es.currentSelectedGameObject != null ? Diag.PathOf(es.currentSelectedGameObject.transform) : "-")}");
        }
        if (CarInfoFrames > 0 && now >= carInfoNext)
        {
            CarInfoFrames--;
            carInfoNext = now + 0.25f;
            var gc = Global.Instance;
            var car = gc != null ? gc.playerCar : null;
            if (car != null)
                Plugin.L.LogInfo($"car: y={car.transform.position.y:F2} layer=[{car.layerMinY:F1},{car.layerMaxY:F1}] floor={car.currentFloorLevel:F1} stable={car.stableHeight:F1} " +
                                 $"vy={car.verticalSpeed:F2} rbVy={(car.rBody != null ? car.rBody.velocity.y : 0):F2} rising={car.isRising} sinking={car.isSinking} usedAscend={car.usedAscend} " +
                                 $"climbIn={VRAltitude.LastClimb:F2} warn={VRAltitude.Describe()}");
        }
        if (now >= nextPoll)
        {
            nextPoll = now + 0.25f;
            if (File.Exists(cmdPath))
            {
                foreach (var line in File.ReadAllLines(cmdPath))
                    if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#")) queue.Enqueue(line.Trim());
                File.Delete(cmdPath);
            }
        }

        CpuSampler.Tick();
        GpuTimer.Update();
        if (CpuSampler.PendingReport is { } report)
        {
            CpuSampler.PendingReport = null;
            SaveProfile($"cpu-{profileLabel}.txt", report);
        }
        if (GpuTimer.PendingReport is { } gpuReport)
        {
            GpuTimer.PendingReport = null;
            SaveProfile($"gpu-{gpuLabel}.txt", gpuReport);
        }

        for (int i = held.Count - 1; i >= 0; i--)
            if (now >= held[i].until) { pad.SetOverride(held[i].control, 0); held.RemoveAt(i); }

        if (uncapped && !UnityEngine.XR.XRSettings.enabled)
        {
            // The game re-applies its 60 fps cap (e.g. when entering the cockpit); keep it off while measuring.
            if (Application.targetFrameRate != 1000) Application.targetFrameRate = 1000;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
        }
        if (now < carLogUntil && now >= nextCarLog && Global.Instance != null && Global.Instance.playerCar != null)
        {
            nextCarLog = now + carLogEvery;
            var pc = Global.Instance.playerCar;
            var rbc = pc.rBody;
            var lv = pc.transform.InverseTransformDirection(rbc.velocity);
            Plugin.L.LogInfo($"car: t={now:F2} localVel={Diag.V(lv)} yawRate={rbc.angularVelocity.y * Mathf.Rad2Deg:F1} steer={pc.steerInput:F2} strafe={pc.strafeInput:F2} pos={Diag.V(pc.transform.position)} euler={Diag.V(pc.transform.eulerAngles)} up.y={pc.transform.up.y:F2} " +
                             $"vel={Diag.V(rbc.velocity)} angVel={Diag.V(rbc.angularVelocity)} speed={pc.currentSpeed:F1} fuel={pc.currentFuel:F3} gas={pc.gasInput:F2} max={pc.currentMaxSpeed:F0} floor={pc.currentFloorLevel:F1} layer=[{pc.layerMinY:F0},{pc.layerMaxY:F0}] frozen={pc.controlsFrozen}");
        }
        if (benchLabel != null) { BenchFrame(now); return; }
        if (burstLeft > 0)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, $"{burstName}-{burstIndex++:D2}-f{Time.frameCount}.png"));
            burstLeft--;
            return;
        }

        if (now < waitUntil || queue.Count == 0) return;
        if (waitFor != null)
        {
            if (!Diag.StateLine().Contains(waitFor) && now < waitForDeadline) return;
            Plugin.L.LogInfo($"waitstate '{waitFor}' {(now < waitForDeadline ? "reached" : "timed out")}: {Diag.StateLine()}");
            waitFor = null;
        }
        var cmd = queue.Dequeue();
        Plugin.L.LogInfo("cmd> " + cmd);
        try { Run(cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries), now); }
        catch (Exception e) { Plugin.L.LogError($"cmd '{cmd}' failed: {e}"); }
    }

    static void Tree(System.Text.StringBuilder sb, Transform t, int depth, int maxDepth)
    {
        if (t == null || depth > maxDepth) return;
        var comps = string.Join(",", t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform" && n != "RectTransform"));
        var tmp = t.GetComponent<TMPro.TextMeshProUGUI>();
        string text = tmp != null ? $" \"{tmp.text}\" font={Diag.Name(tmp.font)} size={tmp.fontSize} color={tmp.color}" : "";
        var rt = t.TryCast<RectTransform>();
        string rect = rt != null ? $" rect={rt.rect.width:F0}x{rt.rect.height:F0}@{Diag.V(rt.anchoredPosition)}" : "";
        sb.AppendLine($"{new string(' ', depth * 2)}{(t.gameObject.activeSelf ? "" : "(off) ")}{t.name} [{comps}]{rect}{text}");
        for (int i = 0; i < t.childCount; i++) Tree(sb, t.GetChild(i), depth + 1, maxDepth);
    }

    // Run once at the end of a frame, while the first-person body still has its rendered pose (VRHost).
    public static bool DevLive;
    public static Action PendingCapture;
    public static int EyeLogFrames;
    static int CarInfoFrames;
    static int PadInfoFrames;
    static float carInfoNext;
    internal static GameObject TestLight;

    // farshot <name> <up> <yaw> <pitch>: the main camera itself (its fog, culling and effects as rendered this frame)
    // re-rendered from above the player, looking out in a set direction, to see how far the city shows.
    static void FarShot(string path, float up, float yaw, float pitch)
    {
        var g = Global.Instance;
        var cam = g != null ? g.mainCamera : null;
        if (cam == null || g.player == null) return;
        var t = cam.transform;
        var pos = t.position; var rot = t.rotation;
        var eye = cam.stereoTargetEye; var target = cam.targetTexture;
        var rt = new RenderTexture(1280, 720, 24);
        // no cockpit in the way (the interior is drawn after the main camera by this component)
        var interior = cam.GetComponent<RedAlliance.Util.MainCameraVehicleRenderer>();
        bool interiorWas = interior != null && interior.enabled;
        if (interior != null) interior.enabled = false;
        var origin = Global.IsInVehicle && g.playerCar != null ? g.playerCar.transform.position : g.player.transform.position;
        try
        {
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.targetTexture = rt;
            t.SetPositionAndRotation(origin + Vector3.up * up, Quaternion.Euler(pitch, yaw, 0));
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t2d = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            t2d.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            t2d.Apply();
            RenderTexture.active = prev;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(t2d));
            UnityEngine.Object.Destroy(t2d);
            Plugin.L.LogInfo($"farshot: {path} fog {RenderSettings.fogStartDistance:F0}-{RenderSettings.fogEndDistance:F0} far {cam.farClipPlane:F0}");
        }
        finally
        {
            t.SetPositionAndRotation(pos, rot);
            cam.targetTexture = target;
            cam.stereoTargetEye = eye;
            if (interior != null) interior.enabled = interiorWas;
            rt.Release();
        }
    }

    // savert <name> <hxtemporal|hxvolumetric|sky>: a render texture as it is at the end of the frame
    static void SaveRT(string path, string which)
    {
        var g = Global.Instance;
        var hx = g != null && g.mainCamera != null ? g.mainCamera.GetComponent<HxVolumetricCamera>() : null;
        Texture tex = which switch
        {
            "hxtemporal" => hx != null ? hx.TemporalTexture : null,
            "hxvolumetric" => hx != null ? hx.VolumetricTexture : null,
            "hxfullblur" => hx != null ? hx.FullBlurRT : null,
            "hxfullblur2" => hx != null ? hx.FullBlurRT2 : null,
            "hxdownblur" => hx != null ? hx.downScaledBlurRT : null,
            _ => Shader.GetGlobalTexture("_GlobalFogSkybox"),
        };
        if (tex == null) { Plugin.L.LogWarning("savert: no texture " + which); return; }
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(tex, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var t2d = new Texture2D(tex.width, tex.height, TextureFormat.RGB24, false);
        t2d.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        t2d.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(t2d));
        UnityEngine.Object.Destroy(t2d);
        Plugin.L.LogInfo($"savert: {which} '{tex.name}' {tex.width}x{tex.height} -> {path}");
    }

    static void PoseShot(string path, float yaw, float dist, float height)
    {
        var g = Global.Instance;
        if (g == null || g.player == null) return;
        var model = g.player.anim != null ? g.player.anim.transform : g.player.transform;
        var center = model.position + Vector3.up * height;
        var go = new GameObject("CloudpunkVR_PoseCam");
        try
        {
            var cam = go.AddComponent<Camera>();
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.fieldOfView = 45;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300;
            go.transform.position = center + Quaternion.Euler(0, model.eulerAngles.y + yaw, 0) * Vector3.forward * dist;
            go.transform.LookAt(center);
            var rt = new RenderTexture(960, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t2d = new Texture2D(960, 720, TextureFormat.RGB24, false);
            t2d.ReadPixels(new Rect(0, 0, 960, 720), 0, 0);
            t2d.Apply();
            RenderTexture.active = prev;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(t2d));
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(t2d);
            rt.Release();
            Plugin.L.LogInfo("posecam: " + path);
        }
        finally { UnityEngine.Object.Destroy(go); }
    }

    // Dev: the game's on-screen button prompts and the actions they show.
    static string DescribeIcons()
    {
        var sb = new System.Text.StringBuilder("icons:\n");
        foreach (var ic in Resources.FindObjectsOfTypeAll<InputIcon>())
        {
            if (!ic.gameObject.scene.IsValid()) continue;
            var parent = ic.transform.parent;
            var near = parent != null ? string.Join(" | ", parent.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true)
                .Select(t => t.text).Where(t => !string.IsNullOrWhiteSpace(t)).Take(3)) : "";
            var setup = ic.setupAction != null && ic.setupAction.GetPersistentEventCount() > 0 ? ic.setupAction.GetPersistentMethodName(0) : "-";
            sb.AppendLine($"  [{(ic.gameObject.activeInHierarchy ? "ON " : "off")}] {Diag.PathOf(ic.transform)} setup={setup} action={(ic.action != null ? ic.action.Name : "-")} " +
                          $"sprite={(ic.imageObj != null && ic.imageObj.sprite != null ? ic.imageObj.sprite.name : "-")} img={(ic.imageObj != null && ic.imageObj.enabled)} " +
                          $"text='{(ic.textObj != null ? ic.textObj.text : "-")}' near='{near}'");
        }
        return sb.ToString();
    }

    // Dev: volumetric lights (Hx) in the scene.
    static string DescribeHx(Global g)
    {
        var sb = new System.Text.StringBuilder("hx:\n");
        foreach (var hc in UnityEngine.Object.FindObjectsOfType<HxVolumetricCamera>())
            sb.AppendLine($"  camera {Diag.PathOf(hc.transform)} enabled={hc.enabled} res={hc.resolution} order={hc.RenderOrder} stereoUsed={hc.SinglePassStereoUsed}");
        foreach (var l in UnityEngine.Object.FindObjectsOfType<HxVolumetricLight>())
        {
            var light = l.GetComponent<Light>();
            sb.AppendLine($"  light {Diag.PathOf(l.transform)} enabled={l.enabled} type={(light != null ? light.type.ToString() : "-")} pos={Diag.V(l.transform.position)} range={(light != null ? light.range : 0):F0}");
        }
        foreach (var cam in Camera.allCameras)
            sb.AppendLine($"  cam {Diag.PathOf(cam.transform)} depth={cam.depth} eye={cam.stereoTargetEye} rt={(cam.targetTexture != null)} comps={string.Join(",", cam.GetComponents<Behaviour>().Select(b => b.GetIl2CppType().Name))}");
        return sb.ToString();
    }

    // Dev: the frames the on-foot movement is computed in.
    static string WalkInfo(Global g)
    {
        var p = g.player;
        if (p == null) return "walk: no player";
        float Y(Transform t) => t != null ? t.eulerAngles.y : float.NaN;
        var mt = p.moveTo;
        var cam = g.mainCamera != null ? g.mainCamera.transform : null;
        var v = p.charController != null ? p.charController.velocity : Vector3.zero;
        float velYaw = new Vector2(v.x, v.z).sqrMagnitude > 0.01f ? Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg : float.NaN;
        var pov = p.firstPersonPOV;
        var brain = g.cameraBrain;
        var live = brain != null ? brain.ActiveVirtualCamera : null;
        var fixedCam = p.fixedCamera != null ? p.fixedCamera.transform : null;
        return $"walk: moveToYaw={Y(mt):F1} pointLocal={Diag.V(p.moveToPoint != null ? p.moveToPoint.localPosition : Vector3.zero)} fixedCamYaw={Y(fixedCam):F1} playerMainCam={Diag.PathOf(p.mainCamera)} pmcYaw={Y(p.mainCamera):F1} moveTo={Diag.PathOf(mt)} local={Diag.V(mt != null ? mt.localPosition : Vector3.zero)} parent={Diag.PathOf(mt != null ? mt.parent : null)} parentYaw={Y(mt != null ? mt.parent : null):F1} " +
               $"moveToPoint={Diag.PathOf(p.moveToPoint)} align={p.alignment.eulerAngles.y:F1} defAlign={p.defaultAlignment.eulerAngles.y:F1} freeLook={p.FreeLook} locked={p.cameraLocked} " +
               $"camYaw={Y(cam):F1} povH={(pov != null ? pov.m_HorizontalAxis.Value : float.NaN):F1} playerYaw={Y(p.transform):F1} velYaw={velYaw:F1} speed={new Vector2(v.x, v.z).magnitude:F2} " +
               $"lr={p.leftRight:F2} ud={p.upDown:F2} input={Diag.V(p.inputVector)} vcam={(live != null ? live.Name : "-")} volume={(p.currentVolume != null ? p.currentVolume.name : "-")}";
    }

    // Dev: how the first-person look is updated (brain timing, the POV yaw axis).
    static string PovInfo(Global g)
    {
        var brain = g.cameraBrain;
        var pov = g.player != null ? g.player.firstPersonPOV : null;
        string b = brain == null ? "brain=-" : $"brain update={brain.m_UpdateMethod} blend={brain.m_BlendUpdateMethod} ignoreTimeScale={brain.m_IgnoreTimeScale}";
        if (pov == null) return "pov: " + b + " pov=-";
        var h = pov.m_HorizontalAxis;
        return $"pov: {b} fixedDt={Time.fixedDeltaTime * 1000:F1}ms vsync={QualitySettings.vSyncCount} target={Application.targetFrameRate} " +
               $"H value={h.Value:F1} range=[{h.m_MinValue:F0},{h.m_MaxValue:F0}] wrap={h.m_Wrap} maxSpeed={h.m_MaxSpeed} accel={h.m_AccelTime} decel={h.m_DecelTime} " +
               $"input='{h.m_InputAxisName}' inputValue={h.m_InputAxisValue:F2} heading={pov.m_RecenterTarget}";
    }

    // Moves the player's car into the nearest parking volume so a following "press Action1" can park and exit.
    static void GoPark(Global g)
    {
        var car = g.playerCar;
        ParkVolume best = null;
        float bestDist = float.MaxValue;
        foreach (var v in UnityEngine.Object.FindObjectsOfType<ParkVolume>())
        {
            if (v.volumeCollider == null) continue;
            float d = Vector3.Distance(v.volumeCollider.bounds.center, car.transform.position);
            if (d < bestDist) { bestDist = d; best = v; }
        }
        if (best == null) { Plugin.L.LogWarning("gopark: no ParkVolume loaded"); return; }
        var target = best.volumeCollider.bounds.center;
        var rb = car.GetComponent<Rigidbody>();
        if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = target; }
        car.transform.position = target;
        Plugin.L.LogInfo($"gopark: moved car {bestDist:F0}m to {Diag.PathOf(best.transform)} at {Diag.V(target)} spawn={Diag.PathOf(best.characterSpawnPoint != null ? best.characterSpawnPoint.transform : null)}");
    }

    void Run(string[] a, float now)
    {
        for (int i = 0; i < a.Length; i++) a[i] = a[i].Replace("%20", " ");
        var g = Global.Instance;
        // unlesscar <command...>: run the command only while the player is not in a car (retries of getting in)
        if (a[0] == "unlesscar")
        {
            if (Global.IsInVehicle) return;
            a = a.Skip(1).ToArray();
        }
        // unlesslevel <command...>: only while still on the title screen (a fallback when the title menu took no key)
        if (a[0] == "unlesslevel")
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name.StartsWith("Level")) return;
            a = a.Skip(1).ToArray();
        }
        switch (a[0])
        {
            case "wait": waitUntil = now + float.Parse(a[1]); break;
            case "waitstate": waitFor = a[1]; waitForDeadline = now + (a.Length > 2 ? float.Parse(a[2]) : 60); break;
            case "dump": Diag.DumpAll(a.Length > 1 ? a[1] : "cmd"); break;
            case "census": Diag.DumpCensus(a.Length > 1 ? a[1] : "cmd"); break;
            case "bindings": Diag.DumpAll("F9"); break;
            case "xrefs": Diag.DumpXrefs(); break;
            case "continue": g.ContinueGame(); break;
            case "tpfoot":
                // tpfoot <x> <y> <z> [yaw]: stand the player (on foot) there, e.g. under a street lamp for the halo shots
            {
                var cc = g.player.charController;
                cc.enabled = false;
                var spot = new Vector3(float.Parse(a[1]), float.Parse(a[2]), float.Parse(a[3]));
                g.player.transform.position = spot;
                if (a.Length > 4) g.player.transform.rotation = Quaternion.Euler(0, float.Parse(a[4]), 0);
                cc.enabled = true;
                Plugin.L.LogInfo($"tpfoot: player at {Diag.V(spot)} (in a car: {Global.IsInVehicle})");
                break;
            }
            case "tpcar":
                // tpcar [side metres]: stand the player beside the car (then "press Action1" gets in)
            {
                var car = g.playerCar.transform;
                var cc = g.player.charController;
                cc.enabled = false;
                var spot = car.position + car.right * (a.Length > 1 ? float.Parse(a[1]) : 2.5f);
                g.player.transform.position = spot;
                g.player.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(car.position - spot, Vector3.up));
                cc.enabled = true;
                Plugin.L.LogInfo($"tpcar: player at {Diag.V(spot)}, car at {Diag.V(car.position)}");
                break;
            }
            case "glass":
                // glass [prop value]...: windshield material values, optionally set some first
            {
                var vi = g.vehicleInterior;
                var mat = vi.windshieldFrontMat != null ? vi.windshieldFrontMat : vi.windshieldFrontMesh.sharedMaterial;
                for (int i = 1; i + 1 < a.Length; i += 2) mat.SetFloat(a[i], float.Parse(a[i + 1]));
                var names = new[] { "_RainColor", "_RainColorBoost", "_NormalOffset", "_RefractionAmount", "_MaskIntensity", "_UseSurfaceRipples",
                    "_UseFlowMap", "_FlowMapIntensity", "_UseCleaners", "_GlassColor", "_GlassAlpha", "_GlassColorBoost", "_WindshieldRain",
                    "_BlurGlass", "_BlurSamples", "_BlurIntensity", "_NoiseIntensity", "_CracksMode", "_GlassDamageCondition", "_Debug", "_DebugMode" };
                var sb = new System.Text.StringBuilder($"glass: {Diag.PathOf(vi.windshieldFrontMesh.transform)} mat='{mat.name}' shader='{mat.shader.name}' queue={mat.renderQueue} keywords=[{string.Join(" ", mat.shaderKeywords)}]");
                foreach (var pn in names) sb.Append($" {pn}={(pn.Contains("Color") && !pn.Contains("Boost") ? mat.GetColor(pn).ToString() : mat.GetFloat(pn).ToString("G4"))}");
                var rm = mat.GetTexture("_RainMask");
                sb.Append($" _RainMask={Diag.Name(rm)} global_WindshieldRain={Shader.GetGlobalFloat("_WindshieldRain")}");
                Plugin.L.LogInfo(sb.ToString());
                break;
            }
            case "layoutset":
                // layoutset <section.function> <bindings>: rewrite the layout file like the editor does (tests reloading)
            {
                var lm = ControlMap.Load(ControlLayout.Path, out _);
                lm[a[1]] = ControlMap.ParseBindings(string.Join(" ", a.Skip(2)));
                ControlMap.Save(ControlLayout.Path, lm);
                Plugin.L.LogInfo($"layoutset: {a[1]} = {string.Join(", ", lm[a[1]])}");
                break;
            }
            case "carlog": carLogUntil = now + float.Parse(a[1]); carLogEvery = a.Length > 2 ? float.Parse(a[2]) : 0.5f; break;
            case "glows": 
                Plugin.L.LogInfo(VRHost.Glows != null ? VRHost.Glows.Describe() : "glows: not in VR");
                Plugin.L.LogInfo($"glows: FindObjectsOfType={UnityEngine.Object.FindObjectsOfType<WhiteCat.Rendering.LensFlareSource>().Length} " +
                                 $"all={Resources.FindObjectsOfTypeAll<WhiteCat.Rendering.LensFlareSource>().Length} " +
                                 $"byName={UnityEngine.Object.FindObjectsOfType<Behaviour>().Count(b => b.GetIl2CppType().Name == "LensFlareSource")}");
                break;
            case "fakehands":
                // fakehands <side> <height> <forward> [pitch yaw roll] | off   (roll is mirrored for the left hand)
                VRCameras.FakeHands = a[1] == "off" ? null : new Vector3(float.Parse(a[1]), float.Parse(a[2]), float.Parse(a[3]));
                VRCameras.FakeHandEuler = a.Length > 6 ? new Vector3(float.Parse(a[4]), float.Parse(a[5]), float.Parse(a[6])) : Vector3.zero;
                break;
            case "armcfg":
                // armcfg <handPitch> <wristBack> [forearmTwistShare] [maxWristSwing maxWristTwist] (180 180 = hand follows exactly)
                Plugin.Cfg.HandPitch.Value = float.Parse(a[1]); VRBody.Arm.WristBack = float.Parse(a[2]);
                if (a.Length > 3) VRBody.Arm.ForearmTwistShare = float.Parse(a[3]);
                if (a.Length > 5) { VRBody.Arm.MaxWristSwing = float.Parse(a[4]); VRBody.Arm.MaxWristTwist = float.Parse(a[5]); }
                break;
            case "icons": Plugin.L.LogInfo(DescribeIcons()); break;
            case "cockpitinfo":
                {
                    var vi = g.vehicleInterior;
                    var eye = vi.cameraCockpit != null ? vi.cameraCockpit.transform : vi.interiorCameraTransform;
                    string R(Transform t) => t == null ? "-" : Diag.V(eye.InverseTransformPoint(t.position));
                    Plugin.L.LogInfo($"cockpit (from {Diag.PathOf(eye)}): socket={R(vi.interiorCameraTransform)} wheel={R(vi.steeringWheel)} speedo={R(vi.speedometer)} lever={R(vi.ascensionLever)} pedal={R(vi.gasPedal)} " +
                                     $"interiorLocal={Diag.PathOf(vi.interiorLocal)} eyeUp={Diag.V(eye.up)} localUp={Diag.V(vi.interiorLocal.up)} camMask={(vi.cameraCockpit != null ? vi.cameraCockpit.cullingMask : 0)}");
                }
                break;
            case "nav":
                // nav [drive]: plan to the nearest quest marker like the World's navigate request
                // nav [drive] [quest id]
                {
                    var args = new System.Text.Json.Nodes.JsonObject { ["drive"] = a.Contains("drive") };
                    var qid = a.Skip(1).FirstOrDefault(x => x != "drive");
                    if (qid != null) args["quest"] = qid;
                    Coo.CooHost.Instance.Pilot.Navigate(args, (r, err) =>
                    {
                        if (err != null) Plugin.L.LogWarning("nav: " + err);
                        else Plugin.L.LogInfo("nav: " + r.ToJsonString());
                    });
                }
                break;
            case "lifts":
                // lifts: the level's lifts and where the game shows each quest marker
                {
                    var sb = new System.Text.StringBuilder($"lifts in scene {UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex}:\n");
                    foreach (var l in UnityEngine.Object.FindObjectsOfType<Lift>())
                    {
                        var col = l.GetComponent<Collider>();
                        sb.Append($"  {Diag.PathOf(l.transform)} at {Diag.V(l.transform.position)} trigger {(col != null ? Diag.V(col.bounds.center) + " size " + Diag.V(col.bounds.size) : "-")} direction {l.direction} destination {l.destination} exit '{l.exitID}'\n");
                    }
                    foreach (var q in Coo.CooGame.ActiveQuests())
                    {
                        var shownAt = Coo.CooGame.MarkerPos(q, out bool other);
                        sb.Append($"  quest {q.id} '{Coo.CooGame.Clean(q.text)}' marker {q.markerType} in scene {q.markerInScene} at {Diag.V(q.markerPos)}; shown at {Diag.V(shownAt)}{(other ? " (other level)" : "")}\n");
                    }
                    Plugin.L.LogInfo(sb.ToString());
                }
                break;
            case "navlift":
                // navlift <x> <y> <z> [drive]: as if a quest lay beyond the lift nearest that point
                Coo.CooHost.Instance.Pilot.Navigate(new System.Text.Json.Nodes.JsonObject
                {
                    ["pos"] = new System.Text.Json.Nodes.JsonArray(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture)),
                    ["label"] = "测试升降梯",
                    ["devLift"] = true,
                    ["drive"] = a.Contains("drive"),
                }, (r, err) =>
                {
                    if (err != null) Plugin.L.LogWarning("navlift: " + err);
                    else Plugin.L.LogInfo("navlift: " + r.ToJsonString());
                });
                break;
            case "carbox":
                // carbox: the player car's solid colliders against its pivot (what the route planner's car box should be)
                {
                    var car = g.playerCar;
                    var sb = new System.Text.StringBuilder($"carbox: pivot {Diag.V(car.transform.position)} rot {Diag.V(car.transform.eulerAngles)}\n");
                    Bounds all = default;
                    bool any = false;
                    foreach (var c in car.GetComponentsInChildren<Collider>(true))
                    {
                        if (c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                        sb.Append($"  {Diag.PathOf(c.transform)} {c.GetIl2CppType().Name} layer {LayerMask.LayerToName(c.gameObject.layer)} centre-pivot {Diag.V(c.bounds.center - car.transform.position)} size {Diag.V(c.bounds.size)}\n");
                        if (!any) { all = c.bounds; any = true; } else all.Encapsulate(c.bounds);
                    }
                    if (any) sb.Append($"  all: centre-pivot {Diag.V(all.center - car.transform.position)} size {Diag.V(all.size)}");
                    Plugin.L.LogInfo(sb.ToString());
                }
                break;
            case "above":
                // above [metres]: every solid collider (all layers) a car-sized box meets going straight up from the car
                {
                    var car = g.playerCar;
                    float up = a.Length > 1 ? float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) : 10f;
                    var hits = Physics.BoxCastAll(car.transform.position, new Vector3(2.2f, 0.6f, 2.3f), Vector3.up, car.transform.rotation, up, ~0, QueryTriggerInteraction.Ignore);
                    var sb = new System.Text.StringBuilder($"above {Diag.V(car.transform.position)} (planner mask 0x{Coo.CooSpace.CarMask():x}):\n");
                    foreach (var hh in hits)
                        if (!hh.transform.IsChildOf(car.transform))
                            sb.Append($"  {hh.distance:F1} m {Diag.PathOf(hh.transform)} layer {LayerMask.LayerToName(hh.collider.gameObject.layer)} ({hh.collider.gameObject.layer}) {hh.collider.GetIl2CppType().Name}\n");
                    Plugin.L.LogInfo(sb.ToString());
                }
                break;
            case "planbudget": Coo.CooPilot.PlanBudgetMs = double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture); break;
            case "space":
                {
                    var car = g.playerCar;
                    var sp = new Coo.CooSpace(Coo.CooGame.PlayerPos(), 110f, 35f, 35f, car != null ? car.layerMinY + 1.5f : -1000f, car != null ? car.layerMaxY - 1.5f : 1000f);
                    var swatch = System.Diagnostics.Stopwatch.StartNew();
                    sp.Flood();
                    Plugin.L.LogInfo($"flood took {swatch.ElapsedMilliseconds} ms\n" + sp.Describe());
                }
                break;
            case "view": Coo.CooHost.Instance?.View.SetMode(a[1]); break;
            case "grab":
                // grab <name>: the desktop window as Windows shows it (what OBS captures), via an external helper
                Directory.CreateDirectory(shotDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python", $"-I \"{Environment.GetEnvironmentVariable("CLOUDPUNKVR_GRAB")}\" \"{Path.Combine(shotDir, a[1] + ".png")}\"") { UseShellExecute = false, CreateNoWindow = true });
                break;
            case "specshot": Directory.CreateDirectory(shotDir); Coo.CooView.SaveSpectator(Path.Combine(shotDir, a[1] + ".png")); break;
            case "dialoginfo":
            {
                // dialoginfo: the dialogue HUD's parts (paths, canvases, materials, sprites) and its hierarchy
                var d = g.dialogUI;
                var sbd = new System.Text.StringBuilder("===== dialoginfo =====\n");
                sbd.AppendLine($"dialogHUD {Diag.PathOf(d.transform)} active={d.gameObject.activeInHierarchy} fading in/out={d.fadingIn}/{d.fadingOut} phase={d.phaseID}");
                void Part(string label, Component c)
                {
                    if (c == null) { sbd.AppendLine($"{label}: null"); return; }
                    var rt = c.transform.TryCast<RectTransform>();
                    var canvas = c.GetComponentInParent<Canvas>();
                    var gr = c.TryCast<UnityEngine.UI.Graphic>();
                    var img = c.TryCast<UnityEngine.UI.Image>();
                    sbd.AppendLine($"{label}: {Diag.PathOf(c.transform)} activeInHierarchy={c.gameObject.activeInHierarchy} rect={(rt != null ? $"{rt.rect.width:F0}x{rt.rect.height:F0} pivot {Diag.V(rt.pivot)} world {Diag.V(rt.position)} lossy {rt.lossyScale.x:F4}" : "-")} " +
                                   $"canvas={(canvas != null ? $"{canvas.name} mode={canvas.renderMode} cam={Diag.Name(canvas.worldCamera)} layer={canvas.gameObject.layer} order={canvas.sortingOrder}" : "-")} " +
                                   $"material={(gr != null && gr.material != null ? $"{gr.material.name} shader={gr.material.shader.name}" : "-")} color={(gr != null ? gr.color.ToString() : "-")} " +
                                   $"sprite={(img != null ? Diag.Name(img.sprite) + " " + (img.sprite != null ? $"{img.sprite.rect.width}x{img.sprite.rect.height} tex={Diag.Name(img.sprite.texture)}" : "") + $" type={img.type} preserve={img.preserveAspect}" : "-")}");
                }
                Part("portrait", d.portrait); Part("nameObj", d.nameObj); Part("textField", d.textField);
                Part("portraitInterior", d.portraitInterior); Part("nameObjInterior", d.nameObjInterior); Part("textFieldInterior", d.textFieldInterior);
                Part("exteriorGroup", d.exteriorCanvasGroupGlobal); Part("interiorGroup", d.interiorCanvasGroupGlobal);
                sbd.AppendLine($"group alpha ext={d.exteriorCanvasGroupGlobal?.alpha} int={d.interiorCanvasGroupGlobal?.alpha}");
                if (d.materials != null)
                    for (int i = 0; i < d.materials.Length; i++)
                    {
                        var dm = d.materials[i];
                        sbd.AppendLine($"materials[{i}] {Diag.Name(dm)} shader={(dm != null ? dm.shader.name : "-")} tex={(dm != null && dm.mainTexture != null ? Diag.Name(dm.mainTexture) : "-")}");
                    }
                if (d.textContents != null)
                    for (int i = 0; i < d.textContents.Length; i++) sbd.AppendLine($"textContents[{i}] {Diag.PathOf(d.textContents[i]?.transform)}");
                Tree(sbd, d.transform, 0, a.Length > 1 ? int.Parse(a[1]) : 6);
                foreach (var grp in new[] { d.exteriorCanvasGroupGlobal, d.interiorCanvasGroupGlobal })
                    if (grp != null && !grp.transform.IsChildOf(d.transform)) { sbd.AppendLine("--- " + Diag.PathOf(grp.transform)); Tree(sbd, grp.transform, 0, 6); }
                Plugin.L.LogInfo(sbd.ToString());
                break;
            }
            case "dialogfake":
            {
                // dialogfake <ext|int|off> [name] [text...]: show the dialogue HUD's exterior or interior box with a made-up line
                var d = g.dialogUI;
                bool ext = a[1] == "ext", inner = a[1] == "int";
                string nm = a.Length > 2 ? a[2].Replace('+', ' ') : "可缇";
                string tx = a.Length > 3 ? string.Join(" ", a.Skip(3)) : "这是一句测试台词，看看框里能放下多少字。";
                foreach (var (grp, show) in new[] { (d.exteriorCanvasGroupGlobal, ext), (d.interiorCanvasGroupGlobal, inner) })
                {
                    if (grp == null) continue;
                    grp.alpha = show ? 1 : 0;
                    if (show) for (var p = grp.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                }
                if (ext) { d.nameObj.text = nm; d.textField.text = tx; d.textField.alpha = 1; }
                if (inner) { d.nameObjInterior.text = nm; d.textFieldInterior.text = tx; d.textFieldInterior.alpha = 1; }
                break;
            }
            case "live":
            {
                // live <0|1>: on air without the World (needs no connection: the dev channel stands in for it)
                var host = Coo.CooHost.Instance;
                host.Channel.OnAir = host.Overlay.OnAir = a[1] == "1";
                DevLive = a[1] == "1";
                break;
            }
            case "tmpfont":
            {
                // tmpfont: which of the call box's characters the game's fonts lack, and whether a fallback from a
                // Windows font can be made
                var d = g.dialogUI;
                foreach (var t in new TMPro.TMP_Text[] { d.nameObj, d.textField, d.nameObjInterior, d.textFieldInterior })
                {
                    if (t == null || t.font == null) continue;
                    Plugin.L.LogInfo($"tmpfont: {Diag.PathOf(t.transform)} font {t.font.name} 可={t.font.HasCharacter('可', true)} 缇={t.font.HasCharacter('缇', true)} 嗯={t.font.HasCharacter('嗯', true)} fallbacks={t.font.fallbackFontAssetTable?.Count}");
                }
                try
                {
                    var os = UiFont.Os(new[] { "Microsoft YaHei UI", "Microsoft YaHei" }, 64);
                    var fa = TMPro.TMP_FontAsset.CreateFontAsset(os);
                    Plugin.L.LogInfo($"tmpfont: from OS font -> {(fa == null ? "null" : $"{fa.name} add 缇 {fa.TryAddCharacters("可缇嗯")} has {fa.HasCharacter('缇')}")}");
                }
                catch (Exception e) { Plugin.L.LogWarning("tmpfont: " + e.Message); }
                break;
            }
            case "dlgclose": if (g.dialogUI.gameObject.activeInHierarchy) g.dialogUI.FadeOut(); break;
            case "cortisay": Coo.CooHost.Instance.Channel.Say(string.Join(" ", a.Skip(2)), float.Parse(a[1])); break;
            case "spout":
                // spout [sender|-] [zoom x y]: pick the Spout sender and the crop; logs what is received
                {
                    var sp = Coo.CooHost.Instance.Spout;
                    if (a.Length > 1) sp.Wanted = a[1] == "-" ? "" : a[1];
                    if (a.Length > 4) { sp.Zoom = float.Parse(a[2]); sp.CenterX = float.Parse(a[3]); sp.CenterY = float.Parse(a[4]); }
                    Plugin.L.LogInfo($"spout: senders [{string.Join(", ", Coo.CooSpout.Senders())}] active '{Coo.CooSpout.ActiveSender()}' receiving '{sp.Sender}' {sp.Width}x{sp.Height} fmt {sp.Format} problem '{sp.Problem}' channel {Coo.CooHost.Instance.Channel.Describe().ToJsonString()}");
                    break;
                }
            case "spoutshot":
                // spoutshot <name>: the preview frame and the box picture as the World would get them
                {
                    var sp = Coo.CooHost.Instance.Spout;
                    Directory.CreateDirectory(shotDir);
                    if (!sp.Live) { Plugin.L.LogWarning("spoutshot: not receiving: " + sp.Problem); break; }
                    File.WriteAllBytes(Path.Combine(shotDir, a[1] + "_frame.jpg"), sp.PreviewJpeg(640, out _, out _));
                    sp.Render();
                    File.WriteAllBytes(Path.Combine(shotDir, a[1] + "_box.jpg"), sp.BoxJpeg());
                    break;
                }
            case "overlayinfo": Plugin.L.LogInfo("overlay: " + Coo.CooHost.Instance.Overlay.Describe().ToJsonString()); break;
            // holofig <height> <lift> <out> [yaw] [zoom]: the pop-out hologram over the cockpit call screen (metres, degrees)
            case "holofig":
                Coo.CooHoloFigure.Height = float.Parse(a[1]);
                Coo.CooHoloFigure.Lift = float.Parse(a[2]);
                Coo.CooHoloFigure.Out = float.Parse(a[3]);
                if (a.Length > 4) Coo.CooHoloFigure.Yaw = float.Parse(a[4]);
                if (a.Length > 5) Coo.CooHost.Instance.Channel.Figure.Zoom = float.Parse(a[5]);
                break;
            // fufu <right> <down> <fwd> [yaw] [scale]: the passenger-seat doll from the camera socket (metres, degrees)
            case "fufu":
                Coo.CooFufu.Right = float.Parse(a[1]); Coo.CooFufu.Down = float.Parse(a[2]); Coo.CooFufu.Fwd = float.Parse(a[3]);
                if (a.Length > 4) Coo.CooFufu.Yaw = float.Parse(a[4]);
                if (a.Length > 5) Coo.CooFufu.Scale = float.Parse(a[5]);
                break;
            // interiorparts [minRight]: cockpit meshes from the driver's eye (x right, y up, z forward), to find the seats
            case "interiorparts":
                {
                    var vi = g.vehicleInterior;
                    var eye = vi.cameraCockpit != null ? vi.cameraCockpit.transform : vi.interiorCameraTransform;
                    float minRight = a.Length > 1 ? float.Parse(a[1]) : 0.2f;
                    var il = vi.interiorLocal;
                    var fwd = Vector3.ProjectOnPlane(il.forward, il.up).normalized;
                    var right = Vector3.Cross(il.up, fwd);
                    int parts = 0;
                    foreach (var r in il.GetComponentsInChildren<Renderer>(true))
                    {
                        var d = r.bounds.center - eye.position;
                        var rel = new Vector3(Vector3.Dot(d, right), Vector3.Dot(d, il.up), Vector3.Dot(d, fwd));
                        if (rel.x < minRight) continue;
                        Plugin.L.LogInfo($"part: {Diag.PathOf(r.transform)} rel {Diag.V(rel)} size {Diag.V(r.bounds.size)} on {r.enabled && r.gameObject.activeInHierarchy} layer {r.gameObject.layer}");
                        if (++parts >= 80) break;
                    }
                    Plugin.L.LogInfo($"part: {parts} to the right of {minRight} m");
                    break;
                }
            case "fufushader": Coo.CooHost.Instance.Fufu.SetShader(string.Join(" ", a, 1, a.Length - 1)); break;
            case "fufulight": Coo.CooFufu.LightIntensity = float.Parse(a[1]); break;
            case "fufuinfo": Plugin.L.LogInfo("fufuinfo: " + Coo.CooHost.Instance.Fufu.Describe()); break;
            case "popinfo": Plugin.L.LogInfo("popinfo: " + Coo.CooHost.Instance.Channel.Figure.Describe() + " channel " + Coo.CooHost.Instance.Channel.Describe().ToJsonString() + " | " + Coo.CooHost.Instance.Channel.Debug()); break;
            // wristtest <0|1>: flat mode, the wrist screen in front of the camera (to look at its contents)
            // cockpitlook <yaw> <pitch>: flat mode, the cockpit camera turned (0 0 = back to the game's)
            case "cockpitlook": CockpitLook = new Vector2(float.Parse(a[1]), float.Parse(a[2])); break;
            case "wristtest": WristTest = a[1] == "1"; break;
            // rtshot <name> <render texture name>: any render texture as a PNG in shots/
            case "rtshot":
                {
                    RenderTexture found = null;
                    foreach (var r in Resources.FindObjectsOfTypeAll<RenderTexture>()) if (r.name == a[2]) { found = r; break; }
                    if (found == null) { Plugin.L.LogWarning("rtshot: no render texture " + a[2]); break; }
                    Directory.CreateDirectory(shotDir);
                    var prevRt = RenderTexture.active;
                    RenderTexture.active = found;
                    var tx = new Texture2D(found.width, found.height, TextureFormat.RGBA32, false);
                    tx.ReadPixels(new Rect(0, 0, found.width, found.height), 0, 0);
                    tx.Apply();
                    RenderTexture.active = prevRt;
                    File.WriteAllBytes(Path.Combine(shotDir, a[1] + ".png"), ImageConversion.EncodeToPNG(tx));
                    UnityEngine.Object.Destroy(tx);
                    Plugin.L.LogInfo($"rtshot: {a[2]} {found.width}x{found.height} -> {a[1]}.png");
                    break;
                }
            case "routeinfo": Plugin.L.LogInfo("routeinfo: " + Coo.CooHost.Instance.Pilot.RouteInfo()); break;
            // places [query]: the place index (legend, ids, kinds, distances) into the log
            case "places": Plugin.L.LogInfo("places:\n" + Coo.CooPlaces.Dump(a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "")); break;
            // navto <place> [drive]: navigate there as the World would
            case "navto":
                {
                    bool drv = a[a.Length - 1] == "drive";
                    var q = string.Join(" ", a, 1, a.Length - 1 - (drv ? 1 : 0));
                    Coo.CooHost.Instance.Pilot.Navigate(new System.Text.Json.Nodes.JsonObject { ["place"] = q, ["drive"] = drv }, (r, err) =>
                    {
                        if (err != null) Plugin.L.LogWarning("navto: " + err);
                        else Plugin.L.LogInfo("navto: " + r.ToJsonString());
                    });
                    break;
                }
            case "wristbox":
                // wristbox <along> <lift> [width]: the wrist screen on the left forearm (metres behind the wrist, centre above the arm, width)
                Coo.CooChannel.WristAlong = float.Parse(a[1]);
                Coo.CooChannel.WristLift = float.Parse(a[2]);
                if (a.Length > 3) Coo.CooChannel.WristWidth = float.Parse(a[3]);
                break;
            case "armlog": VRBody.Arm.LogFrames = a.Length > 1 ? int.Parse(a[1]) : 1; break;
            case "vrinput":
                // vrinput <input id> <value> [seconds]: as if a VR control were held (goes through the layout)
                SteamVRInput.Fake[a[1]] = (float.Parse(a[2]), now + (a.Length > 3 ? float.Parse(a[3]) : 0.2f));
                break;
            case "posecam":
                // posecam <name> <yaw> <distance> <height>: photograph the player from outside, in the pose rendered this frame
                {
                    string nm = Path.Combine(shotDir, a[1] + ".png");
                    float yaw = float.Parse(a[2]), dist = float.Parse(a[3]), hgt = float.Parse(a[4]);
                    PendingCapture = () => PoseShot(nm, yaw, dist, hgt);
                }
                break;
            case "testlight":
                // testlight <forward> <right> <range> <intensity> [hx 0|1] | testlight off | testlight on: a point light in front of the
                // camera (with a Hx volumetric halo), and where each eye should see it
                {
                    if (a[1] == "off" || a[1] == "on") { if (TestLight != null) TestLight.SetActive(a[1] == "on"); break; }
                    if (a[1] == "hx") { var hl = TestLight != null ? TestLight.GetComponent<HxVolumetricLight>() : null; if (hl != null) hl.enabled = a[2] == "1"; break; }
                    if (TestLight != null) UnityEngine.Object.Destroy(TestLight);
                    // along where the headset looks (world camera's game pose plus the tracked head)
                    var wc = VRCameras.WorldCamera(g.mainCamera);
                    var t = wc.transform;
                    var vc = VRCameras.Instance;
                    var basis = Quaternion.Euler(0, t.eulerAngles.y, 0);   // in Update the camera holds its game pose
                    var fwd = vc != null ? (basis * vc.HeadRot) * Vector3.forward : t.forward;
                    var rightV = vc != null ? (basis * vc.HeadRot) * Vector3.right : t.right;
                    var from = vc != null ? t.position + basis * vc.HeadPos : t.position;
                    TestLight = new GameObject("CloudpunkVR_TestLight");
                    TestLight.transform.position = from + fwd * float.Parse(a[1]) + rightV * float.Parse(a[2]);
                    var tl = TestLight.AddComponent<Light>();
                    tl.type = LightType.Point; tl.range = float.Parse(a[3]); tl.intensity = float.Parse(a[4]); tl.color = new Color(1f, 0.75f, 0.45f);
                    if (a.Length < 6 || a[5] == "1")
                        try { TestLight.AddComponent<HxVolumetricLight>(); } catch (Exception e) { Plugin.L.LogWarning("testlight: hx " + e.Message); }
                }
                break;
            case "testlightup": if (TestLight != null) TestLight.transform.position += Vector3.up * float.Parse(a[1]); break;
            case "testlightpos":
                {
                    if (TestLight == null) break;
                    var mc = VRCameras.WorldCamera(g.mainCamera);
                    var p4 = TestLight.transform.position;
                    string Eye(Camera.StereoscopicEye e)
                    {
                        var clip = mc.GetStereoProjectionMatrix(e) * mc.GetStereoViewMatrix(e) * new Vector4(p4.x, p4.y, p4.z, 1);
                        return $"u={(clip.x / clip.w + 1) / 2:F4} v={(1 - (clip.y / clip.w + 1) / 2):F4}";
                    }
                    Plugin.L.LogInfo($"testlight expected: L {Eye(Camera.StereoscopicEye.Left)} R {Eye(Camera.StereoscopicEye.Right)}");
                }
                break;
            case "handmesh":
                // handmesh: vertices of the player's hand meshes in their hand bone's space -> shots/handmesh_<L|R>.txt
                {
                    var vb = VRHost.Body;
                    foreach (var arm in vb != null ? vb.Arms : Array.Empty<VRBody.Arm>())
                    {
                        if (arm.Hand == null) continue;
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"# finger {arm.FingerAxis.x:F4} {arm.FingerAxis.y:F4} {arm.FingerAxis.z:F4} back {arm.BackAxis.x:F4} {arm.BackAxis.y:F4} {arm.BackAxis.z:F4}");
                        foreach (var mf in arm.Hand.GetComponentsInChildren<MeshFilter>(true))
                        {
                            var mesh = mf.sharedMesh;
                            if (mesh == null) continue;
                            if (!mesh.isReadable) { sb.AppendLine($"# {mf.name} not readable, bounds {mesh.bounds}"); continue; }
                            var vs = mesh.vertices;
                            sb.AppendLine($"# {mf.name} {vs.Length} vertices");
                            for (int i = 0; i < vs.Length; i++)
                            {
                                var p = arm.Hand.InverseTransformPoint(mf.transform.TransformPoint(vs[i]));
                                sb.AppendLine($"{p.x:F4} {p.y:F4} {p.z:F4}");
                            }
                        }
                        File.WriteAllText(Path.Combine(shotDir, "handmesh_" + (arm.Side < 0 ? "L" : "R") + ".txt"), sb.ToString());
                    }
                    Plugin.L.LogInfo("handmesh: written");
                }
                break;
            case "savert":
                {
                    string nm = Path.Combine(shotDir, a[1] + ".png"), which = a[2];
                    PendingCapture = () => SaveRT(nm, which);
                }
                break;
            case "farshot":
                {
                    string nm = Path.Combine(shotDir, a[1] + ".png");
                    float up = float.Parse(a[2]), fy = float.Parse(a[3]), fp = float.Parse(a[4]);
                    PendingCapture = () => FarShot(nm, up, fy, fp);
                }
                break;
            case "hxlights": Plugin.L.LogInfo(DescribeHx(g)); break;
            case "body": Plugin.L.LogInfo(VRHost.Body != null ? VRHost.Body.Describe() : "body: not in VR"); break;
            case "vrfx":
                // vrfx <fog|dof|flare> <0|1>: A/B the VR post-processing fixes
                bool vOn = a[2] == "1";
                if (a[1] == "fog") VRPostFx.FogFix = vOn; else if (a[1] == "dof") VRPostFx.DofOff = vOn; else if (a[1] == "flare") VRPostFx.FlaresOff = vOn; else if (a[1] == "glass") VRPostFx.GlassTune = vOn;
                VRPostFx.Refresh();
                break;
            case "cfg":
                // cfg <Section> <Key> <value>: change a plugin setting
            {
                // (TryGetEntry<T> throws when the entry has another type, so go through the untyped entry.)
                var def = new BepInEx.Configuration.ConfigDefinition(a[1], a[2]);
                if (!Plugin.Cfg.File.ContainsKey(def)) { Plugin.L.LogWarning("cfg: no setting " + def); break; }
                var entry = Plugin.Cfg.File[def];
                entry.SetSerializedValue(entry.SettingType == typeof(bool) ? (a[3] == "1" || a[3] == "true" ? "true" : "false") : a[3]);
                Plugin.L.LogInfo($"cfg: {def} = {entry.GetSerializedValue()}");
                break;
            }
            case "closemenu": if (g.IsInMenu()) g.CloseMenu(); break;
            case "cockpit": g.playerCar.SwitchToCockpit_Local(a[1] == "1"); break;
            case "interior": g.vehicleInterior.SwitchToCockpit(a[1] == "1"); break;
            case "fp": g.player.SwitchCamera(a[1] == "1"); break;
            case "walkinfo": Plugin.L.LogInfo(WalkInfo(g)); break;
            case "povinfo": Plugin.L.LogInfo(PovInfo(g)); break;
            // turntrace <frames>: one line per rendered frame with the game yaw the main camera got and how it got it
            case "turntrace": VRCameras.TraceFrames = int.Parse(a[1]); break;
            case "align": g.player.alignment = Quaternion.Euler(0, float.Parse(a[1]), 0); break;
            case "freelookflag": g.player.FreeLook = a[1] == "1"; break;
            case "fpcam":
                // flat-mode stand-in for VRBody: make the game's first-person virtual camera the live one
                g.player.firstPersonCamera.gameObject.SetActive(true);
                g.player.firstPersonCamera.enabled = true;
                g.player.firstPersonCamera.Priority = a[1] == "1" ? 50 : 1;
                break;
            case "camyaw":
                // rotate the live game camera's parent rig (dev): only to see which frame walking follows
                if (g.mainCamera != null) g.mainCamera.transform.rotation = Quaternion.Euler(0, float.Parse(a[1]), 0);
                break;
            case "timescale": Time.timeScale = float.Parse(a[1]); break;
            // film <job.json>: keyframed camera capture (Film.cs); filmstop; campose [name]: all camera poses as JSON
            case "film": Film.Start(a[1]); break;
            case "cockpitmap": FilmCockpit.DumpMap(); break;   // in the car, cockpit view: interior mapping + parts in car space
            case "filmstop": Film.Stop("filmstop"); break;
            case "campose": Film.DumpCameras(a.Length > 1 ? a[1] : null); break;
            case "scenes": FilmScout.Scenes(); break;
            case "filmlayers": FilmScout.Layers(); break;
            case "where": FilmScout.Where(a.Skip(1).ToArray()); break;
            case "findclear": FilmScout.FindClear(float.Parse(a[1]), float.Parse(a[2]), float.Parse(a[3]), float.Parse(a[4]), a.Length > 5 ? float.Parse(a[5]) : 20f, a.Length > 6 ? float.Parse(a[6]) : 120f); break;
            case "probe": FilmScout.Probe(float.Parse(a[1]), float.Parse(a[2]), a.Length > 3 ? float.Parse(a[3]) : 200f, a.Length > 4 ? float.Parse(a[4]) : 20f); break;
            case "probehits": FilmScout.ProbeHits(float.Parse(a[1]), float.Parse(a[2])); break;
            case "neon": FilmScout.Neon(a.Length > 1 ? float.Parse(a[1]) : 80f, a.Length > 2 ? int.Parse(a[2]) : 40); break;
            case "tpto":
                // tpto <x> <y> <z>: put the player's car there (districts load their streets around the player)
                {
                    var c = g.playerCar;
                    var to = new Vector3(float.Parse(a[1]), float.Parse(a[2]), float.Parse(a[3]));
                    var crb = c.GetComponent<Rigidbody>();
                    if (crb != null) { crb.velocity = Vector3.zero; crb.angularVelocity = Vector3.zero; crb.position = to; }
                    c.transform.position = to;
                    Plugin.L.LogInfo($"tpto: car at {Diag.V(to)}");
                }
                break;
            case "scene":
                // scene <build index>: the game's own district change (Player.ChangeScene), e.g. 7 = Level3 (Avalon Heights)
                if (Global.Instance != null && Global.Instance.player != null) { Plugin.L.LogInfo("scene: changing to " + a[1]); Global.Instance.player.ChangeScene(int.Parse(a[1])); }
                else Plugin.L.LogWarning("scene: no player");
                break;
            case "slice":FilmScout.Slice(float.Parse(a[1]), float.Parse(a[2]), float.Parse(a[3]), float.Parse(a[4]), a.Skip(5).Select(float.Parse).ToArray()); break;
            case "pathcheck": FilmScout.PathCheck(a[1], a.Length > 2 ? float.Parse(a[2]) : 0.8f); break;
            case "pad":
                // pad <InputControlType> <value> [seconds]
                pad.TryAttach();
                var control = Enum.Parse<InputControlType>(a[1], true);
                pad.SetOverride(control, float.Parse(a[2]));
                if (a.Length > 3) held.Add((control, now + float.Parse(a[3])));
                break;
            case "press":
                // press <InputControlType> [seconds]
                pad.TryAttach();
                var button = Enum.Parse<InputControlType>(a[1], true);
                pad.SetOverride(button, 1);
                held.Add((button, now + (a.Length > 2 ? float.Parse(a[2]) : 0.2f)));
                break;
            case "click":
                // click <GameObject path>: invoke a menu button the way a confirm press would
                var target = GameObject.Find(a[1]);
                var mb = target != null ? target.GetComponent<MenuButton>() : null;
                if (mb != null) mb.clickAction.Invoke();
                else if (target != null && target.GetComponent<UnityEngine.UI.Button>() is { } btn) btn.onClick.Invoke();
                else Plugin.L.LogWarning("click: nothing to click at " + a[1]);
                break;
            case "select":
                // select <GameObject path>: move UI selection (EventSystem) to an object
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(GameObject.Find(a[1]));
                break;
            case "uirt":
                // uirt <name>: save the VR UI render texture
                var tex = VRHost.UITexture;
                if (tex == null) { Plugin.L.LogWarning("uirt: no VR UI texture"); break; }
                var prev = RenderTexture.active;
                RenderTexture.active = tex;
                var t2d = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                t2d.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                t2d.Apply();
                RenderTexture.active = prev;
                Directory.CreateDirectory(shotDir);
                File.WriteAllBytes(Path.Combine(shotDir, a[1] + ".png"), ImageConversion.EncodeToPNG(t2d));
                UnityEngine.Object.Destroy(t2d);
                break;
            case "burst":
                // burst <name> <frames>: one screenshot per consecutive frame (flicker hunting)
                burstName = a[1];
                burstLeft = int.Parse(a[2]);
                burstIndex = 0;
                Directory.CreateDirectory(shotDir);
                break;
            case "shot":
                // shot <name> [both|left|right]: in VR the eye textures, not the desktop mirror
                Directory.CreateDirectory(shotDir);
                if (a.Length > 3 && UnityEngine.XR.XRSettings.enabled)
                    ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, a[1] + ".png"), int.Parse(a[3]), a[2] switch
                    {
                        "left" => ScreenCapture.StereoScreenCaptureMode.LeftEye,
                        "right" => ScreenCapture.StereoScreenCaptureMode.RightEye,
                        _ => ScreenCapture.StereoScreenCaptureMode.BothEyes,
                    });
                else if (a.Length > 2 && UnityEngine.XR.XRSettings.enabled)
                    ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, a[1] + ".png"), a[2] switch
                    {
                        "left" => ScreenCapture.StereoScreenCaptureMode.LeftEye,
                        "right" => ScreenCapture.StereoScreenCaptureMode.RightEye,
                        _ => ScreenCapture.StereoScreenCaptureMode.BothEyes,
                    });
                else ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, a[1] + ".png"));
                break;
            case "gopark": GoPark(g); break;
            case "snap": VRControls.SnapTurn(float.Parse(a[1])); break;
            case "inject": ActionInjector.Press(a[1]); break;
            case "fakehead":
                // fakehead <yaw> <pitch> [abs]: extra head rotation, or with "abs" the whole head rotation
                VRCameras.FakeHead = Quaternion.Euler(float.Parse(a[2]), float.Parse(a[1]), 0);
                VRCameras.FakeHeadAbsolute = a.Length > 3 && a[3] == "abs";
                break;
            // fakeheadpos <x> <y> <z>: the head stands this far from its calibrated spot (metres, tracking space)
            case "fakeheadpos": VRCameras.FakeHeadOffset = new Vector3(float.Parse(a[1]), float.Parse(a[2]), float.Parse(a[3])); break;
            case "tree":
                // tree <options|controls|menu|path> [depth]: UI hierarchy with components and texts
                Transform start = a[1] switch
                {
                    "options" => g.options.transform,
                    "controls" => g.controls.transform,
                    "menu" => g.menuCanvas.transform,
                    _ => GameObject.Find(a[1])?.transform,
                };
                var sbt = new System.Text.StringBuilder($"===== tree {a[1]} ({Diag.PathOf(start)}) =====\n");
                Tree(sbt, start, 0, a.Length > 2 ? int.Parse(a[2]) : 4);
                Plugin.L.LogInfo(sbt.ToString());
                break;
            case "uncap": uncapped = a[1] == "1"; break;
            case "carpose":
                // carpose <yaw>: pin the player's car facing a fixed direction (repeatable cockpit views for A/B runs)
                var body = g.playerCar.GetComponent<Rigidbody>();
                body.isKinematic = true;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                var pose = Quaternion.Euler(0, float.Parse(a[1]), 0);
                body.rotation = pose;
                g.playerCar.transform.rotation = pose;
                break;
            case "freeze":
                // freeze <0|1>: pin the player's car so benchmark runs see the same view
                var rb = g.playerCar.GetComponent<Rigidbody>();
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = a[1] == "1";
                break;
            case "bench":
                benchLabel = a.Length > 2 ? a[2] : "run";
                benchStart = now;
                benchUntil = now + float.Parse(a[1]);
                break;
            case "prof":
                // prof <seconds> <label> [threads] [with stacks]: sampling CPU profile, written to profiles/cpu-<label>.txt
                profileLabel = a.Length > 2 ? a[2] : "run";
                CpuSampler.Start(float.Parse(a[1]), profileLabel, a.Length > 3 ? int.Parse(a[3]) : 8, a.Length > 4 ? int.Parse(a[4]) : 8);
                break;
            case "gpu":
                // gpu <seconds> <label> [count draws 0|1]: GPU timeline per camera event, written to profiles/gpu-<label>.txt
                gpuLabel = a.Length > 2 ? a[2] : "run";
                GpuTimer.Start(float.Parse(a[1]), gpuLabel, a.Length > 3 && a[3] == "1");
                break;
            case "perf":
                // perf <minimap|mirror|rain|parking|rig> <0|1>
                if (a[1] == "rig") { Perf.TrimRig = a[2] == "1"; Perf.TrimVRCameraRig(); break; }
                if (a[1] == "sky") { Perf.MonoSky = a[2] == "1"; Perf.MonoSkyCamera(); break; }
                if (a[2] == "0") perf.Disabled.Add(a[1]); else perf.Disabled.Remove(a[1]);
                break;
            case "xref": Diag.DumpXref(a[1]); break;
            case "nativemap": NativeMap.Dump(a[1], a.Length > 2 ? a[2] : "^(Assembly-CSharp|Assembly-CSharp-firstpass|UnityEngine\\.CoreModule|UnityEngine\\.PhysicsModule|Cinemachine|InControl|UnityEngine\\.AIModule)$"); break;
            case "usedby": Diag.DumpUsedBy(a[1]); break;
            case "nopause":
                // nopause: stop the game pausing itself when its window loses focus (keeps unattended runs valid)
                foreach (var core in UnityEngine.Object.FindObjectsOfType<AppCoreControl>())
                {
                    Plugin.L.LogInfo($"AppCoreControl {Diag.PathOf(core.transform)}: pauseOnFocusLoss={core.pauseOnFocusLoss} frameRateLimit={core.frameRateLimit} " +
                                     $"vsync={core.enableVsync} maxQueuedFrames={core.maxQueuedFrames} aa={core.antialiasing} (QualitySettings.maxQueuedFrames={QualitySettings.maxQueuedFrames})");
                    core.pauseOnFocusLoss = false;
                }
                break;
            case "camera":
                // camera <GameObject path> <0|1>: switch a camera component (e.g. the skybox camera) for an A/B run
                var camObject = GameObject.Find(a[1]);
                var camera = camObject != null ? camObject.GetComponent<Camera>() : null;
                if (camera == null) { Plugin.L.LogWarning("camera: not found " + a[1]); break; }
                camera.enabled = a[2] == "1";
                Plugin.L.LogInfo($"camera {a[1]} enabled={camera.enabled} target={(camera.targetTexture != null ? camera.targetTexture.name : "-")} eye={camera.stereoTargetEye}");
                break;
            case "skydown":
                // skydown <n>: downsample factor of the sky texture the skybox camera renders for the city shaders
                foreach (var v in UnityEngine.Object.FindObjectsOfType<ViewToGlobalTexture>())
                {
                    v.downsample = int.Parse(a[1]);
                    var rt = v.skyboxRT;
                    Plugin.L.LogInfo($"skydown: {Diag.PathOf(v.transform)} downsample={v.downsample} rt={(rt != null ? $"{rt.width}x{rt.height}" : "-")}");
                }
                break;
            case "fullspeed": CpuTopology.SetFullSpeed(a[1] == "1"); break;
            case "pin": CpuTopology.PinEngineThreads(a[1] == "1"); break;
            case "physsync":
                // physsync <0|1>: Physics.autoSyncTransforms (1 = sync every changed transform before each physics query)
                Physics.autoSyncTransforms = a[1] == "1";
                Plugin.L.LogInfo($"physsync: autoSyncTransforms={Physics.autoSyncTransforms}");
                break;
            case "comps":
                // comps <TypeName> <0|1>: enable/disable every MonoBehaviour of a type in the loaded scenes (A/B cost)
                int switched = 0;
                if (a[2] == "1")
                {
                    if (switchedOff.TryGetValue(a[1], out var list))
                        foreach (var b in list) if (b != null) { b.enabled = true; switched++; }
                    switchedOff.Remove(a[1]);
                }
                else
                {
                    var list = switchedOff.TryGetValue(a[1], out var l) ? l : switchedOff[a[1]] = new List<MonoBehaviour>();
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                        if (b.enabled && b.GetIl2CppType().Name == a[1]) { b.enabled = false; list.Add(b); switched++; }
                }
                Plugin.L.LogInfo($"comps {a[1]} -> {a[2]}: {switched} components");
                break;
            case "comp":
                // comp <TypeName> <0|1>: toggle a component on the main camera (A/B cost measurement)
                foreach (var b in g.mainCamera.GetComponents<Behaviour>())
                    if (b.GetIl2CppType().Name == a[1]) b.enabled = a[2] == "1";
                break;
            case "hx":
                // hx <full|half|quarter> [samples]: volumetric lighting resolution on the main camera
                var hx = g.mainCamera.GetComponent<HxVolumetricCamera>();
                hx.resolution = Enum.Parse<HxVolumetricCamera.Resolution>(a[1], true);
                if (a.Length > 2) hx.SampleCount = int.Parse(a[2]);
                break;
            case "ppfx":
                // ppfx <EffectTypeName> <0|1>: switch a post-processing effect on all profiles
                int n = 0;
                foreach (var s in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessEffectSettings>())
                    if (s.GetIl2CppType().Name == a[1]) { s.active = a[2] == "1"; n++; }
                Plugin.L.LogInfo($"ppfx {a[1]} -> {a[2]} on {n} settings");
                break;
            case "effects":
                var on = new List<string>();
                foreach (var s in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessEffectSettings>())
                    if (s.active && s.enabled.value) on.Add($"{s.GetIl2CppType().Name}({s.name})");
                var h = g.mainCamera.GetComponent<HxVolumetricCamera>();
                Plugin.L.LogInfo($"effects: pp=[{string.Join(", ", on)}] hx res={h.resolution} samples={h.SampleCount} dirSamples={h.DirectionalSampleCount} " +
                                 $"blur={h.blurCount} upBlur={h.UpSampledblurCount} density={h.densityResolution} temporal={h.TemporalSampling}");
                break;
            case "shadows": QualitySettings.shadowDistance = float.Parse(a[1]); break;
            case "shadowres": QualitySettings.shadowResolution = Enum.Parse<ShadowResolution>(a[1], true); break;
            case "shadowq": QualitySettings.shadows = Enum.Parse<ShadowQuality>(a[1], true); break;
            case "far": VRCameras.FarClipOverride = float.Parse(a[1]); break;
            case "layercull":
                // layercull <dist> <layer>...: per-layer cull distance on the main camera (0 = far plane)
                var dists = g.mainCamera.layerCullDistances;
                foreach (var name in a.Skip(2)) dists[LayerMask.NameToLayer(name)] = float.Parse(a[1]);
                g.mainCamera.layerCullDistances = dists;
                break;
            case "cascades": QualitySettings.shadowCascades = int.Parse(a[1]); break;
            case "lodbias": QualitySettings.lodBias = float.Parse(a[1]); break;
            case "near":
                // near <layer> <radius>: renderers on a layer around the main camera, with shader and queue
                int layer = LayerMask.NameToLayer(a[1]);
                float radius = float.Parse(a[2]);
                var at = g.mainCamera.transform.position;
                int shown = 0;
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r.gameObject.layer != layer || !r.enabled) continue;
                    float d = Vector3.Distance(r.bounds.center, at);
                    if (d > radius) continue;
                    var mat = r.sharedMaterial;
                    Plugin.L.LogInfo($"near {a[1]}: {Diag.PathOf(r.transform)} d={d:F1} type={r.GetIl2CppType().Name} " +
                                     $"shader={(mat != null ? mat.shader.name : "-")} queue={(mat != null ? mat.renderQueue : -1)}");
                    if (++shown >= 40) break;
                }
                Plugin.L.LogInfo($"near {a[1]}: {shown} renderers within {radius}m");
                break;
            case "maskoff":
                // maskoff <layer name>...: drop layers from the main camera culling mask
                if (cullSaved == int.MinValue) cullSaved = g.mainCamera.cullingMask;
                int m = g.mainCamera.cullingMask;
                foreach (var name in a.Skip(1)) m &= ~(1 << LayerMask.NameToLayer(name));
                g.mainCamera.cullingMask = m;
                break;
            case "layers":
                // layers <GameObject path>: renderer layers under an object
                var root = GameObject.Find(a[1]);
                if (root == null) { Plugin.L.LogWarning("layers: not found " + a[1]); break; }
                var counts = new Dictionary<string, int>();
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    var key = $"{LayerMask.LayerToName(r.gameObject.layer)}{(r.enabled && r.gameObject.activeInHierarchy ? "" : "(off)")}";
                    counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                }
                Plugin.L.LogInfo($"layers {a[1]}: {string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
                break;
            case "eyescale":
                UnityEngine.XR.XRSettings.eyeTextureResolutionScale = float.Parse(a[1]);
                Plugin.L.LogInfo($"eye texture now {UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight}");
                break;
            case "cull":
                // cull <mask|-1>: main camera culling mask, 0 isolates non-rendering cost
                if (cullSaved == int.MinValue) cullSaved = g.mainCamera.cullingMask;
                g.mainCamera.cullingMask = a[1] == "restore" ? cullSaved : int.Parse(a[1]);
                break;
            case "pixellights": QualitySettings.pixelLightCount = int.Parse(a[1]); break;
            case "quality":
                Plugin.L.LogInfo($"quality level={QualitySettings.GetQualityLevel()} shadowDist={QualitySettings.shadowDistance} cascades={QualitySettings.shadowCascades} " +
                                 $"shadowRes={QualitySettings.shadowResolution} lodBias={QualitySettings.lodBias} pixelLights={QualitySettings.pixelLightCount} " +
                                 $"aa={QualitySettings.antiAliasing} aniso={QualitySettings.anisotropicFiltering} softParticles={QualitySettings.softParticles} " +
                                 $"realtimeProbes={QualitySettings.realtimeReflectionProbes} fixedDt={Time.fixedDeltaTime} path={g.mainCamera.actualRenderingPath}");
                break;
            case "inspect":
                // inspect <path> [TypeName|*] [depth] [memberFilter]: fields of components on an object
                Inspect.Components(a[1], a.Length > 2 ? a[2] : null, a.Length > 3 ? int.Parse(a[3]) : 1, a.Length > 4 ? a[4] : "");
                break;
            case "mats": Inspect.Materials(a[1], a.Length > 2 ? int.Parse(a[2]) : 99); break;
            case "setmember": Inspect.Set(a[1], a[2], a[3], a[4]); break;
            case "findmember": Inspect.FindMember(a[1]); break;
            case "drips": VRRain.KeepDrips = a[1] == "1"; break;
            case "rain3d":
                // rain3d <0|1> [speed length width opacity]: the 3D rain instead of the screen-space rain effects
                VRRain.Enabled = a[1] == "1";
                if (a.Length > 5) { VRRain.FallSpeed = float.Parse(a[2]); VRRain.StreakLength = float.Parse(a[3]); VRRain.StreakWidth = float.Parse(a[4]); VRRain.Opacity = float.Parse(a[5]); }
                break;
            case "hxset":
                // hxset <density> [mie] [extinction] [ambientStrength]: Hx volumetric light settings on the main camera
                {
                    foreach (var hxc in UnityEngine.Object.FindObjectsOfType<HxVolumetricCamera>())
                    {
                        hxc.Density = float.Parse(a[1]);
                        if (a.Length > 2) hxc.MieScattering = float.Parse(a[2]);
                        if (a.Length > 3) hxc.Extinction = float.Parse(a[3]);
                        if (a.Length > 4) hxc.AmbientLightingStrength = float.Parse(a[4]);
                        Plugin.L.LogInfo($"hxset: {hxc.name} density={hxc.Density} mie={hxc.MieScattering} extinction={hxc.Extinction} ambient={hxc.AmbientLightingStrength}");
                    }
                }
                break;
            case "findname":
                // findname <substring>: active objects whose name contains it, with components, renderer material and distance
                {
                    var cp = g.mainCamera.transform.position;
                    int found = 0;
                    foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>())
                    {
                        if (tr.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0 || !tr.gameObject.activeInHierarchy) continue;
                        var comps = string.Join(",", tr.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(nm => nm != "Transform"));
                        var rr = tr.GetComponent<Renderer>();
                        Plugin.L.LogInfo($"findname: {Diag.PathOf(tr)} d={Vector3.Distance(cp, tr.position):F0} scale={Diag.V(tr.lossyScale)} comps=[{comps}] " +
                                         $"mat={(rr != null && rr.sharedMaterial != null ? rr.sharedMaterial.name + "/" + rr.sharedMaterial.shader.name : "-")} layer={LayerMask.LayerToName(tr.gameObject.layer)} " +
                                         $"onCam={tr.IsChildOf(g.mainCamera.transform)}");
                        if (++found >= 40) break;
                    }
                    Plugin.L.LogInfo($"findname: {found} for '{a[1]}'");
                }
                break;
            case "padinfo": PadInfoFrames = int.Parse(a[1]); break;
            case "particles":
                // particles <radius>: particle systems near the camera or riding on it / the player, with what draws them
                {
                    float rad = float.Parse(a[1]);
                    var cp = g.mainCamera.transform.position;
                    int listed = 0;
                    foreach (var ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
                    {
                        var tr = ps.transform;
                        bool onCam = tr.IsChildOf(g.mainCamera.transform);
                        bool onPlayer = g.player != null && tr.IsChildOf(g.player.transform);
                        float dist = Vector3.Distance(cp, tr.position);
                        if (!onCam && !onPlayer && dist > rad) continue;
                        if (ps.particleCount == 0 && !ps.isPlaying) continue;
                        var r = ps.GetComponent<ParticleSystemRenderer>();
                        Plugin.L.LogInfo($"particles: {Diag.PathOf(tr)} d={dist:F0} cam={onCam} player={onPlayer} playing={ps.isPlaying} count={ps.particleCount} " +
                                         $"mode={(r != null ? r.renderMode.ToString() : "-")} mat={(r != null && r.sharedMaterial != null ? r.sharedMaterial.name + "/" + r.sharedMaterial.shader.name : "-")} layer={LayerMask.LayerToName(ps.gameObject.layer)}");
                        if (++listed >= 60) break;
                    }
                    Plugin.L.LogInfo($"particles: {listed} listed within {rad} m");
                }
                break;
            case "layerstats":
                // layerstats <min> <max>: enabled, visible renderers per layer whose bounds centre is this far from the camera
                {
                    float lo = float.Parse(a[1]), hi = float.Parse(a[2]);
                    var cp = g.mainCamera.transform.position;
                    var layerCounts = new Dictionary<string, int>();
                    int lodded = 0, total = 0;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    {
                        if (!r.enabled || !r.isVisible) continue;
                        float dd = Vector3.Distance(cp, r.bounds.center);
                        if (dd < lo || dd >= hi) continue;
                        total++;
                        var ln = LayerMask.LayerToName(r.gameObject.layer);
                        layerCounts[ln] = layerCounts.TryGetValue(ln, out var c0) ? c0 + 1 : 1;
                        if (r.GetComponentInParent<LODGroup>() != null) lodded++;
                    }
                    Plugin.L.LogInfo($"layerstats {lo}-{hi} m: {total} visible renderers ({lodded} under a LOD group): " +
                                     string.Join(", ", layerCounts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value)));
                }
                break;
            case "lodinfo":
                {
                    var groups = UnityEngine.Object.FindObjectsOfType<LODGroup>();
                    var byCount = new Dictionary<int, int>();
                    foreach (var lg in groups) byCount[lg.lodCount] = byCount.TryGetValue(lg.lodCount, out var c1) ? c1 + 1 : 1;
                    Plugin.L.LogInfo($"lod: {groups.Length} LOD groups ({string.Join(", ", byCount.Select(kv => kv.Value + "x" + kv.Key + " levels"))}) lodBias={QualitySettings.lodBias} maxLOD={QualitySettings.maximumLODLevel}");
                }
                break;
            case "atmoinfo":
                // atmoinfo: ambient light, reflection, Hx global fog settings, rain particle systems and rain post effects
                {
                    Plugin.L.LogInfo($"atmo: ambient mode={RenderSettings.ambientMode} light={RenderSettings.ambientLight} sky={RenderSettings.ambientSkyColor} equator={RenderSettings.ambientEquatorColor} " +
                                     $"ground={RenderSettings.ambientGroundColor} intensity={RenderSettings.ambientIntensity} reflection={RenderSettings.reflectionIntensity} skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "-")} sun={(RenderSettings.sun != null ? RenderSettings.sun.name + " " + RenderSettings.sun.intensity : "-")}");
                    var hxc = g.mainCamera.GetComponent<HxVolumetricCamera>();
                    if (hxc != null)
                        Plugin.L.LogInfo($"atmo: hx density={hxc.Density} extinction={hxc.Extinction} extEffect={hxc.ExtinctionEffect} mie={hxc.MieScattering} ambientStrength={hxc.AmbientLightingStrength} ambient={hxc.Ambient} " +
                                         $"ambSky={hxc.AmbientSky} ambEq={hxc.AmbientEquator} ambGround={hxc.AmbientGround} ambI={hxc.AmbientIntensity} fogHeight={hxc.FogHeightEnabled}/{hxc.FogHeight}/{hxc.FogTransitionSize} " +
                                         $"noise={hxc.NoiseEnabled} samples={hxc.SampleCount}/{hxc.DirectionalSampleCount} maxLight={hxc.MaxLightDistance} maxDir={hxc.MaxDirectionalRayDistance} res={hxc.resolution} blur={hxc.blurCount}/{hxc.UpSampledblurCount} " +
                                         $"particles={hxc.ParticleDensitySupport} tint={hxc.TintMode} lights={HxVolumetricCamera.ActiveLights?.Count}");
                    foreach (var ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
                    {
                        var pn = Diag.PathOf(ps.transform);
                        if (pn.IndexOf("rain", StringComparison.OrdinalIgnoreCase) < 0 && pn.IndexOf("drop", StringComparison.OrdinalIgnoreCase) < 0 && pn.IndexOf("splash", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var r = ps.GetComponent<ParticleSystemRenderer>();
                        bool onCam = ps.transform.IsChildOf(g.mainCamera.transform);
                        Plugin.L.LogInfo($"atmo: rain {pn} playing={ps.isPlaying} count={ps.particleCount} onMainCamera={onCam} pos={Diag.V(ps.transform.position)} " +
                                         $"mode={(r != null ? r.renderMode.ToString() : "-")} mat={(r != null && r.sharedMaterial != null ? r.sharedMaterial.name + "/" + r.sharedMaterial.shader.name : "-")} layer={LayerMask.LayerToName(ps.gameObject.layer)}");
                    }
                }
                break;
            case "fogcfg":
                // fogcfg <distanceMul> <densityMul>: the global fog on top of the draw-distance scaling
                VRFarView.FogDistanceMul = float.Parse(a[1]); VRFarView.FogDensityMul = float.Parse(a[2]);
                break;
            case "altcfg":
                // altcfg <distance> <top> <bottom> <scale> [band]: cockpit placement of the altitude warning
                VRAltitude.CockpitDistance = float.Parse(a[1]); VRAltitude.CockpitTop = float.Parse(a[2]);
                VRAltitude.CockpitBottom = float.Parse(a[3]); VRAltitude.CockpitScale = float.Parse(a[4]);
                if (a.Length > 5) VRAltitude.Band = float.Parse(a[5]);
                break;
            case "carinfo":
                // carinfo [frames]: height against the layer limits, vertical motion and inputs
                CarInfoFrames = a.Length > 1 ? int.Parse(a[1]) : 1;
                break;
            case "handcaltest":
                // handcaltest <pitch> <yaw> <roll>: controllers posed as a hand with that grip would hold them palm to
                // palm (head turned 30°, hands tilted), then the solver; it should give the grip back
                {
                    var truth = new Vector3(float.Parse(a[1]), float.Parse(a[2]), float.Parse(a[3]));
                    var headRot = Quaternion.Euler(10, 30, 0);
                    var ahead = Vector3.ProjectOnPlane(headRot * Vector3.forward, Vector3.up).normalized;
                    var right = Vector3.Cross(Vector3.up, ahead);
                    var lp = -right * 0.15f + Vector3.up * 0.02f; var rp = right * 0.15f;
                    Quaternion Pose(float s)
                    {
                        var across = rp - lp;
                        var back = (s < 0 ? -across : across).normalized;
                        var fingers = Vector3.ProjectOnPlane(ahead, back).normalized;
                        var want = Quaternion.LookRotation(fingers, Vector3.Cross(fingers, back * s));
                        return want * Quaternion.Inverse(Quaternion.Euler(truth.x, truth.y * s, truth.z * s));
                    }
                    VRControls.SolveGrip(lp, Pose(-1), rp, Pose(1), headRot, out var got, out var rep);
                    Plugin.L.LogInfo($"handcaltest: truth {truth} -> solved {got} ({rep})");
                }
                break;
            case "stereofx":
                // stereofx sky <0|1>: A/B the per-eye sky texture
                VRStereoFx.SkyPerEye = a[2] == "1";
                break;
            case "hxmatrices":
                // hxmatrices: log the projection/view matrices Hx's buffers set, at each eye's pre- and post-render
                VRStereoFx.HxLogFrame = Time.frameCount;
                break;
            case "ptime": PluginTiming.Start(float.Parse(a[1])); break;
            case "frametiming": VRFrameTiming.Requested = true; VRFrameTiming.Report(); break;
            case "res": Resolution.Apply(int.Parse(a[1])); break;
            case "every": perf.SetEvery(a[1], int.Parse(a[2])); break;
            case "interiorpath":
                // interiorpath <forward|deferred|usegraphicssettings>: rendering path of the cockpit interior camera
            {
                var mcvr = g.mainCamera.GetComponent<RedAlliance.Util.MainCameraVehicleRenderer>();
                var ic = mcvr != null ? mcvr.interiorCamera : null;
                if (ic == null) { Plugin.L.LogWarning("interiorpath: no interior camera"); break; }
                ic.renderingPath = a[1] switch { "forward" => RenderingPath.Forward, "deferred" => RenderingPath.DeferredShading, _ => RenderingPath.UsePlayerSettings };
                Plugin.L.LogInfo($"interiorpath: {Diag.PathOf(ic.transform)} -> {ic.renderingPath} (actual {ic.actualRenderingPath})");
                break;
            }
            case "trailinfo":
                // trailinfo: who owns the trail renderers, how long they are, how far away
            {
                var eyeAt = g.mainCamera.transform.position;
                var byRoot = new Dictionary<string, (int n, int vis, int pts, float near)>();
                foreach (var tr in UnityEngine.Object.FindObjectsOfType<TrailRenderer>())
                {
                    string key = tr.transform.root.name + " / " + tr.gameObject.name + $" t={tr.time:F1} minV={tr.minVertexDistance:F2} layer={LayerMask.LayerToName(tr.gameObject.layer)} align={tr.alignment}";
                    float d = Vector3.Distance(eyeAt, tr.transform.position);
                    (int n, int vis, int pts, float near) v = byRoot.TryGetValue(key, out var x) ? x : (0, 0, 0, 1e9f);
                    byRoot[key] = (v.n + 1, v.vis + (tr.isVisible ? 1 : 0), v.pts + tr.positionCount, Mathf.Min(v.near, d));
                }
                foreach (var kv in byRoot.OrderByDescending(kv => kv.Value.n).Take(15))
                    Plugin.L.LogInfo($"trails: {kv.Value.n} x {kv.Key}: {kv.Value.vis} visible, {kv.Value.pts} points, nearest {kv.Value.near:F0} m");
                break;
            }
            case "trails":
                // trails <0|1>: switch every trail renderer (upper bound of what trails cost)
            {
                int nt = 0;
                foreach (var tr in UnityEngine.Object.FindObjectsOfType<TrailRenderer>()) { tr.enabled = a[1] == "1"; nt++; }
                Plugin.L.LogInfo($"trails: {nt} -> {a[1]}");
                break;
            }
            case "hxeyes":
                // hxeyes <0|1>: A/B Hx built per eye (VRHx)
                VRHx.Enabled = a[1] == "1";
                break;
            case "hxhist": VRHx.OwnRightHistory = a[1] == "1"; break;
            case "hxcapture":
                // hxcapture <name>: Hx's volumetric buffer after each eye, same frame -> <name>_L.png / <name>_R.png
                {
                    string nm = a[1];
                    VRStereoFx.CaptureWhich = a.Length > 2 ? a[2] : "vol";
                    VRStereoFx.CaptureFrame = Time.frameCount;
                    PendingCapture = () =>
                    {
                        if (VRStereoFx.CaptureWhich == "temporal") VRStereoFx.Grab(g.mainCamera, 1, true);
                        for (int e = 0; e < 2; e++)
                        {
                            var rt = VRStereoFx.Captured[e];
                            if (rt == null) { Plugin.L.LogWarning("hxcapture: nothing for eye " + e); continue; }
                            var prevA = RenderTexture.active;
                            RenderTexture.active = rt;
                            var t2 = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                            t2.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                            t2.Apply();
                            RenderTexture.active = prevA;
                            File.WriteAllBytes(Path.Combine(shotDir, nm + (e == 0 ? "_L" : "_R") + ".png"), ImageConversion.EncodeToPNG(t2));
                            UnityEngine.Object.Destroy(t2);
                        }
                        Plugin.L.LogInfo("hxcapture: " + nm);
                    };
                }
                break;
            case "typeinfo": Inspect.TypeInfo(a[1]); break;
            case "fogglobals":
                // fogglobals: WhiteCat GlobalFog's shader globals (its FloatParameter values do not marshal)
                foreach (var gf in Resources.FindObjectsOfTypeAll<WhiteCat.Rendering.PostProcessing.GlobalFog>())
                {
                    if (!gf.active) continue;
                    Plugin.L.LogInfo($"globalfog '{gf.name}': density={Shader.GetGlobalFloat(WhiteCat.Rendering.PostProcessing.GlobalFog._densityID)} transmittance={Shader.GetGlobalFloat(WhiteCat.Rendering.PostProcessing.GlobalFog._transmittanceID)} " +
                                     $"skyAtt={Shader.GetGlobalFloat(WhiteCat.Rendering.PostProcessing.GlobalFog._skyAttenuationID)} altitude={Shader.GetGlobalFloat(WhiteCat.Rendering.PostProcessing.GlobalFog._altitudeID)} height={Shader.GetGlobalFloat(WhiteCat.Rendering.PostProcessing.GlobalFog._heightID)} " +
                                     $"near={Shader.GetGlobalFloat(WhiteCat.Rendering.PostProcessing.GlobalFog._fogNearID)} far={Shader.GetGlobalFloat(WhiteCat.Rendering.PostProcessing.GlobalFog._fogFarID)} color={Shader.GetGlobalColor(WhiteCat.Rendering.PostProcessing.GlobalFog._colorID)} " +
                                     $"ids={WhiteCat.Rendering.PostProcessing.GlobalFog._densityID},{WhiteCat.Rendering.PostProcessing.GlobalFog._transmittanceID},{WhiteCat.Rendering.PostProcessing.GlobalFog._fogNearID},{WhiteCat.Rendering.PostProcessing.GlobalFog._fogFarID}");
                    break;
                }
                break;
            case "xrinfo":
                {
                    var mc = g.mainCamera;
                    string P(Camera.StereoscopicEye e) { var m = mc.GetStereoProjectionMatrix(e); return $"m00={m.m00:F3} m02={m.m02:F3} m11={m.m11:F3} m12={m.m12:F3}"; }
                    string V(Camera.StereoscopicEye e) { var m = mc.GetStereoViewMatrix(e) * mc.cameraToWorldMatrix; return $"eyeOffset=({m.m03:F3},{m.m13:F3},{m.m23:F3})"; }
                    Plugin.L.LogInfo($"xr: mode={UnityEngine.XR.XRSettings.stereoRenderingMode} eyeTex={UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight} " +
                                     $"dim={UnityEngine.XR.XRSettings.eyeTextureDesc.dimension} vrUsage={UnityEngine.XR.XRSettings.eyeTextureDesc.vrUsage} stereoEnabled={mc.stereoEnabled} sep={mc.stereoSeparation:F3} " +
                                     $"| L {P(Camera.StereoscopicEye.Left)} {V(Camera.StereoscopicEye.Left)} | R {P(Camera.StereoscopicEye.Right)} {V(Camera.StereoscopicEye.Right)}");
                }
                break;
            case "eyelog":
                // eyelog <frames>: log each main-camera OnPreRender/OnPostRender with the active eye
                EyeLogFrames = int.Parse(a[1]);
                break;
            // fogtest <off|Linear|Exponential|ExponentialSquared> [density]: fog mode on top of the far view (needs DrawDistance > 600)
            case "fogtest":
                if (a[1] == "off") VRFarView.TestMode = null;
                else { VRFarView.TestMode = (FogMode)Enum.Parse(typeof(FogMode), a[1]); VRFarView.TestDensity = float.Parse(a[2]); }
                break;
            // fakestereo <ipd metres> <asym 0|1>: real eye spacing and Quest-like asymmetric frusta on the null headset
            case "fakestereo": VRCameras.FakeIpd = float.Parse(a[1]); VRCameras.FakeAsym = a[2] == "1"; break;
            case "lightinfo":
                {
                    int flares = 0, halos = 0, lights = 0;
                    var kinds = new Dictionary<string, int>();
                    foreach (var lo in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Light>()))
                    {
                        var l = lo.Cast<Light>();
                        lights++;
                        if (l.flare != null) flares++;
                        foreach (var c in l.GetComponents<Behaviour>())
                        {
                            var kn = c.GetIl2CppType().Name;
                            if (kn == "Light") continue;
                            kinds[kn] = kinds.TryGetValue(kn, out var k) ? k + 1 : 1;
                        }
                    }
                    int lensFlares = -1;
                    Plugin.L.LogInfo($"lights: {lights} with flare={flares} LensFlare components={lensFlares} other components on lights: " +
                                     string.Join(", ", kinds.Select(kv => kv.Key + "=" + kv.Value)));
                }
                break;
            case "foginfo":
                // foginfo: Unity fog, main camera range and culling, LOD
                {
                    var mc = g.mainCamera;
                    var lcd = mc.layerCullDistances;
                    var culled = new List<string>();
                    for (int i = 0; i < 32; i++) if (lcd[i] > 0) culled.Add($"{LayerMask.LayerToName(i)}={lcd[i]:F0}");
                    Plugin.L.LogInfo($"fog: unity fog={RenderSettings.fog} mode={RenderSettings.fogMode} start={RenderSettings.fogStartDistance} end={RenderSettings.fogEndDistance} " +
                                     $"density={RenderSettings.fogDensity} color={RenderSettings.fogColor} | main far={mc.farClipPlane} sphericalCull={mc.layerCullSpherical} " +
                                     $"layerCull=[{string.Join(" ", culled)}] lodBias={QualitySettings.lodBias} maxLOD={QualitySettings.maximumLODLevel}");
                }
                break;
            case "cams":
                // cams: every camera with what it renders into and how it clears
                foreach (var cc in Camera.allCameras)
                    Plugin.L.LogInfo($"cam: {Diag.PathOf(cc.transform)} depth={cc.depth} clear={cc.clearFlags} bg={cc.backgroundColor} rt={(cc.targetTexture != null ? cc.targetTexture.name + " " + cc.targetTexture.width + "x" + cc.targetTexture.height : "-")} " +
                                     $"eye={cc.stereoTargetEye} mask=0x{cc.cullingMask:X} ortho={cc.orthographic} size={cc.orthographicSize:F0} enabled={cc.enabled}");
                break;
            case "matprops": Inspect.MaterialProps(a[1], a.Skip(2)); break;
            case "find": Inspect.Find(a[1], a.Length > 2 ? int.Parse(a[2]) : 20); break;
            case "ppdump":
                // ppdump [EffectTypeName]: every post-processing effect setting with its parameters
                foreach (var s in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessEffectSettings>())
                {
                    var tn = s.GetIl2CppType().Name;
                    if (a.Length > 1 && tn != a[1]) continue;
                    Plugin.L.LogInfo($"pp {tn} in '{s.name}' active={s.active} enabled={s.enabled.value}: {Inspect.Obj(s, 1)}");
                }
                break;
            case "eyes":
                // eyes <both|left|right>: what the desktop window mirrors (both = side by side, for stereo screenshots)
                UnityEngine.XR.XRSettings.gameViewRenderMode = a[1] switch
                {
                    "both" => UnityEngine.XR.GameViewRenderMode.BothEyes,
                    "right" => UnityEngine.XR.GameViewRenderMode.RightEye,
                    _ => UnityEngine.XR.GameViewRenderMode.LeftEye,
                };
                break;
            case "quit": Application.Quit(); break;
            default: Plugin.L.LogWarning("unknown cmd " + a[0]); break;
        }
    }
}
