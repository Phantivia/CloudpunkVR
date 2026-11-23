using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;

namespace CloudpunkVR;

// Film "traffic": flying cars on lanes of our own along the camera's path. The game spawns its traffic only around the
// player's car and on its low lanes (y 10–35 m in Midtown), so a capture far from the player, or up at the streets'
// height, shows an empty sky. These are copies of the game's own pooled traffic cars (models, lights, trails), moved by
// the film clock, so a capture is the same every time.
//
//   "traffic": { "pool": "VehiclePool",                     // where the game keeps its traffic cars (copied, never moved)
//                "lanes": [ { "points": [[x,y,z], …],     // polyline, world space (dense enough to look smooth)
//                             "speed": 30, "count": 6,    // cars evenly spaced over the lane, driving from point 0 on
//                             "offset": 0, "loop": false, // arc-length offset of the first car; loop = closed lane
//                             "seed": 1 }, … ],
//                "beamsNear": 15 }                       // a car's see-through parts (headlight beams: big pale wedges
//                                                       // across the frame from close by) off within this of the camera
// Each car's scripts are switched off (the game's driver would take it back), its rigidbody made kinematic and its
// colliders disabled; lights and trails stay. A car reaching the end of an open lane starts again at its beginning
// (its trails cleared so they do not streak across).
internal static class FilmTraffic
{
    sealed class Lane { public Vector3[] p; public float[] cum; public float L, speed; public bool loop; }
    sealed class Car { public Transform t; public Lane lane; public float s0, lastS = float.NaN; public TrailRenderer[] trails; public List<Renderer> beams; public bool beamsOff; }
    public static Vector3 Eye;            // the filmed camera, for "beamsNear"
    static float beamsNear;

    static GameObject root;
    static readonly List<Car> cars = new();

    public static bool Active => root != null;

    public static void Start(JsonObject job)
    {
        Stop();
        if (job["traffic"] is not JsonObject tj) return;
        var pool = Film.FindAny((string)tj["pool"] ?? "VehiclePool");
        if (pool == null) { Plugin.L.LogWarning("film traffic: no vehicle pool"); return; }
        var prefabs = new List<GameObject>();
        for (int i = 0; i < pool.childCount; i++)
        {
            var c = pool.GetChild(i).gameObject;
            if (c.GetComponentsInChildren<Renderer>(true).Length > 0) prefabs.Add(c);
        }
        if (prefabs.Count == 0) { Plugin.L.LogWarning("film traffic: the pool has no cars"); return; }
        root = new GameObject("CloudpunkVR_FilmTraffic");
        int made = 0, beamCount = 0;
        var beamNames = new List<string>();
        beamsNear = (float?)tj["beamsNear"] ?? 0f;
        foreach (var ln in tj["lanes"].AsArray())
        {
            var lj = ln.AsObject();
            var pts = new List<Vector3>();
            foreach (var p in lj["points"].AsArray()) pts.Add(new Vector3((float)p[0], (float)p[1], (float)p[2]));
            if (pts.Count < 2) continue;
            bool loop = (bool?)lj["loop"] ?? false;
            if (loop) pts.Add(pts[0]);
            var lane = new Lane { p = pts.ToArray(), cum = new float[pts.Count], speed = (float?)lj["speed"] ?? 30f, loop = loop };
            for (int i = 1; i < pts.Count; i++) lane.cum[i] = lane.cum[i - 1] + Vector3.Distance(pts[i], pts[i - 1]);
            lane.L = lane.cum[^1];
            int count = (int?)lj["count"] ?? 4;
            float offset = (float?)lj["offset"] ?? 0f;
            uint seed = (uint)((int?)lj["seed"] ?? made + 1);
            for (int k = 0; k < count; k++)
            {
                var src = prefabs[(int)(Hash(seed * 7919u + (uint)k) % (uint)prefabs.Count)];
                var go = UnityEngine.Object.Instantiate(src, root.transform);
                go.name = "film car " + made;
                Strip(go);
                // evenly spaced, with a little jitter so a lane does not look like a train
                float jitter = (Hash(seed * 31u + (uint)k * 977u) % 1000u) / 1000f - 0.5f;
                var car = new Car { t = go.transform, lane = lane, s0 = offset + (k + 0.35f * jitter) * lane.L / count, trails = go.GetComponentsInChildren<TrailRenderer>(true), beams = new List<Renderer>() };
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                    if (mr != null && mr.sharedMaterial != null && mr.sharedMaterial.renderQueue > 2500) car.beams.Add(mr);
                beamCount += car.beams.Count;
                if (car.beams.Count > 0 && beamNames.Count < 6) foreach (var b in car.beams) if (beamNames.Count < 6 && !beamNames.Contains(b.gameObject.name)) beamNames.Add(b.gameObject.name);
                cars.Add(car);
                go.SetActive(true);
                made++;
            }
        }
        Plugin.L.LogInfo($"film traffic: {made} cars on {tj["lanes"].AsArray().Count} lanes (copies of {prefabs.Count} pooled cars)" +
                         (beamsNear > 0 ? $"; {beamCount} see-through parts off within {beamsNear} m of the camera ({string.Join(", ", beamNames)})" : ""));
    }

    public static void Stop()
    {
        if (root != null) UnityEngine.Object.Destroy(root);
        root = null;
        cars.Clear();
    }

    static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }

    static void Strip(GameObject go)
    {
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) mb.enabled = false;
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.detectCollisions = false; }
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        foreach (var a in go.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
    }

    public static void Update(double t)
    {
        if (root == null) return;
        float ft = (float)t;
        foreach (var c in cars)
        {
            if (c.t == null) continue;
            var ln = c.lane;
            float s = c.s0 + ln.speed * ft;
            s = ((s % ln.L) + ln.L) % ln.L;
            bool wrapped = !float.IsNaN(c.lastS) && s < c.lastS - ln.L * 0.5f;
            c.lastS = s;
            int i = Array.BinarySearch(ln.cum, s);
            if (i < 0) i = ~i - 1;
            i = Mathf.Clamp(i, 0, ln.p.Length - 2);
            float seg = Mathf.Max(1e-4f, ln.cum[i + 1] - ln.cum[i]);
            var pos = Vector3.Lerp(ln.p[i], ln.p[i + 1], (s - ln.cum[i]) / seg);
            // heading from a point a little ahead; level the nose
            float s2 = Mathf.Min(s + 6f, ln.L - 0.01f);
            int j = Array.BinarySearch(ln.cum, s2);
            if (j < 0) j = ~j - 1;
            j = Mathf.Clamp(j, 0, ln.p.Length - 2);
            var ahead = Vector3.Lerp(ln.p[j], ln.p[j + 1], (s2 - ln.cum[j]) / Mathf.Max(1e-4f, ln.cum[j + 1] - ln.cum[j]));
            var fwd = ahead - pos;
            if (fwd.sqrMagnitude < 1e-6f) fwd = ln.p[i + 1] - ln.p[i];
            fwd.y *= 0.3f;
            c.t.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd.normalized, Vector3.up));
            if (beamsNear > 0 && c.beams.Count > 0)
            {
                bool off = (pos - Eye).sqrMagnitude < beamsNear * beamsNear;
                if (off != c.beamsOff) { c.beamsOff = off; foreach (var b in c.beams) if (b != null) b.enabled = !off; }
            }
            if (wrapped && !ln.loop) foreach (var tr in c.trails) if (tr != null) tr.Clear();
        }
    }
}
