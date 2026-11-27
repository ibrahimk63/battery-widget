using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
    private bool _isInitializing = true;

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

        PositionWindow();
        ApplySettings();
        _isInitializing = false;
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
        Topmost = _settings.AlwaysOnTop;
        AlwaysOnTopMenuItem.IsChecked = _settings.AlwaysOnTop;
        Opacity = Math.Clamp(_settings.Opacity, 0.35, 1.0);
    }

    public void UpdateDevices(IReadOnlyList<DeviceBatteryInfo> devices)
    {
        _allDevices = devices;
        int totalPages = Math.Max(1, (int)Math.Ceiling(_allDevices.Count / (double)PageSize));
        if (_currentPage >= totalPages)
            _currentPage = totalPages - 1;

        RenderCurrentPage();
    }

    private void RenderCurrentPage(bool force = false)
    {
        if (_allDevices.Count == 0)
        {
            if (_lastRenderedSignature != "EMPTY" || force)
            {
                DevicesPanel.Children.Clear();
                PaginationPanel.Children.Clear();
                PaginationPanel.Visibility = Visibility.Collapsed;
                EmptyStateText.Text = "Bağlı cihaz bulunamadı";
                EmptyStateText.Visibility = Visibility.Visible;
                _lastRenderedSignature = "EMPTY";
            }
            return;
        }

        int totalPages = Math.Max(1, (int)Math.Ceiling(_allDevices.Count / (double)PageSize));
        _currentPage = Math.Clamp(_currentPage, 0, totalPages - 1);

        var pageDevices = _allDevices
            .Skip(_currentPage * PageSize)
            .Take(PageSize)
            .ToList();

        var sigBuilder = new StringBuilder();
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

        EmptyStateText.Visibility = Visibility.Collapsed;
        DevicesPanel.Children.Clear();

        foreach (var device in pageDevices)
        {
            DevicesPanel.Children.Add(BuildRadialDeviceCard(device));
        }

        RenderPagination(totalPages);
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
        return card;
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

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (_isInitializing)
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

    private void Settings_Click(object sender, RoutedEventArgs e) => _onOpenSettings();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Exit_Click(object sender, RoutedEventArgs e) => _onExit();
}
