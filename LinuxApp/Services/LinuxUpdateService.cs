using System.Diagnostics;
using System.Globalization;
using Voltra;

namespace Voltra.Linux;

internal static class LinuxUpdateService
{
    internal static string Repository => BuildInfo.Get("UpdateRepository")!;
    internal static Version Version => BuildInfo.Version;
    internal static string DisplayVersion => Version.ToString();
    internal static string DirectoryName => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    internal static bool Portable => Environment.ProcessPath == Path.Combine(DirectoryName, "LinuxApp") &&
        File.Exists(Path.Combine(DirectoryName, "voltra-portable.json")) && File.Exists(Path.Combine(DirectoryName, "update-install.py"));

    internal static async Task<UpdateRelease?> CheckAsync(CancellationToken token)
    {
        using var service = new UpdateService();
        return await service.CheckLinuxAsync(token);
    }

    internal static async Task<PreparedUpdate> PrepareAsync(UpdateRelease release, IProgress<int> progress,
        Action unpacking, CancellationToken token)
    {
        if (!Portable) throw new IOException("Automatic installation requires the extracted portable release in a user-owned directory.");
        await ProcessCommand.RunAsync("/usr/bin/python3", token, "-I", Path.Combine(DirectoryName, "update-install.py"),
            "check", DirectoryName, DisplayVersion);
        var temp = Directory.CreateTempSubdirectory("voltra-download-");
        try
        {
            using var service = new UpdateService();
            var verified = await service.DownloadLinuxAsync(release, Path.Combine(temp.FullName, "archive"), progress, token);
            token.ThrowIfCancellationRequested();
            unpacking();
            // Use the installer from this running release. The downloaded tree is data until commit.
            var info = new ProcessStartInfo("/usr/bin/python3")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "-I", Path.Combine(DirectoryName, "update-install.py"), "install", DirectoryName,
                DisplayVersion, verified.Path, verified.Sha256, verified.Version.ToString(), Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
                info.ArgumentList.Add(arg);
            var helper = Process.Start(info) ?? throw new IOException("Could not start the installer.");
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                if (await helper.StandardOutput.ReadLineAsync(deadline.Token) != "READY")
                {
                    string error = (await helper.StandardError.ReadToEndAsync(deadline.Token)).Trim();
                    throw new IOException(error.Length == 0 ? "Could not prepare the update." : error);
                }
                token.ThrowIfCancellationRequested();
                return new PreparedUpdate(helper);
            }
            catch
            {
                helper.StandardInput.Close(); // EOF cancels before commit and cleans staging.
                helper.Dispose();
                throw;
            }
        }
        finally
        {
            try { temp.Delete(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"Could not remove temporary update files: {ex.Message}"); }
        }
    }

    internal static bool UpdateLocked()
    {
        if (!OperatingSystem.IsLinux() || !Portable || HasStartupTicket() || Environment.GetEnvironmentVariable("VOLTRA_UPDATE_ROLLBACK") == "1") return false;
        string path = Path.Combine(Path.GetDirectoryName(DirectoryName)!, "." + Path.GetFileName(DirectoryName) + ".update.lock");
        if (!File.Exists(path)) return false;
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file.Lock(0, 1);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasStartupTicket()
    {
        if (Environment.GetEnvironmentVariable("VOLTRA_UPDATE_VERSION") != DisplayVersion ||
            Environment.GetEnvironmentVariable("VOLTRA_UPDATE_ACK") is not { } ack || !Path.IsPathFullyQualified(ack)) return false;
        var job = Directory.GetParent(ack);
        return Path.GetFileName(ack) == "started" && job is not null && job.Parent?.FullName == Path.GetDirectoryName(DirectoryName) &&
            job.Name.StartsWith("." + Path.GetFileName(DirectoryName) + ".update-", StringComparison.Ordinal);
    }

    internal static string? StartupResult()
    {
        if (Environment.GetEnvironmentVariable("VOLTRA_UPDATE_ROLLBACK") == "1")
        {
            Environment.SetEnvironmentVariable("VOLTRA_UPDATE_ROLLBACK", null);
            return "The updated application could not start. Voltra restored the previous version.";
        }
        if (!HasStartupTicket()) return null;
        LinuxSettingsService.AtomicWrite(Environment.GetEnvironmentVariable("VOLTRA_UPDATE_ACK")!, DisplayVersion);
        Environment.SetEnvironmentVariable("VOLTRA_UPDATE_ACK", null);
        Environment.SetEnvironmentVariable("VOLTRA_UPDATE_VERSION", null);
        return $"Updated to Voltra {DisplayVersion}.";
    }
}

internal sealed class PreparedUpdate(Process helper) : IDisposable
{
    internal void Commit()
    {
        if (helper.HasExited) throw new IOException("The prepared update expired. Check for updates again.");
        helper.StandardInput.WriteLine("COMMIT");
        helper.StandardInput.Flush();
    }
    public void Dispose() { helper.StandardInput.Close(); helper.Dispose(); }
}
