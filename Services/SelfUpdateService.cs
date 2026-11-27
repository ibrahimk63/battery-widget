using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace BatteryWidget.Services;

/// <summary>
/// Downloads, cryptographically validates, and performs atomic in-place single-file .exe self-updates on Windows.
/// </summary>
public static class SelfUpdateService
{
    private const long MinValidExeBytes = 1_000_000;

    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BatteryWidget/1.2.0");
        return client;
    }

    public static bool CanSelfUpdateInPlace(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return false;

        if (!processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        string fileName = Path.GetFileName(processPath);
        if (string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "testhost.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return false;

        string probe = Path.Combine(dir, $".bw-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ValidateDownloadedExecutable(
        string filePath,
        long? expectedSize,
        string? expectedSha256,
        out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            reason = "İndirilen güncelleme dosyası bulunamadı.";
            return false;
        }

        var info = new FileInfo(filePath);
        if (info.Length < MinValidExeBytes)
        {
            reason = "İndirilen dosya geçerli bir BatteryWidget.exe boyutunda değil.";
            return false;
        }

        if (expectedSize is > 0 && info.Length != expectedSize.Value)
        {
            reason = $"Dosya boyutu uyuşmuyor (beklenen: {expectedSize.Value}, gelen: {info.Length}).";
            return false;
        }

        using (var fs = File.OpenRead(filePath))
        {
            Span<byte> header = stackalloc byte[2];
            if (fs.Read(header) < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                reason = "İndirilen dosya geçerli bir Windows çalıştırılabilir dosyası (.exe) değil.";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            using var fs = File.OpenRead(filePath);
            byte[] hash = SHA256.HashData(fs);
            string actualHex = Convert.ToHexString(hash).ToLowerInvariant();
            if (!string.Equals(actualHex, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                reason = "SHA-256 güvenlik doğrulaması başarısız oldu.";
                return false;
            }
        }

        return true;
    }

    public static async Task<(bool Success, string? StagedPath, string Message)> DownloadAndStageUpdateAsync(
        UpdateCheckResult update,
        string targetExePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl) || !UpdateCheckService.IsTrustedDownloadUrl(update.DownloadUrl))
            return (false, null, "Güvenilir güncelleme indirme bağlantısı bulunamadı.");

        if (!CanSelfUpdateInPlace(targetExePath))
            return (false, null, "Uygulama klasörüne yazma izni yok; tarayıcı üzerinden indirebilirsiniz.");

        string partPath = targetExePath + ".update.part";
        string stagedPath = targetExePath + ".update";

        try
        {
            TryDeleteFile(partPath);
            TryDeleteFile(stagedPath);

            using var resp = await DownloadClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"İndirme başarısız (HTTP {(int)resp.StatusCode}).");

            long? totalBytes = resp.Content.Headers.ContentLength ?? update.ExpectedAssetSize;
            long downloaded = 0;

            await using (var remote = await resp.Content.ReadAsStreamAsync(ct))
            await using (var local = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await remote.ReadAsync(buffer, ct)) > 0)
                {
                    await local.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;

                    if (totalBytes is > 0)
                        progress?.Report(Math.Clamp((double)downloaded / totalBytes.Value, 0.0, 1.0));
                }
            }

            if (!ValidateDownloadedExecutable(partPath, update.ExpectedAssetSize, update.ExpectedSha256, out string reason))
            {
                TryDeleteFile(partPath);
                return (false, null, reason);
            }

            File.Move(partPath, stagedPath, overwrite: true);
            progress?.Report(1.0);
            return (true, stagedPath, $"v{update.LatestVersion} doğrulandı ve kuruluma hazır.");
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partPath);
            return (false, null, "Güncelleme indirmesi iptal edildi.");
        }
        catch (Exception ex)
        {
            TryDeleteFile(partPath);
            return (false, null, $"Güncelleme indirilemedi: {ex.Message}");
        }
    }

    public static string BuildUpdaterPowerShellScript()
    {
        return """
$ErrorActionPreference = 'Stop'
$target = $env:BW_TARGET
$staged = $env:BW_STAGED
$pidToWait = [int]$env:BW_PID
$backup = "$target.bak"

if ($pidToWait -gt 0) {
    Wait-Process -Id $pidToWait -Timeout 15 -ErrorAction SilentlyContinue
}

$swapped = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        if (Test-Path -LiteralPath $backup) {
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $target) {
            Move-Item -LiteralPath $target -Destination $backup -Force
        }
        Move-Item -LiteralPath $staged -Destination $target -Force
        $swapped = $true
        break
    } catch {
        if (-not (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $target -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 350
    }
}

if ($swapped) {
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $target
}
""";
    }

    public static bool LaunchSwapProcess(string targetExePath, string stagedExePath, int currentPid)
    {
        if (string.IsNullOrWhiteSpace(targetExePath) || string.IsNullOrWhiteSpace(stagedExePath))
            return false;

        if (!File.Exists(stagedExePath))
            return false;

        try
        {
            string script = BuildUpdaterPowerShellScript();
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.Environment["BW_TARGET"] = targetExePath;
            psi.Environment["BW_STAGED"] = stagedExePath;
            psi.Environment["BW_PID"] = currentPid.ToString();

            return Process.Start(psi) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void CleanupLeftoverUpdateFiles(string? processPath = null)
    {
        string? exePath = processPath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        TryDeleteFile(exePath + ".bak");
        TryDeleteFile(exePath + ".update");
        TryDeleteFile(exePath + ".update.part");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only
        }
    }
}
