using static Voltra.AudioTarget;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Voltra;

internal sealed class WindowsAudio(Action signal) : IDisposable
{
    private readonly Guid context = Guid.NewGuid();
    private readonly Dictionary<(string Device, string Session), AudioTarget> targets = [];
    private readonly ConcurrentQueue<AudioTarget> changes = new();
    private MMDeviceEnumerator? enumerator;
    private IDeviceEnumerator? nativeEnumerator;
    private MMDevice? masterDevice;
    private AudioTarget? master;
    private const string Master = "@endpoint";

    internal SessionBindings Bindings { get; } = new();
    private readonly Dictionary<uint, AudioApplication?> owners = [];
    private HashSet<string> registered = new(StringComparer.OrdinalIgnoreCase);
    private string[]? registeredPaths;
    private uint foregroundPid;
    private nint foregroundWindow;
    private bool hasDynamicMappings;

    internal string[] Refresh(MixerSettings settings)
    {
        var channels = settings.Channels;
        hasDynamicMappings = channels.Any(channel => channel.Dynamic);
        if (!ReferenceEquals(registeredPaths, settings.RegisteredGames))
        {
            registeredPaths = settings.RegisteredGames;
            registered = new(registeredPaths, StringComparer.OrdinalIgnoreCase);
        }
        owners.Clear();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string, string)>();
        var bindings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < channels.Length; i++)
        {
            if (channels[i].Processes.StartsWith('@')) continue;
            foreach (string name in channels[i].Processes.Split([',', ';'],
                         StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                bindings.TryAdd(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe", i);
        }
        try
        {
            enumerator ??= new MMDeviceEnumerator();
            nativeEnumerator ??= (IDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try { ScanSessions(device.ID, bindings, names, seen); }
                    catch (Exception ex) when (Unavailable(ex)) { }
                }
            }
            ScanMaster(Array.FindIndex(channels, channel => channel.Processes == Channel.SystemVolume));
            if (master is not null) seen.Add(master.Key);
        }
        catch (Exception ex) when (Unavailable(ex)) { ReleaseMaster(); }

        foreach (var key in targets.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            targets[key].Dispose();
            targets.Remove(key);
        }
        return [.. names];
    }

    private AudioApplication? Owner(uint pid)
    {
        if (!owners.TryGetValue(pid, out var app))
            owners[pid] = app = WindowsApplicationIdentity.Read(pid);
        return app;
    }

    private void ScanSessions(string deviceId, Dictionary<string, int> bindings, SortedSet<string> names,
        HashSet<(string, string)> seen)
    {
        Check(nativeEnumerator!.GetDevice(deviceId, out var device));
        using var deviceRef = new ComReference(device);
        Check(device.Activate(typeof(ISessionManager).GUID, 23, IntPtr.Zero, out var service));
        using var serviceRef = new ComReference(service);
        Check(((ISessionManager)service).GetSessionEnumerator(out var sessions));
        using var sessionsRef = new ComReference(sessions);
        Check(sessions.GetCount(out int count));
        for (int i = 0; i < count; i++)
        {
            try
            {
                Check(sessions.GetSession(i, out var control));
                using var sessionRef = new ComReference(control);
                if (control is not IAudioSessionControl2 session) continue;
                Check(session.GetState(out var state));
                if (state == AudioSessionState.AudioSessionStateExpired) continue;
                int ownership = session.GetProcessId(out uint pid);
                Check(ownership);
                if (pid == 0) continue;
                using var process = Process.GetProcessById((int)pid);
                string name = process.ProcessName + ".exe";
                names.Add(name);
                int channel = bindings.GetValueOrDefault(name, -1);
                if (channel < 0 && !hasDynamicMappings) continue;
                Check(session.GetSessionInstanceIdentifier(out string instance));
                var key = (deviceId, instance);
                targets.TryGetValue(key, out var target);
                // A cross-process session's creator PID does not establish exclusive ownership.
                AudioApplication? app = null;
                if (hasDynamicMappings && ownership == 0 && session.IsSystemSoundsSession() == 1)
                    owners[pid] = app = target?.App ?? Owner(pid);
                if (channel < 0 && app is null) continue;
                if (target is null)
                {
                    target = Subscribe(key, channel, control);
                    sessionRef.Transfer(); // subscription now owns this COM reference
                    targets.Add(key, target);
                    target.Level = Percent(target.Read());
                }
                target.Channel = channel;
                target.App = app;
                seen.Add(key);
            }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
    }

    private AudioTarget Subscribe((string, string) key, int channel, IAudioSessionControl control)
    {
        var volume = (ISessionVolume)control;
        float Read() { Check(volume.Get(out float value)); return value; }
        var target = new AudioTarget(key, channel, Percent(Read()), Read, value => Check(volume.Set(value, context)));
        var subscription = new SessionEvents(context, () => Enqueue(target));
        Check(control.RegisterAudioSessionNotification(subscription));
        target.Unsubscribe = () =>
        {
            try { control.UnregisterAudioSessionNotification(subscription); }
            finally { Marshal.ReleaseComObject(control); }
        };
        return target;
    }

    private sealed class ComReference(object value) : IDisposable
    {
        private bool owns = true;
        internal void Transfer() => owns = false;
        public void Dispose() { if (owns) Marshal.ReleaseComObject(value); }
    }

    // Called on the audio worker. Between discovery passes only a foreground
    // change requires resolving the cached audio sessions again.
    internal void Resolve(MixerSettings settings, bool scan)
    {
        nint window = 0;
        string windowClass = "";
        uint pid = hasDynamicMappings ? WindowsApplicationIdentity.ForegroundPid(out window, out windowClass) : 0;
        if (!scan && window == foregroundWindow && pid == foregroundPid) return;
        foregroundWindow = window;
        foregroundPid = pid;
        var focus = pid != 0 ? Owner(pid) : null;
        // Java is a shared executable. Treat only this process instance as
        // Minecraft after seeing both the game's graphics window and title.
        if (focus is not null && WindowsApplicationIdentity.MinecraftWindow(window, windowClass, focus))
        {
            owners[pid] = focus = focus with { RecognizedGame = true, Name = "Minecraft" };
            foreach (var target in targets.Values)
                if (target.App?.Key == focus.Key) target.App = focus;
        }
        Bindings.Resolve(settings.Channels, settings.FormatAppNames, targets.Values, focus, registered);
    }

    internal bool ForegroundUnchanged() =>
        WindowsApplicationIdentity.ForegroundPid(out var window, out _) == foregroundPid && window == foregroundWindow;

    private void ScanMaster(int channel)
    {
        if (channel < 0) { ReleaseMaster(); return; }
        MMDevice? device = null;
        try
        {
            device = enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var key = (device.ID, Master);
            if (master is not null && master.Key == key && master.Channel == channel) return;
            ReleaseMaster();
            var volume = device.AudioEndpointVolume;
            volume.NotificationGuid = context;
            var target = new AudioTarget(key, channel, Percent(volume.MasterVolumeLevelScalar),
                () => volume.MasterVolumeLevelScalar, value => volume.MasterVolumeLevelScalar = value);
            AudioEndpointVolumeNotificationDelegate callback = change =>
            {
                if (change.EventContext != context) Enqueue(target);
            };
            volume.OnVolumeNotification += callback;
            target.Unsubscribe = () => volume.OnVolumeNotification -= callback;
            master = target;
            masterDevice = device;
            device = null;
            targets.Add(key, target);
        }
        catch (Exception ex) when (Unavailable(ex)) { ReleaseMaster(); }
        finally { device?.Dispose(); }
    }

    private void ReleaseMaster()
    {
        if (master is null) return;
        master.Dispose();
        targets.Remove(master.Key);
        master = null;
        try { masterDevice?.Dispose(); }
        catch (Exception ex) when (Unavailable(ex)) { }
        masterDevice = null;
    }

    private void Enqueue(AudioTarget target)
    {
        changes.Enqueue(target);
        signal();
    }

    internal void Drain(Action<int, int> external)
    {
        while (changes.TryDequeue(out var change))
        {
            if (!targets.TryGetValue(change.Key, out var current) ||
                !ReferenceEquals(current, change)) continue;
            try
            {
                // A newer hardware write or Windows adjustment can win while the callback is queued.
                int actual = Percent(current.Read());
                if (actual == current.Level) continue;
                current.Level = actual;
                Bindings.Notify(current, actual, external);
            }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
    }

    public void Dispose()
    {
        ReleaseMaster();
        foreach (var target in targets.Values) target.Dispose();
        targets.Clear();
        if (nativeEnumerator is not null) Marshal.ReleaseComObject(nativeEnumerator);
        enumerator?.Dispose();
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class SessionEvents(Guid context, Action changed) : IAudioSessionEvents
    {
        public int OnSimpleVolumeChanged(float volume, bool muted, ref Guid origin)
        {
            if (origin != context) changed();
            return 0;
        }
        public int OnDisplayNameChanged(string name, ref Guid origin) => 0;
        public int OnIconPathChanged(string path, ref Guid origin) => 0;
        public int OnChannelVolumeChanged(uint count, IntPtr volumes, uint index, ref Guid origin) => 0;
        public int OnGroupingParamChanged(ref Guid group, ref Guid origin) => 0;
        public int OnStateChanged(AudioSessionState state) => 0;
        public int OnSessionDisconnected(AudioSessionDisconnectReason reason) => 0;
    }

    // COM interfaces retain the native method order. The first two methods of each
    // prefix are required to reach the operation this mixer uses.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow flow, DeviceState state, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow flow, Role role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(in Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionManager
    {
        [PreserveSig] int GetAudioSessionControl(in Guid session, uint flags, out IAudioSessionControl control);
        [PreserveSig] int GetSimpleAudioVolume(in Guid session, uint flags, out ISessionVolume volume);
        [PreserveSig] int GetSessionEnumerator(out ISessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionVolume
    {
        [PreserveSig] int Set(float volume, in Guid context);
        [PreserveSig] int Get(out float volume);
    }
}
