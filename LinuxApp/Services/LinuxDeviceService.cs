using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using Voltra;

namespace Voltra.Linux;

internal enum ControllerConnection { Disconnected, Connecting, Connected, Error }

internal sealed record DeviceSnapshot(string Status, string Display, string[] Ports, int[]? Levels = null,
    string? Port = null, string? Firmware = null, ControllerConnection Connection = ControllerConnection.Disconnected,
    byte[]? Displayed = null, DeviceFirmware? Identity = null, string? UsbSocket = null);
internal sealed record DisplayFrame(byte[] Pixels, DisplayPage Page, int Flags, uint Media, string[]? Labels = null);

internal sealed class LinuxDeviceService : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly SemaphoreSlim portGate = new(1, 1);
    private int suspended, generation;
    private sealed record ReturnTarget(string Socket, string Mac, string? ManualPort, long Until);
    private ReturnTarget? returning;

    internal async Task<IDisposable> HoldForFirmwareAsync(DeviceSnapshot target, CancellationToken token)
    {
        var current = Snapshot;
        if (target.Identity is null || target.UsbSocket is null || current.Connection != ControllerConnection.Connected ||
            current.Identity != target.Identity || current.UsbSocket != target.UsbSocket || current.Port != target.Port)
            throw new IOException("The selected controller disconnected or changed.");
        if (Interlocked.CompareExchange(ref suspended, 1, 0) != 0)
            throw new InvalidOperationException("Another device operation is active.");
        try
        {
            Interlocked.Increment(ref generation);
            await portGate.WaitAsync(token);
        }
        catch { Volatile.Write(ref suspended, 0); throw; }
        Publish("USB reserved for firmware update", "OLED waiting", available);
        return new FirmwareLease(this, target.UsbSocket!, target.Identity!.Mac);
    }

    private sealed class FirmwareLease(LinuxDeviceService owner, string socket, string mac) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            Volatile.Write(ref owner.returning, new ReturnTarget(socket, mac, owner.settings().ManualPort, Environment.TickCount64 + 45000));
            Volatile.Write(ref owner.suspended, 0);
            owner.portGate.Release();
        }
    }
    private readonly Func<Settings> settings;
    private readonly Func<LinuxSerialPort[]> findPorts;
    private readonly Queue<string> diagnostics = new();
    private LinuxSerialPort[]? previousScan;
    private string[] available = [];
    private DeviceSnapshot snapshot = new("Looking for Voltra…", "OLED waiting", []);
    private DisplayFrame? display;
    private LinuxSerialPort? lastController;
    internal DeviceSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal void SetFrame(DisplayFrame value) => Volatile.Write(ref display, value);

    internal LinuxDeviceService(Func<Settings> settings, Func<LinuxSerialPort[]>? findPorts = null)
    {
        this.settings = settings;
        this.findPorts = findPorts ?? new LinuxSerialDiscovery().Scan;
        worker = Task.Run(() => RunAsync(stop.Token));
    }

    private sealed record Retry(long After, int Failures, string Message);
    private sealed class IdentityTimeout() : IOException("Voltra identity was not confirmed.");
    private sealed class KnobDataTimeout() : IOException("Voltra stopped sending knob data.");

    internal string Diagnostics
    {
        get { lock (diagnostics) return string.Join(Environment.NewLine, diagnostics); }
    }

    private void Log(string message)
    {
        string line = $"[{DateTimeOffset.Now:HH:mm:ss}] Serial: {message}";
        lock (diagnostics)
        {
            if (diagnostics.Count == 80) diagnostics.Dequeue();
            diagnostics.Enqueue(line);
        }
        Console.Error.WriteLine(line);
    }

    private LinuxSerialPort[] Scan()
    {
        var found = findPorts();
        if (previousScan is { } previous && previous.AsSpan().SequenceEqual(found)) return previous;
        previousScan = found;
        available = found.Select(port => port.Path).ToArray();
        Log(found.Length == 0 ? "No serial ports discovered." :
            "Discovered " + string.Join(", ", found.Select(port => port.Description + (port.IsCandidate ? " [candidate]" : ""))) + ".");
        return found;
    }

    private async Task RunAsync(CancellationToken token)
    {
        var retries = new Dictionary<LinuxSerialPort, Retry>();
        string? previousRequest = null;
        bool firstScan = true;
        int observedGeneration = 0;
        while (!token.IsCancellationRequested)
        {
            if (Volatile.Read(ref suspended) != 0) { await Task.Delay(100, token); continue; }
            int cycle = Volatile.Read(ref generation);
            if (observedGeneration != cycle) { retries.Clear(); observedGeneration = cycle; }
            string? requested = settings().ManualPort;
            if (firstScan || previousRequest != requested)
            {
                retries.Clear();
                Log(requested is null ? "Automatic detection selected." : "Manual detection selected.");
                previousRequest = requested;
                firstScan = false;
            }

            var ports = Scan();
            foreach (var removed in retries.Keys.Where(port => !ports.Contains(port)).ToArray())
                retries.Remove(removed);
            string? requestedNode = requested is { Length: > 0 } ? LinuxSerialDiscovery.LinkTarget(requested, final: false) : null;
            var recovery = Volatile.Read(ref returning);
            var candidates = recovery is not null && recovery.ManualPort == requested && Environment.TickCount64 < recovery.Until
                ? ports.Where(port => port.UsbSocket == recovery.Socket).ToArray()
                : requested is null
                ? ports.Where(port => port.IsCandidate).OrderByDescending(WasController).ToArray()
                : ports.Where(port => port.Path == requested || port.Node == requested || port.Node == requestedNode).Take(1).ToArray();

            string status = requested is null
                ? "Waiting for a Voltra ESP32-S3 controller."
                : requested.Length == 0 ? "Select a serial port." : "Waiting for the selected serial port.";
            bool error = false;
            foreach (var candidate in candidates)
            {
                if (requested != settings().ManualPort || token.IsCancellationRequested) break;
                retries.TryGetValue(candidate, out var retry);
                if (retry is not null && retry.After > Environment.TickCount64)
                {
                    status = retry.Message;
                    error = true;
                    continue;
                }

                bool reconnect = WasController(candidate);
                bool logAttempt = retry is null || retry.Failures <= 3;
                if (logAttempt)
                    Log($"{(reconnect || retry is not null ? "Retrying" : "Opening")} {candidate.Description}.");
                try
                {
                    await portGate.WaitAsync(token);
                    try
                    {
                        if (Volatile.Read(ref suspended) != 0 || cycle != Volatile.Read(ref generation)) break;
                        await ReadPortAsync(candidate, requested, token);
                    }
                    finally { portGate.Release(); }
                    Log($"Closed {candidate.Description}.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    TimeoutException or InvalidOperationException or ArgumentException)
                {
                    bool connected = Snapshot.Levels is not null;
                    status = ErrorMessage(candidate, ex);
                    error = true;
                    int failures = connected ? 1 : (retry?.Failures ?? 0) + 1;
                    // A non-Voltra Espressif device stays untouched until it re-enumerates.
                    // Give an already connected controller another chance after a reset.
                    bool rejected = requested is null && ex is IdentityTimeout && !reconnect;
                    long delay = Math.Min(15_000, 1_000 << Math.Min(failures - 1, 4));
                    retries[candidate] = new(rejected ? long.MaxValue : Environment.TickCount64 + delay, failures, status);
                    if (logAttempt || connected || rejected)
                        Log(status + (rejected ? " Waiting for USB topology to change." : $" Retrying in {delay / 1000}s."));
                }
                if (requested == settings().ManualPort)
                    Publish(status, "OLED waiting", available, connection: error ? ControllerConnection.Error : ControllerConnection.Disconnected);
            }
            if (requested == settings().ManualPort)
                Publish(status, "OLED waiting", available, connection: error ? ControllerConnection.Error : ControllerConnection.Disconnected);
            await Task.Delay(requested == settings().ManualPort ? 1000 : 100, token);
        }
    }

    private bool WasController(LinuxSerialPort candidate) => lastController is { } last &&
        ((candidate.Path != candidate.Node && candidate.Path == last.Path) ||
         (candidate.Node == last.Node && candidate.HardwarePath == last.HardwarePath));

    private static string ErrorMessage(LinuxSerialPort candidate, Exception error)
    {
        string node = Path.GetFileName(candidate.Node);
        // .NET can report EBUSY as IOException and some access failures without a stable errno.
        return error switch
        {
            IdentityTimeout => $"{node}: Voltra identity was not confirmed.",
            UnauthorizedAccessException => $"{node}: access denied or port busy. Check serial permissions and close other serial apps.",
            FileNotFoundException or DirectoryNotFoundException => $"{node}: serial port disconnected.",
            IOException { HResult: 16 } => $"{node}: serial port busy. Close other serial apps.",
            IOException when error.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) =>
                $"{node}: serial port busy. Close other serial apps.",
            KnobDataTimeout => $"{node}: Voltra stopped sending knob data.",
            _ => $"{node}: serial connection unavailable ({error.GetType().Name})."
        };
    }

    private async Task ReadPortAsync(LinuxSerialPort candidate, string? requested, CancellationToken token)
    {
        string port = candidate.Path;
        string[] ports = available;
        using var serial = new SerialPort(port, 115200) { WriteTimeout = 250, NewLine = "\n", Encoding = Encoding.ASCII };
        bool identified = false, ready = false, standalone = false;
        DeviceFirmware? identity = null;
        string status = "OLED waiting";
        try
        {
            Publish($"Checking {Path.GetFileName(candidate.Node)}…", status, ports, connection: ControllerConnection.Connecting);
            serial.Open();
            try { serial.DtrEnable = true; }
            catch (IOException) { /* PTYs and some USB bridges don't expose modem signals. */ }
            serial.DiscardInBuffer();
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            var buffer = new StringBuilder(160);
            bool overflow = false;
            long opened = Environment.TickCount64, lastPacket = opened, nextHello = 0, nextPage = 0, nextFrame = 0,
                ackDeadline = 0, lastFrame = 0, nextScan = opened + 1500;
            int[]? levels = null;
            var parsed = new int[5];
            DisplayFrame? sending = null, sent = null;
            byte[]? displayed = null;
            string[]? configuredNames = null;
            string[]? labels = null;
            string[] currentLabels = [];
            (DisplayPage Page, int Flags, uint Media)? pageState = null;
            int chunk = 0;
            while (!token.IsCancellationRequested && settings().ManualPort == requested && Volatile.Read(ref suspended) == 0)
            {
                long now = Environment.TickCount64;
                if (now >= nextScan)
                {
                    if (!Scan().Any(item => item.Node == candidate.Node && item.HardwarePath == candidate.HardwarePath))
                        throw new FileNotFoundException("Serial port disconnected.");
                    ports = available;
                    nextScan = now + 1500;
                }
                string incoming;
                try { incoming = serial.ReadExisting(); }
                catch (OverflowException ex)
                {
                    // .NET can try to allocate a negative buffer when the USB tty vanishes.
                    throw new FileNotFoundException("Serial port disconnected.", ex);
                }
                foreach (char ch in incoming)
                {
                    if (ch != '\n')
                    {
                        if (buffer.Length < 160) buffer.Append(ch);
                        else overflow = true;
                        continue;
                    }
                    string line = overflow ? "" : buffer.ToString().TrimEnd('\r');
                    buffer.Clear();
                    overflow = false;
                    if (!identified)
                    {
                        if (!SerialProtocol.IsIdentity(line, nonce)) continue;
                        identified = true;
                        Log($"Verified Voltra identity on {candidate.Description}.");
                        nextHello = 0;
                        continue;
                    }
                    if (SerialProtocol.TryLevels(line, parsed))
                    {
                        lastPacket = now;
                        if (levels is null || !levels.AsSpan().SequenceEqual(parsed)) levels = parsed.ToArray();
                    }
                    else if (line is "OLED,READY,128,64,3" or "OLED,READY,128,64,2")
                    {
                        standalone = line.EndsWith(",3", StringComparison.Ordinal);
                        ready = true;
                        status = "OLED ready";
                    }
                    else if (line == "OLED,ABSENT")
                    {
                        ready = standalone = false;
                        labels = null;
                        sent = sending = null;
                        displayed = null;
                        pageState = null;
                        ackDeadline = 0;
                        status = "OLED not detected";
                    }
                    else if (sending is not null && ackDeadline != 0 && line == $"OLED,ACK,{chunk}")
                    {
                        ackDeadline = 0;
                        if (++chunk == 4)
                        {
                            sent = sending;
                            displayed = sending.Pixels;
                            sending = null;
                            lastFrame = now;
                            status = "OLED ready";
                        }
                    }
                    else identity ??= DeviceFirmware.Parse(line, nonce);
                }

                if (now >= nextHello)
                {
                    if (!identified) serial.WriteLine($"@H,{nonce}");
                    else
                    {
                        serial.WriteLine("@Q");
                        if (identity is null) serial.WriteLine($"@I,{nonce}");
                    }
                    nextHello = now + (identified ? 2500 : 240);
                }
                if (!identified && now - opened > 2500) throw new IdentityTimeout();
                if (identified && now - lastPacket > 2600) throw new KnobDataTimeout();
                if (identified && levels is not null)
                {
                    if (Snapshot.Levels is null)
                        Log($"Connected to Voltra on {candidate.Description}.");
                    if (Volatile.Read(ref returning) is { } recovery && identity is not null)
                    {
                        if (recovery.ManualPort == requested && Environment.TickCount64 < recovery.Until &&
                            (recovery.Socket != candidate.UsbSocket || recovery.Mac != identity.Mac))
                            throw new IOException("The returning controller identity changed.");
                        Interlocked.CompareExchange(ref returning, null, recovery);
                    }
                    lastController = candidate;
                    Publish("Controller connected", status, ports, levels, port, identity?.Version.ToString(), ControllerConnection.Connected, displayed, identity, candidate.UsbSocket);
                }
                var frame = Volatile.Read(ref display);
                if (ready && levels is not null && frame is not null)
                {
                    var names = frame.Labels ?? settings().Channels.Select(c => c.Label).ToArray();
                    if (configuredNames == null || !configuredNames.AsSpan().SequenceEqual(names))
                    {
                        configuredNames = names;
                        currentLabels = names.Select(name => SerialProtocol.Ascii(name, 18)).ToArray();
                    }
                    if (labels is null || !labels.AsSpan().SequenceEqual(currentLabels))
                    {
                        for (int i = 0; i < 5; i++) serial.WriteLine($"@L{i + 1},{currentLabels[i]}");
                        labels = currentLabels;
                    }
                    var selectedPage = (frame.Page, frame.Flags, frame.Media);
                    if (standalone && sending is null && (pageState != selectedPage || now >= nextPage))
                    {
                        serial.WriteLine(SerialProtocol.PageCommand((int)frame.Page, frame.Flags, DateTimeOffset.Now, frame.Media));
                        if (pageState != selectedPage) sent = null;
                        pageState = selectedPage;
                        nextPage = now + 1500;
                    }
                    if (ackDeadline != 0 && now >= ackDeadline)
                    {
                        sending = sent = null;
                        ackDeadline = 0;
                        nextFrame = now + 750;
                        status = "OLED transfer timed out; retrying";
                    }
                    if (sending is null && now >= nextFrame &&
                        (sent is null || !sent.Pixels.AsSpan().SequenceEqual(frame.Pixels) || now - lastFrame >= 2500))
                    {
                        sending = frame;
                        chunk = 0;
                        nextFrame = now + 145;
                    }
                    if (sending is not null && ackDeadline == 0)
                    {
                        serial.WriteLine(SerialProtocol.FrameChunk(sending.Pixels, chunk));
                        ackDeadline = now + 850;
                    }
                }
                await Task.Delay(12, token);
            }
        }
        finally
        {
            if (serial.IsOpen && identified && standalone)
            {
                try { serial.WriteLine("@D"); }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                { Log($"Could not notify {candidate.Description} of disconnection."); }
            }
        }
    }

    private void Publish(string status, string oled, string[] ports, int[]? levels = null, string? port = null, string? firmware = null,
        ControllerConnection connection = ControllerConnection.Disconnected, byte[]? displayed = null, DeviceFirmware? identity = null, string? socket = null) =>
        Volatile.Write(ref snapshot, new(status, oled, ports, levels, port, firmware, connection, displayed, identity, socket));

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker; }
        catch (OperationCanceledException) { }
        finally { stop.Dispose(); }
    }
}
