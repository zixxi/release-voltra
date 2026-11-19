using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace Voltra;

internal sealed class OledRenderer : IDisposable
{
    internal const int Width = 128, Height = 64;
    private readonly Bitmap bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
    private readonly byte[] argb = new byte[Width * Height * 4];
    private readonly Font small = new("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font large = new("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font medium = new("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
    private Image? media;
    private MemoryStream? mediaStream;
    private FrameDimension? gifDimension;
    private int[] frameEnds = [];
    private int selectedFrame = -1;
    private long mediaStart;
    private string? lastKey;
    private byte[]? lastFrame;
    private static readonly byte[] Dither = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    internal bool HasMedia => media is not null;
    internal uint MediaId { get; private set; }

    internal void LoadMedia(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 20 * 1024 * 1024)
            throw new IOException("Choose an image or GIF smaller than 20 MB.");

        byte[] content = File.ReadAllBytes(path);
        var stream = new MemoryStream(content, writable: false);
        Image? candidate = null;
        FrameDimension? dimension = null;
        int[] ends = [];
        try
        {
            candidate = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (candidate.Width > 4096 || candidate.Height > 4096)
                throw new IOException("Choose an image up to 4096 × 4096 pixels.");
            if (candidate.FrameDimensionsList.Contains(FrameDimension.Time.Guid))
            {
                int count = candidate.GetFrameCount(FrameDimension.Time);
                if (count > 1)
                {
                    dimension = FrameDimension.Time;
                    ends = new int[count];
                    byte[]? delays = candidate.PropertyIdList.Contains(0x5100)
                        ? candidate.GetPropertyItem(0x5100)?.Value : null;
                    int elapsed = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int value = delays is not null && delays.Length >= (i + 1) * 4
                            ? BitConverter.ToInt32(delays, i * 4) : 10;
                        elapsed += Math.Clamp(value, 2, 1000) * 10;
                        ends[i] = elapsed;
                    }
                }
            }
        }
        catch
        {
            candidate?.Dispose();
            stream.Dispose();
            throw;
        }

        media?.Dispose();
        mediaStream?.Dispose();
        media = candidate;
        mediaStream = stream;
        MediaId = Math.Max(1u, BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(content)));
        gifDimension = dimension;
        frameEnds = ends;
        mediaStart = Environment.TickCount64;
        selectedFrame = -1;
        lastKey = null;
    }

    internal byte[] Render(MixerSettings config, MixerState state, WeatherReading? weather)
    {
        DateTime now = DateTime.Now;
        string key;
        string top = "", center = "", bottom = "";
        bool drawMedia = config.Page == OledPage.Media && media is not null;
        int?[] values = config.Page == OledPage.Mixer ? state.WindowsLevels ?? new int?[5] : [];
        int separators = config.Page == OledPage.Mixer ? SeparatorGaps(config) : 0;
        switch (config.Page)
        {
            case OledPage.Mixer:
                key = "m:" + string.Join("|", config.Channels.Select(channel => channel.Label)) +
                    ":" + string.Join(",", values) + ":" + separators;
                break;
            case OledPage.Clock:
                top = now.ToString("dddd");
                center = now.ToString(config.TwelveHourClock ? "h:mm tt" : "HH:mm");
                bottom = now.ToString("dd MMM yyyy");
                key = "c:" + center + bottom;
                break;
            case OledPage.Weather:
                top = weather?.City ?? (config.City.Length == 0 ? "Weather" : config.City);
                center = weather is null ? "-- °C" : $"{weather.Temperature:0} °C";
                bottom = weather?.Condition ?? (config.City.Length == 0 ? "Enter a city" : "Waiting for weather");
                key = "w:" + top + center + bottom;
                break;
            case OledPage.Media:
                int frameIndex = 0;
                if (gifDimension is not null)
                {
                    int position = (int)((Environment.TickCount64 - mediaStart) % frameEnds[^1]);
                    frameIndex = Array.BinarySearch(frameEnds, position + 1);
                    if (frameIndex < 0) frameIndex = ~frameIndex;
                    if (selectedFrame != frameIndex)
                    {
                        media!.SelectActiveFrame(gifDimension, frameIndex);
                        selectedFrame = frameIndex;
                    }
                }
                key = "g:" + frameIndex;
                if (!drawMedia) { top = "Image / GIF"; bottom = "Choose a file on PC"; }
                break;
            default:
                var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                top = "PC uptime";
                center = $"{(long)uptime.TotalHours}h {uptime.Minutes:00}m";
                bottom = now.ToString("ddd, dd MMM");
                key = "u:" + center + bottom;
                break;
        }

        if (lastKey == key && lastFrame is not null) return lastFrame;
        if (config.Page == OledPage.Mixer)
        {
            lastFrame = DrawMixer(config, values, separators);
            lastKey = key;
            return lastFrame;
        }
        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.Clear(Color.Black);
            canvas.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            if (drawMedia)
            {
                canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                double scale = Math.Min((double)Width / media!.Width, (double)Height / media.Height);
                int width = Math.Clamp((int)Math.Round(media.Width * scale), 1, Width);
                int height = Math.Clamp((int)Math.Round(media.Height * scale), 1, Height);
                canvas.DrawImage(media, new Rectangle((Width - width) / 2, (Height - height) / 2, width, height),
                    0, 0, media.Width, media.Height, GraphicsUnit.Pixel);
            }
            else
            {
                Center(canvas, top, small, 2);
                Center(canvas, center, center.Length > 8 || (config.Page == OledPage.Clock && config.TwelveHourClock)
                    ? medium : large, 14);
                Center(canvas, bottom, small, 51);
            }
        }
        lastFrame = PackPixels(drawMedia);
        lastKey = key;
        return lastFrame;
    }

    internal static int SeparatorGaps(MixerSettings config)
    {
        if (config.Separator == MixerSeparator.Disabled) return 0;
        if (config.Separator == MixerSeparator.ChannelFive) return 1 << 3;

        int gaps = 0;
        for (int channel = 0; channel < config.Channels.Length; channel++)
        {
            if (config.Channels[channel].Processes != Channel.SystemVolume) continue;
            if (channel > 0) gaps |= 1 << (channel - 1);
            if (channel < config.Channels.Length - 1) gaps |= 1 << channel;
        }
        return gaps;
    }

    private static byte[] DrawMixer(MixerSettings config, int?[] values, int separators)
    {
        byte[] frame = new byte[Width * Height / 8];
        for (int channel = 0; channel < 5; channel++)
        {
            int y = channel * 11 + 1;
            string title = config.Channels[channel].Label;
            string label = Ascii(title, 18);
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
            if ((separators & (1 << channel)) != 0)
                for (int x = 1; x < 127; x++) OledMixerFont.Pixel(frame, x, y + 9);
        }
        return frame;
    }

    private static void Center(Graphics canvas, string text, Font font, int y)
    {
        using var layout = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        canvas.DrawString(text, font, Brushes.White, new RectangleF(1, y, Width - 2, Height - y), layout);
    }

    private byte[] PackPixels(bool dither)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), argb, y * Width * 4, Width * 4);
        }
        finally { bitmap.UnlockBits(data); }

        byte[] pixels = new byte[Width * Height / 8];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 4;
                int luminance = (argb[i] * 29 + argb[i + 1] * 150 + argb[i + 2] * 77) >> 8;
                int threshold = dither ? Dither[(y & 3) * 4 + (x & 3)] * 16 + 8 : 127;
                if (luminance >= threshold)
                    pixels[y * (Width / 8) + x / 8] |= (byte)(0x80 >> (x & 7));
            }
        }
        return pixels;
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

    public void Dispose()
    {
        media?.Dispose();
        mediaStream?.Dispose();
        bitmap.Dispose();
        small.Dispose(); large.Dispose(); medium.Dispose();
    }
}
