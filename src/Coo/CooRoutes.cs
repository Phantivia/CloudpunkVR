using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloudpunkVR.Coo;

// Routes for the car over the game's own traffic network: AIManager's VehicleTracks, the splines the city's traffic
// flies along, sampled into points. Track ends that meet are joined (that is how the game connects them, by
// position, in VehicleTrack.FindConnections), a route enters the network at the sample nearest the car, follows
// tracks with A*, and leaves it at the sample nearest the goal. Tracks are driven either way; against the traffic's
// direction costs more.
internal sealed class CooRoutes
{
    sealed class Track
    {
        public Vector3[] P;      // samples
        public float[] S;        // arc length at each sample
        public float Length => S[S.Length - 1];
    }

    readonly List<Track> tracks = new();
    // node = track * 2 + end (0 first sample, 1 last); edges to other nodes with costs
    readonly List<List<(int to, float cost)>> links = new();
    int builtScene = -1;
    public int TrackCount => tracks.Count;

    public bool EnsureBuilt()
    {
        int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        if (builtScene == scene && tracks.Count > 0) return true;
        tracks.Clear();
        links.Clear();
        var mgr = UnityEngine.Object.FindObjectOfType<AIManager>();
        var arr = mgr != null ? mgr.tracks : null;
        if (arr == null || arr.Length == 0) return false;
        for (int i = 0; i < arr.Length; i++)
        {
            var vt = arr[i];
            if (vt == null) continue;
            var samples = vt.bakedSamples;
            var pts = new List<Vector3>();
            if (samples != null && samples.Length >= 2)
                for (int k = 0; k < samples.Length; k++) pts.Add(samples[k].position);
            else
            {
                pts.Add(vt.firstPos);
                pts.Add(vt.lastPos);
            }
            var t = new Track { P = pts.ToArray(), S = new float[pts.Count] };
            for (int k = 1; k < pts.Count; k++) t.S[k] = t.S[k - 1] + Vector3.Distance(pts[k - 1], pts[k]);
            if (t.Length < 1) continue;
            tracks.Add(t);
        }
        for (int i = 0; i < tracks.Count * 2; i++) links.Add(new List<(int, float)>());
        // along each track
        for (int i = 0; i < tracks.Count; i++)
        {
            links[i * 2].Add((i * 2 + 1, tracks[i].Length));
            links[i * 2 + 1].Add((i * 2, tracks[i].Length * 1.35f));
        }
        // ends that meet: a coarse spatial hash so this stays fast with thousands of tracks
        const float join = 14f;
        var cells = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => (Mathf.FloorToInt(p.x / join), Mathf.FloorToInt(p.y / join), Mathf.FloorToInt(p.z / join));
        Vector3 End(int node) => node % 2 == 0 ? tracks[node / 2].P[0] : tracks[node / 2].P[tracks[node / 2].P.Length - 1];
        for (int n = 0; n < tracks.Count * 2; n++)
        {
            var c = Cell(End(n));
            if (!cells.TryGetValue(c, out var l)) cells[c] = l = new List<int>();
            l.Add(n);
        }
        int joins = 0;
        for (int n = 0; n < tracks.Count * 2; n++)
        {
            var p = End(n);
            var c = Cell(p);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!cells.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var l)) continue;
                        foreach (var m in l)
                        {
                            if (m / 2 == n / 2) continue;
                            float d = Vector3.Distance(p, End(m));
                            if (d <= join) { links[n].Add((m, d + 1f)); joins++; }
                        }
                    }
        }
        builtScene = scene;
        Plugin.L.LogInfo($"coo: route network {tracks.Count} tracks, {joins} joins (scene {scene})");
        return true;
    }

    // Nearest sample on any track within maxDist: (track, sample index, distance).
    (int track, int index, float dist) Nearest(Vector3 p, float maxDist, int skip = 1)
    {
        int bt = -1, bi = -1;
        float best = maxDist * maxDist;
        for (int t = 0; t < tracks.Count; t++)
        {
            var pts = tracks[t].P;
            for (int i = 0; i < pts.Length; i += skip)
            {
                float d = (pts[i] - p).sqrMagnitude;
                if (d < best) { best = d; bt = t; bi = i; }
            }
        }
        return (bt, bi, Mathf.Sqrt(best));
    }

    // Candidate entry/exit points: the nearest sample of each track that comes within range.
    List<(int track, int index, float dist)> Near(Vector3 p, float range, int max)
    {
        var list = new List<(int, int, float)>();
        float r2 = range * range;
        for (int t = 0; t < tracks.Count; t++)
        {
            var pts = tracks[t].P;
            int bi = -1;
            float best = r2;
            for (int i = 0; i < pts.Length; i++)
            {
                float d = (pts[i] - p).sqrMagnitude;
                if (d < best) { best = d; bi = i; }
            }
            if (bi >= 0) list.Add((t, bi, Mathf.Sqrt(best)));
        }
        list.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    // A route being planned over several frames: Step runs it for a slice of time. The free-space floods are a few
    // hundred thousand physics probes for a long route; done in one go they froze the game for 0.3–0.4 s at every
    // reroute (11-10 stream).
    public sealed class Job
    {
        readonly IEnumerator<bool> steps;
        public List<Vector3> Route;
        public string Why;
        public bool Done;
        public int Frames;
        public double Ms;
        public Job(IEnumerable<bool> s) { steps = s.GetEnumerator(); }

        public void Step(double budgetMs)
        {
            if (Done) return;
            Frames++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (sw.Elapsed.TotalMilliseconds < budgetMs)
                    if (!steps.MoveNext()) { Done = true; break; }
            }
            catch (Exception e)
            {
                Why = "规划出错：" + e.Message;
                Route = null;
                Done = true;
                Plugin.L.LogWarning("coo: route planning failed: " + e);
            }
            Ms += sw.Elapsed.TotalMilliseconds;
        }
    }

    // The flood around the goal, kept for the next plan to the same goal: a reroute only floods around the car.
    CooSpace goalFlood;
    Vector3 goalFloodAt;
    float goalFloodBand;
    int goalFloodScene = -1;

    public Job Plan(Vector3 from, Vector3 to, float minY, float maxY)
    {
        Job job = null;
        // the iterator body first runs on the first Step, by which time job is set
        job = new Job(PlanSteps(from, to, minY, maxY, () => job));
        return job;
    }

    bool Changed(int scene, Job job)
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex == scene) return false;
        job.Why = "规划途中换了场景";
        return true;
    }

    // Dev and tests: plan in one go.
    public List<Vector3> PlanNow(Vector3 from, Vector3 to, float minY, float maxY, out string why)
    {
        var job = Plan(from, to, minY, maxY);
        while (!job.Done) job.Step(1000);
        why = job.Why;
        return job.Route;
    }

    // A route as a list of points from the car to the goal, or null. Local free-space floods connect the car and the
    // goal to the network (a parking deck, a landing pad); the network carries the long middle.
    IEnumerable<bool> PlanSteps(Vector3 from, Vector3 to, float minY, float maxY, Func<Job> self)
    {
        var job = self();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        // the floods span the floor's whole allowed height band: lanes can run far below a parking deck
        float Up(Vector3 p) => Mathf.Clamp(maxY - p.y, 10f, 110f);
        float Down(Vector3 p) => Mathf.Clamp(p.y - minY, 10f, 110f);
        bool haveNet = EnsureBuilt();
        var nearFrom = haveNet ? Near(from, 150f, 40) : new List<(int track, int index, float dist)>();
        float direct = Vector3.Distance(from, to);
        var sf = new CooSpace(from, 150f, Up(from), Down(from), minY + 1.5f, maxY - 1.5f);
        var sTargets = new List<Vector3>();
        foreach (var c in nearFrom) sTargets.Add(tracks[c.track].P[c.index]);
        if (direct < 100f) sTargets.Add(to);
        foreach (var s in sf.FloodSteps(sTargets)) yield return s;
        if (Changed(scene, job)) yield break;
        if (direct < 100f && sf.CostTo(to, true) < float.MaxValue)
        {
            Plugin.L.LogInfo($"coo: local route ({sf.Probes} probes, {sw.ElapsedMilliseconds} ms over {job.Frames} frames)");
            job.Route = CooSpace.Pull(sf.PathTo(to, true), CooSpace.CarMask());
            yield break;
        }
        if (!haveNet) { job.Why = "这一层没有交通航线数据"; yield break; }
        var nearTo = Near(to, 160f, 40);
        float band = minY * 1000f + maxY;
        bool cached = goalFlood != null && goalFloodScene == scene && goalFloodBand == band && (goalFloodAt - to).sqrMagnitude < 1f;
        CooSpace gf;
        if (cached) gf = goalFlood;
        else
        {
            gf = new CooSpace(to, 160f, Up(to), Down(to), minY + 1.5f, maxY - 1.5f);
            var gTargets = new List<Vector3>();
            foreach (var c in nearTo) gTargets.Add(tracks[c.track].P[c.index]);
            foreach (var s in gf.FloodSteps(gTargets)) yield return s;
            if (Changed(scene, job)) yield break;
            goalFlood = gf;
            goalFloodAt = to;
            goalFloodBand = band;
            goalFloodScene = scene;
        }

        var starts = new List<(int track, int index, float dist)>();
        foreach (var c in nearFrom)
        {
            float cst = sf.CostTo(tracks[c.track].P[c.index]);
            if (cst < float.MaxValue) starts.Add((c.track, c.index, cst));
        }
        var goals = new List<(int track, int index, float dist)>();
        foreach (var c in nearTo)
        {
            float cst = gf.CostTo(tracks[c.track].P[c.index]);
            if (cst < float.MaxValue) goals.Add((c.track, c.index, cst));
        }
        if (starts.Count == 0)
        {
            var near = Near(from, 150f, 3);
            Plugin.L.LogInfo($"coo: no reachable entry; {near.Count} candidates near {Diag.V(from)}" +
                             (near.Count > 0 ? $", first {Diag.V(tracks[near[0].track].P[near[0].index])} cell {sf.CellOf(tracks[near[0].track].P[near[0].index])} cost {sf.CostTo(tracks[near[0].track].P[near[0].index])}" : "") +
                             $", flood probes {sf.Probes}");
            job.Why = "从这里飞不到附近的航线";
            yield break;
        }
        if (goals.Count == 0)
        {
            Plugin.L.LogInfo($"coo: no lane reachable from the goal; {nearTo.Count} lanes within 160 m, " + gf.Describe().Split('\n')[0]);
            job.Why = "目标附近没有能飞到的航线";
            yield break;
        }
        yield return true;

        int N = tracks.Count * 2;
        int START = N;
        var g = new float[N + 2];
        var prev = new int[N + 2];
        var prevEntry = new int[N + 2];
        for (int i = 0; i < g.Length; i++) { g[i] = float.MaxValue; prev[i] = -1; prevEntry[i] = -1; }
        var goalLinks = new Dictionary<int, List<(int cand, float cost)>>();
        for (int k = 0; k < goals.Count; k++)
        {
            var (t, i, d) = goals[k];
            var tr = tracks[t];
            void AddGoal(int node, float along)
            {
                if (!goalLinks.TryGetValue(node, out var l)) goalLinks[node] = l = new List<(int, float)>();
                l.Add((k, along + d));
            }
            AddGoal(t * 2, tr.S[i]);
            AddGoal(t * 2 + 1, (tr.Length - tr.S[i]) * 1.35f);
        }
        var open = new SortedSet<(float f, int node)>();
        float H(int node)
        {
            var tr = tracks[node / 2];
            var p = node % 2 == 0 ? tr.P[0] : tr.P[tr.P.Length - 1];
            return Vector3.Distance(p, to);
        }
        for (int k = 0; k < starts.Count; k++)
        {
            var (t, i, d) = starts[k];
            var tr = tracks[t];
            float toLast = tr.Length - tr.S[i] + d, toFirst = tr.S[i] * 1.35f + d;
            if (toLast < g[t * 2 + 1]) { g[t * 2 + 1] = toLast; prev[t * 2 + 1] = START; prevEntry[t * 2 + 1] = k; open.Add((toLast + H(t * 2 + 1), t * 2 + 1)); }
            if (toFirst < g[t * 2]) { g[t * 2] = toFirst; prev[t * 2] = START; prevEntry[t * 2] = k; open.Add((toFirst + H(t * 2), t * 2)); }
        }
        float bestGoal = float.MaxValue;
        int bestGoalNode = -1, bestCand = -1, sameTrackEntry = -1, sameTrackExit = -1;
        for (int a = 0; a < starts.Count; a++)
            for (int b = 0; b < goals.Count; b++)
                if (starts[a].track == goals[b].track)
                {
                    var tr = tracks[starts[a].track];
                    float along = Mathf.Abs(tr.S[goals[b].index] - tr.S[starts[a].index]) * (goals[b].index >= starts[a].index ? 1f : 1.35f);
                    float c = starts[a].dist + along + goals[b].dist;
                    if (c < bestGoal) { bestGoal = c; sameTrackEntry = a; sameTrackExit = b; bestGoalNode = -2; }
                }
        int expanded = 0;
        while (open.Count > 0)
        {
            var cur = open.Min;
            open.Remove(cur);
            int n = cur.node;
            if (cur.f >= bestGoal) break;
            if (++expanded > 200000) break;
            if (goalLinks.TryGetValue(n, out var gl))
                foreach (var (cand, cost) in gl)
                    if (g[n] + cost < bestGoal) { bestGoal = g[n] + cost; bestGoalNode = n; bestCand = cand; }
            foreach (var (m, cost) in links[n])
            {
                float ng = g[n] + cost;
                if (ng < g[m])
                {
                    g[m] = ng;
                    prev[m] = n;
                    open.Add((ng + H(m), m));
                }
            }
        }
        if (bestGoalNode == -1) { job.Why = "找不到连到目标的航线"; yield break; }

        var middle = new List<Vector3>();
        Vector3 entryPoint, exitPoint;
        if (bestGoalNode == -2)
        {
            var tr = tracks[starts[sameTrackEntry].track];
            AppendSamples(middle, tr, starts[sameTrackEntry].index, goals[sameTrackExit].index);
        }
        else
        {
            var chain = new List<int>();
            for (int n = bestGoalNode; n != -1 && n != START; n = prev[n]) chain.Add(n);
            chain.Reverse();
            int entry = prevEntry[chain[0]];
            var et = tracks[starts[entry].track];
            AppendSamples(middle, et, starts[entry].index, chain[0] % 2 == 0 ? 0 : et.P.Length - 1);
            for (int c = 1; c < chain.Count; c++)
            {
                int a = chain[c - 1], b = chain[c];
                if (a / 2 == b / 2)
                {
                    var tr = tracks[a / 2];
                    AppendSamples(middle, tr, a % 2 == 0 ? 0 : tr.P.Length - 1, b % 2 == 0 ? 0 : tr.P.Length - 1);
                }
            }
            var last = chain[chain.Count - 1];
            var xt = tracks[goals[bestCand].track];
            AppendSamples(middle, xt, last % 2 == 0 ? 0 : xt.P.Length - 1, goals[bestCand].index);
        }
        entryPoint = middle[0];
        exitPoint = middle[middle.Count - 1];
        int mask = CooSpace.CarMask();
        var head = CooSpace.Pull(sf.PathTo(entryPoint), mask) ?? new List<Vector3> { from, entryPoint };
        var tailRev = gf.PathTo(exitPoint);
        List<Vector3> tail;
        if (tailRev != null) { tailRev.Reverse(); tail = CooSpace.Pull(tailRev, mask); }
        else tail = new List<Vector3> { exitPoint, to };
        var route = new List<Vector3>(head);
        route.AddRange(middle);
        route.AddRange(tail);
        Plugin.L.LogInfo($"coo: route head {head.Count} pts, network {middle.Count} samples, tail {tail.Count} pts; floods {sf.Probes}+{gf.Probes} probes{(cached ? " (goal side kept from the last plan)" : "")}, {sw.ElapsedMilliseconds} ms wall over {job.Frames} frames");
        job.Route = Simplify(route);
    }

    static void AppendSamples(List<Vector3> route, Track tr, int from, int to)
    {
        int step = to >= from ? 1 : -1;
        for (int i = from; ; i += step)
        {
            route.Add(tr.P[i]);
            if (i == to) break;
        }
    }

    // Drop points closer than 4 m to the previous one and points on straight stretches.
    static List<Vector3> Simplify(List<Vector3> pts)
    {
        var a = new List<Vector3>();
        foreach (var p in pts)
            if (a.Count == 0 || (p - a[a.Count - 1]).sqrMagnitude > 16f) a.Add(p);
        if (a.Count < 3) return a;
        var b = new List<Vector3> { a[0] };
        for (int i = 1; i < a.Count - 1; i++)
        {
            var d1 = (a[i] - b[b.Count - 1]).normalized;
            var d2 = (a[i + 1] - a[i]).normalized;
            if (Vector3.Dot(d1, d2) < 0.9995f || (a[i] - b[b.Count - 1]).magnitude > 40f) b.Add(a[i]);
        }
        b.Add(a[a.Count - 1]);
        return b;
    }

    public static float Length(List<Vector3> r)
    {
        float s = 0;
        for (int i = 1; i < r.Count; i++) s += Vector3.Distance(r[i - 1], r[i]);
        return s;
    }
}
