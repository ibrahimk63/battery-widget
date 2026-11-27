using System.Runtime.InteropServices;
using BatteryWidget.Models;
using HidSharp;
using Windows.Devices.Power;
using Windows.System.Power;

namespace BatteryWidget.Providers;

/// <summary>
/// Reads battery status from Xbox controllers (Xbox Wireless Adapter dongle / USB / Bluetooth via XInput + Windows.Gaming.Input)
/// and PlayStation controllers (DualSense / DualShock 4 via USB/Bluetooth HID).
/// </summary>
public sealed class GamepadBatteryProvider : IBatteryProvider
{
    private const int SonyVendorId = 0x054C;
    private static readonly int[] DualSenseProductIds = [0x0CE6, 0x0DF2]; // DualSense, DualSense Edge
    private static readonly int[] DualShock4ProductIds = [0x05C4, 0x09CC]; // DS4 v1, DS4 v2

    public string ProviderName => "Oyun Kolları (Xbox & PlayStation)";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            results.AddRange(PollXboxControllers(cancellationToken));
            results.AddRange(PollPlayStationControllers(cancellationToken));
            return results;
        }, cancellationToken);
    }

    private static List<DeviceBatteryInfo> PollXboxControllers(CancellationToken ct)
    {
        var found = new List<DeviceBatteryInfo>();
        var activeSlots = new List<(uint Index, XINPUT_BATTERY_INFORMATION Info)>();

        for (uint i = 0; i < 4; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (XInputGetState(i, out _) != 0)
                    continue;

                if (XInputGetBatteryInformation(i, BATTERY_DEVTYPE_GAMEPAD, out var batInfo) == 0)
                    activeSlots.Add((i, batInfo));
            }
            catch
            {
                // XInput1_4.dll may not be present on minimal Windows builds
                break;
            }
        }

        IReadOnlyList<Windows.Gaming.Input.Gamepad>? wgiGamepads = null;
        try { wgiGamepads = Windows.Gaming.Input.Gamepad.Gamepads; }
        catch { /* Ignore WGI unavailable */ }

        for (int idx = 0; idx < activeSlots.Count; idx++)
        {
            var (slot, batInfo) = activeSlots[idx];
            string label = activeSlots.Count == 1 ? "Xbox Controller" : $"Xbox Controller #{slot + 1}";

            // Try Windows.Gaming.Input for exact mWh percentage if firmware provides it
            if (wgiGamepads is not null && idx < wgiGamepads.Count)
            {
                try
                {
                    var report = wgiGamepads[idx].TryGetBatteryReport();
                    if (report is { RemainingCapacityInMilliwattHours: > 0, FullChargeCapacityInMilliwattHours: > 0 })
                    {
                        int pct = Math.Clamp((int)Math.Round(
                            report.RemainingCapacityInMilliwattHours.Value * 100.0 /
                            report.FullChargeCapacityInMilliwattHours.Value), 0, 100);
                        bool charging = report.Status == BatteryStatus.Charging;

                        found.Add(new DeviceBatteryInfo
                        {
                            Id = $"xbox-pad-{slot}",
                            Name = label,
                            Category = DeviceCategory.Gamepad,
                            Connection = batInfo.BatteryType == BATTERY_TYPE_WIRED ? ConnectionKind.UsbWired : ConnectionKind.Wireless24Ghz,
                            BatteryPercent = pct,
                            IsCharging = charging,
                            IsConnected = true
                        });
                        continue;
                    }
                }
                catch
                {
                    // Fall back to discrete XInput level
                }
            }

            var mapped = MapXInputBattery(batInfo.BatteryType, batInfo.BatteryLevel);
            if (mapped is not null)
            {
                found.Add(new DeviceBatteryInfo
                {
                    Id = $"xbox-pad-{slot}",
                    Name = label,
                    Category = DeviceCategory.Gamepad,
                    Connection = mapped.Value.IsWired ? ConnectionKind.UsbWired : ConnectionKind.Wireless24Ghz,
                    BatteryPercent = -1,
                    DisplayLevelText = mapped.Value.LevelText,
                    VisualFillPercent = mapped.Value.VisualFill,
                    IsCharging = mapped.Value.IsWired,
                    IsConnected = true
                });
            }
        }

        return found;
    }

    public static (string LevelText, int VisualFill, bool IsWired)? MapXInputBattery(byte batteryType, byte batteryLevel)
    {
        if (batteryType == BATTERY_TYPE_DISCONNECTED)
            return null;

        if (batteryType == BATTERY_TYPE_WIRED)
            return ("Kablolu", 100, true);

        if (batteryType is BATTERY_TYPE_ALKALINE or BATTERY_TYPE_NIMH or BATTERY_TYPE_UNKNOWN)
        {
            return batteryLevel switch
            {
                BATTERY_LEVEL_FULL => ("Dolu", 100, false),
                BATTERY_LEVEL_MEDIUM => ("Orta", 65, false),
                BATTERY_LEVEL_LOW => ("Düşük", 25, false),
                BATTERY_LEVEL_EMPTY => ("Boş", 8, false),
                _ => null
            };
        }

        return null;
    }

    private static List<DeviceBatteryInfo> PollPlayStationControllers(CancellationToken ct)
    {
        var results = new List<DeviceBatteryInfo>();
        var sonyDevices = DeviceList.Local.GetHidDevices(SonyVendorId)
            .Where(d => DualSenseProductIds.Contains(d.ProductID) || DualShock4ProductIds.Contains(d.ProductID))
            .ToList();

        foreach (var device in sonyDevices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!device.TryOpen(out var stream))
                    continue;

                using (stream)
                {
                    stream.ReadTimeout = 150;
                    var buffer = new byte[Math.Max(device.GetMaxInputReportLength(), 64)];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        continue;

                    var span = buffer.AsSpan(0, read);
                    if (DualSenseProductIds.Contains(device.ProductID))
                    {
                        var parsed = ParseDualSenseReport(span);
                        if (parsed.HasValue)
                        {
                            results.Add(new DeviceBatteryInfo
                            {
                                Id = $"sony-ds-{device.ProductID:X4}",
                                Name = device.ProductID == 0x0DF2 ? "DualSense Edge" : "DualSense",
                                Category = DeviceCategory.Gamepad,
                                Connection = parsed.Value.IsBluetooth ? ConnectionKind.Bluetooth : ConnectionKind.UsbWired,
                                BatteryPercent = parsed.Value.Percent,
                                IsCharging = parsed.Value.IsCharging,
                                IsConnected = true
                            });
                        }
                    }
                    else if (DualShock4ProductIds.Contains(device.ProductID))
                    {
                        var parsed = ParseDualShock4Report(span);
                        if (parsed.HasValue)
                        {
                            results.Add(new DeviceBatteryInfo
                            {
                                Id = $"sony-ds4-{device.ProductID:X4}",
                                Name = "DualShock 4",
                                Category = DeviceCategory.Gamepad,
                                Connection = parsed.Value.IsBluetooth ? ConnectionKind.Bluetooth : ConnectionKind.UsbWired,
                                BatteryPercent = parsed.Value.Percent,
                                IsCharging = parsed.Value.IsCharging,
                                IsConnected = true
                            });
                        }
                    }
                }
            }
            catch
            {
                // Ignore busy or timed-out controller HID interfaces
            }
        }

        return results;
    }

    public static (int Percent, bool IsCharging, bool IsBluetooth)? ParseDualSenseReport(ReadOnlySpan<byte> data)
    {
        if (data.Length < 54)
            return null;

        bool isBt = data[0] == 0x31;
        int idx = data[0] switch
        {
            0x01 when data.Length >= 54 => 53,
            0x31 when data.Length >= 55 => 54,
            _ => -1
        };

        if (idx < 0)
            return null;

        byte raw = data[idx];
        int level = raw & 0x0F;
        int status = (raw >> 4) & 0x0F;

        bool isCharging = status == 1;
        int percent = status == 2 ? 100 : Math.Clamp((level + 1) * 10, 0, 100);
        if (level == 0 && status == 0)
            percent = 5;

        return (percent, isCharging, isBt);
    }

    public static (int Percent, bool IsCharging, bool IsBluetooth)? ParseDualShock4Report(ReadOnlySpan<byte> data)
    {
        bool isBt = data.Length >= 33 && data[0] == 0x11;
        int idx = data[0] switch
        {
            0x01 when data.Length >= 31 => 30,
            0x11 when data.Length >= 33 => 32,
            _ => -1
        };

        if (idx < 0)
            return null;

        byte raw = data[idx];
        bool cable = (raw & 0x10) != 0;
        int level = raw & 0x0F;

        int percent = cable
            ? Math.Clamp(level * 10, 0, 100)
            : Math.Clamp((level + 1) * 10, 0, 100);

        bool isCharging = cable && level <= 10;
        return (percent, isCharging, isBt);
    }

    #region XInput P/Invoke

    private const byte BATTERY_DEVTYPE_GAMEPAD = 0x00;
    public const byte BATTERY_TYPE_DISCONNECTED = 0x00;
    public const byte BATTERY_TYPE_WIRED = 0x01;
    public const byte BATTERY_TYPE_ALKALINE = 0x02;
    public const byte BATTERY_TYPE_NIMH = 0x03;
    public const byte BATTERY_TYPE_UNKNOWN = 0xFF;

    public const byte BATTERY_LEVEL_EMPTY = 0x00;
    public const byte BATTERY_LEVEL_LOW = 0x01;
    public const byte BATTERY_LEVEL_MEDIUM = 0x02;
    public const byte BATTERY_LEVEL_FULL = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetBatteryInformation(uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

    #endregion
}
