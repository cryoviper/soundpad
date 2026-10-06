using System;
using System.IO;

public static class Logger
{
    // Kept in the app data folder so it doesn't clutter the desktop.
    private static readonly string LogPath = Path.Combine(BoomBx.AppPaths.DataDir, "app.log");

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { /* Ignore logging failures */ }
    }
}