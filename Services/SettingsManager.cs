using System;
using System.IO;
using System.Text.Json;
using BoomBx.Models;

namespace BoomBx.Services
{
    public static class SettingsManager
    {
        private static string AppDataDir => AppPaths.DataDir;

        public static AppSettings LoadSettings()
        {
            Directory.CreateDirectory(AppDataDir);
            var path = Path.Combine(AppDataDir, "settings.json");
            
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings()
                    : new AppSettings();
            }
            catch (Exception ex)
            {
                Logger.Log($"Settings file broken, using defaults: {ex.Message}");
                return new AppSettings();
            }
        }

        public static void SaveSettings(AppSettings settings)
        {
            Directory.CreateDirectory(AppDataDir);
            var path = Path.Combine(AppDataDir, "settings.json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.Log($"Couldn't save settings: {ex.Message}");
            }
        }
    }
}