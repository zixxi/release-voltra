namespace Voltra;

// AUMIDs can identify a packaged app across executables. For ordinary desktop
// apps use the full image path, never a window title or a bare executable name.
internal sealed record AudioApplication(string Key, string Path, string Name, bool RecognizedGame)
{
    internal static bool SharedHost(string path) => System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant()
        is "java" or "javaw" or "python" or "pythonw" or "dotnet" or "node" or "electron";

    internal string Display(bool formatted) => formatted ? Name : System.IO.Path.GetFileName(Path);
}

internal sealed class GameSelection
{
    private readonly List<string> recent = [];
    private static readonly StringComparer Identity = StringComparer.OrdinalIgnoreCase;

    internal AudioApplication? Choose(IEnumerable<AudioApplication> apps, AudioApplication? focused,
        IReadOnlySet<string> registered)
    {
        var eligible = apps.Where(app => app.RecognizedGame ||
                (!AudioApplication.SharedHost(app.Path) && (registered.Contains(app.Path) || registered.Contains(app.Key))))
            .DistinctBy(app => app.Key, Identity).ToDictionary(app => app.Key, Identity);
        recent.RemoveAll(key => !eligible.ContainsKey(key));
        if (focused is not null && eligible.ContainsKey(focused.Key))
        {
            recent.RemoveAll(key => Identity.Equals(key, focused.Key));
            recent.Insert(0, focused.Key);
        }
        if (recent.Count > 0) return eligible[recent[0]];
        if (eligible.Count != 1) return null;
        var only = eligible.Values.First();
        recent.Add(only.Key);
        return only;
    }
}
