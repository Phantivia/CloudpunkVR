using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CloudpunkVR;

// Controller layout: which physical control does what, per situation. Shared by the plugin and the layout editor
// (tools/ControlsEditor), so it only uses basic .NET. The file is plain text:
//
//   [car]
//   gas = right_trigger
//   climb = left_stick_y, right_stick_y@0.2
//   steer = -left_stick_x          (leading minus: inverted)
//
// Several inputs on one line add up (axes) or any of them counts (buttons); "@n" sets that input's dead zone.
internal static class ControlMap
{
    public const string FileName = "CloudpunkVR.controls.cfg";
    public const int Version = 2;

    // Defaults that changed between layout versions: (function, old default, new default). A file from an older
    // version gets the new default for a function only where it still holds the old one.
    static readonly (int version, string id, string from, string to)[] Migrations =
    {
        (2, "common.map", "x", "left_grip"),
        (2, "car.strafe_left", "left_grip", "none"),
        (2, "car.strafe_right", "right_grip", "none"),
    };

    public enum Kind { Button, Axis }

    public sealed class Function
    {
        public string Section, Key, Zh, En, Default;
        public Kind Kind;
        public string Id => Section + "." + Key;
    }

    public sealed class Input
    {
        public string Id, Zh, En;
        public bool IsAxis;
    }

    public static readonly Input[] Inputs =
    {
        new() { Id = "left_stick_x", Zh = "左摇杆 左右", En = "Left stick X", IsAxis = true },
        new() { Id = "left_stick_y", Zh = "左摇杆 前后", En = "Left stick Y", IsAxis = true },
        new() { Id = "right_stick_x", Zh = "右摇杆 左右", En = "Right stick X", IsAxis = true },
        new() { Id = "right_stick_y", Zh = "右摇杆 前后", En = "Right stick Y", IsAxis = true },
        new() { Id = "left_trigger", Zh = "左扳机", En = "Left trigger", IsAxis = true },
        new() { Id = "right_trigger", Zh = "右扳机", En = "Right trigger", IsAxis = true },
        new() { Id = "left_grip", Zh = "左握把", En = "Left grip", IsAxis = true },
        new() { Id = "right_grip", Zh = "右握把", En = "Right grip", IsAxis = true },
        new() { Id = "a", Zh = "A 键（右手下）", En = "A (right lower)" },
        new() { Id = "b", Zh = "B 键（右手上）", En = "B (right upper)" },
        new() { Id = "x", Zh = "X 键（左手下）", En = "X (left lower)" },
        new() { Id = "y", Zh = "Y 键（左手上）", En = "Y (left upper)" },
        new() { Id = "left_stick_click", Zh = "左摇杆按下", En = "Left stick click" },
        new() { Id = "right_stick_click", Zh = "右摇杆按下", En = "Right stick click" },
        new() { Id = "left_menu", Zh = "左手菜单键", En = "Left menu button" },
    };

    public static readonly string[] Sections = { "common", "car", "foot", "menu" };
    public static readonly Dictionary<string, string> SectionZh = new()
    {
        ["common"] = "通用", ["car"] = "驾驶", ["foot"] = "步行", ["menu"] = "菜单",
    };

    public static readonly Function[] Functions =
    {
        F("common", "interact", Kind.Button, "a", "交互 / 确认", "Interact / confirm"),
        F("common", "back", Kind.Button, "b", "取消 / 返回", "Cancel / back"),
        F("common", "inventory", Kind.Button, "y", "物品栏", "Inventory"),
        F("common", "camera", Kind.Button, "x", "切换第一 / 第三人称视角", "First / third person view"),
        F("common", "map", Kind.Button, "left_grip", "全图", "Full map"),
        F("common", "pause", Kind.Button, "left_menu", "暂停菜单", "Pause menu"),
        F("common", "hud", Kind.Button, "", "显示 / 隐藏界面", "Show / hide HUD"),
        F("common", "recenter", Kind.Button, "right_stick_click", "重置视角（长按）", "Recenter view (hold)"),

        // Car defaults follow Aircar's Touch/Index bindings as far as the game's own driving goes: right trigger thrust,
        // left trigger reverse, left stick turn and climb. Cloudpunk's cars do not pitch or roll, so the right stick
        // does what those do to the flight path in Aircar: sideways (strafe) and up/down (climb).
        F("car", "gas", Kind.Axis, "right_trigger", "油门", "Accelerate"),
        F("car", "brake", Kind.Axis, "left_trigger", "刹车 / 倒车", "Brake / reverse"),
        F("car", "steer", Kind.Axis, "left_stick_x", "转向", "Steer"),
        F("car", "climb", Kind.Axis, "left_stick_y, right_stick_y", "升降", "Climb / sink"),
        F("car", "strafe", Kind.Axis, "right_stick_x", "横移", "Strafe"),
        F("car", "strafe_left", Kind.Button, "", "向左横移", "Strafe left"),
        F("car", "strafe_right", Kind.Button, "", "向右横移", "Strafe right"),
        F("car", "view", Kind.Button, "left_stick_click", "切换驾驶舱 / 追尾视角（X 键也可以）", "Cockpit / chase view (X does it too)"),

        F("foot", "move_x", Kind.Axis, "left_stick_x", "左右移动", "Move left / right"),
        F("foot", "move_y", Kind.Axis, "left_stick_y", "前后移动", "Move forward / back"),
        F("foot", "turn", Kind.Axis, "right_stick_x", "转身", "Turn"),
        F("foot", "sneak", Kind.Axis, "left_trigger", "潜行", "Sneak"),

        F("menu", "nav_x", Kind.Axis, "left_stick_x", "左右选择", "Navigate left / right"),
        F("menu", "nav_y", Kind.Axis, "left_stick_y", "上下选择", "Navigate up / down"),
        F("menu", "scroll", Kind.Axis, "right_stick_y", "滚动 / 地图缩放", "Scroll / map zoom"),
        F("menu", "tab_left", Kind.Button, "left_grip", "上一页", "Previous tab"),
        F("menu", "tab_right", Kind.Button, "right_grip", "下一页", "Next tab"),
    };

    static Function F(string s, string k, Kind kind, string def, string zh, string en) =>
        new() { Section = s, Key = k, Kind = kind, Default = def, Zh = zh, En = en };

    public sealed class Binding
    {
        public string Input;
        public bool Invert;
        public float DeadZone = -1;   // -1: the default for that kind of input

        public override string ToString() =>
            (Invert ? "-" : "") + Input + (DeadZone >= 0 ? "@" + DeadZone.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "");
    }

    public static List<Binding> ParseBindings(string text)
    {
        var list = new List<Binding>();
        foreach (var raw in text.Split(new[] { ',', '+' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Trim();
            if (s.Length == 0 || s == "none") continue;
            var b = new Binding();
            if (s.StartsWith("-")) { b.Invert = true; s = s.Substring(1); }
            int at = s.IndexOf('@');
            if (at >= 0)
            {
                float.TryParse(s.Substring(at + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b.DeadZone);
                s = s.Substring(0, at);
            }
            b.Input = s.Trim();
            if (FindInput(b.Input) != null) list.Add(b);
        }
        return list;
    }

    public static Input FindInput(string id)
    {
        foreach (var i in Inputs) if (i.Id == id) return i;
        return null;
    }

    // Function id -> bindings; every function present (defaults fill what the file lacks).
    public static Dictionary<string, List<Binding>> Load(string path, out bool existed)
    {
        var map = new Dictionary<string, List<Binding>>();
        existed = File.Exists(path);
        var fromFile = new Dictionary<string, string>();
        int version = 1;
        if (existed)
        {
            string section = "";
            foreach (var line0 in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = line0.Trim();
                if (line.StartsWith("# layout ") && int.TryParse(line.Substring(9).Trim(), out var v0)) { version = v0; continue; }
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                fromFile[section + "." + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }
        if (existed)
            foreach (var (ver, id, from, to) in Migrations)
                if (version < ver && fromFile.TryGetValue(id, out var cur) && Same(cur, from)) fromFile[id] = to;
        foreach (var f in Functions)
            map[f.Id] = ParseBindings(fromFile.TryGetValue(f.Id, out var v) ? v : f.Default);
        Migrated = existed && version < Version;
        return map;
    }

    // Whether the last Load read a file from an older layout version (the caller saves it back as the current one).
    public static bool Migrated;

    static bool Same(string a, string b) =>
        string.Join(",", ParseBindings(a)) == string.Join(",", ParseBindings(b));

    public static Dictionary<string, List<Binding>> Defaults()
    {
        var map = new Dictionary<string, List<Binding>>();
        foreach (var f in Functions) map[f.Id] = ParseBindings(f.Default);
        return map;
    }

    public static void Save(string path, Dictionary<string, List<Binding>> map)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cloudpunk VR controller layout. Edit with \"Cloudpunk VR 按键设置.exe\" in the game folder, or by hand.");
        sb.AppendLine("# Cloudpunk VR 按键映射。可以用游戏目录里的「Cloudpunk VR 按键设置.exe」编辑，也可以手改；游戏运行中保存后一秒内生效。");
        sb.AppendLine("#   function = input[, input...]   several inputs add up (axes) or any of them works (buttons)");
        sb.AppendLine("#   -input inverts an axis; input@0.3 sets its dead zone; none = unbound");
        sb.AppendLine($"# layout {Version}");
        sb.Append("# inputs:");
        foreach (var i in Inputs) sb.Append(' ').Append(i.Id);
        sb.AppendLine();
        foreach (var s in Sections)
        {
            sb.AppendLine();
            sb.AppendLine($"[{s}]");
            foreach (var f in Functions)
            {
                if (f.Section != s) continue;
                var list = map.TryGetValue(f.Id, out var l) ? l : new List<Binding>();
                sb.AppendLine($"# {f.Zh} / {f.En}");
                sb.AppendLine($"{f.Key} = {(list.Count == 0 ? "none" : string.Join(", ", list))}");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
