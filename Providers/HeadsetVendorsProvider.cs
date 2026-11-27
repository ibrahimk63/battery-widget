using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Reads battery status from SteelSeries wireless headsets/mice (VID 0x1038)
/// and ATK N9 / N9 Pro / N9 Ultra wireless headsets (VID 0x373B, PID 0x11CB).
/// </summary>
public sealed class HeadsetVendorsProvider : IBatteryProvider
{
    public const int SteelSeriesVendorId = 0x1038;
    public const int AtkVendorId = 0x373B;
    public const int AtkN9ProductId = 0x11CB;

    public string ProviderName => "SteelSeries & ATK N9 Kulaklıklar";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            results.AddRange(PollSteelSeries(cancellationToken));
            results.AddRange(PollAtkN9(cancellationToken));
            return results;
        }, cancellationToken);
    }

    private static List<DeviceBatteryInfo> PollSteelSeries(CancellationToken ct)
    {
        var results = new List<DeviceBatteryInfo>();
        var devices = DeviceList.Local.GetHidDevices(SteelSeriesVendorId)
            .Where(d => d.GetMaxOutputReportLength() >= 32 && d.GetMaxInputReportLength() >= 32)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            string key = $"{device.ProductID:X4}";
            if (seen.Contains(key))
                continue;

            try
            {
                if (!device.TryOpen(out var stream))
                    continue;

                using (stream)
                {
                    stream.ReadTimeout = 300;
                    stream.WriteTimeout = 300;

                    var parsed = TryQueryBytes(stream, device, [0x06, 0x18])
                              ?? TryQueryBytes(stream, device, [0xB0]);

                    if (parsed.HasValue)
                    {
                        seen.Add(key);
                        string name = TryGetProductName(device) ?? "SteelSeries Kablosuz";
                        results.Add(new DeviceBatteryInfo
                        {
                            Id = $"steelseries-{key}",
                            Name = name,
                            Category = BluetoothBatteryProvider.GuessCategory(name),
                            Connection = ConnectionKind.Wireless24Ghz,
                            BatteryPercent = parsed.Value.Percent,
                            IsCharging = parsed.Value.IsCharging,
                            IsConnected = true
                        });
                    }
                }
            }
            catch
            {
                // Ignore busy/unsupported interfaces
            }
        }

        return results;
    }

    private static List<DeviceBatteryInfo> PollAtkN9(CancellationToken ct)
    {
        var results = new List<DeviceBatteryInfo>();
        var devices = DeviceList.Local.GetHidDevices(AtkVendorId, AtkN9ProductId).ToList();
        if (devices.Count == 0)
            return results;

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                int outLen = device.GetMaxOutputReportLength();
                int inLen = device.GetMaxInputReportLength();
                int featLen = device.GetMaxFeatureReportLength();

                if (outLen < 8 && featLen < 8)
                    continue;

                if (!device.TryOpen(out var stream))
                    continue;

                using (stream)
                {
                    stream.ReadTimeout = 300;
                    stream.WriteTimeout = 300;

                    var desc = device.GetReportDescriptor();
                    foreach (var outRep in desc.OutputReports)
                    {
                        byte[][] probes =
                        [
                            [outRep.ReportID, 0x04],
                            [outRep.ReportID, 0x18],
                            [outRep.ReportID, 0x02],
                            [outRep.ReportID, 0x89]
                        ];

                        foreach (var cmd in probes)
                        {
                            var buf = new byte[Math.Max(outLen, outRep.Length)];
                            Array.Copy(cmd, buf, Math.Min(cmd.Length, buf.Length));
                            try
                            {
                                stream.Write(buf, 0, buf.Length);
                                if (inLen > 0)
                                {
                                    var inBuf = new byte[inLen];
                                    int read = stream.Read(inBuf, 0, inBuf.Length);
                                    if (read > 0)
                                    {
                                        var parsed = UniversalCompxProvider.ParseCompxResponse(inBuf.AsSpan(0, read))
                                                  ?? ParseSteelSeriesResponse(inBuf.AsSpan(0, read));
                                        if (parsed.HasValue)
                                        {
                                            results.Add(new DeviceBatteryInfo
                                            {
                                                Id = "atk-n9-373B-11CB",
                                                Name = TryGetProductName(device) ?? "ATK N9 Wireless",
                                                Category = DeviceCategory.Headset,
                                                Connection = ConnectionKind.Wireless24Ghz,
                                                BatteryPercent = parsed.Value.Percent,
                                                IsCharging = parsed.Value.IsCharging,
                                                IsConnected = true
                                            });
                                            return results;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Try next command
                            }
                        }
                    }
                }
            }
            catch
            {
                // Try next collection
            }
        }

        // Dongle is connected (serial/ATK HUB port may be holding telemetry)
        string fallbackName = TryGetProductName(devices[0]) ?? "ATK N9 Wireless";
        results.Add(new DeviceBatteryInfo
        {
            Id = "atk-n9-373B-11CB",
            Name = fallbackName,
            Category = DeviceCategory.Headset,
            Connection = ConnectionKind.Wireless24Ghz,
            BatteryPercent = -1,
            IsConnected = false,
            StatusNote = "ATK Hub / Uyku"
        });

        return results;
    }

    private static (int Percent, bool IsCharging)? TryQueryBytes(HidStream stream, HidDevice dev, byte[] cmd)
    {
        try
        {
            var req = new byte[dev.GetMaxOutputReportLength()];
            Array.Copy(cmd, req, Math.Min(cmd.Length, req.Length));
            stream.Write(req, 0, req.Length);

            var resp = new byte[dev.GetMaxInputReportLength()];
            int read = stream.Read(resp, 0, resp.Length);
            if (read > 0)
                return ParseSteelSeriesResponse(resp.AsSpan(0, read));
        }
        catch
        {
            // Ignore timeout
        }
        return null;
    }

    public static (int Percent, bool IsCharging)? ParseSteelSeriesResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        // Arctis Nova series: [0x06, 0x18, level(0..100 or 0..4), charging, ...]
        if (data[0] == 0x06 && data[1] == 0x18)
        {
            int raw = data[2];
            int pct = raw <= 4 ? raw * 25 : Math.Clamp(raw, 0, 100);
            bool chg = data[3] == 1 || data[3] == 3;
            return (pct, chg);
        }

        // Arctis 7/Pro series: [0xB0, ..., percent at 2, ...]
        if (data[0] == 0xB0 && data[2] is >= 0 and <= 100)
        {
            bool chg = data.Length > 3 && data[3] != 0;
            return (data[2], chg);
        }

        return null;
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
