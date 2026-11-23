using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CloudpunkVR;

// The game's on-screen button prompts (InputIcon: "TALK [E]", "Switch to 3rd Person [X]", the driving tutorial...)
// show the keyboard key or the Xbox glyph of the game action. In VR neither is what the player presses, so each
// prompt is rewritten to the VR control that does the same thing in the current controller layout, in the key frame
// the game uses for keyboard keys.
internal static class VRPrompts
{
    // InputIcon setup method -> layout function that performs it in VR.
    static readonly Dictionary<string, string> BySetup = new()
    {
        ["SetupAction"] = "common.interact", ["SetupSkipDialog"] = "common.interact",
        ["SetupCancel"] = "common.back", ["SetupMenu"] = "common.pause", ["SetupInventory"] = "common.inventory",
        ["SetupMap"] = "common.map", ["SetupDisableHUD"] = "common.hud",
        ["SetupUnlockCamera"] = "common.camera", ["SetupCameraZoomIn"] = "common.camera", ["SetupCameraZoomOut"] = "common.camera",
        ["SetupAccelerate"] = "car.gas", ["SetupBrake"] = "car.brake",
        ["SetupSteerLeft"] = "car.steer", ["SetupSteerRight"] = "car.steer",
        ["SetupStrafeLeft"] = "car.strafe", ["SetupStrafeRight"] = "car.strafe",
        ["SetupAscend"] = "car.climb", ["SetupDescend"] = "car.climb",
        ["SetupMoveUp"] = "foot.move_y", ["SetupMoveDown"] = "foot.move_y", ["SetupMoveLeft"] = "foot.move_x", ["SetupMoveRight"] = "foot.move_x",
        ["SetupWalk"] = "foot.sneak",
        ["SetupMapMove"] = "menu.nav_y", ["SetupMapZoomIn"] = "menu.scroll", ["SetupMapZoomOut"] = "menu.scroll",
    };

    // Game action name (InputIcon.action, when the icon names one) -> layout function.
    static readonly Dictionary<string, string> ByAction = new()
    {
        ["Interact"] = "common.interact", ["SkipDialog"] = "common.interact", ["ControllerConfirm"] = "common.interact",
        ["Cancel"] = "common.back", ["Inventory"] = "common.inventory", ["ControlsMenu"] = "common.pause",
        ["ControlsFullMap"] = "common.map", ["ControlsDisableHUD"] = "common.hud", ["CameraUnlock"] = "common.camera",
        ["CameraZoomUp"] = "common.camera", ["CameraZoomDown"] = "common.camera", ["CameraCockpit"] = "car.view",
        ["DriveForward"] = "car.gas", ["DriveBackward"] = "car.brake", ["SteerLeft"] = "car.steer", ["SteerRight"] = "car.steer",
        ["Ascend"] = "car.climb", ["Sink"] = "car.climb", ["StrafeLeft"] = "car.strafe", ["StrafeRight"] = "car.strafe",
        ["MoveLeft"] = "foot.move_x", ["MoveRight"] = "foot.move_x", ["MoveUp"] = "foot.move_y", ["MoveDown"] = "foot.move_y",
        ["Sneak"] = "foot.sneak",
    };

    static Sprite keyFrame;
    static bool active;

    public static void Apply(Harmony h)
    {
        active = true;
        var post = new HarmonyMethod(typeof(VRPrompts), nameof(AfterSetup));
        int n = 0;
        foreach (var name in BySetup.Keys)
        {
            var m = AccessTools.Method(typeof(InputIcon), name);
            if (m == null) { Plugin.L.LogWarning($"prompts: InputIcon.{name} not found"); continue; }
            h.Patch(m, postfix: post);
            n++;
        }
        Plugin.L.LogInfo($"prompts: {n} InputIcon setups show VR controls");
    }

    static void AfterSetup(InputIcon __instance, MethodBase __originalMethod)
    {
        using var __pt = PluginTiming.Measure("patch:prompts");
        if (!active || __instance == null) return;
        string fn = null;
        var action = __instance.action;
        if (action != null && ByAction.TryGetValue(action.Name, out var byAction)) fn = byAction;
        else if (__originalMethod != null) BySetup.TryGetValue(__originalMethod.Name, out fn);
        if (fn == null) return;
        Show(__instance, Label(fn));
    }

    // Short name of the first control bound to a function, as printed on a Touch / Index controller.
    public static string Label(string fn)
    {
        var layout = ControlLayout.Instance;
        var input = layout != null ? layout.FirstInput(fn) : null;
        return input switch
        {
            null => "-",
            "a" => "A", "b" => "B", "x" => "X", "y" => "Y",
            "left_trigger" => "LT", "right_trigger" => "RT", "left_grip" => "LG", "right_grip" => "RG",
            "left_stick_x" or "left_stick_y" or "left_stick_click" => "LS",
            "right_stick_x" or "right_stick_y" or "right_stick_click" => "RS",
            "left_menu" => "MENU",
            _ => input.ToUpperInvariant(),
        };
    }

    static void Show(InputIcon icon, string label)
    {
        var img = icon.imageObj;
        var txt = icon.textObj;
        if (txt == null) return;
        // Keyboard prompts are a key frame sprite with the key's name in the text; remember that frame and use it for
        // prompts the game draws as a controller glyph.
        if (img != null && img.sprite != null && keyFrame == null && !string.IsNullOrEmpty(txt.text) && txt.text != "-" && txt.gameObject.activeSelf)
            keyFrame = img.sprite;
        if (img != null && keyFrame != null && img.sprite != keyFrame)
        {
            img.sprite = keyFrame;
            img.enabled = true;
        }
        if (!txt.gameObject.activeSelf) txt.gameObject.SetActive(true);
        if (!txt.enabled) txt.enabled = true;
        if (txt.text != label) txt.text = label;
    }
}
