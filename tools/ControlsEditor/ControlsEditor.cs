// Cloudpunk VR controls: a small editor for the controller layout file (BepInEx/config/CloudpunkVR.controls.cfg).
// Built against .NET Framework 4.x, which every Windows 10/11 has; shares ControlMap.cs with the plugin.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CloudpunkVR;

internal static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var path = args.Length > 0 ? args[0] : FindLayoutFile();
        if (path == null) return;
        Application.Run(new EditorForm(path));
    }

    // Next to the game: <game>/BepInEx/config/CloudpunkVR.controls.cfg. Elsewhere: ask for the game folder.
    static string FindLayoutFile()
    {
        foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, @"G:\Games\CloudpunkVR" })
        {
            if (Directory.Exists(Path.Combine(dir, "BepInEx")))
                return Path.Combine(dir, "BepInEx", "config", ControlMap.FileName);
        }
        using (var dlg = new FolderBrowserDialog { Description = "选择 Cloudpunk VR 的游戏目录（里面有 BepInEx 文件夹）" })
        {
            if (dlg.ShowDialog() != DialogResult.OK) return null;
            return Path.Combine(dlg.SelectedPath, "BepInEx", "config", ControlMap.FileName);
        }
    }
}

internal sealed class EditorForm : Form
{
    const int Slots = 2;

    sealed class Row
    {
        public ControlMap.Function Function;
        public ComboBox[] Input = new ComboBox[Slots];
        public CheckBox[] Invert = new CheckBox[Slots];
        public NumericUpDown[] Dead = new NumericUpDown[Slots];
    }

    readonly string path;
    readonly List<Row> rows = new List<Row>();
    readonly ToolStripStatusLabel status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    bool dirty;

    static readonly string[] InputIds;
    static readonly string[] InputNames;

    static EditorForm()
    {
        InputIds = new string[ControlMap.Inputs.Length + 1];
        InputNames = new string[ControlMap.Inputs.Length + 1];
        InputIds[0] = "";
        InputNames[0] = "（无）";
        for (int i = 0; i < ControlMap.Inputs.Length; i++)
        {
            InputIds[i + 1] = ControlMap.Inputs[i].Id;
            InputNames[i + 1] = ControlMap.Inputs[i].Zh;
        }
    }

    public EditorForm(string path)
    {
        this.path = path;
        Text = "Cloudpunk VR 按键设置";
        // Layout below is in 96-dpi pixels; WinForms scales it to the screen's DPI when the form loads.
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(760, 480);

        var top = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 104, Padding = new Padding(10, 8, 10, 0),
            Text = "每个功能可以绑两个输入：轴类功能把两个输入相加，按键类功能任意一个按下都算。\n" +
                   "「反向」把轴的方向反过来；「死区」是摇杆或扳机推到多少才开始生效。保存后游戏一秒内生效，不用重启。\n" +
                   "按键名按 Quest 手柄：A / B 在右手，X / Y 在左手；Index 手柄左手的 A / B 算作 X / Y。",
        };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var section in ControlMap.Sections)
            tabs.TabPages.Add(BuildPage(section));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 8, 6), FlowDirection = FlowDirection.LeftToRight };
        var save = new Button { Text = "保存", AutoSize = true };
        var reset = new Button { Text = "恢复默认", AutoSize = true };
        var reload = new Button { Text = "重新读取", AutoSize = true };
        var folder = new Button { Text = "打开配置文件夹", AutoSize = true };
        save.Click += (s, e) => Save();
        reset.Click += (s, e) => { Fill(ControlMap.Defaults()); dirty = true; UpdateStatus("已恢复默认，保存后生效"); };
        reload.Click += (s, e) => LoadFile();
        folder.Click += (s, e) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + (File.Exists(path) ? path : Path.GetDirectoryName(path)) + "\"");
        };
        buttons.Controls.AddRange(new Control[] { save, reset, reload, folder });
        var strip = new StatusStrip { SizingGrip = false };
        strip.Items.Add(status);

        Controls.Add(tabs);
        Controls.Add(top);
        Controls.Add(buttons);
        Controls.Add(strip);
        AcceptButton = save;
        FormClosing += (s, e) =>
        {
            if (!dirty) return;
            var r = MessageBox.Show(this, "有改动还没保存，要保存吗？", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) e.Cancel = true;
            else if (r == DialogResult.Yes && !Save()) e.Cancel = true;
        };
        LoadFile();
    }

    TabPage BuildPage(string section)
    {
        var page = new TabPage(ControlMap.SectionZh[section]) { AutoScroll = true, Padding = new Padding(6) };
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 + Slots * 3, Location = new Point(6, 6), CellBorderStyle = TableLayoutPanelCellBorderStyle.None };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        for (int k = 0; k < Slots; k++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        }
        void Header(string text, int col) => grid.Controls.Add(new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 6, 3, 6) }, col, 0);
        Header("功能", 0);
        for (int k = 0; k < Slots; k++)
        {
            Header(k == 0 ? "输入" : "第二个输入", 1 + k * 3);
            Header("反向", 2 + k * 3);
            Header("死区", 3 + k * 3);
        }
        int r = 1;
        foreach (var f in ControlMap.Functions)
        {
            if (f.Section != section) continue;
            var row = new Row { Function = f };
            grid.Controls.Add(new Label { Text = f.Zh, AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, r);
            for (int k = 0; k < Slots; k++)
            {
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
                combo.Items.AddRange(InputNames);
                var inv = new CheckBox { AutoSize = true, Margin = new Padding(12, 6, 3, 3), Enabled = f.Kind == ControlMap.Kind.Axis };
                var dead = new NumericUpDown { Width = 80, DecimalPlaces = 2, Increment = 0.05m, Minimum = 0, Maximum = 0.95m, Enabled = f.Kind == ControlMap.Kind.Axis };
                combo.SelectedIndexChanged += (s, e) => { dirty = true; UpdateStatus(""); };
                inv.CheckedChanged += (s, e) => dirty = true;
                dead.ValueChanged += (s, e) => dirty = true;
                row.Input[k] = combo;
                row.Invert[k] = inv;
                row.Dead[k] = dead;
                grid.Controls.Add(combo, 1 + k * 3, r);
                grid.Controls.Add(inv, 2 + k * 3, r);
                grid.Controls.Add(dead, 3 + k * 3, r);
            }
            rows.Add(row);
            r++;
        }
        grid.RowCount = r;
        page.Controls.Add(grid);
        if (section == "foot")
            page.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, Location = new Point(10, 40 + r * 34),
                Text = "转身是快速转动还是平滑转动、行走跟视线还是跟左手柄，在游戏的「选项」里设置。",
            });
        if (section == "car")
            page.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, Location = new Point(10, 40 + r * 34),
                Text = "默认照 Aircar：右扳机油门，左扳机倒车，左摇杆左右转向、前后升降；右摇杆左右横移、前后升降。死区也照 Aircar。",
            });
        return page;
    }

    // same defaults as ControlLayout.DefaultDeadZone (Aircar's input settings)
    static float DefaultDead(string input) =>
        input.EndsWith("_trigger") ? 0.15f : input.Contains("_stick_") && !input.EndsWith("click") ? 0.08f : 0.05f;

    void Fill(Dictionary<string, List<ControlMap.Binding>> map)
    {
        foreach (var row in rows)
        {
            var list = map.TryGetValue(row.Function.Id, out var l) ? l : new List<ControlMap.Binding>();
            for (int k = 0; k < Slots; k++)
            {
                var b = k < list.Count ? list[k] : null;
                int idx = b == null ? 0 : Array.IndexOf(InputIds, b.Input);
                row.Input[k].SelectedIndex = Math.Max(0, idx);
                row.Invert[k].Checked = b != null && b.Invert;
                float dz = b == null ? 0.08f : b.DeadZone >= 0 ? b.DeadZone : DefaultDead(b.Input);
                row.Dead[k].Value = (decimal)Math.Min(0.95f, Math.Max(0f, dz));
            }
        }
    }

    Dictionary<string, List<ControlMap.Binding>> Collect()
    {
        var map = new Dictionary<string, List<ControlMap.Binding>>();
        foreach (var row in rows)
        {
            var list = new List<ControlMap.Binding>();
            for (int k = 0; k < Slots; k++)
            {
                int idx = row.Input[k].SelectedIndex;
                if (idx <= 0) continue;
                var input = InputIds[idx];
                var b = new ControlMap.Binding { Input = input, Invert = row.Invert[k].Checked };
                float dz = (float)row.Dead[k].Value;
                if (row.Function.Kind == ControlMap.Kind.Axis && Math.Abs(dz - DefaultDead(input)) > 0.001f) b.DeadZone = dz;
                list.Add(b);
            }
            map[row.Function.Id] = list;
        }
        return map;
    }

    void LoadFile()
    {
        try
        {
            var map = ControlMap.Load(path, out bool existed);
            Fill(map);
            dirty = false;
            UpdateStatus(existed ? "已读取 " + path : "还没有配置文件，显示的是默认布局；保存后创建 " + path);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "读取失败：" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    bool Save()
    {
        try
        {
            ControlMap.Save(path, Collect());
            dirty = false;
            UpdateStatus("已保存 " + DateTime.Now.ToString("HH:mm:ss") + "，游戏运行中一秒内生效");
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "保存失败：" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    void UpdateStatus(string text)
    {
        if (text.Length > 0) status.Text = text;
        else if (dirty) status.Text = "有改动未保存";
    }
}
