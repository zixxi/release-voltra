using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Voltra;

internal static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;
    private static readonly Dictionary<string, string?> Metadata = Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(item => item.Key, item => item.Value);

    internal static Version Version { get; } = Assembly.GetName().Version is { } version
        ? new(version.Major, version.Minor, version.Build)
        : throw new InvalidOperationException("Build version is missing.");

    internal static string? Get(string key) => Metadata.GetValueOrDefault(key)?.Trim();
}

internal sealed record UpdateAsset(long Id, string Name, long Size);
internal sealed record UpdateRelease(Version Version, string Notes, UpdateAsset? Package, UpdateAsset? Manifest, UpdateAsset? Signature);
internal sealed record VerifiedUpdate(Version Version, string Path, string Sha256);

/// <summary>Future signed installer handoff. A successful launch must wait for Voltra to exit
/// before replacing any files, and report installation failures without removing the current version.</summary>
internal interface IUpdateInstaller
{
    void Launch(VerifiedUpdate update, int currentProcessId, string applicationPath);
}

internal sealed partial class UpdateService : IDisposable
{
    private const int MetadataLimit = 1024 * 1024;
    private const long PackageLimit = 1024L * 1024 * 1024;
    private static readonly Regex RepositoryFormat = new(@"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z");
    private static readonly Regex StableVersion = new(@"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z");
    private readonly HttpClient http;
    private readonly string? repository;
    private readonly string? publicKey;
    private long retryAfterUnixSeconds;

    internal UpdateService() : this(BuildInfo.Get("UpdateRepository"), BuildInfo.Get("UpdatePublicKey"),
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    internal UpdateService(string? repository, string? publicKey, HttpMessageHandler handler)
    {
        this.repository = repository is not null && RepositoryFormat.IsMatch(repository) &&
            repository.Split('/')[1] is not ("." or "..") ? repository : null;
        this.publicKey = publicKey;
        http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Voltra/{BuildInfo.Version}");
    }

    internal Uri? RepositoryUrl => repository is null ? null : new($"https://github.com/{repository}");
    internal bool CanDownload(UpdateRelease release) => repository is not null && !string.IsNullOrWhiteSpace(publicKey) &&
        release.Package is not null && release.Manifest is not null && release.Signature is not null;

    internal async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        if (repository is null) throw new InvalidOperationException("Update repository is not configured.");
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < Volatile.Read(ref retryAfterUnixSeconds))
            throw new InvalidOperationException("GitHub rate limited requests. Try again later.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await GetAsync(Api("latest"), binary: false, timeout.Token).ConfigureAwait(false);
        byte[] json = await ReadSmallAsync(response, MetadataLimit, timeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("tag_name").GetString() == FirmwareReleaseTag)
        {
            // Recover when the dedicated firmware release was accidentally marked as latest.
            using var recent = await GetAsync(new Uri($"https://api.github.com/repos/{repository}/releases?per_page=30"),
                binary: false, timeout.Token).ConfigureAwait(false);
            using var listing = JsonDocument.Parse(await ReadSmallAsync(recent, MetadataLimit, timeout.Token).ConfigureAwait(false));
            root = listing.RootElement.EnumerateArray()
                .Where(item => !item.GetProperty("draft").GetBoolean() && !item.GetProperty("prerelease").GetBoolean())
                .Select(item => (Release: item, Version: TryStableVersion(item.GetProperty("tag_name").GetString() ?? "", out var v) ? v : null))
                .Where(item => item.Version is not null).OrderByDescending(item => item.Version)
                .FirstOrDefault().Release;
            if (root.ValueKind == JsonValueKind.Undefined) return null;
            root = root.Clone();
        }
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!StableVersion.IsMatch(tag) || !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new InvalidDataException("GitHub returned a release with an invalid version tag.");
        if (version <= BuildInfo.Version) return null;

        string? architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => null
        };
        string? name = architecture is null ? null : $"Voltra-{version}-win-{architecture}.exe";
        var assets = ReadUploadedAssets(root);
        UpdateAsset? Find(string? assetName) => assets.SingleOrDefault(asset => asset.Name == assetName);
        return new(version, root.GetProperty("body").GetString() ?? "",
            Find(name), Find(name + ".json"), Find(name + ".json.sig"));
    }

    internal async Task<VerifiedUpdate> DownloadAsync(UpdateRelease release, string destination, IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        if (release.Version <= BuildInfo.Version || !CanDownload(release))
            throw new InvalidOperationException("A verified update package is unavailable.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var token = timeout.Token;
        var package = release.Package!;
        var manifest = await ReadAssetAsync(release.Manifest!, 8192, token).ConfigureAwait(false);
        var signature = await ReadAssetAsync(release.Signature!, 1024, token).ConfigureAwait(false);
        VerifySignature(manifest, signature);

        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        long length = root.GetProperty("size").GetInt64();
        if (root.GetProperty("product").GetString() != "Voltra" ||
            root.GetProperty("repository").GetString() != repository ||
            root.GetProperty("version").GetString() != release.Version.ToString() ||
            root.GetProperty("asset").GetString() != package.Name ||
            length != package.Size || length is <= 0 or > PackageLimit)
            throw new InvalidDataException("The signed manifest does not match this update.");
        byte[] expectedHash = Convert.FromHexString(root.GetProperty("sha256").GetString() ?? "");
        if (expectedHash.Length != 32) throw new InvalidDataException("The signed package hash is invalid.");

        await DownloadVerifiedAssetAsync(package, expectedHash, destination, progress, token).ConfigureAwait(false);
        return new(release.Version, Path.GetFullPath(destination), Convert.ToHexString(expectedHash));
    }

    private async Task DownloadVerifiedAssetAsync(UpdateAsset package, byte[] expectedHash, string destination,
        IProgress<int> progress, CancellationToken token, Action? verifying = null)
    {
        long length = package.Size;
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("Choose a new filename for the verified package.");

        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".voltra-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var response = await GetAsync(AssetUri(package), binary: true, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long header && header != length)
                throw new InvalidDataException("The package size does not match the manifest.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            long received = 0;
            int lastPercent = -1;
            byte[] buffer = new byte[65536];
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                while (true)
                {
                    int count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > length) throw new InvalidDataException("The package exceeds its signed size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    int percent = (int)(received * 100 / length);
                    if (percent != lastPercent) progress.Report(lastPercent = percent);
                }
            }
            verifying?.Invoke();
            if (received != length || !CryptographicOperations.FixedTimeEquals(expectedHash, hash.GetHashAndReset()))
                throw new CryptographicException("The package does not match its signed SHA-256 digest.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static UpdateAsset[] ReadUploadedAssets(JsonElement root) =>
        root.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("state").GetString() == "uploaded")
            .Select(asset => new UpdateAsset(asset.GetProperty("id").GetInt64(),
                asset.GetProperty("name").GetString() ?? "", asset.GetProperty("size").GetInt64()))
            .Where(asset => asset.Id > 0 && asset.Size > 0).ToArray();
    private void VerifySignature(byte[] manifest, byte[] signature)
    {
        using var rsa = RSA.Create();
        byte[] key = Convert.FromBase64String(publicKey!);
        rsa.ImportSubjectPublicKeyInfo(key, out int keyLength);
        if (keyLength != key.Length || rsa.KeySize < 2048 ||
            !rsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("The update manifest signature is invalid.");
    }

    private Uri Api(string path) => new($"https://api.github.com/repos/{repository}/releases/{path}");
    private Uri AssetUri(UpdateAsset asset) => Api($"assets/{asset.Id.ToString(CultureInfo.InvariantCulture)}");

    private async Task<byte[]> ReadAssetAsync(UpdateAsset asset, int limit, CancellationToken token)
    {
        if (asset.Size > limit) throw new InvalidDataException("The release metadata asset is too large.");
        using var response = await GetAsync(AssetUri(asset), binary: true, token).ConfigureAwait(false);
        byte[] bytes = await ReadSmallAsync(response, limit, token).ConfigureAwait(false);
        return bytes.LongLength == asset.Size ? bytes : throw new InvalidDataException("The release asset size is invalid.");
    }

    private static async Task<byte[]> ReadSmallAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The GitHub response is too large.");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > limit) throw new InvalidDataException("The GitHub response is too large.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, bool binary, CancellationToken token)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < Volatile.Read(ref retryAfterUnixSeconds))
            throw new InvalidOperationException("GitHub rate limited requests. Try again later.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd(binary ? "application/octet-stream" : "application/vnd.github+json");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (binary && response.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
        {
            var redirect = response.Headers.Location;
            response.Dispose();
            if (redirect is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } ||
                redirect.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("GitHub returned an unexpected asset destination.");
            response = await http.GetAsync(redirect, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (response.StatusCode == HttpStatusCode.Forbidden &&
                 (response.Headers.RetryAfter is not null || Header(response, "X-RateLimit-Remaining") == "0")))
            {
                var now = DateTimeOffset.UtcNow;
                var retryAfter = response.Headers.RetryAfter?.Date ?? now.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(2));
                if (response.Headers.RetryAfter is null &&
                    long.TryParse(Header(response, "X-RateLimit-Reset"), out long reset) &&
                    reset is > 0 and <= 253402300799)
                    retryAfter = DateTimeOffset.FromUnixTimeSeconds(reset);
                if (retryAfter < now) retryAfter = now.AddMinutes(1);
                Volatile.Write(ref retryAfterUnixSeconds, retryAfter.ToUnixTimeSeconds());
                throw new InvalidOperationException("GitHub rate limited requests. Try again later.");
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("GitHub could not find a public release or asset in the configured repository.");
            throw new HttpRequestException($"GitHub returned HTTP {(int)response.StatusCode}.");
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public void Dispose() => http.Dispose();
}
