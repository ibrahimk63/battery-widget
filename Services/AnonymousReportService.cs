using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryWidget.Models;
using HidSharp;

namespace BatteryWidget.Services;

public sealed record HidDeviceSnapshot(
    int VendorId,
    int ProductId,
    string ProductName,
    int MaxIn,
    int MaxOut,
    int MaxFeat);

public sealed record UnknownHidCandidate(
    string Vid,
    string Pid,
    string ProductName,
    int MaxIn,
    int MaxOut,
    int MaxFeat);

public sealed record AnonymousReportPayload(
    string AppVersion,
    string OsPlatform,
    int DetectedDeviceCount,
    List<string> DetectedCategories,
    List<UnknownHidCandidate> UnknownHidCandidates);

/// <summary>
/// Builds strictly PII-free anonymous hardware summaries (only unmatched vendor HID VID:PID + packet lengths)
/// and submits them when the user opts in or triggers a one-click report.
/// </summary>
public static class AnonymousReportService
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };

    public static AnonymousReportPayload BuildPayload(
        IReadOnlyList<DeviceBatteryInfo> activeDevices,
        IEnumerable<HidDeviceSnapshot>? snapshots = null,
        string? appVersion = null)
    {
        string version = string.IsNullOrWhiteSpace(appVersion)
            ? (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0")
            : appVersion;

        IEnumerable<HidDeviceSnapshot> source = snapshots ?? EnumerateVendorHidSnapshots();

        var detectedTokens = activeDevices
            .SelectMany(d => new[] { d.Id ?? string.Empty, d.Name ?? string.Empty })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var unknownCandidates = source
            .Where(s => s.VendorId > 0 && (s.MaxOut >= 8 || s.MaxFeat >= 8))
            .GroupBy(s => (s.VendorId, s.ProductId))
            .Where(g => !IsAlreadyDetected(g.Key.VendorId, g.Key.ProductId, g.First().ProductName, detectedTokens))
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.MaxOut).ThenByDescending(x => x.MaxFeat).First();
                return new UnknownHidCandidate(
                    $"0x{g.Key.VendorId:X4}",
                    $"0x{g.Key.ProductId:X4}",
                    SanitizeProductName(best.ProductName),
                    best.MaxIn,
                    best.MaxOut,
                    best.MaxFeat);
            })
            .OrderBy(c => c.Vid)
            .ThenBy(c => c.Pid)
            .Take(20)
            .ToList();

        var categories = activeDevices
            .Where(d => d.IsConnected)
            .Select(d => d.Category.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        return new AnonymousReportPayload(
            version,
            "Windows",
            activeDevices.Count(d => d.IsConnected),
            categories,
            unknownCandidates);
    }

    public static string SerializePayloadPretty(AnonymousReportPayload payload) =>
        JsonSerializer.Serialize(payload, PrettyJsonOptions);

    public static string ComputeSignature(AnonymousReportPayload payload)
    {
        var canonical = string.Join("|", payload.UnknownHidCandidates
            .OrderBy(c => c.Vid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Pid, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{c.Vid}:{c.Pid}"));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash);
    }

    public static async Task<(bool Sent, string Message)> SendReportAsync(
        AnonymousReportPayload payload,
        string? endpointUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl) ||
            !Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return (false, "Anonim bildirim sunucusu henüz tanımlı değil.");
        }

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await SharedHttpClient.PostAsync(uri, content, ct);

            if (resp.IsSuccessStatusCode)
                return (true, "Anonim cihaz bilgisi başarıyla iletildi. Desteğiniz için teşekkürler!");

            return (false, $"Sunucu hatası: {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"Gönderilemedi: {ex.Message}");
        }
    }

    public static async Task TryAutoReportIfConsentedAsync(
        AppSettings settings,
        IReadOnlyList<DeviceBatteryInfo> activeDevices,
        CancellationToken ct = default)
    {
        if (!settings.EnableAnonymousReporting)
            return;

        string? endpoint = RemoteProfileSyncService.GetActiveReportEndpointUrl();
        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        var payload = BuildPayload(activeDevices);
        if (payload.UnknownHidCandidates.Count == 0)
            return;

        string signature = ComputeSignature(payload);
        bool recentlyReportedSame =
            string.Equals(signature, settings.LastReportedSignature, StringComparison.OrdinalIgnoreCase) &&
            settings.LastReportSentUtc is not null &&
            DateTimeOffset.UtcNow - settings.LastReportSentUtc.Value < TimeSpan.FromDays(7);

        if (recentlyReportedSame)
            return;

        var (sent, _) = await SendReportAsync(payload, endpoint, ct);
        if (sent)
        {
            settings.LastReportedSignature = signature;
            settings.LastReportSentUtc = DateTimeOffset.UtcNow;
            settings.Save();
        }
    }

    private static bool IsAlreadyDetected(int vid, int pid, string productName, IReadOnlyList<string> detectedTokens)
    {
        string vidHex = $"{vid:X4}";
        string pidHex = $"{pid:X4}";
        string combined = $"{vidHex}-{pidHex}";
        string combinedColon = $"{vidHex}:{pidHex}";

        foreach (var token in detectedTokens)
        {
            if (token.Contains(combined, StringComparison.OrdinalIgnoreCase) ||
                token.Contains(combinedColon, StringComparison.OrdinalIgnoreCase) ||
                token.Contains(pidHex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(productName) &&
                productName.Length >= 4 &&
                token.Contains(productName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<HidDeviceSnapshot> EnumerateVendorHidSnapshots()
    {
        var list = new List<HidDeviceSnapshot>();
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            if (d.VendorID <= 0)
                continue;

            int maxOut = d.GetMaxOutputReportLength();
            int maxFeat = d.GetMaxFeatureReportLength();
            if (maxOut < 8 && maxFeat < 8)
                continue;

            string name = "Bilinmeyen Aygıt";
            try
            {
                var p = d.GetProductName();
                if (!string.IsNullOrWhiteSpace(p))
                    name = p;
            }
            catch { /* ignore */ }

            list.Add(new HidDeviceSnapshot(
                d.VendorID,
                d.ProductID,
                name,
                d.GetMaxInputReportLength(),
                maxOut,
                maxFeat));
        }

        return list;
    }

    private static string SanitizeProductName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Bilinmeyen Aygıt";

        string cleaned = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }
}
