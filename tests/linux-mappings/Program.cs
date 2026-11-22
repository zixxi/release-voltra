using System.Text.Json;
using Voltra.Linux;

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
if (args is ["--x11", var expected])
{
    using var tracker = new ForegroundWindow();
    var foreground = await tracker.ReadAsync(true, default);
    Check(foreground.Supported && foreground.AppId == expected, "XCB reads active window application ID");
    return;
}
AudioTarget Stream(string app, int id, bool game = false, int pid = 0) =>
    new(id.ToString(), app, app, [], 40, new("app:" + app, ["app:" + app], pid, "100", game));
var spotify = Stream("Spotify", 1);
var discord = Stream("Discord", 2);
var firefox = Stream("Firefox", 3);
var a = Stream("Alpha", 4, true);
var b = Stream("Beta", 5, true);
var resolver = new DynamicAudio();
AudioTarget[] apps = [spotify, discord, firefox, a, b];
foreach (var app in apps)
    Check(DynamicAudio.FocusKey(apps, new(AppId: app.Target), null) == app.Identity!.Key, $"focused {app.Target}");
Check(DynamicAudio.FocusKey(apps, new(), null) == null && DynamicAudio.FocusKey(apps, new(AppId:"Silent"), null) == null, "desktop and silent app");
Check(DynamicAudio.FocusKey(apps, new(AppId:"Spotify", Supported:false), null) == null, "unsupported compositor");
Check(resolver.Resolve(apps, null, new()).Game.Length == 0, "ambiguous games without focus");
Check(resolver.Resolve(apps, a.Identity!.Key, new()).Game.Single() == a, "foreground game");
Check(resolver.Resolve(apps, "app:Spotify", new()).Game.Single() == a, "game retained after focus loss");
Check(resolver.Resolve(apps, b.Identity!.Key, new()).Game.Single() == b, "new foreground game wins");
Check(resolver.Resolve([spotify, a], null, new()).Game.Single() == a, "game closes");
Check(resolver.Resolve([spotify], "app:Spotify", new()).Game.Length == 0, "ordinary audio does not identify a game");
var rules = new MappingRules { GameAllow = ["app:Spotify"], FocusAllow = ["app:Firefox"], FocusDeny = ["app:Firefox"] };
Check(resolver.Resolve([spotify], null, rules).Game.Single() == spotify, "manual game recognition");
Check(resolver.Resolve([spotify], null, rules with { GameDeny = ["app:Spotify"] }).Game.Length == 0, "game deny precedence");
Check(resolver.Resolve(apps, "app:Firefox", rules).Focused.Length == 0 && resolver.Resolve(apps, "app:Discord", rules).Focused.Length == 0,
    "independent focus allow and deny");
Check(resolver.Resolve([spotify, Stream("Spotify", 6)], "app:Spotify", new()).Focused.Length == 2, "multiple related streams");
Check(ForegroundWindow.ParseSway("""{"nodes":[],"floating_nodes":[{"id":2,"focused":true,"app_id":"Discord","pid":123}]}""") == new Focus(123,"Discord","2"),
    "Sway floating window");
string dir = Directory.CreateTempSubdirectory("voltra-test-").FullName;
try
{
    if (OperatingSystem.IsLinux())
    {
        async Task<AppIdentity?> ReadProcess(string name, string argument)
        {
            string file = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.Copy("/bin/sleep", file);
            var start = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
            start.ArgumentList.Add("60");
            // GNU sleep accepts environment variables, but not a Wine command line.
            // Test the native Steam path with a real process and controlled metadata.
            start.Environment["SteamAppId"] = argument;
            using var process = System.Diagnostics.Process.Start(start)!;
            try { return AppIdentity.Read(process.Id, "", Path.GetFileName(file), ""); }
            finally { process.Kill(); await process.WaitForExitAsync(); }
        }
        var steam = await ReadProcess("steamapps/common/Example/game", "480");
        Check(steam is { Game:true, Key:"steam:480" }, "Steam ID corroborated by native game path");
        var unrelated = await ReadProcess("outside/browser", "480");
        Check(unrelated is { Game:false }, "ambient Steam environment alone is insufficient");
        var wrong = AppIdentity.Read(Environment.ProcessId, "flatpak.App", "unrelated-binary", "", hostPid:false);
        Check(wrong is { Pid:0, Key:"app:flatpak.App" }, "namespace-local PID cannot bind an unrelated host PID");
    }
    Environment.SetEnvironmentVariable("PATH", Path.Combine(AppContext.BaseDirectory, "fixtures") + ":" + Environment.GetEnvironmentVariable("PATH"));
    Environment.SetEnvironmentVariable("VOLTRA_TEST_AUDIO", dir);
    Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
    Environment.SetEnvironmentVariable("SWAYSOCK", null);
    using (var unsupported = new ForegroundWindow())
        Check(!(await unsupported.ReadAsync(true, default)).Supported, "Wayland without supported IPC");
    Environment.SetEnvironmentVariable("SWAYSOCK", "fixture");
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
    void Set(string file, string text)
    {
        var path = Path.Combine(dir, file);
        File.WriteAllText(path + ".new", text);
        File.Move(path + ".new", path, true);
    }
    void Event(string kind) => File.AppendAllText(Path.Combine(dir, "events"), $"Event '{kind}' on sink-input #2\n");
    void FocusOn(string app) => Set("focus", JsonSerializer.Serialize(new { focused=true, id=8, app_id=app }));
    string Writes() => File.ReadAllText(Path.Combine(dir, "writes"));
    await LinuxSettingsService.SaveAsync(new Settings { Rules = rules });
    Check(LinuxSettingsService.Read(out _).Rules.FocusDeny.SequenceEqual(rules.FocusDeny), "rules round trip");
    const string two = """[[1,"Spotify",11,"music"],[2,"Discord",12,"communication"]]""";
    Set("events",""); Set("writes",""); Set("1","20"); Set("2","75"); Set("streams",two); FocusOn("Spotify");
    var config = new Settings();
    config.Channels[0] = new("Focused", DynamicAudio.Focused);
    config.Channels[1] = new("Game", DynamicAudio.Game);
    config.Channels[3] = new("Unused", "");
    config = config with { Rules = rules with { FocusAllow = [], FocusDeny = [], GameAllow = [] } };
    await LinuxSettingsService.SaveAsync(config);
    Check(LinuxSettingsService.Read(out _).Channels[0].Target == DynamicAudio.Focused, "mapping round trip");
    int[] levels = [20, 30, 0, 0, 0];
    await using var service = new PipeWireAudioService(() => Volatile.Read(ref config),
        () => new("", "", [], (int[])Volatile.Read(ref levels).Clone()));
    void Knob(int i,int value) { var next=(int[])levels.Clone(); next[i]=value; Volatile.Write(ref levels,next); }
    async Task Until(Func<bool> condition, string text)
    {
        var end = Environment.TickCount64 + 7500;
        while (!condition() && Environment.TickCount64 < end) await Task.Delay(25);
        Check(condition(), text + " [" + service.Snapshot.Status + "]");
    }
    await Until(() => service.Snapshot.Volumes[0] == 20, "initial focus resolves");
    FocusOn("Discord");
    await Until(() => service.Snapshot.Volumes[0] == 75, "switch reads new target");
    Knob(0,32); await Task.Delay(280);
    Check(Writes() == "", "soft takeover protects new target");
    Knob(0,74);
    await Until(() => Writes().Contains("set-sink-input-volume 2 74%"), "soft pickup");
    Set("2","90"); Event("change");
    await Until(() => service.Snapshot.Volumes[0] == 90 && service.Snapshot.Waiting[0], "external volume releases capture");
    var before = Writes(); Knob(0,78); await Task.Delay(250);
    Check(Writes() == before, "external level preserved");
    FocusOn("Spotify"); FocusOn("Desktop"); Knob(0,90);
    await Until(() => service.Snapshot.Volumes[0] == null, "rapid switch to desktop");
    Check(Writes() == before, "no stale write at focus change");
    Set("streams","[]"); Event("remove"); FocusOn("Discord");
    await Task.Delay(450);
    Check(service.Snapshot.Volumes[0] == null && service.Snapshot.Volumes[1] == null, "temporarily missing streams");
    Set("streams",two); Event("new");
    await Until(() => service.Snapshot.Volumes[0] == 90, "recreated stream");
    // Change enum through persisted JSON, exercising the same configuration route as Settings.
    string raw = JsonSerializer.Serialize(config).Replace("\"Takeover\":0","\"Takeover\":1");
    Volatile.Write(ref config, JsonSerializer.Deserialize<Settings>(raw)!);
    FocusOn("Spotify"); await Until(() => service.Snapshot.Volumes[0] == 20,"immediate target baseline");
    Knob(0,67); await Until(() => Writes().Contains("set-sink-input-volume 1 67%"),"immediate first movement");
    Set("3","85"); Set("streams","""[[1,"Spotify",11,"music"],[3,"Chess",19,"game"]]"""); Event("new");
    await Until(() => service.Snapshot.Volumes[1] == 85, "late game audio");
    Knob(1,45); await Until(() => Writes().Contains("set-sink-input-volume 3 45%"),"game immediate movement");
    Set("3","70"); Set("4","90");
    Set("streams","""[[3,"Chess",19,"game"],[4,"Chess",20,"game"]]"""); Event("new");
    string soft = JsonSerializer.Serialize(config).Replace("\"Takeover\":1","\"Takeover\":0");
    Volatile.Write(ref config, JsonSerializer.Deserialize<Settings>(soft)!);
    await Until(() => service.Snapshot.Volumes[1] == 80 && service.Snapshot.Waiting[1], "game group rebinding at mean");
    before = Writes(); Knob(1,55); await Task.Delay(250);
    Check(Writes() == before, "game soft takeover protects both streams");
    Knob(1,81);
    await Until(() => Writes().Contains("set-sink-input-volume 3 71%") && Writes().Contains("set-sink-input-volume 4 91%"),
        "game soft pickup preserves relative levels");
    Set("streams",two); Event("remove");
    await Until(() => service.Snapshot.Volumes[1] == null, "game stream removal");
    Check(!Writes().Contains("set-sink-volume "), "no Master writes");
}
finally { Directory.Delete(dir, true); }
