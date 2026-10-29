// Sim Mode tray app: one hotkey flips between the sim-racing monitors alone and the desk monitors.
// Build: build.bat (uses the .NET Framework csc that ships with Windows)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    public static readonly int ShowPanelMessage = RegisterWindowMessage("SimMode.ShowPanel");

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mutex = new Mutex(true, "SimModeTray", out created))
        {
            if (!created)
            {
                // Already running: starting the exe again opens its panel.
                PostMessage((IntPtr)0xFFFF, ShowPanelMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Ui.Init();
            Application.Run(new TrayApp(Array.IndexOf(args, "/startup") >= 0));
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}

enum Mode { Sim, Desk, All, Other }

class TrayApp : ApplicationContext
{
    const string RegKey = @"Software\SimMode";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "SimMode";
    const string DefaultSimMonitor = "34E6UC";

    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
    readonly Icon iconDesk, iconSim, iconOther, iconBusy;
    readonly MessageWindow messages;
    readonly PanelForm panel;
    readonly Dictionary<string, int> layoutX = new Dictionary<string, int>();   // left-to-right order of the monitors

    ToolStripMenuItem miSim, miDesk, miAll;
    List<string> simMonitors;    // at least one
    List<string> deskMonitors;   // empty = every connected monitor except the sim monitors
    bool busy;

    public TrayApp(bool atStartup)
    {
        // "SimMonitor" held the single sim monitor of older versions.
        simMonitors = SplitList(LoadSetting("SimMonitors", LoadSetting("SimMonitor", DefaultSimMonitor)));
        if (simMonitors.Count == 0) simMonitors.Add(DefaultSimMonitor);
        deskMonitors = SplitList(LoadSetting("DeskMonitors", ""));
        foreach (var item in SplitList(LoadSetting("Layout", "")))
        {
            int eq = item.LastIndexOf('='), x;
            if (eq > 0 && int.TryParse(item.Substring(eq + 1), out x)) layoutX[item.Substring(0, eq)] = x;
        }

        iconDesk = MakeIcon(Ui.Desk);
        iconSim = MakeIcon(Ui.Sim);
        iconOther = MakeIcon(Ui.Other);
        iconBusy = MakeIcon(Ui.Busy);

        panel = new PanelForm(this);
        BuildMenu();
        tray.Icon = iconOther;
        tray.Visible = true;
        tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) panel.Toggle();
            else if (e.Button == MouseButtons.Middle) Toggle();
        };

        messages = new MessageWindow(Toggle, () => panel.ShowNearTray());

        poll.Interval = 3000;
        poll.Tick += (s, e) => UpdateUi();
        poll.Start();
        UpdateUi();

        if (atStartup)
        {
            // Windows turns every monitor back on at boot (Sim/Desk mode are not saved to its display settings).
            if (DeskAtStartup && CurrentMode() == Mode.All)
            {
                var delay = new System.Windows.Forms.Timer { Interval = 5000 };
                delay.Tick += (s, e) => { delay.Dispose(); GoDesk(); };
                delay.Start();
            }
        }
        else panel.ShowNearTray();

        if (!HotkeyOk)
            tray.ShowBalloonTip(4000, "Sim Mode", "Ctrl+Alt+S is used by another app. Use the tray icon instead.", ToolTipIcon.Warning);
    }

    public bool Busy { get { return busy; } }
    public bool HotkeyOk { get { return messages.HotkeyRegistered; } }
    public List<string> SimMonitors { get { return new List<string>(simMonitors); } }
    public bool IsSim(string name) { return simMonitors.Exists(s => Displays.Matches(name, s)); }

    void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        miSim = new ToolStripMenuItem("Sim mode", null, (s, e) => GoSim());
        miDesk = new ToolStripMenuItem("Desk mode", null, (s, e) => GoDesk());
        miAll = new ToolStripMenuItem("All monitors", null, (s, e) => GoAll());
        var open = new ToolStripMenuItem("Open Sim Mode", null, (s, e) => panel.ShowNearTray());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miSim);
        menu.Items.Add(miDesk);
        menu.Items.Add(miAll);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => Quit());
        menu.Opening += (s, e) => UpdateUi();
        tray.ContextMenuStrip = menu;
    }

    // ---- state and settings, also used by the panel ----

    public Mode CurrentMode()
    {
        var active = Displays.Active();
        if (Displays.SameSet(active, SimSet())) return Mode.Sim;
        if (Displays.SameSet(active, DeskSet())) return Mode.Desk;
        if (active.Length == Displays.Connected().Length) return Mode.All;
        return Mode.Other;
    }

    // The sim monitors that are plugged in; a missing one does not block Sim mode on the others.
    public List<string> SimSet()
    {
        var connected = Displays.Connected();
        return simMonitors.FindAll(s => Array.Exists(connected, n => Displays.Matches(n, s)));
    }

    public List<string> DeskSet()
    {
        if (deskMonitors.Count > 0) return new List<string>(deskMonitors);
        var set = new List<string>();
        foreach (var n in Displays.Connected()) if (!IsSim(n)) set.Add(n);
        return set;
    }

    public void SetSimMonitors(List<string> list)
    {
        if (list.Count == 0) return;
        simMonitors = new List<string>(list);
        SaveSetting("SimMonitors", string.Join("|", simMonitors.ToArray()));
        deskMonitors.RemoveAll(IsSim);
        SaveSetting("DeskMonitors", string.Join("|", deskMonitors.ToArray()));
        UpdateUi();
    }

    public void SetDeskMonitors(List<string> list)
    {
        // Store "all except the sim monitor" as the default, so new monitors are included automatically.
        var all = new List<string>();
        foreach (var n in Displays.Connected()) if (!IsSim(n)) all.Add(n);
        bool isDefault = list.Count == all.Count && all.TrueForAll(list.Contains);
        deskMonitors = isDefault ? new List<string>() : list;
        SaveSetting("DeskMonitors", string.Join("|", deskMonitors.ToArray()));
        UpdateUi();
    }

    public bool DeskAtStartup
    {
        get { return LoadSetting("DeskAtStartup", "1") == "1"; }
        set { SaveSetting("DeskAtStartup", value ? "1" : "0"); }
    }

    public bool StartWithWindows
    {
        get
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(AppName) != null;
        }
        set
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\" /startup");
                else k.DeleteValue(AppName, false);
            }
        }
    }

    // Connected monitors, left to right as last seen on the desktop.
    public List<Displays.MonitorInfo> MonitorsInOrder()
    {
        var mons = Displays.Monitors();
        if (mons.FindAll(m => m.Active).Count >= 2)
        {
            bool changed = false;
            foreach (var m in mons)
            {
                int old;
                if (m.Active && (!layoutX.TryGetValue(m.Name, out old) || old != m.X)) { layoutX[m.Name] = m.X; changed = true; }
            }
            if (changed)
            {
                var parts = new List<string>();
                foreach (var kv in layoutX) parts.Add(kv.Key + "=" + kv.Value);
                SaveSetting("Layout", string.Join("|", parts.ToArray()));
            }
        }
        var order = new List<Displays.MonitorInfo>(mons);
        order.Sort((a, b) =>
        {
            int xa, xb;
            bool ka = layoutX.TryGetValue(a.Name, out xa), kb = layoutX.TryGetValue(b.Name, out xb);
            if (ka && kb) return xa.CompareTo(xb);
            if (ka != kb) return ka ? -1 : 1;
            return mons.IndexOf(a).CompareTo(mons.IndexOf(b));
        });
        return order;
    }

    // ---- switching ----

    // Hotkey and middle-click: Sim mode <-> Desk mode.
    public void Toggle()
    {
        if (CurrentMode() == Mode.Sim) GoDesk();
        else GoSim();
    }

    public void GoSim()
    {
        // Ordered here, on the UI thread: the middle sim monitor (left to right) goes first, so a
        // triple-screen rig gets its centre screen as primary when the desk primary turns off.
        var sims = new List<string>();
        foreach (var m in MonitorsInOrder())
            if (IsSim(m.Name) && !sims.Contains(m.Name)) sims.Add(m.Name);
        if (sims.Count > 2)
        {
            string centre = sims[(sims.Count - 1) / 2];
            sims.Remove(centre);
            sims.Insert(0, centre);
        }
        Run(() => EnterSim(sims));
    }
    public void GoDesk() { Run(EnterDesk); }
    public void GoAll() { Run(() => { Displays.Extend(); return null; }); }

    string EnterSim(List<string> sims)
    {
        if (sims.Count == 0)
            return simMonitors.Count == 1 ? "Sim monitor '" + simMonitors[0] + "' is not connected." : "None of the sim monitors is connected.";
        if (!sims.TrueForAll(Displays.IsActive))
        {
            Displays.Extend();
            for (int i = 0; i < 10 && !sims.TrueForAll(Displays.IsActive); i++) Thread.Sleep(500);
        }
        return Displays.Keep(sims);
    }

    string EnterDesk()
    {
        var desk = DeskSet();
        if (desk.Count == 0) return "No desk monitors are connected.";
        if (!desk.TrueForAll(Displays.IsActive))
        {
            Displays.Extend();   // brings back the layout Windows saved with every monitor on
            for (int i = 0; i < 10 && !desk.TrueForAll(Displays.IsActive); i++) Thread.Sleep(500);
        }
        return Displays.Keep(desk);
    }

    async void Run(Func<string> change)
    {
        if (busy) return;
        busy = true;
        UpdateUi();
        try
        {
            string error = await Task.Run(change);
            if (error != null) tray.ShowBalloonTip(4000, "Sim Mode", error, ToolTipIcon.Warning);
        }
        finally
        {
            busy = false;
            UpdateUi();
            panel.Reposition();   // the screen it was on may have been turned off
        }
    }

    void UpdateUi()
    {
        Mode mode = CurrentMode();
        var mons = MonitorsInOrder();
        tray.Icon = busy ? iconBusy : mode == Mode.Sim ? iconSim : mode == Mode.Desk ? iconDesk : iconOther;
        tray.Text = "Sim Mode - " + (busy ? "Switching..." : ModeName(mode));
        miSim.Checked = mode == Mode.Sim;
        miDesk.Checked = mode == Mode.Desk;
        miAll.Checked = mode == Mode.All;
        miSim.Enabled = miDesk.Enabled = miAll.Enabled = !busy;
        panel.UpdateUi(mode, mons);
    }

    public static string ModeName(Mode mode)
    {
        return mode == Mode.Sim ? "Sim mode" : mode == Mode.Desk ? "Desk mode" : mode == Mode.All ? "All monitors" : "Custom layout";
    }

    static List<string> SplitList(string value)
    {
        return new List<string>(value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
    }

    static string LoadSetting(string name, string fallback)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
        {
            var v = k == null ? null : k.GetValue(name) as string;
            return v ?? fallback;
        }
    }

    static void SaveSetting(string name, string value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue(name, value);
    }

    void Quit()
    {
        poll.Stop();
        messages.Dispose();
        panel.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    static Icon MakeIcon(Color bg)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            using (var pen = new Pen(Color.White, 2.5f))
            using (var brush = new SolidBrush(bg))
            using (var path = Ui.Round(new RectangleF(0, 0, 31, 31), 7))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
                g.DrawRectangle(pen, 4, 8, 24, 12);          // wide screen
                g.DrawLine(pen, 16, 21, 16, 25);             // neck
                g.DrawLine(pen, 10, 25.5f, 22, 25.5f);       // base
            }
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}

// ---------------------------------------------------------------- look and feel

static class Ui
{
    public static readonly Color Bg = Color.FromArgb(28, 28, 30);
    public static readonly Color Card = Color.FromArgb(40, 40, 43);
    public static readonly Color CardHover = Color.FromArgb(52, 52, 56);
    public static readonly Color Line = Color.FromArgb(62, 62, 66);
    public static readonly Color Fg = Color.FromArgb(242, 242, 245);
    public static readonly Color Dim = Color.FromArgb(150, 150, 158);
    public static readonly Color Sim = Color.FromArgb(226, 54, 54);
    public static readonly Color Desk = Color.FromArgb(0, 120, 212);
    public static readonly Color Other = Color.FromArgb(110, 110, 118);
    public static readonly Color Busy = Color.FromArgb(232, 150, 30);

    public static float Scale = 1f;
    static string textFamily = "Segoe UI", displayFamily = "Segoe UI Semibold";

    public static void Init()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
        var installed = new HashSet<string>();
        using (var fonts = new InstalledFontCollection())
            foreach (var f in fonts.Families) installed.Add(f.Name);
        if (installed.Contains("Segoe UI Variable Text")) textFamily = "Segoe UI Variable Text";
        if (installed.Contains("Segoe UI Variable Display Semibold")) displayFamily = "Segoe UI Variable Display Semibold";
        else if (installed.Contains("Segoe UI Variable Display")) displayFamily = "Segoe UI Variable Display";
    }

    public static int S(float v) { return (int)Math.Round(v * Scale); }
    public static Font Text(float pt) { return new Font(textFamily, pt); }
    public static Font Strong(float pt) { return new Font(displayFamily, pt, displayFamily.EndsWith("Semibold") ? FontStyle.Regular : FontStyle.Bold); }

    public static Color Mix(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }
}

// Base for the owner-drawn controls: double-buffered, hover/press tracking.
class SmoothControl : Control
{
    protected bool Hover, Down;

    public SmoothControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = Down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
}

class RoundButton : SmoothControl
{
    public Color Fill = Ui.Card;
    public string Caption2;   // optional small line under the text

    public void SetFill(Color c) { if (c != Fill) { Fill = c; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        Color c = !Enabled ? Ui.Mix(Fill, BackColor, 0.55f) : Down ? Ui.Mix(Fill, Color.Black, 0.18f) : Hover ? Ui.Mix(Fill, Color.White, 0.10f) : Fill;
        using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(8)))
        using (var b = new SolidBrush(c))
            g.FillPath(b, path);
        Color fg = Enabled ? Ui.Fg : Ui.Mix(Ui.Fg, BackColor, 0.45f);
        if (string.IsNullOrEmpty(Caption2))
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else
        {
            int h = Height / 2;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, Ui.S(4), Width, h), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
            using (var small = Ui.Text(8f))
                TextRenderer.DrawText(g, Caption2, small, new Rectangle(0, h + Ui.S(2), Width, h - Ui.S(6)), Ui.Mix(fg, c, 0.25f), TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }
    }
}

// Pill that can be selected (single choice or multi choice).
class Chip : SmoothControl
{
    bool selected;
    public Color Accent = Ui.Desk;
    public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Invalidate(); } } }

    public Chip(string text, Font font)
    {
        Text = text;
        Font = font;
        Size = new Size(TextRenderer.MeasureText(text, font).Width + Ui.S(34), Ui.S(30));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        bool on = selected && Enabled;
        Color fill = !Enabled ? BackColor : on ? Ui.Mix(Accent, BackColor, 0.72f) : Hover ? Ui.CardHover : Ui.Card;
        Color border = on ? Accent : Ui.Line;
        using (var path = Ui.Round(r, Height / 2f))
        using (var b = new SolidBrush(fill))
        using (var p = new Pen(border, on ? 1.5f : 1f) { DashStyle = Enabled ? DashStyle.Solid : DashStyle.Dash })
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        int dotX = Ui.S(12), dotY = Height / 2;
        using (var b = new SolidBrush(on ? Accent : Ui.Line))
            g.FillEllipse(b, dotX - Ui.S(4), dotY - Ui.S(4), Ui.S(8), Ui.S(8));
        TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(22), 0, Width - Ui.S(26), Height),
            on ? Ui.Fg : Enabled ? Ui.Dim : Ui.Mix(Ui.Dim, BackColor, 0.45f), TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

// Windows 11 style settings row: text on the left, switch on the right.
class ToggleRow : SmoothControl
{
    bool on;
    public event EventHandler CheckedChanged;
    public bool Checked { get { return on; } set { if (on != value) { on = value; Invalidate(); } } }

    protected override void OnClick(EventArgs e)
    {
        on = !on;
        Invalidate();
        if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - Ui.S(56), Height), Hover ? Ui.Fg : Ui.Mix(Ui.Fg, Ui.Dim, 0.35f),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        int tw = Ui.S(40), th = Ui.S(20);
        var track = new RectangleF(Width - tw - 1, (Height - th) / 2f, tw, th);
        using (var path = Ui.Round(track, th / 2f))
        {
            if (on) using (var b = new SolidBrush(Hover ? Ui.Mix(Ui.Desk, Color.White, 0.1f) : Ui.Desk)) g.FillPath(b, path);
            else using (var p = new Pen(Ui.Dim, 1.2f)) g.DrawPath(p, path);
        }
        float k = on ? Ui.S(14) : Ui.S(10);
        float cx = on ? track.Right - th / 2f : track.X + th / 2f, cy = track.Y + th / 2f;
        using (var b = new SolidBrush(on ? Color.White : Ui.Dim))
            g.FillEllipse(b, cx - k / 2, cy - k / 2, k, k);
    }
}

// Live picture of the monitors: lit ones are on, outlined ones are off.
class MonitorPreview : SmoothControl
{
    List<Displays.MonitorInfo> mons = new List<Displays.MonitorInfo>();
    Predicate<string> isSim = n => false;
    Mode mode;
    bool busy;

    public MonitorPreview() { Cursor = Cursors.Default; }

    public void SetData(List<Displays.MonitorInfo> monitors, Predicate<string> simTest, Mode current, bool isBusy)
    {
        mons = monitors;
        isSim = simTest;
        mode = current;
        busy = isBusy;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(10)))
        using (var b = new SolidBrush(Ui.Card))
            g.FillPath(b, path);
        if (mons.Count == 0) return;

        // Scale resolutions into the box; screens sit on a common baseline like on a desk.
        float gap = Ui.S(10), pad = Ui.S(16), labelH = Ui.S(34);
        float totalW = 0, maxH = 0;
        foreach (var m in mons) { totalW += Math.Max(m.Width, 1); maxH = Math.Max(maxH, Math.Max(m.Height, 1)); }
        float availW = Width - pad * 2 - gap * (mons.Count - 1), availH = Height - pad * 2 - labelH;
        float scale = Math.Min(availW / totalW, availH / maxH);
        float x = (Width - (totalW * scale + gap * (mons.Count - 1))) / 2f;
        float baseline = (Height - (maxH * scale + labelH)) / 2f + maxH * scale;

        using (var name = Ui.Text(8f))
        using (var small = Ui.Text(7.5f))
        {
            foreach (var m in mons)
            {
                float w = Math.Max(m.Width, 1) * scale, h = Math.Max(m.Height, 1) * scale;
                var r = new RectangleF(x, baseline - h, w, h);
                bool sim = isSim(m.Name);
                Color accent = busy ? Ui.Busy : sim ? Ui.Sim : Ui.Desk;
                using (var path = Ui.Round(r, Ui.S(4)))
                {
                    if (m.Active)
                    {
                        using (var lg = new LinearGradientBrush(r, Ui.Mix(accent, Color.White, 0.18f), Ui.Mix(accent, Color.Black, 0.25f), 90f))
                            g.FillPath(lg, path);
                        using (var p = new Pen(Ui.Mix(accent, Color.White, 0.35f), 1f)) g.DrawPath(p, path);
                    }
                    else
                    {
                        using (var b = new SolidBrush(Ui.Mix(Ui.Card, Color.Black, 0.25f))) g.FillPath(b, path);
                        using (var p = new Pen(Ui.Line, 1.2f) { DashStyle = DashStyle.Dash }) g.DrawPath(p, path);
                    }
                }
                // stand
                using (var b = new SolidBrush(m.Active ? Ui.Mix(accent, Ui.Card, 0.45f) : Ui.Line))
                    g.FillRectangle(b, r.X + w / 2 - Ui.S(1.5f), r.Bottom + 1, Ui.S(3), Ui.S(5));

                string tag = sim ? "SIM" : m.Active ? "ON" : "OFF";
                var tagRect = Rectangle.Round(r);
                TextRenderer.DrawText(g, tag, small, tagRect, m.Active ? Color.White : Ui.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                var labelRect = new Rectangle((int)(r.X - gap / 2), (int)(baseline + Ui.S(8)), (int)(w + gap), Ui.S(16));
                TextRenderer.DrawText(g, m.Name, name, labelRect, m.Active ? Ui.Fg : Ui.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                labelRect.Y += Ui.S(15);
                TextRenderer.DrawText(g, m.Width + " \u00D7 " + m.Height, small, labelRect, Ui.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
                x += w + gap;
            }
        }
    }
}

// "Ctrl + Alt + S  to switch" drawn as key caps.
class KeyHint : SmoothControl
{
    public string[] Keys = { "Ctrl", "Alt", "S" };
    public string Suffix = "to switch";
    public bool Unavailable;

    public KeyHint() { Cursor = Cursors.Default; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        if (Unavailable)
        {
            TextRenderer.DrawText(g, "Ctrl+Alt+S is used by another app", Font, ClientRectangle, Ui.Busy, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            return;
        }
        var parts = new List<Size>();
        int total = 0;
        foreach (var k in Keys) { var s = TextRenderer.MeasureText(k, Font); parts.Add(s); total += s.Width + Ui.S(12) + Ui.S(6); }
        int plus = TextRenderer.MeasureText("+", Font).Width;
        int suffixW = TextRenderer.MeasureText(Suffix, Font).Width;
        total += plus * (Keys.Length - 1) + Ui.S(8) + suffixW;
        int x = (Width - total) / 2, h = Ui.S(22), y = (Height - h) / 2;
        for (int i = 0; i < Keys.Length; i++)
        {
            var cap = new RectangleF(x, y, parts[i].Width + Ui.S(12), h);
            using (var path = Ui.Round(cap, Ui.S(5)))
            using (var b = new SolidBrush(Ui.Card))
            using (var p = new Pen(Ui.Line))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Keys[i], Font, Rectangle.Round(cap), Ui.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            x += (int)cap.Width + Ui.S(3);
            if (i < Keys.Length - 1)
            {
                TextRenderer.DrawText(g, "+", Font, new Rectangle(x, 0, plus, Height), Ui.Dim, TextFormatFlags.VerticalCenter);
                x += plus + Ui.S(3);
            }
        }
        TextRenderer.DrawText(g, Suffix, Font, new Rectangle(x + Ui.S(8), 0, suffixW + 4, Height), Ui.Dim, TextFormatFlags.VerticalCenter);
    }
}

// ---------------------------------------------------------------- panel

// Dark flyout that opens above the tray, Windows 11 style (rounded corners, shadow).
class PanelForm : Form
{
    const int W = 360;
    readonly TrayApp app;
    readonly Label lblTitle, lblStatus, lblSimHeader, lblSimHint, lblDeskHeader, lblDeskHint;
    readonly Panel statusDot;
    readonly SmoothControl close;
    readonly MonitorPreview preview;
    readonly RoundButton btnMain, btnDesk, btnAll;
    readonly ToggleRow rowDeskAtStartup, rowStartup;
    readonly KeyHint keys;
    readonly List<Chip> simChips = new List<Chip>(), deskChips = new List<Chip>();
    readonly Font chipFont;
    DateTime hiddenAt = DateTime.MinValue;
    Mode mode = Mode.Other;
    bool updating;
    int chipsTop;

    public PanelForm(TrayApp app)
    {
        this.app = app;
        Text = "Sim Mode";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Ui.Bg;
        ForeColor = Ui.Fg;
        Font = Ui.Text(9.5f);
        chipFont = Ui.Text(9f);
        DoubleBuffered = true;
        ClientSize = new Size(Ui.S(W), Ui.S(600));

        lblTitle = new Label { Text = "Sim Mode", Font = Ui.Strong(15f), AutoSize = true, Location = new Point(Ui.S(18), Ui.S(14)), ForeColor = Ui.Fg };
        close = new CloseButton { Size = new Size(Ui.S(32), Ui.S(32)), Location = new Point(Ui.S(W - 44), Ui.S(12)), BackColor = Ui.Bg };
        close.Click += (s, e) => HidePanel();

        statusDot = new Panel { Size = new Size(Ui.S(10), Ui.S(10)), Location = new Point(Ui.S(21), Ui.S(56)), BackColor = Ui.Bg };
        statusDot.Paint += (s, e) =>
        {
            Ui.Smooth(e.Graphics);
            using (var b = new SolidBrush(StateColor())) e.Graphics.FillEllipse(b, 0, 0, statusDot.Width - 1, statusDot.Height - 1);
        };
        lblStatus = new Label { AutoSize = true, Font = Ui.Text(9.5f), ForeColor = Ui.Dim, Location = new Point(Ui.S(36), Ui.S(51)) };

        preview = new MonitorPreview { Location = new Point(Ui.S(18), Ui.S(82)), Size = new Size(Ui.S(W - 36), Ui.S(140)), BackColor = Ui.Bg };

        btnMain = new RoundButton { Font = Ui.Strong(12f), Location = new Point(Ui.S(18), Ui.S(236)), Size = new Size(Ui.S(W - 36), Ui.S(58)), BackColor = Ui.Bg };
        btnMain.Click += (s, e) => app.Toggle();
        btnDesk = new RoundButton { Text = "Desk mode", Font = Ui.Text(9.5f), Location = new Point(Ui.S(18), Ui.S(304)), Size = new Size(Ui.S((W - 46) / 2), Ui.S(36)), BackColor = Ui.Bg };
        btnDesk.Click += (s, e) => app.GoDesk();
        btnAll = new RoundButton { Text = "All monitors", Font = Ui.Text(9.5f), Location = new Point(Ui.S(28 + (W - 46) / 2), Ui.S(304)), Size = new Size(Ui.S((W - 46) / 2), Ui.S(36)), BackColor = Ui.Bg };
        btnAll.Click += (s, e) => app.GoAll();

        lblSimHeader = SectionLabel("SIM MONITORS");
        lblSimHint = new Label { Text = "on in Sim mode", AutoSize = true, Font = Ui.Text(8f), ForeColor = Ui.Dim, BackColor = Ui.Bg };
        lblDeskHeader = SectionLabel("DESK MONITORS");
        lblDeskHint = new Label { Text = "on in Desk mode", AutoSize = true, Font = Ui.Text(8f), ForeColor = Ui.Dim, BackColor = Ui.Bg };
        chipsTop = Ui.S(360);

        rowDeskAtStartup = new ToggleRow { Text = "Desk mode when Windows starts", Font = Ui.Text(9.5f), Size = new Size(Ui.S(W - 36), Ui.S(36)), BackColor = Ui.Bg };
        rowDeskAtStartup.CheckedChanged += (s, e) => { if (!updating) app.DeskAtStartup = rowDeskAtStartup.Checked; };
        rowStartup = new ToggleRow { Text = "Start with Windows", Font = Ui.Text(9.5f), Size = new Size(Ui.S(W - 36), Ui.S(36)), BackColor = Ui.Bg };
        rowStartup.CheckedChanged += (s, e) => { if (!updating) app.StartWithWindows = rowStartup.Checked; };
        keys = new KeyHint { Font = Ui.Text(8.5f), Size = new Size(Ui.S(W - 36), Ui.S(30)), BackColor = Ui.Bg };

        Controls.AddRange(new Control[] { lblTitle, close, statusDot, lblStatus, preview, btnMain, btnDesk, btnAll,
            lblSimHeader, lblSimHint, lblDeskHeader, lblDeskHint, rowDeskAtStartup, rowStartup, keys });

        Deactivate += (s, e) => HidePanel();
        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HidePanel(); } };
        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Escape) HidePanel(); };
    }

    static Label SectionLabel(string text)
    {
        return new Label { Text = text, AutoSize = true, Font = Ui.Strong(8f), ForeColor = Ui.Dim, BackColor = Ui.Bg };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, dark = 1, border = ColorTranslator.ToWin32(Ui.Line);
        DwmSetWindowAttribute(Handle, 33, ref round, 4);    // DWMWA_WINDOW_CORNER_PREFERENCE = round (Windows 11)
        DwmSetWindowAttribute(Handle, 20, ref dark, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(Handle, 34, ref border, 4);   // DWMWA_BORDER_COLOR
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var p = new Pen(Ui.Line))
        {
            // thin separators above the settings and the key hint
            e.Graphics.DrawLine(p, Ui.S(18), rowDeskAtStartup.Top - Ui.S(10), ClientSize.Width - Ui.S(18), rowDeskAtStartup.Top - Ui.S(10));
            e.Graphics.DrawLine(p, Ui.S(18), keys.Top - Ui.S(6), ClientSize.Width - Ui.S(18), keys.Top - Ui.S(6));
        }
    }

    Color StateColor()
    {
        if (app.Busy) return Ui.Busy;
        return mode == Mode.Sim ? Ui.Sim : mode == Mode.Desk ? Ui.Desk : Ui.Other;
    }

    // Monitor chips: rebuilt when the panel opens or the sim monitors change.
    void Rebuild()
    {
        updating = true;
        SuspendLayout();
        foreach (var c in simChips) { Controls.Remove(c); c.Dispose(); }
        foreach (var c in deskChips) { Controls.Remove(c); c.Dispose(); }
        simChips.Clear();
        deskChips.Clear();

        var connected = new List<string>(Displays.Connected());
        foreach (var sim in app.SimMonitors)
            if (!connected.Exists(n => Displays.Matches(n, sim))) connected.Add(sim);   // remembered but unplugged
        var desk = app.DeskSet();

        int y = chipsTop;
        lblSimHeader.Location = new Point(Ui.S(18), y);
        lblSimHint.Location = new Point(lblSimHeader.Right + Ui.S(6), y + Ui.S(1));
        y += Ui.S(22);
        foreach (var n in connected)
        {
            var chip = new Chip(n, chipFont) { Accent = Ui.Sim, BackColor = Ui.Bg, Selected = app.IsSim(n) };
            chip.Click += (s, e) =>
            {
                var c = (Chip)s;
                if (c.Selected && simChips.FindAll(x => x.Selected).Count == 1) return;   // keep at least one sim monitor
                c.Selected = !c.Selected;
                var list = new List<string>();
                foreach (var x in simChips) if (x.Selected) list.Add(x.Text);
                app.SetSimMonitors(list);
                BeginInvoke((Action)Rebuild);   // the desk chips change; not disposed from inside this chip's click
            };
            simChips.Add(chip);
        }
        y = FlowChips(simChips, y) + Ui.S(16);

        lblDeskHeader.Location = new Point(Ui.S(18), y);
        lblDeskHint.Location = new Point(lblDeskHeader.Right + Ui.S(6), y + Ui.S(1));
        y += Ui.S(22);
        foreach (var n in connected)
        {
            // Sim monitors stay in the list, greyed out, so picking them does not reflow the panel.
            bool isSim = app.IsSim(n);
            var chip = new Chip(n, chipFont) { Accent = Ui.Desk, BackColor = Ui.Bg, Selected = !isSim && desk.Contains(n), Enabled = !isSim };
            if (isSim) chip.Cursor = Cursors.Default;
            chip.Click += (s, e) =>
            {
                var c = (Chip)s;
                int selected = deskChips.FindAll(x => x.Selected).Count;
                if (c.Selected && selected == 1) return;   // keep at least one desk monitor
                c.Selected = !c.Selected;
                var list = new List<string>();
                foreach (var x in deskChips) if (x.Selected) list.Add(x.Text);
                app.SetDeskMonitors(list);
            };
            deskChips.Add(chip);
        }
        y = FlowChips(deskChips, y) + Ui.S(22);

        rowDeskAtStartup.Location = new Point(Ui.S(18), y); y += Ui.S(38);
        rowStartup.Location = new Point(Ui.S(18), y); y += Ui.S(48);
        keys.Location = new Point(Ui.S(18), y); y += Ui.S(42);
        ClientSize = new Size(Ui.S(W), y);
        ResumeLayout();
        updating = false;
        Reposition();   // keep the bottom edge above the tray if a monitor was plugged in or out
        Invalidate();
    }

    int FlowChips(List<Chip> chips, int y)
    {
        int x = Ui.S(18), right = Ui.S(W - 18), rowH = Ui.S(30);
        foreach (var c in chips)
        {
            if (x + c.Width > right && x > Ui.S(18)) { x = Ui.S(18); y += rowH + Ui.S(8); }
            c.Location = new Point(x, y);
            Controls.Add(c);
            x += c.Width + Ui.S(8);
        }
        return y + rowH;
    }

    public void UpdateUi(Mode current, List<Displays.MonitorInfo> monitors)
    {
        mode = current;
        if (!Visible) return;
        updating = true;
        bool busy = app.Busy;
        lblStatus.Text = busy ? "Switching..." : TrayApp.ModeName(mode);
        lblStatus.ForeColor = busy ? Ui.Busy : mode == Mode.Other ? Ui.Dim : Ui.Fg;
        statusDot.Invalidate();
        preview.SetData(monitors, app.IsSim, mode, busy);

        bool sim = mode == Mode.Sim;
        btnMain.Text = busy ? "Switching..." : sim ? "Back to Desk" : "Start Sim mode";
        btnMain.Caption2 = busy ? null : sim ? "turn the desk monitors back on" : SimCaption(app.SimMonitors);
        btnMain.SetFill(busy ? Ui.Busy : sim ? Ui.Desk : Ui.Sim);
        btnMain.Invalidate();
        btnMain.Enabled = !busy;
        btnDesk.Enabled = !busy && mode != Mode.Desk;
        btnAll.Enabled = !busy && mode != Mode.All;

        rowDeskAtStartup.Checked = app.DeskAtStartup;
        rowStartup.Checked = app.StartWithWindows;
        keys.Unavailable = !app.HotkeyOk;
        keys.Invalidate();
        updating = false;
    }

    static string SimCaption(List<string> sims)
    {
        if (sims.Count == 1) return "only " + sims[0] + " stays on";
        if (sims.Count == 2) return "only " + sims[0] + " and " + sims[1] + " stay on";
        return "only the " + sims.Count + " sim monitors stay on";
    }

    public void Toggle()
    {
        if (Visible) HidePanel();
        else if ((DateTime.Now - hiddenAt).TotalMilliseconds > 300) ShowNearTray();   // the click that just closed it via Deactivate
    }

    public void ShowNearTray()
    {
        Rebuild();
        Reposition(true);
        Show();
        Activate();
        UpdateUi(app.CurrentMode(), app.MonitorsInOrder());
    }

    public void Reposition() { Reposition(false); }

    void Reposition(bool force)
    {
        if (!Visible && !force) return;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - Ui.S(12), wa.Bottom - Height - Ui.S(12));
    }

    void HidePanel()
    {
        if (!Visible) return;
        hiddenAt = DateTime.Now;
        Hide();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

class CloseButton : SmoothControl
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        if (Hover)
            using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(6)))
            using (var b = new SolidBrush(Ui.CardHover))
                g.FillPath(b, path);
        float c = Width / 2f, k = Ui.S(5);
        using (var p = new Pen(Hover ? Ui.Fg : Ui.Dim, Ui.S(1.4f)))
        {
            g.DrawLine(p, c - k, c - k, c + k, c + k);
            g.DrawLine(p, c - k, c + k, c + k, c - k);
        }
    }
}

// Hidden window for the global hotkey and the "open the panel" message from a second launch.
class MessageWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312, MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, ID = 1;
    readonly Action onHotkey, onShowPanel;
    public bool HotkeyRegistered { get; private set; }

    public MessageWindow(Action onHotkey, Action onShowPanel)
    {
        this.onHotkey = onHotkey;
        this.onShowPanel = onShowPanel;
        CreateHandle(new CreateParams());
        HotkeyRegistered = RegisterHotKey(Handle, ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (int)Keys.S);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == ID) onHotkey();
        else if (m.Msg == Program.ShowPanelMessage) onShowPanel();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (HotkeyRegistered) UnregisterHotKey(Handle, ID);
        DestroyHandle();
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

// Windows display topology via the CCD API (QueryDisplayConfig / SetDisplayConfig).
// Monitors are matched by EDID friendly name, so Windows renumbering displays does not matter.
// Nothing here is saved to Windows' display database: the layout Windows stored with every
// monitor on stays intact, and Extend() always brings it back.
static class Displays
{
    const int PathSize = 72, ModeSize = 64, TargetNameSize = 420;
    const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2;
    const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80, SDC_ALLOW_CHANGES = 0x400;
    const uint InvalidModeIdx = 0xFFFFFFFF;
    // DISPLAYCONFIG_PATH_INFO offsets
    const int SourceModeIdx = 12, TargetLuidAndId = 20, TargetModeIdx = 32, TargetAvailable = 60;
    // DISPLAYCONFIG_MODE_INFO: source mode desktop position
    const int SourcePosX = 28, SourcePosY = 32;

    public static bool Matches(string name, string wanted)
    {
        return name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Monitors that are on.
    public static string[] Active()
    {
        byte[] paths, modes; uint np, nm;
        if (Query(QDC_ONLY_ACTIVE_PATHS, out paths, out np, out modes, out nm) != 0) return new string[0];
        var names = new List<string>();
        for (int i = 0; i < np; i++) names.Add(TargetName(paths, i * PathSize));
        return names.ToArray();
    }

    // Monitors that are plugged in, on or off.
    public static string[] Connected()
    {
        byte[] paths, modes; uint np, nm;
        if (Query(QDC_ALL_PATHS, out paths, out np, out modes, out nm) != 0) return new string[0];
        var names = new List<string>();
        for (int i = 0; i < np; i++)
        {
            if (BitConverter.ToInt32(paths, i * PathSize + TargetAvailable) == 0) continue;
            string name = TargetName(paths, i * PathSize);
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
        return names.ToArray();
    }

    public static bool IsActive(string monitor)
    {
        return Array.Exists(Active(), n => Matches(n, monitor));
    }

    public class MonitorInfo
    {
        public string Name;
        public int Width, Height, X, Y;
        public bool Active;
    }

    // Every connected monitor with its resolution; desktop position for the ones that are on.
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        byte[] paths, modes; uint np, nm;
        if (Query(QDC_ONLY_ACTIVE_PATHS, out paths, out np, out modes, out nm) == 0)
        {
            for (int i = 0; i < np; i++)
            {
                uint idx = BitConverter.ToUInt32(paths, i * PathSize + SourceModeIdx);
                var m = new MonitorInfo { Name = TargetName(paths, i * PathSize), Active = true };
                if (idx != InvalidModeIdx)
                {
                    int off = (int)idx * ModeSize;
                    m.Width = BitConverter.ToInt32(modes, off + 16);
                    m.Height = BitConverter.ToInt32(modes, off + 20);
                    m.X = PosX(modes, off);
                    m.Y = PosY(modes, off);
                }
                list.Add(m);
            }
        }
        if (Query(QDC_ALL_PATHS, out paths, out np, out modes, out nm) == 0)
        {
            for (int i = 0; i < np; i++)
            {
                int off = i * PathSize;
                if (BitConverter.ToInt32(paths, off + TargetAvailable) == 0) continue;
                string name = TargetName(paths, off);
                if (name.Length == 0 || list.Exists(x => x.Name == name)) continue;
                var m = new MonitorInfo { Name = name };
                PreferredSize(paths, off, out m.Width, out m.Height);
                list.Add(m);
            }
        }
        return list;
    }

    static void PreferredSize(byte[] paths, int pathOffset, out int width, out int height)
    {
        // DISPLAYCONFIG_TARGET_PREFERRED_MODE: header {type=3, size, adapterId, id}, width, height, target mode
        byte[] packet = new byte[80];
        BitConverter.GetBytes(3).CopyTo(packet, 0);
        BitConverter.GetBytes(packet.Length).CopyTo(packet, 4);
        Array.Copy(paths, pathOffset + TargetLuidAndId, packet, 8, 12);
        bool ok = DisplayConfigGetDeviceInfo(packet) == 0;
        width = ok ? BitConverter.ToInt32(packet, 20) : 1920;
        height = ok ? BitConverter.ToInt32(packet, 24) : 1080;
    }

    public static bool SameSet(string[] active, List<string> wanted)
    {
        if (active.Length != wanted.Count) return false;
        foreach (var w in wanted) if (!Array.Exists(active, n => Matches(n, w))) return false;
        return true;
    }

    // Turns every monitor on, in the extended layout Windows saved for them.
    public static int Extend()
    {
        return SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
    }

    // Turns off every active monitor not in `keep`. The first monitor in `keep` becomes primary
    // unless the current primary stays on. Returns null on success, otherwise a message.
    public static string Keep(List<string> keep)
    {
        byte[] paths, modes; uint np, nm;
        if (Query(QDC_ONLY_ACTIVE_PATHS, out paths, out np, out modes, out nm) != 0) return "Could not read the display configuration.";

        var kept = new List<int>();
        foreach (var want in keep)
            for (int i = 0; i < np; i++)
                if (!kept.Contains(i) && Matches(TargetName(paths, i * PathSize), want)) { kept.Add(i); break; }
        if (kept.Count == 0) return "None of these monitors is on: " + string.Join(", ", keep.ToArray());
        if (kept.Count == np) return null;

        byte[] newPaths = new byte[kept.Count * PathSize];
        var newModes = new List<byte>();
        var remap = new Dictionary<uint, uint>();
        var sourceModes = new List<int>();
        for (int k = 0; k < kept.Count; k++)
        {
            Array.Copy(paths, kept[k] * PathSize, newPaths, k * PathSize, PathSize);
            foreach (int idxOff in new[] { SourceModeIdx, TargetModeIdx })
            {
                int at = k * PathSize + idxOff;
                uint idx = BitConverter.ToUInt32(newPaths, at);
                if (idx == InvalidModeIdx) continue;
                uint n;
                if (!remap.TryGetValue(idx, out n))
                {
                    n = (uint)(newModes.Count / ModeSize);
                    remap[idx] = n;
                    byte[] mode = new byte[ModeSize];
                    Array.Copy(modes, idx * ModeSize, mode, 0, ModeSize);
                    newModes.AddRange(mode);
                }
                if (idxOff == SourceModeIdx) sourceModes.Add((int)n * ModeSize);
                BitConverter.GetBytes(n).CopyTo(newPaths, at);
            }
        }

        byte[] modeBytes = newModes.ToArray();
        if (sourceModes.Count > 0 && !sourceModes.Exists(m => PosX(modeBytes, m) == 0 && PosY(modeBytes, m) == 0))
        {
            // The primary monitor was turned off: move the desktops so the first kept one starts at 0,0.
            int dx = PosX(modeBytes, sourceModes[0]), dy = PosY(modeBytes, sourceModes[0]);
            foreach (int m in sourceModes)
            {
                BitConverter.GetBytes(PosX(modeBytes, m) - dx).CopyTo(modeBytes, m + SourcePosX);
                BitConverter.GetBytes(PosY(modeBytes, m) - dy).CopyTo(modeBytes, m + SourcePosY);
            }
        }

        int r = SetDisplayConfig((uint)kept.Count, newPaths, (uint)(modeBytes.Length / ModeSize), modeBytes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
        return r == 0 ? null : "Windows refused the display change (error " + r + ").";
    }

    static int PosX(byte[] modes, int m) { return BitConverter.ToInt32(modes, m + SourcePosX); }
    static int PosY(byte[] modes, int m) { return BitConverter.ToInt32(modes, m + SourcePosY); }

    static int Query(uint flags, out byte[] paths, out uint np, out byte[] modes, out uint nm)
    {
        paths = modes = null;
        int r = GetDisplayConfigBufferSizes(flags, out np, out nm);
        if (r != 0) return r;
        paths = new byte[np * PathSize];
        modes = new byte[nm * ModeSize];
        return QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
    }

    static string TargetName(byte[] paths, int pathOffset)
    {
        // DISPLAYCONFIG_TARGET_DEVICE_NAME: header {type=2, size, adapterId, id}, then the friendly name at offset 36
        byte[] packet = new byte[TargetNameSize];
        BitConverter.GetBytes(2).CopyTo(packet, 0);
        BitConverter.GetBytes(TargetNameSize).CopyTo(packet, 4);
        Array.Copy(paths, pathOffset + TargetLuidAndId, packet, 8, 12);
        if (DisplayConfigGetDeviceInfo(packet) != 0) return "";
        return Encoding.Unicode.GetString(packet, 36, 128).Split('\0')[0];
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, byte[] paths, ref uint numModes, byte[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, byte[] paths, uint numModes, byte[] modes, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(byte[] packet);
}
