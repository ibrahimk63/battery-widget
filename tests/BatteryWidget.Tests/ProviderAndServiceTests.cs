using BatteryWidget.Models;
using BatteryWidget.Providers;
using BatteryWidget.Services;
using Xunit;

namespace BatteryWidget.Tests;

public class ProviderAndServiceTests
{
    [Fact]
    public void HyperX_ParseBatteryResponse_Parses62ByteProtocolAndLegacy()
    {
        byte[] packet62 = [0x66, 0x89, 0x00, 0x00, 84, 0x01, 0x00]; // 84%, charging
        var res62 = HyperXCloud3Provider.ParseBatteryResponse(packet62);
        Assert.NotNull(res62);
        Assert.Equal(84, res62.Value.Percent);
        Assert.True(res62.Value.IsCharging);

        byte[] legacy = [0x06, 0xFF, 0xBB, 0x02, 0x00, 0x4E, 0x01, 0x00]; // 0x4E = 78%, charging = 1
        var resLegacy = HyperXCloud3Provider.ParseBatteryResponse(legacy);
        Assert.NotNull(resLegacy);
        Assert.Equal(78, resLegacy.Value.Percent);
        Assert.True(resLegacy.Value.IsCharging);
    }

    [Fact]
    public void Darmoshark_ParseBatteryResponse_ParsesRealtekB6BaseInfoAndLiveEvent()
    {
        // Realtek 64-byte Base Info: Report ID 0xB6, cmd 0x05, byte[20] = 0x36 (54%, not charging)
        var baseInfo = new byte[64];
        baseInfo[0] = 0xB6;
        baseInfo[1] = 0x05;
        baseInfo[20] = 54;
        var res1 = DarmosharkM3V2Provider.ParseBatteryResponse(baseInfo);
        Assert.NotNull(res1);
        Assert.Equal(54, res1.Value.Percent);
        Assert.False(res1.Value.IsCharging);

        // With bit 7 set -> charging
        baseInfo[20] = 0x80 | 54;
        var resCharging = DarmosharkM3V2Provider.ParseBatteryResponse(baseInfo);
        Assert.NotNull(resCharging);
        Assert.Equal(54, resCharging.Value.Percent);
        Assert.True(resCharging.Value.IsCharging);

        // Live event 0xE2: byte[4] = charging, byte[5] = percent
        var liveEvt = new byte[64];
        liveEvt[0] = 0xB6;
        liveEvt[1] = 0xE2;
        liveEvt[4] = 0x01;
        liveEvt[5] = 54;
        var resLive = DarmosharkM3V2Provider.ParseBatteryResponse(liveEvt);
        Assert.NotNull(resLive);
        Assert.Equal(54, resLive.Value.Percent);
        Assert.True(resLive.Value.IsCharging);
    }

    [Fact]
    public void JsonHidProfile_ParseWithProfile_ScalesAndReadsChargingBit()
    {
        var profile = new HidDeviceProfile
        {
            Name = "Test Mouse",
            BatteryByteIndex = 2,
            ChargingByteIndex = 3,
            ChargingMask = 0x02,
            MaxBatteryValue = 100
        };

        byte[] packet = [0x00, 0x01, 82, 0x02];
        var result = JsonHidProfileProvider.ParseWithProfile(profile, packet);

        Assert.NotNull(result);
        Assert.Equal(82, result.Value.Percent);
        Assert.True(result.Value.IsCharging);
    }

    [Fact]
    public void NotificationService_TriggersOnceBelowThreshold_AndResetsAfterCharging()
    {
        int notifications = 0;
        var service = new NotificationService((_, _) => notifications++);
        var settings = new AppSettings { EnableLowBatteryNotifications = true, LowBatteryThreshold = 15 };

        var lowDevice = new DeviceBatteryInfo
        {
            Id = "dev-1",
            Name = "Darmoshark M3 V2",
            BatteryPercent = 12,
            IsCharging = false,
            IsConnected = true
        };

        // First poll -> should notify
        service.Evaluate([lowDevice], settings);
        Assert.Equal(1, notifications);

        // Second poll at same level -> should NOT spam
        service.Evaluate([lowDevice], settings);
        Assert.Equal(1, notifications);

        // Device plugged in to charge -> resets alert state
        service.Evaluate([lowDevice with { IsCharging = true }], settings);

        // Drops again -> should notify again
        service.Evaluate([lowDevice], settings);
        Assert.Equal(2, notifications);
    }
}
