using BatteryWidget.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryWidget.Providers;

/// <summary>
/// Automatically discovers connected Bluetooth LE devices exposing the standard GATT Battery Service (0x180F / 0x2A19).
/// </summary>
public sealed class BluetoothBatteryProvider : IBatteryProvider
{
    public string ProviderName => "Bluetooth (BLE/GATT)";

    public async Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
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
                    using var bleDevice = await BluetoothLEDevice.FromIdAsync(devInfo.Id);
                    if (bleDevice is null)
                        continue;

                    var servicesResult = await bleDevice.GetGattServicesForUuidAsync(
                        GattServiceUuids.Battery, BluetoothCacheMode.Uncached);

                    if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                        continue;

                    using var batteryService = servicesResult.Services[0];
                    var charsResult = await batteryService.GetCharacteristicsForUuidAsync(
                        GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);

                    if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                        continue;

                    var readResult = await charsResult.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (readResult.Status == GattCommunicationStatus.Success && readResult.Value.Length > 0)
                    {
                        using var reader = DataReader.FromBuffer(readResult.Value);
                        int percent = Math.Clamp((int)reader.ReadByte(), 0, 100);

                        results.Add(new DeviceBatteryInfo
                        {
                            Id = $"ble-{bleDevice.BluetoothAddress:X}",
                            Name = string.IsNullOrWhiteSpace(bleDevice.Name) ? devInfo.Name : bleDevice.Name,
                            Category = GuessCategory(bleDevice.Name),
                            Connection = ConnectionKind.Bluetooth,
                            BatteryPercent = percent,
                            IsCharging = false,
                            IsConnected = true
                        });
                    }
                }
                catch
                {
                    // Skip unreachable BLE devices
                }
            }
        }
        catch
        {
            // Bluetooth radio may be off or unavailable
        }

        return results;
    }

    private static DeviceCategory GuessCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DeviceCategory.Other;

        if (name.Contains("mouse", StringComparison.OrdinalIgnoreCase) || name.Contains("m3", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Mouse;
        if (name.Contains("headset", StringComparison.OrdinalIgnoreCase) || name.Contains("cloud", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("buds", StringComparison.OrdinalIgnoreCase) || name.Contains("airpods", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Headset;
        if (name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) || name.Contains("keychron", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Keyboard;
        if (name.Contains("controller", StringComparison.OrdinalIgnoreCase) || name.Contains("xbox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dualsense", StringComparison.OrdinalIgnoreCase))
            return DeviceCategory.Gamepad;

        return DeviceCategory.Other;
    }
}
