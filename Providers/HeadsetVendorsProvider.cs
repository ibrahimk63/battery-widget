using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BatteryWidget.Models;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace BatteryWidget.Providers;

/// <summary>
/// Reads battery status from:
/// 1) SteelSeries Arctis / Nova headsets (VID 0x1038)
/// 2) ATK N9 Pro / N9 Ultra (VID 0x373B, PID 0x11CB / 0x11CA / 0x1204 / 0x1203) via 9600-baud USB Serial COM port
/// 3) ATK N9 V2 Pro / Ultra / Ultimate (Bolutek HID Report 0x55 / cmd 0x65)
/// </summary>
public sealed class HeadsetVendorsProvider : IBatteryProvider
{
    public const int SteelSeriesVendorId = 0x1038;
    public const int AtkVendorId = 0x373B;

    private static readonly HashSet<int> AtkN9SerialPids = [0x11CB, 0x11CA, 0x1204, 0x1203];
    private static readonly HashSet<int> AtkN9BolutekPids = [0x1262, 0x1261, 0x12D4, 0x12D3, 0x12D6, 0x12D5];

    public string ProviderName => "SteelSeries & ATK N9 Kulaklıklar";

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            var results = new List<DeviceBatteryInfo>();
            results.AddRange(PollSteelSeries(cancellationToken));
            results.AddRange(PollAtkN9Serial(cancellationToken));
            results.AddRange(PollAtkN9Bolutek(cancellationToken));
            return results;
        }, cancellationToken);
    }

    private static List<DeviceBatteryInfo> PollSteelSeries(CancellationToken ct)
    {
        var results = new List<DeviceBatteryInfo>();
        var devices = DeviceList.Local.GetHidDevices(SteelSeriesVendorId)
            .Where(d => d.GetMaxOutputReportLength() >= 32 && d.GetMaxInputReportLength() >= 32)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            string key = $"{device.ProductID:X4}";
            if (seen.Contains(key))
                continue;

            try
            {
                if (!device.TryOpen(out var stream))
                    continue;

                using (stream)
                {
                    stream.ReadTimeout = 300;
                    stream.WriteTimeout = 300;

                    var parsed = TryQueryBytes(stream, device, [0x06, 0x18])
                              ?? TryQueryBytes(stream, device, [0xB0]);

                    if (parsed.HasValue)
                    {
                        seen.Add(key);
                        string name = TryGetProductName(device) ?? "SteelSeries Kablosuz";
                        results.Add(new DeviceBatteryInfo
                        {
                            Id = $"steelseries-{key}",
                            Name = name,
                            Category = BluetoothBatteryProvider.GuessCategory(name),
                            Connection = ConnectionKind.Wireless24Ghz,
                            BatteryPercent = parsed.Value.Percent,
                            IsCharging = parsed.Value.IsCharging,
                            IsConnected = true
                        });
                    }
                }
            }
            catch
            {
                // Ignore busy/unsupported interfaces
            }
        }

        return results;
    }

    private static List<DeviceBatteryInfo> PollAtkN9Serial(CancellationToken ct)
    {
        var results = new List<DeviceBatteryInfo>();

        var hidPresent = DeviceList.Local.GetHidDevices(AtkVendorId)
            .Where(d => AtkN9SerialPids.Contains(d.ProductID))
            .ToList();

        var ports = FindAtkN9SerialPorts();
        if (hidPresent.Count == 0 && ports.Count == 0)
            return results;

        int pid = hidPresent.FirstOrDefault()?.ProductID ?? ports.FirstOrDefault().Pid;
        string displayName = hidPresent.Select(TryGetProductName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
            ?? (pid is 0x1204 or 0x1203 ? "ATK N9 Ultra" : "ATK N9 Pro");
        bool isWired = pid is 0x11CA or 0x1203;
        string id = $"atk-n9-373B-{pid:X4}";

        bool accessDenied = false;

        foreach (var (portName, _) in ports)
        {
            ct.ThrowIfCancellationRequested();
            var (batResp, denied) = QuerySerial64(portName, [0x02, 0x00, 0x03]);
            if (denied)
            {
                accessDenied = true;
                continue;
            }

            if (batResp is not null)
            {
                var parsed = ParseAtkN9SerialBattery(batResp);
                if (parsed.HasValue)
                {
                    bool online = true;
                    var (statResp, _) = QuerySerial64(portName, [0x02, 0x00, 0x04]);
                    if (statResp is { Length: >= 5 } && statResp[1] == 0x00 && statResp[2] == 0x04)
                        online = statResp[4] == 1;

                    results.Add(new DeviceBatteryInfo
                    {
                        Id = id,
                        Name = displayName,
                        Category = DeviceCategory.Headset,
                        Connection = isWired ? ConnectionKind.UsbWired : ConnectionKind.Wireless24Ghz,
                        BatteryPercent = online ? parsed.Value.Percent : -1,
                        IsCharging = parsed.Value.IsCharging,
                        IsConnected = online,
                        StatusNote = online ? null : "Uyku"
                    });
                    return results;
                }
            }
        }

        // Dongle is connected, COM port held by ATK HUB or headset sleeping
        results.Add(new DeviceBatteryInfo
        {
            Id = id,
            Name = displayName,
            Category = DeviceCategory.Headset,
            Connection = isWired ? ConnectionKind.UsbWired : ConnectionKind.Wireless24Ghz,
            BatteryPercent = -1,
            IsConnected = false,
            StatusNote = accessDenied ? "ATK Hub Açık" : "Uyku / Yanıt Yok"
        });

        return results;
    }

    private static List<DeviceBatteryInfo> PollAtkN9Bolutek(CancellationToken ct)
    {
        var results = new List<DeviceBatteryInfo>();
        var devices = DeviceList.Local.GetHidDevices(AtkVendorId)
            .Where(d => AtkN9BolutekPids.Contains(d.ProductID) &&
                        d.GetMaxOutputReportLength() >= 21 &&
                        d.GetMaxInputReportLength() >= 21)
            .ToList();

        if (devices.Count == 0)
            return results;

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!device.TryOpen(out var stream))
                    continue;

                using (stream)
                {
                    stream.ReadTimeout = 300;
                    stream.WriteTimeout = 300;

                    var req = new byte[device.GetMaxOutputReportLength()];
                    req[0] = 0x55; // Report ID 85 (Bolutek)
                    req[1] = 0x65; // Vo.GetBattery = 101
                    req[2] = 0x00;

                    stream.Write(req, 0, req.Length);

                    var resp = new byte[device.GetMaxInputReportLength()];
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        int read = stream.Read(resp, 0, resp.Length);
                        if (read <= 0)
                            break;

                        var parsed = ParseAtkN9BolutekBattery(resp.AsSpan(0, read));
                        if (parsed.HasValue)
                        {
                            string name = TryGetProductName(device) ?? "ATK N9 V2";
                            results.Add(new DeviceBatteryInfo
                            {
                                Id = $"atk-n9-v2-{device.ProductID:X4}",
                                Name = name,
                                Category = DeviceCategory.Headset,
                                Connection = ConnectionKind.Wireless24Ghz,
                                BatteryPercent = parsed.Value.Percent,
                                IsCharging = parsed.Value.IsCharging,
                                IsConnected = true
                            });
                            return results;
                        }
                    }
                }
            }
            catch
            {
                // Try next collection
            }
        }

        return results;
    }

    /// <summary>
    /// Parses ATK N9 Pro / Ultra 9600-baud serial 64-byte response:
    /// [len, cmd(0x00), subCmd(0x03), ..., data[1] at index 4 -> bit7=charging, bits6..0=percent].
    /// </summary>
    public static (int Percent, bool IsCharging)? ParseAtkN9SerialBattery(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || data[1] != 0x00 || data[2] != 0x03)
            return null;

        byte raw = data[4];
        bool isCharging = (raw >> 7) == 1;
        int percent = raw & 0x7F;
        if (percent is < 0 or > 100)
            return null;

        return (percent, isCharging);
    }

    /// <summary>
    /// Parses ATK N9 V2 (Bolutek) HID response:
    /// - With Report ID 0x55: [0x55, 0x65, percent, status(2=charging)]
    /// - Raw: [0x65, percent, status(2=charging)]
    /// </summary>
    public static (int Percent, bool IsCharging)? ParseAtkN9BolutekBattery(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0x55 && data[1] == 0x65)
        {
            int pct = data[2];
            if (pct is >= 0 and <= 100)
                return (pct, data[3] == 2);
        }

        if (data.Length >= 3 && data[0] == 0x65)
        {
            int pct = data[1];
            if (pct is >= 0 and <= 100)
                return (pct, data[2] == 2);
        }

        return null;
    }

    private static (int Percent, bool IsCharging)? TryQueryBytes(HidStream stream, HidDevice dev, byte[] cmd)
    {
        try
        {
            var req = new byte[dev.GetMaxOutputReportLength()];
            Array.Copy(cmd, req, Math.Min(cmd.Length, req.Length));
            stream.Write(req, 0, req.Length);

            var resp = new byte[dev.GetMaxInputReportLength()];
            int read = stream.Read(resp, 0, resp.Length);
            if (read > 0)
                return ParseSteelSeriesResponse(resp.AsSpan(0, read));
        }
        catch
        {
            // Ignore timeout
        }
        return null;
    }

    public static (int Percent, bool IsCharging)? ParseSteelSeriesResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        if (data[0] == 0x06 && data[1] == 0x18)
        {
            int raw = data[2];
            int pct = raw <= 4 ? raw * 25 : Math.Clamp(raw, 0, 100);
            bool chg = data[3] == 1 || data[3] == 3;
            return (pct, chg);
        }

        if (data[0] == 0xB0 && data[2] is >= 0 and <= 100)
        {
            bool chg = data.Length > 3 && data[3] != 0;
            return (data[2], chg);
        }

        return null;
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }

    #region Win32 SetupAPI & Serial COM P/Invoke (Zero-dependency 9600-baud Serial)

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint GENERIC_READ_WRITE = 0xC0000000;
    private const uint OPEN_EXISTING = 3;
    private const uint PURGE_ALL = 0x000F;
    private const int ERROR_ACCESS_DENIED = 5;

    private static readonly Guid GUID_DEVINTERFACE_COMPORT = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public sbyte XonChar;
        public sbyte XoffChar;
        public sbyte ErrorChar;
        public sbyte EofChar;
        public sbyte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(in Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property,
        out uint PropertyRegDataType,
        byte[]? PropertyBuffer,
        uint PropertyBufferSize,
        out uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(SafeFileHandle hFile, ref DCB lpDCB);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(SafeFileHandle hFile, in DCB lpDCB);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommTimeouts(SafeFileHandle hFile, in COMMTIMEOUTS lpCommTimeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PurgeComm(SafeFileHandle hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    private static List<(string PortName, int Pid)> FindAtkN9SerialPorts()
    {
        var ports = new List<(string PortName, int Pid)>();
        Guid guid = GUID_DEVINTERFACE_COMPORT;
        IntPtr set = SetupDiGetClassDevsW(guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return ports;

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            uint idx = 0;

            while (SetupDiEnumDeviceInfo(set, idx++, ref data))
            {
                string? hwId = GetRegistryString(set, ref data, SPDRP_HARDWAREID);
                if (string.IsNullOrWhiteSpace(hwId) ||
                    !hwId.Contains("VID_373B", StringComparison.OrdinalIgnoreCase))
                    continue;

                int matchedPid = 0;
                foreach (int pid in AtkN9SerialPids)
                {
                    if (hwId.Contains($"PID_{pid:X4}", StringComparison.OrdinalIgnoreCase))
                    {
                        matchedPid = pid;
                        break;
                    }
                }
                if (matchedPid == 0)
                    continue;

                string? friendly = GetRegistryString(set, ref data, SPDRP_FRIENDLYNAME);
                if (string.IsNullOrWhiteSpace(friendly))
                    continue;

                var match = Regex.Match(friendly, @"\((COM\d+)\)", RegexOptions.IgnoreCase);
                if (match.Success)
                    ports.Add((match.Groups[1].Value.ToUpperInvariant(), matchedPid));
            }
        }
        catch
        {
            // Ignore SetupAPI failure
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return ports;
    }

    private static string? GetRegistryString(IntPtr set, ref SP_DEVINFO_DATA data, uint prop)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, null, 0, out uint reqSize);
        if (reqSize == 0 || reqSize > 4096)
            return null;

        var buf = new byte[reqSize];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, buf, reqSize, out _))
            return null;

        return Marshal.PtrToStringUni(Marshal.UnsafeAddrOfPinnedArrayElement(buf, 0))?.TrimEnd('\0');
    }

    private static (byte[]? Response, bool AccessDenied) QuerySerial64(string portName, byte[] cmdPrefix)
    {
        try
        {
            using var handle = CreateFileW($@"\\.\{portName}", GENERIC_READ_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                return (null, err == ERROR_ACCESS_DENIED);
            }

            var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
            if (GetCommState(handle, ref dcb))
            {
                dcb.BaudRate = 9600;
                dcb.ByteSize = 8;
                dcb.Parity = 0;   // NOPARITY
                dcb.StopBits = 0; // ONESTOPBIT
                dcb.Flags = 0x0001; // fBinary = 1
                SetCommState(handle, dcb);
            }

            var timeouts = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = 30,
                ReadTotalTimeoutMultiplier = 0,
                ReadTotalTimeoutConstant = 450,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 300
            };
            SetCommTimeouts(handle, timeouts);
            PurgeComm(handle, PURGE_ALL);

            var packet = new byte[64];
            Array.Copy(cmdPrefix, packet, Math.Min(cmdPrefix.Length, packet.Length));

            if (!WriteFile(handle, packet, (uint)packet.Length, out _, IntPtr.Zero))
                return (null, false);

            var resp = new byte[64];
            if (ReadFile(handle, resp, (uint)resp.Length, out uint bytesRead, IntPtr.Zero) && bytesRead >= 5)
                return (resp[..(int)bytesRead], false);
        }
        catch
        {
            // Ignore serial errors
        }

        return (null, false);
    }

    #endregion
}
