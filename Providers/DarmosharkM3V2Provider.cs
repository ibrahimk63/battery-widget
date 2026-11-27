using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Reads battery status from Darmoshark M3 V2 (VID 0x0BDA / PID 0xFFE0, 2.4GHz receiver)
/// using the official Darmoshark Launcher WebHID protocol:
/// Output Report 0xB5, cmd 0x05 (or 0x06) -> Input Report 0xB6/0xB4, byte[20]: bit7 = charging, bits6..0 = percent.
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

    public string ProviderName => "Darmoshark M3 V2";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var candidates = DeviceList.Local.GetHidDevices()
                .Where(IsDarmosharkDevice)
                .OrderByDescending(d => d.GetMaxOutputReportLength() == 64 && d.GetMaxInputReportLength() == 64)
                .ThenByDescending(d => d.GetMaxOutputReportLength())
                .ToList();

            if (candidates.Count == 0)
                return results;

            foreach (var device in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = TryQueryDevice(device);
                if (parsed is not null)
                {
                    results.Add(parsed);
                    break;
                }
            }

            if (results.Count == 0 && candidates.Count > 0)
            {
                results.Add(new DeviceBatteryInfo
                {
                    Id = $"darmoshark-{candidates[0].VendorID:X4}-{candidates[0].ProductID:X4}",
                    Name = GetDisplayName(candidates[0]),
                    Category = DeviceCategory.Mouse,
                    Connection = ConnectionKind.Wireless24Ghz,
                    BatteryPercent = -1,
                    IsConnected = false,
                    StatusNote = "Uyku"
                });
            }

            return results;
        }, cancellationToken);
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

                // Official Darmoshark Launcher getBaseInfo(): Report ID 0xB5 (181), cmd 0x05 -> if gears > 5, cmd 0x06
                var parsed = QueryCommand(stream, 0xB5, 0x05);
                if (parsed is null)
                    parsed = QueryCommand(stream, 0xB5, 0x06);
                if (parsed is null)
                    parsed = QueryCommand(stream, 0xB3, 0x05);

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
        // Read up to 3 packets in case an asynchronous event packet (0xE2) arrives first
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
    /// Parses Darmoshark M3 V2 Realtek 64-byte HID responses.
    /// - Base info (0xB6/0xB4, cmd 0x05 or 0x06): byte[20] -> bit7 = charging, bits6..0 = percent (0..100).
    /// - Live update event (0xB6/0xB4, cmd 0xE2): byte[4] = charging state, byte[5] = percent.
    /// </summary>
    public static (int Percent, bool IsCharging)? ParseBatteryResponse(ReadOnlySpan<byte> data)
    {
        // Pattern 1: Realtek 64-byte Base Info (Report ID 0xB6 or 0xB4, cmd 0x05 or 0x06)
        if (data.Length >= 21 && (data[0] == 0xB6 || data[0] == 0xB4) && (data[1] == 0x05 || data[1] == 0x06))
        {
            byte powerByte = data[20];
            bool isCharging = (powerByte & 0x80) != 0;
            int percent = powerByte & 0x7F;
            if (percent is >= 0 and <= 100)
                return (percent, isCharging);
        }

        // Pattern 2: Live update event (CMD_MOUSE_ELE_MODE = 0xE2 / 226)
        if (data.Length >= 6 && (data[0] == 0xB6 || data[0] == 0xB4) && data[1] == 0xE2)
        {
            if (data[4] != 0xFF || data[5] != 0xFF)
            {
                bool isCharging = data[4] != 0;
                int percent = Math.Min((int)data[5], 100);
                return (percent, isCharging);
            }
        }

        // Pattern 3: Compx/Telink legacy format [0x08, 0x04, ..., percent at 6, charging at 7]
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
        return string.IsNullOrWhiteSpace(raw) ? "Darmoshark M3 V2" : raw;
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
