using System.Text;

namespace Voltra.Linux;

internal sealed record MappingRules
{
    public string[] GameAllow { get; init; } = [];
    public string[] GameDeny { get; init; } = [];
    public string[] FocusAllow { get; init; } = [];
    public string[] FocusDeny { get; init; } = [];

    internal MappingRules Normalize()
    {
        static string[] Clean(string[]? entries) => (entries ?? []).Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new() { GameAllow = Clean(GameAllow), GameDeny = Clean(GameDeny), FocusAllow = Clean(FocusAllow), FocusDeny = Clean(FocusDeny) };
    }
}

internal sealed record AppIdentity(string Key, string[] Rules, int Pid, string Lifetime, bool Game)
{
    internal static AppIdentity? Read(int pid, string appId, string binary, string role, bool hostPid = true)
    {
        string proc = $"/proc/{pid}";
        string image = "", lifetime = "", steam = "", flatpak = "";
        if (pid > 0)
        {
            try
            {
                image = new FileInfo(proc + "/exe").LinkTarget ?? "";
                // Pulse clients inside a PID namespace may report a container-local PID.
                // Without a server-supplied host PID, corroborate the executable first.
                if (!hostPid && (binary.Length == 0 || Path.GetFileName(image) != Path.GetFileName(binary)))
                    return Read(0, appId, binary, role);
                var stat = File.ReadAllText(proc + "/stat");
                lifetime = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19];
                var info = proc + "/root/.flatpak-info";
                if (File.Exists(info))
                    flatpak = File.ReadLines(info).FirstOrDefault(l => l.StartsWith("name=", StringComparison.Ordinal))?[5..] ?? "";
                // Only recognize a Steam ID when the process also runs inside a game's
                // installation, or Wine was given a game image from that installation.
                bool installed = GamePath(image);
                if (!installed && (Path.GetFileName(image) is "wine" or "wine64" or "wine-preloader" or "wine64-preloader"))
                    installed = Fields(proc + "/cmdline").Any(arg => GamePath(arg) && arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (installed)
                    steam = Fields(proc + "/environ").FirstOrDefault(s => s.StartsWith("SteamAppId=", StringComparison.Ordinal))?[11..] ?? "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IndexOutOfRangeException) { }
        }
        if (!uint.TryParse(steam, out uint number) || number == 0) steam = "";
        var keys = new List<string>();
        if (steam != "") keys.Add("steam:" + steam);
        if (flatpak != "" && flatpak != "com.valvesoftware.Steam") keys.Add("app:" + flatpak);
        if (appId is not ("" or "com.valvesoftware.Steam" or "org.winehq.Wine" or "org.winehq.wine")) keys.Add("app:" + appId);
        bool shared = Shared(binary) || Shared(Path.GetFileName(image));
        if (!shared && Path.IsPathFullyQualified(image) && !image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) keys.Add("exe:" + image);
        if (!shared && binary.Length > 0 && !binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            keys.Add("binary:" + Path.GetFileName(binary));
        string key = keys.FirstOrDefault() ?? (pid > 0 && lifetime != "" ? $"pid:{pid}@{lifetime}" : "");
        bool utility = Path.GetFileName(binary) is "steam" or "steamwebhelper" or "firefox" or "chrome" or "chromium"
            or "spotify" or "Discord" or "discord";
        bool game = !utility && (steam != "" || string.Equals(role, "game", StringComparison.OrdinalIgnoreCase));
        return key.Length == 0 ? null : new(key, keys.Distinct().ToArray(), pid, lifetime, game);
    }

    private static bool Shared(string name) => name is "steam" or "steamwebhelper" or "pressure-vessel" or "java" or "javaw" or "dotnet" or "electron" or "node" or "python"
        or "wine" or "wine64" or "wine-preloader" or "wine64-preloader" || name.StartsWith("python3", StringComparison.Ordinal);
    private static bool GamePath(string path) => path.Replace('\\', '/').Contains("/steamapps/common/", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("/SteamLinuxRuntime", StringComparison.OrdinalIgnoreCase) && !path.Contains("/Proton ", StringComparison.OrdinalIgnoreCase);

    private static string[] Fields(string file)
    {
        using var stream = File.OpenRead(file);
        byte[] bytes = new byte[32768];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(bytes, 0, count).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}

internal sealed class DynamicAudio
{
    internal const string Game = "@game", Focused = "@focused";
    internal static bool IsDynamic(string value) => value is Game or Focused;
    private readonly List<string> recent = [];
    internal static string? FocusKey(AudioTarget[] streams, Focus focus, AppIdentity? owner)
    {
        if (!focus.Supported || focus.Pid == Environment.ProcessId) return null;
        var groups = streams.Where(t => t.Identity != null).Select(t => t.Identity!).ToArray();
        IEnumerable<AppIdentity> candidates = groups.Where(a => focus.Pid > 0 && a.Pid == focus.Pid &&
            owner is { Lifetime.Length: > 0 } && owner.Lifetime == a.Lifetime);
        if (!candidates.Any() && focus.AppId.Length != 0)
            candidates = groups.Where(a => a.Rules.Contains("app:" + focus.AppId));
        if (!candidates.Any() && owner != null && focus.AppId.Length == 0)
            candidates = groups.Where(a => a.Key == owner.Key || a.Rules.Intersect(owner.Rules)
                .Any(rule => !rule.StartsWith("binary:", StringComparison.Ordinal)));
        var matches = candidates.Select(a => a.Key).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal (AudioTarget[] Game, AudioTarget[] Focused) Resolve(AudioTarget[] streams, string? focus, MappingRules rules)
    {
        var groups = streams.Where(t => t.Identity != null && t.Target != Settings.Master)
            .GroupBy(t => t.Identity!.Key).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray());
        static bool Listed(AudioTarget[] group, string[] list) => group.Any(t => t.Identity!.Rules.Any(list.Contains));
        var games = groups.Where(g => !Listed(g.Value, rules.GameDeny) &&
            (g.Value.Any(t => t.Identity!.Game) || Listed(g.Value, rules.GameAllow))).Select(g => g.Key).ToHashSet();
        recent.RemoveAll(key => !games.Contains(key));
        if (focus != null && games.Contains(focus)) { recent.Remove(focus); recent.Insert(0, focus); }
        if (recent.Count == 0 && games.Count == 1) recent.Add(games.Single());
        var front = focus != null && groups.TryGetValue(focus, out var group) && !Listed(group, rules.FocusDeny) &&
            (rules.FocusAllow.Length == 0 || Listed(group, rules.FocusAllow)) ? group : [];
        return (recent.Count == 0 ? [] : groups[recent[0]], front);
    }
}
