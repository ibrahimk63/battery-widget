using System.IO;
using System.Security.Cryptography;
using BatteryWidget.Services;
using Xunit;

namespace BatteryWidget.Tests;

public class AutoUpdaterTests
{
    [Fact]
    public void UpdateCheck_ParseReleaseJson_ExtractsTrustedExeAssetAndSha256()
    {
        const string json = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/ibrahimk63/battery-widget/releases/tag/v1.2.0",
          "assets": [
            {
              "name": "BatteryWidget.exe",
              "size": 65000000,
              "digest": "sha256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
              "browser_download_url": "https://github.com/ibrahimk63/battery-widget/releases/download/v1.2.0/BatteryWidget.exe"
            }
          ]
        }
        """;

        var result = UpdateCheckService.ParseReleaseJson(json, "1.1.0");
        Assert.NotNull(result);
        Assert.True(result.HasUpdate);
        Assert.Equal("1.2.0", result.LatestVersion);
        Assert.Equal("https://github.com/ibrahimk63/battery-widget/releases/download/v1.2.0/BatteryWidget.exe", result.DownloadUrl);
        Assert.Equal(65000000, result.ExpectedAssetSize);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", result.ExpectedSha256);
    }

    [Fact]
    public void UpdateCheck_ParseReleaseJson_RejectsUntrustedHostAndSmallAsset()
    {
        const string untrustedJson = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/ibrahimk63/battery-widget/releases/tag/v1.2.0",
          "assets": [
            {
              "name": "BatteryWidget.exe",
              "size": 65000000,
              "browser_download_url": "https://evil.example.com/BatteryWidget.exe"
            }
          ]
        }
        """;

        var untrusted = UpdateCheckService.ParseReleaseJson(untrustedJson, "1.1.0");
        Assert.NotNull(untrusted);
        Assert.Null(untrusted.DownloadUrl);

        const string tinyJson = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/ibrahimk63/battery-widget/releases/tag/v1.2.0",
          "assets": [
            {
              "name": "BatteryWidget.exe",
              "size": 512,
              "browser_download_url": "https://github.com/ibrahimk63/battery-widget/releases/download/v1.2.0/BatteryWidget.exe"
            }
          ]
        }
        """;

        var tiny = UpdateCheckService.ParseReleaseJson(tinyJson, "1.1.0");
        Assert.NotNull(tiny);
        Assert.Null(tiny.DownloadUrl);
    }

    [Fact]
    public void SelfUpdate_ValidateDownloadedExecutable_ChecksHeaderSizeAndSha256()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bw-upd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string exePath = Path.Combine(tempDir, "BatteryWidget.exe");
            byte[] payload = new byte[1_000_000];
            payload[0] = (byte)'M';
            payload[1] = (byte)'Z';
            File.WriteAllBytes(exePath, payload);

            string expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

            Assert.True(SelfUpdateService.ValidateDownloadedExecutable(exePath, payload.Length, expectedHash, out _));
            Assert.False(SelfUpdateService.ValidateDownloadedExecutable(exePath, payload.Length + 1, expectedHash, out _));
            Assert.False(SelfUpdateService.ValidateDownloadedExecutable(exePath, payload.Length, new string('0', 64), out _));

            payload[0] = (byte)'<';
            payload[1] = (byte)'h';
            string htmlPath = Path.Combine(tempDir, "not-exe.bin");
            File.WriteAllBytes(htmlPath, payload);
            Assert.False(SelfUpdateService.ValidateDownloadedExecutable(htmlPath, null, null, out _));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SelfUpdate_CanSelfUpdateInPlace_AcceptsWritableExeAndRejectsDotnetHost()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bw-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string fakeExe = Path.Combine(tempDir, "BatteryWidget.exe");
            File.WriteAllText(fakeExe, "stub");

            Assert.True(SelfUpdateService.CanSelfUpdateInPlace(fakeExe));
            Assert.False(SelfUpdateService.CanSelfUpdateInPlace(Path.Combine(tempDir, "dotnet.exe")));
            Assert.False(SelfUpdateService.CanSelfUpdateInPlace(Path.Combine(tempDir, "BatteryWidget.dll")));
            Assert.False(SelfUpdateService.CanSelfUpdateInPlace(null));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SelfUpdate_BuildScriptAndCleanup_ContainSafeRollbackAndRemoveLeftovers()
    {
        string script = SelfUpdateService.BuildUpdaterPowerShellScript();
        Assert.Contains("-LiteralPath", script);
        Assert.Contains("$backup", script);
        Assert.Contains("Start-Process -FilePath $target", script);

        string tempDir = Path.Combine(Path.GetTempPath(), $"bw-clean-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string exePath = Path.Combine(tempDir, "BatteryWidget.exe");
            File.WriteAllText(exePath + ".bak", "old");
            File.WriteAllText(exePath + ".update", "new");
            File.WriteAllText(exePath + ".update.part", "part");

            SelfUpdateService.CleanupLeftoverUpdateFiles(exePath);

            Assert.False(File.Exists(exePath + ".bak"));
            Assert.False(File.Exists(exePath + ".update"));
            Assert.False(File.Exists(exePath + ".update.part"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
