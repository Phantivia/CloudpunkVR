using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Cinemachine;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.XrefScans;
using InControl;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace CloudpunkVR;

// Read-only dumps of the game's camera/UI/input setup, used to reverse-engineer what the VR layer has to drive.
internal static class Diag
{
    internal static string PathOf(Transform t)
    {
        if (t == null) return "null";
        var parts = new List<string>();
        for (; t != null; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    internal static string Name(UnityEngine.Object o)
    {
        try { return o == null ? "null" : o.name; } catch { return "dead"; }
    }

    internal static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

    static string Mask(int mask)
    {
        if (mask == -1) return "Everything";
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0)
            {
                var n = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrEmpty(n) ? i.ToString() : n);
            }
        return string.Join("|", names);
    }

    internal static string StateLine()
    {
        string scene = SceneManager.GetActiveScene().name;
        var g = Global.Instance;
        if (g == null) return $"scene={scene} global=null";
        string vcam = "-";
        try
        {
            var brain = g.cameraBrain;
            var a = brain != null ? brain.ActiveVirtualCamera : null;
            if (a != null) vcam = a.Name;
        }
        catch { vcam = "?"; }
        return $"scene={scene} inVehicle={Global.IsInVehicle} cockpit={Global.IsInVehicleCockpit} menu={g.IsInMenu()} vcam={vcam}";
    }

    internal static void DumpAll(string why)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"===== CloudpunkVR dump ({why}) frame={Time.frameCount} =====");
        Section(sb, "xr", DumpXR);
        Section(sb, "game", DumpGame);
        Section(sb, "cameras", DumpCameras);
        Section(sb, "vcams", DumpVcams);
        Section(sb, "canvases", DumpCanvases);
        if (why == "F9") Section(sb, "bindings", DumpBindings);
        Plugin.L.LogInfo(sb.ToString());
    }

    // What is loaded and how much of it is live, for performance work.
    internal static void DumpCensus(string why)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"===== census ({why}) frame={Time.frameCount} {StateLine()} =====");
        Section(sb, "renderers", CensusRenderers);
        Section(sb, "lights and probes", CensusLights);
        Section(sb, "particles, animation, physics, audio", CensusSimulation);
        Section(sb, "scripts (enabled MonoBehaviours by type)", CensusScripts);
        Section(sb, "modules outside Windows", s =>
        {
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                if (!m.FileName.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                    s.AppendLine($"  {m.FileName}");
        });
        Plugin.L.LogInfo(sb.ToString());
    }

    static void Bump(Dictionary<string, int> d, string key, int by = 1) => d[key] = d.TryGetValue(key, out int v) ? v + by : by;

    static string Top(Dictionary<string, int> d, int n) =>
        string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(n).Select(kv => $"{kv.Key}={kv.Value}"));

    static void CensusRenderers(StringBuilder sb)
    {
        var total = new Dictionary<string, int>();
        var enabled = new Dictionary<string, int>();
        var visible = new Dictionary<string, int>();
        var casters = new Dictionary<string, int>();
        var visibleShaders = new Dictionary<string, int>();
        var visibleLayers = new Dictionary<string, int>();
        var materials = new HashSet<IntPtr>();
        long visibleVerts = 0;
        int offscreenSkinned = 0;
        foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            string t = r.GetIl2CppType().Name;
            Bump(total, t);
            if (!r.enabled) continue;
            Bump(enabled, t);
            if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) Bump(casters, t);
            var smr = r.TryCast<SkinnedMeshRenderer>();
            if (smr != null && smr.updateWhenOffscreen) offscreenSkinned++;
            if (!r.isVisible) continue;
            Bump(visible, t);
            Bump(visibleLayers, LayerMask.LayerToName(r.gameObject.layer));
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                materials.Add(m.Pointer);
                Bump(visibleShaders, m.shader != null ? m.shader.name : "?");
            }
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) visibleVerts += mf.sharedMesh.vertexCount;
            else if (smr != null && smr.sharedMesh != null) visibleVerts += smr.sharedMesh.vertexCount;
        }
        foreach (var kv in total.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {kv.Key}: {kv.Value} loaded, {enabled.GetValueOrDefault(kv.Key)} enabled, {visible.GetValueOrDefault(kv.Key)} visible (any camera or shadow), " +
                          $"{casters.GetValueOrDefault(kv.Key)} cast shadows");
        sb.AppendLine($"  visible: {visible.Values.Sum()} renderers, {materials.Count} distinct materials, {visibleVerts / 1000}k mesh vertices; skinned updateWhenOffscreen={offscreenSkinned}");
        sb.AppendLine($"  visible by layer: {Top(visibleLayers, 20)}");
        sb.AppendLine($"  visible material slots by shader: {Top(visibleShaders, 25)}");
        sb.AppendLine($"  LODGroups={UnityEngine.Object.FindObjectsOfType<LODGroup>().Length}");
    }

    static void CensusLights(StringBuilder sb)
    {
        var kinds = new Dictionary<string, int>();
        foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
        {
            if (!l.isActiveAndEnabled) { Bump(kinds, "disabled"); continue; }
            Bump(kinds, $"{l.type}/{(l.shadows == LightShadows.None ? "noshadow" : "SHADOW")}/{l.renderMode}");
            if (l.shadows != LightShadows.None)
                sb.AppendLine($"  shadowed: {PathOf(l.transform)} {l.type} range={l.range:F0} res={l.shadowResolution} strength={l.shadowStrength:F2} mask={Mask(l.cullingMask)}");
        }
        sb.AppendLine($"  lights: {Top(kinds, 20)}");
        foreach (var p in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>())
            if (p.isActiveAndEnabled && p.mode != UnityEngine.Rendering.ReflectionProbeMode.Baked)
                sb.AppendLine($"  probe: {PathOf(p.transform)} mode={p.mode} refresh={p.refreshMode} slicing={p.timeSlicingMode} res={p.resolution}");
        sb.AppendLine($"  reflection probes: {UnityEngine.Object.FindObjectsOfType<ReflectionProbe>().Length} loaded; realtime probes listed above (quality realtimeReflectionProbes={QualitySettings.realtimeReflectionProbes})");
        var sky = RenderSettings.skybox;
        sb.AppendLine($"  skybox material={Name(sky)} shader={(sky != null && sky.shader != null ? sky.shader.name : "-")}");
        foreach (var v in UnityEngine.Object.FindObjectsOfType<ViewToGlobalTexture>())
        {
            var rt = v.skyboxRT;
            sb.AppendLine($"  sky texture {PathOf(v.transform)}: enabled={v.enabled} global={v.globalTextureName} downsample={v.downsample} " +
                          $"rt={(rt != null ? $"{rt.width}x{rt.height} {rt.format}" : "-")} skycam={PathOf(v.skycam != null ? v.skycam.transform : null)}");
        }
    }

    static void CensusSimulation(StringBuilder sb)
    {
        var ps = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
        int playing = 0, particles = 0;
        foreach (var p in ps) if (p.isPlaying) { playing++; particles += p.particleCount; }
        sb.AppendLine($"  particle systems: {ps.Length} loaded, {playing} playing, {particles} live particles");
        var cull = new Dictionary<string, int>();
        foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>())
            Bump(cull, a.isActiveAndEnabled ? a.cullingMode.ToString() : "disabled");
        sb.AppendLine($"  animators: {Top(cull, 5)}; legacy Animation={UnityEngine.Object.FindObjectsOfType<Animation>().Length}");
        var bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
        int dynamicBodies = 0, awake = 0;
        foreach (var b in bodies) if (!b.isKinematic) { dynamicBodies++; if (!b.IsSleeping()) awake++; }
        var colliders = new Dictionary<string, int>();
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>()) if (c.enabled) Bump(colliders, c.GetIl2CppType().Name);
        sb.AppendLine($"  rigidbodies: {bodies.Length} ({dynamicBodies} dynamic, {awake} awake); colliders: {Top(colliders, 8)}; fixedDeltaTime={Time.fixedDeltaTime} " +
                      $"maxDelta={Time.maximumDeltaTime} solverIterations={Physics.defaultSolverIterations}");
        int audioPlaying = 0;
        var sources = UnityEngine.Object.FindObjectsOfType<AudioSource>();
        foreach (var s in sources) if (s.isPlaying) audioPlaying++;
        sb.AppendLine($"  audio sources: {sources.Length} loaded, {audioPlaying} playing; canvases={UnityEngine.Object.FindObjectsOfType<Canvas>().Length}");
    }

    static void CensusScripts(StringBuilder sb)
    {
        var counts = new Dictionary<string, int>();
        int all = 0;
        foreach (var m in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (!m.enabled) continue;
            all++;
            Bump(counts, m.GetIl2CppType().FullName);
        }
        sb.AppendLine($"  {all} enabled on active objects, {counts.Count} types");
        foreach (var kv in counts.OrderByDescending(kv => kv.Value).Take(45))
            sb.AppendLine($"    {kv.Value,6}  {kv.Key}");
    }

    static void Section(StringBuilder sb, string name, Action<StringBuilder> f)
    {
        sb.AppendLine("--- " + name);
        try { f(sb); }
        catch (Exception e) { sb.AppendLine("  !! " + e); }
    }

    static void DumpXR(StringBuilder sb)
    {
        sb.AppendLine($"  enabled={XRSettings.enabled} device={XRSettings.loadedDeviceName} active={XRSettings.isDeviceActive} " +
                      $"model={XRDevice.model} hz={XRDevice.refreshRate} mode={XRSettings.stereoRenderingMode} " +
                      $"eye={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} scale={XRSettings.eyeTextureResolutionScale} " +
                      $"space={XRDevice.GetTrackingSpaceType()}");
        sb.AppendLine($"  head={V(InputTracking.GetLocalPosition(XRNode.CenterEye))} rot={V(InputTracking.GetLocalRotation(XRNode.CenterEye).eulerAngles)}");
        sb.AppendLine($"  screen={Screen.width}x{Screen.height} vSync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate} " +
                      $"quality={QualitySettings.GetQualityLevel()} timeScale={Time.timeScale}");
    }

    static void DumpGame(StringBuilder sb)
    {
        var g = Global.Instance;
        if (g == null) { sb.AppendLine("  Global.Instance null"); return; }
        sb.AppendLine($"  mainCamera={Name(g.mainCamera)} uiCamera={Name(g.uiCamera)} brain={Name(g.cameraBrain)} " +
                      $"inVehicle={Global.IsInVehicle} cockpit={Global.IsInVehicleCockpit} menu={g.IsInMenu()} dialog={g.IsInDialog(false)} " +
                      $"menuCamera={PathOf(g.menuCamera)} uiParent={PathOf(g.uiParent != null ? g.uiParent.transform : null)}");
        var p = g.player;
        if (p != null)
            sb.AppendLine($"  player active={p.gameObject.activeInHierarchy} pos={V(p.transform.position)} FreeLook={p.FreeLook} locked={p.cameraLocked} " +
                          $"fp={Name(p.firstPersonCamera)} fixed={Name(p.fixedCamera)} free={Name(p.freeLookCamera)} mainCamera={PathOf(p.mainCamera)} " +
                          $"cc.height={(p.charController != null ? p.charController.height : -1):F2} cc.center={(p.charController != null ? V(p.charController.center) : "-")} " +
                          $"scale={V(p.transform.lossyScale)} alignment={V(p.alignment.eulerAngles)} input={V(p.inputVector)}");
        var c = g.playerCar;
        if (c != null)
            sb.AppendLine($"  car active={c.gameObject.activeInHierarchy} pos={V(c.transform.position)} rot={V(c.transform.eulerAngles)} cockpit={c.isInVehicleCockpit} " +
                          $"FreeLook={c.FreeLook} fps={Name(c.fpsCamera)} freeLook={Name(c.freeLookCamera)} interiorPos={PathOf(c.interiorPosition)} " +
                          $"speed={c.currentSpeed:F1} gas={c.gasInput:F2} steer={c.steerInput:F2} strafe={c.strafeInput:F2} vert={c.verticalSpeed:F2}");
        var vi = g.vehicleInterior;
        if (vi != null)
            sb.AppendLine($"  interior active={vi.gameObject.activeInHierarchy} pos={V(vi.transform.position)} camCockpit={PathOf(vi.cameraCockpit != null ? vi.cameraCockpit.transform : null)} " +
                          $"camEnabled={(vi.cameraCockpit != null && vi.cameraCockpit.enabled)} interiorLocal={PathOf(vi.interiorLocal)} " +
                          $"interiorCam={PathOf(vi.interiorCameraTransform)} target={PathOf(vi.cameraTargetObject)} follow={vi.followAngles} rotRange={vi.rotationRange}");
    }

    static void DumpCameras(StringBuilder sb)
    {
        foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
        {
            if (!cam.gameObject.scene.IsValid()) continue;
            var comps = string.Join(",", cam.GetComponents<Component>().Select(x => x.GetIl2CppType().Name));
            var rt = cam.targetTexture;
            sb.AppendLine($"  [{(cam.isActiveAndEnabled ? "ON " : "off")}] {PathOf(cam.transform)} depth={cam.depth} clear={cam.clearFlags} " +
                          $"mask={Mask(cam.cullingMask)} rt={(rt != null ? $"{rt.name} {rt.width}x{rt.height}" : "-")} eye={cam.stereoTargetEye} " +
                          $"fov={cam.fieldOfView:F1} clip={cam.nearClipPlane}-{cam.farClipPlane} pos={V(cam.transform.position)} " +
                          $"rot={V(cam.transform.eulerAngles)} scale={V(cam.transform.lossyScale)} comps=[{comps}]");
        }
    }

    static void DumpVcams(StringBuilder sb)
    {
        var core = CinemachineCore.Instance;
        foreach (var v in Resources.FindObjectsOfTypeAll<CinemachineVirtualCameraBase>())
        {
            if (!v.gameObject.scene.IsValid()) continue;
            bool live = false;
            try { live = core.IsLive(v.TryCast<ICinemachineCamera>()); } catch { }
            sb.AppendLine($"  [{(v.isActiveAndEnabled ? "ON " : "off")}{(live ? " LIVE" : "")}] {PathOf(v.transform)} type={v.GetIl2CppType().Name} " +
                          $"prio={v.Priority} follow={PathOf(v.Follow)} lookAt={PathOf(v.LookAt)} pos={V(v.transform.position)} rot={V(v.transform.eulerAngles)}");
        }
    }

    static void DumpCanvases(StringBuilder sb)
    {
        foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (!c.gameObject.scene.IsValid() || !c.isRootCanvas) continue;
            if (c.renderMode == RenderMode.WorldSpace && !c.isActiveAndEnabled) continue;
            sb.AppendLine($"  [{(c.isActiveAndEnabled ? "ON " : "off")}] {PathOf(c.transform)} mode={c.renderMode} cam={Name(c.worldCamera)} " +
                          $"plane={c.planeDistance} order={c.sortingOrder} sortLayer={c.sortingLayerName} goLayer={LayerMask.LayerToName(c.gameObject.layer)} " +
                          $"scale={c.scaleFactor:F2} pos={V(c.transform.position)}");
        }
    }

    static void DumpBindings(StringBuilder sb)
    {
        var gi = GameInput.Instance;
        if (gi == null || gi.playerActions == null) { sb.AppendLine("  no GameInput"); return; }
        var actions = gi.playerActions.Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            var parts = new List<string>();
            var bindings = a.Bindings;
            for (int j = 0; j < bindings.Count; j++)
            {
                var b = bindings[j];
                var dev = b.TryCast<DeviceBindingSource>();
                parts.Add(dev != null ? $"pad:{dev.Control}" : $"{b.BindingSourceType}:{b.Name}");
            }
            sb.AppendLine($"  {a.Name}: {string.Join(", ", parts)}");
        }
        sb.AppendLine($"  activeDevice={InputManager.ActiveDevice?.Name} devices={InputManager.Devices.Count}");
    }

    static readonly (Type, string)[] XrefTargets =
    {
        (typeof(Player), "SwitchCamera"), (typeof(Player), "EnableCamera"), (typeof(Player), "DisableCamera"),
        (typeof(Player), "LockCamera"), (typeof(Player), "RestoreCamera"), (typeof(Player), "Movement"),
        (typeof(PlayerCar), "SwitchToCockpit_Local"), (typeof(PlayerCar), "EnableCamera"), (typeof(PlayerCar), "Inputs"),
        (typeof(VehicleInterior), "SwitchToCockpit"), (typeof(VehicleInterior), "EnableCamera"), (typeof(VehicleInterior), "InteriorSwitchLocal"),
        (typeof(VehicleInterior), "UpdatePosition"), (typeof(VehicleInterior), "FreeLookCamera"), (typeof(VehicleInterior), "RecenterCamera"),
        (typeof(ControlsButton), "SetupCockpitView"), (typeof(GameInput), "Update"),
    };

    internal static void DumpXrefs()
    {
        var sb = new StringBuilder("===== CloudpunkVR xrefs =====\n");
        foreach (var (t, name) in XrefTargets) Xref(sb, t, name);
        Plugin.L.LogInfo(sb.ToString());
    }

    // xref <TypeName>.<Method>, type looked up by simple or full name in the interop assemblies
    internal static void DumpXref(string spec)
    {
        int dot = spec.LastIndexOf('.');
        string typeName = spec.Substring(0, dot), method = spec.Substring(dot + 1);
        var t = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(x => x.FullName == typeName || x.Name == typeName);
        var sb = new StringBuilder($"===== xref {spec} =====\n");
        if (t == null) sb.AppendLine("  type not found");
        else Xref(sb, t, method);
        Plugin.L.LogInfo(sb.ToString());
    }

    // Callers of a method (needs BepInEx's MethodXrefScanCache.db in BepInEx/interop).
    internal static void DumpUsedBy(string spec)
    {
        int dot = spec.LastIndexOf('.');
        string typeName = spec.Substring(0, dot), method = spec.Substring(dot + 1);
        var t = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(x => x.FullName == typeName || x.Name == typeName);
        var sb = new StringBuilder($"===== used by {spec} =====\n");
        if (t == null) sb.AppendLine("  type not found");
        else
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name == method))
                try
                {
                    foreach (var x in XrefScanner.UsedBy(m))
                        if (x.Type == XrefType.Method && x.TryResolve() is { } caller)
                            sb.AppendLine($"  {caller.DeclaringType?.FullName}.{caller.Name}");
                }
                catch (Exception e) { sb.AppendLine("  !! " + e.Message); }
        Plugin.L.LogInfo(sb.ToString());
    }

    static void Xref(StringBuilder sb, Type t, string name)
    {
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name == name))
        {
            sb.AppendLine($"  {t.Name}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
            try
            {
                foreach (var x in XrefScanner.XrefScan(m))
                {
                    if (x.Type == XrefType.Method)
                    {
                        var r = x.TryResolve();
                        if (r != null) sb.AppendLine($"     calls {r.DeclaringType?.Name}.{r.Name}");
                    }
                    else
                    {
                        try
                        {
                            var s = x.ReadAsObject()?.ToString();
                            if (!string.IsNullOrEmpty(s)) sb.AppendLine($"     str \"{s}\"");
                        }
                        catch { }
                    }
                }
                foreach (var x in XrefScanner.UsedBy(m))
                {
                    var r = x.TryResolve();
                    if (r != null) sb.AppendLine($"     usedBy {r.DeclaringType?.Name}.{r.Name}");
                }
            }
            catch (Exception e) { sb.AppendLine("     !! " + e.Message); }
        }
    }
}
