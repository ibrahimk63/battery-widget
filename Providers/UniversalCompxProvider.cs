using System.Collections.Concurrent;
using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Universal CompX / Beken / Nordic / PAW3950 8K wireless peripheral battery provider.
/// Covers VXE R1 SE+ / R1+ / R1 Pro / Mad R, Monster Pusat Ghost Pro, Throne Repel 8K, ATK, VGN,
/// Lamzu, Scyrox, Attack Shark, Ajazz, Zaopin, Pulsar, and any unknown USB/2.4GHz HID device
/// exposing Report ID 0x08 OR unnumbered 17B/64B Output/Feature reports.
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
        0x054C, // Sony PlayStation (gamepad provider)
        0x36A7  // WLMouse (WraithEcosystemProvider)
    ];

    private sealed record CachedTarget(
        string DevicePath,
        int OutputLength,
        int InputLength,
        int FeatureLength,
        bool SupportsOut08,
        bool SupportsOut00,
        bool SupportsFeat08,
        bool SupportsFeat00);

    private readonly ConcurrentDictionary<string, CachedTarget?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "Evrensel CompX / VXE / PAW3950 8K HID";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var devices = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID > 0 && !ExcludedVendorIds.Contains(d.VendorID))
                // Prioritize 17-byte 0xFF02 collections (used by VXE R1 SE+ / R1+ / ATK 1K dongles)
                .OrderByDescending(d => d.GetMaxOutputReportLength() == 17 && d.GetMaxInputReportLength() == 17)
                .ThenByDescending(d => d.GetMaxOutputReportLength())
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
            bool hasOut00 = outLen is 17 or 18 or 64 or 65 && inLen >= 17 && desc.OutputReports.Any(r => r.ReportID == 0x00);
            bool hasFeat08 = featLen >= 17 && desc.FeatureReports.Any(r => r.ReportID == 0x08);
            bool hasFeat00 = featLen is 17 or 18 or 64 or 65 && desc.FeatureReports.Any(r => r.ReportID == 0x00);

            if (!hasOut08 && !hasOut00 && !hasFeat08 && !hasFeat00)
                return null;

            return new CachedTarget(device.DevicePath, outLen, inLen, featLen, hasOut08, hasOut00, hasFeat08, hasFeat00);
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
                    var req17 = new byte[target.OutputLength];
                    req17[0] = 0x08;
                    req17[1] = 0x04;
                    if (req17.Length > 16)
                        req17[16] = 0x49;

                    if (TryWriteAndRead(stream, req17, target.InputLength) is { } parsed17)
                        return BuildInfo(device, parsed17.Percent, parsed17.IsCharging);

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

                if (target.SupportsOut00)
                {
                    var req00 = new byte[target.OutputLength];
                    req00[0] = 0x00;
                    req00[1] = 0x08;
                    req00[2] = 0x04;
                    if (req00.Length > 17)
                        req00[17] = 0x49;

                    if (TryWriteAndRead(stream, req00, target.InputLength) is { } parsed00)
                        return BuildInfo(device, parsed00.Percent, parsed00.IsCharging);
                }

                if (target.SupportsFeat08)
                {
                    var reqFeat = new byte[target.FeatureLength];
                    reqFeat[0] = 0x08;
                    reqFeat[1] = 0x04;
                    if (reqFeat.Length > 16)
                        reqFeat[16] = 0x49;

                    if (TryFeatureQuery(stream, reqFeat, 0x08) is { } parsedFeat)
                        return BuildInfo(device, parsedFeat.Percent, parsedFeat.IsCharging);
                }

                if (target.SupportsFeat00)
                {
                    var reqFeat00 = new byte[target.FeatureLength];
                    reqFeat00[0] = 0x00;
                    reqFeat00[1] = 0x08;
                    reqFeat00[2] = 0x04;
                    if (reqFeat00.Length > 17)
                        reqFeat00[17] = 0x49;

                    if (TryFeatureQuery(stream, reqFeat00, 0x00) is { } parsedFeat00)
                        return BuildInfo(device, parsedFeat00.Percent, parsedFeat00.IsCharging);
                }
            }
        }
        catch
        {
            // Ignore busy/unreadable HID interfaces
        }

        return null;
    }

    private static (int Percent, bool IsCharging)? TryFeatureQuery(HidStream stream, byte[] reqFeat, byte reportId)
    {
        try
        {
            stream.SetFeature(reqFeat);
            var respFeat = new byte[reqFeat.Length];
            respFeat[0] = reportId;
            stream.GetFeature(respFeat);
            return ParseCompxResponse(respFeat);
        }
        catch
        {
            return null;
        }
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
    /// Parses CompX/VXE/Monster/ATK/PAW3950 HID responses:
    /// - Standard: [0x08, 0x04/0x72, ..., percent at 6, charging at 7, mV at 8..9]
    /// - Zero-prefixed: [0x00, 0x08, 0x04/0x72, ..., percent at 7, charging at 8, mV at 9..10]
    /// Includes Li-Ion millivolt fallback when percentage byte is 0.
    /// </summary>
    public static (int Percent, bool IsCharging)? ParseCompxResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x08 && (data[1] == 0x04 || data[1] == 0x72))
        {
            int pct = data[6];
            if (pct is >= 1 and <= 100)
                return (pct, data[7] == 1);

            if (pct == 0 && data.Length >= 10)
            {
                int mv = (data[8] << 8) | data[9];
                if (mv is >= 3200 and <= 4350)
                    return (MillivoltsToPercent(mv), data[7] == 1);
            }
        }

        if (data.Length >= 9 && data[0] == 0x00 && data[1] == 0x08 && (data[2] == 0x04 || data[2] == 0x72))
        {
            int pct = data[7];
            if (pct is >= 1 and <= 100)
                return (pct, data[8] == 1);

            if (pct == 0 && data.Length >= 11)
            {
                int mv = (data[9] << 8) | data[10];
                if (mv is >= 3200 and <= 4350)
                    return (MillivoltsToPercent(mv), data[8] == 1);
            }
        }

        return null;
    }

    private static int MillivoltsToPercent(int mv) =>
        Math.Clamp((int)Math.Round((mv - 3300) * 100.0 / 900.0), 1, 100);

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

        if (name.Contains("w75", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("w60", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("vxe75", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("atk68", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Keyboard;

        if (name.Contains("pusat", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ghost", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("repel", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("throne", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("vxe", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("r1", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mad r", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("atk", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("vgn", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("lamzu", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("maya", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("atlantis", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("thorn", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wlmouse", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("beast", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mchose", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("sora", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ninjutso", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wraith", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ajazz", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("shark", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("zaopin", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("scyrox", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Mouse;

        return DeviceCategory.Other;
    }
}
