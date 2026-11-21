using System.Runtime.InteropServices;
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
        using var textRenderer = new OledTextRenderer();
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

                DateTime local = DateTime.Now;
                byte[] pixels = config.Page switch
                {
                    DisplayPage.Mixer => Mixer(config, audio.Snapshot.Volumes),
                    DisplayPage.Clock => textRenderer.Render(OledFrame.Clock(local, config.TwelveHour)),
                    DisplayPage.Weather => textRenderer.Render(OledFrame.Weather(city, weather)),
                    DisplayPage.Image => media is null ? textRenderer.Render(OledFrame.NoMedia())
                        : FrameAt(media, now - animationStart),
                    _ => textRenderer.Render(OledFrame.Uptime(local, now))
                };
                string message = config.Page switch
                {
                    DisplayPage.Image => mediaStatus,
                    DisplayPage.Weather => weatherStatus.Length > 0 || weather is null ? weatherStatus : $"{weather.City} · {weather.Temperature:0} °C",
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
        int gaps = config.Separator switch
        {
            SeparatorMode.BeforeChannelFive => 1 << 3,
            SeparatorMode.Automatic => OledFrame.AutomaticGaps(config.Channels.Select(channel =>
                channel.Target.Equals(Settings.Master, StringComparison.OrdinalIgnoreCase)).ToArray()),
            _ => 0
        };
        return OledFrame.Mixer(config.Channels.Select(channel => channel.Label).ToArray(), values, gaps);
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
        using var stream = File.OpenRead(path);
        if (stream.Length > 25 * 1024 * 1024)
            throw new IOException("Choose an image or GIF smaller than 25 MiB.");
        uint mediaId = OledFrame.MediaId(stream);
        stream.Position = 0;
        using var codec = SKCodec.Create(stream) ?? throw new IOException("Could not decode this image.");
        if ((long)codec.Info.Width * codec.Info.Height > 4_000_000)
            throw new IOException("Choose an image smaller than four megapixels.");
        int count = Math.Max(1, codec.FrameCount);
        if (count > 300) throw new IOException("Choose a GIF with at most 300 frames.");
        var frames = new (byte[] Pixels, int Milliseconds)[count];
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var oled = new SKBitmap(OledFrame.Width, OledFrame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(oled);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        var pixels = new byte[oled.RowBytes * oled.Height];
        var metadata = codec.FrameInfo;
        var (x, y, width, height) = OledFrame.MediaBounds(info.Width, info.Height);
        var bounds = SKRect.Create(x, y, width, height);
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            // The codec decodes any required earlier GIF frames when no prior frame is provided.
            var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i));
            if (result != SKCodecResult.Success) throw new IOException($"Image frame {i + 1} could not be decoded: {result}.");
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(bitmap, bounds, paint);
            canvas.Flush();
            Marshal.Copy(oled.GetPixels(), pixels, 0, pixels.Length);
            var packed = OledFrame.PackBgra(pixels, oled.RowBytes, dither: true);
            int delay = Math.Clamp(metadata.Length > i ? metadata[i].Duration : 100, 20, 10_000);
            frames[i] = (packed, delay);
        }
        return new(mediaId, frames, frames.Sum(frame => frame.Milliseconds));
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker; }
        catch (OperationCanceledException) { }
        finally { stop.Dispose(); }
    }
}
