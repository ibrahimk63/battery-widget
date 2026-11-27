using System.Windows;
using BatteryWidget.Models;
using BatteryWidget.Services;

namespace BatteryWidget.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onSaved;

    public SettingsWindow(AppSettings settings, Action onSaved)
    {
        InitializeComponent();
        _settings = settings;
        _onSaved = onSaved;

        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        LowBatteryNotifCheck.IsChecked = _settings.EnableLowBatteryNotifications;
        ThresholdSlider.Value = _settings.LowBatteryThreshold;
        IntervalSlider.Value = _settings.PollIntervalSeconds;
        OpacitySlider.Value = _settings.Opacity;

        UpdateLabels();
        HidReportBox.Text = HidDiagnosticService.GenerateHidReport();
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded)
            UpdateLabels();
    }

    private void UpdateLabels()
    {
        ThresholdValueText.Text = $"%{(int)ThresholdSlider.Value}";
        IntervalValueText.Text = $"{(int)IntervalSlider.Value}s";
        OpacityValueText.Text = $"%{(int)(OpacitySlider.Value * 100)}";
    }

    private void ScanHid_Click(object sender, RoutedEventArgs e)
    {
        HidReportBox.Text = HidDiagnosticService.GenerateHidReport();
    }

    private void ProbeHid_Click(object sender, RoutedEventArgs e)
    {
        HidReportBox.Text = HidDiagnosticService.ProbeKnownDevices();
    }

    private void CopyHid_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(HidReportBox.Text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _settings.EnableLowBatteryNotifications = LowBatteryNotifCheck.IsChecked == true;
        _settings.LowBatteryThreshold = (int)ThresholdSlider.Value;
        _settings.PollIntervalSeconds = (int)IntervalSlider.Value;
        _settings.Opacity = OpacitySlider.Value;

        _onSaved();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
