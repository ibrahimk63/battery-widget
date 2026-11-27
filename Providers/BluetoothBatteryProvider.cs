using System.Runtime.InteropServices;
using BatteryWidget.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryWidget.Providers;

/// <summary>
/// Discovers connected Bluetooth devices (both Classic Bluetooth HFP/HID via Windows SetupAPI
/// DEVPKEY_Bluetooth_Battery and Bluetooth LE devices via GATT Battery Service 0x180F / 0x2A19).
/// </summary>
public sealed class BluetoothBatteryProvider : IBatteryProvider
{
    public string ProviderName => "Bluetooth (Classic + BLE)";

    public async Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DeviceBatteryInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Source 1: Fast Win32 SetupAPI scan for Windows DEVPKEY_Bluetooth_Battery (covers Classic + cached BLE)
        var pnpDevices = await Task.Run(() => ReadWindowsBluetoothBatteries(cancellationToken), cancellationToken);
        foreach (var dev in pnpDevices)
        {
            var norm = NormalizeBluetoothName(dev.Name);
            if (seenNames.Add(norm))
                results.Add(dev);
        }

        // Source 2: BLE GATT Battery Service fallback for BLE devices without DEVPKEY_Bluetooth_Battery
        var bleDevices = await ReadBleGattBatteriesAsync(cancellationToken);
        foreach (var dev in bleDevices)
        {
            var norm = NormalizeBluetoothName(dev.Name);
            if (seenNames.Add(norm))
                results.Add(dev);
        }

        return results;
    }

    public static string NormalizeBluetoothName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var clean = name.Trim();
        string[] suffixes = [" Hands-Free AG Audio", " Hands-Free", " Stereo", " Avrcp Transport", " LE"];
        foreach (var suffix in suffixes)
        {
            if (clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                clean = clean[..^suffix.Length].Trim();
        }
        return clean;
    }

    private static List<DeviceBatteryInfo> ReadWindowsBluetoothBatteries(CancellationToken ct)
    {
        var list = new List<DeviceBatteryInfo>();
        IntPtr devInfoSet = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            return list;

        try
        {
            var devInfoData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            uint index = 0;

            while (SetupDiEnumDeviceInfo(devInfoSet, index++, ref devInfoData))
            {
                ct.ThrowIfCancellationRequested();

                if (!TryGetByteProperty(devInfoSet, ref devInfoData, DEVPKEY_Bluetooth_Battery, out byte battery))
                    continue;

                if (battery > 100)
                    continue;

                string? rawName = TryGetStringProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_FriendlyName)
                                  ?? TryGetStringProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_DeviceDesc);

                string cleanName = NormalizeBluetoothName(rawName);
                if (string.IsNullOrWhiteSpace(cleanName))
                    cleanName = "Bluetooth Cihazı";

                string containerId = TryGetGuidProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_ContainerId)
                                     ?? cleanName;

                list.Add(new DeviceBatteryInfo
                {
                    Id = $"bt-pnp-{containerId}",
                    Name = cleanName,
                    Category = GuessCategory(cleanName),
                    Connection = ConnectionKind.Bluetooth,
                    BatteryPercent = battery,
                    IsCharging = false,
                    IsConnected = true
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore SetupAPI enumeration errors
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return list;
    }

    private static async Task<List<DeviceBatteryInfo>> ReadBleGattBatteriesAsync(CancellationToken cancellationToken)
    {
        var results = new List<DeviceBatteryInfo>();

        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var devices = await DeviceInformation.FindAllAsync(selector);

            foreach (var devInfo in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(1500);

                    using var bleDevice = await BluetoothLEDevice.FromIdAsync(devInfo.Id).AsTask(timeoutCts.Token);
                    if (bleDevice is null)
                        continue;

                    var servicesResult = await bleDevice.GetGattServicesForUuidAsync(
                        GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask(timeoutCts.Token);

                    if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                        continue;

                    using var batteryService = servicesResult.Services[0];
                    var charsResult = await batteryService.GetCharacteristicsForUuidAsync(
                        GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached).AsTask(timeoutCts.Token);

                    if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                        continue;

                    var readResult = await charsResult.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(timeoutCts.Token);
                    if (readResult.Status == GattCommunicationStatus.Success && readResult.Value.Length > 0)
                    {
                        using var reader = DataReader.FromBuffer(readResult.Value);
                        int percent = Math.Clamp((int)reader.ReadByte(), 0, 100);
                        string name = NormalizeBluetoothName(
                            string.IsNullOrWhiteSpace(bleDevice.Name) ? devInfo.Name : bleDevice.Name);

                        results.Add(new DeviceBatteryInfo
                        {
                            Id = $"ble-{bleDevice.BluetoothAddress:X}",
                            Name = string.IsNullOrWhiteSpace(name) ? "Bluetooth LE Cihazı" : name,
                            Category = GuessCategory(name),
                            Connection = ConnectionKind.Bluetooth,
                            BatteryPercent = percent,
                            IsCharging = false,
                            IsConnected = true
                        });
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Skip unreachable or timed-out BLE devices
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Bluetooth radio may be off or unavailable
        }

        return results;
    }

    public static DeviceCategory GuessCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DeviceCategory.Other;

        if (name.Contains("mouse", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("fare", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("m3", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx master", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx anywhere", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Mouse;

        if (name.Contains("headset", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("headphone", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("cloud", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("buds", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("airpods", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wh-", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wf-", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("jbl", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("kulak", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Headset;

        if (name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("klavye", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("keychron", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Keyboard;

        if (name.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("xbox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dualsense", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dualshock", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("gamepad", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Gamepad;

        return DeviceCategory.Other;
    }

    #region Win32 SetupAPI P/Invoke

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint DEVPROP_TYPE_BYTE = 0x00000003;
    private const uint DEVPROP_TYPE_GUID = 0x0000000D;
    private const uint DEVPROP_TYPE_STRING = 0x00000012;

    private static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery = new()
    {
        fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        pid = 2
    };

    private static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 2
    };

    private static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    private static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new()
    {
        fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        pid = 2
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        in DEVPROPKEY PropertyKey,
        out uint PropertyType,
        byte[]? PropertyBuffer,
        uint PropertyBufferSize,
        out uint RequiredSize,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private static bool TryGetByteProperty(IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfoData, in DEVPROPKEY key, out byte value)
    {
        value = 0;
        var buf = new byte[4];
        if (!SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, key, out uint propType, buf, (uint)buf.Length, out uint reqSize, 0))
            return false;

        if (propType != DEVPROP_TYPE_BYTE || reqSize < 1)
            return false;

        value = buf[0];
        return true;
    }

    private static string? TryGetStringProperty(IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfoData, in DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, key, out uint propType, null, 0, out uint reqSize, 0);
        if (reqSize == 0 || reqSize > 4096)
            return null;

        var buf = new byte[reqSize];
        if (!SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, key, out propType, buf, reqSize, out _, 0))
            return null;

        if (propType != DEVPROP_TYPE_STRING)
            return null;

        return Marshal.PtrToStringUni(Marshal.UnsafeAddrOfPinnedArrayElement(buf, 0))?.TrimEnd('\0');
    }

    private static string? TryGetGuidProperty(IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfoData, in DEVPROPKEY key)
    {
        var buf = new byte[16];
        if (!SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, key, out uint propType, buf, 16, out uint reqSize, 0))
            return null;

        if (propType != DEVPROP_TYPE_GUID || reqSize != 16)
            return null;

        return new Guid(buf).ToString("N");
    }

    #endregion
}
