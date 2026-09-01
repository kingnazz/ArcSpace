using System.Text.Json;

namespace ArcSpace.Services;

public sealed class AppUpdateSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArcSpace");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public bool AutoInstallUpdates { get; set; }
    public bool CheckForUpdatesOnLaunch { get; set; } = true;

    public static AppUpdateSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppUpdateSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppUpdateSettings>(json) ?? new AppUpdateSettings();
        }
        catch
        {
            return new AppUpdateSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
