using System.IO;
using System.Windows;
using BatteryWidget.Models;
using BatteryWidget.Providers;
using BatteryWidget.Services;

namespace BatteryWidget.UI;

public partial class SettingsWindow : Window
{
    private sealed record HiddenItemDisplay(string Id, string Name)
    {
        public override string ToString() => $"{Name} ({Id})";
    }

    private sealed record ProfileFileDisplay(string FileName, string FullPath)
    {
        public override string ToString() => FileName;
    }

    private static readonly int[] IntervalStepsSeconds = [10, 20, 30, 60, 120, 180, 300];

    private readonly AppSettings _settings;
    private readonly BatteryMonitorService _monitorService;
    private readonly Action _onSaved;
    private IReadOnlyList<LearnedCandidate> _currentCandidates = [];

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
        LoadLearnableDevices();
        LoadDeviceManagementLists();
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

    private void LoadLearnableDevices()
    {
        var items = AutoLearnService.ListLearnableDevices();
        LearnDeviceCombo.ItemsSource = items;
        if (items.Count > 0)
            LearnDeviceCombo.SelectedIndex = 0;
    }

    private void RefreshLearnDevices_Click(object sender, RoutedEventArgs e)
    {
        LoadLearnableDevices();
        LearnStatusText.Text = "Cihaz listesi yenilendi.";
    }

    private async void FindCandidates_Click(object sender, RoutedEventArgs e)
    {
        await RunAutoLearnScanAsync(refine: false);
    }

    private async void RefineCandidates_Click(object sender, RoutedEventArgs e)
    {
        await RunAutoLearnScanAsync(refine: true);
    }

    private async Task RunAutoLearnScanAsync(bool refine)
    {
        if (LearnDeviceCombo.SelectedItem is not LearnableDeviceItem selected)
        {
            LearnStatusText.Text = "Lütfen önce listeden bir cihaz seçin.";
            return;
        }

        if (!int.TryParse(KnownPercentBox.Text.Trim(), out int knownPct) || knownPct < 1 || knownPct > 100)
        {
            LearnStatusText.Text = "Lütfen 1 ile 100 arasında geçerli bir pil yüzdesi girin.";
            return;
        }

        FindCandidatesBtn.IsEnabled = false;
        RefineCandidatesBtn.IsEnabled = false;
        SaveLearnedBtn.IsEnabled = false;
        LearnStatusText.Text = $"{selected.DisplayName} taranıyor...";

        try
        {
            var found = await AutoLearnService.ScanDeviceForBatteryAsync(
                selected.VendorId, selected.ProductId, selected.DisplayName, knownPct);

            if (refine && _currentCandidates.Count > 0)
                _currentCandidates = AutoLearnService.RefineCandidates(_currentCandidates, found);
            else
                _currentCandidates = found;

            CandidatesListBox.ItemsSource = _currentCandidates;
            if (_currentCandidates.Count > 0)
            {
                CandidatesListBox.SelectedIndex = 0;
                SaveLearnedBtn.IsEnabled = true;
                RefineCandidatesBtn.IsEnabled = true;
                LearnStatusText.Text = _currentCandidates.Count == 1
                    ? "Tek kesin aday bulundu! 'Seçili Adayı Kalıcı Kaydet' ile kaydedebilirsiniz."
                    : $"{_currentCandidates.Count} aday bulundu. Şarj %1 değişince yeni değeri yazıp '2. Daralt'a basabilir veya doğrudan kaydedebilirsiniz.";
            }
            else
            {
                LearnStatusText.Text = "Eşleşen aday bulunamadı. Cihazın açık/aktif olduğundan ve pil yüzdesinin doğru olduğundan emin olun.";
            }
        }
        catch (Exception ex)
        {
            LearnStatusText.Text = $"Tarama hatası: {ex.Message}";
        }
        finally
        {
            FindCandidatesBtn.IsEnabled = true;
        }
    }

    private void SaveLearned_Click(object sender, RoutedEventArgs e)
    {
        if (CandidatesListBox.SelectedItem is not LearnedCandidate candidate)
        {
            LearnStatusText.Text = "Lütfen listeden bir aday seçin.";
            return;
        }

        var category = BluetoothBatteryProvider.GuessCategory(candidate.DeviceName);
        string savedPath = AutoLearnService.SaveLearnedProfile(candidate, candidate.DeviceName, category);
        LearnStatusText.Text = $"Kaydedildi: {savedPath}";
        LoadDeviceManagementLists();
        _ = _monitorService.PollNowAsync();
    }

    private void LoadDeviceManagementLists()
    {
        HiddenDevicesListBox.ItemsSource = _settings.HiddenDevices
            .Select(h => new HiddenItemDisplay(h.Id, string.IsNullOrWhiteSpace(h.Name) ? h.Id : h.Name))
            .ToList();

        var profiles = new List<ProfileFileDisplay>();
        string dir = JsonHidProfileProvider.UserProfilesDirectory;
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                profiles.Add(new ProfileFileDisplay(Path.GetFileName(file), file));
        }
        SavedProfilesListBox.ItemsSource = profiles;
    }

    private void UnhideSelected_Click(object sender, RoutedEventArgs e)
    {
        if (HiddenDevicesListBox.SelectedItem is not HiddenItemDisplay item)
        {
            ManageStatusText.Text = "Geri getirmek için gizlenen listeden bir cihaz seçin.";
            return;
        }

        _monitorService.UnhideDevice(item.Id);
        LoadDeviceManagementLists();
        ManageStatusText.Text = $"{item.Name} geri getirildi.";
    }

    private void UnhideAll_Click(object sender, RoutedEventArgs e)
    {
        _monitorService.UnhideAllDevices();
        LoadDeviceManagementLists();
        ManageStatusText.Text = "Gizlenen tüm cihazlar geri getirildi.";
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SavedProfilesListBox.SelectedItem is not ProfileFileDisplay item)
        {
            ManageStatusText.Text = "Silmek için profil listesinden bir dosya seçin.";
            return;
        }

        try
        {
            if (File.Exists(item.FullPath))
                File.Delete(item.FullPath);
            LoadDeviceManagementLists();
            ManageStatusText.Text = $"{item.FileName} silindi.";
            _ = _monitorService.PollNowAsync();
        }
        catch (Exception ex)
        {
            ManageStatusText.Text = $"Silme hatası: {ex.Message}";
        }
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
