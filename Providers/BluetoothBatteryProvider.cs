using System.Runtime.InteropServices;
using BatteryWidget.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryWidget.Providers;

/// <summary>
/// Discovers all currently connected Bluetooth devices (Classic Bluetooth via Windows SetupAPI
/// DEVPKEY_Bluetooth_Battery and Bluetooth LE via GATT Battery Service 0x180F / 0x2A19).
/// Filters strictly against active Windows AEP connections keyed by ContainerId so stale
/// disconnected devices never appear and multiple simultaneous BT devices are all supported.
/// </summary>
public sealed class BluetoothBatteryProvider : IBatteryProvider
{
    public sealed record ConnectedBtDevice(string ContainerId, string Name, bool IsBle, string DeviceId);

    public string ProviderName => "Bluetooth (Classic + BLE)";

    public async Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DeviceBatteryInfo>();
        var connectedByContainer = await GetConnectedBluetoothAepMapAsync(cancellationToken);
        if (connectedByContainer.Count == 0)
            return results;

        var resolvedContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Source 1: Fast Win32 SetupAPI scan for Windows DEVPKEY_Bluetooth_Battery (Classic + cached BLE)
        var pnpReadings = await Task.Run(() => ReadRawPnpBluetoothBatteries(cancellationToken), cancellationToken);
        var connectedNames = connectedByContainer.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Name,
            StringComparer.OrdinalIgnoreCase);

        var pnpDevices = FilterAndMapConnectedPnpBatteries(pnpReadings, connectedNames);
        foreach (var dev in pnpDevices)
        {
            results.Add(dev);
            if (dev.Id.StartsWith("bt-pnp-", StringComparison.OrdinalIgnoreCase))
                resolvedContainers.Add(dev.Id["bt-pnp-".Length..]);
        }

        // Source 2: Parallel BLE GATT Battery Service fallback for connected BLE devices not yet resolved
        var bleDevices = await ReadBleGattBatteriesAsync(connectedByContainer, resolvedContainers, cancellationToken);
        results.AddRange(bleDevices);

        return results;
    }

    public static IReadOnlyList<DeviceBatteryInfo> FilterAndMapConnectedPnpBatteries(
        IEnumerable<(string ContainerId, byte BatteryPercent, string? FallbackName)> pnpReadings,
        IReadOnlyDictionary<string, string> connectedContainerNames)
    {
        var results = new List<DeviceBatteryInfo>();
        var seenContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (containerId, battery, fallbackName) in pnpReadings)
        {
            if (string.IsNullOrWhiteSpace(containerId) || battery > 100)
                continue;

            // Only include devices that are actually connected right now
            if (!connectedContainerNames.TryGetValue(containerId, out var aepName))
                continue;

            if (!seenContainers.Add(containerId))
                continue;

            string cleanName = NormalizeBluetoothName(aepName);
            if (string.IsNullOrWhiteSpace(cleanName))
                cleanName = NormalizeBluetoothName(fallbackName);
            if (string.IsNullOrWhiteSpace(cleanName))
                cleanName = "Bluetooth Cihazı";

            results.Add(new DeviceBatteryInfo
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

        return results;
    }

    private static async Task<Dictionary<string, ConnectedBtDevice>> GetConnectedBluetoothAepMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, ConnectedBtDevice>(StringComparer.OrdinalIgnoreCase);
        string[] extraProps = ["System.Devices.Aep.ContainerId", "System.Devices.Aep.DeviceAddress"];

        try
        {
            var classicSelector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var bleSelector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);

            var classicTask = DeviceInformation.FindAllAsync(classicSelector, extraProps).AsTask(ct);
            var bleTask = DeviceInformation.FindAllAsync(bleSelector, extraProps).AsTask(ct);

            await Task.WhenAll(classicTask, bleTask);

            foreach (var info in classicTask.Result)
                AddAepEntry(map, info, isBle: false);

            foreach (var info in bleTask.Result)
                AddAepEntry(map, info, isBle: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Bluetooth radio may be off or unavailable
        }

        return map;
    }

    private static void AddAepEntry(Dictionary<string, ConnectedBtDevice> map, DeviceInformation info, bool isBle)
    {
        string? containerId = null;
        if (info.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var rawCid) && rawCid is Guid guidCid)
            containerId = guidCid.ToString("N");
        else if (rawCid is string strCid && Guid.TryParse(strCid, out var parsedCid))
            containerId = parsedCid.ToString("N");

        if (string.IsNullOrWhiteSpace(containerId))
            return;

        string cleanName = NormalizeBluetoothName(info.Name);
        if (!map.TryGetValue(containerId, out var existing))
        {
            map[containerId] = new ConnectedBtDevice(containerId, cleanName, isBle, info.Id);
        }
        else if (isBle && !existing.IsBle)
        {
            // Keep BLE DeviceId if both Classic and BLE AEP exist for same container, preferring non-empty name
            string bestName = !string.IsNullOrWhiteSpace(cleanName) ? cleanName : existing.Name;
            map[containerId] = new ConnectedBtDevice(containerId, bestName, true, info.Id);
        }
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

    private static List<(string ContainerId, byte BatteryPercent, string? FallbackName)> ReadRawPnpBluetoothBatteries(CancellationToken ct)
    {
        var list = new List<(string ContainerId, byte BatteryPercent, string? FallbackName)>();
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

                string? containerId = TryGetGuidProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_ContainerId);
                if (string.IsNullOrWhiteSpace(containerId))
                    continue;

                string? fallbackName = TryGetStringProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_FriendlyName)
                                       ?? TryGetStringProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_DeviceDesc);

                list.Add((containerId, battery, fallbackName));
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

    private static async Task<List<DeviceBatteryInfo>> ReadBleGattBatteriesAsync(
        IReadOnlyDictionary<string, ConnectedBtDevice> connectedByContainer,
        HashSet<string> alreadyFoundContainerIds,
        CancellationToken cancellationToken)
    {
        var targets = connectedByContainer.Values
            .Where(d => d.IsBle && !alreadyFoundContainerIds.Contains(d.ContainerId))
            .ToList();

        if (targets.Count == 0)
            return [];

        var tasks = targets.Select(target => ReadSingleBleDeviceAsync(target, cancellationToken));
        var results = await Task.WhenAll(tasks);

        return results.Where(r => r is not null).Cast<DeviceBatteryInfo>().ToList();
    }

    private static async Task<DeviceBatteryInfo?> ReadSingleBleDeviceAsync(ConnectedBtDevice target, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(3000);

            using var bleDevice = await BluetoothLEDevice.FromIdAsync(target.DeviceId).AsTask(timeoutCts.Token);
            if (bleDevice is null)
                return null;

            var percent = await TryReadGattBatteryLevelAsync(bleDevice, BluetoothCacheMode.Cached, timeoutCts.Token)
                       ?? await TryReadGattBatteryLevelAsync(bleDevice, BluetoothCacheMode.Uncached, timeoutCts.Token);

            if (!percent.HasValue)
                return null;

            string name = !string.IsNullOrWhiteSpace(target.Name)
                ? target.Name
                : NormalizeBluetoothName(bleDevice.Name);

            if (string.IsNullOrWhiteSpace(name))
                name = "Bluetooth LE Cihazı";

            return new DeviceBatteryInfo
            {
                Id = $"bt-pnp-{target.ContainerId}",
                Name = name,
                Category = GuessCategory(name),
                Connection = ConnectionKind.Bluetooth,
                BatteryPercent = percent.Value,
                IsCharging = false,
                IsConnected = true
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> TryReadGattBatteryLevelAsync(
        BluetoothLEDevice bleDevice,
        BluetoothCacheMode cacheMode,
        CancellationToken ct)
    {
        var servicesResult = await bleDevice.GetGattServicesForUuidAsync(
            GattServiceUuids.Battery, cacheMode).AsTask(ct);

        if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            return null;

        using var batteryService = servicesResult.Services[0];
        var charsResult = await batteryService.GetCharacteristicsForUuidAsync(
            GattCharacteristicUuids.BatteryLevel, cacheMode).AsTask(ct);

        if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
            return null;

        var readResult = await charsResult.Characteristics[0].ReadValueAsync(cacheMode).AsTask(ct);
        if (readResult.Status == GattCommunicationStatus.Success && readResult.Value.Length > 0)
        {
            using var reader = DataReader.FromBuffer(readResult.Value);
            return Math.Clamp((int)reader.ReadByte(), 0, 100);
        }

        return null;
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
