using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Voltra.Linux.Views;

public sealed partial class DashboardView : UserControl
{
    private readonly Meter[] meters = new Meter[5];

    public DashboardView() => InitializeComponent();

    internal void Setup(MainViewModel model, WriteableBitmap preview, Action openOled)
    {
        Preview.Setup("OLED preview", 192, preview);
        SizeChanged += (_, e) => DisplayPanel.MinHeight = Math.Clamp(e.NewSize.Width * .34, 200, 260);
        IdlePageMenu.ItemsSource = OledView.PageNames;
        IdlePageMenu.SelectedIndex = (int)model.Settings.Page;
        IdlePageMenu.SelectionChanged += (_, _) =>
        {
            if (IdlePageMenu.SelectedIndex >= 0 && (int)model.Settings.Page != IdlePageMenu.SelectedIndex)
                model.Change(model.Settings with { Page = (DisplayPage)IdlePageMenu.SelectedIndex });
        };
        OpenOled.Click += (_, _) => openOled();
        EditMapping.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window parent) return;
            var dialog = new MappingWindow(model.Settings.Channels, model.Audio.Snapshot.Applications, model.Settings.FormatAppNames);
            if (await dialog.ShowDialog<Channel[]?>(parent) is { } channels)
                model.Change(model.Settings with { Channels = channels });
        };
        for (int i = 0; i < meters.Length; i++)
        {
            var meter = meters[i] = new Meter(i);
            Grid.SetColumn(meter, i);
            Gauges.Children.Add(meter);
        }
    }

    internal void Refresh(Settings config, DeviceSnapshot device, AudioSnapshot audio, int activeChannel)
    {
        if (IdlePageMenu.SelectedIndex != (int)config.Page) IdlePageMenu.SelectedIndex = (int)config.Page;
        Preview.Refresh(device);
        for (int i = 0; i < meters.Length; i++)
            meters[i].Set(config.Channels[i].Label, audio.Volumes[i],
                audio.Waiting[i] ? device.Levels?[i] : null, activeChannel == i, config.Accent);
    }

    internal void InvalidatePreview() => Preview.InvalidatePixels();

    private sealed class Meter : Control
    {
        private readonly int index;
        private string label = "", accent = "";
        private int? volume, waiting;
        private bool active;
        private ThemeVariant? theme;

        internal Meter(int index) => this.index = index;

        internal void Set(string label, int? volume, int? waiting, bool active, string accent)
        {
            if (this.label == label && this.volume == volume && this.waiting == waiting &&
                this.active == active && this.accent == accent && theme == ActualThemeVariant) return;
            if (this.label != label || this.waiting != waiting)
                ToolTip.SetTip(this, waiting is int knob ? $"{label} · waiting for knob at {knob}%" : label);
            this.label = label;
            this.volume = volume;
            this.waiting = waiting;
            this.active = active;
            this.accent = accent;
            theme = ActualThemeVariant;
            AutomationProperties.SetName(this, $"Knob {index + 1}, {label}, {(volume is int reading ? $"{reading} percent" : "audio session unavailable")}");
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double radius = Math.Min(Bounds.Width * .33, Bounds.Height * .28);
            if (radius < 10) return;
            var center = new Point(Bounds.Width / 2, radius + 16);
            var app = Application.Current!;
            IBrush Brush(string key, IBrush fallback) =>
                app.TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
            Typeface Font(string key, FontWeight weight = FontWeight.Normal) =>
                app.TryGetResource(key, ActualThemeVariant, out var value) && value is FontFamily font
                    ? new Typeface(font, weight: weight) : Typeface.Default;
            var tone = Brush("VoltraAccent", Brushes.MediumPurple);
            var track = Brush("Line", Brushes.Gray);
            var muted = Brush("Muted", Brushes.Silver);
            var text = Brush("Text", Brushes.White);
            Point At(double part, double distance)
            {
                double angle = (135 + 270 * part) * Math.PI / 180;
                return new(center.X + Math.Cos(angle) * distance, center.Y + Math.Sin(angle) * distance);
            }

            Arc(track, 1, 5);
            var tick = new Pen(muted, 1.2);
            for (int i = 0; i <= 10; i++)
                context.DrawLine(tick, At(i / 10d, radius + 5), At(i / 10d, radius + (i % 5 == 0 ? 11 : 8)));
            if (waiting is int knob) context.DrawLine(new Pen(muted, 2.2), At(knob / 100d, radius + 4), At(knob / 100d, radius + 11));
            if (volume is int level)
            {
                double filled = Math.Clamp(level / 100d, 0, 1);
                if (filled > 0) Arc(tone, filled, active ? 6 : 5);
                context.DrawLine(new Pen(tone, 2.5), center, At(filled, radius - 9));
                context.DrawEllipse(tone, null, center, 3.5, 3.5);
            }

            double captionY = center.Y + radius + 10;
            Caption(label, Font("VoltraFont", FontWeight.SemiBold), 14, text, captionY);
            Caption(volume is int current ? $"{current}%" : "—", Font("MeterFont"), 17,
                volume.HasValue ? text : muted, captionY + 23);

            void Arc(IBrush brush, double value, double thickness)
            {
                var geometry = new StreamGeometry();
                using (var shape = geometry.Open())
                {
                    shape.BeginFigure(At(0, radius), false);
                    shape.ArcTo(At(value, radius), new Size(radius, radius), 0, value > 2d / 3d, SweepDirection.Clockwise);
                    shape.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(brush, thickness), geometry);
            }

            void Caption(string value, Typeface face, double size, IBrush brush, double top)
            {
                using var line = new Avalonia.Media.TextFormatting.TextLayout(value, face, size, brush,
                    TextAlignment.Center, TextWrapping.NoWrap, textTrimming: TextTrimming.CharacterEllipsis,
                    maxWidth: Math.Max(1, Bounds.Width - 2), maxLines: 1);
                line.Draw(context, new Point(1, top + (23 - line.Height) / 2));
            }
        }
    }
}
