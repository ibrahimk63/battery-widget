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
        var baseInfo = new byte[64];
        baseInfo[0] = 0xB6;
        baseInfo[1] = 0x05;
        baseInfo[20] = 54;
        var res1 = DarmosharkM3V2Provider.ParseBatteryResponse(baseInfo);
        Assert.NotNull(res1);
        Assert.Equal(54, res1.Value.Percent);
        Assert.False(res1.Value.IsCharging);

        baseInfo[20] = 0x80 | 54;
        var resCharging = DarmosharkM3V2Provider.ParseBatteryResponse(baseInfo);
        Assert.NotNull(resCharging);
        Assert.Equal(54, resCharging.Value.Percent);
        Assert.True(resCharging.Value.IsCharging);

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

        service.Evaluate([lowDevice], settings);
        Assert.Equal(1, notifications);

        service.Evaluate([lowDevice], settings);
        Assert.Equal(1, notifications);

        service.Evaluate([lowDevice with { IsCharging = true }], settings);

        service.Evaluate([lowDevice], settings);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void NotificationService_TriggersOnQualitativeLowLevel()
    {
        int notifications = 0;
        var service = new NotificationService((_, _) => notifications++);
        var settings = new AppSettings { EnableLowBatteryNotifications = true, LowBatteryThreshold = 15 };

        var xboxPad = new DeviceBatteryInfo
        {
            Id = "xbox-pad-0",
            Name = "Xbox Controller",
            BatteryPercent = -1,
            DisplayLevelText = "Düşük",
            VisualFillPercent = 25,
            IsConnected = true
        };

        service.Evaluate([xboxPad], settings);
        Assert.Equal(1, notifications);

        service.Evaluate([xboxPad], settings);
        Assert.Equal(1, notifications);

        service.Evaluate([xboxPad with { DisplayLevelText = "Dolu", VisualFillPercent = 100 }], settings);
        service.Evaluate([xboxPad], settings);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void Gamepad_MapXInputBattery_MapsQualitativeLevelsWithoutFakePercent()
    {
        Assert.Equal(("Dolu", 100, false), GamepadBatteryProvider.MapXInputBattery(
            GamepadBatteryProvider.BATTERY_TYPE_ALKALINE, GamepadBatteryProvider.BATTERY_LEVEL_FULL));
        Assert.Equal(("Orta", 65, false), GamepadBatteryProvider.MapXInputBattery(
            GamepadBatteryProvider.BATTERY_TYPE_NIMH, GamepadBatteryProvider.BATTERY_LEVEL_MEDIUM));
        Assert.Equal(("Düşük", 25, false), GamepadBatteryProvider.MapXInputBattery(
            GamepadBatteryProvider.BATTERY_TYPE_ALKALINE, GamepadBatteryProvider.BATTERY_LEVEL_LOW));
        Assert.Equal(("Boş", 8, false), GamepadBatteryProvider.MapXInputBattery(
            GamepadBatteryProvider.BATTERY_TYPE_ALKALINE, GamepadBatteryProvider.BATTERY_LEVEL_EMPTY));
        Assert.Equal(("Kablolu", 100, true), GamepadBatteryProvider.MapXInputBattery(
            GamepadBatteryProvider.BATTERY_TYPE_WIRED, GamepadBatteryProvider.BATTERY_LEVEL_FULL));
        Assert.Null(GamepadBatteryProvider.MapXInputBattery(
            GamepadBatteryProvider.BATTERY_TYPE_DISCONNECTED, 0));
    }

    [Fact]
    public void Gamepad_ParseDualSenseAndDualShock4_ParsesPercentAndCharging()
    {
        var dsUsb = new byte[64];
        dsUsb[0] = 0x01;
        dsUsb[53] = 0x15; // status=1 (charging), level=5 -> 60%
        var resDs = GamepadBatteryProvider.ParseDualSenseReport(dsUsb);
        Assert.NotNull(resDs);
        Assert.Equal(60, resDs.Value.Percent);
        Assert.True(resDs.Value.IsCharging);
        Assert.False(resDs.Value.IsBluetooth);

        var ds4Bt = new byte[64];
        ds4Bt[0] = 0x11;
        ds4Bt[32] = 0x07; // cable=0, level=7 -> 80%
        var resDs4 = GamepadBatteryProvider.ParseDualShock4Report(ds4Bt);
        Assert.NotNull(resDs4);
        Assert.Equal(80, resDs4.Value.Percent);
        Assert.False(resDs4.Value.IsCharging);
        Assert.True(resDs4.Value.IsBluetooth);
    }

    [Fact]
    public void DeviceBatteryInfo_UsesDisplayLevelTextAndEffectiveRingPercent()
    {
        var dev = new DeviceBatteryInfo
        {
            Id = "xbox-pad-0",
            Name = "Xbox Controller",
            BatteryPercent = -1,
            DisplayLevelText = "Orta",
            VisualFillPercent = 65
        };

        Assert.Equal("Orta", dev.PercentText);
        Assert.Equal(65, dev.EffectiveRingPercent);
    }

    [Fact]
    public void Bluetooth_NormalizeBluetoothName_StripsAudioProfileSuffixes()
    {
        Assert.Equal("JBL Tune 760NC", BluetoothBatteryProvider.NormalizeBluetoothName("JBL Tune 760NC Hands-Free AG Audio"));
        Assert.Equal("AirPods Pro", BluetoothBatteryProvider.NormalizeBluetoothName("AirPods Pro Stereo"));
    }
}
