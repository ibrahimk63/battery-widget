using BatteryWidget.Models;

namespace BatteryWidget.Services;

public sealed class NotificationService
{
    private readonly HashSet<string> _notifiedLowBatteryIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, string>? _notifyCallback;

    public NotificationService(Action<string, string>? notifyCallback = null)
    {
        _notifyCallback = notifyCallback;
    }

    public IReadOnlyList<DeviceBatteryInfo> Evaluate(IEnumerable<DeviceBatteryInfo> devices, AppSettings settings)
    {
        var triggered = new List<DeviceBatteryInfo>();
        if (!settings.EnableLowBatteryNotifications)
            return triggered;

        int threshold = Math.Clamp(settings.LowBatteryThreshold, 5, 50);

        foreach (var device in devices)
        {
            if (!device.IsConnected || device.BatteryPercent < 0)
                continue;

            // Reset alert state once battery is charged above threshold + 5% or currently charging
            if (device.IsCharging || device.BatteryPercent > threshold + 5)
            {
                _notifiedLowBatteryIds.Remove(device.Id);
                continue;
            }

            if (device.BatteryPercent <= threshold && _notifiedLowBatteryIds.Add(device.Id))
            {
                triggered.Add(device);
                _notifyCallback?.Invoke(
                    "Düşük Pil Uyarısı",
                    $"{device.Name} pili %{device.BatteryPercent} seviyesine düştü.");
            }
        }

        return triggered;
    }
}
