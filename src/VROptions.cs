using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CloudpunkVR;

// VR settings inside the game's own Options screen. In VR the desktop-window rows (resolution, display mode,
// monitor, vsync, frame cap, mouse lock, run in background) do nothing, so they are hidden and VR rows take their
// place. Each VR row is a clone of the game's "Bloom" row, so font, colours, selection highlight, arrows and pad
// handling are the game's; only the label, the value list and the apply callback are ours.
internal sealed class VROptions
{
    static readonly string[] DesktopRows = { "Resolution", "Display Mode", "Monitor", "VSynch", "Max Framerate", "Lock Mouse", "Run In Background" };
    const string TemplateRow = "Bloom";

    sealed class Row
    {
        public string[] Label;          // en, zh-Hans, zh-Hant
        public string[][] Values;       // per language
        public Func<int> Get;
        public Action<int> Set;
    }

    static readonly int[] Resolutions = { 40, 50, 60, 70, 80, 90, 100 };
    static readonly float[] Tilts = { 0f, 0.25f, 0.5f, 0.75f, 1f };
    static readonly float[] UIDistances = { 1.3f, 1.6f, 2.0f };
    static readonly float[] DrawDistances = { 0f, 1500f, 2500f, 3500f };
    static readonly float[] Frost = { 0f, 0.35f, 0.7f, 1f };
    static readonly float[] TurnSpeeds = { 90f, 150f, 220f };

    readonly List<Row> rows;
    readonly List<GameObject> clones = new();
    bool built;

    public VROptions()
    {
        var c = Plugin.Cfg;
        string[] Same(params string[] v) => v;
        string[] Pct(float[] v) => Array.ConvertAll(v, x => $"{Mathf.RoundToInt(x * 100)}%");
        string[] Pcts(int[] v) => Array.ConvertAll(v, x => $"{x}%");
        rows = new List<Row>
        {
            new Row
            {
                Label = new[] { "VR RESOLUTION", "VR 分辨率", "VR 解析度" },
                Values = new[] { Pcts(Resolutions), Pcts(Resolutions), Pcts(Resolutions) },
                Get = () => Nearest(Array.ConvertAll(Resolutions, x => (float)x), c.ResolutionPercent.Value),
                Set = i => { c.ResolutionPercent.Value = Resolutions[i]; Resolution.Apply(Resolutions[i]); },
            },
            new Row
            {
                // smooth at three speeds, then snap at two angles
                Label = new[] { "TURNING", "转身方式", "轉身方式" },
                Values = new[]
                {
                    Same("SMOOTH 90°/S", "SMOOTH 150°/S", "SMOOTH 220°/S", "SNAP 30°", "SNAP 45°"),
                    Same("平滑 90°/秒", "平滑 150°/秒", "平滑 220°/秒", "分段 30°", "分段 45°"),
                    Same("平滑 90°/秒", "平滑 150°/秒", "平滑 220°/秒", "分段 30°", "分段 45°"),
                },
                Get = () => c.TurnMode.Value == "snap" ? (c.SnapTurnDegrees.Value >= 40 ? 4 : 3) : Nearest(TurnSpeeds, c.SmoothTurnSpeed.Value),
                Set = i =>
                {
                    c.TurnMode.Value = i < 3 ? "smooth" : "snap";
                    if (i < 3) c.SmoothTurnSpeed.Value = TurnSpeeds[i];
                    else c.SnapTurnDegrees.Value = i == 3 ? 30 : 45;
                },
            },
            new Row
            {
                Label = new[] { "WALK DIRECTION", "行走方向", "行走方向" },
                Values = new[] { Same("HEAD", "LEFT HAND"), Same("视线方向", "左手方向"), Same("視線方向", "左手方向") },
                Get = () => c.HeadRelativeWalk.Value ? 0 : 1,
                Set = i => c.HeadRelativeWalk.Value = i == 0,
            },
            new Row
            {
                Label = new[] { "COCKPIT TILT", "驾驶舱倾斜", "駕駛艙傾斜" },
                Values = new[] { Pct(Tilts), Pct(Tilts), Pct(Tilts) },
                Get = () => Nearest(Tilts, c.CockpitTilt.Value),
                Set = i => c.CockpitTilt.Value = Tilts[i],
            },
            new Row
            {
                Label = new[] { "UI DISTANCE", "界面距离", "介面距離" },
                Values = new[] { Same("NEAR", "MEDIUM", "FAR"), Same("较近", "适中", "较远"), Same("較近", "適中", "較遠") },
                Get = () => Nearest(UIDistances, c.UIDistance.Value),
                Set = i => { c.UIDistance.Value = UIDistances[i]; c.UIWidth.Value = UIDistances[i] * 1.125f; },
            },
            new Row
            {
                Label = new[] { "VR SHADOWS", "VR 阴影", "VR 陰影" },
                Values = new[] { Same("HIGH", "MEDIUM", "LOW"), Same("较高", "适中", "较低"), Same("較高", "適中", "較低") },
                Get = () => Math.Clamp(c.VRShadows.Value, 0, 2),
                Set = i => { c.VRShadows.Value = i; Perf.ApplyVRShadows(i); },
            },
            new Row
            {
                Label = new[] { "DRAW DISTANCE", "可视距离", "可視距離" },
                Values = new[] { Same("GAME (600 M)", "1500 M", "2500 M", "3500 M"), Same("游戏默认", "1500 米", "2500 米", "3500 米"), Same("遊戲預設", "1500 米", "2500 米", "3500 米") },
                Get = () => Nearest(DrawDistances, c.DrawDistance.Value),
                Set = i => c.DrawDistance.Value = DrawDistances[i],
            },
            new Row
            {
                Label = new[] { "FIRST-PERSON BODY", "第一人称身体", "第一人稱身體" },
                Values = new[] { Same("ON", "OFF"), Same("开启", "关闭"), Same("開啟", "關閉") },
                Get = () => c.ShowBody.Value ? 0 : 1,
                Set = i => c.ShowBody.Value = i == 0,
            },
            new Row
            {
                Label = new[] { "WINDOW FROST", "车窗磨砂", "車窗磨砂" },
                Values = new[] { Same("CLEAR", "LIGHT", "MEDIUM", "AS GAME"), Same("清透", "轻微", "适中", "同原版"), Same("清透", "輕微", "適中", "同原版") },
                Get = () => Nearest(Frost, c.WindshieldBlur.Value),
                Set = i => c.WindshieldBlur.Value = Frost[i],
            },
            new Row
            {
                Label = new[] { "COMFORT FILTER", "舒适滤镜", "舒適濾鏡" },
                Values = new[] { Same("ON", "OFF"), Same("开启", "关闭"), Same("開啟", "關閉") },
                Get = () => c.DisableComfortEffects.Value ? 0 : 1,
                Set = i => c.DisableComfortEffects.Value = i == 0,
            },
        };
    }

    static int Nearest(float[] options, float value)
    {
        int best = 0;
        for (int i = 1; i < options.Length; i++)
            if (Mathf.Abs(options[i] - value) < Mathf.Abs(options[best] - value)) best = i;
        return best;
    }

    static int Language()
    {
        var g = Global.Instance;
        if (g == null) return 0;
        return g.selectedLanguage switch { Global.SupportedLanguage.CNs => 1, Global.SupportedLanguage.CNt => 2, _ => 0 };
    }

    // Cheap per-frame check; builds once the Options screen exists and keeps the desktop rows hidden.
    public void Update()
    {
        var g = Global.Instance;
        if (g == null || g.options == null) return;
        var list = g.options.transform.Find("Options");
        if (list == null) return;
        if (!built) Build(list);
        bool changed = !linked;
        foreach (var name in DesktopRows)
        {
            var row = list.Find(name);
            if (row != null && row.gameObject.activeSelf) { row.gameObject.SetActive(false); changed = true; }
        }
        if (changed && built) Relink(list);
    }

    // The rows use explicit navigation wired in the editor (Quality -> Resolution -> ...); with rows hidden and
    // inserted, chain up/down through the visible rows in order. Left/right stay as they are: they change values.
    void Relink(Transform list)
    {
        var visible = new List<Selectable>();
        for (int i = 0; i < list.childCount; i++)
        {
            var child = list.GetChild(i);
            if (!child.gameObject.activeSelf) continue;
            var s = child.GetComponent<Selectable>();
            if (s != null) visible.Add(s);
        }
        if (visible.Count == 0) return;
        bool wrap = visible[0].navigation.selectOnUp != null;
        for (int i = 0; i < visible.Count; i++)
        {
            var nav = visible[i].navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = i > 0 ? visible[i - 1] : wrap ? visible[visible.Count - 1] : null;
            nav.selectOnDown = i < visible.Count - 1 ? visible[i + 1] : wrap ? visible[0] : null;
            visible[i].navigation = nav;
        }
        linked = true;
    }
    bool linked;

    void Build(Transform list)
    {
        var template = list.Find(TemplateRow);
        if (template == null) return;
        built = true;
        int lang = Language();
        int index = list.Find("Quality") is { } quality ? quality.GetSiblingIndex() + 1 : 0;
        foreach (var r in rows)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, list, false);
            go.name = "VR " + r.Label[0];
            go.transform.SetSiblingIndex(index++);
            clones.Add(go);

            var label = go.transform.Find("Name");
            var localized = label.GetComponent<LocalizedText>();
            if (localized != null) UnityEngine.Object.Destroy(localized);
            var labelText = label.GetComponent<TMPro.TextMeshProUGUI>();
            var button = go.GetComponent<OptionsButton>();
            // The game's CJK font assets only hold glyphs its own texts use; fall back to English per element.
            labelText.text = Renderable(labelText.font, r.Label[lang]) ? r.Label[lang] : r.Label[0];
            var values = Array.TrueForAll(r.Values[lang], v => Renderable(button.value.font, v)) ? r.Values[lang] : r.Values[0];

            Silence(button.setupAction);
            Silence(button.applyAction);
            var row = r;
            button.setupAction.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() =>
            {
                button.values.Clear();
                foreach (var v in values) button.values.Add(v);
                button.currentValue = Math.Clamp(row.Get(), 0, values.Length - 1);
                button.value.text = values[button.currentValue];
            })));
            button.applyAction.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() =>
            {
                row.Set(Math.Clamp(button.currentValue, 0, values.Length - 1));
            })));
        }
        Plugin.L.LogInfo($"VR options: {rows.Count} rows added to the Options screen (language {lang})");
    }

    static bool Renderable(TMPro.TMP_FontAsset font, string text)
    {
        if (font == null) return true;
        foreach (var ch in text)
            if (!char.IsWhiteSpace(ch) && !font.HasCharacter(ch, true)) return false;
        return true;
    }

    // Cloned rows carry the template's inspector-wired listeners (FillBloom/ApplyBloom); switch them off.
    static void Silence(UnityEventBase e)
    {
        for (int i = 0; i < e.GetPersistentEventCount(); i++)
            e.SetPersistentListenerState(i, UnityEventCallState.Off);
    }
}
