using System.Diagnostics;
using System.IO;
using System.Windows;
using BatteryWidget.Models;
using BatteryWidget.Providers;
using BatteryWidget.Services;

namespace BatteryWidget.UI;

public partial class SettingsWindow : Window
{
    private sealed record ActiveDeviceDisplay(string Id, string Name, string PercentText, string ConnectionLabel, bool IsConnected)
    {
        public override string ToString()
        {
            string status = IsConnected ? ConnectionLabel : "Uyku / Bağlı değil";
            return $"{Name} — {PercentText} ({status})";
        }
    }

    private sealed record HiddenItemDisplay(string Id, string Name)
    {
        public override string ToString() => $"{Name} ({Id})";
    }

    private sealed record ProfileFileDisplay(string FileName, string FullPath)
    {
        public override string ToString() => FileName;
    }

    private const string GitHubNewIssueUrl =
        "https://github.com/ibrahimk63/battery-widget/issues/new?title=Desteklenmeyen+Cihaz+Raporu&body=Cihaz+Marka%2FModeli%3A+%0A%0ATan%C4%B1lama+Raporu+%28Ctrl%2BV+ile+a%C5%9Fa%C4%9F%C4%B1ya+yap%C4%B1%C5%9Ft%C4%B1r%C4%B1n%29%3A%0A%60%60%60text%0A%0A%60%60%60";

    private static readonly int[] IntervalStepsSeconds = [10, 20, 30, 60, 120, 180, 300];

    private readonly AppSettings _settings;
    private readonly BatteryMonitorService _monitorService;
    private readonly Action _onSaved;
    private readonly Func<UpdateCheckResult, IProgress<double>?, CancellationToken, Task<(bool Started, string Message)>>? _applyUpdateAsync;
    private UpdateCheckResult? _updateInfo;
    private IReadOnlyList<LearnedCandidate> _currentCandidates = [];

    public SettingsWindow(
        AppSettings settings,
        BatteryMonitorService monitorService,
        Action onSaved,
        UpdateCheckResult? updateInfo = null,
        Func<UpdateCheckResult, IProgress<double>?, CancellationToken, Task<(bool Started, string Message)>>? applyUpdateAsync = null)
    {
        InitializeComponent();
        _settings = settings;
        _monitorService = monitorService;
        _onSaved = onSaved;
        _updateInfo = updateInfo;
        _applyUpdateAsync = applyUpdateAsync;

        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        LowBatteryNotifCheck.IsChecked = _settings.EnableLowBatteryNotifications;
        AutoAppUpdateCheck.IsChecked = _settings.EnableAutoAppUpdate;
        RemoteSyncCheck.IsChecked = _settings.EnableRemoteProfileSync;
        AnonymousReportCheck.IsChecked = _settings.EnableAnonymousReporting;
        ThresholdSlider.Value = _settings.LowBatteryThreshold;
        IntervalSlider.Value = FindNearestStepIndex(_settings.PollIntervalSeconds);
        OpacitySlider.Value = _settings.Opacity;

        RefreshUpdateUi();

        UpdateSyncStatusText();
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
        if (ThresholdValueText is null || IntervalValueText is null || OpacityValueText is null)
            return;

        ThresholdValueText.Text = $"%{(int)ThresholdSlider.Value}";
        int idx = Math.Clamp((int)Math.Round(IntervalSlider.Value), 0, IntervalStepsSeconds.Length - 1);
        int sec = IntervalStepsSeconds[idx];
        IntervalValueText.Text = sec >= 60 ? $"{sec / 60} dk" : $"{sec} sn";
        OpacityValueText.Text = $"%{(int)(OpacitySlider.Value * 100)}";
    }

    private void ScanHid_Click(object sender, RoutedEventArgs e)
    {
        HidReportBox.Text = HidDiagnosticService.GenerateHidReport();
        DiagStatusText.Text = "Ham USB / HID listesi oluşturuldu.";
    }

    private async void UniversalScan_Click(object sender, RoutedEventArgs e)
    {
        await RunUniversalDiagnosticsAsync(pollFirst: true);
    }

    private async Task RunUniversalDiagnosticsAsync(bool pollFirst)
    {
        UniversalScanBtn.IsEnabled = false;
        DiagStatusText.Text = "Taranıyor...";
        HidReportBox.Text = "Bu bilgisayardaki tüm kablosuz, Bluetooth ve USB HID aygıtları taranıyor...";
        try
        {
            var devices = pollFirst
                ? await _monitorService.PollNowAsync()
                : _monitorService.CurrentDevices;

            HidReportBox.Text = await HidDiagnosticService.GenerateUniversalDiagnosticsAsync(devices);
            DiagStatusText.Text = "Tarama tamamlandı.";
        }
        catch (Exception ex)
        {
            HidReportBox.Text = $"Tanılama hatası: {ex.Message}";
            DiagStatusText.Text = $"Tanılama hatası: {ex.Message}";
        }
        finally
        {
            UniversalScanBtn.IsEnabled = true;
        }
    }

    private void CopyHid_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HidReportBox.Text))
        {
            DiagStatusText.Text = "Kopyalanacak rapor yok. Önce 'Tüm Cihazları Tara'ya basın.";
            return;
        }

        System.Windows.Clipboard.SetText(HidReportBox.Text);
        DiagStatusText.Text = "✓ Rapor panoya kopyalandı. İstediğiniz yere (GitHub/Discord/mesaj) Ctrl+V ile yapıştırabilirsiniz.";
    }

    private void ReportIssue_Click(object sender, RoutedEventArgs e)
    {
        bool hasReport = !string.IsNullOrWhiteSpace(HidReportBox.Text);
        if (hasReport)
            System.Windows.Clipboard.SetText(HidReportBox.Text);

        try
        {
            Process.Start(new ProcessStartInfo(GitHubNewIssueUrl) { UseShellExecute = true });
            DiagStatusText.Text = hasReport
                ? "✓ Rapor panoya kopyalandı ve GitHub destek sayfası açıldı! Açılan kutuya Ctrl+V ile yapıştırıp gönderin."
                : "GitHub destek sayfası açıldı. Önce 'Tüm Cihazları Tara'ya basıp raporu kopyalayabilirsiniz.";
        }
        catch (Exception ex)
        {
            DiagStatusText.Text = $"Tarayıcı açılamadı: {ex.Message}";
        }
    }

    private void PreviewAnonymousReport_Click(object sender, RoutedEventArgs e)
    {
        var payload = AnonymousReportService.BuildPayload(_monitorService.CurrentDevices);
        HidReportBox.Text = AnonymousReportService.SerializePayloadPretty(payload);
        DiagStatusText.Text = $"Önizleme hazır: {payload.UnknownHidCandidates.Count} tanınmayan USB HID adayı bulundu (sıfır kişisel veri).";
    }

    private async void SendAnonymousReport_Click(object sender, RoutedEventArgs e)
    {
        SendAnonBtn.IsEnabled = false;
        DiagStatusText.Text = "Anonim donanım özeti hazırlanıyor...";
        try
        {
            var payload = AnonymousReportService.BuildPayload(_monitorService.CurrentDevices);
            string jsonPreview = AnonymousReportService.SerializePayloadPretty(payload);
            HidReportBox.Text = jsonPreview;

            if (payload.UnknownHidCandidates.Count == 0)
            {
                DiagStatusText.Text = "Tüm uyumlu USB HID cihazlarınız zaten algılanıyor; gönderilecek eksik cihaz yok.";
                return;
            }

            string? endpoint = RemoteProfileSyncService.GetActiveReportEndpointUrl();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                System.Windows.Clipboard.SetText(jsonPreview);
                DiagStatusText.Text = "Anonim sunucu henüz yapılandırılmadı; özet panoya kopyalandı, 'Sorun Bildir (GitHub)' ile iletebilirsiniz.";
                return;
            }

            var (sent, message) = await AnonymousReportService.SendReportAsync(payload, endpoint);
            if (sent)
            {
                _settings.LastReportedSignature = AnonymousReportService.ComputeSignature(payload);
                _settings.LastReportSentUtc = DateTimeOffset.UtcNow;
                _settings.Save();
            }
            DiagStatusText.Text = sent ? $"✓ {message}" : message;
        }
        catch (Exception ex)
        {
            DiagStatusText.Text = $"Gönderim hatası: {ex.Message}";
        }
        finally
        {
            SendAnonBtn.IsEnabled = true;
        }
    }

    private async void SyncProfiles_Click(object sender, RoutedEventArgs e)
    {
        SyncProfilesBtn.IsEnabled = false;
        SyncStatusText.Text = "Güncelleniyor...";
        try
        {
            var result = await RemoteProfileSyncService.SyncIfDueAsync(_settings, force: true);
            SyncStatusText.Text = $"{result.Message} (Toplam {result.ProfileCount} uzak profil)";
            if (result.Updated)
                await _monitorService.PollNowAsync();
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = $"Hata: {ex.Message}";
        }
        finally
        {
            SyncProfilesBtn.IsEnabled = true;
        }
    }

    private void UpdateSyncStatusText()
    {
        int count = RemoteProfileSyncService.LoadCachedOrEmbeddedDatabase().Profiles.Count;
        SyncStatusText.Text = _settings.LastProfileSyncUtc is { } last
            ? $"Son kontrol: {last.ToLocalTime():dd.MM HH:mm} • {count} profil"
            : $"Hazır temel katalog: {count} profil";
    }

    private void RefreshUpdateUi()
    {
        if (_updateInfo is { HasUpdate: true })
        {
            UpdateBadgeBtn.Content = $"Yeni Sürüm: v{_updateInfo.LatestVersion} — Şimdi Güncelle";
            UpdateBadgeBtn.Visibility = Visibility.Visible;
            AppUpdateStatusText.Text = $"Yeni sürüm hazır: v{_updateInfo.LatestVersion} (Mevcut: v{_updateInfo.CurrentVersion})";
        }
        else
        {
            UpdateBadgeBtn.Visibility = Visibility.Collapsed;
            AppUpdateStatusText.Text = $"Mevcut sürüm: v{UpdateCheckService.GetCurrentVersion()} (Güncel)";
        }
    }

    private async void CheckAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckAppUpdateBtn.IsEnabled = false;
        AppUpdateStatusText.Text = "Yeni sürüm kontrol ediliyor...";
        try
        {
            var result = await UpdateCheckService.CheckForUpdateAsync();
            if (result is null)
            {
                AppUpdateStatusText.Text = "GitHub ile bağlantı kurulamadı.";
                return;
            }

            _updateInfo = result;
            RefreshUpdateUi();
            if (result.HasUpdate)
                AppUpdateStatusText.Text = $"Yeni sürüm bulundu: v{result.LatestVersion}. Üstteki yeşil butonla otomatik güncelleyebilirsiniz.";
        }
        catch (Exception ex)
        {
            AppUpdateStatusText.Text = $"Kontrol hatası: {ex.Message}";
        }
        finally
        {
            CheckAppUpdateBtn.IsEnabled = true;
        }
    }

    private async void OpenUpdateUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_updateInfo is null)
            return;

        if (_applyUpdateAsync is not null
            && !string.IsNullOrWhiteSpace(_updateInfo.DownloadUrl)
            && SelfUpdateService.CanSelfUpdateInPlace(Environment.ProcessPath))
        {
            UpdateBadgeBtn.IsEnabled = false;
            CheckAppUpdateBtn.IsEnabled = false;
            var targetVer = _updateInfo.LatestVersion;
            var progress = new Progress<double>(p =>
            {
                int pct = (int)Math.Round(p * 100);
                UpdateBadgeBtn.Content = $"İndiriliyor... %{pct}";
                AppUpdateStatusText.Text = $"v{targetVer} indiriliyor (%{pct})...";
            });

            try
            {
                var (started, message) = await _applyUpdateAsync(_updateInfo, progress, CancellationToken.None);
                AppUpdateStatusText.Text = message;
                if (!started)
                {
                    UpdateBadgeBtn.Content = $"Yeni Sürüm: v{targetVer} — Şimdi Güncelle";
                    UpdateBadgeBtn.IsEnabled = true;
                    CheckAppUpdateBtn.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                AppUpdateStatusText.Text = $"Güncelleme hatası: {ex.Message}";
                UpdateBadgeBtn.Content = $"Yeni Sürüm: v{targetVer} — Şimdi Güncelle";
                UpdateBadgeBtn.IsEnabled = true;
                CheckAppUpdateBtn.IsEnabled = true;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(_updateInfo.ReleaseUrl))
            return;

        AppUpdateStatusText.Text = "Klasöre yazma izni yok; indirme sayfası tarayıcıda açılıyor...";
        try
        {
            Process.Start(new ProcessStartInfo(_updateInfo.ReleaseUrl) { UseShellExecute = true });
        }
        catch { /* ignore browser open error */ }
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
            LearnStatusText.Text = "Lütfen önce Adım 1'den bir cihaz seçin.";
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
                    ? "Tek kesin sonuç bulundu! 'Cihazı Kaydet ve Ekle' ile kaydedebilirsiniz."
                    : $"{_currentCandidates.Count} sonuç bulundu. Şarj %1 değişince yeni değeri yazıp '2. Sonucu Doğrula'ya basabilir veya doğrudan 'Cihazı Kaydet ve Ekle' ile kaydedebilirsiniz.";
            }
            else
            {
                LearnStatusText.Text = "Eşleşen sonuç bulunamadı. Cihazın açık/aktif olduğundan ve pil yüzdesinin doğru olduğundan emin olun.";
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
            LearnStatusText.Text = "Lütfen Adım 3 listesinden bir sonuç seçin.";
            return;
        }

        var category = BluetoothBatteryProvider.GuessCategory(candidate.DeviceName);
        string savedPath = AutoLearnService.SaveLearnedProfile(candidate, candidate.DeviceName, category);
        LearnStatusText.Text = $"Kaydedildi: {Path.GetFileName(savedPath)}";
        LoadDeviceManagementLists();
        _ = _monitorService.PollNowAsync();
    }

    private void LoadDeviceManagementLists()
    {
        ActiveDevicesListBox.ItemsSource = _monitorService.CurrentDevices
            .Select(d => new ActiveDeviceDisplay(d.Id, d.Name, d.PercentText, d.ConnectionLabel, d.IsConnected))
            .ToList();

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

    private void HideActiveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveDevicesListBox.SelectedItem is not ActiveDeviceDisplay item)
        {
            ManageStatusText.Text = "Gizlemek için aktif cihaz listesinden bir cihaz seçin.";
            return;
        }

        _monitorService.HideDevice(item.Id, item.Name);
        LoadDeviceManagementLists();
        ManageStatusText.Text = $"{item.Name} widget ekranından gizlendi.";
    }

    private async void RefreshActiveDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshActiveBtn.IsEnabled = false;
        ManageStatusText.Text = "Cihazlar yenileniyor...";
        try
        {
            await _monitorService.PollNowAsync();
            LoadDeviceManagementLists();
            ManageStatusText.Text = "Cihaz listesi güncellendi.";
        }
        catch (Exception ex)
        {
            ManageStatusText.Text = $"Yenileme hatası: {ex.Message}";
        }
        finally
        {
            RefreshActiveBtn.IsEnabled = true;
        }
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
            ManageStatusText.Text = "Silmek için kayıt listesinden bir dosya seçin.";
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
        _settings.EnableAutoAppUpdate = AutoAppUpdateCheck.IsChecked == true;
        _settings.EnableRemoteProfileSync = RemoteSyncCheck.IsChecked == true;
        _settings.EnableAnonymousReporting = AnonymousReportCheck.IsChecked == true;
        _settings.LowBatteryThreshold = (int)ThresholdSlider.Value;
        _settings.PollIntervalSeconds = IntervalStepsSeconds[idx];
        _settings.Opacity = OpacitySlider.Value;

        _onSaved();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
