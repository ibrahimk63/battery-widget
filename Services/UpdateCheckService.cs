using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace BatteryWidget.Services;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? DownloadUrl = null,
    long? ExpectedAssetSize = null,
    string? ExpectedSha256 = null);

/// <summary>
/// Checks GitHub Releases API to detect newer BatteryWidget releases and locate trusted BatteryWidget.exe assets.
/// </summary>
public static class UpdateCheckService
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/ibrahimk63/battery-widget/releases/latest";

    private const long MinValidExeBytes = 1_000_000;

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BatteryWidget/1.2.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static bool IsTrustedDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        string host = uri.Host;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewerVersion(string currentVersion, string latestTag)
    {
        if (!TryParseSemanticVersion(currentVersion, out var cur) ||
            !TryParseSemanticVersion(latestTag, out var lat))
        {
            return false;
        }

        return lat > cur;
    }

    public static string GetCurrentVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.2.0";

    public static UpdateCheckResult? ParseReleaseJson(string json, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            string? htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(htmlUrl))
                return null;

            string? downloadUrl = null;
            long? assetSize = null;
            string? expectedSha256 = null;

            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (!string.Equals(name, "BatteryWidget.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? candidateUrl = asset.TryGetProperty("browser_download_url", out var dlEl)
                        ? dlEl.GetString()
                        : null;
                    long candidateSize = asset.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var parsedSize)
                        ? parsedSize
                        : 0;

                    if (!IsTrustedDownloadUrl(candidateUrl) || candidateSize < MinValidExeBytes)
                        continue;

                    downloadUrl = candidateUrl;
                    assetSize = candidateSize;

                    if (asset.TryGetProperty("digest", out var digestEl))
                        expectedSha256 = NormalizeSha256Digest(digestEl.GetString());

                    break;
                }
            }

            bool hasUpdate = IsNewerVersion(currentVersion, tag);
            return new UpdateCheckResult(
                hasUpdate,
                currentVersion,
                tag.TrimStart('v', 'V'),
                htmlUrl,
                downloadUrl,
                assetSize,
                expectedSha256);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        string current = GetCurrentVersion();

        try
        {
            using var resp = await SharedHttpClient.GetAsync(LatestReleaseApiUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            string json = await resp.Content.ReadAsStringAsync(ct);
            return ParseReleaseJson(json, current);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeSha256Digest(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["sha256:".Length..].Trim();

        if (trimmed.Length != 64)
            return null;

        foreach (char c in trimmed)
        {
            bool isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
                return null;
        }

        return trimmed.ToLowerInvariant();
    }

    private static bool TryParseSemanticVersion(string raw, out Version parsed)
    {
        parsed = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string clean = raw.Trim().TrimStart('v', 'V');
        int dash = clean.IndexOf('-');
        if (dash >= 0)
            clean = clean[..dash];

        return Version.TryParse(clean, out parsed!);
    }
}
