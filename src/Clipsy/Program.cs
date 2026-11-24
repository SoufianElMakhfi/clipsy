namespace Clipsy;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\Clipsy.SingleInstance", out bool first);
        if (!first) return; // läuft schon

        Application.ThreadException += (_, e) => Log.Write($"UI-Fehler: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Absturz: {e.ExceptionObject}");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetColorMode(SystemColorMode.System); // hell/dunkel wie Windows
        Log.Write("Clipsy gestartet");
        Application.Run(new TrayApp());
    }
}
