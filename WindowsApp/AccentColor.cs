namespace Voltra;

internal readonly record struct AccentColor(double Hue, double Saturation, double Brightness)
{
    internal static AccentColor FromRgb(Color color, double previousHue = 0)
    {
        int maximum = Math.Max(color.R, Math.Max(color.G, color.B));
        int minimum = Math.Min(color.R, Math.Min(color.G, color.B));
        double saturation = maximum == 0 ? 0 : (maximum - minimum) / (double)maximum;
        return new(saturation == 0 ? previousHue : color.GetHue() / 360d, saturation, maximum / 255d);
    }

    internal Color ToRgb()
    {
        double sector = Hue * 6;
        double chroma = Brightness * Saturation;
        double middle = chroma * (1 - Math.Abs(sector % 2 - 1));
        double offset = Brightness - chroma;
        (double red, double green, double blue) = ((int)sector % 6) switch
        {
            0 => (chroma, middle, 0d),
            1 => (middle, chroma, 0d),
            2 => (0d, chroma, middle),
            3 => (0d, middle, chroma),
            4 => (middle, 0d, chroma),
            _ => (chroma, 0d, middle)
        };
        int Channel(double value) => (int)Math.Round((value + offset) * 255);
        return Color.FromArgb(Channel(red), Channel(green), Channel(blue));
    }

    internal string ToHex()
    {
        Color color = ToRgb();
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
