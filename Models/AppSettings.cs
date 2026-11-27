using System.IO;
using System.Text.Json;

namespace BatteryWidget.Models;

public sealed class HiddenDeviceEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool IsDockedToTaskbar { get; set; } = false;
    public double TaskbarDockLeft { get; set; } = double.NaN;
    public double Opacity { get; set; } = 0.92;
    public bool AlwaysOnTop { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public int LowBatteryThreshold { get; set; } = 15;
    public bool EnableLowBatteryNotifications { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public List<HiddenDeviceEntry> HiddenDevices { get; set; } = [];

    public bool EnableAutoAppUpdate { get; set; } = true;
    public bool EnableRemoteProfileSync { get; set; } = true;
    public DateTimeOffset? LastProfileSyncUtc { get; set; }
    public string? RemoteProfileEtag { get; set; }

    public bool TelemetryConsentAsked { get; set; } = false;
    public bool EnableAnonymousReporting { get; set; } = false;
    public string? LastReportedSignature { get; set; }
    public DateTimeOffset? LastReportSentUtc { get; set; }

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
