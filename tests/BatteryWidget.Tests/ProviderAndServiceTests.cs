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

    [Fact]
    public void Bluetooth_FilterAndMapConnectedPnpBatteries_SupportsMultipleDevicesAndFiltersStaleNames()
    {
        var connected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cid_headset"] = "Sony WH-1000XM5",
            ["cid_xbox"] = "Xbox Wireless Controller"
        };

        var rawPnp = new List<(string ContainerId, byte BatteryPercent, string? FallbackName)>
        {
            ("cid_headset", 80, "Bluetooth Audio"),
            ("cid_xbox", 65, "Old Disconnected Headset"), // stale/child PnP name must be overridden by AEP name
            ("cid_stale_old", 40, "Old Headphone")         // not connected -> must be filtered out
        };

        var mapped = BluetoothBatteryProvider.FilterAndMapConnectedPnpBatteries(rawPnp, connected);

        Assert.Equal(2, mapped.Count);
        Assert.Contains(mapped, d => d.Name == "Sony WH-1000XM5" && d.BatteryPercent == 80 && d.Category == DeviceCategory.Headset);
        Assert.Contains(mapped, d => d.Name == "Xbox Wireless Controller" && d.BatteryPercent == 65 && d.Category == DeviceCategory.Gamepad);
        Assert.DoesNotContain(mapped, d => d.Name.Contains("Old", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MonitorService_DeduplicateBluetoothAndXInputGamepads_RemovesDuplicateQualitativeXInput()
    {
        var list = new List<DeviceBatteryInfo>
        {
            new()
            {
                Id = "bt-pnp-xbox1",
                Name = "Xbox Wireless Controller",
                Category = DeviceCategory.Gamepad,
                Connection = ConnectionKind.Bluetooth,
                BatteryPercent = 72,
                IsConnected = true
            },
            new()
            {
                Id = "xbox-pad-0",
                Name = "Xbox Controller",
                Category = DeviceCategory.Gamepad,
                Connection = ConnectionKind.Wireless24Ghz,
                BatteryPercent = -1,
                DisplayLevelText = "Orta",
                VisualFillPercent = 65,
                IsConnected = true
            }
        };

        BatteryMonitorService.DeduplicateBluetoothAndXInputGamepads(list);

        Assert.Single(list);
        Assert.Equal(72, list[0].BatteryPercent);
        Assert.Equal(ConnectionKind.Bluetooth, list[0].Connection);
    }

    [Fact]
    public void UniversalCompx_ParseCompxResponse_Parses17ByteAnd64ByteFormats()
    {
        var resp17 = new byte[17];
        resp17[0] = 0x08;
        resp17[1] = 0x04;
        resp17[6] = 85;
        resp17[7] = 1; // charging
        var res17 = UniversalCompxProvider.ParseCompxResponse(resp17);
        Assert.NotNull(res17);
        Assert.Equal(85, res17.Value.Percent);
        Assert.True(res17.Value.IsCharging);

        var resp64 = new byte[64];
        resp64[0] = 0x08;
        resp64[1] = 0x72;
        resp64[6] = 63;
        resp64[7] = 0;
        var res64 = UniversalCompxProvider.ParseCompxResponse(resp64);
        Assert.NotNull(res64);
        Assert.Equal(63, res64.Value.Percent);
        Assert.False(res64.Value.IsCharging);
    }

    [Fact]
    public void LogitechHidpp_ParseHidppBatteryResponse_ParsesUnifiedAndLegacy()
    {
        var unified = new byte[20];
        unified[0] = 0x11;
        unified[4] = 90;
        unified[6] = 1; // recharging
        var resUnified = LogitechHidppProvider.ParseHidppBatteryResponse(unified, isUnified: true);
        Assert.NotNull(resUnified);
        Assert.Equal(90, resUnified.Value.Percent);
        Assert.True(resUnified.Value.IsCharging);

        var legacy = new byte[20];
        legacy[0] = 0x11;
        legacy[4] = 45;
        legacy[5] = 0; // discharging
        var resLegacy = LogitechHidppProvider.ParseHidppBatteryResponse(legacy, isUnified: false);
        Assert.NotNull(resLegacy);
        Assert.Equal(45, resLegacy.Value.Percent);
        Assert.False(resLegacy.Value.IsCharging);
    }

    [Fact]
    public void Razer_ComputeChecksumAndParseBatteryPercent_WorksCorrectly()
    {
        var payload = new byte[90];
        payload[0] = 0x00;
        payload[1] = 0x1F;
        payload[5] = 0x02;
        payload[6] = 0x07;
        payload[7] = 0x80;
        byte crc = RazerBatteryProvider.ComputeRazerChecksum(payload);
        Assert.Equal(0x02 ^ 0x07 ^ 0x80, crc);

        var resp = new byte[90];
        resp[0] = 0x02;
        resp[9] = 128; // ~50% of 255
        var pct = RazerBatteryProvider.ParseRazerBatteryPercent(resp);
        Assert.Equal(50, pct);
    }

    [Fact]
    public void HeadsetVendors_ParseSteelSeriesResponse_ParsesNovaAndArctis7()
    {
        var nova = new byte[] { 0x06, 0x18, 75, 0x01 };
        var resNova = HeadsetVendorsProvider.ParseSteelSeriesResponse(nova);
        Assert.NotNull(resNova);
        Assert.Equal(75, resNova.Value.Percent);
        Assert.True(resNova.Value.IsCharging);

        var arctis7 = new byte[] { 0xB0, 0x00, 60, 0x00 };
        var resA7 = HeadsetVendorsProvider.ParseSteelSeriesResponse(arctis7);
        Assert.NotNull(resA7);
        Assert.Equal(60, resA7.Value.Percent);
        Assert.False(resA7.Value.IsCharging);
    }

    [Fact]
    public void FingerprintEngine_ParseRapoo8KReport_ParsesDischargingAndCharging()
    {
        var pkt = new byte[19];
        pkt[0] = 0x07;
        pkt[7] = 1; // discharging
        pkt[8] = 73;
        var res1 = UniversalHidFingerprintProvider.ParseRapoo8KReport(pkt);
        Assert.NotNull(res1);
        Assert.Equal(73, res1.Value.Percent);
        Assert.False(res1.Value.IsCharging);

        pkt[7] = 2; // charging
        pkt[8] = 95;
        var res2 = UniversalHidFingerprintProvider.ParseRapoo8KReport(pkt);
        Assert.NotNull(res2);
        Assert.Equal(95, res2.Value.Percent);
        Assert.True(res2.Value.IsCharging);
    }

    [Fact]
    public void AutoLearn_FindAndRefineCandidates_NarrowsDownExactBatteryByte()
    {
        byte[] packetAt74 = [0x08, 0x04, 74, 0x10, 74, 0x00];
        byte[] packetAt73 = [0x08, 0x04, 74, 0x10, 73, 0x00];

        var first = AutoLearnService.FindMatchingByteCandidates(
            0x24AE, 0x1417, "8K Mouse", false, true, 0x08, null, packetAt74, 74);
        Assert.Equal(2, first.Count); // indices 2 and 4 both matched 74

        var second = AutoLearnService.FindMatchingByteCandidates(
            0x24AE, 0x1417, "8K Mouse", false, true, 0x08, null, packetAt73, 73);
        var refined = AutoLearnService.RefineCandidates(first, second);

        Assert.Single(refined);
        Assert.Equal(4, refined[0].BatteryByteIndex);
    }

    [Fact]
    public void JsonHidProfile_ParseWithProfile_AppliesMaskAndExpectedReportId()
    {
        var profile = new HidDeviceProfile
        {
            Name = "Learned Mouse",
            ExpectedReportId = 0xB6,
            BatteryByteIndex = 20,
            BatteryMask = 0x7F,
            MaxBatteryValue = 100
        };

        var wrongReport = new byte[24];
        wrongReport[0] = 0x05;
        wrongReport[20] = 0x80 | 64;
        Assert.Null(JsonHidProfileProvider.ParseWithProfile(profile, wrongReport));

        var rightReport = new byte[24];
        rightReport[0] = 0xB6;
        rightReport[20] = 0x80 | 64;
        var parsed = JsonHidProfileProvider.ParseWithProfile(profile, rightReport);
        Assert.NotNull(parsed);
        Assert.Equal(64, parsed.Value.Percent);
    }

    [Fact]
    public void HeadsetVendors_ParseAtkN9SerialBattery_ParsesPercentAndCharging()
    {
        var pkt = new byte[64];
        pkt[0] = 0x04; // len
        pkt[1] = 0x00; // AUTH
        pkt[2] = 0x03; // GetBattery
        pkt[4] = 76;   // 76%, not charging
        var res = HeadsetVendorsProvider.ParseAtkN9SerialBattery(pkt);
        Assert.NotNull(res);
        Assert.Equal(76, res.Value.Percent);
        Assert.False(res.Value.IsCharging);

        pkt[4] = 0x80 | 88; // bit 7 = charging
        var resChg = HeadsetVendorsProvider.ParseAtkN9SerialBattery(pkt);
        Assert.NotNull(resChg);
        Assert.Equal(88, resChg.Value.Percent);
        Assert.True(resChg.Value.IsCharging);
    }

    [Fact]
    public void HeadsetVendors_ParseAtkN9BolutekBattery_ParsesReport55AndRaw65()
    {
        byte[] rep55 = [0x55, 0x65, 67, 0x02];
        var res55 = HeadsetVendorsProvider.ParseAtkN9BolutekBattery(rep55);
        Assert.NotNull(res55);
        Assert.Equal(67, res55.Value.Percent);
        Assert.True(res55.Value.IsCharging);

        byte[] raw65 = [0x65, 52, 0x01];
        var res65 = HeadsetVendorsProvider.ParseAtkN9BolutekBattery(raw65);
        Assert.NotNull(res65);
        Assert.Equal(52, res65.Value.Percent);
        Assert.False(res65.Value.IsCharging);
    }

    [Fact]
    public void UniversalCompx_ParseCompxResponse_ParsesZeroPrefixedPaw3950Frame()
    {
        var resp = new byte[18];
        resp[0] = 0x00;
        resp[1] = 0x08;
        resp[2] = 0x04;
        resp[7] = 91;
        resp[8] = 1;
        var parsed = UniversalCompxProvider.ParseCompxResponse(resp);
        Assert.NotNull(parsed);
        Assert.Equal(91, parsed.Value.Percent);
        Assert.True(parsed.Value.IsCharging);
    }

    [Fact]
    public void MonitorService_FilterHiddenDevices_ExcludesHiddenIdsAndRestoresWhenRemoved()
    {
        var devices = new List<DeviceBatteryInfo>
        {
            new() { Id = "dev-a", Name = "A", BatteryPercent = 80 },
            new() { Id = "dev-b", Name = "B", BatteryPercent = 50 }
        };
        var hidden = new List<HiddenDeviceEntry>
        {
            new() { Id = "dev-b", Name = "B" }
        };

        var visible = BatteryMonitorService.FilterHiddenDevices(devices, hidden);
        Assert.Single(visible);
        Assert.Equal("dev-a", visible[0].Id);

        hidden.Clear();
        var restored = BatteryMonitorService.FilterHiddenDevices(devices, hidden);
        Assert.Equal(2, restored.Count);
    }
}
