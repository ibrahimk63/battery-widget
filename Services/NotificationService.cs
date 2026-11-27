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
            if (!device.IsConnected)
                continue;

            bool isQualitativeLow = device.BatteryPercent < 0 &&
                (string.Equals(device.DisplayLevelText, "Düşük", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(device.DisplayLevelText, "Boş", StringComparison.OrdinalIgnoreCase));

            bool isQualitativeOk = device.BatteryPercent < 0 &&
                (string.Equals(device.DisplayLevelText, "Orta", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(device.DisplayLevelText, "Dolu", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(device.DisplayLevelText, "Kablolu", StringComparison.OrdinalIgnoreCase));

            if (device.BatteryPercent < 0 && !isQualitativeLow && !isQualitativeOk)
                continue;

            // Reset alert state once battery is charged above threshold + 5% or currently charging
            if (device.IsCharging || device.BatteryPercent > threshold + 5 || isQualitativeOk)
            {
                _notifiedLowBatteryIds.Remove(device.Id);
                continue;
            }

            if ((device.BatteryPercent >= 0 && device.BatteryPercent <= threshold) || isQualitativeLow)
            {
                if (_notifiedLowBatteryIds.Add(device.Id))
                {
                    triggered.Add(device);
                    string levelStr = device.BatteryPercent >= 0 ? $"%{device.BatteryPercent}" : device.PercentText;
                    _notifyCallback?.Invoke(
                        "Düşük Pil Uyarısı",
                        $"{device.Name} pili {levelStr} seviyesine düştü.");
                }
            }
        }

        return triggered;
    }
}
