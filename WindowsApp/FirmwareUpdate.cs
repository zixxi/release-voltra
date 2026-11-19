using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Voltra;

internal sealed record DeviceFirmware(string Hardware, Version Version, int Protocol, string Mac, int FlashSize,
    int RunningOffset, int BootOffset)
{
    internal static DeviceFirmware? Parse(string line, string nonce)
    {
        if (!line.StartsWith("VOLTRA,INFO,", StringComparison.Ordinal)) return null;
        string[] fields = line.Split(',');
        if (fields.Length != 10 || fields[2] != nonce || fields[3].Length is < 1 or > 32 ||
            !UpdateService.TryStableVersion(fields[4], out var version) ||
            !int.TryParse(fields[5], out int protocol) || !Regex.IsMatch(fields[6], @"\A[0-9A-F]{12}\z") ||
            !int.TryParse(fields[7], out int size) || !int.TryParse(fields[8], out int running) ||
            !int.TryParse(fields[9], out int boot)) return null;
        return new(fields[3], version, protocol, fields[6], size, running, boot);
    }
}

internal sealed record FirmwareRelease(Version Version, Version MinimumAppVersion, string Notes, UpdateAsset Package,
    byte[] Hash)
{
    internal const string Hardware = "VOLTRA-S3-ZERO";
    internal const int Protocol = 1;
    internal const int FlashSize = 0x400000;
    internal const int ApplicationOffset = 0x10000;
    internal const int ApplicationCapacity = 0x140000;
    internal const int PartitionTableOffset = 0x8000;
    internal const string PartitionTableHash = "148b959cbff1c38aa8e1d5c0ba9d612c54997b945e56a63f41223eef650653a1";

    internal static bool Supports(DeviceFirmware device) =>
        device.Hardware == Hardware && device.Protocol == Protocol && device.FlashSize == FlashSize &&
        device.RunningOffset == ApplicationOffset && device.BootOffset == ApplicationOffset;

    internal bool CanInstall(DeviceFirmware device) =>
        Supports(device) && Version > device.Version && MinimumAppVersion <= BuildInfo.Version;
}

internal sealed partial class UpdateService
{
    private const string FirmwareReleaseTag = "firmware-s3-zero";
    private const string ManifestName = "firmware-manifest.json";
    private FirmwareRelease? cachedFirmware;
    private DateTimeOffset firmwareCheckedAt;

    internal bool FirmwareConfigured => repository is not null && !string.IsNullOrWhiteSpace(publicKey);
    internal static bool TryStableVersion(string text, out Version version)
    {
        version = new(0, 0, 0);
        if (!StableVersion.IsMatch(text) || !Version.TryParse(text.TrimStart('v'), out var parsed)) return false;
        version = parsed;
        return true;
    }

    internal async Task<FirmwareRelease> CheckFirmwareAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!FirmwareConfigured) throw new InvalidOperationException("Firmware signing key is not configured.");
        if (!refresh && cachedFirmware is not null && DateTimeOffset.UtcNow - firmwareCheckedAt < TimeSpan.FromHours(6))
            return cachedFirmware;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var response = await GetAsync(Api("tags/" + FirmwareReleaseTag), binary: false, token).ConfigureAwait(false);
        using var release = JsonDocument.Parse(await ReadSmallAsync(response, MetadataLimit, token).ConfigureAwait(false));
        var root = release.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() ||
            root.GetProperty("tag_name").GetString() != FirmwareReleaseTag)
            throw new InvalidDataException("The firmware release is not a stable release.");

        var assets = ReadUploadedAssets(root);
        UpdateAsset Required(string name) => assets.SingleOrDefault(asset => asset.Name == name)
            ?? throw new InvalidDataException($"The firmware release is missing {name}.");

        var manifest = await ReadAssetAsync(Required(ManifestName), 16384, token).ConfigureAwait(false);
        var signature = await ReadAssetAsync(Required(ManifestName + ".sig"), 1024, token).ConfigureAwait(false);
        VerifySignature(manifest, signature);
        using var signed = JsonDocument.Parse(manifest);
        var fields = signed.RootElement;
        if (fields.GetProperty("product").GetString() != "Voltra" ||
            fields.GetProperty("repository").GetString() != repository ||
            fields.GetProperty("hardware").GetString() != FirmwareRelease.Hardware ||
            fields.GetProperty("protocolVersion").GetInt32() != FirmwareRelease.Protocol ||
            fields.GetProperty("flashSize").GetInt32() != FirmwareRelease.FlashSize ||
            fields.GetProperty("appOffset").GetInt32() != FirmwareRelease.ApplicationOffset ||
            fields.GetProperty("appCapacity").GetInt32() != FirmwareRelease.ApplicationCapacity ||
            fields.GetProperty("partitionTableOffset").GetInt32() != FirmwareRelease.PartitionTableOffset ||
            fields.GetProperty("partitionTableSha256").GetString() != FirmwareRelease.PartitionTableHash ||
            !TryStableVersion(fields.GetProperty("firmwareVersion").GetString() ?? "", out var version) ||
            !TryStableVersion(fields.GetProperty("minimumAppVersion").GetString() ?? "", out var minimumApp))
            throw new InvalidDataException("The signed firmware targets a different controller or flash layout.");
        string packageName = $"Voltra-S3-Zero-Firmware-{version}.bin";
        var package = Required(packageName);
        byte[] hash = Convert.FromHexString(fields.GetProperty("sha256").GetString() ?? "");
        if (hash.Length != 32 || fields.GetProperty("asset").GetString() != packageName ||
            fields.GetProperty("size").GetInt64() != package.Size || package.Size > FirmwareRelease.ApplicationCapacity)
            throw new InvalidDataException("The signed firmware image metadata is invalid.");
        var notes = fields.TryGetProperty("notes", out var text) ? text.GetString() ?? "" : "";
        if (cachedFirmware is not null && version < cachedFirmware.Version)
            throw new InvalidDataException("GitHub returned an older signed firmware release.");
        cachedFirmware = new(version, minimumApp, notes, package, hash);
        firmwareCheckedAt = DateTimeOffset.UtcNow;
        return cachedFirmware;
    }

    internal async Task DownloadFirmwareAsync(FirmwareRelease release, DeviceFirmware device, string path,
        IProgress<int> progress, CancellationToken token)
    {
        if (!release.CanInstall(device) || !ReferenceEquals(release, cachedFirmware))
            throw new InvalidOperationException("There is no newer compatible firmware for this controller.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await DownloadVerifiedAssetAsync(release.Package, release.Hash, path, progress, timeout.Token).ConfigureAwait(false);
    }

}

internal static class FirmwareFlasher
{
    private static string ToolPath => Path.Combine(AppContext.BaseDirectory, "tools", "VoltraFlasher.exe");
    private static string? ToolHash => BuildInfo.Get("FirmwareFlasherSha256");
    internal static bool IsPackaged => ToolHash is { Length: 64 } && File.Exists(ToolPath);

    internal static async Task VerifyToolAsync(CancellationToken token)
    {
        if (!IsPackaged) throw new InvalidOperationException("This Voltra build does not include the firmware flashing tool.");
        await using var stream = File.OpenRead(ToolPath);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(ToolHash!)))
            throw new CryptographicException("The bundled flashing tool failed its integrity check.");
    }

    internal static async Task RunAsync(DeviceFirmware device, FirmwareRelease release, string path,
        IProgress<string> progress)
    {
        if (!release.CanInstall(device))
            throw new InvalidOperationException("Firmware is incompatible with this controller.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await VerifyToolAsync(timeout.Token).ConfigureAwait(false);
        var args = new ProcessStartInfo(ToolPath)
        {
            WorkingDirectory = Path.GetDirectoryName(ToolPath)!, CreateNoWindow = true,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { device.Mac, path, Convert.ToHexString(release.Hash) })
            args.ArgumentList.Add(arg);
        using var process = Process.Start(args) ?? throw new InvalidOperationException("Unable to start the flashing tool.");
        string? failure = null;
        bool verified = false;
        try
        {
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("VOLTRA_STATUS ", StringComparison.Ordinal)) progress.Report(line[14..]);
                else if (line.StartsWith("VOLTRA_ERROR ", StringComparison.Ordinal)) failure = line[13..];
                else if (line == "VOLTRA_VERIFIED") verified = true;
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !verified)
                throw new IOException(failure ?? "The firmware tool stopped before verifying the flash. " + (await error.ConfigureAwait(false)).Trim());
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            throw new IOException("The firmware tool timed out. The controller may need recovery.");
        }
    }
}
