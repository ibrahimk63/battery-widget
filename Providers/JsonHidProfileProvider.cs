using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Loads user-defined and Auto-Learned USB/2.4GHz HID device profiles from both
/// app-local profiles/*.json and persistent %AppData%\BatteryWidget\profiles/*.json.
/// </summary>
public sealed class JsonHidProfileProvider : IBatteryProvider
{
    private readonly string[] _profilesDirectories;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string UserProfilesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryWidget",
        "profiles");

    public string ProviderName => "JSON HID Profilleri";

    public JsonHidProfileProvider(string? profilesDirectory = null)
    {
        _profilesDirectories = profilesDirectory is not null
            ? [profilesDirectory]
            : [Path.Combine(AppContext.BaseDirectory, "profiles"), UserProfilesDirectory];
    }

    public IReadOnlyList<HidDeviceProfile> LoadProfiles()
    {
        var list = new List<HidDeviceProfile>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in _profilesDirectories)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var profile = JsonSerializer.Deserialize<HidDeviceProfile>(json, JsonOptions);
                    if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
                        continue;

                    string key = $"{profile.VendorIdHex}:{string.Join(",", profile.ProductIdsHex)}:{profile.Name}";
                    if (seenKeys.Add(key))
                        list.Add(profile);
                }
                catch
                {
                    // Skip invalid profile files
                }
            }
        }
        return list;
    }

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var profiles = LoadProfiles();
            if (profiles.Count == 0)
                return results;

            foreach (var profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = PollProfile(profile);
                if (info is not null)
                    results.Add(info);
            }

            return results;
        }, cancellationToken);
    }

    private static DeviceBatteryInfo? PollProfile(HidDeviceProfile profile)
    {
        int vid = profile.ParseVendorId();
        var pids = profile.ParseProductIds();
        var devices = DeviceList.Local.GetHidDevices(vid)
            .Where(d => pids.Count == 0 || pids.Contains(d.ProductID))
            .ToList();

        if (devices.Count == 0)
            return null;

        var query = profile.ParseQueryPacket();

        foreach (var device in devices)
        {
            try
            {
                if (!device.TryOpen(out var stream))
                    continue;

                using (stream)
                {
                    stream.ReadTimeout = 350;
                    stream.WriteTimeout = 350;

                    if (profile.UseFeatureReport && device.GetMaxFeatureReportLength() > 0)
                    {
                        var buf = new byte[device.GetMaxFeatureReportLength()];
                        if (query is not null)
                        {
                            Array.Copy(query, buf, Math.Min(query.Length, buf.Length));
                            try { stream.SetFeature(buf); } catch { /* some devices only support GetFeature */ }
                        }
                        else if (profile.ExpectedReportId >= 0)
                        {
                            buf[0] = (byte)profile.ExpectedReportId;
                        }

                        stream.GetFeature(buf);
                        var parsed = ParseWithProfile(profile, buf);
                        if (parsed.HasValue)
                            return BuildInfo(profile, vid, device.ProductID, parsed.Value);
                    }
                    else if (device.GetMaxInputReportLength() > 0)
                    {
                        if (!profile.ReadPassiveInput && query is not null && device.GetMaxOutputReportLength() > 0)
                        {
                            var outBuf = new byte[device.GetMaxOutputReportLength()];
                            Array.Copy(query, outBuf, Math.Min(query.Length, outBuf.Length));
                            stream.Write(outBuf, 0, outBuf.Length);
                        }

                        var inBuf = new byte[device.GetMaxInputReportLength()];
                        int attempts = profile.ReadPassiveInput ? 3 : 2;
                        for (int i = 0; i < attempts; i++)
                        {
                            int read = stream.Read(inBuf, 0, inBuf.Length);
                            if (read <= 0)
                                break;

                            var parsed = ParseWithProfile(profile, inBuf.AsSpan(0, read));
                            if (parsed.HasValue)
                                return BuildInfo(profile, vid, device.ProductID, parsed.Value);
                        }
                    }
                }
            }
            catch
            {
                // Try next HID collection
            }
        }

        return null;
    }

    private static DeviceBatteryInfo BuildInfo(HidDeviceProfile profile, int vid, int pid, (int Percent, bool IsCharging) parsed)
    {
        return new DeviceBatteryInfo
        {
            Id = $"profile-{vid:X4}-{pid:X4}",
            Name = profile.Name,
            Category = profile.Category,
            Connection = ConnectionKind.Wireless24Ghz,
            BatteryPercent = parsed.Percent,
            IsCharging = parsed.IsCharging,
            IsConnected = true
        };
    }

    public static (int Percent, bool IsCharging)? ParseWithProfile(HidDeviceProfile profile, ReadOnlySpan<byte> data)
    {
        if (profile.ExpectedReportId >= 0 && (data.Length == 0 || data[0] != profile.ExpectedReportId))
            return null;

        if (profile.BatteryByteIndex < 0 || profile.BatteryByteIndex >= data.Length)
            return null;

        int mask = profile.BatteryMask <= 0 ? 0xFF : profile.BatteryMask;
        int raw = data[profile.BatteryByteIndex] & mask;
        int max = Math.Max(1, profile.MaxBatteryValue);
        int percent = Math.Clamp((int)Math.Round(raw * 100.0 / max), 0, 100);

        bool isCharging = false;
        if (profile.ChargingByteIndex.HasValue &&
            profile.ChargingByteIndex.Value >= 0 &&
            profile.ChargingByteIndex.Value < data.Length)
        {
            isCharging = (data[profile.ChargingByteIndex.Value] & profile.ChargingMask) != 0;
        }

        return (percent, isCharging);
    }
}
