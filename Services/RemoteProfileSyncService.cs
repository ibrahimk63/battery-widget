using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryWidget.Models;

namespace BatteryWidget.Services;

public sealed class RemoteProfilesDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public string UpdatedAt { get; set; } = string.Empty;
    public string? ReportEndpointUrl { get; set; }
    public List<HidDeviceProfile> Profiles { get; set; } = [];
}

/// <summary>
/// Synchronizes curated HID device profiles from GitHub Raw without requiring a new .exe release,
/// with ETag caching and an offline embedded baseline fallback.
/// </summary>
public static class RemoteProfileSyncService
{
    public const string DefaultRemoteDatabaseUrl =
        "https://raw.githubusercontent.com/ibrahimk63/battery-widget/main/profiles-db.json";

    public static string LocalCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryWidget",
        "remote-profiles-db.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BatteryWidget/1.1.0");
        return client;
    }

    public static RemoteProfilesDatabase? ParseDatabaseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var db = JsonSerializer.Deserialize<RemoteProfilesDatabase>(json, JsonOptions);
            if (db is null || db.SchemaVersion != 1 || db.Profiles is null)
                return null;

            db.Profiles = db.Profiles
                .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.VendorIdHex))
                .ToList();
            return db;
        }
        catch
        {
            return null;
        }
    }

    public static RemoteProfilesDatabase LoadCachedOrEmbeddedDatabase()
    {
        try
        {
            if (File.Exists(LocalCachePath))
            {
                var cached = ParseDatabaseJson(File.ReadAllText(LocalCachePath));
                if (cached is not null)
                    return cached;
            }
        }
        catch
        {
            // Fall back to embedded baseline
        }

        return LoadEmbeddedBaselineDatabase() ?? new RemoteProfilesDatabase();
    }

    public static RemoteProfilesDatabase? LoadEmbeddedBaselineDatabase()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("profiles-db.json", StringComparison.OrdinalIgnoreCase));
            if (resName is null)
                return null;

            using var stream = asm.GetManifestResourceStream(resName);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream);
            return ParseDatabaseJson(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    public static string? GetActiveReportEndpointUrl()
    {
        var url = LoadCachedOrEmbeddedDatabase().ReportEndpointUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString()
            : null;
    }

    public static async Task<(bool Updated, int ProfileCount, string Message)> SyncIfDueAsync(
        AppSettings settings,
        bool force = false,
        CancellationToken ct = default)
    {
        var current = LoadCachedOrEmbeddedDatabase();

        if (!force)
        {
            if (!settings.EnableRemoteProfileSync)
                return (false, current.Profiles.Count, "Otomatik senkronizasyon kapalı.");

            if (settings.LastProfileSyncUtc is not null &&
                DateTimeOffset.UtcNow - settings.LastProfileSyncUtc.Value < TimeSpan.FromHours(24))
            {
                return (false, current.Profiles.Count, "Yerel veritabanı güncel.");
            }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, DefaultRemoteDatabaseUrl);
            if (!force && !string.IsNullOrWhiteSpace(settings.RemoteProfileEtag) &&
                EntityTagHeaderValue.TryParse(settings.RemoteProfileEtag, out var etag))
            {
                req.Headers.IfNoneMatch.Add(etag);
            }

            using var resp = await SharedHttpClient.SendAsync(req, ct);
            settings.LastProfileSyncUtc = DateTimeOffset.UtcNow;

            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                settings.Save();
                return (false, current.Profiles.Count, "Veritabanı zaten en güncel sürümde.");
            }

            if (!resp.IsSuccessStatusCode)
            {
                settings.Save();
                return (false, current.Profiles.Count, $"Sunucu yanıtı: {(int)resp.StatusCode}");
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = ParseDatabaseJson(json);
            if (parsed is null)
            {
                settings.Save();
                return (false, current.Profiles.Count, "Uzak veritabanı formatı doğrulanamadı.");
            }

            string? dir = Path.GetDirectoryName(LocalCachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(LocalCachePath, json, ct);

            if (resp.Headers.ETag is not null)
                settings.RemoteProfileEtag = resp.Headers.ETag.ToString();

            settings.Save();
            return (true, parsed.Profiles.Count, $"{parsed.Profiles.Count} cihaz profili güncellendi.");
        }
        catch (Exception ex)
        {
            return (false, current.Profiles.Count, $"Güncellenemedi (çevrimdışı mod): {ex.Message}");
        }
    }
}
