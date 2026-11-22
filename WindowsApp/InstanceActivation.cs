using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Voltra;

internal enum Launch { Primary, BackgroundDuplicate, Activated, Unavailable }

internal sealed class InstanceActivation : IDisposable
{
    private const byte ShowWindow = 1;
    private const byte Accepted = 2;
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private readonly CancellationTokenSource closing = new();
    private readonly Task listener;
    private int pending;

    [SupportedOSPlatform("windows")]
    internal static string PipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return $"Voltra.Activate.{identity.User!.Value}.{process.SessionId}";
    }

    internal static Launch ClaimOrNotify(Mutex ownership, string name, bool minimized)
    {
        if (TryOwn(0)) return Launch.Primary;
        if (minimized) return Launch.BackgroundDuplicate;
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (RequestAsync(name, 400).GetAwaiter().GetResult()) return Launch.Activated;
            // The old primary may be closing. This wait also bounds the retry
            // rate if Windows immediately denies the connection.
            if (TryOwn(80)) return Launch.Primary;
        } while (elapsed.ElapsedMilliseconds < 4000);
        return Launch.Unavailable;

        bool TryOwn(int milliseconds)
        {
            try { return ownership.WaitOne(milliseconds); }
            catch (AbandonedMutexException) { return true; }
        }
    }

    internal InstanceActivation(string name) => listener = Task.Run(() => ListenAsync(name));

    // Consumed by the existing WinForms refresh timer, including when the
    // main window is hidden. A request during construction survives until then.
    internal bool TakeRequest() => Interlocked.Exchange(ref pending, 0) != 0;

    private async Task ListenAsync(string name)
    {
        var token = closing.Token;
        using var process = Process.GetCurrentProcess();
        uint sessionId = (uint)process.SessionId;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, Options);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                if (OperatingSystem.IsWindows() &&
                    (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint clientSession) || clientSession != sessionId))
                    continue;
                using var exchange = CancellationTokenSource.CreateLinkedTokenSource(token);
                exchange.CancelAfter(750);
                await ReceiveAsync(pipe, () => Interlocked.Exchange(ref pending, 1), exchange.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Voltra activation listener: {ex.Message}");
                try { await Task.Delay(80, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }

    internal static async Task ReceiveAsync(Stream pipe, Action enqueue, CancellationToken token)
    {
        var message = new byte[1];
        if (await pipe.ReadAsync(message, token).ConfigureAwait(false) != 1 ||
            message[0] != ShowWindow || token.IsCancellationRequested) return;
        enqueue();
        message[0] = Accepted;
        await pipe.WriteAsync(message, token).ConfigureAwait(false);
        // Keep the reply available until the client closes or times out.
        await pipe.ReadAsync(message, token).ConfigureAwait(false);
    }

    private static async Task<bool> RequestAsync(string name, int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, Options,
                TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            // The foreground permission belongs to the process launched by the
            // shortcut. Grant it only to the owner of this connected pipe.
            if (OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid))
                AllowSetForegroundWindow(pid);
            byte[] message = [ShowWindow];
            await pipe.WriteAsync(message, timeout.Token).ConfigureAwait(false);
            return await pipe.ReadAsync(message, timeout.Token).ConfigureAwait(false) == 1 && message[0] == Accepted;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal void Stop() => closing.Cancel();

    public void Dispose()
    {
        Stop();
        listener.GetAwaiter().GetResult();
        closing.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
