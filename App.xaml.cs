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
    private bool _isSettingsOpen;
    private int _updateApplying;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
            WriteCrashLog("Dispatcher", args.Exception);

        base.OnStartup(e);
        SelfUpdateService.CleanupLeftoverUpdateFiles();

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
            onExit: ExitApplication,
            onToggleTaskbarDock: () => _widgetWindow.ToggleTaskbarDock());

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
            if (_latestUpdate is { HasUpdate: true })
                await TryBackgroundAutoUpdateAsync(_latestUpdate);

            await Task.Delay(2500);
            if (_settings.EnableAnonymousReporting)
                await AnonymousReportService.TryAutoReportIfConsentedAsync(_settings, _monitorService.CurrentDevices);
        }
        catch
        {
            // Background maintenance is strictly non-fatal
        }
    }

    private async Task TryBackgroundAutoUpdateAsync(UpdateCheckResult update)
    {
        string? exePath = Environment.ProcessPath;
        bool canAutoApply = _settings.EnableAutoAppUpdate
            && !_isSettingsOpen
            && !string.IsNullOrWhiteSpace(update.DownloadUrl)
            && SelfUpdateService.CanSelfUpdateInPlace(exePath);

        if (!canAutoApply || string.IsNullOrWhiteSpace(exePath))
        {
            Dispatcher.Invoke(() => _trayManager.ShowNotification(
                "Yeni Sürüm Mevcut",
                $"BatteryWidget v{update.LatestVersion} yayınlandı. Ayarlar'dan güncelleyebilirsiniz."));
            return;
        }

        if (Interlocked.CompareExchange(ref _updateApplying, 1, 0) != 0)
            return;

        try
        {
            var (ok, stagedPath, _) = await SelfUpdateService.DownloadAndStageUpdateAsync(update, exePath);
            if (!ok || string.IsNullOrWhiteSpace(stagedPath) || _isSettingsOpen)
            {
                Interlocked.Exchange(ref _updateApplying, 0);
                return;
            }

            await Dispatcher.InvokeAsync(() => _trayManager.ShowNotification(
                "BatteryWidget Güncelleniyor",
                $"v{update.LatestVersion} indirildi, uygulama otomatik yeniden başlatılıyor..."));

            await Task.Delay(1500);

            bool launched = false;
            await Dispatcher.InvokeAsync(() =>
            {
                launched = LaunchSwapAndShutdown(exePath, stagedPath);
            });

            if (!launched)
                Interlocked.Exchange(ref _updateApplying, 0);
        }
        catch
        {
            Interlocked.Exchange(ref _updateApplying, 0);
        }
    }

    private async Task<(bool Started, string Message)> ApplyUpdateAsync(
        UpdateCheckResult update,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !SelfUpdateService.CanSelfUpdateInPlace(exePath))
            return (false, "Uygulama klasörüne yazma izni yok; tarayıcı üzerinden indirebilirsiniz.");

        if (Interlocked.CompareExchange(ref _updateApplying, 1, 0) != 0)
            return (false, "Güncelleme işlemi zaten devam ediyor.");

        try
        {
            var (ok, stagedPath, message) = await SelfUpdateService.DownloadAndStageUpdateAsync(
                update, exePath, progress, ct);

            if (!ok || string.IsNullOrWhiteSpace(stagedPath))
            {
                Interlocked.Exchange(ref _updateApplying, 0);
                return (false, message);
            }

            bool launched = false;
            await Dispatcher.InvokeAsync(() =>
            {
                launched = LaunchSwapAndShutdown(exePath, stagedPath);
            });

            if (!launched)
            {
                Interlocked.Exchange(ref _updateApplying, 0);
                return (false, "Otomatik güncelleyici başlatılamadı.");
            }

            return (true, "Uygulama yeniden başlatılıyor...");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _updateApplying, 0);
            return (false, $"Güncelleme hatası: {ex.Message}");
        }
    }

    private bool LaunchSwapAndShutdown(string exePath, string stagedPath)
    {
        _widgetWindow.ShutdownDockWatcher();
        _settings.Save();
        _monitorService.Dispose();
        _trayManager.Dispose();

        if (!SelfUpdateService.LaunchSwapProcess(exePath, stagedPath, Environment.ProcessId))
            return false;

        Shutdown();
        return true;
    }

    private void ToggleWidgetVisibility() => _widgetWindow.ToggleUserVisibility();

    private void OpenSettings()
    {
        _isSettingsOpen = true;
        try
        {
            var win = new SettingsWindow(_settings, _monitorService, () =>
            {
                _settings.Save();
                StartupService.SetEnabled(_settings.StartWithWindows);
                _widgetWindow.ApplySettings();
                _monitorService.RestartWithNewInterval();
            }, _latestUpdate, ApplyUpdateAsync);
            win.ShowDialog();
        }
        finally
        {
            _isSettingsOpen = false;
        }
    }

    private void ExitApplication()
    {
        _widgetWindow.ShutdownDockWatcher();
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
