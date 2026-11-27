using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Reads battery levels from Logitech wireless mice, keyboards, and headsets via HID++ 2.0
/// (Unified Battery 0x1004 and Battery Level Status 0x1000) across Lightspeed, Bolt, Unifying, and USB.
/// </summary>
public sealed class LogitechHidppProvider : IBatteryProvider
{
    public const int LogitechVendorId = 0x046D;

    public string ProviderName => "Logitech HID++ 2.0";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var devices = DeviceList.Local.GetHidDevices(LogitechVendorId)
                .Where(d => d.GetMaxOutputReportLength() >= 20 && d.GetMaxInputReportLength() >= 20)
                .ToList();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var desc = device.GetReportDescriptor();
                    if (!desc.OutputReports.Any(r => r.ReportID == 0x11))
                        continue;

                    if (!device.TryOpen(out var stream))
                        continue;

                    using (stream)
                    {
                        stream.ReadTimeout = 250;
                        stream.WriteTimeout = 250;

                        byte[] indices = [0xFF, 0x01, 0x02, 0x03];
                        foreach (byte devIdx in indices)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var parsed = TryQuerySlot(stream, devIdx);
                            if (parsed is null)
                                continue;

                            string baseName = TryGetProductName(device) ?? "Logitech Cihazı";
                            string id = $"logi-{device.ProductID:X4}-{devIdx:X2}";
                            if (!seen.Add(id))
                                continue;

                            results.Add(new DeviceBatteryInfo
                            {
                                Id = id,
                                Name = baseName,
                                Category = BluetoothBatteryProvider.GuessCategory(baseName),
                                Connection = ConnectionKind.Wireless24Ghz,
                                BatteryPercent = parsed.Value.Percent,
                                IsCharging = parsed.Value.IsCharging,
                                IsConnected = true
                            });

                            if (devIdx == 0xFF)
                                break;
                        }
                    }
                }
                catch
                {
                    // Skip busy or non-HID++ interfaces
                }
            }

            return results;
        }, cancellationToken);
    }

    private static (int Percent, bool IsCharging)? TryQuerySlot(HidStream stream, byte devIdx)
    {
        byte featureIdx = GetFeatureIndex(stream, devIdx, 0x10, 0x04);
        bool isUnified = true;

        if (featureIdx == 0)
        {
            featureIdx = GetFeatureIndex(stream, devIdx, 0x10, 0x00);
            isUnified = false;
        }

        if (featureIdx == 0)
            return null;

        // Function 0x00 on 0x1000 or Function 0x10 on 0x1004
        var req = new byte[20];
        req[0] = 0x11;
        req[1] = devIdx;
        req[2] = featureIdx;
        req[3] = (byte)(isUnified ? 0x10 : 0x00);

        stream.Write(req, 0, req.Length);

        var resp = new byte[20];
        for (int i = 0; i < 3; i++)
        {
            int read = stream.Read(resp, 0, resp.Length);
            if (read <= 0) break;
            if (resp[0] == 0x11 && resp[1] == devIdx && resp[2] == featureIdx)
                return ParseHidppBatteryResponse(resp.AsSpan(0, read), isUnified);
        }

        return null;
    }

    private static byte GetFeatureIndex(HidStream stream, byte devIdx, byte msb, byte lsb)
    {
        try
        {
            var req = new byte[20];
            req[0] = 0x11;
            req[1] = devIdx;
            req[2] = 0x00; // Root feature
            req[3] = 0x00; // GetFeature
            req[4] = msb;
            req[5] = lsb;
            stream.Write(req, 0, req.Length);

            var resp = new byte[20];
            for (int i = 0; i < 3; i++)
            {
                int read = stream.Read(resp, 0, resp.Length);
                if (read < 5) break;
                if (resp[0] == 0x11 && resp[1] == devIdx && resp[2] == 0x00)
                    return resp[4];
            }
        }
        catch
        {
            // Slot inactive or error
        }
        return 0;
    }

    public static (int Percent, bool IsCharging)? ParseHidppBatteryResponse(ReadOnlySpan<byte> data, bool isUnified)
    {
        if (data.Length < 7 || data[0] != 0x11)
            return null;

        int percent = data[4];
        if (percent is < 0 or > 100)
            return null;

        int statusCode = isUnified ? data[6] : data[5];
        bool isCharging = statusCode is 1 or 2;
        return (percent, isCharging);
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
