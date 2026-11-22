using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Voltra.Linux.Views;

public sealed partial class OledPreview : UserControl
{
    public OledPreview() => InitializeComponent();

    internal void Setup(string title, double maximumHeight, WriteableBitmap preview)
    {
        TitleText.Text = title;
        Pixels.MaxHeight = maximumHeight;
        Pixels.Source = preview;
    }

    internal void Refresh(DeviceSnapshot device) => DisplayStatus.Text = device.Display == "OLED ready"
        ? "128 × 64 · Monochrome"
        : $"128 × 64 · {device.Display}";

    internal void InvalidatePixels() => Pixels.InvalidateVisual();
}

// Give each framebuffer pixel the same integer number of physical screen pixels,
// including on monitors using fractional desktop scaling.
public sealed class OledPixelImage : Image
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var frame = new Size(OledFrame.Width, OledFrame.Height);
        var factor = Math.Floor(Math.Min(availableSize.Width / frame.Width,
            availableSize.Height / frame.Height) * scale + 1e-7);
        return frame * (Math.Max(1, factor) / scale);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is TopLevel root) root.ScalingChanged += OnScalingChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (e.Root is TopLevel root) root.ScalingChanged -= OnScalingChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScalingChanged(object? sender, EventArgs e) => InvalidateMeasure();
}
