using System.Collections.Concurrent;
using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Universal CompX / Beken / Nordic 2.4GHz wireless peripheral battery provider.
/// Covers Monster Pusat Ghost Pro, VXE, ATK, VGN, Attack Shark, Ajazz, Zaopin, Scyrox, Pulsar, MCHOSE,
/// and any unknown USB/2.4GHz HID device whose Report Descriptor exposes Report ID 0x08 (17B/64B Output or Feature).
/// </summary>
public sealed class UniversalCompxProvider : IBatteryProvider
{
    private static readonly HashSet<int> ExcludedVendorIds =
    [
        0x03F0, // HyperX (dedicated provider)
        0x0BDA, // Realtek / Darmoshark (dedicated provider)
        0x046D, // Logitech (HID++ provider)
        0x1532, // Razer (90B feature provider)
        0x1038, // SteelSeries (dedicated provider)
        0x054C  // Sony PlayStation (gamepad provider)
    ];

    private sealed record CachedTarget(
        string DevicePath,
        int OutputLength,
        int InputLength,
        int FeatureLength,
        bool SupportsOut08,
        bool SupportsFeat08);

    private readonly ConcurrentDictionary<string, CachedTarget?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "Evrensel CompX / 2.4GHz HID (Monster, VXE, ATK vb.)";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var devices = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID > 0 && !ExcludedVendorIds.Contains(d.VendorID))
                .ToList();

            var activePaths = new HashSet<string>(devices.Select(d => d.DevicePath), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _cache.Keys)
            {
                if (!activePaths.Contains(key))
                    _cache.TryRemove(key, out _);
            }

            var seenDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = _cache.GetOrAdd(device.DevicePath, _ => InspectTarget(device));
                if (target is null)
                    continue;

                string devKey = $"{device.VendorID:X4}:{device.ProductID:X4}";
                if (seenDeviceIds.Contains(devKey))
                    continue;

                var info = TryQueryDevice(device, target);
                if (info is not null)
                {
                    seenDeviceIds.Add(devKey);
                    results.Add(info);
                }
            }

            return results;
        }, cancellationToken);
    }

    private static CachedTarget? InspectTarget(HidDevice device)
    {
        try
        {
            int outLen = device.GetMaxOutputReportLength();
            int inLen = device.GetMaxInputReportLength();
            int featLen = device.GetMaxFeatureReportLength();

            if (outLen < 17 && featLen < 17)
                return null;

            var desc = device.GetReportDescriptor();
            bool hasOut08 = outLen >= 17 && inLen >= 17 && desc.OutputReports.Any(r => r.ReportID == 0x08);
            bool hasFeat08 = featLen >= 17 && desc.FeatureReports.Any(r => r.ReportID == 0x08);

            if (!hasOut08 && !hasFeat08)
                return null;

            return new CachedTarget(device.DevicePath, outLen, inLen, featLen, hasOut08, hasFeat08);
        }
        catch
        {
            return null;
        }
    }

    private static DeviceBatteryInfo? TryQueryDevice(HidDevice device, CachedTarget target)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 300;
                stream.WriteTimeout = 300;

                if (target.SupportsOut08)
                {
                    // 1) Standard 17-byte CompX/VXE/Monster query: [0x08, 0x04, ..., checksum 0x49 at byte 16]
                    var req17 = new byte[target.OutputLength];
                    req17[0] = 0x08;
                    req17[1] = 0x04;
                    if (req17.Length > 16)
                        req17[16] = 0x49;

                    if (TryWriteAndRead(stream, req17, target.InputLength) is { } parsed17)
                        return BuildInfo(device, parsed17.Percent, parsed17.IsCharging);

                    // 2) Extended 64-byte CompX/ATK query: [0x08, 0x7D, 0x72, ...]
                    if (target.OutputLength >= 64)
                    {
                        var req64 = new byte[target.OutputLength];
                        req64[0] = 0x08;
                        req64[1] = 0x7D;
                        req64[2] = 0x72;

                        if (TryWriteAndRead(stream, req64, target.InputLength) is { } parsed64)
                            return BuildInfo(device, parsed64.Percent, parsed64.IsCharging);
                    }
                }

                if (target.SupportsFeat08)
                {
                    var reqFeat = new byte[target.FeatureLength];
                    reqFeat[0] = 0x08;
                    reqFeat[1] = 0x04;
                    if (reqFeat.Length > 16)
                        reqFeat[16] = 0x49;

                    try
                    {
                        stream.SetFeature(reqFeat);
                        var respFeat = new byte[target.FeatureLength];
                        respFeat[0] = 0x08;
                        stream.GetFeature(respFeat);

                        var parsed = ParseCompxResponse(respFeat);
                        if (parsed.HasValue)
                            return BuildInfo(device, parsed.Value.Percent, parsed.Value.IsCharging);
                    }
                    catch
                    {
                        // Ignore unsupported feature request
                    }
                }
            }
        }
        catch
        {
            // Ignore busy/unreadable HID interfaces
        }

        return null;
    }

    private static (int Percent, bool IsCharging)? TryWriteAndRead(HidStream stream, byte[] req, int inputLength)
    {
        try
        {
            stream.Write(req, 0, req.Length);
            var resp = new byte[inputLength];
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int read = stream.Read(resp, 0, resp.Length);
                if (read <= 0)
                    break;

                var parsed = ParseCompxResponse(resp.AsSpan(0, read));
                if (parsed.HasValue)
                    return parsed;
            }
        }
        catch
        {
            // Ignore timeout / write errors
        }

        return null;
    }

    /// <summary>
    /// Parses CompX/Beken/Monster/VXE/ATK HID responses:
    /// - Standard: [0x08, 0x04, ..., percent at 6, charging at 7]
    /// - Extended: [0x08, 0x72, ..., percent at 6, charging at 7]
    /// </summary>
    public static (int Percent, bool IsCharging)? ParseCompxResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || data[0] != 0x08)
            return null;

        if (data[1] == 0x04 || data[1] == 0x72)
        {
            int pct = data[6];
            if (pct is >= 1 and <= 100)
            {
                bool isCharging = data[7] == 1;
                return (pct, isCharging);
            }
        }

        return null;
    }

    private static DeviceBatteryInfo BuildInfo(HidDevice device, int percent, bool isCharging)
    {
        string? rawName = null;
        try { rawName = device.GetProductName(); } catch { /* ignore */ }
        string name = string.IsNullOrWhiteSpace(rawName)
            ? $"Kablosuz Cihaz ({device.VendorID:X4}:{device.ProductID:X4})"
            : rawName.Trim();

        return new DeviceBatteryInfo
        {
            Id = $"compx-{device.VendorID:X4}-{device.ProductID:X4}",
            Name = name,
            Category = GuessCategory(name),
            Connection = ConnectionKind.Wireless24Ghz,
            BatteryPercent = percent,
            IsCharging = isCharging,
            IsConnected = true
        };
    }

    private static DeviceCategory GuessCategory(string name)
    {
        var guessed = BluetoothBatteryProvider.GuessCategory(name);
        if (guessed != DeviceCategory.Other)
            return guessed;

        if (name.Contains("pusat", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ghost", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("vxe", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("atk", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("vgn", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ajazz", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("shark", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("zaopin", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("scyrox", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Mouse;

        return DeviceCategory.Other;
    }
}
