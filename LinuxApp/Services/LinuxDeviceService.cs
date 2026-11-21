using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using Voltra;

namespace Voltra.Linux;

internal enum ControllerConnection { Disconnected, Connecting, Connected, Error }

internal sealed record DeviceSnapshot(string Status, string Display, string[] Ports, int[]? Levels = null,
    string? Port = null, string? Firmware = null, ControllerConnection Connection = ControllerConnection.Disconnected,
    byte[]? Displayed = null);
internal sealed record DisplayFrame(byte[] Pixels, DisplayPage Page, int Flags, uint Media);

internal sealed class LinuxDeviceService : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly Func<Settings> settings;
    private readonly Func<string[]> findPorts;
    private DeviceSnapshot snapshot = new("Looking for Voltra…", "OLED waiting", []);
    private DisplayFrame? display;
    private string? lastPort;
    internal DeviceSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal void SetFrame(DisplayFrame value) => Volatile.Write(ref display, value);

    internal LinuxDeviceService(Func<Settings> settings, Func<string[]>? findPorts = null)
    {
        this.settings = settings;
        this.findPorts = findPorts ?? FindPorts;
        worker = Task.Run(() => RunAsync(stop.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string requested = settings().Port;
            var ports = findPorts();
            string[] candidates = requested.Length == 0
                ? ports.Where(IsEspressifUsb).OrderByDescending(port => port == lastPort).ToArray()
                : ports.Contains(requested) ? [requested] : [];
            Publish(requested.Length > 0 && candidates.Length == 0 ? $"Waiting for {requested}" :
                "Connect a Voltra controller or select its port in Settings", "OLED waiting", ports);
            foreach (string port in candidates)
            {
                if (requested != settings().Port || token.IsCancellationRequested) break;
                try { await ReadPortAsync(port, requested, ports, token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    TimeoutException or InvalidOperationException or ArgumentException)
                {
                    string message = ex is UnauthorizedAccessException
                        ? $"Cannot open {port}. Check serial group permissions and that the port is free."
                        : $"{port}: {ex.Message}";
                    Publish(message, "OLED waiting", ports, connection: ControllerConnection.Error);
                }
            }
            await Task.Delay(candidates.Length == 0 ? 900 : 700, token);
        }
    }

    private async Task ReadPortAsync(string port, string requested, string[] ports, CancellationToken token)
    {
        using var serial = new SerialPort(port, 115200) { WriteTimeout = 250, NewLine = "\n", Encoding = Encoding.ASCII };
        bool identified = false, ready = false, standalone = false;
        string? firmware = null;
        string status = "OLED waiting";
        try
        {
            Publish($"Checking {port}…", status, ports, connection: ControllerConnection.Connecting);
            serial.Open();
            try { serial.DtrEnable = true; }
            catch (IOException) { /* PTYs and some USB bridges don't expose modem signals. */ }
            serial.DiscardInBuffer();
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            var buffer = new StringBuilder(160);
            bool overflow = false;
            long opened = Environment.TickCount64, lastPacket = opened, nextHello = 0, nextPage = 0, nextFrame = 0,
                ackDeadline = 0, lastFrame = 0, nextScan = 0;
            int[]? levels = null;
            var parsed = new int[5];
            DisplayFrame? sending = null, sent = null;
            byte[]? displayed = null;
            Channel[]? configuredChannels = null;
            string[]? labels = null;
            string[] currentLabels = [];
            (DisplayPage Page, int Flags, uint Media)? pageState = null;
            int chunk = 0;
            while (!token.IsCancellationRequested && settings().Port == requested)
            {
                long now = Environment.TickCount64;
                if (now >= nextScan)
                {
                    ports = findPorts();
                    nextScan = now + 1500;
                }
                foreach (char ch in serial.ReadExisting())
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
                    else firmware ??= SerialProtocol.FirmwareVersion(line, nonce);
                }

                if (now >= nextHello)
                {
                    if (!identified) serial.WriteLine($"@H,{nonce}");
                    else
                    {
                        serial.WriteLine("@Q");
                        if (firmware is null) serial.WriteLine($"@I,{nonce}");
                    }
                    nextHello = now + (identified ? 2500 : 240);
                }
                if (!identified && now - opened > 2500) throw new IOException("Voltra identity was not confirmed");
                if (identified && now - lastPacket > 2600) throw new IOException("Voltra knob data stopped");
                if (identified && levels is not null)
                {
                    lastPort = port;
                    Publish("Controller connected", status, ports, levels, port, firmware, ControllerConnection.Connected, displayed);
                }
                var frame = Volatile.Read(ref display);
                if (ready && levels is not null && frame is not null)
                {
                    var channels = settings().Channels;
                    if (configuredChannels != channels)
                    {
                        configuredChannels = channels;
                        currentLabels = channels.Select(c => SerialProtocol.Ascii(c.Label, 18)).ToArray();
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
                { Console.Error.WriteLine($"Could not notify {port} of disconnection: {ex.Message}"); }
            }
        }
    }

    private void Publish(string status, string oled, string[] ports, int[]? levels = null, string? port = null, string? firmware = null,
        ControllerConnection connection = ControllerConnection.Disconnected, byte[]? displayed = null) =>
        Volatile.Write(ref snapshot, new(status, oled, ports, levels, port, firmware, connection, displayed));

    internal static string[] FindPorts()
    {
        try
        {
            var paths = new List<string>();
            var targets = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists("/dev/serial/by-id"))
                foreach (string path in Directory.EnumerateFiles("/dev/serial/by-id").Order())
                {
                    var target = File.ResolveLinkTarget(path, true)?.FullName;
                    if (target is not null && File.Exists(target) && targets.Add(target)) paths.Add(path);
                }
            foreach (string pattern in new[] { "ttyACM*", "ttyUSB*" })
                foreach (string path in Directory.EnumerateFiles("/dev", pattern).Order())
                    if (targets.Add(path)) paths.Add(path);
            return paths.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Serial discovery: {ex.Message}");
            return [];
        }
    }

    private static bool IsEspressifUsb(string path)
    {
        try
        {
            string tty = Path.GetFileName(File.ResolveLinkTarget(path, true)?.FullName ?? path);
            var device = Directory.ResolveLinkTarget($"/sys/class/tty/{tty}/device", true) as DirectoryInfo;
            for (var directory = device; directory is not null; directory = directory.Parent)
            {
                string vendor = Path.Combine(directory.FullName, "idVendor");
                string product = Path.Combine(directory.FullName, "idProduct");
                if (File.Exists(vendor) && File.Exists(product))
                    return File.ReadAllText(vendor).Trim() == "303a" && File.ReadAllText(product).Trim() == "1001";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"USB identity for {path}: {ex.Message}"); }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker; }
        catch (OperationCanceledException) { }
        finally { stop.Dispose(); }
    }
}
