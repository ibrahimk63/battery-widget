using BatteryWidget.Models;

namespace BatteryWidget.Providers;

public interface IBatteryProvider
{
    string ProviderName { get; }
    Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default);
}
