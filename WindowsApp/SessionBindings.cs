using System.Runtime.InteropServices;
using static Voltra.AudioTarget;

namespace Voltra;

internal sealed class SessionBindings
{
    private readonly AudioTarget[][] groups = Enumerable.Range(0, 5).Select(_ => Array.Empty<AudioTarget>()).ToArray();
    private readonly AudioTarget?[] meters = new AudioTarget?[5];
    private readonly GameSelection games = new();
    internal string?[] Names { get; private set; } = new string?[5];

    internal void Resolve(Channel[] channels, bool formatted, IEnumerable<AudioTarget> targets,
        AudioApplication? focus, IReadOnlySet<string> registered)
    {
        var sessions = targets.OrderBy(t => t.Key.Device, StringComparer.Ordinal)
            .ThenBy(t => t.Key.Session, StringComparer.Ordinal).ToArray();
        var game = games.Choose(sessions.Select(t => t.App).OfType<AudioApplication>(), focus, registered);
        var names = new string?[5];
        for (int i = 0; i < groups.Length; i++)
        {
            var mapping = channels[i];
            var app = mapping.Processes == Channel.Game ? game : focus;
            var group = mapping.Dynamic
                ? sessions.Where(t => app is not null && t.App is not null &&
                    StringComparer.OrdinalIgnoreCase.Equals(t.App.Key, app.Key)).ToArray()
                : sessions.Where(t => t.Channel == i).ToArray();
            if (mapping.Dynamic && group.Length != 0) names[i] = app!.Display(formatted);
            if (groups[i].AsSpan().SequenceEqual(group)) continue;
            // New session membership is a new pickup, even for the same executable.
            meters[i] = group.FirstOrDefault(t => !groups[i].Contains(t)) ?? group.FirstOrDefault();
            groups[i] = group;
        }
        if (!Names.AsSpan().SequenceEqual(names)) Names = names;
    }

    internal void Notify(AudioTarget target, int level, Action<int, int> external)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            if (!groups[i].Contains(target)) continue;
            meters[i] = target;
            external(i, level);
        }
    }

    internal (object? Source, int? Volume) Current(int channel) =>
        (groups[channel].Length == 0 ? null : groups[channel], meters[channel]?.Level);

    internal int? Apply(int channel, int value, bool dynamicMapping, bool synchronized,
        KnobTakeover mode, Func<bool> focusUnchanged, Action<int, int> external)
    {
        var group = groups[channel];
        if (dynamicMapping && synchronized)
        {
            // A mixer adjustment can arrive before its queued COM callback.
            // Check the entire group before performing the first write.
            foreach (var target in group)
            {
                try
                {
                    int live = Percent(target.Read());
                    if (live == target.Level) continue;
                    target.Level = live;
                    Notify(target, live, external);
                    return null;
                }
                catch (Exception ex) when (Unavailable(ex)) { return null; }
            }
        }
        var representative = meters[channel];
        if (representative is null) return null;
        // A soft pickup at 80% must not push a second stream at 20% to 80%.
        // Move the group by the representative's delta, clipping each at its own limits.
        bool relative = dynamicMapping && synchronized && mode == KnobTakeover.Soft;
        int delta = value - representative.Level;
        bool applied = false;
        int changedPeers = 0;
        foreach (var target in group)
        {
            if (dynamicMapping && !focusUnchanged()) { applied = false; break; }
            int desired = relative ? Math.Clamp(target.Level + delta, 0, 100) : value;
            try
            {
                if (target.Level != desired)
                {
                    target.Write(desired / 100f);
                    target.Level = Percent(target.Read());
                    for (int i = 0; i < groups.Length; i++)
                        if (i != channel && groups[i].Contains(target)) changedPeers |= 1 << i;
                }
                if (ReferenceEquals(target, representative)) applied = true;
            }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
        // Our own COM notifications are filtered. Re-arm overlapping knobs
        // once, against their own representative session.
        for (int i = 0; i < groups.Length; i++)
            if ((changedPeers & (1 << i)) != 0) external(i, meters[i]!.Level);
        return applied ? representative.Level : null;
    }
}

internal sealed class AudioTarget((string Device, string Session) key, int channel, int level, Func<float> read,
    Action<float> write) : IDisposable
{
    internal (string Device, string Session) Key { get; } = key;
    internal int Channel = channel;
    internal AudioApplication? App;
    internal int Level = level;
    internal Func<float> Read { get; } = read;
    internal Action<float> Write { get; } = write;
    internal static int Percent(float value) => float.IsFinite(value)
        ? (int)Math.Round(Math.Clamp(value, 0, 1) * 100)
        : throw new InvalidOperationException("Audio session returned a non-finite volume.");
    internal static bool Unavailable(Exception ex) => ex is COMException or ArgumentException or
        InvalidOperationException or InvalidCastException or System.ComponentModel.Win32Exception;

    internal Action? Unsubscribe { private get; set; }
    public void Dispose()
    {
        try { Unsubscribe?.Invoke(); }
        catch (Exception ex) when (Unavailable(ex)) { }
    }
}
