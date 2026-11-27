using BatteryWidget.Models;
using BatteryWidget.Providers;

namespace BatteryWidget.Services;

public sealed class BatteryMonitorService : IDisposable
{
    private readonly IReadOnlyList<IBatteryProvider> _providers;
    private readonly NotificationService _notificationService;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, int> _lastKnownPercents = new(StringComparer.OrdinalIgnoreCase);
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    public event EventHandler<IReadOnlyList<DeviceBatteryInfo>>? DevicesUpdated;
    public IReadOnlyList<DeviceBatteryInfo> CurrentDevices { get; private set; } = [];

    public BatteryMonitorService(
        IReadOnlyList<IBatteryProvider> providers,
        NotificationService notificationService,
        AppSettings settings)
    {
        _providers = providers;
        _notificationService = notificationService;
        _settings = settings;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.PollIntervalSeconds, 5, 600));
        _timer = new PeriodicTimer(interval);
        _ = RunLoopAsync(_cts.Token);
    }

    public void RestartWithNewInterval()
    {
        if (_cts is not null)
            Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _cts?.Dispose();
        _timer = null;
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await PollNowAsync(ct);

        while (_timer is not null)
        {
            try
            {
                if (!await _timer.WaitForNextTickAsync(ct))
                    break;
                await PollNowAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<IReadOnlyList<DeviceBatteryInfo>> PollNowAsync(CancellationToken ct = default)
    {
        if (!await _pollLock.WaitAsync(0, ct))
            return CurrentDevices;

        try
        {
            var tasks = _providers.Select(async p =>
            {
                try
                {
                    return await p.PollDevicesAsync(ct);
                }
                catch
                {
                    return (IReadOnlyList<DeviceBatteryInfo>)[];
                }
            });

            var results = await Task.WhenAll(tasks);
            var merged = results
                .SelectMany(x => x)
                .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.IsConnected).ThenByDescending(x => x.BatteryPercent).First())
                .Select(ApplyLastKnownPercent)
                .OrderByDescending(d => d.IsConnected)
                .ThenBy(d => d.Name)
                .ToList();

            CurrentDevices = merged;
            _notificationService.Evaluate(merged, _settings);
            DevicesUpdated?.Invoke(this, merged);
            return merged;
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private DeviceBatteryInfo ApplyLastKnownPercent(DeviceBatteryInfo device)
    {
        if (device.IsConnected && device.BatteryPercent >= 0)
        {
            _lastKnownPercents[device.Id] = device.BatteryPercent;
            return device;
        }

        if (!device.IsConnected && device.BatteryPercent < 0 && _lastKnownPercents.TryGetValue(device.Id, out int cached))
        {
            return device with { BatteryPercent = cached, StatusNote = "Uyku" };
        }

        return device;
    }

    public void Dispose()
    {
        Stop();
        _pollLock.Dispose();
    }
}
