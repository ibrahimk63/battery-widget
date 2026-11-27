using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryWidget.Models;
using BatteryWidget.Services;
using HidSharp;

namespace BatteryWidget.Providers;

/// <summary>
/// Loads USB/2.4GHz HID device profiles in priority order:
/// 1) User Auto-Learn profiles (%AppData%\BatteryWidget\profiles\*.json)
/// 2) Remote synced / embedded baseline catalog (profiles-db.json)
/// 3) App-local profiles\*.json
/// </summary>
public sealed class JsonHidProfileProvider : IBatteryProvider, IDisposable
{
    public readonly record struct ParsedProfileFrame(int Percent, bool IsCharging, bool IsOnline);

    public sealed record PassiveCandidateReading(
        int Pid,
        int Percent,
        bool IsCharging,
        bool IsOnline,
        bool IsFresh,
        DateTimeOffset UpdatedUtc,
        string? ProductName);

    private sealed class PassiveListenerSession : IDisposable
    {
        private readonly HidDevice _device;
        private readonly HidDeviceProfile _profile;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private HidStream? _stream;
        private ParsedProfileFrame? _latestFrame;
        private DateTimeOffset _latestUtc;
        private volatile bool _exited;
        private int _disposed;

        public int VendorId { get; }
        public int ProductId { get; }
        public string? ProductName { get; }
        public string ProfileSignature { get; }
        public ManualResetEventSlim FirstFrameEvent { get; } = new(false);
        public bool IsExited => _exited || Volatile.Read(ref _disposed) != 0;

        public PassiveListenerSession(HidDevice device, HidDeviceProfile profile, string profileSignature)
        {
            _device = device;
            _profile = profile;
            ProfileSignature = profileSignature;
            VendorId = device.VendorID;
            ProductId = device.ProductID;
            ProductName = TryGetProductName(device);

            var thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = $"HidPassive-{VendorId:X4}-{ProductId:X4}"
            };
            thread.Start();
        }

        public bool TryGetReading(out ParsedProfileFrame frame, out DateTimeOffset updatedUtc)
        {
            lock (_lock)
            {
                if (_latestFrame.HasValue)
                {
                    frame = _latestFrame.Value;
                    updatedUtc = _latestUtc;
                    return true;
                }
            }

            frame = default;
            updatedUtc = default;
            return false;
        }

        private void RunLoop()
        {
            try
            {
                if (!_device.TryOpen(out var stream))
                    return;

                lock (_lock)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        stream.Dispose();
                        return;
                    }
                    _stream = stream;
                }

                stream.ReadTimeout = 1500;
                int inLen = Math.Max(1, _device.GetMaxInputReportLength());
                var buf = new byte[inLen];

                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        int read = stream.Read(buf, 0, buf.Length);
                        if (read <= 0)
                            continue;

                        var parsed = ParseFrameWithProfile(_profile, buf.AsSpan(0, read));
                        if (parsed.HasValue)
                        {
                            lock (_lock)
                            {
                                _latestFrame = parsed.Value;
                                _latestUtc = DateTimeOffset.UtcNow;
                            }
                            FirstFrameEvent.Set();
                        }
                    }
                    catch (TimeoutException)
                    {
                        // Normal idle wait between 3-5s device heartbeats
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Ignore open/read errors on unplugged device
            }
            finally
            {
                _exited = true;
                try { FirstFrameEvent.Set(); } catch { /* disposed */ }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _cts.Cancel();
            HidStream? streamToClose;
            lock (_lock)
            {
                streamToClose = _stream;
                _stream = null;
            }
            try { streamToClose?.Dispose(); } catch { /* ignore */ }
            try { FirstFrameEvent.Dispose(); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    private readonly string[] _profilesDirectories;
    private readonly bool _includeRemoteCatalog;
    private readonly ConcurrentDictionary<string, PassiveListenerSession> _passiveListeners =
        new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string UserProfilesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryWidget",
        "profiles");

    public string ProviderName => "JSON & Uzak HID Profilleri";

    public JsonHidProfileProvider(string? profilesDirectory = null)
    {
        if (profilesDirectory is not null)
        {
            _profilesDirectories = [profilesDirectory];
            _includeRemoteCatalog = false;
        }
        else
        {
            _profilesDirectories = [UserProfilesDirectory, Path.Combine(AppContext.BaseDirectory, "profiles")];
            _includeRemoteCatalog = true;
        }
    }

    public IReadOnlyList<HidDeviceProfile> LoadProfiles()
    {
        var list = new List<HidDeviceProfile>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddProfileIfNew(HidDeviceProfile? profile)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
                return;

            var pids = string.Join(",", profile.ProductIdsHex.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            string key = $"{profile.VendorIdHex}:{pids}:{profile.Name}";
            if (seenKeys.Add(key))
                list.Add(profile);
        }

        // 1) User Auto-Learn profiles take highest priority
        if (_profilesDirectories.Length > 0 && Directory.Exists(_profilesDirectories[0]))
        {
            foreach (var file in Directory.EnumerateFiles(_profilesDirectories[0], "*.json"))
            {
                try
                {
                    AddProfileIfNew(JsonSerializer.Deserialize<HidDeviceProfile>(File.ReadAllText(file), JsonOptions));
                }
                catch
                {
                    // Skip invalid profile files
                }
            }
        }

        // 2) Remote synced or embedded baseline profiles-db.json
        if (_includeRemoteCatalog)
        {
            foreach (var profile in RemoteProfileSyncService.LoadCachedOrEmbeddedDatabase().Profiles)
                AddProfileIfNew(profile);
        }

        // 3) Remaining app-local directories
        for (int i = 1; i < _profilesDirectories.Length; i++)
        {
            string dir = _profilesDirectories[i];
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    AddProfileIfNew(JsonSerializer.Deserialize<HidDeviceProfile>(File.ReadAllText(file), JsonOptions));
                }
                catch
                {
                    // Skip invalid profile files
                }
            }
        }

        return list;
    }

    public Task<IReadOnlyList<DeviceBatteryInfo>> PollDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeviceBatteryInfo>>(() =>
        {
            if (Volatile.Read(ref _disposed) != 0)
                return [];

            var results = new List<DeviceBatteryInfo>();
            var profiles = LoadProfiles();
            if (profiles.Count == 0)
                return results;

            var allHidDevices = DeviceList.Local.GetHidDevices().Where(d => d.VendorID > 0).ToList();
            PrunePassiveListeners(allHidDevices);

            foreach (var profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = profile.ReadPassiveInput
                    ? PollPassiveProfile(profile, allHidDevices, cancellationToken)
                    : PollActiveProfile(profile, allHidDevices);

                if (info is not null)
                    results.Add(info);
            }

            return results;
        }, cancellationToken);
    }

    private void PrunePassiveListeners(IReadOnlyList<HidDevice> allHidDevices)
    {
        if (_passiveListeners.IsEmpty)
            return;

        var activePaths = new HashSet<string>(allHidDevices.Select(d => d.DevicePath), StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _passiveListeners)
        {
            if (!activePaths.Contains(kv.Key) || kv.Value.IsExited)
            {
                if (_passiveListeners.TryRemove(kv.Key, out var removed))
                    removed.Dispose();
            }
        }
    }

    private DeviceBatteryInfo? PollPassiveProfile(
        HidDeviceProfile profile,
        IReadOnlyList<HidDevice> allHidDevices,
        CancellationToken cancellationToken)
    {
        int vid = profile.ParseVendorId();
        var pids = profile.ParseProductIds();
        var wiredPids = profile.ParseWiredProductIds();

        var devices = allHidDevices
            .Where(d => d.VendorID == vid &&
                        (pids.Count == 0 || pids.Contains(d.ProductID)) &&
                        d.GetMaxInputReportLength() > 0 &&
                        MatchesDescriptor(d, profile))
            .OrderByDescending(d => wiredPids.Contains(d.ProductID))
            .ToList();

        if (devices.Count == 0)
            return null;

        string sig = $"{profile.Name}|{profile.ExpectedReportId}|{profile.BatteryByteIndex}|{profile.LinkStatusByteIndex}";
        var sessions = new List<PassiveListenerSession>(devices.Count);

        foreach (var device in devices)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return null;

            var session = _passiveListeners.GetOrAdd(
                device.DevicePath,
                _ => new PassiveListenerSession(device, profile, sig));

            if (!string.Equals(session.ProfileSignature, sig, StringComparison.Ordinal) || session.IsExited)
            {
                if (_passiveListeners.TryRemove(device.DevicePath, out var old))
                    old.Dispose();

                session = _passiveListeners.GetOrAdd(
                    device.DevicePath,
                    _ => new PassiveListenerSession(device, profile, sig));
            }

            sessions.Add(session);
        }

        // Wait up to 3.5s on cold start if no session has received an initial heartbeat yet
        if (!sessions.Any(s => s.TryGetReading(out _, out _)) && sessions.Any(s => !s.IsExited))
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(3500);
            while (DateTimeOffset.UtcNow < deadline &&
                   !cancellationToken.IsCancellationRequested &&
                   Volatile.Read(ref _disposed) == 0)
            {
                if (sessions.Any(s => s.TryGetReading(out _, out _)) || sessions.All(s => s.IsExited))
                    break;

                Thread.Sleep(50);
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int staleSeconds = Math.Max(10, profile.PassiveStaleSeconds);
        var readings = new List<PassiveCandidateReading>();

        foreach (var session in sessions)
        {
            if (!session.TryGetReading(out var frame, out var updatedUtc))
                continue;

            bool isFresh = (now - updatedUtc).TotalSeconds <= staleSeconds;
            readings.Add(new PassiveCandidateReading(
                session.ProductId,
                frame.Percent,
                frame.IsCharging,
                frame.IsOnline,
                isFresh,
                updatedUtc,
                session.ProductName));
        }

        var selected = SelectBestPassiveCandidate(profile, readings);
        if (selected is null)
            return null;

        int canonicalPid = ResolveCanonicalPid(profile, selected.Pid);
        ConnectionKind conn = wiredPids.Contains(selected.Pid)
            ? ConnectionKind.UsbWired
            : ConnectionKind.Wireless24Ghz;

        if (!selected.IsOnline || !selected.IsFresh)
        {
            return new DeviceBatteryInfo
            {
                Id = $"profile-{vid:X4}-{canonicalPid:X4}",
                Name = ResolveDisplayName(profile, selected.ProductName),
                Category = profile.Category,
                Connection = conn,
                BatteryPercent = -1,
                IsConnected = false,
                StatusNote = "Uyku"
            };
        }

        return new DeviceBatteryInfo
        {
            Id = $"profile-{vid:X4}-{canonicalPid:X4}",
            Name = ResolveDisplayName(profile, selected.ProductName),
            Category = profile.Category,
            Connection = conn,
            BatteryPercent = selected.Percent,
            IsCharging = selected.IsCharging,
            IsConnected = true
        };
    }

    private static DeviceBatteryInfo? PollActiveProfile(HidDeviceProfile profile, IReadOnlyList<HidDevice> allHidDevices)
    {
        int vid = profile.ParseVendorId();
        var pids = profile.ParseProductIds();
        var wiredPids = profile.ParseWiredProductIds();
        var devices = allHidDevices
            .Where(d => d.VendorID == vid &&
                        (pids.Count == 0 || pids.Contains(d.ProductID)) &&
                        MatchesDescriptor(d, profile))
            .OrderByDescending(d => wiredPids.Contains(d.ProductID))
            .ToList();

        if (devices.Count == 0)
            return null;

        var query = profile.ParseQueryPacket();
        var wakeup = profile.ParseWakeupPacket();

        foreach (var device in devices)
        {
            try
            {
                if (!device.TryOpen(out var stream))
                    continue;

                using (stream)
                {
                    stream.ReadTimeout = 300;
                    stream.WriteTimeout = 300;

                    if (wakeup is not null && device.GetMaxOutputReportLength() > 0)
                    {
                        var wakeBuf = new byte[device.GetMaxOutputReportLength()];
                        Array.Copy(wakeup, wakeBuf, Math.Min(wakeup.Length, wakeBuf.Length));
                        try
                        {
                            stream.Write(wakeBuf, 0, wakeBuf.Length);
                            Thread.Sleep(15);
                        }
                        catch { /* optional wakeup */ }
                    }

                    if (profile.UseFeatureReport && device.GetMaxFeatureReportLength() > 0)
                    {
                        var buf = new byte[device.GetMaxFeatureReportLength()];
                        if (query is not null)
                        {
                            Array.Copy(query, buf, Math.Min(query.Length, buf.Length));
                            profile.ApplyChecksum(buf);
                            try { stream.SetFeature(buf); Thread.Sleep(15); } catch { /* some devices only support GetFeature */ }
                        }
                        else if (profile.ExpectedReportId >= 0)
                        {
                            buf[0] = (byte)profile.ExpectedReportId;
                        }

                        stream.GetFeature(buf);
                        var parsed = ParseWithProfile(profile, buf);
                        if (parsed.HasValue)
                            return BuildInfo(profile, vid, device.ProductID, parsed.Value, TryGetProductName(device), wiredPids);
                    }
                    else if (device.GetMaxInputReportLength() > 0)
                    {
                        if (query is not null && device.GetMaxOutputReportLength() > 0)
                        {
                            var outBuf = new byte[device.GetMaxOutputReportLength()];
                            Array.Copy(query, outBuf, Math.Min(query.Length, outBuf.Length));
                            profile.ApplyChecksum(outBuf);
                            stream.Write(outBuf, 0, outBuf.Length);
                        }

                        var inBuf = new byte[device.GetMaxInputReportLength()];
                        for (int i = 0; i < 2; i++)
                        {
                            int read = stream.Read(inBuf, 0, inBuf.Length);
                            if (read <= 0)
                                break;

                            var parsed = ParseWithProfile(profile, inBuf.AsSpan(0, read));
                            if (parsed.HasValue)
                                return BuildInfo(profile, vid, device.ProductID, parsed.Value, TryGetProductName(device), wiredPids);
                        }
                    }
                }
            }
            catch
            {
                // Try next HID collection
            }
        }

        return null;
    }

    private static bool MatchesDescriptor(HidDevice device, HidDeviceProfile profile)
    {
        int? targetUsagePage = profile.ParseUsagePage();
        bool checkInputReportId = profile.ReadPassiveInput && profile.ExpectedReportId >= 0;
        if (!targetUsagePage.HasValue && !checkInputReportId)
            return true;

        try
        {
            var desc = device.GetReportDescriptor();
            if (targetUsagePage.HasValue)
            {
                bool hasUsagePage = desc.DeviceItems.Any(di =>
                    di.Usages.GetAllValues().Any(u => (int)(u >> 16) == targetUsagePage.Value));
                if (!hasUsagePage)
                    return false;
            }

            if (checkInputReportId && !desc.InputReports.Any(r => r.ReportID == profile.ExpectedReportId))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ResolveCanonicalPid(HidDeviceProfile profile, int devicePid)
    {
        if (!profile.MergeProductIds || profile.ProductIdsHex.Count == 0)
            return devicePid;

        try
        {
            string clean = profile.ProductIdsHex[0].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            return Convert.ToInt32(clean, 16);
        }
        catch
        {
            return devicePid;
        }
    }

    private static string ResolveDisplayName(HidDeviceProfile profile, string? deviceProductName)
    {
        if (!string.IsNullOrWhiteSpace(deviceProductName) && profile.ProductIdsHex.Count == 0)
            return deviceProductName.Trim();

        return profile.Name;
    }

    private static DeviceBatteryInfo BuildInfo(
        HidDeviceProfile profile,
        int vid,
        int pid,
        (int Percent, bool IsCharging) parsed,
        string? deviceProductName,
        HashSet<int> wiredPids)
    {
        int canonicalPid = ResolveCanonicalPid(profile, pid);
        return new DeviceBatteryInfo
        {
            Id = $"profile-{vid:X4}-{canonicalPid:X4}",
            Name = ResolveDisplayName(profile, deviceProductName),
            Category = profile.Category,
            Connection = wiredPids.Contains(pid) ? ConnectionKind.UsbWired : ConnectionKind.Wireless24Ghz,
            BatteryPercent = parsed.Percent,
            IsCharging = parsed.IsCharging,
            IsConnected = true
        };
    }

    public static PassiveCandidateReading? SelectBestPassiveCandidate(
        HidDeviceProfile profile,
        IReadOnlyList<PassiveCandidateReading> candidates)
    {
        if (candidates.Count == 0)
            return null;

        var wiredPids = profile.ParseWiredProductIds();
        return candidates
            .OrderByDescending(c => c.IsOnline && c.IsFresh)
            .ThenByDescending(c => wiredPids.Contains(c.Pid))
            .ThenByDescending(c => c.IsFresh)
            .ThenByDescending(c => c.UpdatedUtc)
            .First();
    }

    public static ParsedProfileFrame? ParseFrameWithProfile(HidDeviceProfile profile, ReadOnlySpan<byte> data)
    {
        if (profile.ExpectedReportId >= 0 && (data.Length == 0 || data[0] != profile.ExpectedReportId))
            return null;

        if (profile.BatteryByteIndex < 0 || profile.BatteryByteIndex >= data.Length)
            return null;

        bool isOnline = true;
        if (profile.LinkStatusByteIndex.HasValue)
        {
            int linkIdx = profile.LinkStatusByteIndex.Value;
            if (linkIdx < 0 || linkIdx >= data.Length)
                return null;
            isOnline = data[linkIdx] == profile.LinkStatusOnlineValue;
        }

        int mask = profile.BatteryMask <= 0 ? 0xFF : profile.BatteryMask;
        int raw = data[profile.BatteryByteIndex] & mask;
        int max = Math.Max(1, profile.MaxBatteryValue);
        int percent = Math.Clamp((int)Math.Round(raw * 100.0 / max), 0, 100);

        if (raw == 0 &&
            profile.VoltageHighByteIndex.HasValue &&
            profile.VoltageLowByteIndex.HasValue &&
            profile.VoltageHighByteIndex.Value >= 0 &&
            profile.VoltageLowByteIndex.Value >= 0 &&
            profile.VoltageHighByteIndex.Value < data.Length &&
            profile.VoltageLowByteIndex.Value < data.Length)
        {
            int mv = (data[profile.VoltageHighByteIndex.Value] << 8) | data[profile.VoltageLowByteIndex.Value];
            if (mv is >= 3200 and <= 4350)
                percent = Math.Clamp((int)Math.Round((mv - 3300) * 100.0 / 900.0), 1, 100);
        }

        bool isCharging = false;
        if (profile.ChargingByteIndex.HasValue &&
            profile.ChargingByteIndex.Value >= 0 &&
            profile.ChargingByteIndex.Value < data.Length)
        {
            isCharging = profile.ChargingEqualsValue.HasValue
                ? data[profile.ChargingByteIndex.Value] == profile.ChargingEqualsValue.Value
                : (data[profile.ChargingByteIndex.Value] & profile.ChargingMask) != 0;
        }

        return new ParsedProfileFrame(percent, isCharging, isOnline);
    }

    public static (int Percent, bool IsCharging)? ParseWithProfile(HidDeviceProfile profile, ReadOnlySpan<byte> data)
    {
        var frame = ParseFrameWithProfile(profile, data);
        return frame is { IsOnline: true } f ? (f.Percent, f.IsCharging) : null;
    }

    private static string? TryGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var key in _passiveListeners.Keys.ToList())
        {
            if (_passiveListeners.TryRemove(key, out var session))
                session.Dispose();
        }
    }
}
