using System.IO;
using System.Text.Json;

namespace BatteryWidget.Models;

public sealed class AppSettings
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double Opacity { get; set; } = 0.92;
    public bool AlwaysOnTop { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 30;
    public int LowBatteryThreshold { get; set; } = 15;
    public bool EnableLowBatteryNotifications { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryWidget");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Fall back to defaults on corrupted settings file
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
