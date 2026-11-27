using System.Collections.Concurrent;
using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Supports verified open-source wireless peripheral battery protocols without requiring physical device calibration:
/// - Corsair (Void / HS70 / HS80 / Virtuoso, VID 0x1B1C)
/// - HyperX Extended Family (Cloud II Wireless, Cloud Flight / S, Cloud Alpha Wireless, VID 0x0951 / 0x03F0)
/// - Pulsar & Scyrox (17B CompX 0x08 0x03 + 0x55 checksum)
/// - Ajazz AJ Series (0xFF00 64B Feature 0x05 0x40 + SumMod256 checksum)
/// - ASUS ROG Wireless (VID 0x0B05)
/// - Roccat Wireless (VID 0x1E7D)
/// </summary>
public sealed class OpenSourcePeripheralsProvider : IBatteryProvider
{
    public const int CorsairVendorId = 0x1B1C;
    public const int KingstonHyperXVendorId = 0x0951;
    public const int HpHyperXVendorId = 0x03F0;
    public const int AsusRogVendorId = 0x0B05;
    public const int RoccatVendorId = 0x1E7D;

    private static readonly HashSet<int> Cloud3Pids = [0x05B7, 0x0FCC, 0x098D];
    private static readonly HashSet<int> PulsarScyroxVids = [0x3710, 0x25A7, 0x3554];
    private static readonly HashSet<int> AjazzVids = [0x0C45, 0x1D57, 0x320F, 0x3554];

    private enum ProtocolFamily
    {
        None,
        Corsair,
        HyperXExtended,
        PulsarScyrox,
        AjazzFeature,
        AsusRog,
        Roccat
    }

    private sealed record CachedEndpoint(ProtocolFamily Family, int InLen, int OutLen, int FeatLen);

    private readonly ConcurrentDictionary<string, CachedEndpoint> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "Açık Kaynak Cihaz Paketi (Corsair, HyperX, Pulsar, Ajazz, ASUS, Roccat)";

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
                if (ep.Family == ProtocolFamily.None)
                    continue;

                string devKey = $"{device.VendorID:X4}:{device.ProductID:X4}";
                if (seen.Contains(devKey))
                    continue;

                DeviceBatteryInfo? info = ep.Family switch
                {
                    ProtocolFamily.Corsair => PollCorsair(device, ep),
                    ProtocolFamily.HyperXExtended => PollHyperXExtended(device, ep),
                    ProtocolFamily.PulsarScyrox => PollPulsar(device, ep),
                    ProtocolFamily.AjazzFeature => PollAjazz(device, ep),
                    ProtocolFamily.AsusRog => PollAsusRog(device, ep),
                    ProtocolFamily.Roccat => PollRoccat(device, ep),
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

            if (device.VendorID == CorsairVendorId && (outLen >= 16 || featLen >= 16) && inLen >= 16)
                return new CachedEndpoint(ProtocolFamily.Corsair, inLen, outLen, featLen);

            if ((device.VendorID == KingstonHyperXVendorId ||
                 (device.VendorID == HpHyperXVendorId && !Cloud3Pids.Contains(device.ProductID))) &&
                outLen >= 16 && inLen >= 16)
            {
                return new CachedEndpoint(ProtocolFamily.HyperXExtended, inLen, outLen, featLen);
            }

            if (device.VendorID == AsusRogVendorId && outLen >= 16 && inLen >= 16)
                return new CachedEndpoint(ProtocolFamily.AsusRog, inLen, outLen, featLen);

            if (device.VendorID == RoccatVendorId && outLen >= 8 && inLen >= 8)
                return new CachedEndpoint(ProtocolFamily.Roccat, inLen, outLen, featLen);

            string name = TryGetProductName(device) ?? string.Empty;
            var desc = device.GetReportDescriptor();

            if ((PulsarScyroxVids.Contains(device.VendorID) ||
                 name.Contains("Pulsar", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Scyrox", StringComparison.OrdinalIgnoreCase)) &&
                outLen >= 17 && inLen >= 17 && desc.OutputReports.Any(r => r.ReportID == 0x08))
            {
                return new CachedEndpoint(ProtocolFamily.PulsarScyrox, inLen, outLen, featLen);
            }

            if ((AjazzVids.Contains(device.VendorID) ||
                 name.Contains("Ajazz", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("AJ159", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("AJ199", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("AJ339", StringComparison.OrdinalIgnoreCase)) &&
                featLen >= 64)
            {
                return new CachedEndpoint(ProtocolFamily.AjazzFeature, inLen, outLen, featLen);
            }
        }
        catch
        {
            // Ignore unreadable descriptors
        }

        return new CachedEndpoint(ProtocolFamily.None, 0, 0, 0);
    }

    private static DeviceBatteryInfo? PollCorsair(HidDevice device, CachedEndpoint ep)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                stream.WriteTimeout = 250;

                byte[][] queries =
                [
                    [0xC9, 0x64],
                    [0x00, 0x08, 0x02, 0x0F]
                ];

                foreach (var q in queries)
                {
                    if (TryWriteAndRead(stream, q, ep.OutLen, ep.InLen, ParseCorsairBatteryResponse) is { } parsed)
                    {
                        string name = TryGetProductName(device) ?? $"Corsair Kablosuz ({device.ProductID:X4})";
                        return BuildInfo($"corsair-{device.ProductID:X4}", name, GuessCategoryOrDefault(name, DeviceCategory.Headset), parsed);
                    }
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static DeviceBatteryInfo? PollHyperXExtended(HidDevice device, CachedEndpoint ep)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                stream.WriteTimeout = 250;

                byte[][] queries =
                [
                    [0x0C, 0x02, 0x03, 0x01, 0x00, 0x06],
                    [0x21, 0xFF, 0x05],
                    [0x06, 0xFF, 0xBB, 0x02]
                ];

                foreach (var q in queries)
                {
                    if (TryWriteAndRead(stream, q, ep.OutLen, ep.InLen, ParseHyperXExtendedResponse) is { } parsed)
                    {
                        string name = TryGetProductName(device) ?? $"HyperX Kablosuz ({device.ProductID:X4})";
                        return BuildInfo($"hyperx-ext-{device.VendorID:X4}-{device.ProductID:X4}", name, DeviceCategory.Headset, parsed);
                    }
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static DeviceBatteryInfo? PollPulsar(HidDevice device, CachedEndpoint ep)
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
                req[0] = 0x08;
                req[1] = 0x03;
                int cksumIdx = Math.Min(16, req.Length - 1);
                int sum = 0;
                for (int i = 0; i < cksumIdx; i++)
                    sum += req[i];
                req[cksumIdx] = (byte)((0x55 - sum) & 0xFF);

                stream.Write(req, 0, req.Length);
                var resp = new byte[ep.InLen];
                int read = stream.Read(resp, 0, resp.Length);
                if (read > 0 && ParsePulsarBatteryResponse(resp.AsSpan(0, read)) is { } parsed)
                {
                    string name = TryGetProductName(device) ?? $"Pulsar / Scyrox ({device.VendorID:X4}:{device.ProductID:X4})";
                    return BuildInfo($"pulsar-{device.VendorID:X4}-{device.ProductID:X4}", name, DeviceCategory.Mouse, parsed);
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static DeviceBatteryInfo? PollAjazz(HidDevice device, CachedEndpoint ep)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                stream.WriteTimeout = 250;

                if (ep.OutLen >= 2)
                {
                    var wake = new byte[ep.OutLen];
                    wake[1] = 0xF7;
                    try
                    {
                        stream.Write(wake, 0, wake.Length);
                        Thread.Sleep(15);
                    }
                    catch { /* optional wakeup */ }
                }

                var req = new byte[ep.FeatLen];
                req[0] = 0x05;
                req[1] = 0x40;
                req[2] = 0x00;
                req[3] = 0x01;
                int sum = 0;
                for (int i = 1; i < req.Length - 1; i++)
                    sum += req[i];
                req[^1] = (byte)(sum & 0xFF);

                try { stream.SetFeature(req); Thread.Sleep(15); } catch { /* ignore */ }
                var resp = new byte[ep.FeatLen];
                resp[0] = 0x05;
                stream.GetFeature(resp);

                if (ParseAjazzBatteryResponse(resp) is { } parsed)
                {
                    string name = TryGetProductName(device) ?? $"Ajazz ({device.VendorID:X4}:{device.ProductID:X4})";
                    return BuildInfo($"ajazz-{device.VendorID:X4}-{device.ProductID:X4}", name, DeviceCategory.Mouse, parsed);
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static DeviceBatteryInfo? PollAsusRog(HidDevice device, CachedEndpoint ep)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                stream.WriteTimeout = 250;

                if (TryWriteAndRead(stream, [0x12, 0x07], ep.OutLen, ep.InLen, ParseAsusRogBatteryResponse) is { } parsed)
                {
                    string name = TryGetProductName(device) ?? $"ASUS ROG ({device.ProductID:X4})";
                    return BuildInfo($"asus-{device.ProductID:X4}", name, GuessCategoryOrDefault(name, DeviceCategory.Headset), parsed);
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static DeviceBatteryInfo? PollRoccat(HidDevice device, CachedEndpoint ep)
    {
        try
        {
            if (!device.TryOpen(out var stream))
                return null;

            using (stream)
            {
                stream.ReadTimeout = 250;
                stream.WriteTimeout = 250;

                if (TryWriteAndRead(stream, [0xFF, 0x02], ep.OutLen, ep.InLen, ParseRoccatBatteryResponse) is { } parsed)
                {
                    string name = TryGetProductName(device) ?? $"Roccat Kablosuz ({device.ProductID:X4})";
                    return BuildInfo($"roccat-{device.ProductID:X4}", name, GuessCategoryOrDefault(name, DeviceCategory.Headset), parsed);
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private delegate (int Percent, bool IsCharging)? ResponseParser(ReadOnlySpan<byte> data);

    private static (int Percent, bool IsCharging)? TryWriteAndRead(
        HidStream stream,
        byte[] prefix,
        int outLen,
        int inLen,
        ResponseParser parser)
    {
        try
        {
            var req = new byte[Math.Max(outLen, prefix.Length)];
            Array.Copy(prefix, req, prefix.Length);
            stream.Write(req, 0, req.Length);

            var resp = new byte[inLen];
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int read = stream.Read(resp, 0, resp.Length);
                if (read <= 0)
                    break;

                var parsed = parser(resp.AsSpan(0, read));
                if (parsed.HasValue)
                    return parsed;
            }
        }
        catch
        {
            // Ignore write/read timeouts
        }

        return null;
    }

    public static (int Percent, bool IsCharging)? ParseCorsairBatteryResponse(ReadOnlySpan<byte> data)
    {
        // Void / HS70: [0x64/0xC9, ?, level_with_chg_bit, ?, state(0=off,1=connected,4/5=charging)]
        if (data.Length >= 5 && (data[0] == 0x64 || data[0] == 0xC9))
        {
            byte state = data[4];
            if (state == 0x00)
                return null;

            byte raw = data[2];
            bool chg = (raw & 0x80) != 0 || state is 0x04 or 0x05;
            int pct = raw & 0x7F;
            if (pct is >= 0 and <= 100)
                return (pct, chg);
        }

        // Slipstream / HS80 / Virtuoso (16-bit per-mille scaled 0..1000)
        if (data.Length >= 6 && data[1] == 0x0F)
        {
            int perMille = data[4] | (data[5] << 8);
            if (perMille is >= 10 and <= 1000)
            {
                bool chg = data.Length > 6 && data[6] == 1;
                return (Math.Clamp((int)Math.Round(perMille / 10.0), 1, 100), chg);
            }
        }

        if (data.Length >= 5 && data[2] == 0x0F)
        {
            int perMille = data[3] | (data[4] << 8);
            if (perMille is >= 10 and <= 1000)
            {
                bool chg = data.Length > 5 && data[5] == 1;
                return (Math.Clamp((int)Math.Round(perMille / 10.0), 1, 100), chg);
            }
        }

        return null;
    }

    public static (int Percent, bool IsCharging)? ParseHyperXExtendedResponse(ReadOnlySpan<byte> data)
    {
        // Cloud Alpha Wireless / Modern 64B: [0x0C, 0x02, 0x03, ..., percent at 6, chg at 7]
        if (data.Length >= 8 && data[0] == 0x0C && data[1] == 0x02 && data[2] == 0x03)
        {
            int pct = data[6];
            if (pct is >= 1 and <= 100)
                return (pct, data[7] == 1);
        }

        // Cloud II Wireless: [0x06, 0xFF, 0xBB, 0x02, ..., chg at 6, percent at 7] or [..., percent at 5, chg at 6]
        if (data.Length >= 8 && data[0] == 0x06 && data[1] == 0xFF && data[2] == 0xBB && data[3] == 0x02)
        {
            if (data[7] is >= 1 and <= 100)
                return (data[7], data[6] == 1);
            if (data[5] is >= 1 and <= 100)
                return (data[5], data[6] == 1);
        }

        // Cloud Flight: [0x21, 0xFF, 0x05, status(0x10=charging), percent at 4]
        if (data.Length >= 5 && data[0] == 0x21 && data[1] == 0xFF && data[2] == 0x05)
        {
            int pct = data[4];
            if (pct is >= 1 and <= 100)
                return (pct, data[3] == 0x10);
        }

        return null;
    }

    public static (int Percent, bool IsCharging)? ParsePulsarBatteryResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x08 && data[1] == 0x03)
        {
            int pct = data[6];
            if (pct is >= 1 and <= 100)
                return (pct, data[7] == 1);
        }

        return null;
    }

    public static (int Percent, bool IsCharging)? ParseAjazzBatteryResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 6 && (data[0] == 0x05 || data[0] == 0x00) && data[1] == 0x40)
        {
            int pct = data[4];
            if (pct is >= 1 and <= 100)
                return (pct, data[5] == 1);
        }

        return null;
    }

    public static (int Percent, bool IsCharging)? ParseAsusRogBatteryResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 5 && data[0] == 0x12 && data[1] == 0x07)
        {
            int pct = data[4] is >= 1 and <= 100
                ? data[4]
                : (data[2] is >= 1 and <= 4 ? data[2] * 25 : -1);
            if (pct >= 0)
                return (pct, data[3] == 1);
        }

        return null;
    }

    public static (int Percent, bool IsCharging)? ParseRoccatBatteryResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0x02)
        {
            int raw = data[2];
            int pct = raw <= 5 ? raw * 20 : raw;
            if (pct is >= 0 and <= 100)
                return (pct, data[3] == 1);
        }

        return null;
    }

    private static DeviceBatteryInfo BuildInfo(string id, string name, DeviceCategory cat, (int Percent, bool IsCharging) parsed)
    {
        return new DeviceBatteryInfo
        {
            Id = id,
            Name = name,
            Category = cat,
            Connection = ConnectionKind.Wireless24Ghz,
            BatteryPercent = parsed.Percent,
            IsCharging = parsed.IsCharging,
            IsConnected = true
        };
    }

    private static DeviceCategory GuessCategoryOrDefault(string name, DeviceCategory fallback)
    {
        var g = BluetoothBatteryProvider.GuessCategory(name);
        return g != DeviceCategory.Other ? g : fallback;
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }
}
