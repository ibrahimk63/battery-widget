using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace BatteryWidget.Services;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases API once per session to notify users when a newer BatteryWidget release exists.
/// </summary>
public static class UpdateCheckService
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/ibrahimk63/battery-widget/releases/latest";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BatteryWidget/1.1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
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

    public static async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        string current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";

        try
        {
            using var resp = await SharedHttpClient.GetAsync(LatestReleaseApiUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            string? tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            string? htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(htmlUrl))
                return null;

            bool hasUpdate = IsNewerVersion(current, tag);
            return new UpdateCheckResult(hasUpdate, current, tag.TrimStart('v', 'V'), htmlUrl);
        }
        catch
        {
            return null;
        }
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
