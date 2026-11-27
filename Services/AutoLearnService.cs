using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryWidget.Models;
using BatteryWidget.Providers;
using HidSharp;

namespace BatteryWidget.Services;

public sealed record LearnableDeviceItem(int VendorId, int ProductId, string DisplayName)
{
    public override string ToString() => $"{DisplayName} (0x{VendorId:X4}:0x{ProductId:X4})";
}

public sealed record LearnedCandidate(
    int VendorId,
    int ProductId,
    string DeviceName,
    bool UseFeatureReport,
    bool ReadPassiveInput,
    byte ReportId,
    string? QueryPacketHex,
    int BatteryByteIndex,
    int BatteryMask,
    int MaxBatteryValue,
    string MatchMode,
    string RawHexPreview,
    int Score)
{
    public override string ToString()
    {
        string mode = UseFeatureReport
            ? $"Feature(0x{ReportId:X2})"
            : (ReadPassiveInput ? $"Pasif Input(0x{ReportId:X2})" : $"Komut [{QueryPacketHex}]");
        return $"[{MatchMode}] {mode} -> Byte[{BatteryByteIndex}] | Skor:{Score} | {RawHexPreview}";
    }
}

/// <summary>
/// Interactive Auto-Learn algorithm that probes any unknown USB/2.4GHz HID device
/// and correlates byte positions with the user's known current battery percentage.
/// </summary>
public static class AutoLearnService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<LearnableDeviceItem> ListLearnableDevices()
    {
        return DeviceList.Local.GetHidDevices()
            .Where(d => d.VendorID > 0 &&
                        (d.GetMaxOutputReportLength() >= 8 ||
                         d.GetMaxFeatureReportLength() >= 8 ||
                         d.GetMaxInputReportLength() >= 16))
            .GroupBy(d => (d.VendorID, d.ProductID))
            .Select(g =>
            {
                string name = "(Kablosuz / USB Cihaz)";
                foreach (var d in g)
                {
                    try
                    {
                        var n = d.GetProductName();
                        if (!string.IsNullOrWhiteSpace(n)) { name = n.Trim(); break; }
                    }
                    catch { /* ignore */ }
                }
                return new LearnableDeviceItem(g.Key.VendorID, g.Key.ProductID, name);
            })
            .OrderBy(x => x.DisplayName)
            .ToList();
    }

    public static Task<IReadOnlyList<LearnedCandidate>> ScanDeviceForBatteryAsync(
        int vendorId,
        int productId,
        string deviceName,
        int knownPercent,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<LearnedCandidate>>(() =>
        {
            var candidates = new List<LearnedCandidate>();
            var collections = DeviceList.Local.GetHidDevices(vendorId, productId)
                .Where(d => d.GetMaxOutputReportLength() >= 8 ||
                            d.GetMaxFeatureReportLength() >= 8 ||
                            d.GetMaxInputReportLength() >= 16)
                .ToList();

            foreach (var device in collections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var desc = device.GetReportDescriptor();
                    if (!device.TryOpen(out var stream))
                        continue;

                    using (stream)
                    {
                        stream.ReadTimeout = 200;
                        stream.WriteTimeout = 200;

                        // 1) Probe all Feature Reports via GetFeature
                        int featLen = device.GetMaxFeatureReportLength();
                        if (featLen >= 4 && desc.FeatureReports.Any())
                        {
                            foreach (var fr in desc.FeatureReports)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                try
                                {
                                    var buf = new byte[Math.Max(fr.Length, featLen)];
                                    buf[0] = fr.ReportID;
                                    stream.GetFeature(buf);
                                    candidates.AddRange(FindMatchingByteCandidates(
                                        vendorId, productId, deviceName,
                                        useFeatureReport: true, readPassiveInput: false,
                                        reportId: buf[0], queryPacketHex: null,
                                        response: buf, knownPercent: knownPercent));
                                }
                                catch { /* ignore unsupported GetFeature */ }
                            }
                        }

                        // 2) Probe Passive Input Reports (without writing)
                        int inLen = device.GetMaxInputReportLength();
                        if (inLen >= 4)
                        {
                            for (int attempt = 0; attempt < 2; attempt++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                try
                                {
                                    var inBuf = new byte[inLen];
                                    int read = stream.Read(inBuf, 0, inBuf.Length);
                                    if (read <= 1) break;
                                    candidates.AddRange(FindMatchingByteCandidates(
                                        vendorId, productId, deviceName,
                                        useFeatureReport: false, readPassiveInput: true,
                                        reportId: inBuf[0], queryPacketHex: null,
                                        response: inBuf.AsSpan(0, read), knownPercent: knownPercent));
                                }
                                catch { break; }
                            }
                        }

                        // 3) Probe safe battery query opcodes on each Output Report ID
                        int outLen = device.GetMaxOutputReportLength();
                        if (outLen >= 8 && inLen >= 4 && desc.OutputReports.Any())
                        {
                            byte[] opcodes = [0x04, 0x02, 0x05, 0x06, 0x18, 0x1A, 0x30, 0x65, 0x7D, 0x80, 0x83, 0x89];
                            foreach (var outRep in desc.OutputReports)
                            {
                                foreach (byte op in opcodes)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    int reqLen = Math.Max(outLen, outRep.Length);
                                    var req = new byte[reqLen];
                                    req[0] = outRep.ReportID;
                                    req[1] = op;
                                    if (op == 0x04 && reqLen > 16)
                                        req[16] = 0x49; // CompX checksum

                                    string queryHex = BitConverter.ToString(req, 0, Math.Min(reqLen, 17)).Replace('-', ' ');
                                    try
                                    {
                                        stream.Write(req, 0, req.Length);
                                        var inBuf = new byte[inLen];
                                        int read = stream.Read(inBuf, 0, inBuf.Length);
                                        if (read > 1)
                                        {
                                            candidates.AddRange(FindMatchingByteCandidates(
                                                vendorId, productId, deviceName,
                                                useFeatureReport: false, readPassiveInput: false,
                                                reportId: inBuf[0], queryPacketHex: queryHex,
                                                response: inBuf.AsSpan(0, read), knownPercent: knownPercent));
                                        }
                                    }
                                    catch { /* ignore timeout */ }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Skip busy interface
                }
            }

            return candidates
                .GroupBy(c => (c.UseFeatureReport, c.ReadPassiveInput, c.ReportId, c.QueryPacketHex ?? "", c.BatteryByteIndex, c.BatteryMask, c.MaxBatteryValue))
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.BatteryByteIndex)
                .Take(40)
                .ToList();
        }, cancellationToken);
    }

    public static List<LearnedCandidate> FindMatchingByteCandidates(
        int vendorId,
        int productId,
        string deviceName,
        bool useFeatureReport,
        bool readPassiveInput,
        byte reportId,
        string? queryPacketHex,
        ReadOnlySpan<byte> response,
        int knownPercent)
    {
        var list = new List<LearnedCandidate>();
        if (knownPercent is < 1 or > 100 || response.Length < 2)
            return list;

        string preview = BitConverter.ToString(response[..Math.Min(response.Length, 22)].ToArray());

        for (int i = 1; i < response.Length; i++)
        {
            byte b = response[i];
            if (b == 0)
                continue;

            if (b == knownPercent)
            {
                list.Add(new LearnedCandidate(
                    vendorId, productId, deviceName, useFeatureReport, readPassiveInput,
                    reportId, queryPacketHex, i, 0xFF, 100, "Tam Eşleşme", preview, 100));
            }
            else if ((b & 0x7F) == knownPercent)
            {
                list.Add(new LearnedCandidate(
                    vendorId, productId, deviceName, useFeatureReport, readPassiveInput,
                    reportId, queryPacketHex, i, 0x7F, 100, "7-Bit (Şarj Bitli)", preview, 85));
            }
            else if (b > 100)
            {
                int scaled = (int)Math.Round(b * 100.0 / 255.0);
                if (Math.Abs(scaled - knownPercent) <= 1)
                {
                    list.Add(new LearnedCandidate(
                        vendorId, productId, deviceName, useFeatureReport, readPassiveInput,
                        reportId, queryPacketHex, i, 0xFF, 255, "0-255 Ölçekli", preview, 70));
                }
            }
        }

        return list;
    }

    public static IReadOnlyList<LearnedCandidate> RefineCandidates(
        IReadOnlyList<LearnedCandidate> previous,
        IReadOnlyList<LearnedCandidate> current)
    {
        var currentKeys = current
            .Select(c => (c.UseFeatureReport, c.ReadPassiveInput, c.ReportId, c.QueryPacketHex ?? "", c.BatteryByteIndex, c.BatteryMask, c.MaxBatteryValue))
            .ToHashSet();

        return previous
            .Where(p => currentKeys.Contains((p.UseFeatureReport, p.ReadPassiveInput, p.ReportId, p.QueryPacketHex ?? "", p.BatteryByteIndex, p.BatteryMask, p.MaxBatteryValue)))
            .ToList();
    }

    public static string SaveLearnedProfile(LearnedCandidate candidate, string customName, DeviceCategory category)
    {
        string dir = JsonHidProfileProvider.UserProfilesDirectory;
        Directory.CreateDirectory(dir);

        var profile = new HidDeviceProfile
        {
            Name = string.IsNullOrWhiteSpace(customName) ? candidate.DeviceName : customName.Trim(),
            Category = category,
            VendorIdHex = $"0x{candidate.VendorId:X4}",
            ProductIdsHex = [$"0x{candidate.ProductId:X4}"],
            QueryPacketHex = candidate.QueryPacketHex,
            UseFeatureReport = candidate.UseFeatureReport,
            ReadPassiveInput = candidate.ReadPassiveInput,
            ExpectedReportId = candidate.ReportId,
            BatteryByteIndex = candidate.BatteryByteIndex,
            BatteryMask = candidate.BatteryMask,
            MaxBatteryValue = candidate.MaxBatteryValue
        };

        string path = Path.Combine(dir, $"{candidate.VendorId:X4}_{candidate.ProductId:X4}.json");
        string json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
        return path;
    }
}
