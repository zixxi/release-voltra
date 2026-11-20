using System.Diagnostics;
using System.Security.Cryptography;

namespace Voltra;

internal sealed class InnoUpdateInstaller : IDisposable
{
    private static string StageRoot => Path.Combine(MixerSettings.Folder, "Updates");
    private readonly string package;
    private readonly string application;
    private readonly FileStream lockedPackage;
    private bool launched;

    private InnoUpdateInstaller(string package, string application, FileStream lockedPackage)
    {
        this.package = package;
        this.application = application;
        this.lockedPackage = lockedPackage;
    }

    internal static string CreateStage()
    {
        Directory.CreateDirectory(StageRoot);
        foreach (string stage in Directory.EnumerateDirectories(StageRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) continue;
            if (Directory.GetLastWriteTimeUtc(stage) < DateTime.UtcNow.AddDays(-2)) DeleteStage(stage);
        }
        return Directory.CreateDirectory(Path.Combine(StageRoot, Guid.NewGuid().ToString("N"))).FullName;
    }

    internal static async Task<InnoUpdateInstaller> PrepareAsync(VerifiedUpdate update, CancellationToken token)
    {
        string application = WindowsStartup.InstalledExecutable() ??
            throw new InvalidOperationException("Open the installed copy of Voltra to run an automatic update.");
        if (update.Version <= BuildInfo.Version ||
            Path.GetFileName(update.Path) != UpdateService.InstallerAssetName(update.Version))
            throw new InvalidDataException("The installer is for a different version or architecture.");

        // Deny writes and replacement from before the final hash until CreateProcess opens the image.
        var file = new FileStream(update.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);
        try
        {
            if (file.Length != update.Size ||
                !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, token).ConfigureAwait(false),
                    Convert.FromHexString(update.Sha256)))
                throw new CryptographicException("The staged installer does not match its signed SHA-256 digest.");
            token.ThrowIfCancellationRequested();
            return new(update.Path, application, file);
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void Launch()
    {
        var start = new ProcessStartInfo(package)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(package)!
        };
        start.ArgumentList.Add($"/VOLTRAPID={Environment.ProcessId}");
        start.ArgumentList.Add($"/DIR={Path.GetDirectoryName(application)}");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/NORESTARTAPPLICATIONS");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The installer did not start.");
        launched = true;
    }

    internal static void DeleteStage(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lockedPackage.Dispose();
        if (!launched) DeleteStage(Path.GetDirectoryName(package)!);
    }
}
