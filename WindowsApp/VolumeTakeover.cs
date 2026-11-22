namespace Voltra;

internal enum KnobTakeover { Soft, Immediate }

internal sealed class VolumeTakeover
{
    internal int? Hardware { get; private set; }
    internal int? Actual { get; private set; }
    internal bool Waiting { get; private set; }
    private int anchor;

    internal void Reset() { Hardware = null; Actual = null; Waiting = false; }

    // Discard the input sampled for the previous binding. Only subsequent
    // movement can take ownership of a newly resolved session group.
    internal void Rebind(int? physical, int? volume, bool sync)
    {
        Hardware = physical;
        ObserveWindows(volume, sync);
    }

    internal void ObserveWindows(int? value, bool sync)
    {
        Actual = value;
        Waiting = sync && Hardware is int knob && value is int actual && knob != actual;
        anchor = Hardware ?? 0;
    }

    internal int? Move(int value, bool sync, KnobTakeover mode, int threshold, int pickupDistance = 2)
    {
        int? previous = Hardware;
        Hardware = value;
        if (previous is null)
        {
            if (sync && Actual is int actual)
            {
                Waiting = actual != value;
                anchor = value;
                return null;
            }
            return value;
        }
        if (previous == value) return null;
        if (!sync || !Waiting) return value;
        if (Math.Abs(value - anchor) < threshold) return null;
        if (mode == KnobTakeover.Soft && Actual is int windows &&
            Math.Abs(value - windows) > pickupDistance &&
            (Math.Min(anchor, value) > windows || Math.Max(anchor, value) < windows))
            return null;

        Waiting = false;
        return value;
    }

    internal void Written(int actual) { Actual = actual; Waiting = false; }

    internal void Disconnect()
    {
        Hardware = null;
        Waiting = false;
    }
}
