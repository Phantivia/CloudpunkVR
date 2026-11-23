using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CloudpunkVR;

// The controller layout file (ControlMap) evaluated against the raw SteamVR controls, reloaded when it changes on
// disk so the layout editor's changes apply while the game runs.
internal sealed class ControlLayout
{
    public static readonly string Path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, ControlMap.FileName);

    readonly SteamVRInput vr;
    Dictionary<string, List<ControlMap.Binding>> map;
    readonly Dictionary<string, bool> down = new(), pressed = new();
    DateTime stamp;
    float nextCheck;

    public static ControlLayout Instance { get; private set; }
    public event Action Changed;

    public ControlLayout(SteamVRInput vr)
    {
        this.vr = vr;
        Instance = this;
        Reload(true);
    }

    void Reload(bool first)
    {
        try
        {
            map = ControlMap.Load(Path, out bool existed);
            if (!existed || ControlMap.Migrated) ControlMap.Save(Path, map);
            stamp = File.GetLastWriteTimeUtc(Path);
            Plugin.L.LogInfo($"controls: {(first ? "loaded" : "reloaded")} {Path}");
            if (!first) Changed?.Invoke();
        }
        catch (Exception e)
        {
            Plugin.L.LogError("controls: cannot read the layout, using defaults: " + e.Message);
            map ??= ControlMap.Defaults();
        }
    }

    // Once per frame after polling SteamVR.
    public void Update()
    {
        if (Time.unscaledTime >= nextCheck)
        {
            nextCheck = Time.unscaledTime + 1f;
            try { if (File.Exists(Path) && File.GetLastWriteTimeUtc(Path) != stamp) Reload(false); } catch { }
        }
        foreach (var f in ControlMap.Functions)
        {
            if (f.Kind != ControlMap.Kind.Button) continue;
            bool now = Button(f.Id);
            bool was = down.TryGetValue(f.Id, out var w) && w;
            pressed[f.Id] = now && !was;
            down[f.Id] = now;
        }
    }

    static float Dead(float v, float dz) => Mathf.Abs(v) < dz ? 0 : Mathf.Sign(v) * (Mathf.Abs(v) - dz) / (1 - dz);

    // Sum of the bound inputs: sticks -1..1, triggers/grips 0..1, buttons 0/1. Default dead zones follow Aircar's input
    // settings (DefaultInput.ini): none on the thumbsticks (0.08 here, for stick drift), 0.15 on the triggers.
    public float Axis(string id)
    {
        if (!map.TryGetValue(id, out var list)) return 0;
        float sum = 0;
        foreach (var b in list)
        {
            float v = vr.Value(b.Input);
            bool stick = b.Input.Contains("_stick_") && !b.Input.EndsWith("click");
            float dz = b.DeadZone >= 0 ? b.DeadZone : DefaultDeadZone(b.Input);
            v = Dead(v, Mathf.Clamp(dz, 0, 0.95f));
            sum += b.Invert ? -v : v;
        }
        return Mathf.Clamp(sum, -1, 1);
    }

    public static float DefaultDeadZone(string input) =>
        input.EndsWith("_trigger") ? 0.15f : input.Contains("_stick_") && !input.EndsWith("click") ? 0.08f : 0.05f;

    public bool Button(string id)
    {
        if (!map.TryGetValue(id, out var list)) return false;
        foreach (var b in list)
        {
            float v = vr.Value(b.Input);
            if (b.Invert) v = -v;
            if (v > (b.DeadZone >= 0 ? Mathf.Max(b.DeadZone, 0.05f) : 0.5f)) return true;
        }
        return false;
    }

    public bool Pressed(string id) => pressed.TryGetValue(id, out var p) && p;

    // The first control bound to a function (null when none), for prompts.
    public string FirstInput(string id) => map.TryGetValue(id, out var list) && list.Count > 0 ? list[0].Input : null;

    // Whether an input drives a function, and in which direction: 0 = not bound, 1 = as is, -1 = inverted.
    public int Direction(string id, string input)
    {
        if (!map.TryGetValue(id, out var list)) return 0;
        foreach (var b in list)
            if (b.Input == input) return b.Invert ? -1 : 1;
        return 0;
    }

    // Short label of what is bound to a function, for the in-game controls page.
    public string Label(string id)
    {
        if (!map.TryGetValue(id, out var list) || list.Count == 0) return "-";
        var parts = new List<string>();
        foreach (var b in list) parts.Add(Short(b.Input) + (b.Invert ? "(-)" : ""));
        return string.Join(" / ", parts);
    }

    static string Short(string input) => input switch
    {
        "a" => "A", "b" => "B", "x" => "X", "y" => "Y",
        "left_trigger" => "LT", "right_trigger" => "RT", "left_grip" => "LG", "right_grip" => "RG",
        "left_stick_x" => "LS X", "left_stick_y" => "LS Y", "right_stick_x" => "RS X", "right_stick_y" => "RS Y",
        "left_stick_click" => "LS", "right_stick_click" => "RS", "left_menu" => "Menu",
        _ => input,
    };
}
