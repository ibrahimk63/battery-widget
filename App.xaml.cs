using System.IO;
using System.Windows;
using System.Windows.Threading;
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
    private UpdateCheckResult? _latestUpdate;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
            WriteCrashLog("Dispatcher", args.Exception);

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
            new UniversalCompxProvider(),
            new WraithEcosystemProvider(),
            new OpenSourcePeripheralsProvider(),
            new UniversalHidFingerprintProvider(),
            new LogitechHidppProvider(),
            new RazerBatteryProvider(),
            new HeadsetVendorsProvider(),
            new GamepadBatteryProvider(),
            new BluetoothBatteryProvider(),
            new GenericHidBatteryProvider(),
            new JsonHidProfileProvider()
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

        _ = RunBackgroundStartupTasksAsync();

        if (!_settings.TelemetryConsentAsked)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    if (_settings.TelemetryConsentAsked)
                        return;

                    var consent = new ConsentWindow();
                    consent.ShowDialog();
                    _settings.TelemetryConsentAsked = true;
                    _settings.EnableAnonymousReporting = consent.Accepted;
                    _settings.Save();

                    if (consent.Accepted)
                        _ = AnonymousReportService.TryAutoReportIfConsentedAsync(_settings, _monitorService.CurrentDevices);
                }
                catch
                {
                    // Never fail startup over consent dialog
                }
            }));
        }
    }

    private async Task RunBackgroundStartupTasksAsync()
    {
        try
        {
            var sync = await RemoteProfileSyncService.SyncIfDueAsync(_settings);
            if (sync.Updated)
                await _monitorService.PollNowAsync();

            _latestUpdate = await UpdateCheckService.CheckForUpdateAsync();

            await Task.Delay(2500);
            if (_settings.EnableAnonymousReporting)
                await AnonymousReportService.TryAutoReportIfConsentedAsync(_settings, _monitorService.CurrentDevices);
        }
        catch
        {
            // Background maintenance is strictly non-fatal
        }
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
        var win = new SettingsWindow(_settings, _monitorService, () =>
        {
            _settings.Save();
            StartupService.SetEnabled(_settings.StartWithWindows);
            _widgetWindow.ApplySettings();
            _monitorService.RestartWithNewInterval();
        }, _latestUpdate);
        win.ShowDialog();
    }

    private void ExitApplication()
    {
        _settings.Save();
        _monitorService.Dispose();
        _trayManager.Dispose();
        Shutdown();
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BatteryWidget");
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "crash.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] ({source}) {ex}\n\n");
        }
        catch
        {
            // Ignore crash logger failure
        }
    }
}
