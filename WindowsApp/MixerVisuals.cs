using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Voltra;

internal sealed record ThemePalette(bool Dark, Color Background, Color Sidebar, Color Text,
    Color Muted, Color Field, Color Line, Color Accent, Color Selected, Color Hover, Color Pressed,
    Color AccentForeground, Color Online, Color Pending, Color Error)
{
    internal static bool IsHex(string? text) =>
        text is { Length: 7 } && text[0] == '#' && text.AsSpan(1).ToArray().All(Uri.IsHexDigit);

    internal static ThemePalette From(MixerSettings config)
    {
        bool dark = config.Theme switch
        {
            AppTheme.Light => false,
            AppTheme.System => !Equals(Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0), 1),
            _ => true
        };
        Color background = dark ? Hex("#17181D") : Hex("#F8F9FB");
        Color field = dark ? Hex("#22242B") : Hex("#FFFFFF");
        Color accent = Hex(IsHex(config.Accent) ? config.Accent : MixerSettings.DefaultAccent);
        Color endpoint = dark ? Color.White : Color.Black;
        for (int step = 0; step < 20 && (Contrast(accent, field) < 4.5 || Contrast(accent, background) < 4.5); step++)
            accent = FieldChrome.Blend(accent, endpoint, 0.15f);
        return new(dark, background, dark ? Hex("#121318") : Hex("#EEF0F4"),
            dark ? Hex("#F0F1F4") : Hex("#20232B"), dark ? Hex("#ADB2C0") : Hex("#565D6A"),
            field, dark ? Hex("#3C414D") : Hex("#C6CAD3"), accent,
            FieldChrome.Blend(background, accent, dark ? 0.18f : 0.12f),
            FieldChrome.Blend(field, accent, 0.09f), FieldChrome.Blend(field, accent, 0.25f),
            Contrast(accent, Color.Black) > Contrast(accent, Color.White) ? Color.Black : Color.White,
            dark ? Hex("#83D6A5") : Hex("#1C7045"),
            dark ? Hex("#E4BE78") : Hex("#805B16"),
            dark ? Hex("#FF9AA8") : Hex("#A3213B"));
    }

    private static Color Hex(string text) => ColorTranslator.FromHtml(text);

    internal static double Contrast(Color first, Color second)
    {
        static double Linear(byte value)
        {
            double channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) =>
            Linear(color.R) * 0.2126 + Linear(color.G) * 0.7152 + Linear(color.B) * 0.0722;
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}

internal sealed class KnobReadout : Control
{
    private readonly ThemePalette palette;
    private readonly int channel;
    private readonly Font nameFont = new("Segoe UI Semibold", UiMetrics.Body);
    private readonly Font valueFont = new("Consolas", 13);
    private readonly UiMotion motion;
    private readonly ToolTip tooltip = new();
    private string label = "";
    private int? level, physical;
    private bool active;

    internal KnobReadout(ThemePalette palette, int channel)
    {
        this.palette = palette;
        this.channel = channel;
        motion = new(this, 110, Invalidate);
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    internal void Set(string name, int? value, bool moved, int? waitingPosition)
    {
        if (name == label && value == level && moved == active && physical == waitingPosition) return;
        if (name != label || physical != waitingPosition)
            tooltip.SetToolTip(this, waitingPosition.HasValue ? $"{name} · waiting for knob" : name);
        physical = waitingPosition;
        bool firstReading = level is null;
        label = name;
        level = value;
        motion.Move(value ?? 0, firstReading || value is null);
        active = moved;
        AccessibleName = $"Knob {channel + 1}, {label}, {(level is int n ? $"{n} percent" : "audio session unavailable")}";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(palette.Background);
        if (Width < 30 || Height < 68) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        float radius = Math.Min(Width * 0.33f, Height * 0.28f);
        var center = new PointF(Width / 2f, radius + 16 * scale);
        var arc = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        const float start = 135;
        const float sweep = 270;
        using var track = new Pen(palette.Line, 5 * scale);
        using var accent = new Pen(palette.Accent, (active ? 6 : 5) * scale);
        g.DrawArc(track, arc, start, sweep);

        PointF PointAt(float angle, float distance)
        {
            double rad = angle * Math.PI / 180;
            return new(center.X + (float)Math.Cos(rad) * distance, center.Y + (float)Math.Sin(rad) * distance);
        }
        using var tick = new Pen(palette.Muted, 1.2f * scale);
        for (int i = 0; i <= 10; i++)
        {
            float angle = start + sweep * i / 10;
            g.DrawLine(tick, PointAt(angle, radius + 5 * scale), PointAt(angle, radius + (i % 5 == 0 ? 11 : 8) * scale));
        }
        if (physical is int knob)
        {
            float angle = start + sweep * knob / 100;
            using var marker = new Pen(palette.Muted, 2.2f * scale);
            g.DrawLine(marker, PointAt(angle, radius + 4 * scale), PointAt(angle, radius + 11 * scale));
        }
        if (level.HasValue)
        {
            float filled = sweep * motion.Value / 100;
            if (filled > 0) g.DrawArc(accent, arc, start, filled);
            using var needle = new Pen(palette.Accent, 2.5f * scale);
            g.DrawLine(needle, center, PointAt(start + filled, radius - 9 * scale));
            using var hub = new SolidBrush(palette.Accent);
            g.FillEllipse(hub, center.X - 3.5f * scale, center.Y - 3.5f * scale, 7 * scale, 7 * scale);
        }

        const TextFormatFlags align = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        int captionY = (int)(center.Y + radius + 10 * scale);
        int lineHeight = (int)Math.Ceiling(23 * scale);
        TextRenderer.DrawText(g, label, nameFont, new Rectangle(1, captionY, Width - 2, lineHeight), palette.Text, align);
        TextRenderer.DrawText(g, level is int n ? $"{n}%" : "—", valueFont,
            new Rectangle(1, captionY + lineHeight, Width - 2, lineHeight), level.HasValue ? palette.Text : palette.Muted, align);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { motion.Dispose(); tooltip.Dispose(); nameFont.Dispose(); valueFont.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class FramePreview : Control
{
    private readonly ThemePalette palette;
    private readonly Bitmap bitmap = new(OledRenderer.Width, OledRenderer.Height, PixelFormat.Format32bppRgb);
    private readonly int[] rowPixels = new int[OledRenderer.Width];
    private byte[]? frame;
    private string? overlay;
    private int reading;

    internal FramePreview(ThemePalette palette)
    {
        this.palette = palette;
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
    }

    internal void ShowFrame(byte[] pixels, string? label, int value)
    {
        bool changed = !ReferenceEquals(frame, pixels);
        if (!changed && overlay == label && reading == value) return;
        frame = pixels;
        overlay = label;
        reading = value;
        if (changed)
        {
            int width = OledRenderer.Width, height = OledRenderer.Height;
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                        rowPixels[x] = (pixels[y * (width / 8) + x / 8] & (0x80 >> (x & 7))) != 0 ? 0xFFFFFF : 0;
                    Marshal.Copy(rowPixels, 0, IntPtr.Add(data.Scan0, y * data.Stride), width);
                }
            }
            finally { bitmap.UnlockBits(data); }
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(palette.Background);
        const int inset = 2;
        int height = Math.Min(Height - inset * 2, (Width - inset * 2) / 2);
        if (height < 12) return;
        // Both dimensions are derived from the 128×64 pixel viewport.
        var area = new Rectangle(inset, inset, height * 2, height);
        using var border = new Pen(palette.Line);
        e.Graphics.DrawRectangle(border, Rectangle.Inflate(area, 1, 1));
        e.Graphics.FillRectangle(Brushes.Black, area);
        if (overlay is null)
        {
            if (frame is null) return;
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(bitmap, area, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
            return;
        }

        using var label = new Font("Consolas", area.Height * 0.15f, GraphicsUnit.Pixel);
        using var amount = new Font("Consolas", area.Height * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
        var description = new Rectangle(area.X + area.Width / 32, area.Y, area.Width * 15 / 16, area.Height / 4);
        TextRenderer.DrawText(e.Graphics, overlay, label, description, Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var value = new Rectangle(area.X, area.Y + area.Height / 4, area.Width, area.Height / 2);
        TextRenderer.DrawText(e.Graphics, $"{reading}%", amount, value, Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        var bar = new Rectangle(description.X, area.Y + area.Height * 53 / 64, description.Width, area.Height * 9 / 64);
        e.Graphics.DrawRectangle(Pens.White, bar);
        e.Graphics.FillRectangle(Brushes.White, Rectangle.Inflate(
            new Rectangle(bar.X, bar.Y, Math.Max(2, bar.Width * reading / 100), bar.Height), -1, -1));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) bitmap.Dispose();
        base.Dispose(disposing);
    }
}
