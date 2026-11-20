using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Voltra.Linux.Views;

public sealed partial class DashboardView : UserControl
{
    private readonly Meter[] meters = new Meter[5];
    private readonly TextBlock[] labels = new TextBlock[5];
    private readonly TextBlock[] values = new TextBlock[5];
    private readonly TextBox[] nameFields = new TextBox[5], targets = new TextBox[5];
    private readonly ComboBox[] choices = new ComboBox[5];
    private AudioApplication[] shown = [];

    public DashboardView() => InitializeComponent();

    internal void Setup(MainViewModel model, WriteableBitmap preview)
    {
        PreviewImage.Source = preview;
        for (int i = 0; i < 5; i++)
        {
            int channel = i;
            var panel = new StackPanel { Spacing = 3, Margin = new Thickness(4) };
            labels[i] = new TextBlock { TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            values[i] = new TextBlock { TextAlignment = TextAlignment.Center, FontSize = 12 };
            values[i].Classes.Add("muted");
            meters[i] = new Meter { Height = 91 };
            panel.Children.Add(labels[i]);
            panel.Children.Add(meters[i]);
            panel.Children.Add(values[i]);
            Grid.SetColumn(panel, i);
            Gauges.Children.Add(panel);

            var row = new Grid { ColumnDefinitions = new("*,1.25*,38"), ColumnSpacing = 6 };
            nameFields[i] = new TextBox { Text = model.Settings.Channels[i].Label, Watermark = $"Knob {i+1}" };
            targets[i] = new TextBox { Text = model.Settings.Channels[i].Target, Watermark = "Application ID" };
            Grid.SetColumn(targets[i], 1);
            choices[i] = new ComboBox { PlaceholderText = "", HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(choices[i], "Choose a running application or master output");
            Grid.SetColumn(choices[i], 2);
            choices[i].SelectionChanged += (_, _) =>
            {
                int index = choices[channel].SelectedIndex;
                if (index >= 0 && index < shown.Length)
                    targets[channel].Text = shown[index].Target;
            };
            row.Children.Add(nameFields[i]); row.Children.Add(targets[i]); row.Children.Add(choices[i]);
            MappingRows.Children.Add(row);
        }
        ApplyMappings.Click += (_, _) =>
        {
            var newChannels = Enumerable.Range(0, 5).Select(i =>
            {
                string name = (nameFields[i].Text ?? "").Trim();
                return new Channel(name.Length == 0 ? $"Knob {i + 1}" : name, (targets[i].Text ?? "").Trim());
            }).ToArray();
            model.Change(model.Settings with { Channels = newChannels });
            model.Report(null);
        };
        DataContext = model;
    }

    internal void Refresh(DeviceSnapshot device, AudioSnapshot audio)
    {
        if (DataContext is not MainViewModel model) return;
        DeviceStatus.Text = device.Status;
        DeviceDetail.Text = device.Port is null ? "" : device.Port + (device.Firmware is null ? "" : $"  ·  Firmware {device.Firmware}");
        AudioStatus.Text = audio.Status;
        OledStatus.Text = device.Display;
        if (!shown.AsSpan().SequenceEqual(audio.Applications))
        {
            shown = audio.Applications;
            foreach (var choice in choices)
            {
                choice.ItemsSource = shown.Select(option => option.Name).ToArray();
                choice.SelectedIndex = -1;
            }
        }
        for (int i = 0; i < 5; i++)
        {
            labels[i].Text = model.Settings.Channels[i].Label;
            meters[i].Set(audio.Volumes[i], device.Levels?[i], model.Settings.Accent);
            string level = audio.Volumes[i] is int value ? $"{value}%" : "No audio";
            values[i].Text = audio.Waiting[i] ? level + " · waiting" : level;
        }
    }

    internal void InvalidatePreview() => PreviewImage.InvalidateVisual();

    private sealed class Meter : Control
    {
        private int? volume, physical;
        private string? accent;
        private ThemeVariant? theme;
        internal void Set(int? volume, int? physical, string accent)
        {
            if (this.volume == volume && this.physical == physical && this.accent == accent && theme == ActualThemeVariant)
                return;
            this.accent = accent;
            theme = ActualThemeVariant;
            this.volume = volume;
            this.physical = physical;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double radius = Math.Min(Bounds.Width * .34, 36);
            if (radius < 10) return;
            var center = new Point(Bounds.Width / 2, 45);
            var app = Application.Current!;
            IBrush Brush(string key, IBrush fallback) =>
                app.TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
            var accent = Brush("VoltraAccent", Brushes.MediumPurple);
            var line = Brush("Line", Brushes.Gray);
            var muted = Brush("Muted", Brushes.Silver);
            Point At(double value, double distance)
            {
                double radians = (135 + 270 * value) * Math.PI / 180;
                return new(center.X + Math.Cos(radians) * distance, center.Y + Math.Sin(radians) * distance);
            }
            Arc(line, 1);
            if (volume is > 0) Arc(accent, volume.Value / 100d);
            if (physical is int knob)
            {
                context.DrawLine(new Pen(muted, 2), At(knob / 100d, radius + 1), At(knob / 100d, radius + 6));
            }

            void Arc(IBrush brush, double value)
            {
                var geometry = new StreamGeometry();
                using (var shape = geometry.Open())
                {
                    shape.BeginFigure(At(0, radius), false);
                    shape.ArcTo(At(value, radius), new Size(radius, radius), 0, value > 2d/3d, SweepDirection.Clockwise);
                    shape.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(brush, 4, lineCap: PenLineCap.Round), geometry);
            }
        }
    }
}
