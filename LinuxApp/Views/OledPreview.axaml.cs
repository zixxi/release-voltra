using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Voltra.Linux.Views;

public sealed partial class OledPreview : UserControl
{
    public OledPreview() => InitializeComponent();

    internal void Setup(string title, double maximumHeight, WriteableBitmap preview)
    {
        TitleText.Text = title;
        Viewport.MaxHeight = maximumHeight - 2;
        Pixels.Source = preview;
    }

    internal void Refresh(DeviceSnapshot device) => DisplayStatus.Text = device.Display == "OLED ready"
        ? "128 × 64 · Monochrome"
        : $"128 × 64 · {device.Display}";

    internal void InvalidatePixels() => Pixels.InvalidateVisual();
}
