using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Voltra;

namespace Voltra.Linux.Views;

public sealed class AccentPicker : Control
{
    private AccentColor color = AccentColor.FromRgb(System.Drawing.Color.FromArgb(183,139,250));
    private bool hueDrag;
    private IPointer? captured;
    internal event EventHandler? Changed;
    internal string Hex => color.ToHex();
    private double PlaneHeight => Math.Max(1, Bounds.Height - 33);
    private double drawnHue = -1;
    private LinearGradientBrush? planeGradient;
    private static readonly LinearGradientBrush Shade = Gradient(false, Colors.Transparent, Colors.Black);
    private static readonly LinearGradientBrush Rainbow = Gradient(true, Colors.Red, Colors.Yellow, Colors.Lime,
        Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red);
    private static readonly Pen OuterMarker = new(Brushes.Black, 3);
    private static readonly Pen InnerMarker = new(Brushes.White, 1.5);

    internal void SetHex(string text)
    {
        if (!LinuxSettingsService.TryAccent(text.Trim(), out var rgb)) return;
        color = AccentColor.FromRgb(System.Drawing.Color.FromArgb(rgb.R, rgb.G, rgb.B), color.Hue);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0) return;
        if (planeGradient is null || drawnHue != color.Hue)
        {
            var hue = (color with { Saturation = 1, Brightness = 1 }).ToRgb();
            planeGradient = Gradient(true, Colors.White, Color.FromRgb(hue.R, hue.G, hue.B));
            drawnHue = color.Hue;
        }
        var plane = new Rect(0, 0, Bounds.Width, PlaneHeight);
        var track = new Rect(0, PlaneHeight + 7, Bounds.Width, 26);
        context.DrawRectangle(planeGradient, null, plane, 6, 6);
        context.DrawRectangle(Shade, null, plane, 6, 6);
        context.DrawRectangle(Rainbow, null, track, 6, 6);
        Marker(new Point(Math.Clamp(color.Saturation * Bounds.Width, 5, Math.Max(5, Bounds.Width - 5)),
            Math.Clamp((1 - color.Brightness) * PlaneHeight, 5, Math.Max(5, PlaneHeight - 5))));
        Marker(new Point(Math.Clamp(color.Hue * Bounds.Width, 5, Math.Max(5, Bounds.Width - 5)), track.Center.Y));

        void Marker(Point point)
        {
            context.DrawEllipse(null, OuterMarker, point, 5, 5);
            context.DrawEllipse(null, InnerMarker, point, 5, 5);
        }
    }

    private static LinearGradientBrush Gradient(bool horizontal, params Color[] colors)
    {
        var stops = new GradientStops();
        for (int i = 0; i < colors.Length; i++) stops.Add(new GradientStop(colors[i], i / (colors.Length - 1d)));
        return new()
        {
            StartPoint = RelativePoint.TopLeft,
            EndPoint = new RelativePoint(horizontal ? 1 : 0, horizontal ? 0 : 1, RelativeUnit.Relative),
            GradientStops = stops
        };
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (point.Position.Y > PlaneHeight && point.Position.Y < PlaneHeight + 7) return;
        captured = e.Pointer;
        hueDrag = point.Position.Y > PlaneHeight;
        captured.Capture(this);
        Change(point.Position);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (e.Pointer == captured) Change(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.Pointer != captured) return;
        Change(e.GetPosition(this));
        captured.Capture(null);
        captured = null;
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => captured = null;

    private void Change(Point point)
    {
        double x = Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0, 1);
        color = hueDrag ? color with { Hue = x } :
            color with { Saturation = x, Brightness = 1 - Math.Clamp(point.Y / PlaneHeight, 0, 1) };
        Changed?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
}
