using System.Collections.Concurrent;
using BatteryWidget.Models;
using HidSharp;
using HidSharp.Reports;

namespace BatteryWidget.Providers;

/// <summary>
/// Auto-discovers any standard USB/wireless HID device reporting a standard HID Battery Strength (0x06/0x20)
/// or Battery System Remaining Capacity / Absolute State of Charge (0x85/0x66, 0x85/0x65).
/// Skips devices handled by dedicated providers and internal ACPI/laptop batteries.
/// </summary>
public sealed class GenericHidBatteryProvider : IBatteryProvider
{
    private static readonly HashSet<int> DedicatedVendorIds = [0x03F0, 0x0BDA, 0x054C, 0x046D, 0x1532, 0x1038];
    private const uint UsageBatteryStrength = 0x00060020;       // Generic Device Controls -> Battery Strength
    private const uint UsageAbsoluteSoc = 0x00850065;           // Battery System -> Absolute State of Charge
    private const uint UsageRemainingCapacity = 0x00850066;     // Battery System -> Remaining Capacity

    private sealed record CachedHidBatteryTarget(
        string DevicePath,
        byte ReportId,
        int ReportLength,
        int ByteOffset,
        int BitShift,
        int BitMask,
        int LogicalMin,
        int LogicalMax,
        bool IsFeatureReport);

    private readonly ConcurrentDictionary<string, CachedHidBatteryTarget?> _descriptorCache = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "Standart USB/HID Pil Aygıtları";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var devices = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID > 0 && !DedicatedVendorIds.Contains(d.VendorID))
                .ToList();

            var activePaths = new HashSet<string>(devices.Select(d => d.DevicePath), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _descriptorCache.Keys)
            {
                if (!activePaths.Contains(key))
                    _descriptorCache.TryRemove(key, out _);
            }

            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = _descriptorCache.GetOrAdd(device.DevicePath, _ => InspectDescriptor(device));
                if (target is null || !target.IsFeatureReport)
                    continue;

                var pct = TryReadFeatureBattery(device, target);
                if (!pct.HasValue)
                    continue;

                string name = TryGetProductName(device) ?? $"HID Cihazı ({device.VendorID:X4}:{device.ProductID:X4})";
                if (IsInternalLaptopBattery(name))
                    continue;

                results.Add(new DeviceBatteryInfo
                {
                    Id = $"hid-std-{device.VendorID:X4}-{device.ProductID:X4}",
                    Name = name,
                    Category = BluetoothBatteryProvider.GuessCategory(name),
                    Connection = ConnectionKind.Wireless24Ghz,
                    BatteryPercent = pct.Value,
                    IsCharging = false,
                    IsConnected = true
                });
            }

            return results;
        }, cancellationToken);
    }

    private static CachedHidBatteryTarget? InspectDescriptor(HidDevice device)
    {
        try
        {
            if (device.GetMaxFeatureReportLength() <= 0)
                return null;

            var desc = device.GetReportDescriptor();
            foreach (var report in desc.FeatureReports)
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

                    // Offset 0 in HidSharp buffer is ReportID (even if ReportID == 0, length includes +1)
                    int byteOffset = 1 + (totalBitsBefore / 8);
                    int bitShift = totalBitsBefore % 8;
                    int bitMask = (1 << Math.Min(item.ElementBits, 8)) - 1;

                    return new CachedHidBatteryTarget(
                        device.DevicePath,
                        report.ReportID,
                        Math.Max(report.Length, device.GetMaxFeatureReportLength()),
                        byteOffset,
                        bitShift,
                        bitMask,
                        item.LogicalMinimum,
                        item.LogicalMaximum > item.LogicalMinimum ? item.LogicalMaximum : 100,
                        true);
                }
            }
        }
        catch
        {
            // Ignore unreadable descriptors
        }

        return null;
    }

    private static int? TryReadFeatureBattery(HidDevice device, CachedHidBatteryTarget target)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                var buf = new byte[target.ReportLength];
                buf[0] = target.ReportId;
                stream.GetFeature(buf);

                if (target.ByteOffset >= buf.Length)
                    return null;

                int raw = (buf[target.ByteOffset] >> target.BitShift) & target.BitMask;
                int span = Math.Max(1, target.LogicalMax - target.LogicalMin);
                int pct = Math.Clamp((int)Math.Round((raw - target.LogicalMin) * 100.0 / span), 0, 100);
                return pct;
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool IsInternalLaptopBattery(string name)
    {
        return name.Contains("ACPI", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Internal Battery", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("System Battery", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
