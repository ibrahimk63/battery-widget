using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using BatteryWidget.Models;

namespace BatteryWidget.UI;

/// <summary>
/// Manages the Windows notification area (system tray) icon, tooltip, balloon alerts,
/// and native context menu using pure Win32 Shell_NotifyIconW without WinForms or System.Drawing.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint WmTrayIcon = 0x8001;
    private const int WmNull = 0x0000;
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifWarning = 0x00000002;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;

    private const uint TpmRightButton = 0x0002;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;

    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const uint ImageIcon = 1;
    private const uint LrDefaultColor = 0x00000000;
    private const int IdiApplication = 32512;

    private const uint MenuCmdToggleWidget = 1;
    private const uint MenuCmdToggleDock = 2;
    private const uint MenuCmdRefresh = 3;
    private const uint MenuCmdSettings = 4;
    private const uint MenuCmdExit = 5;

    private Dispatcher? _dispatcher;
    private HwndSource? _hwndSource;
    private IntPtr _hIcon;
    private bool _ownsIcon;
    private bool _iconAdded;
    private bool _disposed;
    private uint _taskbarCreatedMsg;
    private string _currentTooltip = "Battery Dashboard";

    private Action? _onToggleWidget;
    private Action? _onToggleTaskbarDock;
    private Action? _onRefresh;
    private Action? _onOpenSettings;
    private Action? _onExit;

    public void Initialize(
        Action onToggleWidget,
        Action onRefresh,
        Action onOpenSettings,
        Action onExit,
        Action? onToggleTaskbarDock = null)
    {
        if (_disposed)
            return;

        _onToggleWidget = onToggleWidget;
        _onRefresh = onRefresh;
        _onOpenSettings = onOpenSettings;
        _onExit = onExit;
        _onToggleTaskbarDock = onToggleTaskbarDock;

        _dispatcher = Dispatcher.CurrentDispatcher;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        var parameters = new HwndSourceParameters("BatteryWidgetTrayHost")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        (_hIcon, _ownsIcon) = LoadTrayIconHandle();
        AddOrRestoreTrayIcon();
    }

    public void UpdateTooltip(IReadOnlyList<DeviceBatteryInfo> devices)
    {
        if (_disposed)
            return;

        string tooltip = FormatTooltipText(devices);

        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => ApplyTooltip(tooltip));
            return;
        }

        ApplyTooltip(tooltip);
    }

    public void ShowNotification(string title, string message)
    {
        if (_disposed)
            return;

        string safeTitle = TruncateForNotifyIcon(title, 63);
        string safeMessage = TruncateForNotifyIcon(message, 255);

        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => ApplyNotification(safeTitle, safeMessage));
            return;
        }

        ApplyNotification(safeTitle, safeMessage);
    }

    public static string FormatTooltipText(IReadOnlyList<DeviceBatteryInfo> devices)
    {
        if (devices.Count == 0)
            return "Battery Dashboard — Bağlı cihaz yok";

        var summary = string.Join(" | ", devices.Take(3).Select(d =>
            $"{Shorten(d.Name)}: {d.PercentText}"));

        return TruncateForNotifyIcon(summary, 127);
    }

    public static string TruncateForNotifyIcon(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
            return string.Empty;

        if (text.Length <= maxChars)
            return text;

        if (maxChars <= 3)
            return text[..maxChars];

        return text[..(maxChars - 3)] + "...";
    }

    public static byte[]? LoadEmbeddedAppIconBytes()
    {
        using Stream? stream = typeof(TrayIconManager).Assembly.GetManifestResourceStream("BatteryWidget.app.ico");
        if (stream is null)
            return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static byte[]? ExtractBestIconFromIcoBytes(byte[] icoBytes, int targetWidth, int targetHeight)
    {
        if (icoBytes is null || icoBytes.Length < 6)
            return null;

        ushort reserved = BitConverter.ToUInt16(icoBytes, 0);
        ushort type = BitConverter.ToUInt16(icoBytes, 2);
        ushort count = BitConverter.ToUInt16(icoBytes, 4);
        if (reserved != 0 || type != 1 || count == 0)
            return null;

        int bestDiff = int.MaxValue;
        bool bestIsAtLeastTarget = false;
        uint bestOffset = 0;
        uint bestLength = 0;

        for (int i = 0; i < count; i++)
        {
            int entryOffset = 6 + (i * 16);
            if (entryOffset + 16 > icoBytes.Length)
                break;

            int width = icoBytes[entryOffset] == 0 ? 256 : icoBytes[entryOffset];
            int height = icoBytes[entryOffset + 1] == 0 ? 256 : icoBytes[entryOffset + 1];
            uint bytesInRes = BitConverter.ToUInt32(icoBytes, entryOffset + 8);
            uint imageOffset = BitConverter.ToUInt32(icoBytes, entryOffset + 12);

            if (bytesInRes == 0 || (ulong)imageOffset + bytesInRes > (ulong)icoBytes.Length)
                continue;

            int diff = Math.Abs(width - targetWidth) + Math.Abs(height - targetHeight);
            bool isAtLeastTarget = width >= targetWidth && height >= targetHeight;

            if (diff < bestDiff || (diff == bestDiff && isAtLeastTarget && !bestIsAtLeastTarget))
            {
                bestDiff = diff;
                bestIsAtLeastTarget = isAtLeastTarget;
                bestOffset = imageOffset;
                bestLength = bytesInRes;
            }
        }

        if (bestLength == 0)
            return null;

        return icoBytes.AsSpan((int)bestOffset, (int)bestLength).ToArray();
    }

    private void ApplyTooltip(string tooltip)
    {
        _currentTooltip = tooltip;
        if (_disposed || !_iconAdded || _hwndSource is null)
            return;

        var data = CreateNotifyIconData();
        data.uFlags = NifTip;
        data.szTip = _currentTooltip;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private void ApplyNotification(string title, string message)
    {
        if (_disposed || !_iconAdded || _hwndSource is null)
            return;

        var data = CreateNotifyIconData();
        data.uFlags = NifInfo;
        data.szInfoTitle = title;
        data.szInfo = message;
        data.dwInfoFlags = NiifWarning;
        data.uTimeoutOrVersion = 5000;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private void AddOrRestoreTrayIcon()
    {
        if (_disposed || _hwndSource is null)
            return;

        var data = CreateNotifyIconData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = WmTrayIcon;
        data.hIcon = _hIcon;
        data.szTip = _currentTooltip;

        _iconAdded = Shell_NotifyIconW(NimAdd, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed)
            return IntPtr.Zero;

        if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            AddOrRestoreTrayIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == (int)WmTrayIcon)
        {
            int mouseMsg = unchecked((int)(lParam.ToInt64() & 0xFFFF));
            if (mouseMsg == WmLButtonUp)
            {
                _onToggleWidget?.Invoke();
                handled = true;
            }
            else if (mouseMsg == WmRButtonUp || mouseMsg == WmContextMenu)
            {
                ShowContextMenu();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_disposed || _hwndSource is null)
            return;

        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
            return;

        uint selected = 0;
        try
        {
            AppendMenuW(hMenu, MfString, (UIntPtr)MenuCmdToggleWidget, "Widget'ı Göster / Gizle");
            if (_onToggleTaskbarDock is not null)
                AppendMenuW(hMenu, MfString, (UIntPtr)MenuCmdToggleDock, "Görev Çubuğuna Sabitle / Ayır");
            AppendMenuW(hMenu, MfString, (UIntPtr)MenuCmdRefresh, "Şimdi Yenile");
            AppendMenuW(hMenu, MfSeparator, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MfString, (UIntPtr)MenuCmdSettings, "Ayarlar & HID Tanılama");
            AppendMenuW(hMenu, MfSeparator, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MfString, (UIntPtr)MenuCmdExit, "Çıkış");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwndSource.Handle);
            selected = TrackPopupMenuEx(
                hMenu,
                TpmRightButton | TpmReturnCmd | TpmNonotify,
                pt.X,
                pt.Y,
                _hwndSource.Handle,
                IntPtr.Zero);
            PostMessageW(_hwndSource.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(hMenu);
        }

        switch (selected)
        {
            case MenuCmdToggleWidget:
                _onToggleWidget?.Invoke();
                break;
            case MenuCmdToggleDock:
                _onToggleTaskbarDock?.Invoke();
                break;
            case MenuCmdRefresh:
                _onRefresh?.Invoke();
                break;
            case MenuCmdSettings:
                _onOpenSettings?.Invoke();
                break;
            case MenuCmdExit:
                _onExit?.Invoke();
                break;
        }
    }

    private static (IntPtr IconHandle, bool OwnsHandle) LoadTrayIconHandle()
    {
        int cx = GetSystemMetrics(SmCxSmIcon);
        int cy = GetSystemMetrics(SmCySmIcon);
        if (cx <= 0) cx = 16;
        if (cy <= 0) cy = 16;

        byte[]? icoBytes = LoadEmbeddedAppIconBytes();
        if (icoBytes is not null)
        {
            byte[]? iconPayload = ExtractBestIconFromIcoBytes(icoBytes, cx, cy);
            if (iconPayload is not null)
            {
                IntPtr hIcon = CreateIconFromResourceEx(
                    iconPayload,
                    (uint)iconPayload.Length,
                    true,
                    0x00030000,
                    cx,
                    cy,
                    LrDefaultColor);
                if (hIcon != IntPtr.Zero)
                    return (hIcon, true);
            }
        }

        IntPtr moduleHandle = GetModuleHandleW(null);
        IntPtr appIcon = LoadImageW(moduleHandle, (IntPtr)IdiApplication, ImageIcon, cx, cy, LrDefaultColor);
        if (appIcon != IntPtr.Zero)
            return (appIcon, true);

        return (LoadIconW(IntPtr.Zero, (IntPtr)IdiApplication), false);
    }

    private NOTIFYICONDATAW CreateNotifyIconData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwndSource?.Handle ?? IntPtr.Zero,
            uID = TrayIconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private static string Shorten(string name)
        => name.Length > 14 ? name[..14] : name;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_iconAdded && _hwndSource is not null)
        {
            var data = CreateNotifyIconData();
            Shell_NotifyIconW(NimDelete, ref data);
            _iconAdded = false;
        }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }

        if (_ownsIcon && _hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
        }

        _hIcon = IntPtr.Zero;
        _ownsIcon = false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(
        IntPtr hmenu,
        uint fuFlags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(
        [In] byte[] presbits,
        uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer,
        int cxDesired,
        int cyDesired,
        uint Flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr hInst,
        IntPtr lpszName,
        uint uType,
        int cxDesired,
        int cyDesired,
        uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
