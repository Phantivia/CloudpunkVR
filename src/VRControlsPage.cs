using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR;

// The game's Controls screen, controller tab, rewritten for the VR layout (ControlLayout): row values show the VR
// control bound to each action, the pad diagram labels name what the VR control in that place does (composed from
// the game's own localized row and label texts), and rebinding there is off: the layout is edited with
// "Cloudpunk VR 按键设置.exe" next to the game.
internal static class VRControlsPage
{
    static bool active;

    // Rows under Controls/Controller whose value is the VR control bound to the matching layout functions.
    static readonly Dictionary<string, string[]> RowFunctions = new()
    {
        ["Inventory"] = new[] { "common.inventory" },
        ["Interact"] = new[] { "common.interact" },
        ["Map"] = new[] { "common.map" },
        ["Disable HUD"] = new[] { "common.hud" },
        ["Camera Zoom In"] = new[] { "common.camera" },
        ["Camera Zoom Out"] = new[] { "common.camera" },
        ["Accelerate"] = new[] { "car.gas" },
        ["Brake"] = new[] { "car.brake" },
        ["Steer Left"] = new[] { "car.steer" },
        ["Steer Right"] = new[] { "car.steer" },
        ["Ascend"] = new[] { "car.climb" },
        ["Descend"] = new[] { "car.climb" },
        ["Strafe Left"] = new[] { "car.strafe_left", "car.strafe" },
        ["Strafe Right"] = new[] { "car.strafe_right", "car.strafe" },
        ["Cockpit View"] = new[] { "car.view", "common.camera" },
    };

    // Pad diagram label -> the VR control in that place on a Touch controller, and the stick direction (0 = button).
    static readonly Dictionary<string, (string input, int dir)> Diagram = new()
    {
        ["A"] = ("a", 0), ["B"] = ("b", 0), ["X"] = ("x", 0), ["Y"] = ("y", 0),
        ["LT"] = ("left_trigger", 0), ["RT"] = ("right_trigger", 0), ["LB"] = ("left_grip", 0), ["RB"] = ("right_grip", 0),
        ["L Up"] = ("left_stick_y", 1), ["L Down"] = ("left_stick_y", -1), ["L Left"] = ("left_stick_x", -1), ["L Right"] = ("left_stick_x", 1),
        ["R Up"] = ("right_stick_y", 1), ["R Down"] = ("right_stick_y", -1), ["R Left"] = ("right_stick_x", -1), ["R Right"] = ("right_stick_x", 1),
        ["Start"] = ("left_menu", 0), ["Back"] = ("", 0),
        ["Dpad Up"] = ("", 0), ["Dpad Down"] = ("", 0), ["Dpad Left"] = ("", 0), ["Dpad Right"] = ("", 0),
    };

    // Game-localized label texts, captured the first time each diagram label is seen.
    static readonly Dictionary<string, string> labels = new();

    public static void Apply(Harmony h)
    {
        active = true;
        h.Patch(AccessTools.Method(typeof(ControlsButton), nameof(ControlsButton.Update)), postfix: new HarmonyMethod(typeof(VRControlsPage), nameof(RowText)));
        h.Patch(AccessTools.Method(typeof(ControlsButton), nameof(ControlsButton.OnSubmit)), prefix: new HarmonyMethod(typeof(VRControlsPage), nameof(NoRebind)));
        h.Patch(AccessTools.Method(typeof(ControlsButton), nameof(ControlsButton.OnPointerClick)), prefix: new HarmonyMethod(typeof(VRControlsPage), nameof(NoRebind)));
        h.Patch(AccessTools.Method(typeof(ControllerButton), nameof(ControllerButton.Update)), postfix: new HarmonyMethod(typeof(VRControlsPage), nameof(LabelText)));
    }

    static void RowText(ControlsButton __instance)
    {
        var layout = ControlLayout.Instance;
        if (!active || layout == null || !__instance.isController || __instance.value == null) return;
        if (!RowFunctions.TryGetValue(__instance.gameObject.name, out var fns)) return;
        var parts = new List<string>();
        foreach (var f in fns)
        {
            var l = layout.Label(f);
            if (l != "-" && !parts.Contains(l)) parts.Add(l);
        }
        var v = parts.Count == 0 ? "-" : string.Join(" / ", parts);
        if (__instance.value.text != v) __instance.value.text = v;
    }

    static bool NoRebind(ControlsButton __instance) => !active || !__instance.isController;

    static void LabelText(ControllerButton __instance)
    {
        var layout = ControlLayout.Instance;
        if (!active || layout == null || __instance.textObj == null) return;
        string name = __instance.gameObject.name;
        if (!labels.ContainsKey(name) && !string.IsNullOrEmpty(__instance.textObj.text)) labels[name] = __instance.textObj.text;
        if (!Diagram.TryGetValue(name, out var place)) return;
        var names = new List<string>();
        if (place.input.Length > 0)
            foreach (var f in ControlMap.Functions)
            {
                if (f.Section == "menu") continue;
                int dir = layout.Direction(f.Id, place.input);
                if (dir == 0) continue;
                var n = FunctionName(f, place.dir == 0 ? 1 : dir * place.dir, __instance.textObj.font);
                if (n.Length > 0 && !names.Contains(n)) names.Add(n);
            }
        var text = string.Join(" / ", names);
        if (__instance.textObj.text != text) __instance.textObj.text = text;
    }

    // Localized name of a layout function, from the game's own texts where it has one.
    static string FunctionName(ControlMap.Function f, int sign, TMPro.TMP_FontAsset font)
    {
        string Label(string key) => labels.TryGetValue(key, out var s) ? s : "";
        return f.Id switch
        {
            "common.interact" => RowName("Interact"),
            "common.inventory" => RowName("Inventory"),
            "common.map" => RowName("Map"),
            "common.hud" => RowName("Disable HUD"),
            "common.back" => Label("B"),
            "common.pause" => Label("Start"),
            "car.gas" => RowName("Accelerate"),
            "car.brake" => RowName("Brake"),
            "car.steer" => RowName(sign > 0 ? "Steer Right" : "Steer Left"),
            "car.climb" => RowName(sign > 0 ? "Ascend" : "Descend"),
            "car.strafe" => RowName(sign > 0 ? "Strafe Right" : "Strafe Left"),
            "car.strafe_left" => RowName("Strafe Left"),
            "car.strafe_right" => RowName("Strafe Right"),
            "car.view" => RowName("Cockpit View"),
            "foot.move_x" or "foot.move_y" => Label("L Up"),
            _ => Own(f, font),
        };
    }

    // Our own text for functions the game has no label for; the CJK font only holds the game's own glyphs.
    static string Own(ControlMap.Function f, TMPro.TMP_FontAsset font)
    {
        var g = Global.Instance;
        bool chinese = g != null && (g.selectedLanguage == Global.SupportedLanguage.CNs || g.selectedLanguage == Global.SupportedLanguage.CNt);
        string zh = f.Zh.Replace("（长按）", "");
        if (!chinese || font == null) return f.En;
        foreach (var ch in zh)
            if (!char.IsWhiteSpace(ch) && !font.HasCharacter(ch, true)) return f.En;
        return zh;
    }

    // Localized name of a row on the same screen, e.g. "左转" from the Steer Left row.
    static string RowName(string row)
    {
        var g = Global.Instance;
        var t = g != null && g.controls != null ? g.controls.transform.Find($"Controller/Options/{row}/Name") : null;
        var tmp = t != null ? t.GetComponent<TMPro.TextMeshProUGUI>() : null;
        return tmp != null ? tmp.text : "";
    }

    // Swap the hidden "Map" row in for "Unlock Camera" (no camera unlock in VR) so the list keeps its length and
    // the in-vehicle / on-foot sections stay aligned; then chain navigation through the visible rows.
    public static void Update()
    {
        var g = Global.Instance;
        if (g == null || g.controls == null || !g.controls.gameObject.activeInHierarchy) return;
        var list = g.controls.transform.Find("Controller/Options");
        if (list == null) return;
        var map = list.Find("Map");
        var unlock = list.Find("Unlock Camera");
        if (map == null || unlock == null || map.gameObject.activeSelf) return;
        map.gameObject.SetActive(true);
        unlock.gameObject.SetActive(false);
        map.SetSiblingIndex(unlock.GetSiblingIndex());
        Relink(list);
    }

    static void Relink(Transform list)
    {
        var visible = new List<Selectable>();
        for (int i = 0; i < list.childCount; i++)
        {
            var child = list.GetChild(i);
            var s = child.gameObject.activeSelf ? child.GetComponent<Selectable>() : null;
            if (s != null) visible.Add(s);
        }
        for (int i = 0; i < visible.Count; i++)
        {
            var nav = visible[i].navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = i > 0 ? visible[i - 1] : nav.selectOnUp;
            nav.selectOnDown = i < visible.Count - 1 ? visible[i + 1] : nav.selectOnDown;
            visible[i].navigation = nav;
        }
    }
}
