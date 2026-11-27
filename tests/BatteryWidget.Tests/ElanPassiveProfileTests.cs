using BatteryWidget.Models;
using BatteryWidget.Providers;
using BatteryWidget.Services;
using Xunit;

namespace BatteryWidget.Tests;

public class ElanPassiveProfileTests
{
    private static HidDeviceProfile CreateThroneRepelProfile() => new()
    {
        Name = "Throne Repel 8K",
        Category = DeviceCategory.Mouse,
        VendorIdHex = "0x04F3",
        ProductIdsHex = ["0x026F", "0x026E"],
        WiredProductIdsHex = ["0x026E"],
        UsagePageHex = "0x000A",
        UseFeatureReport = false,
        ReadPassiveInput = true,
        MergeProductIds = true,
        ExpectedReportId = 4,
        LinkStatusByteIndex = 1,
        LinkStatusOnlineValue = 1,
        BatteryByteIndex = 4,
        BatteryMask = 255,
        ChargingByteIndex = 5,
        ChargingEqualsValue = 1,
        PassiveStaleSeconds = 25,
        MaxBatteryValue = 100
    };

    [Fact]
    public void Elan_ParseFrameWithProfile_ParsesOnlineDischargingChargingAndOfflineFrames()
    {
        var profile = CreateThroneRepelProfile();

        // Hardware-confirmed Report ID 0x04: byte[1]=link(1), byte[4]=60%, byte[5]=0
        var onlineDischarging = new byte[32];
        onlineDischarging[0] = 0x04;
        onlineDischarging[1] = 0x01;
        onlineDischarging[2] = 0x02;
        onlineDischarging[3] = 0x14;
        onlineDischarging[4] = 0x3C; // 60%
        onlineDischarging[5] = 0x00;

        var f1 = JsonHidProfileProvider.ParseFrameWithProfile(profile, onlineDischarging);
        Assert.NotNull(f1);
        Assert.True(f1.Value.IsOnline);
        Assert.Equal(60, f1.Value.Percent);
        Assert.False(f1.Value.IsCharging);
        Assert.Equal((60, false), JsonHidProfileProvider.ParseWithProfile(profile, onlineDischarging));

        // Charging: byte[4]=85, byte[5]=1
        var onlineCharging = new byte[32];
        onlineCharging[0] = 0x04;
        onlineCharging[1] = 0x01;
        onlineCharging[4] = 85;
        onlineCharging[5] = 0x01;

        var f2 = JsonHidProfileProvider.ParseFrameWithProfile(profile, onlineCharging);
        Assert.NotNull(f2);
        Assert.True(f2.Value.IsOnline);
        Assert.Equal(85, f2.Value.Percent);
        Assert.True(f2.Value.IsCharging);

        // Offline/asleep dongle heartbeat: byte[1]=0 with stale 60%
        var offline = new byte[32];
        offline[0] = 0x04;
        offline[1] = 0x00;
        offline[4] = 60;
        offline[5] = 0x00;

        var f3 = JsonHidProfileProvider.ParseFrameWithProfile(profile, offline);
        Assert.NotNull(f3);
        Assert.False(f3.Value.IsOnline);
        Assert.Null(JsonHidProfileProvider.ParseWithProfile(profile, offline));
    }

    [Fact]
    public void Elan_SelectBestPassiveCandidate_MergesWiredAndWirelessProperly()
    {
        var profile = CreateThroneRepelProfile();
        var now = DateTimeOffset.UtcNow;

        var offlineDongle = new JsonHidProfileProvider.PassiveCandidateReading(
            0x026F, 60, false, false, true, now, "HighSpeed Receiver");
        var onlineDongle = new JsonHidProfileProvider.PassiveCandidateReading(
            0x026F, 72, false, true, true, now, "HighSpeed Receiver");
        var offlineWired = new JsonHidProfileProvider.PassiveCandidateReading(
            0x026E, 72, false, false, true, now, "Gaming Mouse Wired");
        var onlineWired = new JsonHidProfileProvider.PassiveCandidateReading(
            0x026E, 75, true, true, true, now, "Gaming Mouse Wired");

        // 1. Online wired beats offline dongle
        Assert.Equal(0x026E, JsonHidProfileProvider.SelectBestPassiveCandidate(profile, [offlineDongle, onlineWired])!.Pid);
        // 2. Online wired beats online dongle (plugged in takes priority)
        Assert.Equal(0x026E, JsonHidProfileProvider.SelectBestPassiveCandidate(profile, [onlineDongle, onlineWired])!.Pid);
        // 3. Online dongle beats offline wired
        Assert.Equal(0x026F, JsonHidProfileProvider.SelectBestPassiveCandidate(profile, [offlineWired, onlineDongle])!.Pid);
        // 4. Offline dongle still returns candidate so sleep state is preserved
        var onlyOffline = JsonHidProfileProvider.SelectBestPassiveCandidate(profile, [offlineDongle]);
        Assert.NotNull(onlyOffline);
        Assert.False(onlyOffline.IsOnline);
    }

    [Fact]
    public void EmbeddedDatabase_ContainsThroneRepel8KProfile()
    {
        var db = RemoteProfileSyncService.LoadEmbeddedBaselineDatabase();
        Assert.NotNull(db);

        var throne = db.Profiles.FirstOrDefault(p =>
            string.Equals(p.VendorIdHex, "0x04F3", StringComparison.OrdinalIgnoreCase) &&
            p.ProductIdsHex.Contains("0x026F", StringComparer.OrdinalIgnoreCase));

        Assert.NotNull(throne);
        Assert.Equal("Throne Repel 8K", throne.Name);
        Assert.Contains("0x026E", throne.ProductIdsHex, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("0x026E", throne.WiredProductIdsHex, StringComparer.OrdinalIgnoreCase);
        Assert.True(throne.ReadPassiveInput);
        Assert.True(throne.MergeProductIds);
        Assert.Equal(4, throne.ExpectedReportId);
        Assert.Equal(1, throne.LinkStatusByteIndex);
        Assert.Equal(4, throne.BatteryByteIndex);
        Assert.Equal(5, throne.ChargingByteIndex);
    }

    [Fact]
    public void AnonymousReport_FiltersSystemNoiseAndSuppressesSiblingPid()
    {
        var active = new List<DeviceBatteryInfo>
        {
            new()
            {
                Id = "profile-04F3-026F",
                Name = "Throne Repel 8K",
                Category = DeviceCategory.Mouse,
                BatteryPercent = 80,
                IsConnected = true
            }
        };

        var snapshots = new List<HidDeviceSnapshot>
        {
            new(0x00FF, 0xBACC, "Virtual Multitouch Device", 65, 65, 0), // non-battery noise -> filtered
            new(0x048D, 0x5702, "ITE Device", 0, 0, 64),                 // non-battery RGB EC -> filtered
            new(0x04F3, 0x026E, "Gaming Mouse Wired", 0, 0, 136),        // sibling PID of detected Throne -> suppressed
            new(0x04F3, 0x026F, "HighSpeed Receiver", 0, 0, 136),        // detected -> suppressed
            new(0x1234, 0x5678, "Genuine Unknown Mouse", 64, 64, 0)      // genuine unknown -> kept
        };

        var payload = AnonymousReportService.BuildPayload(
            active,
            snapshots,
            "1.3.3",
            [CreateThroneRepelProfile()]);

        Assert.Single(payload.UnknownHidCandidates);
        Assert.Equal("0x1234", payload.UnknownHidCandidates[0].Vid);
        Assert.Equal("0x5678", payload.UnknownHidCandidates[0].Pid);
    }
}
