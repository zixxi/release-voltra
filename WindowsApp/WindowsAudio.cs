using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Voltra;

internal sealed class WindowsAudio(Action signal) : IDisposable
{
    private readonly Guid context = Guid.NewGuid();
    private readonly Dictionary<(string Device, string Session), Target> targets = [];
    private readonly ConcurrentQueue<Target> changes = new();
    private MMDeviceEnumerator? enumerator;
    private IDeviceEnumerator? nativeEnumerator;
    private MMDevice? masterDevice;
    private Target? master;
    private const string Master = "@endpoint";

    internal string[] Refresh(Channel[] channels)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string, string)>();
        var bindings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < channels.Length; i++)
        {
            if (channels[i].Processes == Channel.SystemVolume) continue;
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
        catch (Exception ex) when (Unavailable(ex)) { }

        foreach (var key in targets.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            targets[key].Dispose();
            targets.Remove(key);
        }
        return [.. names];
    }

    private void ScanSessions(string deviceId, Dictionary<string, int> bindings, SortedSet<string> names,
        HashSet<(string, string)> seen)
    {
        Check(nativeEnumerator!.GetDevice(deviceId, out var device));
        Check(device.Activate(typeof(ISessionManager).GUID, 23, IntPtr.Zero, out var service));
        Check(((ISessionManager)service).GetSessionEnumerator(out var sessions));
        Check(sessions.GetCount(out int count));
        for (int i = 0; i < count; i++)
        {
            try
            {
                Check(sessions.GetSession(i, out var control));
                if (control is not IAudioSessionControl2 session) continue;
                Check(session.GetState(out var state));
                if (state == AudioSessionState.AudioSessionStateExpired) continue;
                Check(session.GetProcessId(out uint pid));
                if (pid == 0) continue;
                using var process = Process.GetProcessById((int)pid);
                string name = process.ProcessName + ".exe";
                names.Add(name);
                if (!bindings.TryGetValue(name, out int channel)) continue;
                Check(session.GetSessionInstanceIdentifier(out string instance));
                var key = (deviceId, instance);
                if (targets.TryGetValue(key, out var existing) && existing.Channel != channel)
                {
                    existing.Dispose();
                    targets.Remove(key);
                }
                if (!targets.ContainsKey(key))
                {
                    var volume = (ISessionVolume)control;
                    Check(volume.Get(out float initial));
                    var target = new Target(key, channel, Percent(initial),
                        () => { Check(volume.Get(out float value)); return value; },
                        value => Check(volume.Set(value, context)));
                    var subscription = new SessionEvents(context, () => Enqueue(target));
                    Check(control.RegisterAudioSessionNotification(subscription));
                    target.Unsubscribe = () => control.UnregisterAudioSessionNotification(subscription);
                    targets.Add(key, target);
                    // Read again after subscribing so the first snapshot covers setup races.
                    target.Level = Percent(target.Read());
                }
                seen.Add(key);
            }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
    }

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
            var target = new Target(key, channel, Percent(volume.MasterVolumeLevelScalar),
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

    private void Enqueue(Target target)
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
                external(current.Channel, actual);
            }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
    }

    internal (object? Source, int? Volume) Current(int channel)
    {
        var target = targets.Values.FirstOrDefault(target => target.Channel == channel);
        return (target, target?.Level);
    }

    internal int? Apply(int channel, int value)
    {
        int? actual = null;
        foreach (var target in targets.Values)
        {
            if (target.Channel != channel) continue;
            try
            {
                if (target.Level != value)
                {
                    target.Write(value / 100f);
                    target.Level = Percent(target.Read());
                }
                actual ??= target.Level;
            }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
        return actual;
    }

    public void Dispose()
    {
        ReleaseMaster();
        foreach (var target in targets.Values) target.Dispose();
        targets.Clear();
        enumerator?.Dispose();
    }

    private static int Percent(float value) => Math.Clamp((int)Math.Round(value * 100), 0, 100);
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static bool Unavailable(Exception ex) => ex is COMException or ArgumentException or
        InvalidOperationException or InvalidCastException or System.ComponentModel.Win32Exception;

    private sealed class Target((string Device, string Session) key, int channel, int level, Func<float> read,
        Action<float> write) : IDisposable
    {
        internal (string Device, string Session) Key { get; } = key;
        internal int Channel { get; } = channel;
        internal int Level = level;
        internal Func<float> Read { get; } = read;
        internal Action<float> Write { get; } = write;
        internal Action? Unsubscribe;
        public void Dispose()
        {
            try { Unsubscribe?.Invoke(); }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
    }

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
