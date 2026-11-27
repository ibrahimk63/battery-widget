using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Loads user-defined USB/2.4GHz HID device profiles from the profiles/*.json folder.
/// </summary>
public sealed class JsonHidProfileProvider : IBatteryProvider
{
    private readonly string _profilesDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ProviderName => "JSON HID Profilleri";

    public JsonHidProfileProvider(string? profilesDirectory = null)
    {
        _profilesDirectory = profilesDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "profiles");
    }

    public IReadOnlyList<HidDeviceProfile> LoadProfiles()
    {
        var list = new List<HidDeviceProfile>();
        if (!Directory.Exists(_profilesDirectory))
            return list;

        foreach (var file in Directory.EnumerateFiles(_profilesDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<HidDeviceProfile>(json, JsonOptions);
                if (profile is not null && !string.IsNullOrWhiteSpace(profile.Name))
                    list.Add(profile);
            }
            catch
            {
                // Skip invalid profile files
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
                    stream.ReadTimeout = 500;
                    stream.WriteTimeout = 500;

                    byte[]? response = null;
                    if (profile.UseFeatureReport && device.GetMaxFeatureReportLength() > 0)
                    {
                        var buf = new byte[device.GetMaxFeatureReportLength()];
                        if (query is not null)
                        {
                            Array.Copy(query, buf, Math.Min(query.Length, buf.Length));
                            stream.SetFeature(buf);
                        }
                        stream.GetFeature(buf);
                        response = buf;
                    }
                    else if (device.GetMaxInputReportLength() > 0)
                    {
                        if (query is not null && device.GetMaxOutputReportLength() > 0)
                        {
                            var outBuf = new byte[device.GetMaxOutputReportLength()];
                            Array.Copy(query, outBuf, Math.Min(query.Length, outBuf.Length));
                            stream.Write(outBuf, 0, outBuf.Length);
                        }
                        var inBuf = new byte[device.GetMaxInputReportLength()];
                        int read = stream.Read(inBuf, 0, inBuf.Length);
                        if (read > 0)
                            response = inBuf[..read];
                    }

                    if (response is not null)
                    {
                        var parsed = ParseWithProfile(profile, response);
                        if (parsed.HasValue)
                        {
                            return new DeviceBatteryInfo
                            {
                                Id = $"profile-{vid:X4}-{device.ProductID:X4}",
                                Name = profile.Name,
                                Category = profile.Category,
                                Connection = ConnectionKind.Wireless24Ghz,
                                BatteryPercent = parsed.Value.Percent,
                                IsCharging = parsed.Value.IsCharging,
                                IsConnected = true
                            };
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

    public static (int Percent, bool IsCharging)? ParseWithProfile(HidDeviceProfile profile, ReadOnlySpan<byte> data)
    {
        if (profile.BatteryByteIndex < 0 || profile.BatteryByteIndex >= data.Length)
            return null;

        int raw = data[profile.BatteryByteIndex];
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
