using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using Voltra;

// Exercise the production ownership and activation exchange using real child
// processes, without opening a serial port, audio session or user settings.
if (args is [string endpoint, string kind])
{
    using var instance = new Mutex(false, endpoint);
    Console.WriteLine("ready");
    var result = InstanceActivation.ClaimOrNotify(instance, endpoint, kind == "background");
    if (result == Launch.Primary) instance.ReleaseMutex();
    return (int)result;
}

string name = $"Voltra.test.{Guid.NewGuid():N}";
using var ownership = new Mutex(false, name);
Expect(InstanceActivation.ClaimOrNotify(ownership, name, false) == Launch.Primary, "primary ownership");
using (var quiet = Start("background"))
    Expect(Result(quiet) == Launch.BackgroundDuplicate, "second process respects mutex without opening UI");
int requested = 0;
foreach (int command in new[] { 1, 0, 2, 99, -1 })
{
    using var buffer = new MemoryStream();
    if (command >= 0) buffer.WriteByte((byte)command);
    buffer.Position = 0;
    InstanceActivation.ReceiveAsync(buffer, () => requested++, CancellationToken.None).GetAwaiter().GetResult();
    Expect(requested == 1 && (command == 1 ? buffer.ToArray().SequenceEqual(new byte[] { 1, 2 })
        : buffer.Length == (command < 0 ? 0 : 1)), $"activation protocol {command}");
}
if (args is ["--offline"])
{
    ownership.ReleaseMutex();
    using var fresh = Start();
    Expect(Result(fresh) == Launch.Primary, "fresh process takes ownership after exit");
    return 0;
}
using (var early = Start())
{
    // The second process is already running before the listener is ready.
    using var activation = new InstanceActivation(name);
    Expect(Result(early) == Launch.Activated && activation.TakeRequest(), "early launch retained");
    Expect(!activation.TakeRequest(), "request consumed once");
    using var background = Start("background");
    Expect(Result(background) == Launch.BackgroundDuplicate && !activation.TakeRequest(), "quiet autostart");

    using (var invalid = new NamedPipeClientStream(".", name, PipeDirection.InOut))
    {
        invalid.Connect(2000);
        invalid.WriteByte(99);
        Expect(invalid.ReadByte() == -1, "unknown command rejected");
    }
    Expect(!activation.TakeRequest(), "invalid request does not activate");

    var burst = Enumerable.Range(0, 8).Select(_ => Start(waitReady: false)).ToArray();
    try
    {
        Expect(burst.All(child => Result(child) == Launch.Activated), "eight concurrent secondaries exit");
        Expect(activation.TakeRequest() && !activation.TakeRequest(), "burst coalesced");
    }
    finally { foreach (var child in burst) child.Dispose(); }

    using var stalled = new NamedPipeClientStream(".", name, PipeDirection.InOut);
    stalled.Connect(2000);
    using var next = Start();
    Expect(Result(next) == Launch.Activated && activation.TakeRequest(), "stalled client times out");
}
using (var closing = Start())
{
    ownership.ReleaseMutex();
    Expect(Result(closing) == Launch.Primary, "relaunch during shutdown acquires ownership");
}
Expect(InstanceActivation.ClaimOrNotify(ownership, name, false) == Launch.Primary, "restart");
using (var noListener = Start())
    Expect(Result(noListener) == Launch.Unavailable, "IPC failure exits without another primary");
ownership.ReleaseMutex();
return 0;

Process Start(string kind = "explicit", bool waitReady = true)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true };
    if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(name);
    start.ArgumentList.Add(kind);
    var child = Process.Start(start)!;
    if (waitReady && child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() != "ready")
        throw new InvalidOperationException("Child did not start");
    return child;
}

static Launch Result(Process child)
{
    if (!child.WaitForExit(10000)) throw new TimeoutException("Child did not exit");
    return (Launch)child.ExitCode;
}

static void Expect(bool result, string check)
{
    if (!result) throw new InvalidOperationException(check);
    Console.WriteLine($"PASS {check}");
}
