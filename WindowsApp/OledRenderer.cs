using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Voltra;

internal sealed class OledRenderer : IDisposable
{
    internal const int Width = OledFrame.Width, Height = OledFrame.Height;
    private readonly Bitmap bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
    private readonly byte[] argb = new byte[Width * Height * 4];
    private readonly Font small = new("Segoe UI", OledFrame.SmallSize, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font large = new("Segoe UI", OledFrame.LargeSize, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font medium = new("Segoe UI", OledFrame.MediumSize, FontStyle.Bold, GraphicsUnit.Pixel);
    private Image? media;
    private MemoryStream? mediaStream;
    private FrameDimension? gifDimension;
    private int[] frameEnds = [];
    private int selectedFrame = -1;
    private long mediaStart;
    private string? lastKey;
    private byte[]? lastFrame;

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
        MediaId = OledFrame.MediaId(content);
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
        OledText text = default;
        bool drawMedia = config.Page == OledPage.Media && media is not null;
        int?[] values = config.Page == OledPage.Mixer ? state.WindowsLevels ?? new int?[5] : [];
        string[] labels = config.Page == OledPage.Mixer
            ? config.Channels.Select((ch, i) => ch.Dynamic ? state.TargetNames?[i] ?? ch.Label : ch.Label).ToArray() : [];
        int separators = config.Page == OledPage.Mixer ? SeparatorGaps(config) : 0;
        switch (config.Page)
        {
            case OledPage.Mixer:
                key = "m:" + string.Join("|", labels) +
                    ":" + string.Join(",", values) + ":" + separators;
                break;
            case OledPage.Clock:
                text = OledFrame.Clock(now, config.TwelveHourClock);
                key = "c:" + text.Center + text.Bottom;
                break;
            case OledPage.Weather:
                text = OledFrame.Weather(config.City, weather);
                key = "w:" + text.Top + text.Center + text.Bottom;
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
                if (!drawMedia) text = OledFrame.NoMedia();
                break;
            default:
                text = OledFrame.Uptime(now, Environment.TickCount64);
                key = "u:" + text.Center + text.Bottom;
                break;
        }

        if (lastKey == key && lastFrame is not null) return lastFrame;
        if (config.Page == OledPage.Mixer)
        {
            lastFrame = OledFrame.Mixer(labels, values, separators);
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
                var (x, y, width, height) = OledFrame.MediaBounds(media!.Width, media.Height);
                canvas.DrawImage(media, new Rectangle(x, y, width, height),
                    0, 0, media.Width, media.Height, GraphicsUnit.Pixel);
            }
            else
            {
                Center(canvas, text.Top, small, OledFrame.TopY);
                Center(canvas, text.Center, text.CenterSize == OledFrame.MediumSize ? medium : large, OledFrame.CenterY);
                Center(canvas, text.Bottom, small, OledFrame.BottomY);
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

        return OledFrame.AutomaticGaps(config.Channels.Select(channel => channel.Processes == Channel.SystemVolume).ToArray());
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

        return OledFrame.PackBgra(argb, Width * 4, dither);
    }

    internal static string Ascii(string text, int length) => OledFrame.Ascii(text, length);

    public void Dispose()
    {
        media?.Dispose();
        mediaStream?.Dispose();
        bitmap.Dispose();
        small.Dispose(); large.Dispose(); medium.Dispose();
    }
}
