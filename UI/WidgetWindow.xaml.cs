using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BatteryWidget.Models;
using BatteryWidget.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace BatteryWidget.UI;

public partial class WidgetWindow : Window
{
    private const int PageSize = 2;

    private readonly AppSettings _settings;
    private readonly BatteryMonitorService _monitorService;
    private readonly Action _onOpenSettings;
    private readonly Action _onExit;
    private readonly DispatcherTimer _dockGuardianTimer;

    private bool _isInitializing = true;
    private bool _isDockedToTaskbar;
    private bool _isDragging;
    private bool _isApplyingLayout;
    private bool _isHiddenByUser;
    private bool _isTemporarilyHidden;
    private bool _useLightTaskbarTheme;
    private int _guardianTickCounter;
    private bool _userPrefsSubscribed;

    private IReadOnlyList<DeviceBatteryInfo> _allDevices = [];
    private int _currentPage;
    private string _lastRenderedSignature = string.Empty;

    public WidgetWindow(
        AppSettings settings,
        BatteryMonitorService monitorService,
        Action onOpenSettings,
        Action onExit)
    {
        InitializeComponent();
        _settings = settings;
        _monitorService = monitorService;
        _onOpenSettings = onOpenSettings;
        _onExit = onExit;

        _dockGuardianTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _dockGuardianTimer.Tick += OnDockGuardianTick;

        SourceInitialized += (_, _) => TaskbarDockService.ApplyToolWindowStyle(this);
        Loaded += Window_Loaded;
        Closed += (_, _) => UnsubscribeSystemEvents();

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _userPrefsSubscribed = true;

        RefreshTaskbarTheme(forceRender: false);

        if (_settings.IsDockedToTaskbar)
        {
            _isDockedToTaskbar = true;
            RootBorder.Visibility = Visibility.Collapsed;
            TaskbarRootBorder.Visibility = Visibility.Visible;
            DockToggleMenuItem.Header = "Masaüstüne Ayır";
            Topmost = true;
            Opacity = 1.0;
        }

        PositionWindow();
        ApplySettings();
        _isInitializing = false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDockedToTaskbar)
        {
            EnterTaskbarDockMode(_settings.TaskbarDockLeft, save: false);
        }
    }

    private void PositionWindow()
    {
        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - 336;
            Top = workArea.Bottom - 230;
        }
    }

    public void ApplySettings()
    {
        AlwaysOnTopMenuItem.IsChecked = _settings.AlwaysOnTop;
        RefreshTaskbarTheme(forceRender: true);

        if (_settings.IsDockedToTaskbar != _isDockedToTaskbar)
        {
            if (_settings.IsDockedToTaskbar)
                EnterTaskbarDockMode(_settings.TaskbarDockLeft, save: false);
            else
                ExitTaskbarDockMode(restoreAtCurrentDrop: false, save: true);
            return;
        }

        if (_isDockedToTaskbar)
        {
            Topmost = true;
            Opacity = 1.0;
        }
        else
        {
            Topmost = _settings.AlwaysOnTop;
            Opacity = Math.Clamp(_settings.Opacity, 0.35, 1.0);
        }
    }

    public void UpdateDevices(IReadOnlyList<DeviceBatteryInfo> devices)
    {
        _allDevices = devices;
        int totalPages = Math.Max(1, (int)Math.Ceiling(_allDevices.Count / (double)PageSize));
        if (_currentPage >= totalPages)
            _currentPage = totalPages - 1;

        RenderCurrentPage();
    }

    public void ToggleUserVisibility()
    {
        if (IsVisible)
        {
            _isHiddenByUser = true;
            _isTemporarilyHidden = false;
            Hide();
        }
        else
        {
            _isHiddenByUser = false;
            _isTemporarilyHidden = false;
            Show();
            if (_isDockedToTaskbar)
            {
                SnapToTaskbar(_settings.TaskbarDockLeft, save: false);
                TaskbarDockService.EnsureAboveTaskbar(this);
            }
            else
            {
                Activate();
            }
        }
    }

    public void ToggleTaskbarDock()
    {
        if (_isDockedToTaskbar)
        {
            ExitTaskbarDockMode(restoreAtCurrentDrop: false, save: true);
        }
        else
        {
            _isHiddenByUser = false;
            if (!IsVisible)
                Show();

            double preferredLeft = !double.IsNaN(_settings.TaskbarDockLeft)
                ? _settings.TaskbarDockLeft
                : double.NaN;
            EnterTaskbarDockMode(preferredLeft, save: true);
        }
    }

    public void ShutdownDockWatcher()
    {
        _dockGuardianTimer.Stop();
        UnsubscribeSystemEvents();
    }

    private void UnsubscribeSystemEvents()
    {
        if (_userPrefsSubscribed)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _userPrefsSubscribed = false;
        }
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(() => RefreshTaskbarTheme(forceRender: false));
    }

    private void RefreshTaskbarTheme(bool forceRender)
    {
        bool nextLight = TaskbarDockService.ResolveUseLightTaskbarTheme(
            _settings.TaskbarThemeMode,
            TaskbarDockService.IsWindowsTaskbarLightTheme());

        bool changed = nextLight != _useLightTaskbarTheme;
        if (!changed && !forceRender)
            return;

        _useLightTaskbarTheme = nextLight;
        ApplyTaskbarThemeChrome();

        if (_isDockedToTaskbar && (changed || forceRender))
            RenderCurrentPage(force: true);
    }

    private void ApplyTaskbarThemeChrome()
    {
        string styleKey = _useLightTaskbarTheme ? "TaskbarHostBorderLightStyle" : "TaskbarHostBorderDarkStyle";
        if (TryFindResource(styleKey) is System.Windows.Style style)
            TaskbarRootBorder.Style = style;

        var mutedBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(
            _useLightTaskbarTheme ? "#4B5563" : "#9CA3AF"));
        TaskbarPageIndicator.Foreground = mutedBrush;
        TaskbarEmptyText.Foreground = mutedBrush;
    }

    private void RenderCurrentPage(bool force = false)
    {
        string modePrefix = _isDockedToTaskbar
            ? (_useLightTaskbarTheme ? "TBL|" : "TBD|")
            : "DK|";

        if (_allDevices.Count == 0)
        {
            string emptySig = modePrefix + "EMPTY";
            if (_lastRenderedSignature != emptySig || force)
            {
                _lastRenderedSignature = emptySig;

                if (_isDockedToTaskbar)
                {
                    TaskbarDevicesPanel.Children.Clear();
                    TaskbarPageIndicator.Visibility = Visibility.Collapsed;
                    TaskbarEmptyText.Visibility = Visibility.Visible;
                    if (IsLoaded)
                    {
                        UpdateLayout();
                        SnapToTaskbar(Left, save: false);
                    }
                }
                else
                {
                    DevicesPanel.Children.Clear();
                    PaginationPanel.Children.Clear();
                    PaginationPanel.Visibility = Visibility.Collapsed;
                    EmptyStateText.Text = "Bağlı cihaz bulunamadı";
                    EmptyStateText.Visibility = Visibility.Visible;
                }
            }
            return;
        }

        int totalPages = Math.Max(1, (int)Math.Ceiling(_allDevices.Count / (double)PageSize));
        _currentPage = Math.Clamp(_currentPage, 0, totalPages - 1);

        var pageDevices = _allDevices
            .Skip(_currentPage * PageSize)
            .Take(PageSize)
            .ToList();

        var sigBuilder = new StringBuilder(modePrefix);
        sigBuilder.Append(_currentPage).Append('/').Append(totalPages).Append('|');
        foreach (var d in pageDevices)
        {
            sigBuilder.Append(d.Id).Append(':')
                      .Append(d.Name).Append(':')
                      .Append(d.PercentText).Append(':')
                      .Append(d.EffectiveRingPercent).Append(':')
                      .Append(d.IsCharging).Append(':')
                      .Append(d.IsConnected).Append(':')
                      .Append(d.StatusNote).Append(';');
        }

        string signature = sigBuilder.ToString();
        if (!force && signature == _lastRenderedSignature)
            return;

        _lastRenderedSignature = signature;

        if (_isDockedToTaskbar)
        {
            TaskbarEmptyText.Visibility = Visibility.Collapsed;
            TaskbarDevicesPanel.Children.Clear();

            for (int i = 0; i < pageDevices.Count; i++)
            {
                if (i > 0)
                {
                    TaskbarDevicesPanel.Children.Add(new Border
                    {
                        Width = 1,
                        Height = 14,
                        Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(
                            _useLightTaskbarTheme ? "#33000000" : "#36FFFFFF")),
                        Margin = new Thickness(8, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                TaskbarDevicesPanel.Children.Add(BuildTaskbarDeviceItem(pageDevices[i]));
            }

            if (totalPages > 1)
            {
                TaskbarPageIndicator.Text = $"{_currentPage + 1}/{totalPages}";
                TaskbarPageIndicator.Visibility = Visibility.Visible;
            }
            else
            {
                TaskbarPageIndicator.Visibility = Visibility.Collapsed;
            }

            if (IsLoaded)
            {
                UpdateLayout();
                SnapToTaskbar(Left, save: false);
            }
        }
        else
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            DevicesPanel.Children.Clear();

            foreach (var device in pageDevices)
            {
                DevicesPanel.Children.Add(BuildRadialDeviceCard(device));
            }

            RenderPagination(totalPages);
        }
    }

    private UIElement BuildTaskbarDeviceItem(DeviceBatteryInfo device)
    {
        bool isLight = _useLightTaskbarTheme;
        var accent = GetTaskbarAccentBrush(device, isLight);
        var percentColor = GetTaskbarPercentBrush(device, isLight);

        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = device.IsConnected ? 1.0 : (isLight ? 0.72 : 0.62),
            ToolTip = BuildDeviceTooltip(device),
            ContextMenu = BuildContextMenu(device, isDocked: true)
        };

        row.Children.Add(new Border
        {
            Width = 3,
            Height = 14,
            CornerRadius = new CornerRadius(1.5),
            Background = accent,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(new TextBlock
        {
            Text = device.IconGlyph,
            FontSize = 12.5,
            Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(isLight ? "#111827" : "#F3F4F6")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });

        row.Children.Add(new TextBlock
        {
            Text = TaskbarDockService.FormatShortDeviceName(device.Name, 14),
            FontSize = 11.5,
            FontWeight = isLight ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(isLight ? "#111827" : "#E5E7EB")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });

        string percentDisplay = device.IsCharging ? $"{device.PercentText}⚡" : device.PercentText;
        row.Children.Add(new TextBlock
        {
            Text = percentDisplay,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = percentColor,
            VerticalAlignment = VerticalAlignment.Center
        });

        return row;
    }

    private static string BuildDeviceTooltip(DeviceBatteryInfo device)
    {
        string status = device.IsConnected
            ? (device.IsCharging ? $"{device.ConnectionLabel} • Şarj oluyor" : device.ConnectionLabel)
            : (device.StatusNote ?? "Uyku / Bağlı değil");
        return $"{device.Name}\nPil: {device.PercentText} ({status})";
    }

    private void RenderPagination(int totalPages)
    {
        PaginationPanel.Children.Clear();
        if (totalPages <= 1)
        {
            PaginationPanel.Visibility = Visibility.Collapsed;
            return;
        }

        PaginationPanel.Visibility = Visibility.Visible;

        for (int i = 0; i < totalPages; i++)
        {
            int targetPage = i;
            bool isActive = i == _currentPage;
            var dot = new Ellipse
            {
                Width = isActive ? 14 : 6,
                Height = 6,
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Fill = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(
                    isActive ? "#22C55E" : "#4B5563"))
            };
            dot.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _currentPage = targetPage;
                RenderCurrentPage(force: true);
            };
            PaginationPanel.Children.Add(dot);
        }

        PaginationPanel.Children.Add(new TextBlock
        {
            Text = $"  {_currentPage + 1}/{totalPages} (↕)",
            FontSize = 9.5,
            Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#6B7280")),
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    private UIElement BuildRadialDeviceCard(DeviceBatteryInfo device)
    {
        var accent = GetBatteryBrush(device);

        var card = new Border
        {
            Width = 124,
            Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#181C26")),
            BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#26FFFFFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 12, 10, 10),
            Margin = new Thickness(4),
            Opacity = device.IsConnected ? 1.0 : 0.55
        };

        var stack = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };

        // Radial Battery Ring (74x74)
        var ringGrid = new Grid
        {
            Width = 74,
            Height = 74,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // Background track circle
        ringGrid.Children.Add(new Ellipse
        {
            Width = 68,
            Height = 68,
            Stroke = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22FFFFFF")),
            StrokeThickness = 6
        });

        // Foreground progress arc
        int ringFill = device.EffectiveRingPercent;
        if (ringFill > 0)
        {
            ringGrid.Children.Add(CreateProgressArc(Math.Clamp(ringFill, 1, 100), accent));
        }

        // Center percentage or qualitative level text
        var centerStack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        bool isQualitative = !string.IsNullOrEmpty(device.DisplayLevelText);
        centerStack.Children.Add(new TextBlock
        {
            Text = device.PercentText,
            FontFamily = new System.Windows.Media.FontFamily(isQualitative ? "Segoe UI" : "Consolas, Segoe UI"),
            FontSize = isQualitative ? 12.5 : 17,
            FontWeight = FontWeights.Bold,
            Foreground = WpfBrushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });

        if (device.IsCharging)
        {
            centerStack.Children.Add(new TextBlock
            {
                Text = "⚡ ŞARJ",
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#38BDF8")),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            });
        }

        ringGrid.Children.Add(centerStack);
        stack.Children.Add(ringGrid);

        // Device Icon + Name
        stack.Children.Add(new TextBlock
        {
            Text = $"{device.IconGlyph} {device.Name}",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#F3F4F6")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            MaxWidth = 104,
            ToolTip = device.Name
        });

        // Status / Connection Pill
        var badgeText = device.IsConnected
            ? device.ConnectionLabel
            : (device.StatusNote ?? "Uyku");

        var badge = new Border
        {
            Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(
                device.IsConnected ? "#1FFFFFFF" : "#33F59E0B")),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = badgeText,
                FontSize = 9.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(
                    device.IsConnected ? "#9CA3AF" : "#FBBF24"))
            }
        };

        stack.Children.Add(badge);
        card.Child = stack;
        card.ContextMenu = BuildContextMenu(device, isDocked: false);

        return card;
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu(DeviceBatteryInfo? device, bool isDocked)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#181B24")),
            Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#F3F4F6")),
            BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#2D3343"))
        };

        if (device is not null)
        {
            var hideDeviceItem = new System.Windows.Controls.MenuItem { Header = "Bu Cihazı Kaldır / Gizle" };
            hideDeviceItem.Click += (_, _) => _monitorService.HideDevice(device.Id, device.Name);
            menu.Items.Add(hideDeviceItem);
            menu.Items.Add(new System.Windows.Controls.Separator());
        }

        var refreshItem = new System.Windows.Controls.MenuItem { Header = "Şimdi Yenile" };
        refreshItem.Click += (_, _) => _ = _monitorService.PollNowAsync();
        menu.Items.Add(refreshItem);

        var dockItem = new System.Windows.Controls.MenuItem
        {
            Header = isDocked ? "Masaüstüne Ayır" : "Görev Çubuğuna Sabitle"
        };
        dockItem.Click += (_, _) => ToggleTaskbarDock();
        menu.Items.Add(dockItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Ayarlar & HID Tanılama" };
        settingsItem.Click += (_, _) => _onOpenSettings();
        menu.Items.Add(settingsItem);

        var hideWidgetItem = new System.Windows.Controls.MenuItem { Header = "Gizle (Sistem Tepsisi)" };
        hideWidgetItem.Click += (_, _) => ToggleUserVisibility();
        menu.Items.Add(hideWidgetItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Çıkış" };
        exitItem.Click += (_, _) => _onExit();
        menu.Items.Add(exitItem);

        return menu;
    }

    private static Path CreateProgressArc(int percent, WpfBrush stroke)
    {
        double radius = 31;
        double cx = 37;
        double cy = 37;

        double clamped = Math.Clamp(percent, 1, 100);
        double angle = clamped >= 100 ? 359.99 : (clamped / 100.0) * 360.0;
        double rad = (angle - 90.0) * Math.PI / 180.0;

        var startPoint = new WpfPoint(cx, cy - radius);
        var endPoint = new WpfPoint(
            cx + radius * Math.Cos(rad),
            cy + radius * Math.Sin(rad));

        var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = endPoint,
            Size = new WpfSize(radius, radius),
            IsLargeArc = angle > 180.0,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = 6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    private static WpfBrush GetBatteryBrush(DeviceBatteryInfo device)
    {
        int effective = device.EffectiveRingPercent;
        if (!device.IsConnected || effective < 0)
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#6B7280"));
        if (device.IsCharging)
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#38BDF8"));
        if (effective <= 25 && (device.BatteryPercent <= 15 || string.Equals(device.DisplayLevelText, "Düşük", StringComparison.OrdinalIgnoreCase) || string.Equals(device.DisplayLevelText, "Boş", StringComparison.OrdinalIgnoreCase)))
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#EF4444"));
        if (effective <= 35)
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#F59E0B"));
        return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22C55E"));
    }

    private static WpfBrush GetTaskbarAccentBrush(DeviceBatteryInfo device, bool isLightTaskbar)
    {
        if (!isLightTaskbar)
            return GetBatteryBrush(device);

        int effective = device.EffectiveRingPercent;
        if (!device.IsConnected || effective < 0)
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#6B7280"));
        if (device.IsCharging)
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#0284C7"));
        if (effective <= 25 && (device.BatteryPercent <= 15 || string.Equals(device.DisplayLevelText, "Düşük", StringComparison.OrdinalIgnoreCase) || string.Equals(device.DisplayLevelText, "Boş", StringComparison.OrdinalIgnoreCase)))
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#DC2626"));
        if (effective <= 35)
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#D97706"));
        return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#16A34A"));
    }

    private static WpfBrush GetTaskbarPercentBrush(DeviceBatteryInfo device, bool isLightTaskbar)
    {
        int effective = device.EffectiveRingPercent;
        if (!device.IsConnected || effective < 0)
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(isLightTaskbar ? "#4B5563" : "#9CA3AF"));
        if (device.IsCharging)
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(isLightTaskbar ? "#0369A1" : "#38BDF8"));
        if (effective <= 25 && (device.BatteryPercent <= 15 || string.Equals(device.DisplayLevelText, "Düşük", StringComparison.OrdinalIgnoreCase) || string.Equals(device.DisplayLevelText, "Boş", StringComparison.OrdinalIgnoreCase)))
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(isLightTaskbar ? "#B91C1C" : "#F87171"));
        if (effective <= 35)
            return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(isLightTaskbar ? "#B45309" : "#FBBF24"));
        return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(isLightTaskbar ? "#15803D" : "#4ADE80"));
    }

    private void EnterTaskbarDockMode(double desiredLeft, bool save, TaskbarSnapshot? explicitSnapshot = null)
    {
        _isApplyingLayout = true;
        try
        {
            _isDockedToTaskbar = true;
            _isTemporarilyHidden = false;
            _settings.IsDockedToTaskbar = true;

            RootBorder.Visibility = Visibility.Collapsed;
            TaskbarRootBorder.Visibility = Visibility.Visible;
            DockToggleMenuItem.Header = "Masaüstüne Ayır";
            Topmost = true;
            Opacity = 1.0;
            RefreshTaskbarTheme(forceRender: true);
            UpdateLayout();
            SnapToTaskbar(desiredLeft, save, explicitSnapshot);

            _dockGuardianTimer.Start();
            TaskbarDockService.EnsureAboveTaskbar(this);
        }
        finally
        {
            _isApplyingLayout = false;
        }
    }

    private void ExitTaskbarDockMode(bool restoreAtCurrentDrop, bool save)
    {
        _isApplyingLayout = true;
        try
        {
            _dockGuardianTimer.Stop();
            _isTemporarilyHidden = false;
            _isDockedToTaskbar = false;
            _settings.IsDockedToTaskbar = false;

            double oldWidth = ActualWidth;
            double oldLeft = Left;
            double oldTop = Top;

            TaskbarRootBorder.Visibility = Visibility.Collapsed;
            RootBorder.Visibility = Visibility.Visible;
            DockToggleMenuItem.Header = "Görev Çubuğuna Sabitle";
            Topmost = _settings.AlwaysOnTop;
            Opacity = Math.Clamp(_settings.Opacity, 0.35, 1.0);

            RenderCurrentPage(force: true);
            UpdateLayout();

            double width = ActualWidth > 0 ? ActualWidth : 316;
            double height = ActualHeight > 0 ? ActualHeight : 210;
            var wa = SystemParameters.WorkArea;
            var workRect = new ScreenRect(wa.Left, wa.Top, wa.Right, wa.Bottom);

            double targetLeft;
            double targetTop;
            if (restoreAtCurrentDrop)
            {
                double centerOffset = (oldWidth - width) / 2.0;
                targetLeft = oldLeft + centerOffset;
                targetTop = oldTop - height - 8.0;
                if (targetTop < workRect.Top)
                    targetTop = oldTop;
            }
            else
            {
                targetLeft = _settings.WindowLeft;
                targetTop = _settings.WindowTop;
            }

            var (clampedLeft, clampedTop) = TaskbarDockService.ComputeUndockedPosition(
                targetLeft, targetTop, width, height, workRect);

            Left = clampedLeft;
            Top = clampedTop;

            if (save)
            {
                _settings.WindowLeft = clampedLeft;
                _settings.WindowTop = clampedTop;
                _settings.Save();
            }
        }
        finally
        {
            _isApplyingLayout = false;
        }
    }

    private void SnapToTaskbar(double desiredLeft, bool save, TaskbarSnapshot? explicitSnapshot = null)
    {
        var snap = explicitSnapshot ?? (double.IsNaN(desiredLeft)
            ? TaskbarDockService.GetPrimaryTaskbar(this)
            : TaskbarDockService.GetTaskbarNearPoint(this, desiredLeft + Math.Max(ActualWidth, 120) / 2.0, Top + Math.Max(ActualHeight, 24) / 2.0));

        if (!snap.Found)
            snap = TaskbarDockService.GetPrimaryTaskbar(this);

        if (!snap.Found || snap.TaskbarBoundsDip.Width <= 0 || snap.TaskbarBoundsDip.Height <= 0)
            return;

        double w = ActualWidth > 0 ? ActualWidth : 180;
        double h = ActualHeight > 0 ? ActualHeight : 28;
        var (newLeft, newTop) = TaskbarDockService.ComputeDockedPosition(
            desiredLeft, w, h, snap.TaskbarBoundsDip);

        _isApplyingLayout = true;
        try
        {
            Left = newLeft;
            Top = newTop;
        }
        finally
        {
            _isApplyingLayout = false;
        }

        if (save)
        {
            _settings.TaskbarDockLeft = newLeft;
            _settings.Save();
        }
    }

    private void OnDockGuardianTick(object? sender, EventArgs e)
    {
        if (!_isDockedToTaskbar || _isDragging || _isApplyingLayout || _isHiddenByUser)
            return;

        if (++_guardianTickCounter >= 4)
        {
            _guardianTickCounter = 0;
            RefreshTaskbarTheme(forceRender: false);
        }

        bool fullscreen = TaskbarDockService.IsFullscreenAppActive();
        double centerX = Left + Math.Max(ActualWidth, 80) / 2.0;
        double centerY = Top + Math.Max(ActualHeight, 20) / 2.0;
        var snap = TaskbarDockService.GetTaskbarNearPoint(this, centerX, centerY);

        bool shouldHide = fullscreen || !snap.Found || !snap.IsVisible || snap.IsAutoHideHidden;
        if (shouldHide)
        {
            if (IsVisible)
            {
                _isTemporarilyHidden = true;
                Hide();
            }
            return;
        }

        if (_isTemporarilyHidden && !IsVisible)
        {
            _isTemporarilyHidden = false;
            Show();
        }

        if (!IsVisible)
            return;

        double w = ActualWidth > 0 ? ActualWidth : 180;
        double h = ActualHeight > 0 ? ActualHeight : 28;
        var (expectedLeft, expectedTop) = TaskbarDockService.ComputeDockedPosition(
            double.IsNaN(_settings.TaskbarDockLeft) ? Left : _settings.TaskbarDockLeft,
            w,
            h,
            snap.TaskbarBoundsDip);

        if (Math.Abs(Left - expectedLeft) > 2.0 || Math.Abs(Top - expectedTop) > 1.5)
        {
            _isApplyingLayout = true;
            try
            {
                Left = expectedLeft;
                Top = expectedTop;
            }
            finally
            {
                _isApplyingLayout = false;
            }
        }

        TaskbarDockService.EnsureAboveTaskbar(this);
    }

    private void HandleDragCompleted()
    {
        var cursor = TaskbarDockService.GetCursorPositionDip(this);
        double probeX = double.IsNaN(cursor.X) ? Left + ActualWidth / 2.0 : cursor.X;
        double probeY = double.IsNaN(cursor.Y) ? Top + ActualHeight / 2.0 : cursor.Y;
        var snap = TaskbarDockService.GetTaskbarNearPoint(this, probeX, probeY);
        var winRect = new ScreenRect(Left, Top, Left + ActualWidth, Top + ActualHeight);

        if (!_isDockedToTaskbar)
        {
            if (snap.Found
                && snap.IsVisible
                && !snap.IsAutoHideHidden
                && TaskbarDockService.ShouldDockOnDrop(winRect, probeX, probeY, snap.TaskbarBoundsDip))
            {
                double droppedLeft = Left + Math.Max(0, (ActualWidth - 180) / 2.0);
                EnterTaskbarDockMode(droppedLeft, save: true, explicitSnapshot: snap);
                return;
            }

            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.Save();
        }
        else
        {
            if (!snap.Found
                || TaskbarDockService.ShouldUndockOnDrop(probeX, probeY, winRect, snap.TaskbarBoundsDip))
            {
                ExitTaskbarDockMode(restoreAtCurrentDrop: true, save: true);
                return;
            }

            SnapToTaskbar(Left, save: true, explicitSnapshot: snap);
            TaskbarDockService.EnsureAboveTaskbar(this);
        }
    }

    private void AdvancePage(int delta)
    {
        int totalPages = Math.Max(1, (int)Math.Ceiling(_allDevices.Count / (double)PageSize));
        if (totalPages <= 1)
            return;

        if (delta < 0)
            _currentPage = _currentPage < totalPages - 1 ? _currentPage + 1 : 0;
        else if (delta > 0)
            _currentPage = _currentPage > 0 ? _currentPage - 1 : totalPages - 1;

        RenderCurrentPage(force: true);
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        int totalPages = Math.Max(1, (int)Math.Ceiling(_allDevices.Count / (double)PageSize));
        if (totalPages <= 1)
            return;

        if (e.Delta < 0 && _currentPage < totalPages - 1)
        {
            _currentPage++;
            RenderCurrentPage(force: true);
        }
        else if (e.Delta > 0 && _currentPage > 0)
        {
            _currentPage--;
            RenderCurrentPage(force: true);
        }

        e.Handled = true;
    }

    private void TaskbarPageIndicator_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        AdvancePage(-1);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _isDockedToTaskbar)
        {
            e.Handled = true;
            _onOpenSettings();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        _isDragging = true;
        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can throw if mouse button was released before move began
        }
        finally
        {
            _isDragging = false;
        }

        HandleDragCompleted();
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (_isInitializing || _isDragging || _isApplyingLayout || _isDockedToTaskbar)
            return;

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.Save();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _monitorService.PollNowAsync();

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = AlwaysOnTopMenuItem.IsChecked;
        ApplySettings();
        _settings.Save();
    }

    private void ToggleDock_Click(object sender, RoutedEventArgs e) => ToggleTaskbarDock();
    private void Settings_Click(object sender, RoutedEventArgs e) => _onOpenSettings();
    private void Hide_Click(object sender, RoutedEventArgs e) => ToggleUserVisibility();
    private void Exit_Click(object sender, RoutedEventArgs e) => _onExit();
}
