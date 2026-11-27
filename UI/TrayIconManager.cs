using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BatteryWidget.Models;

namespace BatteryWidget.UI;

public sealed class TrayIconManager : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private Icon? _customIcon;

    public void Initialize(
        Action onToggleWidget,
        Action onRefresh,
        Action onOpenSettings,
        Action onExit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Widget'ı Göster / Gizle", null, (_, _) => onToggleWidget());
        menu.Items.Add("Şimdi Yenile", null, (_, _) => onRefresh());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ayarlar & HID Tanılama", null, (_, _) => onOpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => onExit());

        _customIcon = CreateBatteryIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _customIcon,
            Text = "Battery Dashboard",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                onToggleWidget();
        };
    }

    public static Icon CreateBatteryIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Dark rounded background
        using var bgBrush = new SolidBrush(Color.FromArgb(255, 15, 17, 23));
        using var bgPath = CreateRoundedRect(new Rectangle(1, 1, 30, 30), 7);
        g.FillPath(bgBrush, bgPath);

        using var borderPen = new Pen(Color.FromArgb(255, 34, 197, 94), 2f);
        g.DrawPath(borderPen, bgPath);

        // Battery body
        using var bodyPen = new Pen(Color.White, 2f);
        g.DrawRectangle(bodyPen, 7, 10, 15, 12);

        // Battery cap
        using var capBrush = new SolidBrush(Color.White);
        g.FillRectangle(capBrush, 23, 13, 3, 6);

        // Green charge level bars inside
        using var fillBrush = new SolidBrush(Color.FromArgb(255, 34, 197, 94));
        g.FillRectangle(fillBrush, 10, 13, 4, 6);
        g.FillRectangle(fillBrush, 15, 13, 4, 6);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void UpdateTooltip(IReadOnlyList<DeviceBatteryInfo> devices)
    {
        if (_notifyIcon is null)
            return;

        if (devices.Count == 0)
        {
            _notifyIcon.Text = "Battery Dashboard — Bağlı cihaz yok";
            return;
        }

        var summary = string.Join(" | ", devices.Take(3).Select(d =>
            $"{Shorten(d.Name)}: {d.PercentText}"));

        _notifyIcon.Text = summary.Length > 63 ? summary[..60] + "..." : summary;
    }

    public void ShowNotification(string title, string message)
    {
        _notifyIcon?.ShowBalloonTip(5000, title, message, ToolTipIcon.Warning);
    }

    private static string Shorten(string name)
    {
        return name.Length > 14 ? name[..14] : name;
    }

    public void Dispose()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _customIcon?.Dispose();
        _customIcon = null;
    }
}
