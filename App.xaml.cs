using System.Windows;
using BatteryWidget.Models;
using BatteryWidget.Providers;
using BatteryWidget.Services;
using BatteryWidget.UI;

namespace BatteryWidget;

public partial class App : System.Windows.Application
{
    private AppSettings _settings = null!;
    private BatteryMonitorService _monitorService = null!;
    private TrayIconManager _trayManager = null!;
    private WidgetWindow _widgetWindow = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = AppSettings.Load();
        _settings.StartWithWindows = StartupService.IsEnabled();

        _trayManager = new TrayIconManager();
        var notificationService = new NotificationService((title, message) =>
            _trayManager.ShowNotification(title, message));

        var providers = new List<IBatteryProvider>
        {
            new HyperXCloud3Provider(),
            new DarmosharkM3V2Provider(),
            new JsonHidProfileProvider(),
            new BluetoothBatteryProvider()
        };

        _monitorService = new BatteryMonitorService(providers, notificationService, _settings);
        _widgetWindow = new WidgetWindow(_settings, _monitorService, OpenSettings, ExitApplication);

        _trayManager.Initialize(
            onToggleWidget: ToggleWidgetVisibility,
            onRefresh: () => _ = _monitorService.PollNowAsync(),
            onOpenSettings: OpenSettings,
            onExit: ExitApplication);

        _monitorService.DevicesUpdated += (_, devices) =>
        {
            Dispatcher.Invoke(() =>
            {
                _widgetWindow.UpdateDevices(devices);
                _trayManager.UpdateTooltip(devices);
            });
        };

        _widgetWindow.Show();
        _monitorService.Start();
    }

    private void ToggleWidgetVisibility()
    {
        if (_widgetWindow.IsVisible)
            _widgetWindow.Hide();
        else
        {
            _widgetWindow.Show();
            _widgetWindow.Activate();
        }
    }

    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings, () =>
        {
            _settings.Save();
            StartupService.SetEnabled(_settings.StartWithWindows);
            _widgetWindow.ApplySettings();
            _monitorService.RestartWithNewInterval();
        });
        win.ShowDialog();
    }

    private void ExitApplication()
    {
        _settings.Save();
        _monitorService.Dispose();
        _trayManager.Dispose();
        Shutdown();
    }
}
