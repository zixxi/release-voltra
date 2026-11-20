using SkiaSharp;
using Voltra;

namespace Voltra.Linux;

internal sealed record DisplaySnapshot(string Message, byte[] Pixels);
internal sealed record DisplayMedia(uint Id, (byte[] Pixels, int Milliseconds)[] Frames, int TotalDuration);

internal sealed class OledDisplayService : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly Func<Settings> settings;
    private readonly LinuxDeviceService device;
    private readonly PipeWireAudioService audio;
    private DisplaySnapshot snapshot = new("", new byte[SerialProtocol.FrameSize]);
    internal DisplaySnapshot Snapshot => Volatile.Read(ref snapshot);

    internal OledDisplayService(Func<Settings> settings, LinuxDeviceService device, PipeWireAudioService audio)
    {
        this.settings = settings;
        this.device = device;
        this.audio = audio;
        worker = Task.Run(() => RunAsync(stop.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var weatherService = new WeatherService();
        WeatherReading? weather = null;
        Task<WeatherReading>? weatherJob = null;
        string city = "", lastQuery = "", weatherStatus = "";
        long nextWeather = 0;
        DisplayMedia? media = null;
        Task<DisplayMedia>? mediaJob = null;
        string mediaPath = "", lastMedia = "", mediaStatus = "";
        long animationStart = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var config = settings();
                long now = Environment.TickCount64;
                if (city != config.City)
                {
                    city = config.City;
                    weather = null;
                    weatherStatus = "";
                    nextWeather = 0;
                }
                if (weatherJob is { IsCompleted: true })
                {
                    try
                    {
                        var reading = await weatherJob;
                        if (city == lastQuery) { weather = reading; weatherStatus = ""; }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
                        System.Text.Json.JsonException or OperationCanceledException or KeyNotFoundException)
                    {
                        if (city == lastQuery) weatherStatus = $"Weather: {ex.Message}";
                    }
                    weatherJob = null;
                }
                if (config.Page == DisplayPage.Weather && city.Length > 0 && weatherJob is null && now >= nextWeather)
                {
                    lastQuery = city;
                    weatherJob = weatherService.ReadAsync(city, token);
                    nextWeather = now + 600_000;
                }

                if (mediaPath != config.Media)
                {
                    mediaPath = config.Media;
                    if (mediaJob is null) lastMedia = "";
                    media = null;
                    mediaStatus = "";
                }
                if (mediaJob is { IsCompleted: true })
                {
                    try
                    {
                        var loaded = await mediaJob;
                        if (lastMedia == mediaPath)
                        {
                            media = loaded;
                            animationStart = now;
                            mediaStatus = $"{Path.GetFileName(mediaPath)} · {media.Frames.Length} frames";
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        if (lastMedia == mediaPath) mediaStatus = $"Media: {ex.Message}";
                    }
                    mediaJob = null;
                }
                if (config.Page == DisplayPage.Image && mediaPath.Length > 0 && lastMedia != mediaPath && mediaJob is null)
                {
                    lastMedia = mediaPath;
                    string path = mediaPath;
                    mediaJob = Task.Run(() => DecodeMedia(path, token), token);
                }

                byte[] pixels = config.Page switch
                {
                    DisplayPage.Mixer => Mixer(config, audio.Snapshot.Volumes),
                    DisplayPage.Clock => TextFrame(DateTime.Now.ToString("ddd, dd MMM"),
                        DateTime.Now.ToString(config.TwelveHour ? "hh:mm tt" : "HH:mm"), DateTime.Now.ToString("yyyy")),
                    DisplayPage.Weather => weather is null ? TextFrame("WEATHER",
                        city.Length == 0 ? "SET CITY" : weatherStatus.Length > 0 ? "UNAVAILABLE" : "LOADING", city)
                        : TextFrame(weather.City, $"{weather.Temperature:0} C", weather.Condition),
                    DisplayPage.Image => media is null ? TextFrame("IMAGE / GIF", "VOLTRA", "SELECT A FILE")
                        : FrameAt(media, now - animationStart),
                    _ => TextFrame("PC UPTIME", Uptime(), "VOLTRA")
                };
                string message = config.Page switch
                {
                    DisplayPage.Image => mediaStatus,
                    DisplayPage.Weather => weatherStatus,
                    _ => ""
                };
                if (Snapshot.Pixels.AsSpan().SequenceEqual(pixels)) pixels = Snapshot.Pixels;
                else Volatile.Write(ref snapshot, new(message, pixels));
                if (Snapshot.Message != message) Volatile.Write(ref snapshot, new(message, pixels));
                device.SetFrame(new(pixels, config.Page, config.TwelveHour ? 1 : 0,
                    config.Page == DisplayPage.Image ? media?.Id ?? 0 : 0));
                await Task.Delay(config.Page == DisplayPage.Image ? 75 : 125, token);
            }
        }
        finally
        {
            if (weatherJob is not null)
                try { await weatherJob; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
                    System.Text.Json.JsonException or OperationCanceledException or KeyNotFoundException) { }
            if (mediaJob is not null)
                try { await mediaJob; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or ArgumentException or OperationCanceledException) { }
        }
    }

    internal static byte[] Mixer(Settings config, int?[] values)
    {
        var frame = new byte[SerialProtocol.FrameSize];
        int gaps = config.Separator == SeparatorMode.BeforeChannelFive ? 1 << 3 : 0;
        if (config.Separator == SeparatorMode.Automatic)
            for (int i = 0; i < 5; i++)
                if (config.Channels[i].Target.Equals(Settings.Master, StringComparison.OrdinalIgnoreCase))
                {
                    if (i > 0) gaps |= 1 << (i - 1);
                    if (i < 4) gaps |= 1 << i;
                }

        for (int i = 0; i < 5; i++)
        {
            int y = i * 11 + 1;
            string label = SerialProtocol.Ascii(config.Channels[i].Label, 18);
            if (OledMixerFont.Measure(label) > 43)
            {
                do { label = label[..^1]; } while (OledMixerFont.Measure(label + ".") > 43);
                label += ".";
            }
            OledMixerFont.Draw(frame, label, 1, y + 1);
            int fill = (values[i].GetValueOrDefault() * 56 + 50) / 100;
            for (int x = 0; x < 56; x++)
            {
                OledMixerFont.Pixel(frame, 46 + x, y + 4);
                if (x >= fill) continue;
                OledMixerFont.Pixel(frame, 46 + x, y + 3);
                OledMixerFont.Pixel(frame, 46 + x, y + 5);
            }
            string level = values[i] is int value ? $"{value}%" : "--";
            OledMixerFont.Draw(frame, level, 127 - OledMixerFont.Measure(level), y + 1);
            if ((gaps & (1 << i)) != 0)
                for (int x = 1; x < 127; x++) OledMixerFont.Pixel(frame, x, y + 9);
        }
        return frame;
    }

    private static byte[] TextFrame(string top, string middle, string bottom)
    {
        var frame = new byte[SerialProtocol.FrameSize];
        Center(top, 5, 1);
        Center(middle, 24, 2);
        Center(bottom, 52, 1);
        return frame;

        void Center(string text, int y, int scale)
        {
            text = SerialProtocol.Ascii(text, 128 / (5 * scale));
            int width = OledMixerFont.Measure(text);
            int left = (128 - width * scale) / 2;
            if (scale == 1) { OledMixerFont.Draw(frame, text, left, y); return; }
            var glyphs = new byte[SerialProtocol.FrameSize];
            OledMixerFont.Draw(glyphs, text, 0, 0);
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < width; col++)
                    if ((glyphs[row * 16 + col / 8] & (0x80 >> (col & 7))) != 0)
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                                OledMixerFont.Pixel(frame, left + col * scale + dx, y + row * scale + dy);
        }
    }

    private static string Uptime()
    {
        var duration = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return duration.Days > 0 ? $"{duration.Days}D {duration.Hours:00}H" : $"{duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static byte[] FrameAt(DisplayMedia media, long elapsed)
    {
        long offset = elapsed % media.TotalDuration;
        foreach (var frame in media.Frames)
        {
            if (offset < frame.Milliseconds) return frame.Pixels;
            offset -= frame.Milliseconds;
        }
        return media.Frames[^1].Pixels;
    }

    internal static DisplayMedia DecodeMedia(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 25 * 1024 * 1024)
            throw new IOException("Choose an image or GIF smaller than 25 MiB.");
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new IOException("Could not decode this image.");
        if ((long)codec.Info.Width * codec.Info.Height > 4_000_000)
            throw new IOException("Choose an image smaller than four megapixels.");
        int count = Math.Max(1, codec.FrameCount);
        if (count > 300) throw new IOException("Choose a GIF with at most 300 frames.");
        var frames = new (byte[] Pixels, int Milliseconds)[count];
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var oled = new SKBitmap(128, 64);
        using var canvas = new SKCanvas(oled);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
        var metadata = codec.FrameInfo;
        float scale = Math.Min(128f / info.Width, 64f / info.Height);
        float width = info.Width * scale, height = info.Height * scale;
        var bounds = SKRect.Create((128 - width) / 2, (64 - height) / 2, width, height);
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            // The codec decodes any required earlier GIF frames when no prior frame is provided.
            var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i));
            if (result != SKCodecResult.Success) throw new IOException($"Image frame {i + 1} could not be decoded: {result}.");
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(bitmap, bounds, paint);
            canvas.Flush();
            var packed = new byte[SerialProtocol.FrameSize];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 128; x++)
                {
                    SKColor pixel = oled.GetPixel(x, y);
                    if (pixel.Red * 299 + pixel.Green * 587 + pixel.Blue * 114 >= 128_000)
                        OledMixerFont.Pixel(packed, x, y);
                }
            frames[i] = (packed, Math.Max(75, metadata.Length > i ? metadata[i].Duration : 100));
        }
        uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            System.Security.Cryptography.SHA256.HashData(frames.SelectMany(frame => frame.Pixels).ToArray()));
        return new(id == 0 ? 1u : id, frames, frames.Sum(frame => frame.Milliseconds));
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker; }
        catch (OperationCanceledException) { }
        finally { stop.Dispose(); }
    }
}
