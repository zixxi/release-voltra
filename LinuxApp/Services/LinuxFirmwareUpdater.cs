using System.Diagnostics;
using System.Security.Cryptography;
using Voltra;

namespace Voltra.Linux;

internal readonly record struct FirmwareProgress(string Message, int? Percent = null);

internal sealed class LinuxFirmwareUpdater : IDisposable
{
    private readonly UpdateService releases = new();
    private int installing;
    internal bool Installing => Volatile.Read(ref installing) != 0;
    private static string PendingPath => Path.Combine(LinuxSettingsService.Folder, "firmware-update.pending");
    private string? pending = ReadPending();
    internal bool NeedsConfirmation => pending is not null;

    private static string? ReadPending()
    {
        try { return File.Exists(PendingPath) ? File.ReadAllText(PendingPath) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unknown"; }
    }

    internal void ObserveReconnection(DeviceSnapshot snapshot)
    {
        if (Installing || snapshot is not { Connection: ControllerConnection.Connected, Identity: { } device } ||
            pending != $"{device.Mac} {device.Version}" || !FirmwareRelease.Supports(device)) return;
        try { File.Delete(PendingPath); pending = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal Task<FirmwareRelease> CheckAsync(CancellationToken token, Action verifying) =>
        releases.CheckFirmwareAsync(true, token, verifying);

    internal async Task InstallAsync(MainViewModel model, DeviceSnapshot target, FirmwareRelease release,
        Action<FirmwareProgress> report, Func<Task<bool>> confirmWrite, CancellationToken token)
    {
        if (target.Identity is not { } identity || target.UsbSocket is null || !release.CanInstall(identity))
            throw new InvalidOperationException("Connect a compatible Voltra controller with older firmware.");
        if (Interlocked.CompareExchange(ref installing, 1, 0) != 0)
            throw new InvalidOperationException("Another firmware update is already running.");
        DirectoryInfo? work = null;
        try
        {
            work = Directory.CreateTempSubdirectory("voltra-firmware-");
            string image = Path.Combine(work.FullName, "firmware.bin");
            string helper = await LinuxFlashProcess.PrepareAsync(work.FullName, token);
            report(new("Downloading signed firmware…"));
            bool downloading = true;
            IProgress<FirmwareProgress> download = new Progress<FirmwareProgress>(p => { if (downloading) report(p); });
            try
            {
                await releases.DownloadFirmwareAsync(release, identity, image,
                    new InlineProgress(percent => download.Report(new("Downloading signed firmware…", percent))),
                    () => download.Report(new("Verifying firmware SHA-256…")), token);
            }
            finally { downloading = false; }
            report(new("Firmware size and SHA-256 verified. Preparing the controller…"));
            bool resetNeeded;
            using (await model.Device.HoldForFirmwareAsync(target, token))
            {
                resetNeeded = await LinuxFlashProcess.RunAsync(helper, identity, target.UsbSocket, image, release,
                    report, async () =>
                    {
                        if (!await confirmWrite()) return false;
                        token.ThrowIfCancellationRequested();
                        Directory.CreateDirectory(LinuxSettingsService.Folder);
                        string marker = $"{identity.Mac} {release.Version}";
                        await File.WriteAllTextAsync(PendingPath, marker, token);
                        pending = marker;
                        return true;
                    }, token);
            }
            report(new(resetNeeded ? "Flash verified. Tap RESET with BOOT released to reconnect." :
                "Flash verified. Reconnecting and checking the installed version…"));
            var confirmed = await ConfirmAsync(() => model.Device.Snapshot, identity.Mac, release.Version, TimeSpan.FromSeconds(40));
            if (model.Settings.ManualPort is not null && model.Settings.Port != confirmed.Port)
                model.Change(model.Settings with { Port = confirmed.Port! });
            File.Delete(PendingPath);
            pending = null;
        }
        finally
        {
            Interlocked.Exchange(ref installing, 0);
            try { work?.Delete(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(ex.Message); }
        }
    }

    internal static async Task<DeviceSnapshot> ConfirmAsync(Func<DeviceSnapshot> read, string mac, Version expected, TimeSpan limit)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var device = read();
            if (device.Connection == ControllerConnection.Connected && device.Identity is { } current && current.Mac == mac)
            {
                if (!FirmwareRelease.Supports(current) || current.Version != expected)
                    throw new InvalidDataException($"Controller reported {current.Version}. Expected {expected} on the supported layout.");
                return device;
            }
            await Task.Delay(150);
        } while (timer.Elapsed < limit);
        throw new IOException("Installed version unconfirmed. Tap RESET with BOOT released and check the USB connection.");
    }

    private sealed class InlineProgress(Action<int> handle) : IProgress<int>
    {
        public void Report(int value) => handle(value);
    }

    public void Dispose() => releases.Dispose();
}

internal static class LinuxFlashProcess
{
    internal static async Task<string> PrepareAsync(string directory, CancellationToken token)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "tools", "VoltraFlasher");
        string executable = Path.Combine(directory, "VoltraFlasher");
        if (BuildInfo.Get("LinuxFlasherSha256") is not { Length: 64 } hash || !File.Exists(source))
            throw new FileNotFoundException("The Linux flashing helper is missing. Install the complete Linux package.");
        // Validate the actual private copy that will be executed.
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            await input.CopyToAsync(output, token);
            output.Position = 0;
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(output, token), Convert.FromHexString(hash)))
                throw new CryptographicException("The bundled Linux flashing helper failed its SHA-256 check.");
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return executable;
    }

    internal static async Task<bool> RunAsync(string helper, DeviceFirmware device, string socket, string image,
        FirmwareRelease release, Action<FirmwareProgress> report, Func<Task<bool>> authorize, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(helper)
        {
            WorkingDirectory = Path.GetDirectoryName(helper)!, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        foreach (string arg in new[] { device.Mac, image, Convert.ToHexString(release.Hash),
            release.Package.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), socket }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start the Linux flasher.");
        var errors = DrainAsync(process.StandardError);
        using var search = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        search.CancelAfter(TimeSpan.FromSeconds(115));
        using var write = new CancellationTokenSource();
        bool authorized = false, wrote = false, verifying = false, verified = false, reset = false;
        string? failure = null;
        try
        {
            if (await process.StandardOutput.ReadLineAsync(search.Token) != "VOLTRA_PROTOCOL LINUX-1")
                throw new IOException("The bundled Linux flasher could not initialize.");
            while (await process.StandardOutput.ReadLineAsync(authorized ? write.Token : search.Token) is { } line)
            {
                switch (line)
                {
                    case "VOLTRA_STAGE waiting":
                        report(new("Hold BOOT, press and release RESET, then release BOOT. Waiting for the bootloader on the original USB socket…"));
                        break;
                    case "VOLTRA_STAGE target-verified" when !authorized:
                        search.CancelAfter(Timeout.InfiniteTimeSpan);
                        if (!await authorize()) throw new OperationCanceledException(cancel);
                        cancel.ThrowIfCancellationRequested();
                        authorized = true;
                        write.CancelAfter(TimeSpan.FromMinutes(5));
                        await process.StandardInput.WriteLineAsync("WRITE");
                        process.StandardInput.Close();
                        break;
                    case "VOLTRA_STAGE checking-layout" when authorized:
                        report(new("Checking flash capacity and the installed partition table…")); break;
                    case "VOLTRA_STAGE writing" when authorized:
                        wrote = true; report(new("Writing firmware. Keep USB connected.")); break;
                    case "VOLTRA_STAGE verifying-flash" when wrote:
                        verifying = true; report(new("Verifying firmware in flash…")); break;
                    case "VOLTRA_STAGE restarting" when verifying:
                        report(new("Restarting the controller…")); break;
                    case "VOLTRA_STAGE reset-needed" when verifying: reset = true; break;
                    case "VOLTRA_VERIFIED" when verifying: verified = true; break;
                    default:
                        if (line.StartsWith("VOLTRA_ERROR ", StringComparison.Ordinal))
                            failure = line[13..];
                        else if (wrote && !verifying && line.StartsWith("VOLTRA_PROGRESS ", StringComparison.Ordinal) &&
                            int.TryParse(line.AsSpan(16), out int p) && p is >= 0 and <= 100)
                            report(new("Writing firmware. Keep USB connected.", p));
                        break;
                }
            }
            await process.WaitForExitAsync(authorized ? write.Token : search.Token);
            if (!verified || failure is not null || process.ExitCode != 0)
                throw new IOException(failure ?? "The flashing helper exited without verified readback.");
            return reset;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested &&
            (search.IsCancellationRequested || write.IsCancellationRequested))
        { throw new TimeoutException("The flashing helper timed out. Check USB and reset the controller."); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
            await errors;
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line) Console.Error.WriteLine($"Firmware tool: {line}");
    }
}
