using System.Text.Json;

namespace Voltra;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--sync-startup", StringComparer.OrdinalIgnoreCase))
            return WindowsStartup.SyncForSetup(args);

        bool minimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (args is not ["--after-setup"] && Mutex.TryOpenExisting("Voltra.Setup", out var setup))
        {
            setup.Dispose();
            if (!minimized)
                MessageBox.Show("Finish Voltra Setup before opening the app.", "Voltra");
            return 0;
        }
        using var instance = new Mutex(true, "PurpleMixer.Desktop", out bool first);
        if (!first)
        {
            if (!minimized)
                MessageBox.Show("Voltra is already running. Open it from the system tray.", "Voltra");
            return 0;
        }
        ApplicationConfiguration.Initialize();
        using var form = new MixerForm();
        using var context = new ApplicationContext();
        form.FormClosed += (_, _) => context.ExitThread();
        // A hidden form still needs a handle so Close runs the normal shutdown events.
        _ = form.Handle;
        if (!minimized) form.Show();
        // Leaving MainForm unset keeps WinForms from showing it on entry to the loop.
        Application.Run(context);
        using var installer = form.PendingInstaller;
        try { installer?.Launch(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show("Voltra has closed, but Windows could not start the installer. " + ex.Message,
                "Voltra update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }
}

internal enum OledPage { Mixer, Clock, Weather, Media, Uptime }
internal enum MixerSeparator { Automatic, ChannelFive, Disabled }
internal enum AppTheme { Dark, Light, System }

internal sealed record Channel(string Label, string Processes)
{
    internal const string SystemVolume = "@master";
}

internal sealed record MixerSettings
{
    internal const string DefaultAccent = "#B78BFA";
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    internal static string Folder => Path.Combine(AppData, "Voltra");
    private static string LegacyFolder => Path.Combine(AppData, "PurpleMixer");
    private static string SettingsPath => Path.Combine(Folder, "settings.json");

    public string Port { get; init; } = "";
    public bool AutoDetect { get; init; } = true;
    public string PreferredPort { get; init; } = "";
    public AppTheme Theme { get; init; }
    public string Accent { get; init; } = DefaultAccent;
    public bool LaunchAtWindowsStartup { get; init; } = true;
    public bool FormatAppNames { get; init; } = true;
    public bool WindowsVolumeSync { get; init; } = true;
    public KnobTakeover TakeoverMode { get; init; }
    public int KnobNoiseThreshold { get; init; } = 2;
    public bool TwelveHourClock { get; init; }
    public OledPage Page { get; init; }
    public MixerSeparator Separator { get; init; }
    public string City { get; init; } = "";
    public string MediaPath { get; init; } = "";
    public Channel[] Channels { get; init; } =
    [
        new("Spotify", "Spotify.exe"), new("Discord", "Discord.exe"),
        new("Minecraft", "javaw.exe"), new("Chrome", "chrome.exe"), new("Master", Channel.SystemVolume)
    ];

    internal static MixerSettings Load()
    {
        try
        {
            bool migrate = !File.Exists(SettingsPath);
            string path = migrate ? Path.Combine(LegacyFolder, "settings.json") : SettingsPath;
            var settings = JsonSerializer.Deserialize<MixerSettings>(File.ReadAllText(path));
            if (settings is not null && settings.Channels is { Length: 5 } &&
                settings.Channels.All(channel => channel is not null) && Enum.IsDefined(settings.Page))
            {
                var normalized = settings with
                {
                    PreferredPort = settings.PreferredPort ?? "",
                    TakeoverMode = Enum.IsDefined(settings.TakeoverMode) ? settings.TakeoverMode : KnobTakeover.Soft,
                    KnobNoiseThreshold = Math.Clamp(settings.KnobNoiseThreshold, 1, 5),
                    Port = settings.Port ?? "",
                    Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : AppTheme.Dark,
                    Accent = ThemePalette.IsHex(settings.Accent) ? settings.Accent : DefaultAccent,
                    Separator = Enum.IsDefined(settings.Separator) ? settings.Separator : MixerSeparator.Automatic
                };
                if (migrate) Migrate(normalized);
                return normalized;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    internal static bool ReadStartupPreference() =>
        ReadStartupPreference(SettingsPath) ??
        ReadStartupPreference(Path.Combine(LegacyFolder, "settings.json")) ?? true;

    private static bool? ReadStartupPreference(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("Expected a settings object.");
            if (!root.TryGetProperty(nameof(LaunchAtWindowsStartup), out var value)) return true;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new JsonException("Windows startup preference must be true or false.")
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void Migrate(MixerSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string lastPort = Path.Combine(Folder, "last-port.txt");
            string legacyPort = Path.Combine(LegacyFolder, "last-port.txt");
            if (!File.Exists(lastPort) && File.Exists(legacyPort))
                File.Copy(legacyPort, lastPort, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MixerDiagnostics.Log($"Could not migrate the previous device port: {ex.Message}");
        }

        try { settings.Write(overwrite: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MixerDiagnostics.Log($"Could not migrate settings: {ex.Message}");
        }
    }

    internal void Save() => Write(overwrite: true);

    private void Write(bool overwrite)
    {
        Directory.CreateDirectory(Folder);
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
