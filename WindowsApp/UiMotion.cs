using System.Runtime.InteropServices;

namespace Voltra;

internal sealed class UiMotion(Control owner, int duration, Action redraw) : IDisposable
{
    private const uint GetClientAreaAnimation = 0x1042;
    private System.Windows.Forms.Timer? timer;
    private long start;
    private float origin, target;

    internal float Value { get; private set; }

    internal void Move(float value, bool immediate = false)
    {
        if (target == value && !immediate) return;
        target = value;
        if (immediate || !owner.IsHandleCreated || !owner.Visible || SystemInformation.HighContrast ||
            !SystemParametersInfo(GetClientAreaAnimation, 0, out bool enabled, 0) || !enabled)
        {
            timer?.Stop();
            Value = target;
            redraw();
            return;
        }
        origin = Value;
        start = Environment.TickCount64;
        if (timer is null)
        {
            timer = new() { Interval = 15 };
            timer.Tick += Advance;
        }
        timer.Start();
    }

    private void Advance(object? sender, EventArgs e)
    {
        float progress = Math.Clamp((Environment.TickCount64 - start) / (float)duration, 0, 1);
        float remaining = 1 - progress;
        Value = origin + (target - origin) * (1 - remaining * remaining * remaining);
        if (progress >= 1 || !owner.Visible)
        {
            Value = target;
            timer?.Stop();
        }
        redraw();
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    public void Dispose() => timer?.Dispose();
}

internal static class UiMetrics
{
    internal const int Radius = 6;
    internal const int FieldHeight = 34;
    internal const int SwitchWidth = 48;
    internal const int SwitchHeight = 25;
    internal const int SwitchThumb = 19;
    internal const float Body = 10.25f;
    internal const float Section = 14.25f;
    internal const float Page = 23f;
}
