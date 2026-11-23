using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using UnityEngine;

namespace CloudpunkVR.Coo;

// The places a navigator can search, from the game's own objects: gas stations (`GasStation`), garages (`Garage`),
// parking spots (`ParkVolume`), shops and buyers (characters with item offers or who buy items), the other named
// characters on the map, the named place labels of the full map (`MinimapObject` labels), and the quest markers.
// Rebuilt at most every 20 s; ids stay the same while a place stays where it is.
internal static class CooPlaces
{
    public sealed class Place
    {
        public string Id, Name, Kind;
        public Vector3 Pos;
        public bool Quest;
    }

    static List<Place> cache;
    static float cachedAt = -100;

    public static List<Place> All()
    {
        float now = Time.unscaledTime;
        if (cache != null && now - cachedAt < 20f) return cache;
        cachedAt = now;
        cache = Build();
        return cache;
    }

    // words people use for a kind
    static readonly (string kind, string[] words)[] Synonyms =
    {
        ("加油站", new[] { "加油", "油站", "汽油", "燃料" }),
        ("车库", new[] { "修车", "改装", "车库", "维修" }),
        ("停车位", new[] { "停车", "车位", "泊车" }),
        ("商店", new[] { "商店", "买东西", "店", "商贩", "小贩", "卖" }),
        ("收购", new[] { "收购", "卖掉", "回收" }),
    };

    static string Localized(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var g = Global.Instance;
        string s = null;
        try { if (g != null && g.GetLocalizedText(id, out s) && !string.IsNullOrEmpty(s)) return CooGame.Clean(s); }
        catch { /* not a text id */ }
        return "";
    }

    static bool InScene(Component c) => c != null && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy;

    static string CharName(Character c)
    {
        var n = c.nameObj != null && !string.IsNullOrEmpty(c.nameObj.text) ? c.nameObj.text : c.definedName;
        if (string.IsNullOrEmpty(n) && c.definition != null) n = c.definition.localizedName;
        return CooGame.Clean(n);
    }

    static List<Place> Build()
    {
        var list = new List<Place>();
        foreach (var gs in Resources.FindObjectsOfTypeAll<GasStation>())
            if (InScene(gs)) list.Add(new Place { Name = "加油站", Kind = "加油站", Pos = gs.transform.position });
        foreach (var gr in Resources.FindObjectsOfTypeAll<Garage>())
            if (InScene(gr)) list.Add(new Place { Name = "车库", Kind = "车库", Pos = gr.transform.position });
        foreach (var pv in Resources.FindObjectsOfTypeAll<ParkVolume>())
            if (InScene(pv) && pv.volumeCollider != null) list.Add(new Place { Name = "停车位", Kind = "停车位", Pos = pv.volumeCollider.bounds.center });
        foreach (var c in Resources.FindObjectsOfTypeAll<Character>())
        {
            if (!InScene(c)) continue;
            bool sells = c.itemOffers != null && c.itemOffers.Length > 0;
            if (!sells && !c.buysItems && c.minimapObj == null) continue;
            var n = CharName(c);
            if (n.Length == 0) continue;
            list.Add(new Place { Name = n, Kind = sells ? "商店" : c.buysItems ? "收购" : "人物", Pos = c.transform.position });
        }
        foreach (var mo in Resources.FindObjectsOfTypeAll<MinimapObject>())
        {
            if (mo == null || !mo.gameObject.scene.IsValid() || !mo.isLabel || mo.quest != null) continue;
            var lt = mo.GetComponentInChildren<LocationText>(true);
            string name = lt != null ? CooGame.Clean(lt.localizedContent) : "";
            if (name.Length == 0) name = Localized(mo.LabelID);
            if (name.Length == 0 && lt != null && lt.textElement != null) name = CooGame.Clean(lt.textElement.text);
            if (name.Length > 0) list.Add(new Place { Name = name, Kind = "地名", Pos = mo.transform.position });
        }
        foreach (var q in CooGame.ActiveQuests())
            if (CooGame.HasMarker(q))
                list.Add(new Place { Name = CooGame.Clean(q.text), Kind = "任务", Pos = CooGame.MarkerPos(q, out _), Quest = true, Id = "quest:" + q.id });
        var seen = new HashSet<string>();
        var outp = new List<Place>();
        foreach (var p in list)
        {
            p.Id ??= Id(p);
            if (!seen.Add(p.Id)) continue;
            // a gas station or garage is several trigger components a few tens of metres apart: one place
            if ((p.Kind == "加油站" || p.Kind == "车库") && outp.Exists(o => o.Kind == p.Kind && (o.Pos - p.Pos).sqrMagnitude < 45f * 45f)) continue;
            outp.Add(p);
        }
        return outp;
    }

    static string Id(Place p)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in p.Name) h = h * 31 + c;
            h = h * 31 + Mathf.RoundToInt(p.Pos.x / 4);
            h = h * 31 + Mathf.RoundToInt(p.Pos.y / 4);
            h = h * 31 + Mathf.RoundToInt(p.Pos.z / 4);
            return "p" + ((uint)h % 0xFFFFFF).ToString("x6");
        }
    }

    // "最近的加油站" -> "加油站"
    static string Core(string q)
    {
        q = (q ?? "").Trim();
        foreach (var w in new[] { "最近的", "附近的", "最近", "附近", "一个", "一家", "去" })
            if (q.StartsWith(w)) q = q.Substring(w.Length).Trim();
        return q;
    }

    static int Score(Place p, string q)
    {
        if (q.Length == 0) return 1;
        string n = p.Name.ToLowerInvariant(), k = p.Kind.ToLowerInvariant(), lq = q.ToLowerInvariant();
        if (n == lq) return 5;
        if (k == lq) return 4;
        foreach (var (kind, words) in Synonyms)
            if (p.Kind == kind && Array.Exists(words, w => lq.Contains(w))) return 4;
        if (n.Contains(lq)) return 3;
        if (k.Contains(lq) || lq.Contains(k) && k.Length >= 2) return 2;
        // every character of the query in the name, in order (拼写省略)
        int at = 0;
        foreach (char c in n) if (at < lq.Length && c == lq[at]) at++;
        return at == lq.Length && lq.Length >= 2 ? 1 : 0;
    }

    public static IEnumerable<(Place place, int score, float dist)> Find(string query, Vector3 from)
    {
        string q = Core(query);
        return All()
            .Select(p => (place: p, score: Score(p, q), dist: Vector3.Distance(from, p.Pos)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.dist);
    }

    // A place by its id, or the best match for a name (nearest among the best), for navigate.
    public static Place Resolve(string idOrName, Vector3 from)
    {
        var all = All();
        var byId = all.FirstOrDefault(p => p.Id == idOrName);
        if (byId != null) return byId;
        return Find(idOrName, from).Select(x => x.place).FirstOrDefault();
    }

    // `places` request: { query?, limit? } -> { places: [{ id, name, kind, pos, distance, dy }], total, kinds }
    public static JsonNode Search(JsonObject args)
    {
        if (Global.Instance == null) throw new CooError("游戏还没进入");
        string query = (string)args["query"] ?? "";
        int limit = Mathf.Clamp((int?)args["limit"] ?? 8, 1, 30);
        var from = CooGame.PlayerPos();
        var hits = Find(query, from).Take(limit);
        var arr = new JsonArray();
        foreach (var (p, _, d) in hits)
            arr.Add(new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["kind"] = p.Kind, ["pos"] = CooGame.V(p.Pos),
                ["distance"] = Mathf.RoundToInt(d), ["dy"] = Mathf.RoundToInt(p.Pos.y - from.y),
                ["bearing"] = Bearing(from, p.Pos),
            });
        var kinds = new JsonArray();
        foreach (var k in All().Where(p => !p.Quest).GroupBy(p => p.Kind).OrderByDescending(g => g.Count()).Take(20)) kinds.Add($"{k.Key}×{k.Count()}");
        return new JsonObject { ["places"] = arr, ["total"] = All().Count, ["kinds"] = kinds };
    }

    // Where it lies relative to where the player faces: 前方 / 右前方 / 右侧 / ...
    public static string Bearing(Vector3 from, Vector3 to)
    {
        var g = Global.Instance;
        Transform t = Global.IsInVehicle && g != null && g.playerCar != null ? g.playerCar.transform : g != null && g.player != null ? g.player.transform : null;
        var fwd = t != null ? t.forward : Vector3.forward;
        fwd.y = 0;
        var dir = to - from;
        dir.y = 0;
        if (dir.sqrMagnitude < 1) return "这里";
        float a = Vector3.SignedAngle(fwd, dir, Vector3.up);
        string[] names = { "前方", "右前方", "右侧", "右后方", "后方", "左后方", "左侧", "左前方" };
        int i = Mathf.RoundToInt(((a + 360f) % 360f) / 45f) % 8;
        return names[i];
    }

    public static string Dump(string query)
    {
        var from = CooGame.PlayerPos();
        var rows = (string.IsNullOrEmpty(query) ? All().Select(p => (place: p, score: 1, dist: Vector3.Distance(from, p.Pos))) : Find(query, from)).Take(60);
        return $"{All().Count} places: {string.Join(", ", All().GroupBy(p => p.Kind).Select(g => g.Key + "×" + g.Count()))}\n" +
               string.Join("\n", rows.Select(x => $"{x.place.Id} [{x.place.Kind}] {x.place.Name} {x.dist:F0} m {CooGame.V(x.place.Pos).ToJsonString()}"));
    }
}
