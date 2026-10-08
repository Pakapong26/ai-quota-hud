// Fetches quota data without using any quota: runs collector.py on this laptop and on the VPS (over the existing ssh key)
// and parses the JSON it prints. Host, user and key path come from ~/.vps_env at run time and are never shown or saved.
using System.Diagnostics;
using System.Text.Json;

namespace QuotaWidget;

sealed class Account
{
    public string Source, Home, Kind, Plan, Blocked, Model, Err;
    public double? FreeUsed, FreeLimit, Wallet;
    public double At, Tok5h, Tok24h, Tok7d; public int Msgs24h; public bool Auth = true;
    public List<WinInfo> Win = new();
    public List<(double t, int mins, double used, double reset)> Hist;   // only when fetched with history (--hist)
    public List<(double t, double tok)> TokHist;                          // Claude: tokens per hour, also only with --hist
    // the same Codex home name + plan on the laptop and on the VPS is treated as one account seen twice;
    // rows stay separate (each keeps its own countdown) and the one with the older log line is marked
    public Account Twin; public bool Older;
    public string Name
    {
        get
        {
            if (Kind == "xkiro") return "xKiro";
            if (Data.Alias(Source, Home) is string alias) return alias;
            var n = Home.TrimStart('.').Replace('_', '-');
            var pre = Kind == "codex" ? "codex" : "claude";
            n = n == pre ? "main" : n.StartsWith(pre + "-") ? n[(pre.Length + 1)..] : n;
            return n.Replace("openai-", "oa-");
        }
    }
}

sealed class WinInfo { public double used, reset, spent = -1, cap = -1; public int mins; }

sealed class Snapshot
{
    public List<Account> Accounts = new();
    public DateTime Time;
    public bool LapOk, VpsOk; public string VpsErr = "";
}

static class Data
{
    // Optional row names: %APPDATA%\QuotaWidget\names.txt, one "home=name" or "SOURCE/home=name" per line,
    // e.g. "LAP/.codex=business" (only this PC's ~/.codex) or ".codex-oa=openai-team" (both PCs). Re-read when the file changes.
    static readonly string NamesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuotaWidget", "names.txt");
    static Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
    static DateTime namesSeen;
    public static string Alias(string source, string home)
    {
        try
        {
            var t = File.Exists(NamesPath) ? File.GetLastWriteTimeUtc(NamesPath) : DateTime.MinValue;
            if (t != namesSeen)
            {
                namesSeen = t;
                aliases = t == DateTime.MinValue ? new(StringComparer.OrdinalIgnoreCase) : File.ReadAllLines(NamesPath).Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && !p[0].TrimStart().StartsWith("#"))
                    .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return aliases.TryGetValue($"{source}/{home}", out var a) || aliases.TryGetValue(home ?? "", out a) ? a : null;
    }

    static string Dir => AppContext.BaseDirectory;
    static string Collector => Path.Combine(Dir, "collector.py");

    public static Snapshot Fetch() => Fetch(false);
    // hist: also read the last 31 days of rate-limit readings from the logs (the widget asks at start and then hourly)
    public static Snapshot Fetch(bool hist)
    {
        var s = new Snapshot { Time = DateTime.Now };
        var flag = hist ? " --hist=31" : "";
        var t1 = Task.Run(() => Run(PyExe(), $"\"{Collector}\" \"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\"{flag}", null, 90));
        var t2 = Task.Run(() => RunVps(flag));
        try { var o = t1.Result; if (o != null) { Parse(o, "LAP", s); s.LapOk = true; } } catch (Exception ex) { Program.Log(ex); }
        try { var (o, err) = t2.Result; if (o != null) { Parse(o, "VPS", s); s.VpsOk = true; } else s.VpsErr = err; } catch (Exception ex) { Program.Log(ex); s.VpsErr = "error"; }
        return s;
    }

    static string PyExe()
    {
        foreach (var c in new[] { "py.exe", "python.exe" })
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                try { if (File.Exists(Path.Combine(p, c))) return Path.Combine(p, c); } catch { }
        return "py.exe";
    }

    static (string, string) RunVps(string flag)
    {
        var env = ReadEnv();
        if (!env.TryGetValue("VPS_HOST", out var host) || !env.TryGetValue("VPS_USER", out var user) || !env.TryGetValue("VPS_KEY", out var key)) return (null, "no ~/.vps_env");
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        if (!File.Exists(ssh)) ssh = "ssh.exe";
        var args = $"-i \"{WinPath(key)}\" -o BatchMode=yes -o ConnectTimeout=15 -o StrictHostKeyChecking=accept-new {user}@{host} python3 -{flag}";
        var o = Run(ssh, args, File.ReadAllText(Collector), 90);
        return o == null ? (null, "offline") : (o, "");
    }

    static Dictionary<string, string> ReadEnv()
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var raw in File.ReadAllLines(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vps_env")))
            {
                var l = raw.Trim(); if (l.StartsWith("export ")) l = l[7..];
                var i = l.IndexOf('='); if (i <= 0 || l.StartsWith("#")) continue;
                d[l[..i].Trim()] = l[(i + 1)..].Trim().Trim('"', '\'');
            }
        }
        catch { }
        return d;
    }

    static string WinPath(string p)   // "~/.ssh/x" or "/c/Users/x" (Git Bash style) -> Windows path
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        p = p.Replace("$HOME", home);
        if (p.StartsWith("~")) p = home + p[1..];
        if (p.Length > 3 && p[0] == '/' && p[2] == '/') p = p[1] + ":" + p[2..];
        return p.Replace('/', '\\');
    }

    static string Run(string exe, string args, string stdin, int timeoutSec)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi);
        if (stdin != null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        var outTask = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } return null; }
        var o = outTask.Result.Trim();
        return p.ExitCode == 0 && o.StartsWith("{") ? o : null;
    }

    static void Parse(string json, string src, Snapshot s)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var a in doc.RootElement.GetProperty("accounts").EnumerateArray())
        {
            string S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double D(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var acc = new Account { Source = src, Home = S("home"), Kind = S("kind"), Plan = S("plan"), Blocked = S("blocked"), Model = S("model"), At = D("at"), Tok5h = D("tok5h"), Tok24h = D("tok24h"), Tok7d = D("tok7d"), Msgs24h = (int)D("msgs24h") };
            if (a.TryGetProperty("auth", out var au) && au.ValueKind == JsonValueKind.False) acc.Auth = false;
            acc.Err = S("err");
            if (a.TryGetProperty("free", out var fr) && fr.ValueKind == JsonValueKind.Object)
            {
                if (fr.TryGetProperty("used", out var fu) && fu.ValueKind == JsonValueKind.Number) acc.FreeUsed = fu.GetDouble();
                if (fr.TryGetProperty("limit", out var fl) && fl.ValueKind == JsonValueKind.Number) acc.FreeLimit = fl.GetDouble();
            }
            if (a.TryGetProperty("wallet", out var wl) && wl.ValueKind == JsonValueKind.Number) acc.Wallet = wl.GetDouble();
            if (a.TryGetProperty("win", out var w))
                foreach (var x in w.EnumerateArray())
                {
                    double N(string k, double d) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
                    acc.Win.Add(new WinInfo { used = N("used", 0), mins = (int)N("mins", 0), reset = N("reset", 0), spent = N("spent", -1), cap = N("cap", -1) });
                }
            if (a.TryGetProperty("hist", out var hs) && hs.ValueKind == JsonValueKind.Array)
                acc.Hist = hs.EnumerateArray().Where(x => x.GetArrayLength() == 4).Select(x => (x[0].GetDouble(), (int)x[1].GetDouble(), x[2].GetDouble(), x[3].GetDouble())).ToList();
            if (a.TryGetProperty("thist", out var th) && th.ValueKind == JsonValueKind.Array)
                acc.TokHist = th.EnumerateArray().Where(x => x.GetArrayLength() == 2).Select(x => (x[0].GetDouble(), x[1].GetDouble())).ToList();
            s.Accounts.Add(acc);
        }
    }
}
