using System.Text.Json;
using Avalonia.Media;
using Voltra;

namespace Voltra.Linux;

internal sealed record Channel(string Label, string Target);
internal enum DisplayPage { Mixer, Clock, Weather, Image, Uptime }
internal enum SeparatorMode { Automatic, BeforeChannelFive, None }

internal sealed record Settings
{
    internal const string Master = "@master";
    internal const string DefaultAccent = "#B78BFA";
    public MappingRules Rules { get; init; } = new();
    public Channel[] Channels { get; init; } =
        [new("Spotify", "spotify"), new("Discord", "discord"), new("Firefox", "firefox"),
         new("Game", DynamicAudio.Game), new("Master", Master)];
    public string Theme { get; init; } = "Dark";
    public string Accent { get; init; } = DefaultAccent;
    public string Port { get; init; } = "";
    public bool? AutoDetect { get; init; }
    internal string? ManualPort => (AutoDetect ?? Port.Length == 0) ? null : Port;
    public string City { get; init; } = "";
    public string Media { get; init; } = "";
    public DisplayPage Page { get; init; }
    public SeparatorMode Separator { get; init; }
    public bool TwelveHour { get; init; }
    public bool FormatAppNames { get; init; } = true;
    public bool Sync { get; init; } = true;
    public KnobTakeover Takeover { get; init; }
    public int NoiseThreshold { get; init; } = 2;
    public bool Startup { get; init; }
}

internal static class LinuxSettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static string ConfigHome
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.IsPathFullyQualified(configured ?? "") ? configured! :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
    }

    internal static string Folder => Path.Combine(ConfigHome, "Voltra");
    private static string PathName => Path.Combine(Folder, "settings.json");

    internal static Settings Read(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(PathName)) return new();
            var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName)) ?? new();
            var defaults = new Settings();
            return value with
            {
                Rules = (value.Rules ?? new()).Normalize(),
                Channels = Enumerable.Range(0, 5).Select(i =>
                {
                    var channel = value.Channels?.ElementAtOrDefault(i) ?? defaults.Channels[i];
                    return channel with { Label = channel.Label ?? "", Target = channel.Target ?? "" };
                }).ToArray(),
                Theme = value.Theme is "Dark" or "Light" or "System" ? value.Theme : defaults.Theme,
                Accent = TryAccent(value.Accent, out _) ? value.Accent : defaults.Accent,
                AutoDetect = value.AutoDetect ?? string.IsNullOrEmpty(value.Port),
                Port = value.Port ?? "", City = value.City ?? "", Media = value.Media ?? "",
                Page = Enum.IsDefined(value.Page) ? value.Page : defaults.Page,
                Separator = Enum.IsDefined(value.Separator) ? value.Separator : defaults.Separator,
                Takeover = Enum.IsDefined(value.Takeover) ? value.Takeover : defaults.Takeover,
                NoiseThreshold = Math.Clamp(value.NoiseThreshold, 1, 5)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Could not load preferences: {ex.Message}";
            return new();
        }
    }

    internal static bool TryAccent(string? text, out Color color)
    {
        color = default;
        return text is { Length: 7 } && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit) &&
            Color.TryParse(text, out color);
    }

    internal static Task SaveAsync(Settings value) => Task.Run(() => AtomicWrite(PathName, JsonSerializer.Serialize(value, Json)));

    internal static void AtomicWrite(string destination, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
