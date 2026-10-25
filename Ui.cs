using System.Text.Json;

sealed class Settings
{
    public string Device { get; set; } = "HD60";
    public string VideoFormat { get; set; } = ""; // "" = auto (1080p60 NV12 first)
    public string Monitor { get; set; } = ""; // Screen.DeviceName, "" = auto (secondary monitor)
    public bool Fullscreen { get; set; } = true;
    public bool Vsync { get; set; }
    public int Scaler { get; set; } // 0 = bicubic (default), 1 = pixel-exact, 2 = bilinear
    public bool Audio { get; set; } // opt-in: plays the capture card audio from this process (Discord window share carries it)
    public string AudioIn { get; set; } = "HD60 S Audio"; // part of the endpoint name
    public string AudioOut { get; set; } = ""; // "" = Windows default output
    public bool SkipDupes { get; set; } = true;
    public bool Exclusive { get; set; } // exclusive fullscreen, needed for AMD AFMF on RX 6000
    public bool Stats { get; set; }
    public bool TopMost { get; set; }

    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElgatoPreview", "settings.json");
    public static Settings Load() { try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_)) ?? new(); } catch { return new(); } }
    public void Save() { try { Directory.CreateDirectory(Path.GetDirectoryName(File_)!); File.WriteAllText(File_, JsonSerializer.Serialize(this)); } catch { } }
}

record Fmt(string Key, string Label);

// Click-through status/stats overlay owned by the preview window
sealed class Hud : Form
{
    readonly Label l = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 12f), Padding = new Padding(14, 10, 14, 10), MaximumSize = new Size(1000, 0) };
    public Hud()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 20, 22); Opacity = 0.88; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Controls.Add(l);
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x20; return p; } } // noactivate, toolwindow, click-through
    public void Set(string text, Point at) { l.Text = text; Location = at; Visible = text != ""; }
}

sealed class DarkColors : ProfessionalColorTable
{
    static readonly Color Bg = Color.FromArgb(28, 28, 30), Hi = Color.FromArgb(58, 58, 62);
    public override Color ToolStripDropDownBackground => Bg;
    public override Color ImageMarginGradientBegin => Bg;
    public override Color ImageMarginGradientMiddle => Bg;
    public override Color ImageMarginGradientEnd => Bg;
    public override Color MenuBorder => Hi;
    public override Color MenuItemBorder => Hi;
    public override Color MenuItemSelected => Hi;
    public override Color MenuItemSelectedGradientBegin => Hi;
    public override Color MenuItemSelectedGradientEnd => Hi;
    public override Color MenuItemPressedGradientBegin => Hi;
    public override Color MenuItemPressedGradientEnd => Hi;
    public override Color SeparatorDark => Hi;
    public override Color SeparatorLight => Bg;
    public override Color CheckBackground => Hi;
    public override Color CheckSelectedBackground => Hi;
    public override Color CheckPressedBackground => Hi;
}

// Settings window: every change applies immediately and is saved
sealed class SettingsForm : Form
{
    static readonly Color Bg = Color.FromArgb(24, 24, 26), Field = Color.FromArgb(40, 40, 44);
    readonly Settings cfg;
    readonly Func<string[]> devices;
    readonly Func<Fmt[]> formats;
    readonly Action<bool, bool> apply; // (restart capture, relayout window)
    readonly ComboBox cbDevice = Combo(), cbFormat = Combo(), cbMonitor = Combo(), cbMode = Combo(), cbScale = Combo(), cbAIn = Combo(), cbAOut = Combo();
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    string seen = "";
    bool loading;

    static ComboBox Combo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Color.White, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };

    public SettingsForm(Settings cfg, Func<string[]> devices, Func<Fmt[]> formats, Action<bool, bool> apply)
    {
        this.cfg = cfg; this.devices = devices; this.formats = formats; this.apply = apply;
        Text = "Elgato Preview - Settings";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = Bg; ForeColor = Color.White; Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(20, 16, 20, 16) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        Row(t, "Capture device", cbDevice);
        Row(t, "Video format", cbFormat);
        Row(t, "Monitor", cbMonitor);
        Row(t, "Window mode", cbMode);
        Row(t, "Scaling", cbScale);
        Row(t, "Audio input", cbAIn);
        Row(t, "Audio output", cbAOut);
        Check(t, "Play capture audio (off by default)", cfg.Audio, v => cfg.Audio = v, false, false);
        Check(t, "Skip repeated frames (window follows the game framerate, for Lossless Scaling)", cfg.SkipDupes, v => cfg.SkipDupes = v, false, false);
        Check(t, "Exclusive fullscreen (for AMD Fluid Motion Frames, off by default)", cfg.Exclusive, v => cfg.Exclusive = v, false, false);
        Check(t, "VSync (off = lowest latency, may tear)", cfg.Vsync, v => cfg.Vsync = v, false, false);
        Check(t, "Show stats overlay", cfg.Stats, v => cfg.Stats = v, false, false);
        Check(t, "Always on top", cfg.TopMost, v => cfg.TopMost = v, false, true);
        var close = new Button { Text = "Close", FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Color.White, Anchor = AnchorStyles.Right, Width = 100, Height = 32, Margin = new Padding(0, 12, 0, 0) };
        close.Click += (_, _) => Close();
        t.Controls.Add(close, 1, t.RowCount++);
        Controls.Add(t);

        cbMode.Items.AddRange(["Fullscreen (borderless)", "1080p window (for Lossless Scaling)"]);
        cbScale.Items.AddRange(["Bicubic (sharp and smooth, best for 1080p to 4K)", "Pixel-exact (blocky, only for integer scale)", "Bilinear (soft)"]);
        cbMode.SelectedIndex = cfg.Fullscreen ? 0 : 1;
        cbScale.SelectedIndex = Math.Clamp(cfg.Scaler, 0, 2);
        cbDevice.SelectedIndexChanged += (_, _) => { if (!loading && cbDevice.SelectedItem is string s) { cfg.Device = s; apply(true, false); } };
        cbFormat.SelectedIndexChanged += (_, _) => { if (!loading) { cfg.VideoFormat = cbFormat.SelectedIndex <= 0 ? "" : formats()[cbFormat.SelectedIndex - 1].Key; apply(true, false); } };
        cbMonitor.SelectedIndexChanged += (_, _) => { if (!loading) { cfg.Monitor = cbMonitor.SelectedIndex <= 0 ? "" : Screen.AllScreens[cbMonitor.SelectedIndex - 1].DeviceName; apply(false, true); } };
        cbMode.SelectedIndexChanged += (_, _) => { cfg.Fullscreen = cbMode.SelectedIndex == 0; apply(false, true); };
        cbScale.SelectedIndexChanged += (_, _) => { cfg.Scaler = cbScale.SelectedIndex; apply(false, false); };
        var ai = AudioBridge.Names(NAudio.CoreAudioApi.DataFlow.Capture); var ao = AudioBridge.Names(NAudio.CoreAudioApi.DataFlow.Render);
        cbAIn.Items.AddRange(ai); cbAIn.SelectedIndex = Math.Max(0, Array.FindIndex(ai, n => n.Contains(cfg.AudioIn, StringComparison.OrdinalIgnoreCase)));
        cbAOut.Items.Add("Windows default"); cbAOut.Items.AddRange(ao); cbAOut.SelectedIndex = cfg.AudioOut == "" ? 0 : Math.Max(0, Array.FindIndex(ao, n => n.Contains(cfg.AudioOut, StringComparison.OrdinalIgnoreCase)) + 1);
        cbAIn.SelectedIndexChanged += (_, _) => { if (cbAIn.SelectedItem is string n) { cfg.AudioIn = n; apply(false, false); } };
        cbAOut.SelectedIndexChanged += (_, _) => { cfg.AudioOut = cbAOut.SelectedIndex <= 0 ? "" : (string)cbAOut.SelectedItem!; apply(false, false); };
        Fill();
        refresh.Tick += (_, _) => Fill();
        refresh.Start();
    }

    static void Row(TableLayoutPanel t, string label, Control c)
    {
        int r = t.RowCount++;
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.Silver }, 0, r);
        t.Controls.Add(c, 1, r);
    }

    void Check(TableLayoutPanel t, string text, bool on, Action<bool> set, bool restart, bool layout)
    {
        var c = new CheckBox { Text = text, Checked = on, AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
        c.CheckedChanged += (_, _) => { set(c.Checked); apply(restart, layout); };
        int r = t.RowCount++;
        t.Controls.Add(c, 1, r);
    }

    // Devices and formats arrive from the capture thread, so repopulate when they change (never under an open dropdown)
    void Fill()
    {
        var d = devices(); var f = formats();
        string key = string.Join("|", d) + "#" + string.Join("|", f.Select(x => x.Key)) + "#" + Screen.AllScreens.Length;
        if (key == seen || cbDevice.DroppedDown || cbFormat.DroppedDown || cbMonitor.DroppedDown) return;
        seen = key; loading = true;
        cbDevice.Items.Clear(); cbDevice.Items.AddRange(d);
        cbDevice.SelectedIndex = Array.FindIndex(d, n => n.Contains(cfg.Device, StringComparison.OrdinalIgnoreCase));
        if (d.Length == 0) { cbDevice.Items.Add("(no device found)"); cbDevice.SelectedIndex = 0; }
        cbFormat.Items.Clear(); cbFormat.Items.Add("Auto (1080p60 NV12)");
        cbFormat.Items.AddRange(f.Select(x => (object)x.Label).ToArray());
        int fi = Array.FindIndex(f, x => x.Key == cfg.VideoFormat);
        cbFormat.SelectedIndex = fi + 1;
        cbMonitor.Items.Clear(); cbMonitor.Items.Add("Auto (secondary monitor)");
        foreach (var s in Screen.AllScreens)
            cbMonitor.Items.Add($"{s.DeviceName.Replace(@"\\.\DISPLAY", "Monitor ")}: {s.Bounds.Width}x{s.Bounds.Height}{(s.Primary ? " (primary)" : "")}");
        cbMonitor.SelectedIndex = Math.Max(0, Array.FindIndex(Screen.AllScreens, s => s.DeviceName == cfg.Monitor) + 1);
        loading = false;
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { refresh.Stop(); base.OnFormClosed(e); }
}
