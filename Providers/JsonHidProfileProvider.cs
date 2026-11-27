using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryWidget.Models;
using BatteryWidget.Services;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Loads USB/2.4GHz HID device profiles in priority order:
/// 1) User Auto-Learn profiles (%AppData%\BatteryWidget\profiles\*.json)
/// 2) Remote synced / embedded baseline catalog (profiles-db.json)
/// 3) App-local profiles\*.json
/// </summary>
public sealed class JsonHidProfileProvider : IBatteryProvider
{
    private readonly string[] _profilesDirectories;
    private readonly bool _includeRemoteCatalog;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string UserProfilesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryWidget",
        "profiles");

    public string ProviderName => "JSON & Uzak HID Profilleri";

    public JsonHidProfileProvider(string? profilesDirectory = null)
    {
        if (profilesDirectory is not null)
        {
            _profilesDirectories = [profilesDirectory];
            _includeRemoteCatalog = false;
        }
        else
        {
            _profilesDirectories = [UserProfilesDirectory, Path.Combine(AppContext.BaseDirectory, "profiles")];
            _includeRemoteCatalog = true;
        }
    }

    public IReadOnlyList<HidDeviceProfile> LoadProfiles()
    {
        var list = new List<HidDeviceProfile>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddProfileIfNew(HidDeviceProfile? profile)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
                return;

            var pids = string.Join(",", profile.ProductIdsHex.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            string key = $"{profile.VendorIdHex}:{pids}:{profile.Name}";
            if (seenKeys.Add(key))
                list.Add(profile);
        }

        // 1) User Auto-Learn profiles take highest priority
        if (_profilesDirectories.Length > 0 && Directory.Exists(_profilesDirectories[0]))
        {
            foreach (var file in Directory.EnumerateFiles(_profilesDirectories[0], "*.json"))
            {
                try
                {
                    AddProfileIfNew(JsonSerializer.Deserialize<HidDeviceProfile>(File.ReadAllText(file), JsonOptions));
                }
                catch
                {
                    // Skip invalid profile files
                }
            }
        }

        // 2) Remote synced or embedded baseline profiles-db.json
        if (_includeRemoteCatalog)
        {
            foreach (var profile in RemoteProfileSyncService.LoadCachedOrEmbeddedDatabase().Profiles)
                AddProfileIfNew(profile);
        }

        // 3) Remaining app-local directories
        for (int i = 1; i < _profilesDirectories.Length; i++)
        {
            string dir = _profilesDirectories[i];
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    AddProfileIfNew(JsonSerializer.Deserialize<HidDeviceProfile>(File.ReadAllText(file), JsonOptions));
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
        var wakeup = profile.ParseWakeupPacket();

        foreach (var device in devices)
        {
            try
            {
                if (!device.TryOpen(out var stream))
                    continue;

                using (stream)
                {
                    stream.ReadTimeout = 300;
                    stream.WriteTimeout = 300;

                    if (wakeup is not null && device.GetMaxOutputReportLength() > 0)
                    {
                        var wakeBuf = new byte[device.GetMaxOutputReportLength()];
                        Array.Copy(wakeup, wakeBuf, Math.Min(wakeup.Length, wakeBuf.Length));
                        try
                        {
                            stream.Write(wakeBuf, 0, wakeBuf.Length);
                            Thread.Sleep(15);
                        }
                        catch { /* optional wakeup */ }
                    }

                    if (profile.UseFeatureReport && device.GetMaxFeatureReportLength() > 0)
                    {
                        var buf = new byte[device.GetMaxFeatureReportLength()];
                        if (query is not null)
                        {
                            Array.Copy(query, buf, Math.Min(query.Length, buf.Length));
                            profile.ApplyChecksum(buf);
                            try { stream.SetFeature(buf); Thread.Sleep(15); } catch { /* some devices only support GetFeature */ }
                        }
                        else if (profile.ExpectedReportId >= 0)
                        {
                            buf[0] = (byte)profile.ExpectedReportId;
                        }

                        stream.GetFeature(buf);
                        var parsed = ParseWithProfile(profile, buf);
                        if (parsed.HasValue)
                            return BuildInfo(profile, vid, device.ProductID, parsed.Value, device);
                    }
                    else if (device.GetMaxInputReportLength() > 0)
                    {
                        if (!profile.ReadPassiveInput && query is not null && device.GetMaxOutputReportLength() > 0)
                        {
                            var outBuf = new byte[device.GetMaxOutputReportLength()];
                            Array.Copy(query, outBuf, Math.Min(query.Length, outBuf.Length));
                            profile.ApplyChecksum(outBuf);
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
                                return BuildInfo(profile, vid, device.ProductID, parsed.Value, device);
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

    private static DeviceBatteryInfo BuildInfo(
        HidDeviceProfile profile,
        int vid,
        int pid,
        (int Percent, bool IsCharging) parsed,
        HidDevice device)
    {
        string name = profile.Name;
        try
        {
            string? product = device.GetProductName();
            if (!string.IsNullOrWhiteSpace(product) && profile.ProductIdsHex.Count == 0)
                name = product.Trim();
        }
        catch { /* use profile name */ }

        return new DeviceBatteryInfo
        {
            Id = $"profile-{vid:X4}-{pid:X4}",
            Name = name,
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

        if (raw == 0 &&
            profile.VoltageHighByteIndex.HasValue &&
            profile.VoltageLowByteIndex.HasValue &&
            profile.VoltageHighByteIndex.Value >= 0 &&
            profile.VoltageLowByteIndex.Value >= 0 &&
            profile.VoltageHighByteIndex.Value < data.Length &&
            profile.VoltageLowByteIndex.Value < data.Length)
        {
            int mv = (data[profile.VoltageHighByteIndex.Value] << 8) | data[profile.VoltageLowByteIndex.Value];
            if (mv is >= 3200 and <= 4350)
                percent = Math.Clamp((int)Math.Round((mv - 3300) * 100.0 / 900.0), 1, 100);
        }

        bool isCharging = false;
        if (profile.ChargingByteIndex.HasValue &&
            profile.ChargingByteIndex.Value >= 0 &&
            profile.ChargingByteIndex.Value < data.Length)
        {
            isCharging = profile.ChargingEqualsValue.HasValue
                ? data[profile.ChargingByteIndex.Value] == profile.ChargingEqualsValue.Value
                : (data[profile.ChargingByteIndex.Value] & profile.ChargingMask) != 0;
        }

        return (percent, isCharging);
    }
}
