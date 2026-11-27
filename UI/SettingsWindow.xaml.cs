using System.Windows;
using BatteryWidget.Models;
using BatteryWidget.Services;

namespace BatteryWidget.UI;

public partial class SettingsWindow : Window
{
    private static readonly int[] IntervalStepsSeconds = [10, 20, 30, 60, 120, 180, 300];

    private readonly AppSettings _settings;
    private readonly BatteryMonitorService _monitorService;
    private readonly Action _onSaved;

    public SettingsWindow(AppSettings settings, BatteryMonitorService monitorService, Action onSaved)
    {
        InitializeComponent();
        _settings = settings;
        _monitorService = monitorService;
        _onSaved = onSaved;

        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        LowBatteryNotifCheck.IsChecked = _settings.EnableLowBatteryNotifications;
        ThresholdSlider.Value = _settings.LowBatteryThreshold;
        IntervalSlider.Value = FindNearestStepIndex(_settings.PollIntervalSeconds);
        OpacitySlider.Value = _settings.Opacity;

        UpdateLabels();
        _ = RunUniversalDiagnosticsAsync(pollFirst: false);
    }

    private static int FindNearestStepIndex(int seconds)
    {
        int bestIndex = 0;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < IntervalStepsSeconds.Length; i++)
        {
            int diff = Math.Abs(IntervalStepsSeconds[i] - seconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded)
            UpdateLabels();
    }

    private void UpdateLabels()
    {
        ThresholdValueText.Text = $"%{(int)ThresholdSlider.Value}";
        int idx = Math.Clamp((int)Math.Round(IntervalSlider.Value), 0, IntervalStepsSeconds.Length - 1);
        int sec = IntervalStepsSeconds[idx];
        IntervalValueText.Text = sec >= 60 ? $"{sec / 60} dk" : $"{sec} sn";
        OpacityValueText.Text = $"%{(int)(OpacitySlider.Value * 100)}";
    }

    private void ScanHid_Click(object sender, RoutedEventArgs e)
    {
        HidReportBox.Text = HidDiagnosticService.GenerateHidReport();
    }

    private async void UniversalScan_Click(object sender, RoutedEventArgs e)
    {
        await RunUniversalDiagnosticsAsync(pollFirst: true);
    }

    private async Task RunUniversalDiagnosticsAsync(bool pollFirst)
    {
        UniversalScanBtn.IsEnabled = false;
        HidReportBox.Text = "Bu bilgisayardaki tüm kablosuz, Bluetooth ve USB HID aygıtları taranıyor...";
        try
        {
            var devices = pollFirst
                ? await _monitorService.PollNowAsync()
                : _monitorService.CurrentDevices;

            HidReportBox.Text = await HidDiagnosticService.GenerateUniversalDiagnosticsAsync(devices);
        }
        catch (Exception ex)
        {
            HidReportBox.Text = $"Tanılama hatası: {ex.Message}";
        }
        finally
        {
            UniversalScanBtn.IsEnabled = true;
        }
    }

    private void CopyHid_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(HidReportBox.Text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        int idx = Math.Clamp((int)Math.Round(IntervalSlider.Value), 0, IntervalStepsSeconds.Length - 1);

        _settings.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _settings.EnableLowBatteryNotifications = LowBatteryNotifCheck.IsChecked == true;
        _settings.LowBatteryThreshold = (int)ThresholdSlider.Value;
        _settings.PollIntervalSeconds = IntervalStepsSeconds[idx];
        _settings.Opacity = OpacitySlider.Value;

        _onSaved();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
