using System.Drawing;
using System.Text.Json;
using Voltra;

int passed = 0;
void Check(string name, Action test)
{
    test();
    Console.WriteLine($"PASS {name}");
    passed++;
}
static void Expect(bool condition)
{
    if (!condition) throw new InvalidOperationException("Assertion failed");
}

var mappings = new Channel[] { new("Game", Channel.Game), new("Focus", Channel.Focused),
    new("Static", "spotify.exe"), new("Master", Channel.SystemVolume), new("Unknown", "@future") };
var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var binding = new SessionBindings();
var spotify = new Session("Spotify", 20, channel: 2);
var discord = new Session("Discord", 70);
var chrome = new Session("Chrome", 50);
var game = new Session("Arena", 80, engine: true);
var other = new Session("Rally", 40, engine: true);
var master = new Session("Master", 97, channel: 3);
master.Target.App = null;
var microphoneUtility = new Session("No playback", 60);
var sessions = new List<Session> { spotify, discord, chrome, master };
void Focus(Session? front, bool formatted = true) =>
    binding.Resolve(mappings, formatted, sessions.Select(s => s.Target), front?.Target.App, registered);
int? Read(int ch) => binding.Current(ch).Volume;
int? Write(int ch, int value, Func<bool>? guard = null, Action<int,int>? changed = null,
    KnobTakeover mode = KnobTakeover.Immediate) =>
    binding.Apply(ch, value, mappings[ch].Dynamic, true, mode, guard ?? (() => true), changed ?? ((_, _) => { }));

Check("Static, Master and legacy configuration", () =>
{
    var old = JsonSerializer.Deserialize<Channel>("""{"Label":"Music","Processes":"spotify.exe"}""");
    Expect(old is { Dynamic: false, Label: "Music" });
    var saved = JsonSerializer.Deserialize<Channel[]>(JsonSerializer.Serialize(mappings))!;
    Expect(saved.SequenceEqual(mappings) && saved[0].Dynamic && saved[1].Dynamic);
    Focus(null);
    Expect(Read(0) == null && Read(1) == null && Read(2) == 20 && Read(3) == 97 && Read(4) == null);
});
Check("Foreground apps, desktop, no-session app, and unrelated sessions", () =>
{
    foreach (var app in new[] { spotify, discord, chrome })
    {
        Focus(app);
        Expect(Read(1) == app.Target.Level && binding.Names[1] == app.Name && Read(0) == null);
    }
    foreach (var app in new Session?[] { null, microphoneUtility })
    {
        Focus(app);
        Expect(Read(1) == null && Write(1, 0) == null && master.Live == 97 && chrome.Live == 50);
    }
});
Check("Game starts before audio and later creates a session", () =>
{
    Focus(game);
    Expect(Read(0) == null && Read(1) == null);
    sessions.Add(game);
    Focus(game);
    Expect(Read(0) == 80 && Read(1) == 80 && binding.Names[0] == "Arena");
    Focus(game, false);
    Expect(binding.Names[1] == "Arena.exe");
});
Check("Two games, focus preference, loss and recovery of focus", () =>
{
    sessions.Add(other);
    Focus(other); Expect(Read(0) == 40);
    Focus(spotify); Expect(Read(0) == 40 && Read(1) == 20);
    sessions.Reverse();
    Focus(null); Expect(Read(0) == 40);
    Focus(game); Expect(Read(0) == 80);
    sessions.Remove(game); Focus(game); Expect(Read(0) == 40 && Read(1) == null);
    sessions.Remove(other); Focus(null); Expect(Read(0) == null && Write(0, 0) == null);
    var ambiguous = new GameSelection();
    Expect(ambiguous.Choose([game.Target.App!, other.Target.App!], null, registered) == null);
});
Check("Restart and session membership changes reset the source identity", () =>
{
    sessions.Add(game); Focus(game);
    var first = binding.Current(0).Source;
    sessions.Reverse(); Focus(game); Expect(ReferenceEquals(first, binding.Current(0).Source));
    sessions.Remove(game); var restarted = new Session("Arena", 19, engine: true);
    sessions.Add(restarted); Focus(restarted);
    Expect(!ReferenceEquals(first, binding.Current(0).Source) && Read(0) == 19);
    sessions.Remove(restarted);
});
Check("Manual game registration uses the executable path", () =>
{
    var manual = new Session("Pocket", 45);
    sessions.Add(manual); Focus(manual); Expect(Read(0) == null);
    registered.Add(manual.Target.App!.Path);
    Focus(manual); Expect(binding.Names[0] == manual.Name && Read(0) == 45);
    registered.Clear(); Focus(manual); Expect(Read(0) == null);
    sessions.Remove(manual);
});
Check("Matching helpers and multiple endpoints only affect the resolved application", () =>
{
    var helper = new Session("Different helper executable", 61);
    helper.Target.App = helper.Target.App! with { Key = spotify.Target.App!.Key };
    var impostor = new Session("Spotify", 88);
    impostor.Target.App = impostor.Target.App! with { Key = "different installation" };
    Focus(spotify); var previous = binding.Current(1).Source;
    sessions.Add(helper); sessions.Add(impostor); Focus(spotify);
    Expect(!ReferenceEquals(previous, binding.Current(1).Source) && Read(1) == 61);
    int shared = -1;
    Write(1, 31, changed: (i,v) => { if (i == 2) shared = v; });
    Expect(shared == 31 && helper.Live == 31 && spotify.Live == 31 && impostor.Live == 88 && master.Live == 97);
    sessions.Remove(helper); sessions.Remove(impostor);
});
Check("Soft group pickup preserves different session levels; immediate sets both", () =>
{
    var separate = new Session("Music helper", 80);
    separate.Target.App = spotify.Target.App;
    Focus(spotify);
    sessions.Add(separate); Focus(spotify);
    int previous = spotify.Live;
    Expect(Read(1) == 80);
    Expect(Write(1, 81, mode: KnobTakeover.Soft) == 81);
    Expect(separate.Live == 81 && spotify.Live == previous + 1);
    Expect(Write(1, 32) == 32 && separate.Live == 32 && spotify.Live == 32);
    sessions.Remove(separate);
});
Check("Focus switching re-arms the physical knob against the new group's volume", () =>
{
    var pickup = new VolumeTakeover();
    spotify.Live = spotify.Target.Level = 20;
    discord.Live = discord.Target.Level = 70;
    Focus(spotify); pickup.Rebind(20, Read(1), true);
    Focus(discord); pickup.Rebind(20, Read(1), true);
    for (int knob = 21; knob < 68; knob++)
        Expect(pickup.Move(knob, true, KnobTakeover.Soft, 2) == null);
    Expect(discord.Live == 70 && spotify.Live == 20);
    int captured = pickup.Move(68, true, KnobTakeover.Soft, 2)!.Value;
    Expect(Write(1, captured, mode: KnobTakeover.Soft) == 68 && spotify.Live == 20);
});
Check("Rapid focus switching and focus lost between resolution and write", () =>
{
    for (int i = 0; i < 500; i++)
    {
        var active = (i & 1) == 0 ? spotify : discord;
        var inactive = active == spotify ? discord : spotify;
        int before = inactive.Live;
        Focus(active); Expect(Write(1, i % 101) == i % 101 && inactive.Live == before);
        int writes = active.Writes;
        Expect(Write(1, 100, () => false) == null && active.Writes == writes);
    }
    sessions.Remove(discord); Focus(discord); Expect(Write(1, 33) == null);
    sessions.Add(discord);
});
Check("External change racing a knob write releases pickup in both dynamic modes", () =>
{
    sessions.Add(game); Focus(game);
    for (int index = 0; index < 2; index++)
    {
        var pickup = new VolumeTakeover();
        game.Live = game.Target.Level = 50;
        pickup.Rebind(50, Read(index), true);
        game.Live = 80; // Native callback has not reached Drain yet.
        Expect(Write(index, 51, changed: (i, level) => { if (i == index) pickup.ObserveWindows(level, true); }) == null);
        Expect(game.Live == 80 && pickup.Waiting);
        Expect(pickup.Move(52, true, KnobTakeover.Soft, 2) == null);
        Expect(pickup.Move(79, true, KnobTakeover.Soft, 2) == 79);
        Expect(Write(index, 79) == 79);
    }
});
Check("Soft and immediate takeover on switch, disconnect, and configured thresholds", () =>
{
    foreach (var mode in new[] { KnobTakeover.Soft, KnobTakeover.Immediate })
    foreach (int threshold in new[] { 1, 2, 5 })
    {
        var pickup = new VolumeTakeover();
        foreach (int initial in new[] { 80, 70, 45 })
        {
            pickup.Rebind(20, initial, true);
            Expect(pickup.Move(20, true, mode, threshold, threshold) == null);
            int first = 20 + threshold;
            Expect(pickup.Move(first, true, mode, threshold, threshold) ==
                (mode == KnobTakeover.Immediate ? first : null));
            Expect(pickup.Move(initial - threshold, true, mode, threshold, threshold) == initial - threshold);
            pickup.Written(initial - threshold);
        }
        pickup.Disconnect(); pickup.Rebind(null, 99, true);
        Expect(pickup.Move(10, true, mode, threshold) == null);
        pickup.Rebind(10, null, true); Expect(pickup.Actual == null && !pickup.Waiting);
    }
});
Check("Desktop helper identity requires adjacent image and matching product and company", () =>
{
    var root = Directory.CreateTempSubdirectory();
    try
    {
        string parent = Path.Combine(root.FullName, "Player.exe");
        string helper = Path.Combine(root.FullName, "PlayerAudio.exe");
        string assembly = typeof(Session).Assembly.Location;
        var metadata = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly);
        Expect(WindowsApplicationIdentity.CompanionOwner(helper, metadata) == null);
        File.Copy(assembly, parent);
        Expect(WindowsApplicationIdentity.CompanionOwner(helper, metadata) == parent);
        var unrelated = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location);
        Expect(WindowsApplicationIdentity.CompanionOwner(helper, unrelated) == null);
        var shared = new AudioApplication("runtime#17", Path.Combine("bin", "javaw.exe"), "Java", false);
        var selection = new GameSelection();
        Expect(selection.Choose([shared], shared, new HashSet<string> { shared.Path }) == null);
    }
    finally { root.Delete(true); }
});
Check("Engine evidence and utility exclusions", () =>
{
    var root = Directory.CreateTempSubdirectory();
    try
    {
        string App(string name) => Path.Combine(root.FullName, name + ".exe");
        Expect(!WindowsApplicationIdentity.HasGameSignature(App("Arena")));
        File.WriteAllBytes(Path.Combine(root.FullName, "UnityPlayer.dll"), []);
        Expect(!WindowsApplicationIdentity.HasGameSignature(App("Arena")));
        foreach (var name in new[] { "Arena", "Discord", "Spotify", "Chrome", "GameLauncher" })
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, name + "_Data"));
            Expect(WindowsApplicationIdentity.HasGameSignature(App(name)) == (name == "Arena"));
        }
        Expect(WindowsApplicationIdentity.HasGameSignature(Path.Combine(root.FullName, "Binaries", "Win64", "Rally-Win64-Shipping.exe")));
        Expect(!WindowsApplicationIdentity.HasGameSignature(App("Rally-Win64-Shipping")));
    }
    finally { root.Delete(true); }
});
Check("Gauge sweeps including collapsed arcs at 100%, 125%, and 150% geometry", () =>
{
    var points = new PointF[GaugeArc.Segments + 1];
    foreach (float radius in new[] { 0, 10, 50, 62.5f, 75 })
    foreach (float percent in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1, 0, 0.000001f, 0.3f, 50, 100, 1000 })
    {
        float degrees = GaugeArc.Degrees(percent);
        int count = GaugeArc.Fill(points, new(90, 75), radius, degrees);
        Expect(count is 0 or >= 2 && count <= points.Length);
        if (!float.IsFinite(percent) || percent <= 0.000001f || radius == 0) Expect(count == 0);
        for (int i = 0; i < count; i++)
            Expect(float.IsFinite(points[i].X) && float.IsFinite(points[i].Y) && (i == 0 || points[i] != points[i-1]));
        if (count > 0) Expect(points[count-1] == GaugeArc.Point(new(90, 75), GaugeArc.Start + degrees, radius));
    }
});
Console.WriteLine($"{passed} test groups passed.");

internal sealed class Session
{
    internal readonly string Name;
    internal readonly AudioTarget Target;
    internal int Live, Writes;

    internal Session(string name, int initial, bool engine = false, int channel = -1)
    {
        Name = name;
        Live = initial;
        string path = Path.Combine("apps", name + ".exe");
        Target = new(("speaker", Guid.NewGuid().ToString()), channel, initial, () => Live / 100f,
            value => { Writes++; Live = AudioTarget.Percent(value); }) { App = new(path, path, name, engine) };
    }
}
