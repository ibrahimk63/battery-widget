using System.Collections.Concurrent;
using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Supports the wider Wraith Esports & 8K peripheral ecosystem:
/// - WLMouse (VID 0x36A7, Beast X / Mini / Sword X) via 65B Feature Report (0x83)
/// - Evision / NearLink 8K & HFD Wireless Keyboards (Report ID 0x05, 64B Output/Input)
/// - Hechi 8K Mice (Report ID 0x0B, 64B Output/Input)
/// - MCHOSE / Ninjutso / YJX (VID 0xA8A5 / AA 30 Feature/Output)
/// </summary>
public sealed class WraithEcosystemProvider : IBatteryProvider
{
    public const int WlmouseVendorId = 0x36A7;
    public const int YjxVendorId = 0xA8A5;

    private enum ChipsetFamily
    {
        None,
        WlmouseFeature,
        EvisionReport05,
        HechiReport0B,
        YjxAA30
    }

    private sealed record CachedEndpoint(ChipsetFamily Family, int InLen, int OutLen, int FeatLen);

    private readonly ConcurrentDictionary<string, CachedEndpoint> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "Wraith Ekosistemi (WLMouse, Evision/Hechi 8K, MCHOSE, Ninjutso)";

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

                var ep = _cache.GetOrAdd(device.DevicePath, _ => InspectEndpoint(device));
                if (ep.Family == ChipsetFamily.None)
                    continue;

                string devKey = $"{device.VendorID:X4}:{device.ProductID:X4}";
                if (seen.Contains(devKey))
                    continue;

                DeviceBatteryInfo? info = ep.Family switch
                {
                    ChipsetFamily.WlmouseFeature => PollWlmouse(device, ep),
                    ChipsetFamily.EvisionReport05 => PollEvision(device, ep),
                    ChipsetFamily.HechiReport0B => PollHechi(device, ep),
                    ChipsetFamily.YjxAA30 => PollYjx(device, ep),
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

    private static CachedEndpoint InspectEndpoint(HidDevice device)
    {
        try
        {
            int inLen = device.GetMaxInputReportLength();
            int outLen = device.GetMaxOutputReportLength();
            int featLen = device.GetMaxFeatureReportLength();

            if (device.VendorID == WlmouseVendorId && featLen >= 65)
                return new CachedEndpoint(ChipsetFamily.WlmouseFeature, inLen, outLen, featLen);

            if (device.VendorID == YjxVendorId && (featLen >= 64 || outLen >= 64))
                return new CachedEndpoint(ChipsetFamily.YjxAA30, inLen, outLen, featLen);

            var desc = device.GetReportDescriptor();

            if (outLen >= 64 && inLen >= 64 && desc.OutputReports.Any(r => r.ReportID == 0x05))
                return new CachedEndpoint(ChipsetFamily.EvisionReport05, inLen, outLen, featLen);

            if (outLen >= 32 && inLen >= 32 && desc.OutputReports.Any(r => r.ReportID == 0x0B))
                return new CachedEndpoint(ChipsetFamily.HechiReport0B, inLen, outLen, featLen);
        }
        catch
        {
            // Ignore unreadable descriptors
        }

        return new CachedEndpoint(ChipsetFamily.None, 0, 0, 0);
    }

    private static DeviceBatteryInfo? PollWlmouse(HidDevice device, CachedEndpoint ep)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                var req = new byte[ep.FeatLen];
                req[0] = 0x00;
                req[6] = 0x83;
                stream.SetFeature(req);
                Thread.Sleep(20);

                var resp = new byte[ep.FeatLen];
                resp[0] = 0x00;
                stream.GetFeature(resp);

                var parsed = ParseWlmouseFeatureResponse(resp);
                if (parsed.HasValue)
                {
                    string name = TryGetProductName(device) ?? $"WLMouse ({device.ProductID:X4})";
                    return new DeviceBatteryInfo
                    {
                        Id = $"wlmouse-{device.ProductID:X4}",
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
        catch
        {
            // Ignore
        }
        return null;
    }

    private static DeviceBatteryInfo? PollEvision(HidDevice device, CachedEndpoint ep)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                stream.WriteTimeout = 250;

                // Try Evision 8K (0x1A) and HFD Keyboard (0x30) power info opcodes on Report ID 0x05
                byte[][] queries =
                [
                    [0x05, 0x00, 0x00, 0x1A],
                    [0x05, 0x00, 0x00, 0x30]
                ];

                foreach (var q in queries)
                {
                    var req = new byte[ep.OutLen];
                    Array.Copy(q, req, q.Length);
                    try
                    {
                        stream.Write(req, 0, req.Length);
                        var resp = new byte[ep.InLen];
                        int read = stream.Read(resp, 0, resp.Length);
                        if (read > 0)
                        {
                            var parsed = ParseEvisionBatteryResponse(resp.AsSpan(0, read));
                            if (parsed.HasValue)
                            {
                                string name = TryGetProductName(device) ?? $"8K Kablosuz ({device.VendorID:X4}:{device.ProductID:X4})";
                                return new DeviceBatteryInfo
                                {
                                    Id = $"evision-{device.VendorID:X4}-{device.ProductID:X4}",
                                    Name = name,
                                    Category = GuessWraithCategory(name),
                                    Connection = ConnectionKind.Wireless24Ghz,
                                    BatteryPercent = parsed.Value.Percent,
                                    IsCharging = parsed.Value.IsCharging,
                                    IsConnected = true
                                };
                            }
                        }
                    }
                    catch { /* try next opcode */ }
                }
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    private static DeviceBatteryInfo? PollHechi(HidDevice device, CachedEndpoint ep)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                stream.WriteTimeout = 250;

                var req = new byte[ep.OutLen];
                req[0] = 0x0B;
                req[1] = 0x01;
                stream.Write(req, 0, req.Length);

                var resp = new byte[ep.InLen];
                for (int i = 0; i < 2; i++)
                {
                    int read = stream.Read(resp, 0, resp.Length);
                    if (read <= 0) break;

                    var parsed = ParseHechiBatteryResponse(resp.AsSpan(0, read));
                    if (parsed.HasValue)
                    {
                        string name = TryGetProductName(device) ?? $"8K Fare ({device.VendorID:X4}:{device.ProductID:X4})";
                        return new DeviceBatteryInfo
                        {
                            Id = $"hechi-{device.VendorID:X4}-{device.ProductID:X4}",
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
            // Ignore
        }
        return null;
    }

    private static DeviceBatteryInfo? PollYjx(HidDevice device, CachedEndpoint ep)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                if (ep.FeatLen >= 64)
                {
                    var buf = new byte[ep.FeatLen];
                    buf[0] = 0x00;
                    buf[1] = 0xAA;
                    buf[2] = 0x30;
                    stream.SetFeature(buf);
                    Thread.Sleep(20);
                    stream.GetFeature(buf);

                    for (int i = 3; i < Math.Min(buf.Length, 16); i++)
                    {
                        if (buf[1] == 0xAA && buf[i] is >= 1 and <= 100)
                        {
                            string name = TryGetProductName(device) ?? $"Kablosuz Fare ({device.VendorID:X4}:{device.ProductID:X4})";
                            return new DeviceBatteryInfo
                            {
                                Id = $"yjx-{device.VendorID:X4}-{device.ProductID:X4}",
                                Name = name,
                                Category = GuessWraithCategory(name),
                                Connection = ConnectionKind.Wireless24Ghz,
                                BatteryPercent = buf[i],
                                IsCharging = false,
                                IsConnected = true
                            };
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    public static (int Percent, bool IsCharging)? ParseWlmouseFeatureResponse(ReadOnlySpan<byte> data)
    {
        // WLMouse 65B Feature Report: byte[6] == 0x83 or byte[7] == 0x83 -> percent at 8 (or 9), charging at 7 (or 8)
        if (data.Length >= 10 && data[6] == 0x83 && data[8] is >= 1 and <= 100)
            return (data[8], data[7] != 0);

        if (data.Length >= 11 && data[7] == 0x83 && data[9] is >= 1 and <= 100)
            return (data[9], data[8] != 0);

        return null;
    }

    public static (int Percent, bool IsCharging)? ParseEvisionBatteryResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 9 && data[0] == 0x05 && (data[3] == 0x1A || data[3] == 0x30))
        {
            int pct = data[7];
            if (pct is >= 1 and <= 100)
                return (pct, data[8] > 0);
        }
        return null;
    }

    public static (int Percent, bool IsCharging)? ParseHechiBatteryResponse(ReadOnlySpan<byte> data)
    {
        // Hechi Report ID 0x0B: batteryCharge at offset 14 (baseOffset+13), batteryLevel at offset 15 (baseOffset+14)
        if (data.Length >= 16 && data[0] == 0x0B && data[15] is >= 1 and <= 100 && data[14] <= 2)
            return (data[15], data[14] == 1);

        // Compact 0x0B fallback: percent at byte 3, charging at byte 4
        if (data.Length >= 6 && data[0] == 0x0B && data[3] is >= 1 and <= 100 && data[4] <= 2)
            return (data[3], data[4] == 1);

        return null;
    }

    private static DeviceCategory GuessWraithCategory(string name)
    {
        var g = BluetoothBatteryProvider.GuessCategory(name);
        return g != DeviceCategory.Other ? g : DeviceCategory.Mouse;
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
