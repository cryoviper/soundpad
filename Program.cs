using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BoomBx;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // If anything crashes, write the reason down and show it instead of closing silently.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception, "app");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Log($"[Background task error] {e.Exception}");
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ReportCrash(ex, "startup");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void ReportCrash(Exception? ex, string where)
    {
        var text = $"Boobies Soundpad crashed ({where}) at {DateTime.Now}\n\n{ex}";
        string path = "";
        try
        {
            path = Path.Combine(AppPaths.DataDir, "crash.log");
            File.WriteAllText(path, text);
            Logger.Log(text);
        }
        catch { /* nothing else we can do */ }

        try
        {
            if (OperatingSystem.IsWindows())
                MessageBoxW(IntPtr.Zero,
                    $"Sorry, the app crashed.\n\n{ex?.GetType().Name}: {ex?.Message}\n\nFull details saved to:\n{path}\n\nSend that file to the developer.",
                    "Boobies Soundpad", 0x10);
        }
        catch { /* ignore */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
