using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Voltra;

internal readonly record struct OledText(string Top, string Center, string Bottom, int CenterSize);

internal static class OledFrame
{
    internal const int Width = 128, Height = 64, Size = Width * Height / 8;
    internal const int SmallSize = 10, MediumSize = 19, LargeSize = 30;
    internal const int TopY = 2, CenterY = 14, BottomY = 51;
    private static readonly byte[] Dither = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    internal static OledText Clock(DateTime now, bool twelveHour) => Text(
        now.ToString("dddd"), now.ToString(twelveHour ? "h:mm tt" : "HH:mm"), now.ToString("dd MMM yyyy"), twelveHour);

    internal static OledText Weather(string city, WeatherReading? weather) => Text(
        weather?.City ?? (city.Length == 0 ? "Weather" : city),
        weather is null ? "-- °C" : $"{weather.Temperature:0} °C",
        weather?.Condition ?? (city.Length == 0 ? "Enter a city" : "Waiting for weather"));

    internal static OledText NoMedia() => Text("Image / GIF", "", "Choose a file on PC");

    internal static OledText Uptime(DateTime now, long milliseconds)
    {
        var uptime = TimeSpan.FromMilliseconds(milliseconds);
        return Text("PC uptime", $"{(long)uptime.TotalHours}h {uptime.Minutes:00}m", now.ToString("ddd, dd MMM"));
    }

    private static OledText Text(string top, string center, string bottom, bool compact = false) =>
        new(top, center, bottom, compact || center.Length > 8 ? MediumSize : LargeSize);

    internal static int AutomaticGaps(ReadOnlySpan<bool> master)
    {
        int gaps = 0;
        for (int channel = 0; channel < master.Length; channel++)
        {
            if (!master[channel]) continue;
            if (channel > 0) gaps |= 1 << (channel - 1);
            if (channel < master.Length - 1) gaps |= 1 << channel;
        }
        return gaps;
    }

    internal static byte[] Mixer(ReadOnlySpan<string> labels, ReadOnlySpan<int?> values, int gaps)
    {
        var frame = new byte[Size];
        for (int channel = 0; channel < 5; channel++)
        {
            int y = channel * 11 + 1;
            string label = Ascii(labels[channel], 18);
            if (OledMixerFont.Measure(label) > 43)
            {
                do { label = label[..^1]; } while (OledMixerFont.Measure(label + ".") > 43);
                label += ".";
            }
            OledMixerFont.Draw(frame, label, 1, y + 1);
            int fill = (values[channel].GetValueOrDefault() * 56 + 50) / 100;
            for (int x = 0; x < 56; x++)
            {
                OledMixerFont.Pixel(frame, 46 + x, y + 4);
                if (x >= fill) continue;
                OledMixerFont.Pixel(frame, 46 + x, y + 3);
                OledMixerFont.Pixel(frame, 46 + x, y + 5);
            }
            string number = values[channel] is int value ? $"{value}%" : "--";
            OledMixerFont.Draw(frame, number, 127 - OledMixerFont.Measure(number), y + 1);
            if ((gaps & (1 << channel)) != 0)
                for (int x = 1; x < 127; x++) OledMixerFont.Pixel(frame, x, y + 9);
        }
        return frame;
    }

    internal static (int X, int Y, int Width, int Height) MediaBounds(int sourceWidth, int sourceHeight)
    {
        double scale = Math.Min((double)Width / sourceWidth, (double)Height / sourceHeight);
        int width = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, Width);
        int height = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, Height);
        return ((Width - width) / 2, (Height - height) / 2, width, height);
    }

    internal static uint MediaId(ReadOnlySpan<byte> content) => MediaIdFromHash(SHA256.HashData(content));

    internal static uint MediaId(Stream content) => MediaIdFromHash(SHA256.HashData(content));

    private static uint MediaIdFromHash(ReadOnlySpan<byte> hash) =>
        Math.Max(1u, BinaryPrimitives.ReadUInt32LittleEndian(hash));

    internal static byte[] PackBgra(ReadOnlySpan<byte> bgra, int stride, bool dither)
    {
        var frame = new byte[Size];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = y * stride + x * 4;
                int luminance = (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8;
                int threshold = dither ? Dither[(y & 3) * 4 + (x & 3)] * 16 + 8 : 127;
                if (luminance >= threshold) OledMixerFont.Pixel(frame, x, y);
            }
        return frame;
    }

    internal static string Ascii(string text, int length)
    {
        var ascii = new StringBuilder(length);
        foreach (char character in text.Replace('ł', 'l').Replace('Ł', 'L').Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            ascii.Append(character is >= ' ' and <= '~' && character != '@' ? character : '?');
            if (ascii.Length == length) break;
        }
        return ascii.ToString();
    }
}
