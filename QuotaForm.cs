// The widget window: a per-pixel-alpha layered window drawn with GDI+. Data refreshes every few minutes in the background;
// a light animation timer eases the bars and runs the shimmer. Everything the owner can change is in the right-click menu
// (and the tray icon, which still works when click-through is on) and is saved in %APPDATA%\QuotaWidget\settings.txt.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuotaWidget;

sealed class QuotaForm : Form
{
    static readonly (string name, Color a1, Color a2)[] Themes =
    {
        ("Neon Cyan", Color.FromArgb(34, 211, 238), Color.FromArgb(167, 139, 250)),
        ("Synthwave", Color.FromArgb(244, 114, 182), Color.FromArgb(129, 140, 248)),
        ("Matrix Green", Color.FromArgb(74, 222, 128), Color.FromArgb(190, 242, 100)),
        ("Solar Amber", Color.FromArgb(251, 191, 36), Color.FromArgb(251, 146, 60)),
        ("Arctic Ice", Color.FromArgb(125, 211, 252), Color.FromArgb(199, 210, 254)),
        ("Crimson", Color.FromArgb(248, 113, 113), Color.FromArgb(253, 164, 175)),
        ("Toxic Lime", Color.FromArgb(163, 230, 53), Color.FromArgb(45, 212, 191)),
        ("Royal Violet", Color.FromArgb(192, 132, 252), Color.FromArgb(244, 114, 182)),
    };
    static readonly string[] Backgrounds = { "Glass", "Glass (tinted)", "Solid", "None (floating)" };
    static readonly Color Amber = Color.FromArgb(251, 191, 36), Red = Color.FromArgb(248, 113, 113), Green = Color.FromArgb(74, 222, 128);

    // ---- settings ----
    int theme, bg, panelAlpha = 215, refreshMin = 5;
    byte opacity = 255;
    float size = 1f;
    bool light, effects = true, showInactive, showClaude = true, corners = true, clickThrough, pinDesktop;
    int visibleRows = 5;
    // Lite = clean list (best-account star, sorting, folding bonus check); Full adds alerts, compact rows and usage history.
    // The zip's edition.txt only picks the first-run default; the menu switches any time.
    bool full, bonusOpen, notify = true, compact, spark = true;
    int sortMode;                                           // 0 last used · 1 soonest reset · 2 most left
    static readonly string[] Sorts = { "Last used", "Soonest reset", "Most left" };
    bool Notify => full && notify; bool Compact => full && compact; bool Spark => full && spark;

    Color A1 => Themes[theme].a1;
    Color A2 => Themes[theme].a2;
    Color Ink => light ? Color.FromArgb(17, 24, 39) : Color.FromArgb(232, 246, 255);
    Color Dim => light ? Color.FromArgb(90, 104, 122) : Color.FromArgb(125, 150, 172);
    Color AccentText(Color c) => light ? Blend(c, Color.Black, 0.62f) : c;

    // ---- state ----
    Snapshot snap; bool fetching; DateTime nextFetch = DateTime.MinValue;
    readonly Dictionary<string, float> shown = new();       // eased bar values
    float phase, appear;                                    // shimmer phase, fade-in 0..1
    readonly System.Windows.Forms.Timer tick = new() { Interval = 1000 }, anim = new() { Interval = 33 };
    readonly NotifyIcon tray = new();
    readonly Font fTitle = new("Bahnschrift SemiCondensed", 8.5f), fName = new("Bahnschrift SemiBold", 8.5f), fSmall = new("Bahnschrift SemiCondensed", 7.5f), fNum = new("Bahnschrift", 9f, FontStyle.Bold), fBig = new("Bahnschrift", 11f, FontStyle.Bold);
    float scale = 1f; bool resizing; Point resizeStart; float resizeSize0;
    readonly string cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuotaWidget", "settings.txt");

    const int W = 430, SubH = 24, HeadH = 20;
    int RowH => Compact ? 22 : 40;
    sealed record Row(string Kind, Account A, string Title);          // Kind: head | quota | claude
    List<Row> rows = new();
    float scrollY, scrollTarget;
    int ListH => visibleRows * RowH + HeadH;
    int CheckH => bonusOpen ? 62 : 20;
    int H => 32 + ListH + CheckH + 24;
    float BonusY => 30 + ListH + 4;

    static bool Active(Account a) => a.Auth && a.Win.Count > 0;
    static double MostUsed(Account a) => a.Win.Count == 0 ? 0 : a.Win.Max(v => Eff(v).used);
    static double NextReset(Account a) => a.Win.Where(v => v.reset > Now).Select(v => v.reset).DefaultIfEmpty(double.MaxValue).Min();
    Account best;                                           // the account with the most quota left right now (★ USE)
    void BuildRows()
    {
        var acc = snap?.Accounts ?? new();
        var r = new List<Row>();
        var off = acc.Where(a => a.Kind == "codex" && Active(a)).OrderByDescending(a => a.At).ToList();
        foreach (var a in acc) { a.Twin = null; a.Older = false; }
        foreach (var grp in off.GroupBy(a => $"{a.Name}|{a.Plan}").Where(gr => gr.Select(a => a.Source).Distinct().Count() > 1))
        {
            var newest = grp.First();
            foreach (var a in grp.Skip(1)) { a.Twin = newest; a.Older = true; newest.Twin ??= a; }
        }
        var usable = off.Where(a => !a.Older && a.Blocked == null && MostUsed(a) < 100).ToList();
        best = usable.Count > 1 ? usable.OrderBy(MostUsed).ThenBy(NextReset).First() : null;
        if (sortMode == 1) off = off.OrderBy(NextReset).ToList();
        else if (sortMode == 2) off = off.OrderBy(a => a.Blocked != null).ThenBy(MostUsed).ToList();
        // keep a twin right under the newer row so the pair reads together
        off = off.Where(a => !a.Older).SelectMany(a => off.Where(o => o.Older && o.Twin == a).Prepend(a)).ToList();
        r.Add(new Row("head", null, "OFFICIAL  ·  CODEX / CHATGPT")); r.AddRange(off.Select(a => new Row("quota", a, null)));
        var xk = acc.Where(a => a.Kind == "xkiro").ToList();
        if (xk.Count > 0) { r.Add(new Row("head", null, "xKIRO  ·  PAID + FREE")); r.AddRange(xk.Select(a => new Row("quota", a, null))); }
        var cl = acc.Where(a => a.Kind == "claude" && (showInactive || a.Tok7d > 0)).ToList();
        if (showClaude && cl.Count > 0) { r.Add(new Row("head", null, "CLAUDE  ·  TOKENS 5h · 24h")); r.AddRange(cl.Select(a => new Row("claude", a, null))); }
        var rest = acc.Where(a => a.Kind == "codex" && !Active(a)).ToList();
        if (showInactive && rest.Count > 0) { r.Add(new Row("head", null, "INACTIVE / NO LOGIN")); r.AddRange(rest.Select(a => new Row("quota", a, null))); }
        rows = r;
    }
    float RowHeight(Row r) => r.Kind == "head" ? HeadH : r.Kind == "claude" ? SubH : RowH;
    float ContentH => rows.Sum(RowHeight);
    float MaxScroll => Math.Max(0, ContentH - ListH);

    public QuotaForm()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        LoadSettings(); ApplySize();
        if (Location == Point.Empty) { var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Right - Width - 24, wa.Top + 260); }
        var menu = BuildMenu(); ContextMenuStrip = menu;
        tray.Icon = SystemIcons.Information; tray.Text = "AI Quota Widget"; tray.ContextMenuStrip = BuildMenu(); tray.Visible = true;
        tray.DoubleClick += (_, _) => { SetClickThrough(false); Activate(); };

        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (InGrip(e.Location)) { resizing = true; resizeStart = Cursor.Position; resizeSize0 = size; Capture = true; return; }
            if (OnBonusHeader(e.Location)) { bonusOpen = !bonusOpen; ApplySize(); SaveSettings(); Render(); return; }
            ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); SaveSettings();
        };
        MouseMove += (_, e) =>
        {
            if (resizing) { size = Math.Clamp(resizeSize0 + (Cursor.Position.X - resizeStart.X) / (W * DeviceDpi / 96f), 0.6f, 2.2f); ApplySize(); Render(); return; }
            Cursor = InGrip(e.Location) ? Cursors.SizeNWSE : OnBonusHeader(e.Location) ? Cursors.Hand : Cursors.Default;
            HoverTip(e.Location);
        };
        MouseLeave += (_, _) => { tip.Hide(this); tipFor = null; };
        MouseUp += (_, _) => { if (resizing) { resizing = false; Capture = false; SaveSettings(); } };
        MouseWheel += (_, e) =>
        {
            if ((ModifierKeys & Keys.Control) != 0) { size = Math.Clamp(size + (e.Delta > 0 ? 0.05f : -0.05f), 0.6f, 2.2f); ApplySize(); SaveSettings(); Render(); return; }
            scrollTarget = Math.Clamp(scrollTarget - Math.Sign(e.Delta) * RowH, 0, MaxScroll); if (!anim.Enabled) anim.Start();
        };
        MouseDoubleClick += (_, _) => Refresh_();

        tick.Tick += (_, _) => { if (DateTime.Now >= nextFetch && !fetching) Refresh_(); if (DateTime.Now.Second % 30 == 0) CheckAlerts(); if (!anim.Enabled) Render(); };
        anim.Tick += (_, _) => { phase = (phase + 0.012f) % 1f; appear = Math.Min(1f, appear + 0.06f); bool moving = Step(); if (!effects && !moving && appear >= 1) anim.Stop(); Render(); };
        Shown += (_, _) => { SetClickThrough(clickThrough); ApplyPin(); Render(); tick.Start(); anim.Start(); Refresh_(); };
        FormClosed += (_, _) => { SaveSettings(); tip.Dispose(); tray.Visible = false; tray.Dispose(); tick.Dispose(); anim.Dispose(); };
    }

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80; return cp; } }

    bool InGrip(Point p) => p.X > Width - 16 * scale && p.Y > Height - 16 * scale;
    bool OnBonusHeader(Point p) { float y = p.Y / scale; return y >= BonusY - 2 && y <= BonusY + 14; }

    // compact rows keep one line each; hovering one shows every window, its countdown and the projection
    readonly ToolTip tip = new() { InitialDelay = 0, ShowAlways = true };
    Account tipFor;
    void HoverTip(Point p)
    {
        Account hit = null;
        if (Compact)
        {
            float y = p.Y / scale, ry = 32 - scrollY;
            if (y >= 30 && y <= 30 + ListH)
                foreach (var row in rows) { float rh = RowHeight(row); if (y >= ry && y < ry + rh) { if (row.Kind == "quota") hit = row.A; break; } ry += rh; }
        }
        if (hit == tipFor) return;
        tipFor = hit;
        if (hit == null) { tip.Hide(this); return; }
        var lines = new List<string> { $"{hit.Source} {hit.Name} {hit.Plan?.ToUpperInvariant()}" };
        foreach (var win in hit.Win)
        {
            var (used, wasReset) = Eff(win);
            var proj = Projection(hit, win);
            lines.Add($"{WinLabel(win.mins)}  {Math.Max(0, 100 - used):0}% left  ·  {(wasReset ? "reset ✓" : win.reset > 0 ? "resets in " + Span(win.reset - Now) : "")}{(proj != null ? "  ·  " + proj : "")}");
        }
        tip.Show(string.Join("\n", lines), this, p.X + 14, p.Y + 14);
    }
    void ApplySize() { scale = DeviceDpi / 96f * size; Size = new Size((int)(W * scale), (int)(H * scale)); }

    void Refresh_()
    {
        if (fetching) return;
        fetching = true; if (!anim.Enabled) anim.Start();
        Task.Run(Data.Fetch).ContinueWith(t =>
        {
            try { BeginInvoke(() => { if (t.Exception == null) { snap = t.Result; CheckBonus(); RecordUsage(); CheckAlerts(); BuildRows(); scrollTarget = Math.Min(scrollTarget, MaxScroll); } else Program.Log(t.Exception); fetching = false; nextFetch = DateTime.Now.AddMinutes(refreshMin); ApplySize(); if (!anim.Enabled) anim.Start(); Render(); }); }
            catch { }
        });
    }

    void SetClickThrough(bool on)
    {
        clickThrough = on;
        int ex = GetWindowLong(Handle, -20);
        SetWindowLong(Handle, -20, on ? ex | 0x20 : ex & ~0x20);
        if (on) TopMost = true;
        SaveSettings();
    }

    // ---------------- menu ----------------
    readonly List<(ToolStripMenuItem item, Func<bool> on)> checks = new();
    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false, Renderer = new ToolStripProfessionalRenderer(new MenuColors()), ForeColor = Color.FromArgb(232, 246, 255) };
        var mTheme = new ToolStripMenuItem("Colour theme");
        for (int i = 0; i < Themes.Length; i++) { int k = i; mTheme.DropDownItems.Add(Radio(Themes[i].name, () => theme == k, () => theme = k)); }
        var mMode = new ToolStripMenuItem("Dark / light");
        mMode.DropDownItems.Add(Radio("Dark", () => !light, () => light = false));
        mMode.DropDownItems.Add(Radio("Light", () => light, () => light = true));
        var mBg = new ToolStripMenuItem("Background");
        for (int i = 0; i < Backgrounds.Length; i++) { int k = i; mBg.DropDownItems.Add(Radio(Backgrounds[i], () => bg == k, () => bg = k)); }
        mBg.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (label, a) in new[] { ("Panel 100%", 255), ("Panel 85%", 217), ("Panel 70%", 178), ("Panel 50%", 128), ("Panel 30%", 77), ("Panel 15%", 38) })
            mBg.DropDownItems.Add(Radio(label, () => panelAlpha == a, () => panelAlpha = a));
        mBg.DropDownItems.Add(new ToolStripSeparator());
        mBg.DropDownItems.Add(Check("HUD corner brackets", () => corners, v => corners = v));
        var mOp = new ToolStripMenuItem("Whole widget opacity");
        foreach (var (label, a) in new[] { ("100%", (byte)255), ("85%", (byte)217), ("70%", (byte)178), ("55%", (byte)140), ("40%", (byte)102), ("25%", (byte)64) })
            mOp.DropDownItems.Add(Radio(label, () => opacity == a, () => opacity = a));
        var mSize = new ToolStripMenuItem("Size  (or Ctrl+wheel / drag corner)");
        foreach (var (label, f) in new[] { ("70%", 0.7f), ("85%", 0.85f), ("100%", 1f), ("120%", 1.2f), ("150%", 1.5f), ("180%", 1.8f) })
            mSize.DropDownItems.Add(Radio(label, () => Math.Abs(size - f) < 0.01f, () => { size = f; ApplySize(); }));
        var mRef = new ToolStripMenuItem("Refresh every");
        foreach (var n in new[] { 2, 5, 10, 30 }) { int k = n; mRef.DropDownItems.Add(Radio($"{n} min", () => refreshMin == k, () => { refreshMin = k; nextFetch = DateTime.Now.AddMinutes(k); })); }
        var mShow = new ToolStripMenuItem("Show");
        mShow.DropDownItems.Add(Check("Claude token use", () => showClaude, v => { showClaude = v; BuildRows(); }));
        mShow.DropDownItems.Add(Check("Inactive / no-login accounts", () => showInactive, v => { showInactive = v; BuildRows(); }));
        mShow.DropDownItems.Add(Check("Animations", () => effects, v => { effects = v; if (v) anim.Start(); }));
        mShow.DropDownItems.Add(Check("Bonus check expanded  (or click its header)", () => bonusOpen, v => { bonusOpen = v; ApplySize(); }));
        var mSort = new ToolStripMenuItem("Sort accounts by");
        for (int i = 0; i < Sorts.Length; i++) { int k = i; mSort.DropDownItems.Add(Radio(Sorts[i], () => sortMode == k, () => { sortMode = k; BuildRows(); })); }
        var mEd = new ToolStripMenuItem("Edition");
        mEd.DropDownItems.Add(Radio("Lite  (clean list)", () => !full, () => { full = false; tipFor = null; tip.Hide(this); BuildRows(); scrollTarget = Math.Min(scrollTarget, MaxScroll); ApplySize(); }));
        mEd.DropDownItems.Add(Radio("Full  (alerts, compact, history)", () => full, () => { full = true; BuildRows(); ApplySize(); }));
        mEd.DropDownItems.Add(new ToolStripSeparator());
        var fullOnly = new[] {
            Check("Windows alerts (reset / 90% / bonus)", () => notify, v => notify = v),
            Check("Compact rows (hover for details)", () => compact, v => { compact = v; scrollTarget = 0; ApplySize(); }),
            Check("Usage history + run-out estimate", () => spark, v => spark = v) };
        foreach (var it in fullOnly) mEd.DropDownItems.Add(it);
        mEd.DropDownOpening += (_, _) => { foreach (var it in fullOnly) it.Enabled = full; };

        var miTop = Check("Always on top", () => TopMost && !pinDesktop, v => { pinDesktop = false; ApplyPin(); TopMost = v; });
        var miPin = Check("Pin to desktop (like Rainmeter)", () => pinDesktop, v => { pinDesktop = v; ApplyPin(); });
        var mRows = new ToolStripMenuItem("Rows visible");
        foreach (var n in new[] { 3, 5, 7, 10 }) { int k = n; mRows.DropDownItems.Add(Radio($"{n} rows", () => visibleRows == k, () => { visibleRows = k; scrollTarget = Math.Min(scrollTarget, MaxScroll); ApplySize(); })); }
        var miThrough = Check("Click-through overlay (undo from tray icon)", () => clickThrough, v => SetClickThrough(v));
        var miStart = new ToolStripMenuItem("Start with Windows");
        miStart.Click += (_, _) => { ToggleStartup(); RefreshChecks(); };
        checks.Add((miStart, IsStartup));
        var miNow = new ToolStripMenuItem("Refresh now  (double-click)", null, (_, _) => Refresh_());

        m.Opening += (_, _) => RefreshChecks();
        foreach (var sub in new[] { mTheme, mMode, mBg, mOp, mSize, mRows, mRef, mShow, mSort, mEd }) sub.DropDownOpening += (_, _) => RefreshChecks();
        m.Items.AddRange(new ToolStripItem[] { miNow, new ToolStripSeparator(), mEd, mSort, new ToolStripSeparator(), mTheme, mMode, mBg, mOp, mSize, mRows, mRef, mShow, new ToolStripSeparator(), miPin, miTop, miThrough, miStart,
            new ToolStripSeparator(), new ToolStripMenuItem("Exit", null, (_, _) => Close()) });
        return m;
    }

    ToolStripMenuItem Radio(string text, Func<bool> isOn, Action set)
    {
        var it = new ToolStripMenuItem(text);
        it.Click += (_, _) => { set(); SaveSettings(); Render(); RefreshChecks(); };
        checks.Add((it, isOn)); return it;
    }
    ToolStripMenuItem Check(string text, Func<bool> isOn, Action<bool> set)
    {
        var it = new ToolStripMenuItem(text);
        it.Click += (_, _) => { set(!isOn()); SaveSettings(); Render(); RefreshChecks(); };
        checks.Add((it, isOn)); return it;
    }
    void RefreshChecks() { foreach (var (it, on) in checks) it.Checked = on(); }

    sealed class MenuColors : ProfessionalColorTable
    {
        static readonly Color bgc = Color.FromArgb(14, 20, 32), sel = Color.FromArgb(28, 52, 72), line = Color.FromArgb(40, 70, 95);
        public override Color ToolStripDropDownBackground => bgc;
        public override Color ImageMarginGradientBegin => bgc; public override Color ImageMarginGradientMiddle => bgc; public override Color ImageMarginGradientEnd => bgc;
        public override Color MenuItemSelected => sel; public override Color MenuItemBorder => line; public override Color MenuBorder => line;
        public override Color MenuItemSelectedGradientBegin => sel; public override Color MenuItemSelectedGradientEnd => sel;
        public override Color SeparatorDark => line; public override Color SeparatorLight => bgc;
        public override Color CheckBackground => sel; public override Color CheckSelectedBackground => sel; public override Color CheckPressedBackground => sel;
    }

    // ---------------- values ----------------
    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    static (double used, bool reset) Eff(WinInfo w) => w.reset > 0 && w.reset < Now ? (0, true) : (w.used, false);
    static string WinLabel(int mins) => mins switch { 300 => "5H", 10080 => "7D", 43200 => "30D", _ => mins >= 1440 ? $"{mins / 1440}D" : $"{mins / 60}H" };
    static string Span(double sec)
    {
        if (sec <= 0) return "now";
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours:00}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }
    static string Tok(double n) => n >= 1e9 ? $"{n / 1e9:0.00}B" : n >= 1e6 ? $"{n / 1e6:0.00}M" : n >= 1e3 ? $"{n / 1e3:0.0}k" : $"{n:0}";
    Color Level(double pct) => pct >= 90 ? Red : pct >= 70 ? Amber : A1;

    // ease every bar toward its target; true while something is still moving
    bool Step()
    {
        bool moving = false;
        var acc = snap?.Accounts ?? new();
        foreach (var a in acc.Where(a => a.Kind != "claude"))
            for (int i = 0; i < a.Win.Count; i++) moving |= Ease($"{a.Source}{a.Home}{i}", (float)Eff(a.Win[i]).used);
        var cl = acc.Where(a => a.Kind == "claude").ToList();
        double max = Math.Max(1, cl.Select(c => c.Tok24h).DefaultIfEmpty(0).Max());
        foreach (var c in cl) moving |= Ease($"{c.Source}{c.Home}c", (float)(100 * c.Tok24h / max));
        float ns = scrollY + (scrollTarget - scrollY) * 0.25f; if (Math.Abs(scrollTarget - ns) < 0.5f) ns = scrollTarget; else moving = true; scrollY = ns;
        return moving;
    }
    bool Ease(string k, float target)
    {
        shown.TryGetValue(k, out var v);
        float n = v + (target - v) * 0.16f; if (Math.Abs(target - n) < 0.2f) n = target;
        shown[k] = n; return n != target;
    }
    float Val(string k) => shown.TryGetValue(k, out var v) ? v : 0;

    // ---------------- drawing ----------------
    void Render() { try { RenderFrame(); } catch (Exception ex) { Program.Log(ex); } }

    void RenderFrame()
    {
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(scale, scale);
            Draw(g);
        }
        Blit(bmp);
    }

    public static void Snapshot(string file, string opts)
    {
        using var f = new QuotaForm();
        foreach (var kv in opts.Split(';').Select(x => x.Split('=')).Where(x => x.Length == 2))
            switch (kv[0]) { case "theme": f.theme = int.Parse(kv[1]); break; case "light": f.light = kv[1] == "1"; break; case "bg": f.bg = int.Parse(kv[1]); break; case "alpha": f.panelAlpha = int.Parse(kv[1]); break; case "full": f.full = kv[1] == "1"; break; case "compact": f.compact = kv[1] == "1"; break; case "bonus": f.bonusOpen = kv[1] == "1"; break; case "sort": f.sortMode = int.Parse(kv[1]); break; }
        f.snap = Data.Fetch(); f.CheckBonus(); f.RecordUsage(); f.BuildRows(); f.appear = 1; f.ApplySize(); for (int i = 0; i < 200 && f.Step(); i++) { }
        using var bmp = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            using var desk = new LinearGradientBrush(new Rectangle(0, 0, f.Width, f.Height), Color.FromArgb(40, 60, 90), Color.FromArgb(90, 50, 80), 30f);
            g.FillRectangle(desk, 0, 0, f.Width, f.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.ScaleTransform(f.scale, f.scale);
            f.Draw(g);
        }
        bmp.Save(file, ImageFormat.Png);
        f.tray.Visible = false;
    }

    void Draw(Graphics g)
    {
        int w = W, h = H; bool floating = bg == 3;
        var r = new RectangleF(1, 1, w - 2, h - 2);
        using (var path = Round(r, 14))
        {
            Color baseTop = light ? Color.FromArgb(246, 248, 252) : Color.FromArgb(9, 14, 26), baseBot = light ? Color.FromArgb(228, 233, 242) : Color.FromArgb(5, 8, 16);
            if (bg == 0 || bg == 1)
            {
                var top = Color.FromArgb(panelAlpha, bg == 0 ? baseTop : Blend(A1, baseTop, light ? 0.10f : 0.16f));
                var bot = Color.FromArgb(Math.Max(0, panelAlpha - 20), bg == 0 ? baseBot : Blend(A2, baseBot, light ? 0.10f : 0.12f));
                using var fill = new LinearGradientBrush(r, top, bot, 90f); g.FillPath(fill, path);
            }
            else if (bg == 2) { using var fill = new SolidBrush(Color.FromArgb(panelAlpha, light ? Color.White : Color.Black)); g.FillPath(fill, path); }
            else { using var catchAll = new SolidBrush(Color.FromArgb(1, 0, 0, 0)); g.FillPath(catchAll, path); }
            if (bg != 3)
            {
                using var rim = new LinearGradientBrush(r, Color.FromArgb(150, A1), Color.FromArgb(120, A2), 0f); using var pen = new Pen(rim, 1f); g.DrawPath(pen, path);
                if (effects)   // a soft light running along the top edge
                {
                    float x = -80 + (w + 160) * phase;
                    using var sweep = new LinearGradientBrush(new RectangleF(x, 0, 80, 3), Color.FromArgb(0, A1), Color.FromArgb(0, A1), 0f) { InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, A1), Color.FromArgb(200, A1), Color.FromArgb(0, A1) }, Positions = new[] { 0f, 0.5f, 1f } } };
                    g.SetClip(path); g.FillRectangle(sweep, x, 1, 80, 2); g.ResetClip();
                }
            }
        }
        if (corners)
        {
            using var br = new Pen(Color.FromArgb(220, A1), 1.6f); float L = 12;
            g.DrawLines(br, new[] { new PointF(6, 6 + L), new PointF(6, 6), new PointF(6 + L, 6) });
            g.DrawLines(br, new[] { new PointF(w - 6 - L, h - 6), new PointF(w - 6, h - 6), new PointF(w - 6, h - 6 - L) });
        }

        using var ink = new SolidBrush(Color.FromArgb((int)(255 * Math.Max(0.15f, appear)), Ink)); using var dim = new SolidBrush(Dim); using var acc = new SolidBrush(AccentText(A1)); using var acc2 = new SolidBrush(AccentText(A2));
        Text(g, "AI QUOTA  //  CODEX · CLAUDE", fTitle, dim, 20, 10, floating);
        var clock = DateTime.Now.ToString("HH:mm:ss"); Text(g, clock, fTitle, acc, w - 20 - g.MeasureString(clock, fTitle).Width, 10, floating);

        // scrolling list of accounts (5 rows by default; wheel scrolls, Ctrl+wheel resizes)
        var list = new RectangleF(10, 30, w - 20, ListH);
        var st = g.Save(); g.SetClip(list); g.TranslateTransform(0, -scrollY);
        float y = 32;
        if (rows.Count == 0) Text(g, snap == null ? "loading…" : "no logs found", fSmall, dim, 24, y + 8, floating);
        foreach (var row in rows)
        {
            float rh = RowHeight(row);
            if (y + rh >= 30 + scrollY && y <= 30 + scrollY + ListH)
            {
                if (row.Kind == "head") SectionHeader(g, row.Title, y + 4, w, row.Title.StartsWith("CLAUDE") ? acc2 : acc, floating);
                else if (row.Kind == "claude") ClaudeRow(g, row.A, y + 2, w, ink, dim, floating);
                else if (Compact) CompactRow(g, row.A, y + 2, w, ink, dim, floating);
                else CodexRow(g, row.A, y + 2, w, ink, dim, floating);
            }
            y += rh;
        }
        g.Restore(st);
        if (MaxScroll > 0)
        {
            float th = Math.Max(16, ListH * ListH / ContentH), ty = list.Top + (ListH - th) * (scrollY / MaxScroll);
            using (var tr = new SolidBrush(Color.FromArgb(35, A1))) g.FillRectangle(tr, w - 9, list.Top, 2, ListH);
            using (var tb = new SolidBrush(Color.FromArgb(200, A1))) g.FillRectangle(tb, w - 9.5f, ty, 3, th);
        }

        // bonus-reset checklist: folds to one summary line (click the header); a new early reset unfolds it
        float cy = BonusY;
        bool anyHit = bonusSeen.Count > 0;
        if (bonusOpen)
        {
            SectionHeader(g, "▾ BONUS / EARLY RESET CHECK", cy, w, acc, floating); cy += 16;
            CheckLine(g, "OpenAI", BonusText("codex"), BonusHit("codex"), cy, floating); cy += 14;
            CheckLine(g, "xKiro", BonusText("xkiro"), BonusHit("xkiro"), cy, floating); cy += 14;
            CheckLine(g, "Anthropic", "limit % not in logs · check claude.ai usage page by hand", false, cy, floating, true);
        }
        else
        {
            var sum = anyHit ? "▸ " + BonusText(BonusHit("codex") ? "codex" : "xkiro") : "▸ BONUS CHECK  ·  " + BonusText("codex");
            using var sb = new SolidBrush(AccentText(anyHit ? Green : A1)); Text(g, sum, fSmall, sb, 20, cy, floating);
        }

        // footer: source status, last update, countdown, live pulse while fetching
        float fy = h - 20;
        float pulse = fetching ? 0.5f + 0.5f * (float)Math.Sin(phase * Math.PI * 16) : 1f;
        using (var dot = new SolidBrush(Color.FromArgb((int)(255 * pulse), fetching ? Amber : (snap?.VpsOk ?? false) ? Green : Red))) g.FillEllipse(dot, 20, fy + 3, 6, 6);
        string lap = snap == null ? "…" : snap.LapOk ? "✓" : "✗", vps = snap == null ? "…" : snap.VpsOk ? "✓" : "✗ " + snap.VpsErr;
        Text(g, $"LAP {lap}   VPS {vps}", fSmall, dim, 30, fy, floating);
        var upd = fetching ? "updating…" : snap == null ? "" : $"upd {snap.Time:HH:mm} · next {Span((nextFetch - DateTime.Now).TotalSeconds)} · no quota used";
        Text(g, upd, fSmall, dim, w - 20 - g.MeasureString(upd, fSmall).Width, fy, floating);
        using (var grip = new Pen(Color.FromArgb(110, A1), 1f)) { g.DrawLine(grip, w - 5, h - 12, w - 12, h - 5); g.DrawLine(grip, w - 5, h - 8, w - 8, h - 5); }
    }

    void SectionHeader(Graphics g, string t, float y, int w, Brush b, bool sh)
    {
        Text(g, t, fSmall, b, 20, y, sh);
        float x0 = 24 + g.MeasureString(t, fSmall).Width;
        using var ln = new LinearGradientBrush(new RectangleF(x0, y + 7, w - 20 - x0 + 1, 1), Color.FromArgb(120, A1), Color.FromArgb(0, A1), 0f);
        g.FillRectangle(ln, x0, y + 7, w - 20 - x0, 1);
    }

    void Chip(Graphics g, string t, float x, float y, Color c, bool sh, out float width)
    {
        var sz = g.MeasureString(t, fSmall); width = sz.Width + 6;
        using var path = Round(new RectangleF(x, y + 1, width, 12), 4);
        using var fill = new SolidBrush(Color.FromArgb(light ? 40 : 50, c)); g.FillPath(fill, path);
        using var b = new SolidBrush(AccentText(c)); Text(g, t, fSmall, b, x + 3, y, false);
    }

    void CheckLine(Graphics g, string who, string text, bool hit, float y, bool sh, bool manual = false)
    {
        var box = manual ? "☐" : hit ? "☑" : "·";
        using var b = new SolidBrush(AccentText(manual ? Amber : hit ? Green : Dim));
        Text(g, box, fSmall, b, 22, y, sh);
        using var d = new SolidBrush(Dim); Text(g, who, fSmall, d, 34, y, sh);
        Text(g, text, fSmall, b, 96, y, sh);
    }

    void CodexRow(Graphics g, Account a, float y, int w, Brush ink, Brush dim, bool sh)
    {
        float x = 20;
        Chip(g, a.Source, x, y, a.Source == "VPS" ? A2 : A1, sh, out var cw); x += cw + 5;
        if (a.Older) ink = dim;
        Text(g, a.Name, fName, ink, x, y - 1, sh); x += g.MeasureString(a.Name, fName).Width + 2;
        if (!string.IsNullOrEmpty(a.Plan)) { Chip(g, a.Plan.ToUpperInvariant(), x, y, Dim, sh, out var pw); x += pw + 5; }
        if (a.Twin != null) { Chip(g, "= " + a.Twin.Source, x, y, Dim, sh, out var tw0); x += tw0 + 5; }
        if (a == best) { Chip(g, "★ USE", x, y, Green, sh, out var uw); x += uw + 5; }
        // plans that only report a long window get a small tag instead of an empty "5H" slot
        bool no5h = a.Kind == "codex" && a.Win.Count > 0 && !a.Win.Any(v => v.mins == 300) && a.Plan != "free";
        if (no5h) { Chip(g, "no 5H", x, y, Dim, sh, out var nw); x += nw + 5; }
        float chipsEnd = x;

        double age = a.At > 0 ? Now - a.At : double.MaxValue;
        string note; Color nc;
        if (a.Kind == "xkiro") { note = a.Err != null ? "offline " + a.Err : $"free {Tok(a.FreeUsed ?? 0)}/{Tok(a.FreeLimit ?? 0)} · wallet ${a.Wallet ?? 0:0.00}"; nc = a.Err != null ? Red : Green; }
        else if (!a.Auth) { note = "NO LOGIN"; nc = Dim; }
        else if (a.Older) { note = $"older log by {Span(a.Twin.At - a.At)}"; nc = Dim; }
        else if (bonusSeen.ContainsKey(a.Source + a.Home)) { note = "BONUS RESET ✓"; nc = Green; }
        else if (a.Blocked == "workspace_owner_credits_depleted") { note = "CREDITS 0"; nc = Red; }
        else if (a.Win.Any(v => Eff(v).reset) && a.Win.All(v => Eff(v).used < 100)) { note = "RESET ✓ READY"; nc = Green; }
        else if (age > 86400) { note = $"stale {Span(age).Split(' ')[0]}"; nc = Dim; }
        else if (Spark && a.Win.Select(v => Projection(a, v)).FirstOrDefault(s => s != null) is string pj) { note = pj; nc = Amber; }
        else { note = a.Tok24h > 0 ? $"24h {Tok(a.Tok24h)} tok" : "live"; nc = A1; }
        float noteW = g.MeasureString(note, fSmall).Width;
        using (var nb = new SolidBrush(AccentText(nc))) Text(g, note, fSmall, nb, w - 20 - noteW, y, sh);
        if (Spark && a.Kind == "codex" && !a.Older) { float sw = 46, sx = w - 26 - noteW - sw; if (sx > chipsEnd + 4) Sparkline(g, a, sx, y + 1, sw, 11); }

        var wins = a.Win.Select((v, i) => (v, i)).ToList();
        int slots = wins.Count, s0 = 0;
        float by = y + 18, gap = 10, span = w - 40, bw = slots == 0 ? span : (span - gap * (slots - 1)) / slots;
        if (slots == 0) { Text(g, "no rate-limit data in logs", fSmall, dim, 20, by - 3, sh); return; }
        foreach (var (win, i) in wins)
        {
            var (used, wasReset) = Eff(win);
            float bx = 20 + (i + s0) * (bw + gap), v = Val($"{a.Source}{a.Home}{i}");
            Text(g, WinLabel(win.mins), fSmall, dim, bx, by - 3, sh);
            float tx = bx + 24, tw = Math.Max(20, bw - 24 - 82);
            var c = Level(used);
            if (a.Older) c = Color.FromArgb(110, c);
            using (var track = new SolidBrush(Color.FromArgb(light ? 30 : 40, c))) g.FillRectangle(track, tx, by + 2, tw, 6);
            float fw = Math.Max(0.5f, tw * Math.Clamp(v, 0, 100) / 100f);
            using (var lb = new LinearGradientBrush(new RectangleF(tx, by + 2, tw + 1, 6), Blend(c, A2, 0.75f), c, 0f)) g.FillRectangle(lb, tx, by + 2, fw, 6);
            if (effects && fw > 6)
            {
                float sx = tx + (fw + 30) * ((phase * 2 + i * 0.37f) % 1f) - 30;
                using var shine = new LinearGradientBrush(new RectangleF(sx, by, 30, 10), Color.Transparent, Color.Transparent, 0f) { InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(light ? 90 : 120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) }, Positions = new[] { 0f, 0.5f, 1f } } };
                var st = g.Save(); g.SetClip(new RectangleF(tx, by + 2, fw, 6), CombineMode.Intersect); g.FillRectangle(shine, sx, by + 2, 30, 6); g.Restore(st);
                if (used >= 95) { using var glow = new Pen(Color.FromArgb((int)(60 + 60 * Math.Sin(phase * Math.PI * 8)), c), 3f); g.DrawRectangle(glow, tx - 1, by + 1, fw + 2, 8); }
            }
            // what is left (money for xKiro) on the line, reset countdown under it
            var left = win.cap > 0 ? $"${win.cap - win.spent:0.0} left" : $"{Math.Max(0, 100 - v):0}% left";
            using (var pb = new SolidBrush(AccentText(c))) Text(g, left, fName, pb, tx + tw + 3, by - 4, sh);
            var rs = wasReset ? "reset ✓" : win.reset > 0 ? "↻ " + Span(win.reset - Now) : "";
            Text(g, rs, fSmall, dim, tx + tw + 3, by + 8, sh);
        }
    }

    // one line per account: tightest window only (bar, % left, countdown); hover for the rest
    void CompactRow(Graphics g, Account a, float y, int w, Brush ink, Brush dim, bool sh)
    {
        float x = 20;
        Chip(g, a.Source, x, y, a.Source == "VPS" ? A2 : A1, sh, out var cw); x += cw + 5;
        if (a.Older) ink = dim;
        var name = a.Name.Length > 12 ? a.Name[..11] + "…" : a.Name;
        Text(g, name, fName, ink, x, y - 1, sh); x += g.MeasureString(name, fName).Width + 2;
        if (a == best) Chip(g, "★", x, y, Green, sh, out _);
        if (a.Win.Count == 0)
        {
            var s = a.Kind == "xkiro" ? (a.Err != null ? "offline" : $"wallet ${a.Wallet ?? 0:0.00}") : a.Auth ? "no data" : "NO LOGIN";
            Text(g, s, fSmall, dim, w - 20 - g.MeasureString(s, fSmall).Width, y, sh); return;
        }
        int i = Enumerable.Range(0, a.Win.Count).OrderByDescending(k => Eff(a.Win[k]).used).First();
        var win = a.Win[i]; var (used, wasReset) = Eff(win);
        float v = Val($"{a.Source}{a.Home}{i}"), tx = 170, tw = 90;
        var c = a.Blocked != null ? Red : Level(used); if (a.Older) c = Color.FromArgb(110, c);
        Text(g, WinLabel(win.mins), fSmall, dim, tx - 24, y, sh);
        using (var track = new SolidBrush(Color.FromArgb(light ? 30 : 40, c))) g.FillRectangle(track, tx, y + 5, tw, 5);
        using (var lb = new SolidBrush(c)) g.FillRectangle(lb, tx, y + 5, Math.Max(0.5f, tw * Math.Clamp(v, 0, 100) / 100f), 5);
        var left = a.Blocked != null ? "CREDITS 0" : win.cap > 0 ? $"${win.cap - win.spent:0.0}" : $"{Math.Max(0, 100 - v):0}%";
        using (var pb = new SolidBrush(AccentText(c))) Text(g, left, fName, pb, tx + tw + 6, y - 1, sh);
        var rs = wasReset ? "reset ✓" : win.reset > 0 ? "↻ " + Span(win.reset - Now) : "";
        if (Spark && Projection(a, win) != null) rs = "⚠ " + rs;
        Text(g, rs, fSmall, dim, w - 20 - g.MeasureString(rs, fSmall).Width, y, sh);
    }

    void ClaudeRow(Graphics g, Account c, float y, int w, Brush ink, Brush dim, bool sh)
    {
        float x = 20;
        Chip(g, c.Source, x, y, c.Source == "VPS" ? A2 : A1, sh, out var cw); x += cw + 5;
        Text(g, c.Name, fName, ink, x, y - 1, sh); x += g.MeasureString(c.Name, fName).Width + 4;
        var model = (c.Model ?? "").Replace("claude-", "");
        if (model.Length > 0) Text(g, model, fSmall, dim, x, y, sh);
        float tx = 230, tw = 70, v = Val($"{c.Source}{c.Home}c");
        using (var track = new SolidBrush(Color.FromArgb(light ? 30 : 40, A2))) g.FillRectangle(track, tx, y + 5, tw, 5);
        using (var lb = new LinearGradientBrush(new RectangleF(tx, y + 5, tw + 1, 5), A2, A1, 0f)) g.FillRectangle(lb, tx, y + 5, Math.Max(0.5f, tw * v / 100f), 5);
        var t = $"{Tok(c.Tok5h)} · {Tok(c.Tok24h)}";
        using var nb = new SolidBrush(AccentText(A2)); Text(g, t, fNum, nb, w - 20 - g.MeasureString(t, fNum).Width, y - 3, sh);
    }

    void Text(Graphics g, string t, Font f, Brush b, float x, float y, bool shadow)
    {
        if (shadow) { using var s = new SolidBrush(Color.FromArgb(170, light ? Color.White : Color.Black)); g.DrawString(t, f, s, x + 1, y + 1); }
        g.DrawString(t, f, b, x, y);
    }

    static Color Blend(Color a, Color b, float t) => Color.FromArgb((int)(b.R + (a.R - b.R) * t), (int)(b.G + (a.G - b.G) * t), (int)(b.B + (a.B - b.B) * t));
    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    void Blit(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hBmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hBmp);
        try
        {
            var sz = new SIZE { cx = bmp.Width, cy = bmp.Height }; var src = new POINT(); var pos = new POINT { x = Left, y = Top };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref pos, ref sz, memDc, ref src, 0, ref blend, 2);
        }
        finally { SelectObject(memDc, old); DeleteObject(hBmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc); }
    }

    // ---------------- bonus / early reset detection ----------------
    // Each refresh is compared with the last one: if a window's use drops, or its reset time moves earlier, while the old reset was
    // still in the future, that is an early (bonus) reset. Readings and hits are kept in %APPDATA%\QuotaWidget\history.txt (hits for 48 h).
    readonly Dictionary<string, (double used, double reset)> lastSeen = new();
    readonly Dictionary<string, (double at, string what)> bonusSeen = new();
    string HistPath => Path.Combine(Path.GetDirectoryName(cfgPath), "history.txt");
    DateTime lastCheck;

    void CheckBonus()
    {
        if (lastSeen.Count == 0) LoadHistory();
        foreach (var a in (snap?.Accounts ?? new()).Where(a => a.Kind != "claude"))
            foreach (var win in a.Win)
            {
                string k = $"{a.Source}{a.Home}|{win.mins}";
                if (lastSeen.TryGetValue(k, out var p) && p.reset > Now + 120 && (win.used < p.used - 3 || (win.reset > 0 && win.reset < p.reset - 900)))
                {
                    var what = $"{a.Name} {WinLabel(win.mins)} {p.used:0}%→{win.used:0}%";
                    if (!bonusSeen.ContainsKey(a.Source + a.Home)) { bonusOpen = true; Alert("Early / bonus reset", $"{a.Source} {what}"); }
                    bonusSeen[a.Source + a.Home] = (Now, what);
                }
                lastSeen[k] = (win.used, win.reset);
            }
        foreach (var k in bonusSeen.Where(kv => Now - kv.Value.at > 48 * 3600).Select(kv => kv.Key).ToList()) bonusSeen.Remove(k);
        lastCheck = DateTime.Now; SaveHistory();
    }
    bool IsKind(string key, string kind) => (kind == "xkiro") == key.Contains("xkiro");
    bool BonusHit(string kind) => bonusSeen.Any(kv => IsKind(kv.Key, kind));
    string BonusText(string kind)
    {
        var hits = bonusSeen.Where(kv => IsKind(kv.Key, kind)).ToList();
        if (hits.Count > 0) { var h = hits.OrderByDescending(x => x.Value.at).First().Value; return $"EARLY RESET {DateTimeOffset.FromUnixTimeSeconds((long)h.at).ToLocalTime():dd/MM HH:mm} · {h.what}"; }
        return lastCheck == default ? "waiting for a reading…" : $"none seen · checked {lastCheck:HH:mm}";
    }
    void LoadHistory()
    {
        try
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var l in File.ReadAllLines(HistPath))
            {
                var p = l.Split('\t');
                if (p[0] == "S" && p.Length == 4) lastSeen[p[1]] = (double.Parse(p[2], ic), double.Parse(p[3], ic));
                if (p[0] == "B" && p.Length == 4) bonusSeen[p[1]] = (double.Parse(p[2], ic), p[3]);
            }
        }
        catch { }
    }
    void SaveHistory()
    {
        try
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            Directory.CreateDirectory(Path.GetDirectoryName(HistPath));
            File.WriteAllLines(HistPath, lastSeen.Select(kv => $"S\t{kv.Key}\t{kv.Value.used.ToString(ic)}\t{kv.Value.reset.ToString(ic)}")
                .Concat(bonusSeen.Select(kv => $"B\t{kv.Key}\t{kv.Value.at.ToString(ic)}\t{kv.Value.what}")));
        }
        catch { }
    }

    // ---------------- usage history (Full) ----------------
    // One reading per window every 15 min, kept 7 days in %APPDATA%\QuotaWidget\usage.txt. It feeds the sparkline and a
    // straight-line estimate of when the window runs out at the current pace (only shown if that comes before the reset).
    readonly Dictionary<string, List<(double t, double used, double reset)>> usage = new();
    string UsagePath => Path.Combine(Path.GetDirectoryName(cfgPath), "usage.txt");
    bool usageLoaded;

    void RecordUsage()
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (!usageLoaded)
        {
            usageLoaded = true;
            try
            {
                foreach (var l in File.ReadAllLines(UsagePath))
                {
                    var p = l.Split('\t'); if (p.Length != 4) continue;
                    if (!usage.TryGetValue(p[0], out var ls)) usage[p[0]] = ls = new();
                    ls.Add((double.Parse(p[1], ic), double.Parse(p[2], ic), double.Parse(p[3], ic)));
                }
            }
            catch { }
        }
        bool added = false;
        foreach (var a in (snap?.Accounts ?? new()).Where(a => a.Kind == "codex" && a.At > 0))
            foreach (var win in a.Win)
            {
                string k = $"{a.Source}{a.Home}|{win.mins}";
                if (!usage.TryGetValue(k, out var ls)) usage[k] = ls = new();
                if (ls.Count > 0 && a.At - ls[^1].t < 900) continue;      // log time, so an idle account adds nothing
                ls.Add((a.At, win.used, win.reset)); added = true;
            }
        foreach (var ls in usage.Values) ls.RemoveAll(s => Now - s.t > 7 * 86400);
        if (!added) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UsagePath));
            File.WriteAllLines(UsagePath, usage.SelectMany(kv => kv.Value.Select(s => $"{kv.Key}\t{s.t.ToString(ic)}\t{s.used.ToString(ic)}\t{s.reset.ToString(ic)}")));
        }
        catch { }
    }

    // "out in ~X" when the pace over the last 6 h (same reset period) would hit 100% before the window resets
    string Projection(Account a, WinInfo win)
    {
        var (used, wasReset) = Eff(win);
        if (wasReset || win.reset <= Now || used >= 100 || !usage.TryGetValue($"{a.Source}{a.Home}|{win.mins}", out var ls)) return null;
        var pts = ls.Where(s => Math.Abs(s.reset - win.reset) < 900 && Now - s.t < 6 * 3600).ToList();
        if (pts.Count < 2 || pts[^1].t - pts[0].t < 1800) return null;
        double rate = (pts[^1].used - pts[0].used) / (pts[^1].t - pts[0].t);    // % per second
        if (rate <= 0) return null;
        double outIn = (100 - used) / rate - (Now - pts[^1].t);
        return outIn < win.reset - Now ? $"out in ~{Span(Math.Max(60, outIn))} at this pace" : null;
    }

    // last 7 days of the tightest window, one point per reading; the line drops back at each reset
    void Sparkline(Graphics g, Account a, float x, float y, float w, float h)
    {
        var win = a.Win.OrderByDescending(v => Eff(v).used).First();
        if (!usage.TryGetValue($"{a.Source}{a.Home}|{win.mins}", out var ls) || ls.Count < 2) return;
        double t0 = Now - 7 * 86400;
        var pts = ls.Where(s => s.t >= t0).Select(s => new PointF(x + (float)((s.t - t0) / (7 * 86400)) * w, y + h - (float)Math.Clamp(s.used, 0, 100) / 100f * h)).ToArray();
        if (pts.Length < 2) return;
        using (var axis = new Pen(Color.FromArgb(40, A1), 1f)) g.DrawLine(axis, x, y + h, x + w, y + h);
        using var pen = new Pen(Color.FromArgb(200, A2), 1.2f); g.DrawLines(pen, pts);
    }

    // ---------------- alerts (Full) ----------------
    // A Windows balloon from the tray icon when a window resets, passes 90 %, or an early reset shows up.
    // The first pass after start only records the current state, so opening the widget does not fire a burst.
    readonly Dictionary<string, int> alertState = new();   // 0 normal · 1 at 90 %+ · 2 reset passed
    bool alertsSeeded;
    void CheckAlerts()
    {
        if (snap == null) return;
        foreach (var a in snap.Accounts.Where(a => a.Kind == "codex" && Active(a) && !a.Older))
            foreach (var win in a.Win)
            {
                var (used, wasReset) = Eff(win);
                int s = wasReset ? 2 : used >= 90 ? 1 : 0;
                string k = $"{a.Source}{a.Home}|{win.mins}";
                alertState.TryGetValue(k, out var old); alertState[k] = s;
                if (!alertsSeeded || s == old || s == 0) continue;
                if (s == 2) Alert("Quota reset ✓", $"{a.Source} {a.Name} {WinLabel(win.mins)} is ready again");
                else Alert("Quota almost used", $"{a.Source} {a.Name} {WinLabel(win.mins)} at {used:0}% · resets in {Span(win.reset - Now)}");
            }
        alertsSeeded = true;
    }
    void Alert(string title, string text) { if (Notify && alertsSeeded) try { tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Info); } catch { } }

    // ---------------- pin to desktop ----------------
    // Owned by the desktop window (Progman) and kept at the bottom of the z-order: it sits on the wallpaper under every window and
    // stays after Win+D, the way Rainmeter's "On desktop" works. Click, drag and the menu still work.
    void ApplyPin()
    {
        if (!IsHandleCreated) return;
        if (pinDesktop) { TopMost = false; SetWindowLongPtr(Handle, -8, FindWindow("Progman", null)); SetWindowPos(Handle, (IntPtr)1, 0, 0, 0, 0, 0x13); }
        else SetWindowLongPtr(Handle, -8, IntPtr.Zero);
        SaveSettings();
    }
    protected override void WndProc(ref Message m)
    {
        if (pinDesktop && m.Msg == 0x46)   // WM_WINDOWPOSCHANGING: stay under every other window
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            if ((wp.flags & 0x4) == 0) { wp.hwndInsertAfter = (IntPtr)1; Marshal.StructureToPtr(wp, m.LParam, false); }
        }
        base.WndProc(ref m);
    }
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // ---------------- settings ----------------
    void LoadSettings()
    {
        try
        {
            var kv = File.ReadAllLines(cfgPath).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            int I(string k, int d) => kv.TryGetValue(k, out var v) && int.TryParse(v, out var x) ? x : d;
            var pt = new Point(I("x", 0), I("y", 0)); if (Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(pt))) Location = pt;
            TopMost = I("top", 1) == 1; opacity = (byte)Math.Clamp(I("opacity", 255), 40, 255);
            theme = Math.Clamp(I("theme", 0), 0, Themes.Length - 1); bg = Math.Clamp(I("bg", 0), 0, Backgrounds.Length - 1); panelAlpha = Math.Clamp(I("alpha", 215), 0, 255);
            size = Math.Clamp(I("size", 100), 60, 220) / 100f; light = I("light", 0) == 1; effects = I("fx", 1) == 1; showInactive = I("inactive", 0) == 1;
            showClaude = I("claude", 1) == 1; corners = I("corners", 1) == 1; clickThrough = I("through", 0) == 1; refreshMin = Math.Clamp(I("refresh", 5), 1, 60); pinDesktop = I("pin", 0) == 1; visibleRows = Math.Clamp(I("rows", 5), 3, 12);
            full = I("full", EditionDefault()) == 1; bonusOpen = I("bonus", 0) == 1; notify = I("notify", 1) == 1; compact = I("compact", 0) == 1; spark = I("spark", 1) == 1; sortMode = Math.Clamp(I("sort", 0), 0, Sorts.Length - 1);
        }
        catch { TopMost = true; full = EditionDefault() == 1; }
    }

    // edition.txt next to the exe ("full" or "lite") sets the first-run edition of that zip
    static int EditionDefault()
    {
        try { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "edition.txt")).Trim().Equals("full", StringComparison.OrdinalIgnoreCase) ? 1 : 0; }
        catch { return 0; }
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
            File.WriteAllLines(cfgPath, new[] { $"x={Left}", $"y={Top}", $"top={(TopMost ? 1 : 0)}", $"opacity={opacity}", $"theme={theme}", $"bg={bg}", $"alpha={panelAlpha}",
                $"size={(int)Math.Round(size * 100)}", $"light={(light ? 1 : 0)}", $"fx={(effects ? 1 : 0)}", $"inactive={(showInactive ? 1 : 0)}", $"claude={(showClaude ? 1 : 0)}",
                $"corners={(corners ? 1 : 0)}", $"through={(clickThrough ? 1 : 0)}", $"refresh={refreshMin}", $"pin={(pinDesktop ? 1 : 0)}", $"rows={visibleRows}",
                $"full={(full ? 1 : 0)}", $"bonus={(bonusOpen ? 1 : 0)}", $"notify={(notify ? 1 : 0)}", $"compact={(compact ? 1 : 0)}", $"spark={(spark ? 1 : 0)}", $"sort={sortMode}" });
        }
        catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static bool IsStartup() { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("QuotaWidget") != null; }
    static void ToggleStartup()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (IsStartup()) k.DeleteValue("QuotaWidget", false); else k.SetValue("QuotaWidget", "\"" + Environment.ProcessPath + "\"");
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}
