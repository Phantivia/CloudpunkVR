using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CloudpunkVR;

// Location scouting for the promo film (dev channel): where the loaded city is, how high its walkable surfaces are.
//   scenes                      loaded scenes and their root object counts
//   probe <x> <z> [r] [step]    downward ray casts over a square grid (centre x,z, half size r, spacing step):
//                               the top hit's height and collider per cell, as a small table in the log
//   probehits <x> <z>           every hit straight down at one point (layers of a vertical city)
internal static class FilmScout
{
    public static void Scenes()
    {
        var sb = new StringBuilder("scenes:");
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            sb.Append($"\n  {s.name} loaded={s.isLoaded} roots={s.rootCount}");
        }
        var rs = Object.FindObjectsOfType<Renderer>();
        if (rs.Length > 0)
        {
            var b = rs[0].bounds;
            foreach (var r in rs) if (r.enabled && r.gameObject.activeInHierarchy) b.Encapsulate(r.bounds);
            sb.Append($"\n  renderers={rs.Length} bounds min={Diag.V(b.min)} max={Diag.V(b.max)}");
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    public static void Probe(float x, float z, float r, float step)
    {
        var sb = new StringBuilder($"probe centre ({x:F0},{z:F0}) r={r:F0} step={step:F0}: top hit height per cell (rows = z descending, cols = x ascending; '.' = no hit)\n");
        for (float zz = z + r; zz >= z - r - 0.01f; zz -= step)
        {
            sb.Append($"z={zz,6:F0} |");
            for (float xx = x - r; xx <= x + r + 0.01f; xx += step)
            {
                if (Physics.Raycast(new Vector3(xx, 2000f, zz), Vector3.down, out var hit, 4000f, ~0, QueryTriggerInteraction.Ignore))
                    sb.Append($"{hit.point.y,6:F0}");
                else sb.Append("     .");
            }
            sb.Append('\n');
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    // neon [cell] [top]: where the city glows. Bins every enabled renderer whose material is emissive (shader name or an
    // emission map) or whose name reads like a sign, and every light, into cells (cell m wide, cell/2 tall); logs the
    // densest cells with their centre, counts and a few names.
    public static void Neon(float cell, int top)
    {
        var bins = new System.Collections.Generic.Dictionary<(int, int, int), (int glow, int lights, int all, System.Collections.Generic.List<string> names)>();
        (int, int, int) Key(Vector3 p) => (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / (cell * 0.5f)), Mathf.FloorToInt(p.z / cell));
        var signName = new System.Text.RegularExpressions.Regex("neon|sign|holo|billboard|advert|screen|logo|led|light|lamp|glow|emiss", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            var k = Key(r.bounds.center);
            bins.TryGetValue(k, out var b);
            b.names ??= new System.Collections.Generic.List<string>();
            b.all++;
            bool glow = signName.IsMatch(r.name);
            if (!glow)
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    var sn = m.shader != null ? m.shader.name : "";
                    if (sn.Contains("Emissive") || sn.Contains("Hologram") || sn.Contains("Unlit") || (m.HasProperty("_EmissionMap") && m.GetTexture("_EmissionMap") != null)) { glow = true; break; }
                }
            if (glow) { b.glow++; if (b.names.Count < 4 && !b.names.Contains(r.name)) b.names.Add(r.name); }
            bins[k] = b;
        }
        foreach (var l in Object.FindObjectsOfType<Light>())
        {
            if (!l.enabled || !l.gameObject.activeInHierarchy || l.type == LightType.Directional) continue;
            var k = Key(l.transform.position);
            bins.TryGetValue(k, out var b);
            b.names ??= new System.Collections.Generic.List<string>();
            b.lights++;
            bins[k] = b;
        }
        var sb = new StringBuilder($"neon cells ({cell:F0} m, {cell * 0.5f:F0} m tall), densest first: centre x,y,z | glowing renderers | lights | all renderers | names");
        foreach (var kv in bins.OrderByDescending(kv => kv.Value.glow + kv.Value.lights * 2).Take(top))
        {
            var c = new Vector3((kv.Key.Item1 + 0.5f) * cell, (kv.Key.Item2 + 0.5f) * cell * 0.5f, (kv.Key.Item3 + 0.5f) * cell);
            sb.Append($"\n  {c.x,6:F0} {c.y,5:F0} {c.z,6:F0} | {kv.Value.glow,4} | {kv.Value.lights,3} | {kv.Value.all,5} | {string.Join(", ", kv.Value.names)}");
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    // findclear <x> <z> <r> <step> [ymin] [ymax]: places for the promo stage (a 9 m radius, 14 m tall cylinder): flat
    // walkable ground (downward hits within ±0.35 m across the disk), nothing within 9.3 m sideways from 0.3 to 13 m up,
    // and how much glows within 45 m (emissive renderers + 3 × lights). Best first.
    public static void FindClear(float x0, float z0, float r, float step, float ymin, float ymax)
    {
        var glow = new System.Collections.Generic.List<Vector3>();
        var signName = new System.Text.RegularExpressions.Regex("neon|sign|holo|billboard|advert|screen|logo|led|lamp|glow", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var rr in Object.FindObjectsOfType<Renderer>())
        {
            if (!rr.enabled || !rr.gameObject.activeInHierarchy) continue;
            bool g = signName.IsMatch(rr.name);
            if (!g) foreach (var m in rr.sharedMaterials) { if (m != null && m.shader != null && (m.shader.name.Contains("Emissive") || m.shader.name.Contains("Hologram"))) { g = true; break; } }
            if (g) glow.Add(rr.bounds.center);
        }
        foreach (var l in Object.FindObjectsOfType<Light>()) if (l.enabled && l.type != LightType.Directional) { glow.Add(l.transform.position); glow.Add(l.transform.position); glow.Add(l.transform.position); }
        var found = new System.Collections.Generic.List<(Vector3 p, int score, float floor, string what)>();
        float[] hs = { 0.3f, 2f, 5f, 9f, 13f };
        for (float x = x0 - r; x <= x0 + r + 0.01f; x += step)
            for (float z = z0 - r; z <= z0 + r + 0.01f; z += step)
                foreach (var h in Physics.RaycastAll(new Vector3(x, 2000f, z), Vector3.down, 4000f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.normal.y < 0.95f || h.point.y < ymin || h.point.y > ymax) continue;
                    var p = h.point;
                    bool blocked = false;
                    for (int a = 0; a < 24 && !blocked; a++)
                    {
                        var dir = Quaternion.Euler(0, a * 15f, 0) * Vector3.forward;
                        foreach (var hh in hs)
                            if (Physics.Raycast(p + Vector3.up * hh, dir, 9.3f, ~0, QueryTriggerInteraction.Ignore)) { blocked = true; break; }
                    }
                    if (blocked || Physics.Raycast(p + Vector3.up * 0.3f, Vector3.up, 14f, ~0, QueryTriggerInteraction.Ignore)) continue;
                    int ok = 0, n = 0;
                    foreach (var rad in new[] { 3f, 6f, 8.5f })
                        for (int a = 0; a < 8; a++)
                        {
                            n++;
                            var q = p + Quaternion.Euler(0, a * 45f, 0) * Vector3.forward * rad;
                            if (Physics.Raycast(q + Vector3.up * 1.0f, Vector3.down, out var fh, 1.4f, ~0, QueryTriggerInteraction.Ignore) && Mathf.Abs(fh.point.y - p.y) < 0.35f) ok++;
                        }
                    int score = 0;
                    foreach (var gp in glow) if ((gp - p).sqrMagnitude < 45f * 45f) score++;
                    found.Add((p, score, ok / (float)n, Diag.PathOf(h.collider.transform)));
                }
        var sb = new StringBuilder($"findclear ({x0:F0},{z0:F0}) r={r:F0}: {found.Count} clear spots; best (floor ≥ 0.8) by glow within 45 m:");
        foreach (var f in found.Where(f => f.floor >= 0.8f).OrderByDescending(f => f.score).Take(25))
            sb.Append($"\n  {Diag.V(f.p)} glow={f.score} floor={f.floor:F2} on {f.what}");
        Plugin.L.LogInfo(sb.ToString());
    }

    // where <path> [path…]: world pose and renderer bounds of objects (inactive ones too)
    public static void Where(string[] paths)
    {
        var sb = new StringBuilder("where:");
        foreach (var p in paths)
        {
            var t = Film.FindAny(p.Replace("%20", " ").Replace("+", " "));
            if (t == null) { sb.Append($"\n  {p}: not found"); continue; }
            var rs = t.GetComponentsInChildren<Renderer>(true);
            var b = rs.Length > 0 ? rs[0].bounds : new Bounds(t.position, Vector3.zero);
            foreach (var r in rs) b.Encapsulate(r.bounds);
            sb.Append($"\n  {Diag.PathOf(t)} active={t.gameObject.activeInHierarchy} pos={Diag.V(t.position)} euler={Diag.V(t.eulerAngles)} scale={Diag.V(t.lossyScale)} layer={LayerMask.LayerToName(t.gameObject.layer)} bounds c={Diag.V(b.center)} size={Diag.V(b.size)} renderers={rs.Length}");
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    public static void Layers()
    {
        var sb = new StringBuilder("layers:");
        for (int i = 0; i < 32; i++) { var n = LayerMask.LayerToName(i); if (!string.IsNullOrEmpty(n)) sb.Append($" {i}={n}"); }
        foreach (var c in Camera.allCameras) sb.Append($"\n  {Diag.PathOf(c.transform)} mask=0x{c.cullingMask:X8} far={c.farClipPlane} layerCull={string.Join(",", c.layerCullDistances.Select((d, i) => d > 0 ? $"{i}:{d:F0}" : null).Where(x => x != null))}");
        Plugin.L.LogInfo(sb.ToString());
    }

    // slice <x> <z> <r> <step> <y> [y …]: free space for a camera path at these heights, as PNGs in BepInEx/…/film/
    // (slice_y<height>.png, one pixel per step metres, north up): black = a 1.5 m sphere there hits something, dark grey =
    // free, lighter grey = free with ground within 12 m below (a street), red = glowing renderers within ±20 m of that
    // height (brighter = more), blue lines every 50 m, green = the given centre. Pixel (i, j) is
    // x = x0 − r + i·step, z = z0 + r − j·step.
    public static void Slice(float x0, float z0, float r, float step, float[] ys)
    {
        int n = Mathf.Max(2, Mathf.RoundToInt(2 * r / step) + 1);
        var glow = new System.Collections.Generic.List<Vector3>();
        var signName = new System.Text.RegularExpressions.Regex("neon|sign|holo|billboard|advert|screen|logo|led|lamp|glow", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var rr in Object.FindObjectsOfType<Renderer>())
        {
            if (!rr.enabled || !rr.gameObject.activeInHierarchy) continue;
            bool g = signName.IsMatch(rr.name);
            if (!g) foreach (var m in rr.sharedMaterials) { if (m != null && m.shader != null && (m.shader.name.Contains("Emissive") || m.shader.name.Contains("Hologram") || m.shader.name.Contains("Unlit"))) { g = true; break; } }
            if (g) glow.Add(rr.bounds.center);
        }
        string dir = System.IO.Path.Combine(Plugin.Dir, "film");
        System.IO.Directory.CreateDirectory(dir);
        var sb = new StringBuilder($"slice centre ({x0:F0},{z0:F0}) r={r:F0} step={step:F1}: {n}x{n} px, x = {x0 - r:F0} + i·{step:F1}, z = {z0 + r:F0} − j·{step:F1}");
        foreach (var y in ys)
        {
            var px = new Color32[n * n];
            var heat = new float[n * n];
            foreach (var gp in glow)
            {
                if (Mathf.Abs(gp.y - y) > 20f) continue;
                int i = Mathf.RoundToInt((gp.x - (x0 - r)) / step), j = Mathf.RoundToInt(((z0 + r) - gp.z) / step);
                for (int dj = -2; dj <= 2; dj++)
                    for (int di = -2; di <= 2; di++)
                    {
                        int ii = i + di, jj = j + dj;
                        if (ii >= 0 && jj >= 0 && ii < n && jj < n) heat[jj * n + ii] += 1f / (1 + di * di + dj * dj);
                    }
            }
            int free = 0;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var p = new Vector3(x0 - r + i * step, y, z0 + r - j * step);
                    bool blocked = Physics.CheckSphere(p, 1.5f, ~0, QueryTriggerInteraction.Ignore);
                    byte v = 0;
                    if (!blocked)
                    {
                        free++;
                        v = Physics.Raycast(p, Vector3.down, 12f, ~0, QueryTriggerInteraction.Ignore) ? (byte)150 : (byte)70;
                    }
                    float h = Mathf.Clamp01(heat[j * n + i] / 6f);
                    var c = new Color32((byte)Mathf.Min(255, v + 200 * h), (byte)(v * (1 - h * 0.7f)), (byte)(v * (1 - h * 0.7f)), 255);
                    float wx = p.x, wz = p.z;
                    if (Mathf.Abs(Mathf.Repeat(wx, 50f)) < step * 0.5f || Mathf.Abs(Mathf.Repeat(wz, 50f)) < step * 0.5f) c = new Color32((byte)(c.r / 2), (byte)(c.g / 2), (byte)Mathf.Min(255, c.b / 2 + 110), 255);
                    if (Mathf.Abs(wx - x0) < step * 1.5f && Mathf.Abs(wz - z0) < step * 1.5f) c = new Color32(0, 255, 0, 255);
                    // texture rows go bottom-up: row 0 = south edge
                    px[(n - 1 - j) * n + i] = c;
                }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            string path = System.IO.Path.Combine(dir, $"slice_y{y:F0}.png");
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            Object.Destroy(tex);
            sb.Append($"\n  y={y:F0}: {free * 100 / (n * n)}% free → {path}");
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    // pathcheck <job.json> [radius]: sweeps a sphere between consecutive camera keys of a film job (dense keys make this
    // the real path) and logs what it would pass through.
    public static void PathCheck(string jobPath, float radius)
    {
        var j = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(jobPath)).AsObject();
        var keys = j["keys"].AsArray();
        var sb = new StringBuilder($"pathcheck {System.IO.Path.GetFileName(jobPath)} r={radius:F1}:");
        int hits = 0;
        for (int k = 0; k + 1 < keys.Count; k++)
        {
            if ((bool?)keys[k + 1]["rig"] == true || (bool?)keys[k]["rig"] == true) continue;
            var a = keys[k]["position"].AsArray(); var b = keys[k + 1]["position"].AsArray();
            var pa = new Vector3((float)a[0], (float)a[1], (float)a[2]); var pb = new Vector3((float)b[0], (float)b[1], (float)b[2]);
            var d = pb - pa;
            if (d.sqrMagnitude < 1e-6f) continue;
            bool inside = Physics.CheckSphere(pa, radius, ~0, QueryTriggerInteraction.Ignore);
            RaycastHit hit = default;
            if (inside || Physics.SphereCast(pa, radius, d.normalized, out hit, d.magnitude, ~0, QueryTriggerInteraction.Ignore))
            {
                hits++;
                if (hits <= 60)
                    sb.Append($"\n  t={(double)keys[k]["t"]:F2} {Diag.V(pa)} → {Diag.V(pb)}: " + (inside ? "starts inside geometry" : $"hits {Diag.PathOf(hit.collider.transform)} at {hit.distance:F1} m"));
            }
        }
        sb.Append($"\n  {hits} of {keys.Count - 1} segments blocked");
        Plugin.L.LogInfo(sb.ToString());
    }

    public static void ProbeHits(float x, float z)
    {
        var hits = Physics.RaycastAll(new Vector3(x, 2000f, z), Vector3.down, 4000f, ~0, QueryTriggerInteraction.Ignore)
            .OrderByDescending(h => h.point.y).Take(40);
        var sb = new StringBuilder($"probehits ({x:F1},{z:F1}):");
        foreach (var h in hits)
            sb.Append($"\n  y={h.point.y:F2} n={Diag.V(h.normal)} layer={LayerMask.LayerToName(h.collider.gameObject.layer)} {Diag.PathOf(h.collider.transform)}");
        Plugin.L.LogInfo(sb.ToString());
    }
}
