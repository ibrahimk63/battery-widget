using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Reads battery percentage and charging status from Razer wireless mice, keyboards, and headsets
/// (VID 0x1532) using the 90-byte Razer Feature Report protocol.
/// </summary>
public sealed class RazerBatteryProvider : IBatteryProvider
{
    public const int RazerVendorId = 0x1532;

    public string ProviderName => "Razer Wireless HID";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            var devices = DeviceList.Local.GetHidDevices(RazerVendorId)
                .Where(d => d.GetMaxFeatureReportLength() >= 90)
                .ToList();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = $"{device.VendorID:X4}:{device.ProductID:X4}";
                if (seen.Contains(key))
                    continue;

                var info = TryQueryRazer(device);
                if (info is not null)
                {
                    seen.Add(key);
                    results.Add(info);
                }
            }

            return results;
        }, cancellationToken);
    }

    private static DeviceBatteryInfo? TryQueryRazer(HidDevice device)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 300;
                int featLen = device.GetMaxFeatureReportLength();
                bool hasReportIdPrefix = featLen >= 91;

                var batResp = SendRazerFeatureCommand(stream, featLen, hasReportIdPrefix, 0x07, 0x80, 0x02);
                if (batResp is null)
                    return null;

                var pct = ParseRazerBatteryPercent(batResp);
                if (!pct.HasValue)
                    return null;

                bool isCharging = false;
                var chgResp = SendRazerFeatureCommand(stream, featLen, hasReportIdPrefix, 0x07, 0x84, 0x02);
                if (chgResp is { Length: >= 10 })
                    isCharging = chgResp[9] != 0;

                string name = TryGetProductName(device) ?? $"Razer ({device.ProductID:X4})";
                return new DeviceBatteryInfo
                {
                    Id = $"razer-{device.ProductID:X4}",
                    Name = name,
                    Category = BluetoothBatteryProvider.GuessCategory(name),
                    Connection = ConnectionKind.Wireless24Ghz,
                    BatteryPercent = pct.Value,
                    IsCharging = isCharging,
                    IsConnected = true
                };
            }
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? SendRazerFeatureCommand(
        HidStream stream,
        int featLen,
        bool hasReportIdPrefix,
        byte cmdClass,
        byte cmdId,
        byte dataSize)
    {
        var payload = new byte[90];
        payload[0] = 0x00; // Status: new command
        payload[1] = 0x1F; // Transaction ID
        payload[5] = dataSize;
        payload[6] = cmdClass;
        payload[7] = cmdId;
        payload[88] = ComputeRazerChecksum(payload);

        var buf = new byte[featLen];
        if (hasReportIdPrefix)
        {
            buf[0] = 0x00;
            Array.Copy(payload, 0, buf, 1, 90);
        }
        else
        {
            Array.Copy(payload, 0, buf, 0, 90);
        }

        stream.SetFeature(buf);
        Thread.Sleep(25);
        stream.GetFeature(buf);

        var resp = new byte[90];
        Array.Copy(buf, hasReportIdPrefix ? 1 : 0, resp, 0, 90);
        return resp[0] == 0x02 ? resp : null;
    }

    public static byte ComputeRazerChecksum(ReadOnlySpan<byte> payload90)
    {
        byte crc = 0;
        int end = Math.Min(88, payload90.Length);
        for (int i = 2; i < end; i++)
            crc ^= payload90[i];
        return crc;
    }

    public static int? ParseRazerBatteryPercent(ReadOnlySpan<byte> response90)
    {
        if (response90.Length < 10 || response90[0] != 0x02)
            return null;

        int raw = response90[9];
        return Math.Clamp((int)Math.Round(raw * 100.0 / 255.0), 0, 100);
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
