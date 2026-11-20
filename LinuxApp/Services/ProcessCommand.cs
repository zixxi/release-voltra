using System.Diagnostics;

namespace Voltra.Linux;

internal static class ProcessCommand
{
    internal static Process Start(string command, params string[] arguments)
    {
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException($"Could not start {command}.");
    }

    internal static async Task<string> RunAsync(string command, CancellationToken token, params string[] arguments)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(4));
        using var process = Start(command, arguments);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            string stderr = await errors;
            if (process.ExitCode != 0) throw new IOException(stderr.Trim() is { Length: > 0 } message
                ? $"{command}: {message}" : $"{command} exited with code {process.ExitCode}.");
            return await output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"{command} timed out.");
        }
        finally { Stop(process); }
    }

    internal static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}
