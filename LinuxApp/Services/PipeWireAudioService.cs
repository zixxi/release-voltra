using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Voltra;

namespace Voltra.Linux;

internal sealed record AudioApplication(string Target, string Name, string[]? RuleIds = null);
internal sealed record AudioSnapshot(string Status, AudioApplication[] Applications, int?[] Volumes, bool[] Waiting, string?[]? Targets = null, bool FocusSupported = true)
{
    internal string Label(Channel channel, int i) => DynamicAudio.IsDynamic(channel.Target) ? Targets?[i] ?? channel.Label : channel.Label;
}
internal sealed record AudioTarget(string Id, string Target, string Name, string[] Aliases, int Level, AppIdentity? Identity = null, string Serial = "")
{
    internal bool Matches(string mapping) =>
        Target.Equals(mapping, StringComparison.OrdinalIgnoreCase) || Aliases.Contains(mapping, StringComparer.OrdinalIgnoreCase);
}

internal sealed class PipeWireAudioService : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker, subscription;
    private readonly Func<Settings> settings;
    private readonly Func<DeviceSnapshot> device;
    private AudioSnapshot snapshot = new("Connecting to PipeWire…", [], new int?[5], new bool[5]);
    private int topologyRevision;
    private int volumeRevision;
    private bool subscribed;
    internal AudioSnapshot Snapshot => Volatile.Read(ref snapshot);

    internal PipeWireAudioService(Func<Settings> settings, Func<DeviceSnapshot> device)
    {
        this.settings = settings;
        this.device = device;
        subscription = Task.Run(() => SubscribeAsync(stop.Token));
        worker = Task.Run(() => RunAsync(stop.Token));
    }

    private async Task SubscribeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var process = ProcessCommand.Start("pactl", "subscribe");
                try
                {
                    var errors = process.StandardError.ReadToEndAsync(token);
                    Volatile.Write(ref subscribed, true);
                    while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                    {
                        if (line.Contains("on sink-input", StringComparison.Ordinal) || line.Contains("on sink #", StringComparison.Ordinal)
                            || line.Contains("on server", StringComparison.Ordinal))
                        {
                            if (line.Contains("'change'", StringComparison.Ordinal) && !line.Contains("on server", StringComparison.Ordinal))
                                Interlocked.Increment(ref volumeRevision);
                            else Interlocked.Increment(ref topologyRevision);
                        }
                    }
                    string message = await errors;
                    if (message.Length != 0)
                        Console.Error.WriteLine(message.Trim());
                }
                finally
                {
                    Volatile.Write(ref subscribed, false);
                    ProcessCommand.Stop(process);
                }
            }
            catch (Exception ex) when (ex is IOException or Win32Exception)
            { Console.Error.WriteLine($"Audio change subscription: {ex.Message}"); }

            await Task.Delay(3500, token);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var foreground = new ForegroundWindow();
        var resolver = new DynamicAudio();
        Focus? previousFocus = null;
        AppIdentity? focusOwner = null;
        var knobs = Enumerable.Range(0, 5).Select(_ => new VolumeTakeover()).ToArray();
        AudioTarget[] targets = [];
        AudioApplication[] apps = [];
        var profileIds = new string?[5];
        var awaitingMovement = new bool[5];
        var assignments = new string?[5];
        var targetSets = new string?[5];
        var actual = new int?[5];
        int seenTopology = -1, seenVolume = -1;
        long nextRefresh = 0, lastRefresh = 0, localWrite = 0;
        bool connected = false;
        bool? synced = null;
        while (!token.IsCancellationRequested)
        {
            long now = Environment.TickCount64;
            try
            {
                if (now >= nextRefresh && (now - lastRefresh > (Volatile.Read(ref subscribed) ? 2500 : 850) ||
                    Volatile.Read(ref topologyRevision) != seenTopology ||
                    (now - localWrite > 240 && Volatile.Read(ref volumeRevision) != seenVolume)))
                {
                    if (!connected)
                    {
                        string server = await ProcessCommand.RunAsync("pactl", token, "info");
                        if (!server.Split('\n').Any(line => line.StartsWith("Server Name:", StringComparison.Ordinal) &&
                            line.Contains("PipeWire", StringComparison.OrdinalIgnoreCase)))
                            throw new IOException("The Pulse-compatible server is not running on PipeWire.");
                    }
                    seenTopology = Volatile.Read(ref topologyRevision);
                    seenVolume = Volatile.Read(ref volumeRevision);
                    targets = await ReadTargetsAsync(token);
                    apps = [new(Settings.Master, "Master output"), .. targets
                        .Where(x => x.Target != Settings.Master && x.Target.Length > 0)
                        .GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new AudioApplication(g.Key, g.First().Name, g.SelectMany(x => x.Identity?.Rules.Take(1) ?? []).Distinct().ToArray())).OrderBy(x => x.Name)];
                    connected = true;
                    lastRefresh = Environment.TickCount64;
                    nextRefresh = lastRefresh + 150;
                }

                if (connected)
                {
                    var config = settings();
                    bool syncChanged = synced != config.Sync;
                    synced = config.Sync;
                    var readings = device().Levels;
                    bool dynamic = config.Channels.Any(c => DynamicAudio.IsDynamic(c.Target));
                    var focus = dynamic ? await foreground.ReadAsync(false, token) : new Focus();
                    if (dynamic && (focus != previousFocus || lastRefresh >= now))
                        focusOwner = focus.Pid > 0 ? AppIdentity.Read(focus.Pid, focus.AppId, "", "") : null;
                    previousFocus = focus;
                    var selected = dynamic ? resolver.Resolve(targets, DynamicAudio.FocusKey(targets, focus, focusOwner), config.Rules) : default;
                    var names = new string?[5];
                    for (int i = 0; i < knobs.Length; i++)
                    {
                        var knob = knobs[i];
                        string binding = config.Channels[i].Target.Trim();
                        bool movingTarget = DynamicAudio.IsDynamic(binding);
                        var matching = binding switch
                        {
                            DynamicAudio.Game => selected.Game,
                            DynamicAudio.Focused => selected.Focused,
                            "" => [],
                            _ => targets.Where(x => x.Matches(binding)).ToArray()
                        };
                        if (movingTarget && matching.Length > 0)
                            matching = matching.Select(member => targets.First(t => t.Id == member.Id && t.Identity?.Key == member.Identity!.Key)).ToArray();
                        if (movingTarget && matching.Length > 0)
                            names[i] = config.FormatAppNames ? matching[0].Name : matching[0].Target;
                        var key = string.Join('|', matching.Select(x => movingTarget
                            ? $"{x.Id}:{x.Serial}:{x.Identity?.Key}:{x.Identity?.Pid}:{x.Identity?.Lifetime}" : x.Id).Order());
                        int? current = matching.Length == 0 ? null : (int)Math.Round(matching.Average(x => x.Level));
                        bool switched = profileIds[i] != null && profileIds[i] != config.Profiles.ActiveId;
                        profileIds[i] = config.Profiles.ActiveId;
                        if (switched) awaitingMovement[i] = true;
                        if (switched || assignments[i] != binding || targetSets[i] != key)
                        {
                            if (movingTarget || awaitingMovement[i]) knob.Rebind(readings?[i], current, config.Sync);
                            else knob.Reset();
                            assignments[i] = binding;
                            targetSets[i] = key;
                        }
                        if (knob.Actual != current || syncChanged) knob.ObserveWindows(current, config.Sync);
                        actual[i] = current;
                        if (readings is null)
                        {
                            knob.Disconnect();
                            continue;
                        }
                        if (current is null)
                        {
                            knob.Reset();
                            continue;
                        }
                        if (Volatile.Read(ref topologyRevision) != seenTopology) break;
                        if (awaitingMovement[i])
                        {
                            if (knob.Hardware is not int baseline)
                            {
                                knob.Rebind(readings[i], current, config.Sync);
                                continue;
                            }
                            if (baseline == readings[i]) continue;
                            awaitingMovement[i] = false;
                        }
                        if (knob.Move(readings[i], config.Sync, config.Takeover, config.NoiseThreshold) is not int level ||
                            matching.All(target => target.Level == level)) continue;

                        if (movingTarget)
                        {
                            // An external mixer change can precede its pactl subscription event.
                            // Verify membership and levels before writing anything to this group.
                            var live = await ReadStreamsAsync(token);
                            var group = live.Where(t => t.Identity?.Key == matching[0].Identity!.Key).OrderBy(t => t.Id, StringComparer.Ordinal);
                            if (!group.Select(t => (t.Id, t.Serial, t.Identity!.Pid, t.Identity.Lifetime, t.Level)).SequenceEqual(matching.Select(t => (t.Id, t.Serial, t.Identity!.Pid, t.Identity.Lifetime, t.Level))))
                            {
                                seenTopology = -1;
                                knob.ObserveWindows(current, config.Sync);
                                break;
                            }
                        }
                        bool complete = true;
                        foreach (var target in matching)
                        {
                            if (Volatile.Read(ref topologyRevision) != seenTopology || !ReferenceEquals(settings(), config) ||
                                (movingTarget && await foreground.ReadAsync(true, token) != focus))
                            {
                                complete = false;
                                break;
                            }
                            int desired = movingTarget && config.Sync && config.Takeover == KnobTakeover.Soft
                                ? Math.Clamp(target.Level + level - current.Value, 0, 100) : level;
                            if (target.Level == desired) continue;
                            var command = target.Target == Settings.Master ? "set-sink-volume" : "set-sink-input-volume";
                            await ProcessCommand.RunAsync("pactl", token, command, target.Id, desired.ToString(CultureInfo.InvariantCulture) + "%");
                            var index = Array.FindIndex(targets, t => t.Id == target.Id && t.Target == target.Target);
                            if (index >= 0) targets[index] = target with { Level = desired };
                        }
                        localWrite = Environment.TickCount64;
                        if (!complete) { seenTopology = -1; knob.ObserveWindows(current, config.Sync); break; }
                        actual[i] = (int)Math.Round(matching.Average(t => targets.First(x => x.Id == t.Id && x.Target == t.Target).Level));
                        knob.Written(actual[i]!.Value);
                    }
                    Publish("PipeWire connected", apps, actual.ToArray(), knobs.Select(k => k.Waiting).ToArray(), names, focus.Supported);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or TimeoutException or JsonException or InvalidOperationException)
            {
                connected = false;
                lastRefresh = 0;
                nextRefresh = Environment.TickCount64 + 1300;
                targets = [];
                apps = [];
                Array.Clear(targetSets);
                foreach (var knob in knobs) knob.Reset();
                Publish(ex is Win32Exception ? "pactl is missing. Install pulseaudio-utils." :
                    $"PipeWire unavailable: {ex.Message}", [], new int?[5], new bool[5]);
            }
            // Only a changed, verified knob value produces a write. This loop coalesces serial samples.
            await Task.Delay(75, token);
        }
    }

    private static async Task<AudioTarget[]> ReadTargetsAsync(CancellationToken token)
    {
        var defaultSink = ProcessCommand.RunAsync("pactl", token, "get-default-sink");
        var sinks = ProcessCommand.RunAsync("pactl", token, "--format=json", "list", "sinks");
        var streams = ProcessCommand.RunAsync("pactl", token, "--format=json", "list", "sink-inputs");
        await Task.WhenAll(defaultSink, sinks, streams);
        return ParseTargets((await defaultSink).Trim(), await sinks, await streams);
    }

    private static async Task<AudioTarget[]> ReadStreamsAsync(CancellationToken token) =>
        ParseTargets("", "[]", await ProcessCommand.RunAsync("pactl", token, "--format=json", "list", "sink-inputs"));

    internal static AudioTarget[] ParseTargets(string defaultSink, string sinks, string streams)
    {
        using var sinkDoc = JsonDocument.Parse(sinks);
        using var streamDoc = JsonDocument.Parse(streams);
        var result = new List<AudioTarget>();
        foreach (var sink in sinkDoc.RootElement.EnumerateArray())
            if (Get(sink, "name") == defaultSink && ReadLevel(sink) is int master)
                result.Add(new(defaultSink, Settings.Master, "Master output", [], master));

        foreach (var stream in streamDoc.RootElement.EnumerateArray())
        {
            if (!stream.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object ||
                !stream.TryGetProperty("index", out var index) || !index.TryGetUInt32(out uint id) ||
                ReadLevel(stream) is not int level) continue;
            string[] aliases = new[] { Get(props, "application.id"), Get(props, "application.process.binary"),
                    Get(props, "application.name") }
                .Where(x => x.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (aliases.Length == 0) continue;
            string name = Get(props, "application.name");
            bool hostPid = int.TryParse(Get(props, "pipewire.sec.pid"), out int pid) && pid > 0;
            if (!hostPid)
                int.TryParse(Get(props, "application.process.id"), out pid);
            var identity = AppIdentity.Read(pid, Get(props, "application.id"), Get(props, "application.process.binary"), Get(props, "media.role"), hostPid);
            result.Add(new(id.ToString(CultureInfo.InvariantCulture), aliases[0],
                name.Length == 0 ? aliases[0] : name, aliases, level, identity, Get(props, "object.serial")));
        }
        return result.ToArray();
    }

    private static string Get(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";

    private static int? ReadLevel(JsonElement item)
    {
        if (!item.TryGetProperty("volume", out var volume) || volume.ValueKind != JsonValueKind.Object) return null;
        var channels = new List<int>();
        foreach (var channel in volume.EnumerateObject())
        {
            if (channel.Value.ValueKind != JsonValueKind.Object) continue;
            if (channel.Value.TryGetProperty("value", out var raw) && raw.TryGetInt32(out int value))
                channels.Add((int)Math.Clamp(Math.Round(value * (100d / 65536)), 0, 100));
            else if (int.TryParse(Get(channel.Value, "value_percent").TrimEnd('%'), CultureInfo.InvariantCulture, out int percent))
                channels.Add(Math.Clamp(percent, 0, 100));
        }
        return channels.Count == 0 ? null : (int)Math.Round(channels.Average());
    }

    private void Publish(string status, AudioApplication[] apps, int?[] volumes, bool[] waiting, string?[]? names = null, bool supported = true) =>
        Volatile.Write(ref snapshot, new(status, apps, volumes, waiting, names, supported));

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await Task.WhenAll(worker, subscription); }
        catch (OperationCanceledException) { }
        finally { stop.Dispose(); }
    }
}
