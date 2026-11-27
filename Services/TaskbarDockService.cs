using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BatteryWidget.Services;

public readonly record struct ScreenRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);
    public double CenterX => Left + Width / 2.0;
    public double CenterY => Top + Height / 2.0;

    public ScreenRect Intersect(ScreenRect other)
    {
        double l = Math.Max(Left, other.Left);
        double t = Math.Max(Top, other.Top);
        double r = Math.Min(Right, other.Right);
        double b = Math.Min(Bottom, other.Bottom);
        if (r <= l || b <= t)
            return new ScreenRect(l, t, l, t);
        return new ScreenRect(l, t, r, b);
    }
}

public sealed record TaskbarSnapshot(
    bool Found,
    bool IsVisible,
    ScreenRect TaskbarBoundsDip,
    ScreenRect MonitorBoundsDip,
    bool IsAutoHideHidden);

public static partial class TaskbarDockService
{
    private const int SwpNosize = 0x0001;
    private const int SwpNomove = 0x0002;
    private const int SwpNoactivate = 0x0010;
    private const int SwpShowwindow = 0x0040;
    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private static readonly IntPtr HwndTopmost = new(-1);

    [GeneratedRegex(@"\b(?:Wireless|Gaming|Headset|Mouse|Keyboard|Receiver|Dongle|2\.4\s*GHz|Bluetooth)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FillerWordsRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    public static bool IsPointInDockZone(
        double cursorX,
        double cursorY,
        ScreenRect taskbarBounds,
        double dockMarginDip = 8.0)
    {
        if (taskbarBounds.Width <= 0 || taskbarBounds.Height <= 0)
            return false;

        return cursorX >= taskbarBounds.Left - dockMarginDip
            && cursorX <= taskbarBounds.Right + dockMarginDip
            && cursorY >= taskbarBounds.Top - dockMarginDip
            && cursorY <= taskbarBounds.Bottom + dockMarginDip;
    }

    public static bool ShouldDockOnDrop(
        ScreenRect windowBounds,
        double cursorX,
        double cursorY,
        ScreenRect taskbarBounds)
    {
        if (taskbarBounds.Width <= 0 || taskbarBounds.Height <= 0)
            return false;

        if (IsPointInDockZone(cursorX, cursorY, taskbarBounds, 8.0))
            return true;

        var overlap = windowBounds.Intersect(taskbarBounds);
        double minOverlapHeight = Math.Min(16.0, taskbarBounds.Height * 0.4);

        return overlap.Width > 0
            && overlap.Height >= minOverlapHeight
            && windowBounds.CenterX >= taskbarBounds.Left
            && windowBounds.CenterX <= taskbarBounds.Right;
    }

    public static bool ShouldUndockOnDrop(
        double cursorX,
        double cursorY,
        ScreenRect windowBounds,
        ScreenRect taskbarBounds,
        double undockThresholdDip = 28.0)
    {
        if (taskbarBounds.Width <= 0 || taskbarBounds.Height <= 0)
            return true;

        bool isHorizontal = taskbarBounds.Width >= taskbarBounds.Height;
        if (isHorizontal)
        {
            return cursorY < taskbarBounds.Top - undockThresholdDip
                || cursorY > taskbarBounds.Bottom + undockThresholdDip;
        }

        return cursorX < taskbarBounds.Left - undockThresholdDip
            || cursorX > taskbarBounds.Right + undockThresholdDip;
    }

    public static (double Left, double Top) ComputeDockedPosition(
        double desiredLeft,
        double windowWidth,
        double windowHeight,
        ScreenRect taskbarBounds,
        double edgePaddingDip = 8.0)
    {
        double minLeft = taskbarBounds.Left + edgePaddingDip;
        double maxLeft = Math.Max(minLeft, taskbarBounds.Right - windowWidth - edgePaddingDip);

        double rawLeft = double.IsNaN(desiredLeft) || double.IsInfinity(desiredLeft)
            ? taskbarBounds.Right - windowWidth - 190.0
            : desiredLeft;

        double clampedLeft = Math.Clamp(rawLeft, minLeft, maxLeft);
        double centeredTop = taskbarBounds.Top + (taskbarBounds.Height - windowHeight) / 2.0;

        return (clampedLeft, centeredTop);
    }

    public static (double Left, double Top) ComputeUndockedPosition(
        double desiredLeft,
        double desiredTop,
        double windowWidth,
        double windowHeight,
        ScreenRect workArea,
        double marginDip = 12.0)
    {
        double minLeft = workArea.Left + marginDip;
        double maxLeft = Math.Max(minLeft, workArea.Right - windowWidth - marginDip);
        double minTop = workArea.Top + marginDip;
        double maxTop = Math.Max(minTop, workArea.Bottom - windowHeight - marginDip);

        double rawLeft = double.IsNaN(desiredLeft) || double.IsInfinity(desiredLeft)
            ? workArea.Right - windowWidth - marginDip
            : desiredLeft;

        double rawTop = double.IsNaN(desiredTop) || double.IsInfinity(desiredTop)
            ? workArea.Bottom - windowHeight - marginDip
            : desiredTop;

        return (Math.Clamp(rawLeft, minLeft, maxLeft), Math.Clamp(rawTop, minTop, maxTop));
    }

    public static bool IsTaskbarAutoHidden(ScreenRect taskbarBounds, ScreenRect monitorBounds)
    {
        if (monitorBounds.Width <= 0 || monitorBounds.Height <= 0)
            return taskbarBounds.Width <= 4.0 || taskbarBounds.Height <= 4.0;

        var visiblePart = taskbarBounds.Intersect(monitorBounds);
        return visiblePart.Width <= 4.0 || visiblePart.Height <= 4.0;
    }

    public static bool IsNotificationStateFullscreen(int qunsState)
    {
        // QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4
        return qunsState is 2 or 3 or 4;
    }

    public static string FormatShortDeviceName(string? name, int maxChars = 12)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "Cihaz";

        if (maxChars <= 1)
            return trimmed[..Math.Min(1, trimmed.Length)];

        if (trimmed.Length <= maxChars)
            return trimmed;

        string cleaned = FillerWordsRegex().Replace(trimmed, " ");
        cleaned = MultiSpaceRegex().Replace(cleaned, " ").Trim(' ', '-', '_');

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = trimmed;

        if (cleaned.Length <= maxChars)
            return cleaned;

        string truncated = cleaned[..(maxChars - 1)].TrimEnd(' ', '-');
        if (string.IsNullOrEmpty(truncated))
            truncated = cleaned[..(maxChars - 1)];

        return $"{truncated}…";
    }

    public static TaskbarSnapshot GetPrimaryTaskbar(Visual? referenceVisual)
    {
        return GetTaskbarNearPoint(referenceVisual, double.NaN, double.NaN);
    }

    public static TaskbarSnapshot GetTaskbarNearPoint(Visual? referenceVisual, double pointXDip, double pointYDip)
    {
        try
        {
            var (scaleX, scaleY) = GetScaleFromDevice(referenceVisual);
            var candidates = new List<(IntPtr Hwnd, bool IsPrimary, ScreenRect TaskbarDip, ScreenRect MonitorDip, bool IsVisible)>();

            EnumWindows((hwnd, _) =>
            {
                var sb = new StringBuilder(64);
                if (GetClassName(hwnd, sb, sb.Capacity) == 0)
                    return true;

                string cls = sb.ToString();
                bool isPrimary = string.Equals(cls, "Shell_TrayWnd", StringComparison.Ordinal);
                bool isSecondary = !isPrimary && string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.Ordinal);
                if (!isPrimary && !isSecondary)
                    return true;

                if (!GetWindowRect(hwnd, out var rect))
                    return true;

                var taskbarDip = new ScreenRect(
                    rect.Left * scaleX,
                    rect.Top * scaleY,
                    rect.Right * scaleX,
                    rect.Bottom * scaleY);

                var monitorDip = GetMonitorBoundsDip(hwnd, scaleX, scaleY, taskbarDip);
                bool isVisible = IsWindowVisible(hwnd);
                candidates.Add((hwnd, isPrimary, taskbarDip, monitorDip, isVisible));
                return true;
            }, IntPtr.Zero);

            if (candidates.Count == 0)
            {
                IntPtr primaryHwnd = FindWindow("Shell_TrayWnd", null);
                if (primaryHwnd == IntPtr.Zero || !GetWindowRect(primaryHwnd, out var rect))
                    return new TaskbarSnapshot(false, false, default, default, false);

                var tbDip = new ScreenRect(rect.Left * scaleX, rect.Top * scaleY, rect.Right * scaleX, rect.Bottom * scaleY);
                var monDip = GetMonitorBoundsDip(primaryHwnd, scaleX, scaleY, tbDip);
                bool vis = IsWindowVisible(primaryHwnd);
                return new TaskbarSnapshot(true, vis, tbDip, monDip, IsTaskbarAutoHidden(tbDip, monDip));
            }

            var chosen = candidates[0];
            if (double.IsNaN(pointXDip) || double.IsNaN(pointYDip))
            {
                chosen = candidates.FirstOrDefault(c => c.IsPrimary);
                if (chosen.Hwnd == IntPtr.Zero)
                    chosen = candidates[0];
            }
            else
            {
                double bestDistance = double.MaxValue;
                foreach (var c in candidates)
                {
                    if (IsPointInDockZone(pointXDip, pointYDip, c.TaskbarDip, 12.0))
                    {
                        chosen = c;
                        break;
                    }

                    double dx = Math.Max(0, Math.Max(c.TaskbarDip.Left - pointXDip, pointXDip - c.TaskbarDip.Right));
                    double dy = Math.Max(0, Math.Max(c.TaskbarDip.Top - pointYDip, pointYDip - c.TaskbarDip.Bottom));
                    double dist = dx * dx + dy * dy;
                    if (dist < bestDistance || (Math.Abs(dist - bestDistance) < 0.01 && c.IsPrimary))
                    {
                        bestDistance = dist;
                        chosen = c;
                    }
                }
            }

            bool autoHidden = IsTaskbarAutoHidden(chosen.TaskbarDip, chosen.MonitorDip);
            return new TaskbarSnapshot(true, chosen.IsVisible, chosen.TaskbarDip, chosen.MonitorDip, autoHidden);
        }
        catch
        {
            return new TaskbarSnapshot(false, false, default, default, false);
        }
    }

    public static System.Windows.Point GetCursorPositionDip(Visual? referenceVisual)
    {
        try
        {
            if (GetCursorPos(out var pt))
            {
                var (scaleX, scaleY) = GetScaleFromDevice(referenceVisual);
                return new System.Windows.Point(pt.X * scaleX, pt.Y * scaleY);
            }
        }
        catch
        {
            // Ignore cursor query failures
        }
        return new System.Windows.Point(double.NaN, double.NaN);
    }

    public static bool IsFullscreenAppActive()
    {
        try
        {
            int hr = SHQueryUserNotificationState(out int state);
            return hr == 0 && IsNotificationStateFullscreen(state);
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureAboveTaskbar(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            SetWindowPos(
                hwnd,
                HwndTopmost,
                0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
        }
        catch
        {
            // Ignore transient HWND failures
        }
    }

    public static void ApplyToolWindowStyle(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            nint exStyle = GetWindowLongPtrCompat(hwnd, GwlExstyle);
            SetWindowLongPtrCompat(hwnd, GwlExstyle, exStyle | WsExToolwindow);
        }
        catch
        {
            // Ignore extended style failures
        }
    }

    private static ScreenRect GetMonitorBoundsDip(IntPtr hwnd, double scaleX, double scaleY, ScreenRect fallback)
    {
        try
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    return new ScreenRect(
                        info.RcMonitor.Left * scaleX,
                        info.RcMonitor.Top * scaleY,
                        info.RcMonitor.Right * scaleX,
                        info.RcMonitor.Bottom * scaleY);
                }
            }
        }
        catch
        {
            // Fall back to taskbar bounds
        }
        return fallback;
    }

    private static (double ScaleX, double ScaleY) GetScaleFromDevice(Visual? referenceVisual)
    {
        try
        {
            if (referenceVisual is not null)
            {
                var source = PresentationSource.FromVisual(referenceVisual);
                if (source?.CompositionTarget is not null)
                {
                    var m = source.CompositionTarget.TransformFromDevice;
                    return (m.M11 > 0 ? m.M11 : 1.0, m.M22 > 0 ? m.M22 : 1.0);
                }
            }
        }
        catch
        {
            // Fall back to 1.0 DPI scale
        }
        return (1.0, 1.0);
    }

    private static nint GetWindowLongPtrCompat(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : GetWindowLong32(hwnd, index);
    }

    private static nint SetWindowLongPtrCompat(IntPtr hwnd, int index, nint value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : SetWindowLong32(hwnd, index, (int)value);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        int uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(IntPtr hWnd, int nIndex, nint dwNewLong);
}
