using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;

namespace CloudpunkVR.Coo;

// What the game shows the player, as JSON for the World: where the player is, the car, money, the quests (the
// game's objective list with their map markers), the dialogue on screen, the nearest thing to interact with, the
// pop-ups for things received (items, money, new places) and the tutorial hints. Change events are found by comparing
// snapshots four times a second, so nothing here hooks the game's story code.
//
// After a load (and on connecting) nothing is news for two seconds of play: a save restores money and quests, and
// that would read as income and new quests. Modes are reported once they have held for a moment (pausing for a second
// is not news); loading screens are never reported.
internal sealed class CooGame
{
    readonly CooBridge bridge;

    string lastSector, lastMode, lastLineKey, pendingLineKey, pendingName, pendingText;
    bool pendingRemote;
    float pendingSince;
    int lastMoney = int.MinValue;
    readonly Dictionary<string, string> questSig = new();
    string lastInteract, lastUnlock, lastHint;
    string candidateMode;
    float candidateSince, playableSince = -1;
    float lowFuelWarned = 1f;

    // two seconds of play since the last load or connect
    bool Settled(float now) => playableSince >= 0 && now - playableSince > 2f;

    public CooGame(CooBridge bridge) => this.bridge = bridge;

    public static JsonArray V(Vector3 v) => new() { Math.Round(v.x, 1), Math.Round(v.y, 1), Math.Round(v.z, 1) };

    public static string Mode()
    {
        var g = Global.Instance;
        if (g == null || SceneLoader.IsLoading()) return "loading";
        if (g.IsInMenu()) return "menu";
        if (g.IsInDialog(false)) return "dialogue";
        if (Global.FullMapOpen) return "map";
        if (g.HasBigWindowOpen()) return "window";
        if (Global.IsInVehicle) return Global.IsInVehicleCockpit ? "car" : "car_chase";
        return g.player != null && g.player.gameObject.activeInHierarchy ? "foot" : "none";
    }

    public static Vector3 PlayerPos()
    {
        var g = Global.Instance;
        if (g == null) return Vector3.zero;
        if (Global.IsInVehicle && g.playerCar != null) return g.playerCar.transform.position;
        return g.player != null ? g.player.transform.position : Vector3.zero;
    }

    public static IEnumerable<QuestText> ActiveQuests()
    {
        var g = Global.Instance;
        var dict = g != null ? g.questTexts : null;
        if (dict == null) yield break;
        foreach (var kv in dict)
        {
            var q = kv.Value;
            if (q != null && q.enabled) yield return q;
        }
    }

    public static bool HasMarker(QuestText q) => q.markerType != QuestText.Marker.None && q.markerPos != Vector3.zero;

    // Where the game shows the quest on its maps. A quest on another level (markerInScene is not this scene) has its
    // map marker moved by the game to the lift that leads that way (MinimapObject.Setup: lifts going up, down or
    // across as the marker lies above, below or level, the one with the shortest way player -> lift -> marker); that
    // marker's position is returned then, and otherLevel is set.
    public static Vector3 MarkerPos(QuestText q, out bool otherLevel)
    {
        otherLevel = q.markerInScene != UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        if (!otherLevel) return q.markerPos;
        var g = Global.Instance;
        var arr = g != null ? g.quests : null;
        if (arr != null)
            for (int i = 0; i < arr.Length; i++)
            {
                var qu = arr[i];
                if (qu == null || qu.questText == null || qu.questText.Pointer != q.Pointer) continue;
                var mo = qu.marker;
                if (mo != null && mo.initialized) return mo.transform.position;
            }
        return q.markerPos;
    }

    static JsonObject Quest(QuestText q, Vector3 from)
    {
        var o = new JsonObject
        {
            ["id"] = q.id,
            ["who"] = q.characterName,
            ["text"] = Clean(q.text),
        };
        if (q.progressMax > 0) o["progress"] = $"{q.progressCurrent}/{q.progressMax}";
        if (HasMarker(q))
        {
            var at = MarkerPos(q, out bool other);
            o["marker"] = V(at);
            o["markerKind"] = q.markerType.ToString();
            o["distance"] = Mathf.RoundToInt(Vector3.Distance(from, at));
            if (other) o["otherLevel"] = true;
        }
        return o;
    }

    // Rich-text tags out, whitespace collapsed.
    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        return System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
    }

    public static JsonObject Dialogue()
    {
        var g = Global.Instance;
        var d = g != null ? g.dialogUI : null;
        if (d == null || !d.IsActiveAndNotFadingOut()) return null;
        string name = d.nameObj != null ? Clean(d.nameObj.text) : "";
        string text = d.textField != null ? Clean(d.textField.text) : "";
        if (Global.IsInVehicle && d.textFieldInterior != null && d.textFieldInterior.gameObject.activeInHierarchy)
        {
            name = d.nameObjInterior != null ? Clean(d.nameObjInterior.text) : name;
            text = Clean(d.textFieldInterior.text);
        }
        return new JsonObject { ["speaker"] = name, ["text"] = text, ["remote"] = d.dialog != null && d.dialog.isRemote };
    }

    // What the game names the nearest interactable: a character's name, a pickup's item name, the prompt a trigger
    // shows over itself. Objects the game shows no name for are not reported (their object names mean nothing).
    public static string InteractLabel()
    {
        var g = Global.Instance;
        var it = g != null && g.player != null ? g.player.interaction : null;
        var obj = it != null ? it.closestObject : null;
        if (obj == null || Global.IsInVehicle) return null;
        var chr = obj.GetComponentInParent<Character>();
        if (chr != null)
        {
            var n = chr.nameObj != null && !string.IsNullOrEmpty(chr.nameObj.text) ? chr.nameObj.text : chr.definedName;
            return string.IsNullOrEmpty(Clean(n)) ? null : Clean(n);
        }
        var pick = obj.GetComponentInParent<PickupItem>();
        if (pick != null)
        {
            string n = pick.nameText != null ? Clean(pick.nameText.text) : "";
            if (n.Length == 0 && pick.item != null) n = Clean(pick.item.localizedName);
            if (n.Length > 0) return n;
        }
        var trig = obj.GetComponentInParent<WorldTrigger>();
        if (trig != null && trig.uiCanvas != null)
        {
            var n = TextUnder(trig.uiCanvas);
            if (n.Length > 0) return n;
        }
        return null;
    }

    // The visible texts under a UI object, joined.
    public static string TextUnder(Component root)
    {
        var parts = new List<string>();
        foreach (var t in root.GetComponentsInChildren<TMPro.TMP_Text>(false))
        {
            var x = Clean(t.text);
            if (x.Length > 0 && !parts.Contains(x)) parts.Add(x);
        }
        return string.Join(" ", parts);
    }

    // The pop-up for something received (an item, money with its reason, a newly found place), while it shows.
    static JsonObject Unlock()
    {
        var u = Global.Instance != null ? Global.Instance.unlockedItemUI : null;
        if (u == null || !u.gameObject.activeInHierarchy || u.fadingOut) return null;
        string title = u.textObj != null ? Clean(u.textObj.text) : "";
        string name = u.nameObj != null ? Clean(u.nameObj.text) : "";
        string amount = u.amountObj != null && u.amountObj.gameObject.activeInHierarchy ? Clean(u.amountObj.text) : "";
        if (title.Length == 0 && name.Length == 0) return null;
        return new JsonObject { ["title"] = title, ["name"] = name, ["amount"] = amount };
    }

    // A tutorial hint on screen (driving, camera, zoom), as its text.
    static string Hint()
    {
        var g = Global.Instance;
        if (g == null) return null;
        foreach (var cg in new[] { g.tutorialDriving, g.tutorialCamera, g.tutorialCameraZoom })
            if (cg != null && cg.gameObject.activeInHierarchy && cg.alpha > 0.5f)
            {
                var t = TextUnder(cg);
                if (t.Length > 0) return t;
            }
        return null;
    }

    public JsonObject State()
    {
        var g = Global.Instance;
        var o = new JsonObject { ["type"] = "state", ["mode"] = Mode(), ["vr"] = UnityEngine.XR.XRSettings.enabled };
        if (g == null) return o;
        var pos = PlayerPos();
        o["sector"] = Clean(g.SectorName);
        o["pos"] = V(pos);
        o["money"] = g.money;
        var car = g.playerCar;
        if (car != null)
            o["car"] = new JsonObject
            {
                ["speedKmh"] = Mathf.RoundToInt(car.currentSpeed * 3.6f),
                ["fuel"] = Math.Round(car.currentFuel, 1),
                ["maxFuel"] = car.maxFuel,
                ["floor"] = Math.Round(car.currentFloorLevel, 1),
                ["inParking"] = car.parkingVolume != null,
                ["pos"] = V(car.transform.position),
            };
        var quests = new JsonArray();
        foreach (var q in ActiveQuests()) quests.Add(Quest(q, pos));
        o["quests"] = quests;
        var dlg = Dialogue();
        if (dlg != null) o["dialogue"] = dlg;
        var it = InteractLabel();
        if (it != null) o["interact"] = it;
        var ap = CooHost.Instance?.Pilot;
        if (ap != null) o["autopilot"] = ap.Describe();
        return o;
    }

    // Four times a second: send the state and whatever changed since the last look.
    public void Poll(float now)
    {
        var g = Global.Instance;
        if (g == null) return;
        bridge.Send(State());

        string mode = Mode();
        if (mode == "loading") playableSince = -1;
        else if (playableSince < 0 && mode != "menu" && mode != "none") playableSince = now;
        bool news = Settled(now);

        // a mode is reported once it held for 1.2 s, and only if it differs from the last one reported
        if (mode != candidateMode) { candidateMode = mode; candidateSince = now; }
        if (mode != "loading" && mode != "none" && now - candidateSince > 1.2f && mode != lastMode)
        {
            if (lastMode != null && news) bridge.SendEvent("mode", new JsonObject { ["from"] = lastMode, ["to"] = mode });
            lastMode = mode;
        }
        string sector = Clean(g.SectorName);
        if (!string.IsNullOrEmpty(sector) && sector != lastSector)
        {
            if (lastSector != null && news) bridge.SendEvent("sector", new JsonObject { ["sector"] = sector });
            lastSector = sector;
        }
        if (g.money != lastMoney)
        {
            if (lastMoney != int.MinValue && news) bridge.SendEvent("money", new JsonObject { ["money"] = g.money, ["change"] = g.money - lastMoney });
            lastMoney = g.money;
        }
        PollQuests(news);
        PollDialogue(now);
        var it = InteractLabel();
        if (it != lastInteract)
        {
            if (it != null && news) bridge.SendEvent("interact", new JsonObject { ["target"] = it });
            lastInteract = it;
        }
        var un = Unlock();
        string unKey = un?.ToJsonString();
        if (unKey != lastUnlock)
        {
            if (un != null && news) bridge.SendEvent("unlocked", un);
            lastUnlock = unKey;
        }
        var hint = Hint();
        if (hint != lastHint)
        {
            if (hint != null && news) bridge.SendEvent("hint", new JsonObject { ["text"] = hint });
            lastHint = hint;
        }
        PollFuel(news);
    }

    // Fuel under a quarter, then under a tenth: once each until the tank is filled again.
    void PollFuel(bool news)
    {
        var car = Global.Instance.playerCar;
        if (car == null || car.maxFuel <= 0) return;
        float f = car.currentFuel / car.maxFuel;
        if (f > 0.3f) { lowFuelWarned = 1f; return; }
        float step = f < 0.1f ? 0.1f : f < 0.25f ? 0.25f : 1f;
        if (step >= lowFuelWarned) return;
        lowFuelWarned = step;
        if (news) bridge.SendEvent("fuel", new JsonObject { ["fuel"] = Math.Round(car.currentFuel, 1), ["maxFuel"] = car.maxFuel, ["percent"] = Mathf.RoundToInt(f * 100) });
    }

    void PollQuests(bool news)
    {
        var pos = PlayerPos();
        var seen = new HashSet<string>();
        foreach (var q in ActiveQuests())
        {
            if (string.IsNullOrEmpty(q.id)) continue;
            seen.Add(q.id);
            string sig = $"{Clean(q.text)}|{q.progressCurrent}/{q.progressMax}|{(HasMarker(q) ? V(q.markerPos).ToJsonString() : "-")}";
            bool known = questSig.TryGetValue(q.id, out var old);
            if (!known || old != sig)
            {
                questSig[q.id] = sig;
                if (news) bridge.SendEvent(known ? "quest_updated" : "quest_added", Quest(q, pos));
            }
        }
        var gone = new List<string>();
        foreach (var id in questSig.Keys) if (!seen.Contains(id)) gone.Add(id);
        foreach (var id in gone)
        {
            questSig.Remove(id);
            if (news) bridge.SendEvent("quest_done", new JsonObject { ["id"] = id });
        }
    }

    // A dialogue line is reported once it has finished typing out (the text stops changing) or the next one starts.
    void PollDialogue(float now)
    {
        var d = Dialogue();
        string key = d != null ? (string)d["speaker"] + "\u0001" + (string)d["text"] : null;
        if (d != null && !string.IsNullOrEmpty((string)d["text"]))
        {
            if (key != pendingLineKey)
            {
                // a new line or the same one still typing
                bool sameLine = pendingText != null && ((string)d["text"]).StartsWith(pendingText.Substring(0, Math.Min(pendingText.Length, 6)));
                if (!sameLine) FlushLine();
                pendingLineKey = key;
                pendingName = (string)d["speaker"];
                pendingText = (string)d["text"];
                pendingRemote = (bool?)d["remote"] ?? false;
                pendingSince = now;
            }
            else if (now - pendingSince > 0.6f) FlushLine();
        }
        else FlushLine();
    }

    void FlushLine()
    {
        if (pendingLineKey != null && pendingLineKey != lastLineKey && !string.IsNullOrEmpty(pendingText))
        {
            lastLineKey = pendingLineKey;
            bridge.SendEvent("dialogue", new JsonObject { ["speaker"] = pendingName, ["text"] = pendingText, ["remote"] = pendingRemote });
        }
        pendingLineKey = null;
        pendingText = null;
    }

    // On connecting: the current state is the baseline, not news.
    public void Reset()
    {
        playableSince = -1;
        lastMode = null;
        lastSector = null;
        lastMoney = int.MinValue;
        lastUnlock = lastHint = lastInteract = null;
        lowFuelWarned = 1f;
    }
}
