using BatteryWidget.Models;
using BatteryWidget.Providers;
using BatteryWidget.Services;
using Xunit;

namespace BatteryWidget.Tests;

public class ProtocolReplayTests
{
    [Theory]
    [InlineData(new byte[] { 0x64, 0x00, 78, 0x00, 0x01 }, 78, false)]
    [InlineData(new byte[] { 0xC9, 0x00, 0x80 | 64, 0x00, 0x04 }, 64, true)]
    [InlineData(new byte[] { 0x00, 0x0F, 0x00, 0x00, 0x52, 0x03, 0x01 }, 85, true)] // 0x0352 = 850 -> 85%
    [InlineData(new byte[] { 0x00, 0x00, 0x0F, 0x90, 0x01, 0x00 }, 40, false)]      // 0x0190 = 400 -> 40%
    public void OpenSource_Corsair_ReplaysVoidHs70AndSlipstreamPackets(byte[] packet, int expectedPct, bool expectedChg)
    {
        var res = OpenSourcePeripheralsProvider.ParseCorsairBatteryResponse(packet);
        Assert.NotNull(res);
        Assert.Equal(expectedPct, res.Value.Percent);
        Assert.Equal(expectedChg, res.Value.IsCharging);
    }

    [Fact]
    public void OpenSource_Corsair_ReturnsNullWhenDisconnected()
    {
        byte[] disconnected = [0x64, 0x00, 0x00, 0x00, 0x00];
        Assert.Null(OpenSourcePeripheralsProvider.ParseCorsairBatteryResponse(disconnected));
    }

    [Theory]
    [InlineData(new byte[] { 0x0C, 0x02, 0x03, 0x00, 0x00, 0x00, 91, 0x01 }, 91, true)]  // Cloud Alpha Wireless
    [InlineData(new byte[] { 0x06, 0xFF, 0xBB, 0x02, 0x00, 0x00, 0x00, 68 }, 68, false)] // Cloud II Wireless
    [InlineData(new byte[] { 0x21, 0xFF, 0x05, 0x10, 55 }, 55, true)]                    // Cloud Flight
    public void OpenSource_HyperXExtended_ReplaysAlphaCloud2AndFlightPackets(byte[] packet, int expectedPct, bool expectedChg)
    {
        var res = OpenSourcePeripheralsProvider.ParseHyperXExtendedResponse(packet);
        Assert.NotNull(res);
        Assert.Equal(expectedPct, res.Value.Percent);
        Assert.Equal(expectedChg, res.Value.IsCharging);
    }

    [Fact]
    public void OpenSource_PulsarAjazzAsusRoccat_ReplayValidPackets()
    {
        var pulsar = new byte[17];
        pulsar[0] = 0x08;
        pulsar[1] = 0x03;
        pulsar[6] = 74;
        pulsar[7] = 1;
        var resPulsar = OpenSourcePeripheralsProvider.ParsePulsarBatteryResponse(pulsar);
        Assert.Equal((74, true), resPulsar);

        var ajazz = new byte[64];
        ajazz[0] = 0x05;
        ajazz[1] = 0x40;
        ajazz[4] = 88;
        ajazz[5] = 0;
        var resAjazz = OpenSourcePeripheralsProvider.ParseAjazzBatteryResponse(ajazz);
        Assert.Equal((88, false), resAjazz);

        byte[] asus = [0x12, 0x07, 0x03, 0x01, 75];
        var resAsus = OpenSourcePeripheralsProvider.ParseAsusRogBatteryResponse(asus);
        Assert.Equal((75, true), resAsus);

        byte[] roccatQuantized = [0xFF, 0x02, 4, 0x00]; // 4 * 20 = 80%
        var resRoccat = OpenSourcePeripheralsProvider.ParseRoccatBatteryResponse(roccatQuantized);
        Assert.Equal((80, false), resRoccat);
    }

    [Fact]
    public void HidDeviceProfile_ChecksumsVoltageFallbackAndChargingEquals_WorkCorrectly()
    {
        var sumBuf = new byte[64];
        sumBuf[0] = 0x05;
        sumBuf[1] = 0x40;
        sumBuf[2] = 0x01;
        var sumProfile = new HidDeviceProfile { ChecksumMode = "SumMod256", ChecksumByteIndex = 63 };
        sumProfile.ApplyChecksum(sumBuf);
        Assert.Equal(0x41, sumBuf[63]);

        var compxBuf = new byte[17];
        compxBuf[0] = 0x08;
        compxBuf[1] = 0x03;
        var compxProfile = new HidDeviceProfile { ChecksumMode = "Compx55", ChecksumByteIndex = 16 };
        compxProfile.ApplyChecksum(compxBuf);
        Assert.Equal((byte)((0x55 - 0x0B) & 0xFF), compxBuf[16]);

        var xorBuf = new byte[90];
        xorBuf[2] = 0x10;
        xorBuf[3] = 0x05;
        var xorProfile = new HidDeviceProfile { ChecksumMode = "XorCrc", ChecksumByteIndex = 88 };
        xorProfile.ApplyChecksum(xorBuf);
        Assert.Equal(0x10 ^ 0x05, xorBuf[88]);

        var vProfile = new HidDeviceProfile
        {
            ExpectedReportId = 0x08,
            BatteryByteIndex = 6,
            ChargingByteIndex = 7,
            ChargingEqualsValue = 2,
            VoltageHighByteIndex = 8,
            VoltageLowByteIndex = 9
        };
        var mvPkt = new byte[17];
        mvPkt[0] = 0x08;
        mvPkt[6] = 0;    // triggers voltage fallback
        mvPkt[7] = 2;    // equals 2 => charging
        mvPkt[8] = 0x0F; // 3900 mV
        mvPkt[9] = 0x3C;
        var parsed = JsonHidProfileProvider.ParseWithProfile(vProfile, mvPkt);
        Assert.NotNull(parsed);
        Assert.Equal(67, parsed.Value.Percent);
        Assert.True(parsed.Value.IsCharging);
    }

    [Fact]
    public void RemoteProfileSync_ParseDatabaseJson_ValidatesSchemaAndFallback()
    {
        const string validJson = """
        {
          "schemaVersion": 1,
          "updatedAt": "2026-11-27T00:00:00Z",
          "reportEndpointUrl": "https://example.workers.dev",
          "profiles": [
            {
              "name": "Test Remote Mouse",
              "category": "Mouse",
              "vendorIdHex": "0x373E",
              "productIdsHex": ["0x0047"],
              "batteryByteIndex": 6
            }
          ]
        }
        """;

        var db = RemoteProfileSyncService.ParseDatabaseJson(validJson);
        Assert.NotNull(db);
        Assert.Equal("https://example.workers.dev", db.ReportEndpointUrl);
        Assert.Single(db.Profiles);
        Assert.Equal("Test Remote Mouse", db.Profiles[0].Name);

        Assert.Null(RemoteProfileSyncService.ParseDatabaseJson("{ invalid json"));
        Assert.Null(RemoteProfileSyncService.ParseDatabaseJson("""{"schemaVersion": 99, "profiles": []}"""));

        var embedded = RemoteProfileSyncService.LoadEmbeddedBaselineDatabase();
        Assert.NotNull(embedded);
        Assert.True(embedded.Profiles.Count >= 5);
    }

    [Fact]
    public void AnonymousReport_BuildPayload_ExcludesDetectedDevicesAndContainsNoPii()
    {
        var active = new List<DeviceBatteryInfo>
        {
            new()
            {
                Id = "hyperx-03F0-05B7",
                Name = "HyperX Cloud III",
                Category = DeviceCategory.Headset,
                BatteryPercent = 80,
                IsConnected = true
            }
        };

        var snapshots = new List<HidDeviceSnapshot>
        {
            new(0x03F0, 0x05B7, "HyperX Cloud III", 62, 62, 0),    // already detected -> must be excluded
            new(0x1111, 0x2222, "Mystery 8K Mouse", 64, 64, 65),  // unknown -> must be included
            new(0x3333, 0x4444, "Standard Mouse", 8, 0, 0)        // no Out/Feat -> must be excluded
        };

        var payload = AnonymousReportService.BuildPayload(active, snapshots, "1.1.0");
        Assert.Equal("1.1.0", payload.AppVersion);
        Assert.Equal("Windows", payload.OsPlatform);
        Assert.Equal(1, payload.DetectedDeviceCount);
        Assert.Single(payload.UnknownHidCandidates);
        Assert.Equal("0x1111", payload.UnknownHidCandidates[0].Vid);
        Assert.Equal("0x2222", payload.UnknownHidCandidates[0].Pid);
        Assert.Equal("Mystery 8K Mouse", payload.UnknownHidCandidates[0].ProductName);

        var sig1 = AnonymousReportService.ComputeSignature(payload);
        var sig2 = AnonymousReportService.ComputeSignature(payload);
        Assert.Equal(sig1, sig2);
        Assert.DoesNotContain(Environment.UserName, AnonymousReportService.SerializePayloadPretty(payload), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.1.0", "v1.2.0", true)]
    [InlineData("1.1.0", "v1.1.1", true)]
    [InlineData("1.1.0", "v1.1.0", false)]
    [InlineData("1.1.0", "v1.0.6", false)]
    [InlineData("1.1.0", "invalid", false)]
    public void UpdateCheck_IsNewerVersion_ComparesSemverCorrectly(string current, string latestTag, bool expected)
    {
        Assert.Equal(expected, UpdateCheckService.IsNewerVersion(current, latestTag));
    }
}
