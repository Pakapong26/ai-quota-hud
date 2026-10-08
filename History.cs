// Usage history (Full): a separate window with one card per Codex account (and one per Claude Code home, in tokens). Each card has every rate-limit window as a line over
// the chosen range (7 / 15 / 30 days; it drops back at each reset), how much of the long window was used per day, and the burn
// rate right now. Click a card (or an account row on the widget) to see just that account, larger; double-click for all again.
// The readings come from usage.txt plus a 30-day backfill that collector.py --hist reads from the same logs, so a new install
// shows its history at once. Opened from the menu, the tray icon, an alert, or a click on an account row.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace QuotaWidget;

sealed partial class QuotaForm
{
    HistoryForm histForm;
    DateTime nextHist = DateTime.MinValue;
    const int KeepDays = 31;
    static readonly int[] Ranges = { 7, 15, 30 };
    int histDays = 7;                                       // saved in settings.txt as histdays
    string histFocus;                                       // Source+Home of the one account shown, or null for all
    readonly List<(RectangleF r, string key)> histHits = new();
    const float HistHeadH = 54;

    void ShowHistory() => ShowHistory(null);
    void ShowHistory(Account focus)
    {
        histFocus = focus == null ? null : focus.Source + focus.Home;
        if (histForm == null || histForm.IsDisposed) histForm = new HistoryForm(this);
        histForm.Show(); if (histForm.WindowState == FormWindowState.Minimized) histForm.WindowState = FormWindowState.Normal;
        histForm.Redraw(true); histForm.Activate();
    }

    // readings that collector.py --hist found in the logs go into the same store as the live ones (no duplicates within 5 min)
    void MergeHistory()
    {
        LoadUsage();
        bool added = false;
        foreach (var a in (snap?.Accounts ?? new()).Where(a => a.Kind == "codex" && a.Hist != null))
            foreach (var grp in a.Hist.GroupBy(h => h.mins))
            {
                string k = $"{a.Source}{a.Home}|{grp.Key}";
                if (!usage.TryGetValue(k, out var ls)) usage[k] = ls = new();
                var have = new HashSet<long>(ls.Select(s => (long)(s.t / 300)));    // 5-min buckets already stored
                foreach (var h in grp)
                {
                    long b = (long)(h.t / 300);
                    if (Now - h.t >= KeepDays * 86400 || have.Contains(b) || have.Contains(b - 1) || have.Contains(b + 1)) continue;
                    ls.Add((h.t, h.used, h.reset)); have.Add(b); added = true;
                }
                ls.Sort((x, y) => x.t.CompareTo(y.t));
            }
        int expired = usage.Values.Sum(ls => ls.RemoveAll(s => Now - s.t > KeepDays * 86400));
        if (added || expired > 0) SaveUsage();
        foreach (var a in (snap?.Accounts ?? new()).Where(a => a.Kind == "claude" && a.TokHist != null)) tokHist[a.Source + a.Home] = a.TokHist;
    }
    // Claude Code logs have tokens but no limit %, so Claude cards show tokens per hour and per day (re-read hourly, not stored)
    readonly Dictionary<string, List<(double t, double tok)>> tokHist = new();

    // % of a window used between two times: rises inside one reset period add up; after a reset the new period counts from 0
    static double Burn(List<(double t, double used, double reset)> ls, double from, double to)
    {
        double sum = 0;
        for (int i = 1; i < ls.Count; i++)
        {
            var (p, q) = (ls[i - 1], ls[i]);
            if (q.t < from || q.t >= to) continue;            // each rise belongs to the later reading, so none falls between two days
            sum += Math.Abs(q.reset - p.reset) < 900 ? Math.Max(0, q.used - p.used) : Math.Max(0, q.used);
        }
        return sum;
    }

    List<(double t, double used, double reset)> Series(Account a, int mins) =>
        usage.TryGetValue($"{a.Source}{a.Home}|{mins}", out var ls) ? ls : new();

    IEnumerable<Account> HistoryAccounts()
    {
        var acc = snap?.Accounts ?? new();
        var all = acc.Where(a => a.Kind == "codex" && Active(a) && !a.Older)
            .OrderByDescending(a => a.Win.Sum(w => Series(a, w.mins).Count) > 1).ThenByDescending(MostUsed)
            .Concat(acc.Where(a => a.Kind == "claude" && tokHist.TryGetValue(a.Source + a.Home, out var th) && th.Count > 0).OrderByDescending(a => a.Tok7d)).ToList();
        var one = acc.Where(a => a.Source + a.Home == histFocus && (a.Kind == "claude" || a.Kind == "codex" && Active(a))).Take(1).ToList();   // also an older twin
        return one.Count > 0 ? one : all;
    }
    bool OneAccount => histFocus != null && HistoryAccounts().Count() == 1 && HistoryAccounts().First().Source + HistoryAccounts().First().Home == histFocus;
    float CardH => OneAccount ? 330 : 196;
    float ChartH => OneAccount ? 220 : 96;

    // draws the whole history page at logical width w; returns its height (the snapshot uses this too)
    float DrawHistory(Graphics g, float w, Font fHead, Font fLabel, Font fSm)
    {
        histHits.Clear();
        var accs = HistoryAccounts().ToList();
        using var dim = new SolidBrush(Dim); using var acc = new SolidBrush(AccentText(A1));
        Text(g, "USAGE HISTORY", fHead, acc, 16, 12, false);
        // range chips, then (focused) a way back to all accounts
        float x = 26 + g.MeasureString("USAGE HISTORY", fHead).Width;
        foreach (var d in Ranges)
        {
            var lab = d == 30 ? "30 DAYS" : $"{d} DAYS"; var sz = g.MeasureString(lab, fSm);
            var rc = new RectangleF(x, 12, sz.Width + 10, 17); bool on = d == histDays;
            using (var path = Round(rc, 5))
            {
                using var fill = new SolidBrush(Color.FromArgb(on ? (light ? 70 : 90) : (light ? 18 : 24), A1)); g.FillPath(fill, path);
                if (on) { using var pen = new Pen(Color.FromArgb(200, A1), 1f); g.DrawPath(pen, path); }
            }
            using (var b = new SolidBrush(on ? AccentText(A1) : Dim)) Text(g, lab, fSm, b, x + 5, 13, false);
            histHits.Add((rc, "range:" + d)); x += rc.Width + 5;
        }
        if (OneAccount)
        {
            var lab = "◂ ALL ACCOUNTS  (double-click)"; var sz = g.MeasureString(lab, fSm);
            var rc = new RectangleF(x + 8, 12, sz.Width + 10, 17);
            using (var path = Round(rc, 5)) { using var pen = new Pen(Color.FromArgb(120, A2), 1f); g.DrawPath(pen, path); }
            using (var b = new SolidBrush(AccentText(A2))) Text(g, lab, fSm, b, rc.X + 5, 13, false);
            histHits.Add((rc, "all"));
        }
        var sub = OneAccount ? "burn = how much of a window was used · read from the local logs, no quota used"
                          : "click an account to see it alone · burn = share of a window used · local logs only, no quota used";
        Text(g, sub, fSm, dim, 16, 34, false);
        float y = HistHeadH + 4;
        if (accs.Count == 0) { Text(g, snap == null ? "loading…" : "no Codex accounts with rate-limit data yet", fLabel, dim, 16, y + 6, false); return y + 40; }
        foreach (var a in accs)
        {
            if (a.Kind == "claude") ClaudeHistoryCard(g, a, 10, y, w - 20, fLabel, fSm); else HistoryCard(g, a, 10, y, w - 20, fLabel, fSm);
            histHits.Add((new RectangleF(10, y, w - 20, CardH), "acc:" + a.Source + a.Home));
            y += CardH + 10;
        }
        return y;
    }
    float DrawHistoryHeight() { int n = HistoryAccounts().Count(); return HistHeadH + 4 + (n == 0 ? 40 : n * (CardH + 10)); }

    // what a click at (x, y) in page units does; true when the page changed
    bool HistoryClick(float x, float y)
    {
        foreach (var (r, key) in histHits.Where(h => h.r.Contains(x, y)))
        {
            if (key.StartsWith("range:")) { histDays = int.Parse(key[6..]); SaveSettings(); return true; }
            if (key == "all") { histFocus = null; return true; }
            if (key.StartsWith("acc:") && !OneAccount) { histFocus = key[4..]; return true; }
        }
        return false;
    }

    void HistoryCard(Graphics g, Account a, float x, float y, float w, Font fLabel, Font fSm)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        using (var path = Round(new RectangleF(x, y, w, CardH), 10))
        {
            using var fill = new SolidBrush(light ? Color.FromArgb(250, 251, 254) : Color.FromArgb(14, 21, 36)); g.FillPath(fill, path);
            using var pen = new Pen(Color.FromArgb(light ? 60 : 70, A1), 1f); g.DrawPath(pen, path);
        }
        using var ink = new SolidBrush(Ink); using var dim = new SolidBrush(Dim);
        // header: source dot, name, plan, model
        float hx = x + 12;
        using (var d = new SolidBrush(a.Source == "VPS" ? A2 : A1)) g.FillEllipse(d, hx, y + 14, 6, 6);
        Text(g, a.Source, fSm, dim, hx + 9, y + 10, false); hx += 12 + g.MeasureString(a.Source, fSm).Width;
        Text(g, a.Name, fLabel, ink, hx, y + 8, false); hx += g.MeasureString(a.Name, fLabel).Width + 4;
        Text(g, $"{a.Plan?.ToUpperInvariant()}{(a.Model != null ? "  ·  " + a.Model : "")}", fSm, dim, hx, y + 10, false);

        var wins = a.Win.OrderBy(v => v.mins).ToList();
        Color WinColor(int i) => i == 0 ? A1 : A2;
        float lx = x + w - 12;                                       // legend, right
        for (int i = wins.Count - 1; i >= 0; i--)
        {
            var lab = WinLabel(wins[i].mins) + " used"; float lw = g.MeasureString(lab, fSm).Width;
            lx -= lw; Text(g, lab, fSm, dim, lx, y + 10, false);
            using (var lp = new Pen(WinColor(i), 2f)) g.DrawLine(lp, lx - 16, y + 18, lx - 4, y + 18);
            lx -= 26;
        }

        // line chart over the chosen range, 0..100 % used
        int n = histDays; double t1 = Now, t0 = t1 - n * 86400.0;
        float cx = x + 40, cy = y + 34, cw = (w - 52) * 0.66f, ch = ChartH;
        int every = n <= 7 ? 1 : n <= 15 ? 2 : 5;                    // day labels: every day, every 2nd, every 5th
        string DayLabel(DateTime d, bool shortForm) => n <= 7 ? d.ToString("ddd", ic)[..(shortForm ? 2 : 3)] : d.ToString("d/M", ic);
        using (var grid = new Pen(Color.FromArgb(light ? 30 : 34, Dim), 1f))
        {
            foreach (var pct in new[] { 0, 50, 100 })
            {
                float gy = cy + ch - ch * pct / 100f; g.DrawLine(grid, cx, gy, cx + cw, gy);
                var t = pct + "%"; Text(g, t, fSm, dim, cx - 6 - g.MeasureString(t, fSm).Width, gy - 7, false);
            }
            var day = DateTime.Today.AddDays(-(n - 1));
            for (int i = 0; i < n; i++, day = day.AddDays(1))
            {
                double ts = new DateTimeOffset(day).ToUnixTimeSeconds();
                float dx = cx + (float)((ts - t0) / (t1 - t0)) * cw;
                if (dx >= cx && (n <= 15 || i % every == 0)) g.DrawLine(grid, dx, cy, dx, cy + ch);
                if ((n - 1 - i) % every != 0) continue;                // count back from today so today always has a label
                var lab = DayLabel(day, false);
                float mid = n <= 7 ? cx + (float)((ts + 43200 - t0) / (t1 - t0)) * cw : dx;
                if (mid > cx - 4 && mid < cx + cw - 8) Text(g, lab, fSm, dim, mid - g.MeasureString(lab, fSm).Width / 2, cy + ch + 2, false);
            }
        }
        bool any = false;
        for (int i = 0; i < wins.Count; i++)
        {
            var ls = Series(a, wins[i].mins).Where(s => s.t >= t0).ToList();
            // the live reading too: the store keeps one per 15 min, and a reset in between must still show as a drop
            if (a.At > t0 && (ls.Count == 0 || a.At > ls[^1].t)) ls.Add((a.At, wins[i].used, wins[i].reset));
            if (ls.Count == 0) continue;
            any = true;
            var pts = new List<PointF>();
            PointF P(double t, double u) => new(cx + (float)((t - t0) / (t1 - t0)) * cw, cy + ch - (float)Math.Clamp(u, 0, 100) / 100f * ch);
            // steps, not slopes: use only changes when Codex replies, so a level holds until the next reading;
            // a reset between two readings drops to 0 at the reset time
            for (int k = 0; k < ls.Count; k++)
            {
                if (k > 0)
                {
                    var (p0, q0) = (ls[k - 1], ls[k]);
                    bool resetBetween = Math.Abs(q0.reset - p0.reset) >= 900 && p0.reset > p0.t && p0.reset < q0.t;
                    pts.Add(P(resetBetween ? p0.reset : q0.t, p0.used));
                    if (resetBetween) { pts.Add(P(p0.reset, 0)); pts.Add(P(q0.t, 0)); }
                }
                pts.Add(P(ls[k].t, ls[k].used));
            }
            // the live reading carries the line to "now" (or to 0 if the window has reset since)
            var (nowUsed, nowReset) = Eff(wins[i]);
            if (nowReset && wins[i].reset > ls[^1].t) { pts.Add(P(wins[i].reset, ls[^1].used)); pts.Add(P(wins[i].reset, 0)); }
            else pts.Add(P(t1, ls[^1].used));
            pts.Add(P(t1, nowUsed));
            bool shortOne = i < wins.Count - 1;                         // the busy 5-hour line stays behind the long window's
            using var pen = new Pen(Color.FromArgb(shortOne ? 150 : 255, WinColor(i)), shortOne ? 1.2f : OneAccount ? 2.2f : 1.8f) { LineJoin = LineJoin.Round };
            if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
            using var dot = new SolidBrush(WinColor(i)); var last = pts[^1]; g.FillEllipse(dot, last.X - 2.5f, last.Y - 2.5f, 5, 5);
        }
        if (!any) Text(g, "no readings yet · Codex writes one with every reply", fSm, dim, cx + 8, cy + ch / 2 - 7, false);

        // per-day bars of the longest window (for a weekly window: what share of the week each day took)
        float bx = cx + cw + 30, bw = x + w - 12 - bx;
        if (wins.Count > 0)
        {
            var lw = wins[^1]; var ls = Series(a, lw.mins);
            Text(g, $"{WinLabel(lw.mins)} USED PER DAY", fSm, dim, bx, cy - 4, false);
            var vals = new double[n]; var day = DateTime.Today.AddDays(-(n - 1));
            for (int i = 0; i < n; i++, day = day.AddDays(1))
                vals[i] = Burn(ls, new DateTimeOffset(day).ToUnixTimeSeconds(), new DateTimeOffset(day.AddDays(1)).ToUnixTimeSeconds());
            double max = Math.Max(10, vals.Max());
            float slot = bw / n, top = cy + 22, bh = ch - 22;
            bool numbers = slot >= 20;                                  // values over the bars only when they fit
            int barEvery = n <= 7 ? 1 : Math.Max(1, (int)Math.Ceiling((g.MeasureString("30/10", fSm).Width + 2) / slot));   // dates that do not touch
            var bc = WinColor(wins.Count - 1);                         // same colour as that window's line; today a little lighter
            day = DateTime.Today.AddDays(-(n - 1));
            for (int i = 0; i < n; i++, day = day.AddDays(1))
            {
                float h = (float)(vals[i] / max) * (bh - 12), bxi = bx + i * slot + slot * 0.2f, bwi = Math.Max(1.5f, slot * 0.6f);
                using (var tr = new SolidBrush(Color.FromArgb(light ? 22 : 28, bc))) g.FillRectangle(tr, bxi, top + 12, bwi, bh - 12);
                using (var b = new SolidBrush(Color.FromArgb(i == n - 1 ? 150 : 255, bc))) g.FillRectangle(b, bxi, top + bh - h, bwi, Math.Max(h, vals[i] > 0 ? 1 : 0));
                if (numbers && vals[i] >= 0.5) { var t = $"{vals[i]:0}"; Text(g, t, fSm, dim, bxi + bwi / 2 - g.MeasureString(t, fSm).Width / 2, top + bh - h - 13, false); }
                if ((n - 1 - i) % barEvery != 0) continue;
                var lab = DayLabel(day, true);
                Text(g, lab, fSm, dim, bxi + bwi / 2 - g.MeasureString(lab, fSm).Width / 2, cy + ch + 2, false);
            }
            if (!numbers)
            {
                double busiest = vals.Max();
                if (busiest >= 0.5) Text(g, $"max {busiest:0}%  ·  avg {vals.Average():0.#}%/day", fSm, dim, bx, cy + ch + 16, false);
            }
        }

        // burn right now, one line per window
        float sy = cy + ch + 20 + (wins.Count > 0 && bw / n < 20 ? 12 : 0);
        for (int i = 0; i < wins.Count && i < 2; i++)
        {
            var win = wins[i]; var ls = Series(a, win.mins); var (used, wasReset) = Eff(win);
            double b6 = Burn(ls, Now - 6 * 3600, Now) / 6, b24 = Burn(ls, Now - 86400, Now);
            using (var lb = new SolidBrush(WinColor(i))) g.FillRectangle(lb, x + 12, sy + 5, 8, 3);
            var parts = new List<string> { $"{WinLabel(win.mins)}  {Math.Max(0, 100 - used):0}% left" };
            parts.Add(ls.Count < 2 ? "burn: not enough readings" : $"burn {b6:0.0}%/h (6h)  ·  {b24:0}% in 24h");
            var pj = Projection(a, win);
            if (pj != null) parts.Add("⚠ " + pj);
            else if (!wasReset && win.reset > Now) parts.Add("resets in " + Span(win.reset - Now));
            using var tb = new SolidBrush(pj != null ? AccentText(Amber) : Ink);
            Text(g, string.Join("   ·   ", parts), fSm, tb, x + 26, sy, false); sy += 16;
        }
    }

    static string TokS(double n) => n >= 1e9 ? $"{n / 1e9:0.#}B" : n >= 1e6 ? $"{n / 1e6:0.#}M" : n >= 1e3 ? $"{n / 1e3:0}k" : $"{n:0}";   // short, for axes and bars
    // Claude: tokens per hour as thin bars over the range, tokens per day as bars, and the 5 h / 24 h / 7 d totals
    void ClaudeHistoryCard(Graphics g, Account a, float x, float y, float w, Font fLabel, Font fSm)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        using (var path = Round(new RectangleF(x, y, w, CardH), 10))
        {
            using var fill = new SolidBrush(light ? Color.FromArgb(250, 251, 254) : Color.FromArgb(14, 21, 36)); g.FillPath(fill, path);
            using var pen = new Pen(Color.FromArgb(light ? 60 : 70, A2), 1f); g.DrawPath(pen, path);
        }
        using var ink = new SolidBrush(Ink); using var dim = new SolidBrush(Dim);
        float hx = x + 12;
        using (var d = new SolidBrush(a.Source == "VPS" ? A2 : A1)) g.FillEllipse(d, hx, y + 14, 6, 6);
        Text(g, a.Source, fSm, dim, hx + 9, y + 10, false); hx += 12 + g.MeasureString(a.Source, fSm).Width;
        Text(g, "Claude " + a.Name, fLabel, ink, hx, y + 8, false); hx += g.MeasureString("Claude " + a.Name, fLabel).Width + 4;
        Text(g, (a.Model ?? "").Replace("claude-", ""), fSm, dim, hx, y + 10, false);
        var note = "tokens · Claude logs have no limit %";
        Text(g, note, fSm, dim, x + w - 12 - g.MeasureString(note, fSm).Width, y + 10, false);

        int n = histDays; double t1 = Now, t0 = t1 - n * 86400.0;
        float cx = x + 46, cy = y + 34, cw = (w - 58) * 0.66f, ch = ChartH;
        var hrs = tokHist.TryGetValue(a.Source + a.Home, out var th) ? th.Where(h => h.t >= t0).ToList() : new();
        int every = n <= 7 ? 1 : n <= 15 ? 2 : 5;
        string DayLabel(DateTime d, bool shortForm) => n <= 7 ? d.ToString("ddd", ic)[..(shortForm ? 2 : 3)] : d.ToString("d/M", ic);
        double peak = Math.Max(1, hrs.Select(h => h.tok).DefaultIfEmpty(0).Max());
        using (var grid = new Pen(Color.FromArgb(light ? 30 : 34, Dim), 1f))
        {
            foreach (var f in new[] { 0.0, 0.5, 1.0 })
            {
                float gy = cy + ch - ch * (float)f; g.DrawLine(grid, cx, gy, cx + cw, gy);
                var t = f == 0 ? "0" : TokS(peak * f); Text(g, t, fSm, dim, cx - 6 - g.MeasureString(t, fSm).Width, gy - 7, false);
            }
            var day = DateTime.Today.AddDays(-(n - 1));
            for (int i = 0; i < n; i++, day = day.AddDays(1))
            {
                double ts = new DateTimeOffset(day).ToUnixTimeSeconds();
                float dx = cx + (float)((ts - t0) / (t1 - t0)) * cw;
                if (dx >= cx && (n <= 15 || i % every == 0)) g.DrawLine(grid, dx, cy, dx, cy + ch);
                if ((n - 1 - i) % every != 0) continue;
                var lab = DayLabel(day, false);
                float mid = n <= 7 ? cx + (float)((ts + 43200 - t0) / (t1 - t0)) * cw : dx;
                if (mid > cx - 4 && mid < cx + cw - 8) Text(g, lab, fSm, dim, mid - g.MeasureString(lab, fSm).Width / 2, cy + ch + 2, false);
            }
        }
        if (hrs.Count == 0) Text(g, "no Claude Code use in this range", fSm, dim, cx + 8, cy + ch / 2 - 7, false);
        else
        {
            // one thin bar per hour with use, so quiet hours stay empty
            float bwH = Math.Max(1f, cw / (n * 24f) * 0.8f);
            using var hb = new SolidBrush(A2);
            foreach (var h in hrs)
            {
                float hx2 = cx + (float)((h.t - t0) / (t1 - t0)) * cw, hh = (float)(h.tok / peak) * ch;
                g.FillRectangle(hb, hx2, cy + ch - hh, bwH, Math.Max(1, hh));
            }
        }

        float bx = cx + cw + 30, bw = x + w - 12 - bx, slot = bw / n, top = cy + 22, bh = ch - 22;
        Text(g, "TOKENS PER DAY", fSm, dim, bx, cy - 4, false);
        var vals = new double[n]; var dd = DateTime.Today.AddDays(-(n - 1));
        for (int i = 0; i < n; i++, dd = dd.AddDays(1))
        {
            double a0 = new DateTimeOffset(dd).ToUnixTimeSeconds(), a1 = a0 + 86400;
            vals[i] = hrs.Where(h => h.t >= a0 && h.t < a1).Sum(h => h.tok);
        }
        double max = Math.Max(1, vals.Max());
        bool numbers = slot >= 30;
        int barEvery = n <= 7 ? 1 : Math.Max(1, (int)Math.Ceiling((g.MeasureString("30/10", fSm).Width + 2) / slot));
        dd = DateTime.Today.AddDays(-(n - 1));
        for (int i = 0; i < n; i++, dd = dd.AddDays(1))
        {
            float h = (float)(vals[i] / max) * (bh - 12), bxi = bx + i * slot + slot * 0.2f, bwi = Math.Max(1.5f, slot * 0.6f);
            using (var tr = new SolidBrush(Color.FromArgb(light ? 22 : 28, A2))) g.FillRectangle(tr, bxi, top + 12, bwi, bh - 12);
            using (var b = new SolidBrush(Color.FromArgb(i == n - 1 ? 150 : 255, A2))) g.FillRectangle(b, bxi, top + bh - h, bwi, Math.Max(h, vals[i] > 0 ? 1 : 0));
            if (numbers && vals[i] > 0) { var t = TokS(vals[i]); Text(g, t, fSm, dim, bxi + bwi / 2 - g.MeasureString(t, fSm).Width / 2, top + bh - h - 13, false); }
            if ((n - 1 - i) % barEvery != 0) continue;
            var lab = DayLabel(dd, true);
            Text(g, lab, fSm, dim, bxi + bwi / 2 - g.MeasureString(lab, fSm).Width / 2, cy + ch + 2, false);
        }
        if (!numbers && vals.Max() > 0) Text(g, $"max {TokS(vals.Max())}  ·  avg {TokS(vals.Average())}/day", fSm, dim, bx, cy + ch + 16, false);

        float sy = cy + ch + 20 + (bw / n < 20 ? 12 : 0);
        using (var lb = new SolidBrush(A2)) g.FillRectangle(lb, x + 12, sy + 5, 8, 3);
        Text(g, $"tokens  5h {Tok(a.Tok5h)}   ·   24h {Tok(a.Tok24h)}   ·   7d {Tok(a.Tok7d)}   ·   {a.Msgs24h} replies in 24h", fSm, ink, x + 26, sy, false);
    }

    (Font head, Font label, Font small) HistoryFonts()
    {
        var s = FontSets.FirstOrDefault(f => f.key == fontKey && HasSet(f)); if (s.key == null) s = FontSets[0];
        Font Make(string fam, float pt, bool strong) => new(fam, pt * s.k, strong && fam == s.text ? FontStyle.Bold : FontStyle.Regular);
        return (Make(s.bold, 9f, true), Make(s.bold, 9.5f, true), Make(s.text, 8f, false));
    }

    // the page is drawn at 96 dpi into a bitmap and scaled once, like the widget, so text and lines keep the same proportions
    // on every display scale. The history window can have its own theme and dark/light (or follow the widget).
    int histTheme = -1, histLight = -1, histOpacity = 100;   // -1 = same as the widget
    bool histTop, histBorderless;
    Rectangle histBounds;
    Color HistBack => (histLight < 0 ? light : histLight == 1) ? Color.FromArgb(236, 240, 246) : Color.FromArgb(7, 11, 20);
    Bitmap RenderHistory(int pixelW, float s, out float pageH)
    {
        var (fh, fl, fs) = HistoryFonts();
        int theme0 = theme; bool light0 = light;
        if (histTheme >= 0 && histTheme < Themes.Length) theme = histTheme;
        if (histLight >= 0) light = histLight == 1;
        try
        {
            using (fh) using (fl) using (fs)
            {
                float w = pixelW / s; pageH = DrawHistoryHeight();
                var bmp = new Bitmap(pixelW, Math.Max(1, (int)Math.Ceiling(pageH * s)), PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                g.Clear(HistBack);
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.ScaleTransform(s, s);
                DrawHistory(g, w, fh, fl, fs);
                return bmp;
            }
        }
        finally { theme = theme0; light = light0; }
    }

    // A normal, resizable window (or borderless, HUD style); the page scrolls when there are many accounts.
    // Right-click: range, always on top, opacity, theme, dark / light, borderless. Size and place are remembered.
    sealed class HistoryForm : Form
    {
        readonly QuotaForm q; readonly Canvas canvas; readonly Panel scroller;
        public HistoryForm(QuotaForm owner)
        {
            q = owner;
            Text = "AI Quota - usage history"; Icon = SystemIcons.Information; StartPosition = FormStartPosition.Manual; ShowInTaskbar = true;
            scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            canvas = new Canvas { Location = Point.Empty };
            scroller.Controls.Add(canvas); Controls.Add(scroller);
            scroller.Resize += (_, _) => Redraw(false);
            // press and drag moves the window; press and release in place is a click (range chips, cards)
            canvas.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
                var before = Location;
                ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0);
                if (Location == before && q.HistoryClick(e.X / DpiScale, e.Y / DpiScale)) Redraw(true);
            };
            canvas.MouseDoubleClick += (_, _) => { if (q.histFocus != null) { q.histFocus = null; Redraw(true); } };
            canvas.MouseMove += (_, e) => canvas.Cursor = q.histHits.Any(h => h.r.Contains(e.X / DpiScale, e.Y / DpiScale) && (!q.OneAccount || !h.key.StartsWith("acc:"))) ? Cursors.Hand : Cursors.SizeAll;
            canvas.ContextMenuStrip = Menu();
            ResizeEnd += (_, _) => Remember();
        }
        float DpiScale => DeviceDpi / 96f;
        protected override void OnLoad(EventArgs e)
        {
            // sized here, once the window knows its monitor's DPI (in the constructor it still reports 96)
            float s = DpiScale; var wa = Screen.FromControl(q).WorkingArea;
            MinimumSize = new Size((int)(420 * s), (int)(240 * s));
            var hb = q.histBounds;
            if (!hb.IsEmpty && Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(hb))) Bounds = hb;
            else
            {
                Size = new Size(Math.Min((int)(800 * s), wa.Width), Math.Min((int)(640 * s), wa.Height));
                Location = new Point(Math.Max(wa.Left, Math.Min(q.Left - Width - 12, wa.Right - Width)), Math.Max(wa.Top, Math.Min(q.Top, wa.Bottom - Height)));
            }
            ApplyLook();
            base.OnLoad(e);
        }
        void Remember() { if (WindowState == FormWindowState.Normal) { q.histBounds = Bounds; q.SaveSettings(); } }
        protected override void OnFormClosing(FormClosingEventArgs e) { Remember(); base.OnFormClosing(e); }

        void ApplyLook()
        {
            TopMost = q.histTop;
            Opacity = Math.Clamp(q.histOpacity, 25, 100) / 100.0;
            var b = q.histBorderless ? FormBorderStyle.None : FormBorderStyle.Sizable;
            if (FormBorderStyle != b) { var r = Bounds; FormBorderStyle = b; Bounds = r; }
            Padding = q.histBorderless ? new Padding(Math.Max(3, (int)(4 * DpiScale))) : Padding.Empty;   // the edge you drag to resize
        }
        public void Redraw(bool toTop)
        {
            if (IsDisposed) return;
            var back = q.HistBack;
            int th = q.histTheme >= 0 && q.histTheme < Themes.Length ? q.histTheme : q.theme;
            BackColor = q.histBorderless ? Blend(Themes[th].a1, back, 0.55f) : back;    // borderless: the padding is a thin rim
            scroller.BackColor = canvas.BackColor = back;
            canvas.Page?.Dispose();
            canvas.Page = q.RenderHistory(Math.Max(1, scroller.ClientSize.Width), DpiScale, out _);
            canvas.Size = canvas.Page.Size;
            if (toTop) scroller.AutoScrollPosition = Point.Empty;
            canvas.Invalidate();
        }

        ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false, Renderer = new ToolStripProfessionalRenderer(new MenuColors()), ForeColor = Color.FromArgb(232, 246, 255) };
            var items = new List<(ToolStripMenuItem it, Func<bool> on)>();
            ToolStripMenuItem Item(string text, Func<bool> on, Action set)
            {
                var it = new ToolStripMenuItem(text);
                it.Click += (_, _) => { set(); ApplyLook(); Redraw(false); q.SaveSettings(); };
                items.Add((it, on)); return it;
            }
            var mRange = new ToolStripMenuItem("Range");
            foreach (var d in Ranges) { int k = d; mRange.DropDownItems.Add(Item($"{k} days", () => q.histDays == k, () => q.histDays = k)); }
            var mOp = new ToolStripMenuItem("Opacity");
            foreach (var o in new[] { 100, 90, 80, 70, 55, 40 }) { int k = o; mOp.DropDownItems.Add(Item($"{k}%", () => q.histOpacity == k, () => q.histOpacity = k)); }
            var mTheme = new ToolStripMenuItem("Colour theme");
            mTheme.DropDownItems.Add(Item("Same as the widget", () => q.histTheme < 0, () => q.histTheme = -1));
            mTheme.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < Themes.Length; i++) { int k = i; mTheme.DropDownItems.Add(Item(Themes[i].name, () => q.histTheme == k, () => q.histTheme = k)); }
            var mMode = new ToolStripMenuItem("Dark / light");
            mMode.DropDownItems.Add(Item("Same as the widget", () => q.histLight < 0, () => q.histLight = -1));
            mMode.DropDownItems.Add(Item("Dark", () => q.histLight == 0, () => q.histLight = 0));
            mMode.DropDownItems.Add(Item("Light", () => q.histLight == 1, () => q.histLight = 1));
            var miAll = new ToolStripMenuItem("All accounts  (double-click)", null, (_, _) => { q.histFocus = null; Redraw(true); });
            m.Items.AddRange(new ToolStripItem[] {
                mRange, miAll, new ToolStripSeparator(),
                Item("Always on top", () => q.histTop, () => q.histTop = !q.histTop),
                Item("Borderless (HUD look; drag to move, edges to resize)", () => q.histBorderless, () => q.histBorderless = !q.histBorderless),
                mOp, mTheme, mMode, new ToolStripSeparator(),
                new ToolStripMenuItem("Close", null, (_, _) => Close()) });
            void Sync() { foreach (var (it, on) in items) it.Checked = on(); miAll.Enabled = q.histFocus != null; }
            m.Opening += (_, _) => Sync();
            foreach (var sub in new[] { mRange, mOp, mTheme, mMode }) sub.DropDownOpening += (_, _) => Sync();
            return m;
        }

        // borderless: the thin padding round the page resizes the window like a normal frame
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x84 || !q.histBorderless || WindowState != FormWindowState.Normal) return;   // WM_NCHITTEST
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            int e = Math.Max(6, Padding.Left + 3);
            bool l = p.X < e, r = p.X >= ClientSize.Width - e, t = p.Y < e, b = p.Y >= ClientSize.Height - e;
            int hit = t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 0;
            if (hit != 0) m.Result = (IntPtr)hit;
        }
        protected override void OnFormClosed(FormClosedEventArgs e) { canvas.Page?.Dispose(); canvas.ContextMenuStrip?.Dispose(); base.OnFormClosed(e); }
        sealed class Canvas : Control
        {
            public Bitmap Page;
            public Canvas() { DoubleBuffered = true; }
            protected override void OnPaint(PaintEventArgs e) { if (Page != null) e.Graphics.DrawImageUnscaled(Page, 0, 0); }
        }
    }

    // --snap … hist=1 renders this page instead of the widget (for the README); focus=<home> and days=15/30 pick the view
    static void SnapshotHistory(QuotaForm f, string file, int width, string opts)
    {
        foreach (var kv in opts.Split(';').Select(x => x.Split('=')).Where(x => x.Length == 2))
        {
            if (kv[0] == "days" && int.TryParse(kv[1], out var d)) f.histDays = d;
            if (kv[0] == "focus") f.histFocus = f.HistoryAccounts().FirstOrDefault(a => a.Home == kv[1] || a.Name == kv[1]) is Account a ? a.Source + a.Home : null;
        }
        using var bmp = f.RenderHistory(width, 1f, out _);
        bmp.Save(file, ImageFormat.Png);
    }
}
