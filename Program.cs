using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.MediaFoundation;

// Elgato HD60 S low-latency preview. Right click = menu, F2 = settings. Keys: F fullscreen, L 1080p window for
// Lossless Scaling, V vsync, S sharp/smooth, I stats, T always on top, Esc quit.
// Args: --device <name part>, --monitor <n>, --ls (start in 1080p window), --stats
static class Program
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint idx);

    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        timeBeginPeriod(1);
        Application.Run(new PreviewForm(args));
    }

    public static void BoostThread()
    {
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        uint idx = 0;
        AvSetMmThreadCharacteristicsW("Games", ref idx);
    }
}

sealed class PreviewForm : Form
{
    static readonly Guid LowLatencyKey = new("9C27891A-ED7A-40e1-88E8-B22727A024EE");
    static readonly Guid Yuy2 = new("32595559-0000-0010-8000-00AA00389B71");
    const string Title = "Elgato Preview";
    readonly Settings cfg = Settings.Load();
    readonly Renderer renderer;
    readonly Thread thread;
    readonly Hud hud = new();
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer idle = new() { Interval = 1500 };
    readonly Cursor blank;
    SettingsForm? settings;
    volatile bool running = true, restart;
    volatile string status = "Starting...";
    volatile string statsText = "";
    volatile string[] devices = [];
    volatile Fmt[] formats = [];

    public PreviewForm(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--device" && i + 1 < args.Length) cfg.Device = args[++i];
            else if (args[i] == "--monitor" && i + 1 < args.Length) cfg.Monitor = int.TryParse(args[++i], out int n) ? @"\\.\DISPLAY" + n : args[i];
            else if (args[i] == "--stats") cfg.Stats = true;
            else if (args[i] == "--ls") cfg.Fullscreen = false;
        }
        Text = Title;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        KeyPreview = true;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
        using (var bmp = new Bitmap(32, 32)) blank = new Cursor(bmp.GetHicon());
        hud.Owner = this;

        menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
        menu.BackColor = Color.FromArgb(28, 28, 30);
        menu.ForeColor = Color.White;
        menu.Font = new Font("Segoe UI", 10f);
        menu.Opening += (_, _) => BuildMenu();
        ContextMenuStrip = menu;
        idle.Tick += (_, _) => { idle.Stop(); if (!menu.Visible) Cursor = blank; };
        MouseMove += (_, _) => { Cursor = Cursors.Default; idle.Stop(); idle.Start(); };
        DoubleClick += (_, _) => { cfg.Fullscreen = !cfg.Fullscreen; Apply(false, true); };

        ApplyMode();
        renderer = new Renderer(Handle, ClientSize.Width, ClientSize.Height) { Vsync = cfg.Vsync, Scaler = cfg.Scaler };
        RestartAudio();
        thread = new Thread(CaptureLoop) { IsBackground = true, Name = "capture" };
        thread.Start();
        idle.Start();
    }

    // Auto = the secondary monitor (the user games on the primary one), else the primary
    Screen TargetScreen => Screen.AllScreens.FirstOrDefault(s => s.DeviceName == cfg.Monitor)
        ?? Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen!;

    void ApplyMode()
    {
        var scr = TargetScreen.Bounds;
        StartPosition = FormStartPosition.Manual;
        TopMost = cfg.TopMost;
        // LS mode: exact 1920x1080 client, borderless window (not exclusive fullscreen) so Lossless Scaling can capture it
        Bounds = cfg.Fullscreen ? scr : new Rectangle(scr.X + (scr.Width - 1920) / 2, scr.Y + (scr.Height - 1080) / 2, 1920, 1080);
        UpdateHud();
    }

    // Single entry point for every setting change: push cfg to the renderer/window, save, optionally reopen the device
    void Apply(bool restartDevice, bool relayout)
    {
        renderer.Vsync = cfg.Vsync;
        renderer.Scaler = cfg.Scaler;
        renderer.Exclusive = cfg.Exclusive && cfg.Fullscreen && Form.ActiveForm == this;
        RestartAudio();
        if (relayout) ApplyMode(); else { TopMost = cfg.TopMost; UpdateHud(); }
        if (restartDevice) restart = true;
        cfg.Save();
    }

    static readonly string[] ScalerNames = ["bicubic", "pixel-exact", "bilinear"];
    void CycleScaler() { cfg.Scaler = (cfg.Scaler + 1) % ScalerNames.Length; Apply(false, false); }

    // Reopen the audio bridge only when its settings changed; starts off the UI thread, failure only shows in the stats
    AudioBridge? audio;
    string audioKey = "", audioNote = "audio off";
    readonly object audioLock = new();
    void RestartAudio()
    {
        string key = cfg.Audio ? cfg.AudioIn + "|" + cfg.AudioOut : "";
        if (key == audioKey) return;
        audioKey = key;
        Task.Run(() =>
        {
            lock (audioLock)
            {
                audio?.Dispose(); audio = null; audioNote = "audio off";
                if (key == "") return;
                try { audio = new AudioBridge(cfg.AudioIn, cfg.AudioOut); audioNote = audio.Label; }
                catch (Exception ex) { audioNote = "audio: " + ex.Message; }
            }
        });
    }

    void OpenSettings()
    {
        if (settings is { IsDisposed: false }) { settings.Activate(); return; }
        settings = new SettingsForm(cfg, () => devices, () => formats, Apply);
        settings.Show(this);
    }

    void UpdateHud()
    {
        if (!IsHandleCreated || !Visible) return;
        hud.Set(status != "" ? status : cfg.Stats ? statsText : "", new Point(Left + 16, Top + 16));
        Text = status == "" && cfg.Stats && statsText != "" ? $"{Title} - {statsText.Split((char)10)[0]}" : Title; // also readable from the taskbar
    }

    void SetStatus(string s)
    {
        status = s;
        if (IsHandleCreated && running) BeginInvoke(UpdateHud);
    }

    ToolStripMenuItem Item(string text, bool check, Action act, string keys = "")
    {
        var it = new ToolStripMenuItem(text) { Checked = check, ShortcutKeyDisplayString = keys, ForeColor = Color.White };
        it.Click += (_, _) => act();
        return it;
    }

    void BuildMenu()
    {
        menu.Items.Clear();
        menu.Items.AddRange([
            Item("Settings...", false, OpenSettings, "F2"),
            new ToolStripSeparator(),
            Item("Fullscreen (borderless)", cfg.Fullscreen, () => { cfg.Fullscreen = true; Apply(false, true); }, "F"),
            Item("1080p window for Lossless Scaling", !cfg.Fullscreen, () => { cfg.Fullscreen = false; Apply(false, true); }, "L"),
            new ToolStripSeparator(),
            Item("VSync", cfg.Vsync, () => { cfg.Vsync = !cfg.Vsync; Apply(false, false); }, "V"),
            Item("Scaling: " + ScalerNames[cfg.Scaler], false, CycleScaler, "S"),
            Item("Play capture audio", cfg.Audio, () => { cfg.Audio = !cfg.Audio; Apply(false, false); }),
            Item("Skip repeated frames", cfg.SkipDupes, () => { cfg.SkipDupes = !cfg.SkipDupes; Apply(false, false); }, "D"),
            Item("Exclusive fullscreen (AMD AFMF)", cfg.Exclusive, () => { cfg.Exclusive = !cfg.Exclusive; Apply(false, false); }, "E"),
            Item("Show stats", cfg.Stats, () => { cfg.Stats = !cfg.Stats; Apply(false, false); }, "I"),
            Item("Always on top", cfg.TopMost, () => { cfg.TopMost = !cfg.TopMost; Apply(false, false); }, "T"),
            new ToolStripSeparator(),
            Item("Quit", false, Close, "Esc"),
        ]);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true; TopMost = cfg.TopMost; Activate(); // a freshly started process may not get foreground rights
        UpdateHud();
    }

    // Exclusive fullscreen is dropped whenever the window loses focus (menus, settings, Alt+Tab) and restored on return
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); renderer.Exclusive = false; }
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); renderer.Exclusive = cfg.Exclusive && cfg.Fullscreen; }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (renderer != null) renderer.SetTarget(ClientSize.Width, ClientSize.Height);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override bool ProcessCmdKey(ref Message msg, Keys key)
    {
        switch (key)
        {
            case Keys.Escape: Close(); return true;
            case Keys.F2: OpenSettings(); return true;
            case Keys.F: case Keys.F11: case Keys.Alt | Keys.Enter: cfg.Fullscreen = true; Apply(false, true); return true;
            case Keys.L: cfg.Fullscreen = false; Apply(false, true); return true;
            case Keys.V: cfg.Vsync = !cfg.Vsync; Apply(false, false); return true;
            case Keys.S: CycleScaler(); return true;
            case Keys.D: cfg.SkipDupes = !cfg.SkipDupes; Apply(false, false); return true;
            case Keys.E: cfg.Exclusive = !cfg.Exclusive; Apply(false, false); return true;
            case Keys.I: cfg.Stats = !cfg.Stats; Apply(false, false); return true;
            case Keys.T: cfg.TopMost = !cfg.TopMost; Apply(false, false); return true;
        }
        return base.ProcessCmdKey(ref msg, key);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        running = false;
        lock (audioLock) audio?.Dispose();
        thread.Join(1500);
        base.OnFormClosing(e);
        Environment.Exit(0);
    }

    // Capture thread: ReadSample -> upload -> draw -> present, no queue, always the newest frame
    void CaptureLoop()
    {
        Program.BoostThread();
        MediaFactory.MFStartup();
        while (running)
        {
            try { renderer.Black(); RunDevice(); }
            catch (Exception ex) { SetStatus(formats.Length == 0 ? ex.Message : $"{ex.Message}{Environment.NewLine}Device formats: {string.Join(" | ", formats.Select(f => f.Label))}"); }
            if (!restart) Thread.Sleep(1000);
        }
    }

    static string FourCc(Guid g)
    {
        var b = g.ToByteArray()[..4];
        return b.All(c => c >= 32 && c < 127) ? Encoding.ASCII.GetString(b) : "RGB";
    }

    unsafe void RunDevice()
    {
        restart = false;
        using var enumAttrs = MediaFactory.MFCreateAttributes(1);
        enumAttrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        var found = MediaFactory.MFEnumDeviceSources(enumAttrs).ToArray();
        IMFMediaSource source;
        try
        {
            var names = found.Select(d => d.GetString(CaptureDeviceAttributeKeys.FriendlyName) ?? "").ToArray();
            devices = names;
            int idx = Array.FindIndex(names, n => n.Contains(cfg.Device, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new InvalidOperationException($"No capture device matching '{cfg.Device}'.\nAvailable: {(names.Length == 0 ? "none" : string.Join(", ", names))}\n"
                    + "If the Elgato is plugged in, check Device Manager (it needs the Elgato driver) and try a USB 3 port.\nF2 = settings.");
            source = found[idx].ActivateObject<IMFMediaSource>();
        }
        finally { foreach (var d in found) d.Dispose(); }

        using (source)
        {
            using var readerAttrs = MediaFactory.MFCreateAttributes(3);
            readerAttrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
            readerAttrs.Set(LowLatencyKey, 1u);
            using var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, readerAttrs);
            const SourceReaderIndex Video = SourceReaderIndex.FirstVideoStream;

            // native types: Auto = 1920x1080 @ ~60, NV12 > YUY2 > anything else; or the one picked in settings
            var all = new List<(Fmt f, IMFMediaType t, int score, bool common)>();
            for (int i = 0; ; i++)
            {
                IMFMediaType t;
                try { t = reader.GetNativeMediaType(Video, i); } catch { break; }
                ulong fs = t.GetUInt64(MediaTypeAttributeKeys.FrameSize), fr = t.GetUInt64(MediaTypeAttributeKeys.FrameRate);
                int w = (int)(fs >> 32), h = (int)(fs & 0xFFFFFFFF);
                double fps = (double)(fr >> 32) / Math.Max(1u, (uint)(fr & 0xFFFFFFFF));
                var sub = t.GetGUID(MediaTypeAttributeKeys.Subtype);
                string label = $"{w}x{h}  {fps:0.##} fps  {(sub == VideoFormatGuids.NV12 ? "NV12" : sub == Yuy2 ? "YUY2" : FourCc(sub))}";
                int score = (w == 1920 && h == 1080 ? 1000 : 0) + (fps > 59 ? 500 : (int)fps) + (sub == VideoFormatGuids.NV12 ? 30 : sub == Yuy2 ? 20 : 0);
                // the HD60 S lists ~200 types (every fps from 1 to 60); the menu only shows the usual rates
                bool common = new[] { 23.976, 24, 25, 29.97, 30, 50, 59.94, 60 }.Any(r => Math.Abs(fps - r) < 0.1);
                all.Add((new Fmt(label, label), t, score, common));
            }
            if (all.Count == 0) throw new InvalidOperationException("The device reports no video formats.");
            formats = all.Where(a => a.common).Select(a => a.f).DistinctBy(f => f.Key).ToArray();
            var pick = all.FirstOrDefault(a => a.f.Key == cfg.VideoFormat);
            if (pick.t == null) pick = all.OrderByDescending(a => a.score).First();
            var best = pick.t;
            try { reader.SetCurrentMediaType(Video, best); }
            catch (Exception ex) { throw new InvalidOperationException($"{ex.Message.Split((char)10)[0]}{Environment.NewLine}format: {pick.f.Label}{Environment.NewLine}all: {string.Join(" | ", all.Select(a => a.f.Label))}"); }
            ulong size = best.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            var native = best.GetGUID(MediaTypeAttributeKeys.Subtype);
            bool yuy2 = native == Yuy2; // read packed YUY2 straight in the shader: no converter MFT, one copy less
            if (native != VideoFormatGuids.NV12 && !yuy2)
            {
                using var nv12 = MediaFactory.MFCreateMediaType();
                nv12.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                nv12.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                nv12.Set(MediaTypeAttributeKeys.FrameSize, size);
                reader.SetCurrentMediaType(Video, nv12);
            }
            foreach (var a in all) a.t.Dispose();
            int fw = (int)(size >> 32), fh = (int)(size & 0xFFFFFFFF);
            SetStatus("Waiting for signal...");

            var sw = Stopwatch.StartNew();
            long last = Stopwatch.GetTimestamp(), worst = 0, appWorst = 0;
            double appSum = 0, ageSum = 0; int ages = 0; // app = ReadSample returned -> Present returned; age = driver timestamp -> ReadSample returned (only when the clocks agree)
            int frames = 0, shown = 0;
            byte[]? prev = null;
            // A game below the capture rate (30 fps on a 60 Hz signal) arrives as repeats with capture noise (+-2 on a third of the
            // bytes, measured on the HD60 S, real changes reach 200): present only new frames so the window follows the game
            // framerate and Lossless Scaling sees the real source rate. Sampled compare against the last shown frame.
            const int Stride = 5, Tolerance = 6;
            void Show(IntPtr p, int pitch)
            {
                var cur = new ReadOnlySpan<byte>((void*)p, yuy2 ? pitch * fh : pitch * fh * 3 / 2);
                if (cfg.SkipDupes)
                {
                    if (prev != null && prev.Length == cur.Length)
                    {
                        bool same = true;
                        for (int i = 0; i < cur.Length && same; i += Stride) same = Math.Abs(cur[i] - prev[i]) <= Tolerance;
                        if (same) return;
                    }
                    else prev = new byte[cur.Length];
                    cur.CopyTo(prev);
                }
                renderer.Draw(p, pitch, fw, fh, yuy2); shown++;
            }
            while (running && !restart)
            {
                using var sample = reader.ReadSample(Video, 0, out _, out var flags, out _);
                if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0) throw new InvalidOperationException("Device lost, reconnecting...");
                if (sample == null) continue;
                long t0 = Stopwatch.GetTimestamp();
                long age = MediaFactory.MFGetSystemTime() - sample.SampleTime;
                if (age is > 0 and < 5_000_000) { ageSum += age / 10000.0; ages++; }
                if (status != "") SetStatus("");
                using var buf = sample.ConvertToContiguousBuffer();
                using var b2d = buf.QueryInterfaceOrNull<IMF2DBuffer>();
                if (b2d != null)
                {
                    b2d.Lock2D(out IntPtr p, out int pitch);
                    try { Show(p, pitch); } finally { b2d.Unlock2D(); }
                }
                else
                {
                    buf.Lock(out IntPtr p, out _, out int len);
                    try { Show(p, yuy2 ? len / fh : len * 2 / (3 * fh)); } finally { buf.Unlock(); }
                }
                long now = Stopwatch.GetTimestamp();
                worst = Math.Max(worst, now - last); last = now; frames++;
                appWorst = Math.Max(appWorst, now - t0); appSum += now - t0;
                if (sw.ElapsedMilliseconds >= 1000)
                {
                    statsText = $"{fw}x{fh}   {shown * 1000.0 / sw.ElapsedMilliseconds:F1} fps (capture {frames * 1000.0 / sw.ElapsedMilliseconds:F0})\nworst frame gap {worst * 1000.0 / Stopwatch.Frequency:F1} ms\napp {appSum * 1000.0 / Stopwatch.Frequency / Math.Max(1, frames):F1} ms (worst {appWorst * 1000.0 / Stopwatch.Frequency:F1})   driver age {(ages > 0 ? (ageSum / ages).ToString("F1") + " ms" : "n/a")}\nvsync {(renderer.Vsync ? "on" : "off")}   scaling {ScalerNames[cfg.Scaler]}\n{(audio is { } a ? $"audio {a.PeakDb:F0} dB" : audioNote)}";
                    if (cfg.Stats) BeginInvoke(UpdateHud);
                    frames = 0; shown = 0; worst = 0; appWorst = 0; appSum = 0; ageSum = 0; ages = 0; sw.Restart();
                }
            }
        }
    }
}
