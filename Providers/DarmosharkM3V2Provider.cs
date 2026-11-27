using System.Collections.Concurrent;
using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Reads battery status from Darmoshark M3 V2 (VID 0x0BDA / PID 0xFFE0, 0xFFEA)
/// AND aggressively auto-discovers any Realtek-based 2.4GHz peripheral whose HID descriptor
/// exposes 64-byte Output Report ID 0xB5 and Input Report ID 0xB6/0xB4.
/// </summary>
public sealed class DarmosharkM3V2Provider : IBatteryProvider
{
    public static readonly (int Vid, int Pid)[] KnownPairs =
    [
        (0x0BDA, 0xFFE0), // Darmoshark M3 V2 2.4GHz Realtek receiver
        (0x0BDA, 0xFFEA), // Darmoshark M3 V2 wired USB mode
        (0x3554, 0xF5D5), // Compx M3 variant
        (0x248A, 0x8266)  // Telink M3 variant
    ];

    public static readonly int[] KnownVendorIds = [0x0BDA, 0x3554, 0x248A, 0x1EA7, 0x373B];
    private readonly ConcurrentDictionary<string, bool> _realtekB5Cache = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "Realtek / Darmoshark 2.4GHz HID";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var allDevices = DeviceList.Local.GetHidDevices().Where(d => d.VendorID > 0).ToList();

            var activePaths = new HashSet<string>(allDevices.Select(d => d.DevicePath), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _realtekB5Cache.Keys)
            {
                if (!activePaths.Contains(key))
                    _realtekB5Cache.TryRemove(key, out _);
            }

            var candidates = allDevices
                .Where(d => IsDarmosharkDevice(d) || IsRealtekB5Capable(d))
                .OrderByDescending(d => d.GetMaxOutputReportLength() == 64 && d.GetMaxInputReportLength() == 64)
                .ThenByDescending(d => d.GetMaxOutputReportLength())
                .ToList();

            if (candidates.Count == 0)
                return results;

            var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in candidates.GroupBy(d => $"{d.VendorID:X4}:{d.ProductID:X4}"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeviceBatteryInfo? parsed = null;

                foreach (var device in group)
                {
                    parsed = TryQueryDevice(device);
                    if (parsed is not null)
                        break;
                }

                if (parsed is not null)
                {
                    seenGroups.Add(group.Key);
                    results.Add(parsed);
                }
                else if (KnownPairs.Any(p => $"{p.Vid:X4}:{p.Pid:X4}" == group.Key))
                {
                    var first = group.First();
                    results.Add(new DeviceBatteryInfo
                    {
                        Id = $"darmoshark-{first.VendorID:X4}-{first.ProductID:X4}",
                        Name = GetDisplayName(first),
                        Category = DeviceCategory.Mouse,
                        Connection = ConnectionKind.Wireless24Ghz,
                        BatteryPercent = -1,
                        IsConnected = false,
                        StatusNote = "Uyku"
                    });
                }
            }

            return results;
        }, cancellationToken);
    }

    private bool IsRealtekB5Capable(HidDevice device)
    {
        if (device.GetMaxOutputReportLength() != 64 || device.GetMaxInputReportLength() != 64)
            return false;

        return _realtekB5Cache.GetOrAdd(device.DevicePath, _ =>
        {
            try
            {
                var desc = device.GetReportDescriptor();
                bool hasOutB5 = desc.OutputReports.Any(r => r.ReportID == 0xB5);
                bool hasInB6OrB4 = desc.InputReports.Any(r => r.ReportID == 0xB6 || r.ReportID == 0xB4);
                return hasOutB5 && hasInB6OrB4;
            }
            catch
            {
                return false;
            }
        });
    }

    private static bool IsDarmosharkDevice(HidDevice device)
    {
        if (KnownPairs.Any(p => p.Vid == device.VendorID && p.Pid == device.ProductID))
            return true;

        var name = TryGetProductName(device);
        if (!string.IsNullOrWhiteSpace(name) &&
            (name.Contains("Darmoshark", StringComparison.OrdinalIgnoreCase) ||
             (name.Contains("M3", StringComparison.OrdinalIgnoreCase) && KnownVendorIds.Contains(device.VendorID))))
        {
            return true;
        }
        return false;
    }

    private static DeviceBatteryInfo? TryQueryDevice(HidDevice device)
    {
        try
        {
            int outLen = device.GetMaxOutputReportLength();
            int inLen = device.GetMaxInputReportLength();
            if (outLen != 64 || inLen != 64)
                return null;

            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 500;
                stream.WriteTimeout = 500;

                // Official Darmoshark/Realtek getBaseInfo(): Report ID 0xB5 (181), cmd 0x05 -> cmd 0x06
                var parsed = QueryCommand(stream, 0xB5, 0x05)
                          ?? QueryCommand(stream, 0xB5, 0x06)
                          ?? QueryCommand(stream, 0xB3, 0x05);

                if (parsed.HasValue)
                    return BuildInfo(device, parsed.Value.Percent, parsed.Value.IsCharging);
            }
        }
        catch
        {
            // Ignore unreadable interfaces
        }

        return null;
    }

    private static (int Percent, bool IsCharging)? QueryCommand(HidStream stream, byte reportId, byte cmd)
    {
        var req = new byte[64];
        req[0] = reportId;
        req[1] = cmd;
        stream.Write(req, 0, req.Length);

        var resp = new byte[64];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int read = stream.Read(resp, 0, resp.Length);
            if (read <= 0)
                break;

            var parsed = ParseBatteryResponse(resp.AsSpan(0, read));
            if (parsed.HasValue)
                return parsed;
        }

        return null;
    }

    /// <summary>
    /// Parses Darmoshark M3 V2 / Realtek 64-byte HID responses.
    /// </summary>
    public static (int Percent, bool IsCharging)? ParseBatteryResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 21 && (data[0] == 0xB6 || data[0] == 0xB4) && (data[1] == 0x05 || data[1] == 0x06))
        {
            byte powerByte = data[20];
            bool isCharging = (powerByte & 0x80) != 0;
            int percent = powerByte & 0x7F;
            if (percent is >= 0 and <= 100)
                return (percent, isCharging);
        }

        if (data.Length >= 6 && (data[0] == 0xB6 || data[0] == 0xB4) && data[1] == 0xE2)
        {
            if (data[4] != 0xFF || data[5] != 0xFF)
            {
                bool isCharging = data[4] != 0;
                int percent = Math.Min((int)data[5], 100);
                return (percent, isCharging);
            }
        }

        if (data.Length >= 8 && data[0] == 0x08 && data[1] == 0x04)
        {
            int pct = data[6];
            if (pct is >= 0 and <= 100)
                return (pct, data[7] != 0);
        }

        return null;
    }

    private static DeviceBatteryInfo BuildInfo(HidDevice device, int percent, bool isCharging)
    {
        return new DeviceBatteryInfo
        {
            Id = $"darmoshark-{device.VendorID:X4}-{device.ProductID:X4}",
            Name = GetDisplayName(device),
            Category = DeviceCategory.Mouse,
            Connection = device.ProductID == 0xFFEA ? ConnectionKind.UsbWired : ConnectionKind.Wireless24Ghz,
            BatteryPercent = percent,
            IsCharging = isCharging,
            IsConnected = true
        };
    }

    private static string GetDisplayName(HidDevice device)
    {
        var raw = TryGetProductName(device);
        if (!string.IsNullOrWhiteSpace(raw))
            return raw;

        return device.VendorID == 0x0BDA && (device.ProductID == 0xFFE0 || device.ProductID == 0xFFEA)
            ? "Darmoshark M3 V2"
            : $"Realtek Kablosuz ({device.VendorID:X4}:{device.ProductID:X4})";
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
