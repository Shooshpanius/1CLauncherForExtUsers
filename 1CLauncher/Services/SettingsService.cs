using System;
using System.IO;
using System.Text.Json;

namespace _1CLauncher.Services
{
    public class AppSettings
    {
        public string Username { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string ExternalUrl { get; set; } = string.Empty;
    }

    public static class SettingsService
    {
        private static string GetPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "1CLauncher", "settings.json");
        }

        public static AppSettings Load()
        {
            try
            {
                var path = GetPath();
                if (!File.Exists(path))
                    return new AppSettings();

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var path = GetPath();
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
                // ignore save errors
            }
        }
    }
}
