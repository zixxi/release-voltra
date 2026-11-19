using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace Voltra;

internal enum ConnectionState { Disconnected, Connecting, Connected, Error }

internal sealed record MixerState(string Status, string DisplayStatus, int[] Levels, string[] Apps,
    int MovedChannel = -1, long LastMovement = 0, string? ConnectedPort = null,
    ConnectionState Connection = ConnectionState.Disconnected,
    int?[]? WindowsLevels = null, bool[]? WaitingForKnob = null);

internal sealed class DeviceLink : IDisposable
{
    private const int FrameChunkBytes = 256;
    private const int FrameChunkCount = 4;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private readonly AudioCoordinator audio;
    private readonly AutoResetEvent serialWake = new(false);
    private MixerSettings settings;
    private MixerState state = new("Disconnected", "OLED waiting", new int[5], []);
    private sealed record OledFrame(byte[] Pixels, OledPage Page, int Flags, uint Media);
    private OledFrame? frame;
    private string[] availablePorts = [];
    internal string[] AvailablePorts => Volatile.Read(ref availablePorts);
    private int rescan;

    internal DeviceLink(MixerSettings settings)
    {
        this.settings = settings;
        audio = new(settings);
        worker = Task.Run(() => Run(cancellation.Token));
    }

    internal MixerState State
    {
        get
        {
            var serial = Volatile.Read(ref state);
            var windows = audio.Snapshot;
            return serial with { Apps = windows.Apps, WindowsLevels = windows.Levels, WaitingForKnob = windows.Waiting };
        }
    }

    internal void Configure(MixerSettings value)
    {
        Volatile.Write(ref settings, value);
        audio.Configure(value);
        serialWake.Set();
    }
    internal void SetFrame(byte[] pixels, MixerSettings config, uint mediaId)
    {
        int flags = config.TwelveHourClock ? 1 : 0;
        var old = Volatile.Read(ref frame);
        if (old is not null && old.Pixels == pixels && old.Page == config.Page && old.Flags == flags &&
            old.Media == mediaId) return;
        Volatile.Write(ref frame, new(pixels, config.Page, flags, mediaId));
    }
    internal void Rescan()
    {
        Interlocked.Increment(ref rescan);
        serialWake.Set();
    }

    private void Run(CancellationToken stop)
    {
        SerialPort? serial = null;
        var buffer = new StringBuilder();
        int[] levels = new int[5];
        string status = "Disconnected", display = "OLED waiting";
        ConnectionState connection = ConnectionState.Disconnected;
        long nextConnect = 0, nextHello = 0, nextIdentity = 0, nextFrame = 0, lastFrame = 0, lastMovement = 0;
        long openedAt = 0, lastPacket = 0;
        int movedChannel = -1;
        bool hasLevels = false, ready = false, identityVerified = false, legacyQuerySent = false, lineOverflow = false;
        byte[]? sentFrame = null;
        byte[]? pendingFrame = null;
        int chunkIndex = 0;
        bool awaitingAck = false;
        long ackDeadline = 0;
        string[] sentLabels = [];
        MixerSettings? labelsFor = null;
        string desiredPort = "";
        string connectionMode = "";
        int requestedScan = -1, probeIndex = 0, validPackets = 0;
        bool connected = false;
        bool standaloneOled = false;
        long nextPortList = 0, nextDisplayHeartbeat = 0;
        (OledPage Page, int Flags, uint Media)? sentPage = null;
        var lastDevice = LoadLastPort(Volatile.Read(ref settings).Port);
        string nonce = "";
        var failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool priorityPort = false;
        SerialDataReceivedEventHandler onData = (_, _) =>
        {
            if (!stop.IsCancellationRequested)
            {
                try { serialWake.Set(); }
                catch (ObjectDisposedException) { }
            }
        };
        try
        {
            while (!stop.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                var config = Volatile.Read(ref settings);
                string manualPort = config.PreferredPort.Trim();
                bool automatic = config.AutoDetect && manualPort.Length == 0;
                string mode = automatic ? "auto" : manualPort;
                int scan = Volatile.Read(ref rescan);
                if (!string.Equals(mode, connectionMode, StringComparison.OrdinalIgnoreCase) || scan != requestedScan)
                {
                    connectionMode = mode;
                    requestedScan = scan;
                    desiredPort = "";
                    CloseSerial();
                    nextConnect = 0;
                    probeIndex = 0;
                    nextPortList = 0;
                    failures.Clear();
                    status = automatic ? "Looking for mixer…" : "Disconnected";
                    connection = automatic ? ConnectionState.Connecting : ConnectionState.Disconnected;
                }

                if (now >= nextPortList)
                {
                    long scanStarted = Stopwatch.GetTimestamp();
                    var ports = GetPorts();
                    if (!AvailablePorts.AsSpan().SequenceEqual(ports))
                    {
                        Volatile.Write(ref availablePorts, ports);
                        MixerDiagnostics.Log($"Port discovery: {ports.Length} ports in {Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds:F1} ms.");
                    }
                    nextPortList = now + 600;
                }

                if (serial is null && now >= nextConnect)
                {
                    string port = manualPort;
                    try
                    {
                        if (automatic)
                        {
                            var ports = AvailablePorts.OrderByDescending(p =>
                                p.Equals(lastDevice.Port, StringComparison.OrdinalIgnoreCase)).ToArray();
                            port = ports.Length > 0 ? ports[probeIndex++ % ports.Length] : "";
                        }
                        if (port.Length == 0)
                        {
                            status = automatic ? "Plug in the mixer" : "Select a port in Settings";
                            connection = ConnectionState.Disconnected;
                            nextConnect = now + 850;
                        }
                        else
                        {
                            desiredPort = port;
                            bool rememberedPort = port.Equals(lastDevice.Port, StringComparison.OrdinalIgnoreCase);
                            priorityPort = !automatic || rememberedPort;
                            bool preferredDtr = !rememberedPort || lastDevice.Dtr;
                            bool useDtr = failures.GetValueOrDefault(port) % 2 == 0 ? preferredDtr : !preferredDtr;
                            long openStarted = Stopwatch.GetTimestamp();
                            serial = new SerialPort(port, 115200)
                            {
                                DtrEnable = useDtr,
                                RtsEnable = false, ReadTimeout = 120, WriteTimeout = 120,
                                NewLine = "\n", Encoding = Encoding.ASCII
                            };
                            serial.DataReceived += onData;
                            serial.Open();
                            serial.DiscardInBuffer();
                            openedAt = Environment.TickCount64;
                            nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 4));
                            nextIdentity = openedAt;
                            MixerDiagnostics.Log($"{port} opened in {Stopwatch.GetElapsedTime(openStarted).TotalMilliseconds:F1} ms; DTR {(serial.DtrEnable ? "on" : "off")}.");
                            status = $"Checking {port}…";
                            connection = ConnectionState.Connecting;
                            nextHello = openedAt;
                        }
                    }
                    catch (Exception ex) when (IsPortError(ex))
                    {
                        CloseSerial();
                        failures[port] = failures.GetValueOrDefault(port) + 1;
                        MixerDiagnostics.Log($"{port} open failed: {ex.GetType().Name}: {ex.Message}");
                        status = PortFailure(port, ex);
                        connection = automatic ? ConnectionState.Connecting : ConnectionState.Error;
                        nextConnect = now + (automatic ? 350 : 1800);
                    }
                }

                if (serial is not null)
                {
                    try
                    {
                        foreach (char character in serial.ReadExisting())
                        {
                            if (character == '\n')
                            {
                                if (!lineOverflow) HandleLine(buffer.ToString().TrimEnd('\r'), now);
                                buffer.Clear();
                                lineOverflow = false;
                            }
                            else if (buffer.Length < 160)
                            {
                                buffer.Append(character);
                            }
                            else lineOverflow = true;
                        }

                        if (!identityVerified && now >= nextIdentity && (priorityPort || hasLevels))
                        {
                            serial.WriteLine("@H," + nonce);
                            nextIdentity = now + 180;
                            if (hasLevels && validPackets >= 2 && now - openedAt >= 300)
                            {
                                serial.WriteLine("@Q");
                                legacyQuerySent = true;
                            }
                        }
                        if (connected && now >= nextHello)
                        {
                            serial.WriteLine("@Q");
                            nextHello = now + (ready ? 4500 : 2300);
                        }
                        if (connected && now - lastPacket >= 2300)
                            throw new IOException("Knob data stopped. Reconnecting.");
                        int probeTimeout = priorityPort || hasLevels ? 2300 : failures.GetValueOrDefault(desiredPort) == 0 ? 650 : 1450;
                        if (!connected && now - openedAt >= probeTimeout)
                            throw new IOException("No mixer data received.");
                        if (connected && !ready && display == "OLED waiting" && now - openedAt >= 6500)
                            display = "OLED handshake missing · upload the included sketch";
                        if (ready && !ReferenceEquals(labelsFor, config))
                        {
                            var labels = config.Channels.Select(channel =>
                                OledRenderer.Ascii(channel.Label, 18)).ToArray();
                            if (!sentLabels.AsSpan().SequenceEqual(labels))
                            {
                                for (int i = 0; i < labels.Length; i++) serial.WriteLine($"@L{i + 1},{labels[i]}");
                                sentLabels = labels;
                            }
                            labelsFor = config;
                        }
                        var displayFrame = Volatile.Read(ref frame);
                        if (ready && standaloneOled && displayFrame is not null)
                        {
                            var selection = (displayFrame.Page, displayFrame.Flags, displayFrame.Media);
                            bool changedPage = sentPage != selection;
                            if ((!changedPage || pendingFrame is null) && (changedPage || now >= nextDisplayHeartbeat))
                            {
                                var local = DateTimeOffset.Now;
                                long epoch = local.ToUnixTimeSeconds() + (long)local.Offset.TotalSeconds;
                                serial.WriteLine(FormattableString.Invariant(
                                    $"@P,{(int)selection.Page},{selection.Flags},{epoch},{selection.Media}"));
                                sentPage = selection;
                                nextDisplayHeartbeat = now + 1500;
                                if (changedPage) sentFrame = null;
                            }
                        }
                        var pixels = displayFrame?.Pixels;
                        if (ready && pendingFrame is null && pixels is not null && now >= nextFrame &&
                            (!ReferenceEquals(pixels, sentFrame) || now - lastFrame >= 2500))
                        {
                            pendingFrame = pixels;
                            chunkIndex = 0;
                            nextFrame = now + 175;
                        }
                        if (awaitingAck && now >= ackDeadline)
                            PauseFrames(now);
                        if (ready && pendingFrame is not null && !awaitingAck)
                        {
                            try
                            {
                                string chunk = Convert.ToBase64String(
                                    pendingFrame, chunkIndex * FrameChunkBytes, FrameChunkBytes);
                                serial.WriteLine($"@C{chunkIndex},{chunk}");
                                awaitingAck = true;
                                ackDeadline = now + 850;
                            }
                            catch (TimeoutException)
                            {
                                PauseFrames(now);
                            }
                        }
                    }
                    catch (Exception ex) when (IsPortError(ex))
                    {
                        if (connected) probeIndex = 0;
                        CloseSerial();
                        failures[desiredPort] = failures.GetValueOrDefault(desiredPort) + 1;
                        MixerDiagnostics.Log($"{desiredPort} lost or failed identification: {ex.GetType().Name}: {ex.Message}");
                        status = PortFailure(desiredPort, ex);
                        connection = automatic && !priorityPort ? ConnectionState.Connecting : ConnectionState.Error;
                        nextConnect = now + (automatic ? 250 + Math.Min(failures[desiredPort], 8) * 90 : 600 + Math.Min(failures[desiredPort], 8) * 180);
                    }
                }

                Volatile.Write(ref state, new(status, display, levels, [], movedChannel, lastMovement,
                    connected ? desiredPort : null, connection));
                serialWake.WaitOne(serial is null ? 120 : 18);
            }
        }
        finally
        {
            try { if (standaloneOled && connected) serial?.WriteLine("@D"); }
            catch (Exception ex) when (IsPortError(ex)) { }
            CloseSerial();
        }

        void HandleLine(string line, long now)
        {
            if (line == $"PURPLEMIXER,1,{nonce}")
            {
                identityVerified = true;
                if (hasLevels) Establish();
                return;
            }
            if (legacyQuerySent && validPackets >= 2 && (line is "OLED,READY,128,64,3" or "OLED,READY,128,64,2" or "OLED,READY,128,64" or "OLED,ABSENT"))
            {
                identityVerified = true;
                Establish();
            }
            if (!identityVerified && !line.StartsWith("MIX,", StringComparison.Ordinal)) return;
            if (line is "OLED,READY,128,64,2" or "OLED,READY,128,64,3")
            {
                standaloneOled = line.EndsWith(",3", StringComparison.Ordinal);
                if (!ready)
                {
                    display = "OLED ready";
                    labelsFor = null;
                    sentLabels = [];
                }
                ready = true;
                return;
            }
            if (line == "OLED,READY,128,64")
            {
                display = "OLED firmware update required";
                return;
            }
            if (awaitingAck && line == $"OLED,ACK,{chunkIndex}")
            {
                awaitingAck = false;
                if (++chunkIndex == FrameChunkCount)
                {
                    sentFrame = pendingFrame;
                    pendingFrame = null;
                    lastFrame = now;
                    display = "OLED ready";
                }
                return;
            }
            if (line == "OLED,ABSENT")
            {
                ready = false;
                labelsFor = null;
                sentLabels = [];
                sentFrame = null;
                pendingFrame = null;
                awaitingAck = false;
                display = "OLED not detected";
                return;
            }
            if (!TryReadLevels(line, out var received))
                return;
            if (!connected) validPackets = Math.Min(2, validPackets + 1);
            lastPacket = now;
            bool changed = !hasLevels || !received.AsSpan().SequenceEqual(levels);
            if (changed)
            {
                if (connected && hasLevels)
                {
                    int strongest = 0;
                    for (int channel = 0; channel < 5; channel++)
                    {
                        int change = Math.Abs(levels[channel] - received[channel]);
                        if (change <= strongest) continue;
                        strongest = change;
                        movedChannel = channel;
                    }
                    lastMovement = now;
                }
                levels = received;
            }
            hasLevels = true;
            if (connected && changed) audio.SetHardware(levels);
            else if (identityVerified) Establish();
        }

        void Establish()
        {
            if (connected) return;
            connected = true;
            status = "Connected";
            connection = ConnectionState.Connected;
            failures.Remove(desiredPort);
            audio.SetHardware(levels);
            MixerDiagnostics.Log($"{desiredPort} verified in {Environment.TickCount64 - openedAt} ms" +
                (legacyQuerySent ? " (OLED identity fallback)." : " (challenge response)."));
            var verified = (Port: desiredPort, Dtr: serial!.DtrEnable);
            if (lastDevice != verified)
            {
                lastDevice = verified;
                ThreadPool.QueueUserWorkItem(_ => SaveLastPort(verified));
            }
        }

        void PauseFrames(long now)
        {
            pendingFrame = null;
            awaitingAck = false;
            sentFrame = null;
            nextFrame = now + 1000;
            display = "OLED transfer stalled · retrying";
        }

        void CloseSerial()
        {
            if (serial is not null) serial.DataReceived -= onData;
            try { serial?.Dispose(); }
            catch (Exception ex) when (IsPortError(ex)) { }
            serial = null;
            buffer.Clear();
            hasLevels = ready = identityVerified = legacyQuerySent = lineOverflow = false;
            if (connected) audio.SetHardware(null);
            connected = false;
            validPackets = 0;
            movedChannel = -1;
            lastMovement = 0;
            labelsFor = null;
            sentLabels = [];
            sentFrame = null;
            pendingFrame = null;
            awaitingAck = false;
            standaloneOled = false;
            sentPage = null;
            nextDisplayHeartbeat = 0;
            display = "OLED waiting";
        }
    }

    private static string LastPortPath => Path.Combine(MixerSettings.Folder, "last-port.txt");

    private static (string Port, bool Dtr) LoadLastPort(string fallback)
    {
        try
        {
            string[] stored = File.ReadAllText(LastPortPath).Trim().Split(',', 2);
            string saved = stored[0];
            if (saved.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(saved.AsSpan(3), out _))
                return (saved, stored.Length == 1 || stored[1] != "0");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (fallback, true);
    }

    private static void SaveLastPort((string Port, bool Dtr) device)
    {
        try
        {
            Directory.CreateDirectory(MixerSettings.Folder);
            File.WriteAllText(LastPortPath, $"{device.Port},{(device.Dtr ? 1 : 0)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string[] GetPorts()
    {
        try { return SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception ex) when (IsPortError(ex) || ex is System.Security.SecurityException) { return []; }
    }

    internal static bool TryReadLevels(string line, out int[] levels)
    {
        levels = [];
        if (!line.StartsWith("MIX,", StringComparison.Ordinal)) return false;
        string[] fields = line.Split(',');
        if (fields.Length != 6) return false;
        var parsed = new int[5];
        for (int index = 0; index < parsed.Length; index++)
            if (!int.TryParse(fields[index + 1], out parsed[index]) || parsed[index] is < 0 or > 100)
                return false;
        levels = parsed;
        return true;
    }

    private static string PortFailure(string port, Exception error) => error switch
    {
        UnauthorizedAccessException => $"{port} is busy or unavailable.",
        TimeoutException => $"{port} timed out. Retrying…",
        _ => $"{port} is not responding. Retrying…"
    };

    private static bool IsPortError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException;

    public void Dispose()
    {
        cancellation.Cancel();
        serialWake.Set();
        try { worker.Wait(); }
        catch (AggregateException ex) { Debug.WriteLine(ex); }
        audio.Dispose();
        serialWake.Dispose();
        cancellation.Dispose();
    }
}
