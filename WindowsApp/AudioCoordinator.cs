using System.Diagnostics;

namespace Voltra;

internal sealed record AudioSnapshot(int?[] Levels, bool[] Waiting, string[] Apps, string?[] Names);

internal sealed class AudioCoordinator : IDisposable
{
    private sealed record Input(int[]? Values, long Received, int ConnectionEpoch);
    private readonly CancellationTokenSource stop = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Task worker;
    private MixerSettings settings;
    private Input input = new(null, 0, 0);
    private int connectionEpoch;
    private AudioSnapshot snapshot = new(new int?[5], new bool[5], [], new string?[5]);

    internal AudioCoordinator(MixerSettings settings)
    {
        this.settings = settings;
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal AudioSnapshot Snapshot => Volatile.Read(ref snapshot);

    internal void Configure(MixerSettings value)
    {
        Volatile.Write(ref settings, value);
        wake.Set();
    }

    internal void SetHardware(int[]? values)
    {
        if (values is null) connectionEpoch++;
        Volatile.Write(ref input, new(values, Stopwatch.GetTimestamp(), connectionEpoch));
        wake.Set();
    }

    private void Run()
    {
        using var audio = new WindowsAudio(() =>
        {
            if (stop.IsCancellationRequested) return;
            try { wake.Set(); }
            catch (ObjectDisposedException) { }
        });
        VolumeTakeover[] channels = Enumerable.Range(0, 5).Select(_ => new VolumeTakeover()).ToArray();
        var sources = new object?[5];
        string? profile = null;
        var awaitingMovement = new bool[5];
        Channel[]? mappings = null;
        bool? previousSync = null, previousFormatting = null;
        Input? consumed = null;
        string[] apps = [];
        long nextScan = 0, nextReport = Environment.TickCount64 + 10_000;
        int updates = 0;
        double totalMs = 0, maximumMs = 0;
        while (!stop.IsCancellationRequested)
        {
            long now = Environment.TickCount64;
            var config = Volatile.Read(ref settings);
            bool switched = profile is not null && profile != config.Profiles.ActiveId;
            profile = config.Profiles.ActiveId;
            if (switched) Array.Fill(awaitingMovement, true);
            bool remapped = !ReferenceEquals(mappings, config.Channels);
            bool syncChanged = previousSync != config.WindowsVolumeSync;
            if (remapped)
            {
                for (int channel = 0; channel < channels.Length; channel++)
                {
                    if (mappings is not null && mappings[channel].Processes == config.Channels[channel].Processes) continue;
                    channels[channel].Reset();
                    sources[channel] = null;
                }
                mappings = config.Channels;
            }
            previousSync = config.WindowsVolumeSync;

            bool scan = now >= nextScan || remapped;
            if (scan)
            {
                apps = audio.Refresh(config);
                nextScan = now + 950;
            }
            audio.Resolve(config, scan || config.FormatAppNames != previousFormatting);
            previousFormatting = config.FormatAppNames;
            var latest = Volatile.Read(ref input);
            if (consumed?.ConnectionEpoch != latest.ConnectionEpoch)
                foreach (var channel in channels) channel.Disconnect();
            for (int channel = 0; channel < channels.Length; channel++)
            {
                var (source, volume) = audio.Bindings.Current(channel);
                if (!switched && ReferenceEquals(sources[channel], source)) continue;
                sources[channel] = source;
                if (switched || config.Channels[channel].Dynamic || awaitingMovement[channel])
                    channels[channel].Rebind(latest.Values?[channel], volume, config.WindowsVolumeSync);
                else
                {
                    channels[channel].ObserveWindows(volume, config.WindowsVolumeSync);
                    if (!config.WindowsVolumeSync && channels[channel].Hardware is int position && source is not null)
                        Write(channel, position);
                }
            }
            audio.Drain(ExternalChange);

            if (syncChanged && latest.Values is not null)
            {
                for (int channel = 0; channel < channels.Length; channel++)
                {
                    if (config.WindowsVolumeSync)
                        channels[channel].ObserveWindows(channels[channel].Actual, true);
                    else if (!config.Channels[channel].Dynamic && channels[channel].Hardware is int position)
                        Write(channel, position);
                }
            }
            if (!ReferenceEquals(latest, consumed) || remapped)
            {
                consumed = latest;
                if (latest.Values is null)
                    foreach (var channel in channels) channel.Disconnect();
                else
                {
                    for (int index = 0; index < channels.Length; index++)
                    {
                        if (awaitingMovement[index])
                        {
                            if (sources[index] is null || channels[index].Hardware is not int baseline)
                            {
                                channels[index].Rebind(latest.Values[index], channels[index].Actual, config.WindowsVolumeSync);
                                continue;
                            }
                            if (baseline == latest.Values[index]) continue;
                            awaitingMovement[index] = false;
                        }
                        bool wasWaiting = channels[index].Waiting;
                        int? value = channels[index].Move(latest.Values[index], config.WindowsVolumeSync,
                            config.TakeoverMode, config.KnobNoiseThreshold,
                            config.Channels[index].Dynamic ? config.KnobNoiseThreshold : 2);
                        if (value is int position && (!config.Channels[index].Dynamic || sources[index] is not null)) Write(index, position);
                        if (wasWaiting != channels[index].Waiting)
                            MixerDiagnostics.Log($"Channel {index + 1}: {(channels[index].Waiting ? "waiting for knob" : "hardware control")}");
                    }
                    double ms = Stopwatch.GetElapsedTime(latest.Received).TotalMilliseconds;
                    updates++;
                    totalMs += ms;
                    maximumMs = Math.Max(maximumMs, ms);
                }
            }

            var published = Snapshot;
            int?[] actual = channels.Select(channel => channel.Actual).ToArray();
            bool[] waiting = channels.Select(channel => channel.Waiting).ToArray();
            if (!published.Levels.AsSpan().SequenceEqual(actual) || !published.Waiting.AsSpan().SequenceEqual(waiting) ||
                !published.Apps.AsSpan().SequenceEqual(apps) || !ReferenceEquals(published.Names, audio.Bindings.Names))
                Volatile.Write(ref snapshot, new(actual, waiting, apps, audio.Bindings.Names));

            if (now >= nextReport)
            {
                if (updates > 0)
                    MixerDiagnostics.Log($"Audio: {updates / 10d:F1} input updates/s, desktop processing mean {totalMs / updates:F1} ms, max {maximumMs:F1} ms.");
                nextReport = now + 10_000;
                updates = 0;
                totalMs = maximumMs = 0;
            }
            wake.WaitOne(100);

            void ExternalChange(int index, int value)
            {
                if (config.Channels[index].Dynamic)
                    channels[index].Rebind(latest.Values?[index], value, config.WindowsVolumeSync);
                else
                    channels[index].ObserveWindows(value, config.WindowsVolumeSync);
                if (config.WindowsVolumeSync)
                    MixerDiagnostics.Log($"Channel {index + 1}: external volume {value}%.");
                else if (!config.Channels[index].Dynamic && channels[index].Hardware is int position)
                    Write(index, position);
            }

            void Write(int index, int value)
            {
                if (awaitingMovement[index] || !ReferenceEquals(config.Channels, Volatile.Read(ref settings).Channels)) return;
                bool dynamicMapping = config.Channels[index].Dynamic;
                int? actual = audio.Bindings.Apply(index, value, dynamicMapping, config.WindowsVolumeSync,
                    config.TakeoverMode, audio.ForegroundUnchanged, (other, level) => channels[other].ObserveWindows(level, config.WindowsVolumeSync));
                if (actual.HasValue) channels[index].Written(actual.Value);
                else if (dynamicMapping) channels[index].ObserveWindows(channels[index].Actual, config.WindowsVolumeSync);
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        wake.Set();
        try { worker.Wait(); }
        catch (AggregateException ex) { MixerDiagnostics.Log(ex.GetBaseException().Message); }
        // The native callbacks have now been unsubscribed. A callback already in
        // flight still sees cancellation and skips signaling.
        wake.Dispose();
        stop.Dispose();
    }
}

internal static class MixerDiagnostics
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("VOLTRA_DIAGNOSTICS") == "1" ||
        Environment.GetEnvironmentVariable("PURPLE_MIXER_DIAGNOSTICS") == "1";
    private static readonly object Gate = new();

    internal static void Log(string message)
    {
        Trace.WriteLine(message);
        if (!Enabled) return;
        // Keep disk I/O off the serial and audio workers. Bound the diagnostic file.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(MixerSettings.Folder);
                    string path = Path.Combine(MixerSettings.Folder, "mixer-diagnostics.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                        File.Move(path, path + ".previous", true);
                    File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        });
    }
}
