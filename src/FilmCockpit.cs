using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HarmonyLib;
using RedAlliance.Util;
using UnityEngine;

namespace CloudpunkVR;

// Film "hero" and "cockpit": a car of our own driven by the film clock, and the game's cockpit interior drawn as if it
// sat inside that car, so a camera can fly at the car, through its windshield and into the driver's seat in one shot.
//
// The game's cockpit is not part of the car: it is a separate copy of the interior near the world origin
// (VehicleInterior.interiorLocal, PlayerCar layer), drawn by its own camera (MainCameraVehicleRenderer.interiorCamera)
// from the main camera's OnPostRender, over the finished world and before post-processing. In play the interior camera
// copies the main camera's view relative to the player's car. Here its view is the filmed camera's, mapped through
// "the interior placed inside the hero car": interior → world = hero(t) · interiorInCar · interiorLocal(t)⁻¹, where
// interiorInCar is measured once from the live game (main camera in the cockpit = the interior camera's seat):
// interiorInCar = car⁻¹ · mainCam · interiorCam⁻¹ · interiorLocal, all rigid. Outside the car (before "cockpit.from")
// the interior is not drawn at all and the hero is the copied exterior; from "from" on the interior is drawn over the
// world (it is its own pass, so it always covers the world: the camera must be inside the car by then).
//
//   "hero":    { "from": "player" | "<object path>",  // whose meshes to copy (the player's car: its exterior renderers)
//                "keys": [[t, x,y,z, qx,qy,qz,qw], …],  // world pose of that object's root over film time (linear/slerp:
//                                                       // write them dense, e.g. 60 a second)
//                "layer": "Vehicle", "skip": ["…"],    // layer for the copies; renderer names containing these skipped
//                "skipLayers": ["PlayerCar","Interior","UI"], "inactive": false,  // copy inactive renderers too
//                "hideFrom": 3.2, "hideUntil": 70,      // film times the exterior copy is off (inside the car)
//                "parts": { "names": ["Windshield"], "hideFrom": 3.2, "hideUntil": 70 },   // or a list of such groups:
//                                                       // copies whose name contains one of the names, off in between
//                "trails": { "from": 74.8, "at": [[x,y,z], …],   // light trails from these car-space points from then
//                            "pool": "VehiclePool", "width": 1.5, "time": 1.0,   // (a traffic car's trail, copied)
//                            "light": [r,g,b, range, intensity], "dot": 0.25 } }   // and a light at their middle,
//                                                       // a glowing dot of that size at each
//   "cockpit": { "from": 3.2, "until": 99,             // film times the interior is drawn; at "until" (the camera has
//                                                       // left the car) a seated performer moves into the hero copy
//                "interiorInCar": [16 floats],         // optional: a mapping logged by an earlier run ("cockpitmap")
//                "keepCamera": false,                  // leave the interior camera enabled (see Owns)
//                "holo": { "dir": "…", "fps": 30,      // the cockpit's pop-out hologram (Coo/CooHoloFigure) with its
//                          "show": [70, 99],           // picture from <dir>/<round(t*fps):D5>.png instead of Spout;
//                          "crop": [x,y,w,h],          // the part of the image used (0-1, from the top left)
//                          "aspect": 0.99, "picHeight": 1024,      // that part's width/height; picture texture height
//                          "height": 0.48,                         // the figure's height in metres (the game's 0.30)
//                          "yaw": -75,                             // turned from facing the driver (toward the camera)
//                          "first": 2100 } }                       // before this frame number the first one is shown
//   "actor": { …, "seat": { "eye": [x,y,z],            // the performer's eyes in stage space (VRStage export, seated)
//                           "at": [right, up, fwd],     // where they go, from the cockpit camera's seat (interior metres)
//                           "yaw": 0 } }               // the performer (stage +Z) turned from the car's forward
// The player has to be in the car in cockpit view when "film" starts (or "interiorInCar" given): the interior copy and
// its camera are only live then. "cockpitmap" logs the mapping and the cockpit's parts in car space for the job script.
internal static class FilmCockpit
{
    static GameObject hero;
    static Transform heroT;
    static float[] kt = Array.Empty<float>();
    static Vector3[] kp;
    static Quaternion[] kr;
    static float hideFrom = float.MaxValue, cockpitFrom = float.MaxValue, cockpitUntil = float.MaxValue;
    // hero "parts": { "names": ["Windshield", …], "hideFrom": t }: copies whose name contains one of these switched off
    // from t (the glass and the car's own interior meshes once the game's cockpit pass draws the inside)
    // "parts" may be one group or a list of groups, each with its own times
    static readonly List<(GameObject go, float from, float until)> heroParts = new();
    static float hideUntil = float.MaxValue;
    // hero "trails": the game's traffic-car light trails on the hero, lit from a time (he floors it)
    static readonly List<TrailRenderer> heroTrails = new();
    static float trailsFrom = float.MaxValue;
    static bool trailsOn;
    // the performer leaves the interior copy for the hero copy when the camera has left the car (cockpit "until")
    static bool unseated;
    static int actorLayer;
    // cockpit "holo": the pop-out hologram fed from a PNG sequence
    static Coo.CooHoloFigure holoFig;
    static RectTransform holoAnchor;
    static string holoDir;
    static float holoFps = 30f, holoFrom = float.MaxValue, holoUntil = float.MaxValue;
    static int holoLoaded = int.MinValue, holoFirst = int.MinValue;
    static Texture2D holoTex;
    static double holoLastT = double.NaN;
    static float holoSavedHeight = float.NaN, holoSavedYaw = float.NaN;
    static bool cockpit, mapped, keepCamera;
    static string offTarget = "active";   // "offscreen" film: where the interior pass draws (see InteriorPrefix)
    static Matrix4x4 interiorInCar;
    static Camera interior, filmCam;
    static Transform interiorLocal;
    static Vector3 heroScale = Vector3.one;
    static double now;
    static bool patched;
    static Transform seatedRoot;
    static Transform seatedWasParent;

    public static bool Cockpit => cockpit;
    // "keepCamera": leave the interior camera enabled (normally the film's exclusive mode switches it off; the
    // interior pass renders it explicitly either way, unless the game's OnPostRender only draws an enabled camera)
    public static bool Owns(Camera c) => cockpit && keepCamera && interior != null && c == interior;

    public static void Start(JsonObject job, Camera cam)
    {
        Stop();
        filmCam = cam;
        try
        {
            if (job["hero"] is JsonObject hj) BuildHero(hj);
            if (job["cockpit"] is JsonObject cj) StartCockpit(cj, cam);
            if (job["actor"] is JsonObject aj && aj["seat"] is JsonObject seat) Seat(seat);
            if (job["cockpit"] is JsonObject cj2 && cj2["lights"] is JsonArray lj) BuildLights(lj);
        }
        catch (Exception e)
        {
            Plugin.L.LogError("film cockpit: " + e);
        }
    }

    public static void Stop()
    {
        // a performer moved into the hero goes back under the stage before the hero goes (FilmStage.Stop removes it)
        if (unseated && seatedRoot != null) seatedRoot.SetParent(seatedWasParent, true);
        unseated = false;
        if (hero != null) UnityEngine.Object.Destroy(hero);
        hero = null; heroT = null;
        heroParts.Clear(); hideUntil = float.MaxValue;
        heroTrails.Clear(); trailsFrom = float.MaxValue; trailsOn = false; heroTrailLight = null;
        kt = Array.Empty<float>(); kp = null; kr = null;
        hideFrom = cockpitFrom = cockpitUntil = float.MaxValue;
        holoFig?.Destroy();
        if (!float.IsNaN(holoSavedHeight)) { Coo.CooHoloFigure.Height = holoSavedHeight; holoSavedHeight = float.NaN; }
        if (!float.IsNaN(holoSavedYaw)) { Coo.CooHoloFigure.Yaw = holoSavedYaw; holoSavedYaw = float.NaN; }
        holoFig = null; holoAnchor = null; holoDir = null; holoLoaded = int.MinValue; holoLastT = double.NaN;
        holoFrom = holoUntil = float.MaxValue;
        if (holoTex != null) UnityEngine.Object.Destroy(holoTex);
        holoTex = null;
        if (interior != null) { interior.ResetWorldToCameraMatrix(); interior.ResetProjectionMatrix(); }
        interior = null; interiorLocal = null; filmCam = null;
        cockpit = mapped = keepCamera = false;
        seatedRoot = seatedWasParent = null;   // the stage root itself is destroyed by FilmStage.Stop
        foreach (var l in cabinLights) if (l.go != null) UnityEngine.Object.Destroy(l.go);
        cabinLights.Clear();
    }

    // ---------------------------------------------------------------- cabin lights
    //   "cockpit": { …, "lights": [ { "at": [x,y,z],          // car space (x right, y up, z forward of the car root)
    //                                 "color": [r,g,b], "range": 1.5, "intensity": [[t, i], …] | i,
    //                                 "lamp": 0.02 } ] }     // also a glowing dot of this diameter (an indicator)
    // Point lights inside the interior copy (PlayerCar layer only, which the interior camera draws): a fill on the
    // driver's face, the dashboard lighting up, the red live lamp.
    sealed class CabinLight { public GameObject go; public Light light; public Material lampMat; public float[][] curve; public Color color; }
    static readonly List<CabinLight> cabinLights = new();

    static void BuildLights(JsonArray lj)
    {
        var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
        if (vi == null || vi.interiorLocal == null || !mapped) { Plugin.L.LogWarning("film cockpit: no interior for the cabin lights"); return; }
        int layer = LayerMask.NameToLayer("PlayerCar");
        var toLocal = interiorInCar.inverse;            // car space -> interiorLocal's local space
        foreach (var n in lj)
        {
            var o = n.AsObject();
            var at = o["at"].AsArray();
            var local = toLocal.MultiplyPoint3x4(new Vector3((float)at[0], (float)at[1], (float)at[2]));
            var go = new GameObject("CloudpunkVR_CabinLight");
            go.transform.SetParent(vi.interiorLocal, false);
            go.transform.localPosition = local;
            if (layer >= 0) go.layer = layer;
            var col = o["color"] is JsonArray c ? new Color((float)c[0], (float)c[1], (float)c[2], 1f) : Color.white;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.color = col; l.range = (float?)o["range"] ?? 1.5f; l.shadows = LightShadows.None;
            if (layer >= 0) l.cullingMask = 1 << layer;
            l.renderMode = LightRenderMode.ForcePixel;
            var cl = new CabinLight { go = go, light = l, color = col };
            var ij = o["intensity"];
            cl.curve = ij is JsonArray ? ReadCurve(ij.AsArray()) : new[] { new[] { 0f, ij != null ? (float)ij : 1f } };
            if (o["lamp"] != null)
            {
                var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col2 = dot.GetComponent<Collider>();
                if (col2 != null) UnityEngine.Object.Destroy(col2);
                dot.transform.SetParent(go.transform, false);
                dot.transform.localScale = Vector3.one * (float)o["lamp"];
                if (layer >= 0) dot.layer = layer;
                var add = Shader.Find("Legacy Shaders/Particles/Additive");
                cl.lampMat = new Material(add) { mainTexture = Texture2D.whiteTexture };
                dot.GetComponent<MeshRenderer>().sharedMaterial = cl.lampMat;
            }
            cabinLights.Add(cl);
        }
        Plugin.L.LogInfo($"film cockpit: {cabinLights.Count} cabin lights");
    }

    static float[][] ReadCurve(JsonArray a)
    {
        var r = new float[a.Count][];
        for (int i = 0; i < a.Count; i++) { var k = a[i].AsArray(); r[i] = new[] { (float)k[0], (float)k[1] }; }
        Array.Sort(r, (x, y) => x[0].CompareTo(y[0]));
        return r;
    }

    static float CurveAt(float[][] c, float t)
    {
        if (t <= c[0][0]) return c[0][1];
        if (t >= c[^1][0]) return c[^1][1];
        int s = 0;
        while (s < c.Length - 2 && t > c[s + 1][0]) s++;
        float x = (t - c[s][0]) / Math.Max(1e-6f, c[s + 1][0] - c[s][0]);
        return c[s][1] + (c[s + 1][1] - c[s][1]) * x;
    }

    // ---------------------------------------------------------------- hero car

    static void BuildHero(JsonObject hj)
    {
        string from = (string)hj["from"] ?? "player";
        Transform src = from == "player"
            ? (Global.Instance != null && Global.Instance.playerCar != null ? Global.Instance.playerCar.transform : null)
            : Film.FindAny(from);
        if (src == null) { Plugin.L.LogWarning("film hero: source not found: " + from); return; }
        int layer = LayerMask.NameToLayer((string)hj["layer"] ?? "Vehicle");
        if (layer < 0) layer = 0;
        var skip = new List<string>();
        if (hj["skip"] is JsonArray sk) foreach (var n in sk) skip.Add((string)n);
        var skipLayers = new HashSet<int>();
        foreach (var n in hj["skipLayers"] is JsonArray sl ? sl : new JsonArray("PlayerCar", "Interior", "UI"))
        {
            int l = LayerMask.NameToLayer((string)n);
            if (l >= 0) skipLayers.Add(l);
        }
        bool inactive = (bool?)hj["inactive"] ?? false;

        hero = new GameObject("CloudpunkVR_FilmHero");
        heroT = hero.transform;
        heroScale = src.lossyScale;
        heroT.localScale = heroScale;
        var invRot = Quaternion.Inverse(src.rotation);
        int made = 0;
        var names = new List<string>();
        foreach (var mr in src.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (mr == null || (!inactive && !mr.gameObject.activeInHierarchy) || skipLayers.Contains(mr.gameObject.layer)) continue;
            string n = mr.gameObject.name;
            if (skip.Exists(s => n.Contains(s))) continue;
            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var go = new GameObject("hero " + n);
            var t = go.transform;
            t.SetParent(heroT, false);
            t.localPosition = src.InverseTransformPoint(mr.transform.position);
            t.localRotation = invRot * mr.transform.rotation;
            var ls = mr.transform.lossyScale;
            t.localScale = new Vector3(ls.x / heroScale.x, ls.y / heroScale.y, ls.z / heroScale.z);
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mr.sharedMaterials;
            r.shadowCastingMode = mr.shadowCastingMode;
            r.receiveShadows = mr.receiveShadows;
            go.layer = layer;
            made++;
            foreach (var pj in PartGroups(hj))
                if (pj["names"] is JsonArray pn && pn.Any(nn => n.Contains((string)nn)))
                {
                    heroParts.Add((go, (float?)pj["hideFrom"] ?? float.MaxValue, (float?)pj["hideUntil"] ?? float.MaxValue));
                    break;
                }
            names.Add($"{n}@{LayerMask.LayerToName(mr.gameObject.layer)}{(mr.enabled ? "" : " (was off)")}");
        }
        int lights = 0;
        foreach (var l in src.GetComponentsInChildren<Light>(true))
        {
            if (l == null || (!inactive && !l.gameObject.activeInHierarchy)) continue;
            var go = new GameObject("hero light " + l.gameObject.name);
            var t = go.transform;
            t.SetParent(heroT, false);
            t.localPosition = src.InverseTransformPoint(l.transform.position);
            t.localRotation = invRot * l.transform.rotation;
            var c = go.AddComponent<Light>();
            c.type = l.type; c.color = l.color; c.intensity = l.intensity; c.range = l.range; c.spotAngle = l.spotAngle;
            c.shadows = LightShadows.None; c.cullingMask = l.cullingMask; c.renderMode = l.renderMode;
            lights++;
        }
        // keys: [t, x,y,z, qx,qy,qz,qw]
        var ks = hj["keys"] as JsonArray ?? new JsonArray();
        kt = new float[ks.Count]; kp = new Vector3[ks.Count]; kr = new Quaternion[ks.Count];
        for (int i = 0; i < ks.Count; i++)
        {
            var k = ks[i].AsArray();
            kt[i] = (float)k[0];
            kp[i] = new Vector3((float)k[1], (float)k[2], (float)k[3]);
            kr[i] = new Quaternion((float)k[4], (float)k[5], (float)k[6], (float)k[7]);
        }
        hideFrom = (float?)hj["hideFrom"] ?? float.MaxValue;
        hideUntil = (float?)hj["hideUntil"] ?? float.MaxValue;
        if (hj["trails"] is JsonObject tj) BuildTrails(tj, layer);
        Pose(0);
        Plugin.L.LogInfo($"film hero: {made} meshes, {lights} lights copied from {Diag.PathOf(src)} (scale {Diag.V(heroScale)}), {kt.Length} keys: {string.Join(", ", names)}");
    }

    static void Pose(float t)
    {
        if (heroT == null || kt.Length == 0) return;
        Vector3 p; Quaternion r;
        if (t <= kt[0]) { p = kp[0]; r = kr[0]; }
        else if (t >= kt[^1]) { p = kp[^1]; r = kr[^1]; }
        else
        {
            int i = Array.BinarySearch(kt, t);
            if (i < 0) i = ~i - 1;
            i = Math.Clamp(i, 0, kt.Length - 2);
            float x = (t - kt[i]) / Math.Max(1e-6f, kt[i + 1] - kt[i]);
            p = Vector3.LerpUnclamped(kp[i], kp[i + 1], x);
            r = Quaternion.Slerp(kr[i], kr[i + 1], x);
        }
        heroT.SetPositionAndRotation(p, r);
        bool show = t < hideFrom || t >= hideUntil;
        if (hero.activeSelf != show) hero.SetActive(show);
        UpdateTrails(t);
        foreach (var (part, from, until) in heroParts)
        {
            bool on = t < from || t >= until;
            if (part != null && part.activeSelf != on) part.SetActive(on);
        }
    }

    static void BuildTrails(JsonObject tj, int layer)
    {
        trailsFrom = (float?)tj["from"] ?? 0f;
        var pool = Film.FindAny((string)tj["pool"] ?? "VehiclePool");
        TrailRenderer tmpl = null;
        if (pool != null)
            for (int i = 0; i < pool.childCount && tmpl == null; i++)
            {
                var trs = pool.GetChild(i).GetComponentsInChildren<TrailRenderer>(true);
                if (trs.Length > 0 && trs[0].sharedMaterial != null) tmpl = trs[0];
            }
        float width = (float?)tj["width"] ?? 1f;
        foreach (var n in tj["at"] as JsonArray ?? new JsonArray())
        {
            var p = n.AsArray();
            var go = new GameObject("hero trail");
            go.layer = layer;
            go.transform.SetParent(heroT, false);
            go.transform.localPosition = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            var tr = go.AddComponent<TrailRenderer>();
            if (tmpl != null)
            {
                tr.sharedMaterial = tmpl.sharedMaterial;
                tr.colorGradient = tmpl.colorGradient;
                tr.widthCurve = tmpl.widthCurve;
                tr.widthMultiplier = tmpl.widthMultiplier * width;
                tr.time = (float?)tj["time"] ?? tmpl.time;
                tr.minVertexDistance = tmpl.minVertexDistance;
                tr.numCapVertices = tmpl.numCapVertices;
            }
            else
            {
                tr.sharedMaterial = new Material(Shader.Find("Legacy Shaders/Particles/Additive")) { mainTexture = Texture2D.whiteTexture };
                tr.widthMultiplier = 0.25f * width;
                tr.time = (float?)tj["time"] ?? 1f;
            }
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tr.receiveShadows = false;
            tr.emitting = false;
            heroTrails.Add(tr);
        }
        if (tj["light"] is JsonArray lj && lj.Count >= 5 && heroTrails.Count > 0)
        {
            var mid = Vector3.zero;
            foreach (var tr in heroTrails) mid += tr.transform.localPosition;
            var go = new GameObject("hero trail light");
            go.transform.SetParent(heroTrails[0].transform.parent, false);
            go.transform.localPosition = mid / heroTrails.Count;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.color = new Color((float)lj[0], (float)lj[1], (float)lj[2]); l.range = (float)lj[3]; l.intensity = (float)lj[4];
            l.shadows = LightShadows.None;
            // and a glowing dot at each trail's root (the bloom makes them two lights going away)
            float dot = tj["dot"] != null ? (float)tj["dot"] : 0f;
            if (dot > 0f)
            {
                var add = Shader.Find("Legacy Shaders/Particles/Additive");
                var mat = add != null ? new Material(add) { mainTexture = Texture2D.whiteTexture } : null;
                if (mat != null) mat.SetColor("_TintColor", new Color(l.color.r, l.color.g, l.color.b, 1f));
                foreach (var tr in heroTrails)
                {
                    var sph = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    var col = sph.GetComponent<Collider>();
                    if (col != null) UnityEngine.Object.Destroy(col);
                    sph.layer = layer;
                    sph.transform.SetParent(go.transform, false);
                    sph.transform.position = tr.transform.position;
                    sph.transform.localScale = Vector3.one * dot;
                    if (mat != null) sph.GetComponent<MeshRenderer>().sharedMaterial = mat;
                }
            }
            go.SetActive(false);
            heroTrailLight = go;
        }
        Plugin.L.LogInfo($"film hero: {heroTrails.Count} trails from {trailsFrom:F2} s ({(tmpl != null ? "copied from " + Diag.PathOf(tmpl.transform) : "plain additive")})");
    }

    static GameObject heroTrailLight;

    static void UpdateTrails(float t)
    {
        bool on = t >= trailsFrom;
        if (on == trailsOn) return;
        trailsOn = on;
        foreach (var tr in heroTrails) if (tr != null) { tr.Clear(); tr.emitting = on; }
        if (heroTrailLight != null) heroTrailLight.SetActive(on);
    }

    static IEnumerable<JsonObject> PartGroups(JsonObject hj)
    {
        if (hj["parts"] is JsonObject one) yield return one;
        else if (hj["parts"] is JsonArray many) foreach (var g in many) if (g is JsonObject o) yield return o;
    }

    /// <summary>World pose of the hero's root (without scale) at film time t; identity if there is no hero.</summary>
    static Matrix4x4 HeroAt(float t)
    {
        if (heroT == null) return Matrix4x4.identity;
        return Matrix4x4.TRS(heroT.position, heroT.rotation, Vector3.one);
    }

    // ---------------------------------------------------------------- cockpit

    static void StartCockpit(JsonObject cj, Camera cam)
    {
        var g = Global.Instance;
        var vi = g != null ? g.vehicleInterior : null;
        var mcvr = cam.GetComponent<MainCameraVehicleRenderer>();
        interior = mcvr != null ? mcvr.interiorCamera : null;
        if (interior == null && vi != null) interior = vi.cameraCockpit;
        interiorLocal = vi != null ? vi.interiorLocal : null;
        if (interior == null || interiorLocal == null || mcvr == null)
        {
            Plugin.L.LogWarning($"film cockpit: no interior (renderer {(mcvr != null)}, camera {(interior != null)}, interiorLocal {(interiorLocal != null)}): get in the car, cockpit view");
            interior = null;
            return;
        }
        cockpitFrom = (float?)cj["from"] ?? 0f;
        cockpitUntil = (float?)cj["until"] ?? float.MaxValue;
        if (cj["interiorInCar"] is JsonArray m && m.Count == 16)
        {
            for (int i = 0; i < 16; i++) interiorInCar[i] = (float)m[i];
            mapped = true;
            Plugin.L.LogInfo("film cockpit: interiorInCar from the job");
        }
        else if (Measure(cam, out interiorInCar, out string why)) { mapped = true; Plugin.L.LogInfo("film cockpit: interiorInCar measured " + Arr(interiorInCar)); }
        else { Plugin.L.LogWarning("film cockpit: " + why); interior = null; return; }
        cockpit = true;
        keepCamera = (bool?)cj["keepCamera"] ?? false;
        offTarget = (string)cj["offscreenTarget"] ?? "active";
        if (keepCamera && !interior.enabled) interior.enabled = true;
        if (!patched)
        {
            patched = true;
            Plugin.Harmony.Patch(AccessTools.Method(typeof(MainCameraVehicleRenderer), nameof(MainCameraVehicleRenderer.OnPostRender)),
                prefix: new HarmonyMethod(typeof(FilmCockpit), nameof(InteriorPrefix)));
        }
        Plugin.L.LogInfo($"film cockpit: {Diag.PathOf(interior.transform)} (enabled {interior.enabled}, mask 0x{interior.cullingMask:X8}, clear {interior.clearFlags}, near {interior.nearClipPlane}) drawn from {cockpitFrom:F2} s" +
                         (cockpitUntil < float.MaxValue ? $" until {cockpitUntil:F2} s" : ""));
        if (cj["holo"] is JsonObject hj) StartHolo(hj);
    }

    // ---------------------------------------------------------------- the pop-out hologram from a PNG sequence

    static void StartHolo(JsonObject hj)
    {
        holoDir = (string)hj["dir"];
        holoFps = (float?)hj["fps"] ?? 30f;
        holoFirst = (int?)hj["first"] ?? int.MinValue;
        var show = hj["show"] as JsonArray;
        holoFrom = show != null && show.Count > 0 ? (float)show[0] : 0f;
        holoUntil = show != null && show.Count > 1 ? (float)show[1] : float.MaxValue;
        var grp = Global.Instance != null && Global.Instance.dialogUI != null ? Global.Instance.dialogUI.interiorCanvasGroupGlobal : null;
        var at = grp != null ? grp.transform.Find("Dialog_Interior/Interior_Portrait_Image") : null;
        holoAnchor = at != null ? at.TryCast<RectTransform>() : null;
        if (holoAnchor == null || string.IsNullOrEmpty(holoDir) || !Directory.Exists(holoDir))
        {
            Plugin.L.LogWarning($"film cockpit: no hologram (call screen portrait {(holoAnchor != null)}, dir {holoDir})");
            holoAnchor = null;
            return;
        }
        holoFig = new Coo.CooHoloFigure(null, 7177)
        {
            Aspect = (float?)hj["aspect"] ?? 0.68f,
            PicHeight = (int?)hj["picHeight"] ?? 1024,
        };
        if (hj["height"] != null) { holoSavedHeight = Coo.CooHoloFigure.Height; Coo.CooHoloFigure.Height = (float)hj["height"]; }
        if (hj["yaw"] != null) { holoSavedYaw = Coo.CooHoloFigure.Yaw; Coo.CooHoloFigure.Yaw = (float)hj["yaw"]; }
        if (hj["crop"] is JsonArray c && c.Count == 4)   // x, y from the top left; the texture's uv start bottom left
            holoFig.FilmCrop = new Rect((float)c[0], 1f - (float)c[1] - (float)c[3], (float)c[2], (float)c[3]);
        Plugin.L.LogInfo($"film cockpit: hologram from {holoDir} at {holoFps} fps, shown {holoFrom:F2}-{holoUntil:F2} s, on {Diag.PathOf(holoAnchor)}");
    }

    static void UpdateHolo(double t)
    {
        if (holoFig == null) return;
        int idx = Math.Max(holoFirst, (int)Math.Round(t * holoFps));
        if (idx != holoLoaded && t >= holoFrom - 0.1 && t < holoUntil)
        {
            holoLoaded = idx;
            var path = Path.Combine(holoDir, idx.ToString("D5") + ".png");
            if (File.Exists(path))
            {
                try
                {
                    var (w, h, rgba) = Coo.Png.Decode(File.ReadAllBytes(path));
                    if (holoTex == null || holoTex.width != w || holoTex.height != h)
                    {
                        if (holoTex != null) UnityEngine.Object.Destroy(holoTex);
                        holoTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "CloudpunkVR_FilmHolo", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };
                    }
                    holoTex.LoadRawTextureData(rgba);
                    holoTex.Apply(false);
                }
                catch (Exception e) { Plugin.L.LogWarning($"film cockpit: hologram frame {path}: {e.Message}"); }
            }
        }
        holoFig.FilmSource = holoTex;
        holoFig.FilmClock = t;
        float dt = double.IsNaN(holoLastT) ? 0f : (float)Math.Max(0.0, t - holoLastT);
        holoLastT = t;
        holoFig.Update(holoAnchor, t >= holoFrom && t < holoUntil, dt);
    }

    // The camera has left the car: the interior pass stops, so the performer goes from the interior copy into the hero
    // copy at the same place in the car (and onto the layer the world camera draws), and rides along with it.
    static void Unseat(float t)
    {
        unseated = true;
        if (seatedRoot == null || heroT == null || interiorLocal == null || !mapped) return;
        var L = Matrix4x4.TRS(interiorLocal.position, interiorLocal.rotation, Vector3.one);
        var toWorld = HeroAt(t) * interiorInCar * L.inverse;
        var p = toWorld.MultiplyPoint3x4(seatedRoot.position);
        var r = Quaternion.LookRotation(toWorld.GetColumn(2), toWorld.GetColumn(1)) * seatedRoot.rotation;
        seatedRoot.SetParent(heroT, true);
        seatedRoot.SetPositionAndRotation(p, r);
        foreach (var tr in seatedRoot.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = actorLayer;
        // the cabin lights too (the dashboard glow and the live lamp seen through the windows), lighting everything now
        int vlayer = hero != null ? hero.layer : 0;
        foreach (var cl in cabinLights)
        {
            if (cl.go == null) continue;
            var lp = toWorld.MultiplyPoint3x4(cl.go.transform.position);
            cl.go.transform.SetParent(heroT, true);
            cl.go.transform.position = lp;
            cl.light.cullingMask = -1;
            foreach (var tr in cl.go.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = vlayer;
        }
        Plugin.L.LogInfo($"film cockpit: the camera left the car at {t:F2} s; actor moved into the hero at {Diag.V(p)}, layer {actorLayer}; {cabinLights.Count} cabin lights with it");
    }

    // The live mapping of the interior into the player's car: valid while the player sits in the cockpit view.
    static bool Measure(Camera main, out Matrix4x4 m, out string why)
    {
        m = Matrix4x4.identity;
        var g = Global.Instance;
        if (g == null || g.playerCar == null || !Global.IsInVehicle || !Global.IsInVehicleCockpit) { why = "not in the car's cockpit view; nothing to measure (give interiorInCar)"; return false; }
        var vi = g.vehicleInterior;
        var ic = interior != null ? interior : vi.cameraCockpit;
        var car = g.playerCar.transform;
        Matrix4x4 R(Transform t) => Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
        m = R(car).inverse * R(main.transform) * R(ic.transform).inverse * R(vi.interiorLocal);
        why = null;
        return true;
    }

    public static void Update(double t, Camera cam)
    {
        now = t;
        Pose((float)t);
        if (!unseated && seatedRoot != null && t >= cockpitUntil) Unseat((float)t);
        UpdateHolo(t);
        foreach (var l in cabinLights)
        {
            float i = Math.Max(0f, CurveAt(l.curve, (float)t));
            if (l.light != null) { l.light.intensity = i; l.light.enabled = i > 0f; }
            if (l.lampMat != null)
            {
                var c = l.color * Math.Min(1f, i);
                c.a = 1f;
                l.lampMat.SetColor("_TintColor", c);
            }
        }
    }

    // MainCameraVehicleRenderer.OnPostRender: the interior pass inside the main camera's. While filming with a cockpit:
    // skipped outside [from, until); otherwise the interior camera gets the filmed camera's view through the mapping.
    static bool InteriorPrefix(MainCameraVehicleRenderer __instance)
    {
        if (!cockpit || !Film.Active || UnityEngine.XR.XRSettings.enabled) return true;
        if (now < cockpitFrom || now >= cockpitUntil || interior == null || interiorLocal == null) return false;
        var cam = __instance.GetComponent<Camera>();
        if (cam == null || cam != filmCam) return true;
        var L = Matrix4x4.TRS(interiorLocal.position, interiorLocal.rotation, Vector3.one);
        var toWorld = HeroAt((float)now) * interiorInCar * L.inverse;     // interior space → world
        if (interior.stereoTargetEye != StereoTargetEyeMask.None) interior.stereoTargetEye = StereoTargetEyeMask.None;
        // windowed the filmed camera has no target and the interior draws where the game draws it. "offscreen" gives
        // the filmed camera its own target; drawn straight into that, the interior was overwritten by the camera's
        // post-processing (11-09 4K: no cockpit at all), so then it draws into what is bound now (the camera's
        // intermediate, before the post-processing) or, "none", with no target like windowed
        var tgt = cam.targetTexture;
        if (tgt != null && tgt.name == "CloudpunkVR film")
            tgt = offTarget == "active" ? RenderTexture.active : offTarget == "none" ? null : tgt;
        interior.targetTexture = tgt;
        interior.worldToCameraMatrix = cam.worldToCameraMatrix * toWorld;
        interior.projectionMatrix = cam.projectionMatrix;
        return true;
    }

    // ---------------------------------------------------------------- the performer in the driver's seat

    static void Seat(JsonObject seat)
    {
        var root = FilmStage.Root;
        var g = Global.Instance;
        var vi = g != null ? g.vehicleInterior : null;
        if (root == null || vi == null || vi.interiorLocal == null) { Plugin.L.LogWarning("film cockpit: no actor or no interior to seat it in"); return; }
        var il = vi.interiorLocal;
        var socket = interior != null ? interior.transform : vi.cameraCockpit != null ? vi.cameraCockpit.transform : vi.interiorCameraTransform;
        var up = il.up;
        var fwd = Vector3.ProjectOnPlane(il.forward, up).normalized;
        var right = Vector3.Cross(up, fwd);
        var eye = seat["eye"] is JsonArray e ? new Vector3((float)e[0], (float)e[1], (float)e[2]) : new Vector3(0, 1.1f, 0);
        var at = seat["at"] is JsonArray a ? new Vector3((float)a[0], (float)a[1], (float)a[2]) : Vector3.zero;
        var rot = Quaternion.LookRotation(fwd, up) * Quaternion.Euler(0, (float?)seat["yaw"] ?? 0f, 0);
        var target = socket.position + right * at.x + up * at.y + fwd * at.z;
        root.SetPositionAndRotation(target - rot * Vector3.Scale(eye, root.lossyScale), rot);   // the stage space may be scaled (actor.scale)
        seatedWasParent = root.parent;
        root.SetParent(il, true);
        seatedRoot = root;
        actorLayer = root.gameObject.layer;
        int layer = LayerMask.NameToLayer("PlayerCar");
        if (layer >= 0) foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        Plugin.L.LogInfo($"film cockpit: actor seated under {Diag.PathOf(il)} at {Diag.V(root.localPosition)} (eye {Diag.V(eye)} → socket {Diag.V(at)}), layer {layer}");
    }

    // ---------------------------------------------------------------- dev: cockpitmap

    /// <summary>Log and write (film/cockpitmap.json) the interior mapping and the cockpit's parts in the player car's
    /// space (x right, y up, z forward of the car root), for the job script: where the eyes, the wheel and the windshield
    /// are, and the exterior's bounds.</summary>
    public static void DumpMap()
    {
        var g = Global.Instance;
        var main = g != null ? g.mainCamera : null;
        if (main == null) { Plugin.L.LogWarning("cockpitmap: no main camera"); return; }
        var mcvr = main.GetComponent<MainCameraVehicleRenderer>();
        interior = mcvr != null ? mcvr.interiorCamera : g.vehicleInterior != null ? g.vehicleInterior.cameraCockpit : null;
        if (!Measure(main, out var m, out var why)) { Plugin.L.LogWarning("cockpitmap: " + why); interior = null; return; }
        var vi = g.vehicleInterior;
        var car = g.playerCar.transform;
        var carInv = Matrix4x4.TRS(car.position, car.rotation, Vector3.one).inverse;
        var L = Matrix4x4.TRS(vi.interiorLocal.position, vi.interiorLocal.rotation, Vector3.one);
        var interiorToCar = m * L.inverse;                    // interior world → car local
        string P(Vector3 v) => $"[{v.x:F3},{v.y:F3},{v.z:F3}]";
        Vector3 InCar(Vector3 interiorWorld) => interiorToCar.MultiplyPoint3x4(interiorWorld);
        var sb = new System.Text.StringBuilder("{");
        sb.Append("\"interiorInCar\":").Append(Arr(m));
        sb.Append(",\"carScale\":").Append(P(car.lossyScale));
        sb.Append(",\"eye\":").Append(P(carInv.MultiplyPoint3x4(main.transform.position)));
        sb.Append(",\"eyeForward\":").Append(P(carInv.MultiplyVector(main.transform.forward)));
        sb.Append(",\"mainNear\":").Append(main.nearClipPlane.ToString("R")).Append(",\"mainFov\":").Append(main.fieldOfView.ToString("R"));
        void Part(string name, Transform t) { if (t != null) sb.Append(",\"").Append(name).Append("\":").Append(P(InCar(t.position))); }
        Part("socket", vi.interiorCameraTransform);
        Part("wheel", vi.steeringWheel);
        Part("speedometer", vi.speedometer);
        Part("lever", vi.ascensionLever);
        Part("pedal", vi.gasPedal);
        if (vi.windshieldFrontMesh != null)
        {
            var b = vi.windshieldFrontMesh.bounds;
            sb.Append(",\"windshieldMin\":").Append(P(InCar(b.min))).Append(",\"windshieldMax\":").Append(P(InCar(b.max)))
              .Append(",\"windshieldCentre\":").Append(P(InCar(b.center)));
        }
        // the interior's own extent in car space (where the cabin is) and the exterior's
        Bounds? ib = null;
        foreach (var r in vi.interiorLocal.GetComponentsInChildren<Renderer>())
        {
            if (r == null || !r.enabled) continue;
            var c = InCar(r.bounds.center);
            if (ib == null) ib = new Bounds(c, Vector3.zero); else { var x = ib.Value; x.Encapsulate(c); ib = x; }
        }
        if (ib != null) sb.Append(",\"interiorPartsMin\":").Append(P(ib.Value.min)).Append(",\"interiorPartsMax\":").Append(P(ib.Value.max));
        Bounds? eb = null;
        var parts = new List<string>();
        foreach (var r in car.GetComponentsInChildren<MeshRenderer>())
        {
            if (r == null) continue;
            var b = r.bounds;
            foreach (var corner in new[] { b.min, b.max })
            {
                var c = carInv.MultiplyPoint3x4(corner);
                if (eb == null) eb = new Bounds(c, Vector3.zero); else { var x = eb.Value; x.Encapsulate(c); eb = x; }
            }
            parts.Add($"\"{r.gameObject.name.Replace("\"", "'")}@{LayerMask.LayerToName(r.gameObject.layer)}{(r.enabled ? "" : " off")}\"");
        }
        if (eb != null) sb.Append(",\"exteriorMin\":").Append(P(eb.Value.min)).Append(",\"exteriorMax\":").Append(P(eb.Value.max));
        sb.Append(",\"exteriorParts\":[").Append(string.Join(",", parts)).Append("]}");
        string dir = Path.Combine(Plugin.Dir, "film");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cockpitmap.json");
        File.WriteAllText(path, sb.ToString());
        Plugin.L.LogInfo("cockpitmap: " + path + "\n" + sb);
        interior = null;
    }

    static string Arr(Matrix4x4 m)
    {
        var parts = new string[16];
        for (int i = 0; i < 16; i++) parts[i] = m[i].ToString("R");
        return "[" + string.Join(",", parts) + "]";
    }
}
