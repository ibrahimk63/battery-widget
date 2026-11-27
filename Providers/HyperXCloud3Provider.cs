using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Reads battery status from HyperX Cloud III Wireless (HP/HyperX VID 0x03F0, PID 0x05B7/0x0FCC) over USB HID.
/// Supports the 62-byte vendor collection protocol (Report ID 0x66, commands 0x89 battery / 0x8A charging).
/// </summary>
public sealed class HyperXCloud3Provider : IBatteryProvider
{
    public const int VendorId = 0x03F0;
    public static readonly int[] KnownProductIds = [0x05B7, 0x0FCC, 0x098D];

    public string ProviderName => "HyperX Cloud III Wireless";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var devices = DeviceList.Local.GetHidDevices(VendorId)
                .Where(d => KnownProductIds.Contains(d.ProductID) ||
                            (TryGetProductName(d)?.Contains("Cloud III", StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();

            if (devices.Count == 0)
                return results;

            // Prioritize the 62-byte vendor-defined control interface (In=62, Out=62)
            var ordered = devices
                .OrderByDescending(d => d.GetMaxOutputReportLength() == 62 && d.GetMaxInputReportLength() == 62)
                .ThenByDescending(d => d.GetMaxOutputReportLength())
                .ToList();

            foreach (var device in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = TryQueryDevice(device);
                if (parsed is not null)
                {
                    results.Add(parsed);
                    break;
                }
            }

            // Dongle is connected, but headset is asleep or powered off
            if (results.Count == 0 && ordered.Count > 0)
            {
                results.Add(new DeviceBatteryInfo
                {
                    Id = $"hyperx-{ordered[0].VendorID:X4}-{ordered[0].ProductID:X4}",
                    Name = GetDisplayName(ordered[0]),
                    Category = DeviceCategory.Headset,
                    Connection = ConnectionKind.Wireless24Ghz,
                    BatteryPercent = -1,
                    IsConnected = false,
                    StatusNote = "Uyku"
                });
            }

            return results;
        }, cancellationToken);
    }

    private static DeviceBatteryInfo? TryQueryDevice(HidDevice device)
    {
        try
        {
            int outLen = device.GetMaxOutputReportLength();
            int inLen = device.GetMaxInputReportLength();
            if (outLen < 16 || inLen < 16)
                return null;

            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 400;
                stream.WriteTimeout = 400;

                // 1) Primary Cloud III Wireless protocol: Report ID 0x66, Command 0x89 (battery)
                var req = new byte[outLen];
                req[0] = 0x66;
                req[1] = 0x89;
                stream.Write(req, 0, req.Length);

                var resp = new byte[inLen];
                int read = stream.Read(resp, 0, resp.Length);
                if (read > 0)
                {
                    var parsed = ParseBatteryResponse(resp.AsSpan(0, read));
                    if (parsed.HasValue)
                    {
                        bool isCharging = parsed.Value.IsCharging;
                        // Query charging state (0x66, 0x8A) if not already known
                        try
                        {
                            var chgReq = new byte[outLen];
                            chgReq[0] = 0x66;
                            chgReq[1] = 0x8A;
                            stream.Write(chgReq, 0, chgReq.Length);
                            var chgResp = new byte[inLen];
                            int chgRead = stream.Read(chgResp, 0, chgResp.Length);
                            if (chgRead >= 5 && chgResp[0] == 0x66 && chgResp[1] == 0x8A)
                                isCharging = chgResp[4] != 0 || chgResp[2] == 1;
                        }
                        catch
                        {
                            // Optional charging query
                        }

                        return new DeviceBatteryInfo
                        {
                            Id = $"hyperx-{device.VendorID:X4}-{device.ProductID:X4}",
                            Name = GetDisplayName(device),
                            Category = DeviceCategory.Headset,
                            Connection = ConnectionKind.Wireless24Ghz,
                            BatteryPercent = parsed.Value.Percent,
                            IsCharging = isCharging,
                            IsConnected = true
                        };
                    }
                }
            }
        }
        catch
        {
            // Ignore unreadable HID interfaces
        }

        return null;
    }

    /// <summary>
    /// Parses HyperX Cloud III Wireless HID battery response packets.
    /// </summary>
    public static (int Percent, bool IsCharging)? ParseBatteryResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return null;

        // Pattern 1 (Cloud III Wireless 62-byte): [0x66, 0x89, ..., percent at index 4 (or 3)]
        if (data[0] == 0x66 && data[1] == 0x89)
        {
            int pct = data[4];
            if (pct is >= 1 and <= 100)
                return (pct, data.Length > 5 && data[5] == 1);

            if (data[3] is >= 1 and <= 100)
                return (data[3], data.Length > 4 && data[4] == 1);
        }

        // Pattern 2 (Legacy / NGENUITY): [0x06, 0xFF, 0xBB, 0x02, ..., percent, charging]
        for (int i = 0; i < data.Length - 4; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xBB && data[i + 2] == 0x02)
            {
                int percentIdx = i + 4;
                if (percentIdx < data.Length)
                {
                    int pct = data[percentIdx];
                    if (pct is >= 0 and <= 100)
                    {
                        bool charging = (percentIdx + 1 < data.Length) && (data[percentIdx + 1] == 1 || data[percentIdx + 1] == 2);
                        return (pct, charging);
                    }
                }
            }
        }

        return null;
    }

    private static string GetDisplayName(HidDevice device)
    {
        var raw = TryGetProductName(device);
        return string.IsNullOrWhiteSpace(raw) ? "HyperX Cloud III" : raw;
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
