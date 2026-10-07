// Quota Widget: a desktop HUD of Codex rate-limit windows and Claude token use for every account on this laptop and the VPS.
// It only reads the logs those tools already write (collector.py), so it never spends any quota.
namespace QuotaWidget;

static class Program
{
    public static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuotaWidget"); Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "errors.log"); if (File.Exists(f) && new FileInfo(f).Length > 200_000) File.Move(f, f + ".old", true);
            File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n");
        }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "QuotaWidget.SingleInstance", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        if (args.Length >= 1 && args[0] == "--snap") { QuotaForm.Snapshot(args.Length > 1 ? args[1] : "snap.png", args.Length > 2 ? args[2] : ""); return; }
        Application.Run(new QuotaForm());
    }
}
