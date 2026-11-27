using System.Collections.Concurrent;
using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Vendor-agnostic HID Fingerprint Discovery Engine.
/// Detects devices by HID Report Descriptor structure regardless of brand:
/// - Strategy A: Rapoo / 8K wireless mice (Input Report ID 0x07, >= 19 bytes)
/// - Strategy B: Standard HID Battery Input Reports (Usage 0x06/0x20, 0x85/0x65, 0x85/0x66)
/// </summary>
public sealed class UniversalHidFingerprintProvider : IBatteryProvider
{
    private const uint UsageBatteryStrength = 0x00060020;
    private const uint UsageAbsoluteSoc = 0x00850065;
    private const uint UsageRemainingCapacity = 0x00850066;

    private enum FingerprintKind
    {
        None,
        Rapoo8KInput07,
        StandardInputBattery
    }

    private sealed record CachedFingerprint(
        FingerprintKind Kind,
        byte ReportId,
        int InputLength,
        int ByteOffset,
        int BitShift,
        int BitMask,
        int LogicalMin,
        int LogicalMax);

    private readonly ConcurrentDictionary<string, CachedFingerprint> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "Evrensel HID Parmak İzi Motoru";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var devices = DeviceList.Local.GetHidDevices().Where(d => d.VendorID > 0).ToList();

            var activePaths = new HashSet<string>(devices.Select(d => d.DevicePath), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _cache.Keys)
            {
                if (!activePaths.Contains(key))
                    _cache.TryRemove(key, out _);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fp = _cache.GetOrAdd(device.DevicePath, _ => InspectFingerprint(device));
                if (fp.Kind == FingerprintKind.None)
                    continue;

                string devKey = $"{device.VendorID:X4}:{device.ProductID:X4}";
                if (seen.Contains(devKey))
                    continue;

                var info = fp.Kind switch
                {
                    FingerprintKind.Rapoo8KInput07 => PollRapoo8K(device, fp),
                    FingerprintKind.StandardInputBattery => PollStandardInputBattery(device, fp),
                    _ => null
                };

                if (info is not null)
                {
                    seen.Add(devKey);
                    results.Add(info);
                }
            }

            return results;
        }, cancellationToken);
    }

    private static CachedFingerprint InspectFingerprint(HidDevice device)
    {
        try
        {
            int inLen = device.GetMaxInputReportLength();
            if (inLen < 2)
                return new CachedFingerprint(FingerprintKind.None, 0, 0, 0, 0, 0, 0, 100);

            var desc = device.GetReportDescriptor();

            // Strategy A: Rapoo / 8K receiver & mouse telemetry (Input Report ID 0x07, length >= 19)
            if (inLen >= 19 && desc.InputReports.Any(r => r.ReportID == 0x07 && r.Length >= 19))
            {
                return new CachedFingerprint(FingerprintKind.Rapoo8KInput07, 0x07, inLen, 8, 0, 0xFF, 0, 100);
            }

            // Strategy B: Standard HID Battery usage inside an Input Report
            foreach (var report in desc.InputReports)
            {
                foreach (var item in report.DataItems)
                {
                    bool matches = item.Usages.GetAllValues().Any(u =>
                        u == UsageBatteryStrength || u == UsageAbsoluteSoc || u == UsageRemainingCapacity);

                    if (!matches || item.ElementCount < 1 || item.ElementBits is < 1 or > 16)
                        continue;

                    int totalBitsBefore = 0;
                    foreach (var prev in report.DataItems)
                    {
                        if (ReferenceEquals(prev, item))
                            break;
                        totalBitsBefore += prev.TotalBits;
                    }

                    int byteOffset = 1 + (totalBitsBefore / 8);
                    int bitShift = totalBitsBefore % 8;
                    int bitMask = (1 << Math.Min(item.ElementBits, 8)) - 1;

                    return new CachedFingerprint(
                        FingerprintKind.StandardInputBattery,
                        report.ReportID,
                        Math.Max(report.Length, inLen),
                        byteOffset,
                        bitShift,
                        bitMask,
                        item.LogicalMinimum,
                        item.LogicalMaximum > item.LogicalMinimum ? item.LogicalMaximum : 100);
                }
            }
        }
        catch
        {
            // Ignore unreadable descriptors
        }

        return new CachedFingerprint(FingerprintKind.None, 0, 0, 0, 0, 0, 0, 100);
    }

    private static DeviceBatteryInfo? PollRapoo8K(HidDevice device, CachedFingerprint fp)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                var buf = new byte[fp.InputLength];
                for (int i = 0; i < 3; i++)
                {
                    int read = stream.Read(buf, 0, buf.Length);
                    if (read <= 0)
                        break;

                    var parsed = ParseRapoo8KReport(buf.AsSpan(0, read));
                    if (parsed.HasValue)
                    {
                        string name = TryGetProductName(device) ?? $"8K Kablosuz Fare ({device.VendorID:X4}:{device.ProductID:X4})";
                        return new DeviceBatteryInfo
                        {
                            Id = $"fp-rapoo8k-{device.VendorID:X4}-{device.ProductID:X4}",
                            Name = name,
                            Category = DeviceCategory.Mouse,
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
            // Ignore read timeout (mouse idle)
        }

        return null;
    }

    private static DeviceBatteryInfo? PollStandardInputBattery(HidDevice device, CachedFingerprint fp)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 200;
                var buf = new byte[fp.InputLength];
                for (int i = 0; i < 2; i++)
                {
                    int read = stream.Read(buf, 0, buf.Length);
                    if (read <= fp.ByteOffset)
                        continue;

                    if (fp.ReportId != 0 && buf[0] != fp.ReportId)
                        continue;

                    int raw = (buf[fp.ByteOffset] >> fp.BitShift) & fp.BitMask;
                    int span = Math.Max(1, fp.LogicalMax - fp.LogicalMin);
                    int pct = Math.Clamp((int)Math.Round((raw - fp.LogicalMin) * 100.0 / span), 0, 100);

                    string name = TryGetProductName(device) ?? $"HID Cihazı ({device.VendorID:X4}:{device.ProductID:X4})";
                    if (name.Contains("ACPI", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Internal Battery", StringComparison.OrdinalIgnoreCase))
                        return null;

                    return new DeviceBatteryInfo
                    {
                        Id = $"fp-std-in-{device.VendorID:X4}-{device.ProductID:X4}",
                        Name = name,
                        Category = BluetoothBatteryProvider.GuessCategory(name),
                        Connection = ConnectionKind.Wireless24Ghz,
                        BatteryPercent = pct,
                        IsCharging = false,
                        IsConnected = true
                    };
                }
            }
        }
        catch
        {
            // Ignore timeout
        }

        return null;
    }

    /// <summary>
    /// Parses Rapoo / 8K wireless mouse input reports:
    /// byte[0] = 0x07, byte[7] = battery state (1=discharging, 2=charging), byte[8] = percentage (1..100).
    /// </summary>
    public static (int Percent, bool IsCharging)? ParseRapoo8KReport(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9 || data[0] != 0x07)
            return null;

        byte state = data[7];
        byte pct = data[8];
        if ((state == 1 || state == 2) && pct is >= 1 and <= 100)
            return (pct, state == 2);

        return null;
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
