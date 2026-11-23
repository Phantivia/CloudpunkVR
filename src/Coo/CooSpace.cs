using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloudpunkVR.Coo;

// Free space around a point for the car, in 5 m cells: a bounded Dijkstra flood that asks the physics scene whether
// a car-sized box fits in each cell (only against the layers the player's car collides with, minus moving traffic).
// Used where the traffic network does not reach: from a parking deck out to the lanes, from the lanes down to a
// parking spot, around something the car got stuck on.
internal sealed class CooSpace
{
    public const float Cell = 5f;
    static readonly Vector3 CarHalf = new(2.3f, 1.4f, 2.3f);

    readonly Vector3 origin;
    readonly int rx, ry, rz;          // half extents in cells
    readonly float[] cost;
    readonly int[] prev;
    readonly sbyte[] free;            // -1 unknown, 0 blocked, 1 free
    readonly int mask;
    readonly float minY, maxY;
    public int Probes { get; private set; }

    static int cachedMask, cachedFor;

    public static int CarMask()
    {
        var g = Global.Instance;
        var car = g != null ? g.playerCar : null;
        if (car == null) return Physics.DefaultRaycastLayers;
        if (cachedFor == car.GetInstanceID() && cachedMask != 0) return cachedMask;
        // what the city is built of; the car's own colliders are left out (Physics.GetIgnoreLayerCollision reports
        // nothing for the car's layer in this build, so the layers are named)
        int own = 0, m = 0;
        foreach (var c in car.GetComponentsInChildren<Collider>(true))
            if (!c.isTrigger) own |= 1 << c.gameObject.layer;
        foreach (var name in new[] { "Default", "Floor", "Obstacle", "Streets", "StreetDetails", "MidRangeDetail", "Lamps", "Water" })
        {
            int l = LayerMask.NameToLayer(name);
            if (l >= 0) m |= 1 << l;
        }
        int carLayer = car.gameObject.layer;
        // not the car itself, the player, or traffic that will have moved by the time the car gets there
        foreach (var name in new[] { "Player", "PlayerCar", "Vehicle", "Racer", "Character", "PlayerVolume", "VehicleVolume", "RacingVolume", "Interaction", "Interactable" })
        {
            int l = LayerMask.NameToLayer(name);
            if (l >= 0) m &= ~(1 << l);
        }
        m &= ~(1 << carLayer);
        m &= ~own;
        cachedMask = m;
        cachedFor = car.GetInstanceID();
        var names = new System.Collections.Generic.List<string>();
        for (int l = 0; l < 32; l++) if ((m & (1 << l)) != 0) names.Add(LayerMask.LayerToName(l));
        Plugin.L.LogInfo($"coo: car collides with {string.Join(",", names)} (car colliders on 0x{own:x})");
        return m;
    }

    public CooSpace(Vector3 center, float radius, float up, float down, float minY, float maxY)
    {
        origin = center;
        rx = rz = Mathf.CeilToInt(radius / Cell);
        ry = Mathf.CeilToInt(Mathf.Max(up, down) / Cell);   // symmetric grid; cells outside the band are blocked
        this.minY = minY;
        this.maxY = maxY;
        int n = (2 * rx + 1) * (2 * ry + 1) * (2 * rz + 1);
        cost = new float[n];
        prev = new int[n];
        free = new sbyte[n];
        for (int i = 0; i < n; i++) { cost[i] = float.MaxValue; prev[i] = -1; free[i] = -1; }
        mask = CarMask();
    }

    int Index(int x, int y, int z) => ((x + rx) * (2 * ry + 1) + (y + ry)) * (2 * rz + 1) + (z + rz);
    void Coords(int i, out int x, out int y, out int z)
    {
        int sz = 2 * rz + 1, sy = 2 * ry + 1;
        z = i % sz - rz;
        y = i / sz % sy - ry;
        x = i / sz / sy - rx;
    }
    public Vector3 CenterOf(int i)
    {
        Coords(i, out var x, out var y, out var z);
        return origin + new Vector3(x, y, z) * Cell;
    }
    public int CellOf(Vector3 p)
    {
        var d = (p - origin) / Cell;
        int x = Mathf.RoundToInt(d.x), y = Mathf.RoundToInt(d.y), z = Mathf.RoundToInt(d.z);
        if (Math.Abs(x) > rx || Math.Abs(y) > ry || Math.Abs(z) > rz) return -1;
        return Index(x, y, z);
    }

    bool Free(int i)
    {
        if (free[i] >= 0) return free[i] == 1;
        var c = CenterOf(i);
        bool ok = c.y > minY && c.y < maxY && !Physics.CheckBox(c, CarHalf, Quaternion.identity, mask, QueryTriggerInteraction.Ignore) && !Inside(c);
        Probes++;
        free[i] = (sbyte)(ok ? 1 : 0);
        return ok;
    }

    // Inside a thick piece of the city (a deck slab, a building block): an overlap test sees only a mesh collider's
    // surface, so a car box that fits wholly inside reads as free, and the flood walked into parking decks sideways and
    // out at the top (11-10). Inside means the faces straight above and straight below are both seen from behind and
    // are less than 10 m apart: a slab. A lift's tube is a closed shell too, but its hollow is 38 m tall and the car
    // flies in it.
    bool Inside(Vector3 c)
    {
        bool was = Physics.queriesHitBackfaces;
        Physics.queriesHitBackfaces = true;
        try
        {
            if (!Physics.Raycast(c, Vector3.up, out var up, 10f, mask, QueryTriggerInteraction.Ignore) || up.normal.y <= 0) return false;
            return Physics.Raycast(c, Vector3.down, out var down, 10f - up.distance, mask, QueryTriggerInteraction.Ignore) && down.normal.y < 0;
        }
        finally { Physics.queriesHitBackfaces = was; }
    }

    // A cast that also hits faces from behind: one that starts inside something then hits its far side.
    bool CastHits(Vector3 from, Vector3 half, Vector3 dir, float dist)
    {
        bool was = Physics.queriesHitBackfaces;
        Physics.queriesHitBackfaces = true;
        try { return Physics.BoxCast(from, half, dir, Quaternion.identity, dist, mask, QueryTriggerInteraction.Ignore); }
        finally { Physics.queriesHitBackfaces = was; }
    }

    // A move up or down between two free cells. Routes went up through parking decks (11-10): the car's box at two
    // stacked cell centres leaves about 2 m between them unchecked, and an overlap test does not see a box that lies
    // wholly inside a thick floor (a mesh collider is only its surface). A move between levels is allowed when both
    // the column it leaves from and the column it arrives in are open from the lower cell to the upper one: a flat
    // box swept up between their centres hits any floor in between. One sweep per cell, cached.
    sbyte[] vclear;     // cell -> open up to the cell above: -1 unknown, 0 no, 1 yes

    bool EdgeFree(int x, int y, int z, int dx, int dy, int dz)
    {
        int low = Math.Min(y, y + dy);
        return OpenUp(x, low, z) && (dx == 0 && dz == 0 || OpenUp(x + dx, low, z + dz));
    }

    static readonly Vector3 SweepHalf = new(2.0f, 0.3f, 2.0f);

    bool OpenUp(int x, int y, int z)
    {
        if (y + 1 > ry) return false;
        int i = Index(x, y, z);
        vclear ??= NewUnknown(cost.Length);
        if (vclear[i] >= 0) return vclear[i] == 1;
        bool ok = Free(i) && Free(Index(x, y + 1, z));
        if (ok)
        {
            ok = !CastHits(CenterOf(i), SweepHalf, Vector3.up, Cell);
            Probes++;
        }
        vclear[i] = (sbyte)(ok ? 1 : 0);
        return ok;
    }

    static sbyte[] NewUnknown(int n)
    {
        var a = new sbyte[n];
        for (int k = 0; k < n; k++) a[k] = -1;
        return a;
    }

    // Costs from the center to every reachable cell (vertical moves cost a little more: the car climbs slower than it flies).
    public void Flood(int maxProbes = 150000)
    {
        foreach (var _ in FloodSteps(null, maxProbes)) { }
    }

    // The same flood in small steps (one yield about every 32 probes) so a route can be planned over several frames.
    // With targets (points the caller will ask CostTo/PathTo about) it stops once each target's cell or a neighbour
    // has its final cost, or once the flood has gone `slack` metres past the first target it reached: the costs
    // beyond that are never used.
    public IEnumerable<bool> FloodSteps(List<Vector3> targets, int maxProbes = 150000, float slack = 150f)
    {
        int start = Index(0, 0, 0);
        free[start] = 1;
        cost[start] = 0;
        var open = new SortedSet<(float, int)> { (0, start) };
        HashSet<int> waiting = null;
        Dictionary<int, List<int>> waitersOf = null;
        float stopAt = float.MaxValue;
        if (targets != null && targets.Count > 0)
        {
            // a target is settled when its own cell or any neighbour is (CostTo falls back to the neighbours)
            waiting = new HashSet<int>();
            waitersOf = new Dictionary<int, List<int>>();
            for (int k = 0; k < targets.Count; k++)
            {
                int ti = CellOf(targets[k]);
                if (ti < 0) continue;
                waiting.Add(k);
                Coords(ti, out var x, out var y, out var z);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            int nx = x + dx, ny = y + dy, nz = z + dz;
                            if (Math.Abs(nx) > rx || Math.Abs(ny) > ry || Math.Abs(nz) > rz) continue;
                            int j = Index(nx, ny, nz);
                            if (!waitersOf.TryGetValue(j, out var l)) waitersOf[j] = l = new List<int>();
                            l.Add(k);
                        }
            }
            if (waiting.Count == 0) waiting = null;
        }
        int sinceYield = 0;
        while (open.Count > 0 && Probes < maxProbes)
        {
            var (c, i) = open.Min;
            open.Remove(open.Min);
            if (c > cost[i]) continue;
            if (waiting != null)
            {
                if (c > stopAt) break;
                if (waitersOf.TryGetValue(i, out var ws))
                {
                    foreach (var k in ws) waiting.Remove(k);
                    if (stopAt == float.MaxValue) stopAt = c + slack;
                    if (waiting.Count == 0) break;
                }
            }
            int before = Probes;
            Coords(i, out var x, out var y, out var z);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (Math.Abs(nx) > rx || Math.Abs(ny) > ry || Math.Abs(nz) > rz) continue;
                        int j = Index(nx, ny, nz);
                        if (!Free(j)) continue;
                        if (dy != 0 && !EdgeFree(x, y, z, dx, dy, dz)) continue;
                        float step = Cell * Mathf.Sqrt(dx * dx + dz * dz + dy * dy * 1.6f);
                        float nc = c + step;
                        if (nc < cost[j]) { cost[j] = nc; prev[j] = i; open.Add((nc, j)); }
                    }
            sinceYield += Probes - before + 1;
            if (sinceYield >= 32) { sinceYield = 0; yield return true; }
        }
    }

    // strict: only a cell with nothing between its centre and p (for the destination itself; lane samples often sit
    // against a wall or inside a lift tube, and any reached neighbour does for them)
    public float CostTo(Vector3 p, bool strict = false)
    {
        int i = Into(p, strict);
        return i < 0 ? float.MaxValue : cost[i] + Vector3.Distance(CenterOf(i), p);
    }

    // The reached cell a path to p ends in: p's own cell, or when that is blocked (a lane sample next to a wall, a
    // parking spot full of deck) the cheapest reached neighbour, either way only one with nothing between its centre
    // and p. Taking any neighbour drew the last stretch from a cell under a parking deck straight up through it.
    int Into(Vector3 p, bool strict)
    {
        int i = CellOf(p);
        if (i < 0) return -1;
        if (cost[i] < float.MaxValue && (!strict || Leads(i, p))) return i;
        int bj = -1;
        float best = float.MaxValue;
        Coords(i, out var x, out var y, out var z);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (Math.Abs(nx) > rx || Math.Abs(ny) > ry || Math.Abs(nz) > rz) continue;
                    int j = Index(nx, ny, nz);
                    if (cost[j] == float.MaxValue) continue;
                    float c = cost[j] + Vector3.Distance(CenterOf(j), p);
                    if (c < best && (!strict || Leads(j, p))) { best = c; bj = j; }
                }
        return bj;
    }

    bool Leads(int j, Vector3 p)
    {
        var a = CenterOf(j);
        var d = p - a;
        float len = d.magnitude;
        if (len < 1.5f) return true;
        return !CastHits(a, new Vector3(1f, 1f, 1f), d / len, len - 1f);
    }

    // Cell centres from the flood's center to p (inclusive of p), or null if unreached.
    public List<Vector3> PathTo(Vector3 p, bool strict = false)
    {
        int i = Into(p, strict);
        if (i < 0) return null;
        var list = new List<Vector3>();
        for (int k = i; k >= 0; k = prev[k]) list.Add(CenterOf(k));
        list.Reverse();
        list[0] = origin;
        list.Add(p);
        if (DevCommands.Enabled && list.Count <= 40)
            Plugin.L.LogInfo($"space: path from {Diag.V(origin)} to {Diag.V(p)} via cell {i} (own cell {CellOf(p)} reached {CellOf(p) >= 0 && cost[CellOf(p)] < float.MaxValue}): {string.Join(" ", list.ConvertAll(Diag.V))}");
        return list;
    }

    // Dev: what blocks the cells around the center, and how far the flood got.
    public string Describe()
    {
        int reached = 0;
        float far = 0;
        for (int i = 0; i < cost.Length; i++)
            if (cost[i] < float.MaxValue) { reached++; far = Mathf.Max(far, Vector3.Distance(CenterOf(i), origin)); }
        var sb = new System.Text.StringBuilder($"space: probes={Probes} reached={reached} farthest={far:F0}m mask=0x{mask:x} band=[{minY:F0},{maxY:F0}]\n");
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var c = origin + new Vector3(dx, dy, dz) * Cell;
                    var hits = Physics.OverlapBox(c, CarHalf, Quaternion.identity, mask, QueryTriggerInteraction.Ignore);
                    if (hits.Length == 0) continue;
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var h in hits) names.Add($"{h.name}[{LayerMask.LayerToName(h.gameObject.layer)}]");
                    sb.Append($"  ({dx},{dy},{dz}) blocked by {string.Join(", ", names)}\n");
                }
        return sb.ToString();
    }

    // Leave out points the car can fly past in a straight line (a sphere the car's width gets through).
    public static List<Vector3> Pull(List<Vector3> pts, int mask)
    {
        if (pts == null || pts.Count < 3) return pts;
        var o = new List<Vector3> { pts[0] };
        int i = 0;
        while (i < pts.Count - 1)
        {
            int j = pts.Count - 1;
            for (; j > i + 1; j--)
            {
                var a = pts[i];
                var d = pts[j] - a;
                if (!Physics.SphereCast(a, 1.8f, d.normalized, out _, d.magnitude, mask, QueryTriggerInteraction.Ignore)) break;
            }
            o.Add(pts[j]);
            i = j;
        }
        return o;
    }
}
