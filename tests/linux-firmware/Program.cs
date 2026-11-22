using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Voltra;
using Voltra.Linux;

using var fixture = Assembly.GetExecutingAssembly().GetManifestResourceStream("Voltra.Linux.FirmwareTests.signed-release.json")!;
using var signed = JsonDocument.Parse(fixture);
var manifest = signed.RootElement.GetProperty("manifest").GetBytesFromBase64();
var signature = signed.RootElement.GetProperty("signature").GetBytesFromBase64();
string name = "Voltra-S3-Zero-Firmware-0.1.1.bin";
byte[] image = args.Length == 1 ? await File.ReadAllBytesAsync(args[0]) : new byte[417632];
var assets = new[] { new UpdateAsset(1, "firmware-manifest.json", manifest.Length),
    new UpdateAsset(2, "firmware-manifest.json.sig", signature.Length), new UpdateAsset(3, name, image.Length) };
byte[] Metadata(IEnumerable<UpdateAsset> items) => JsonSerializer.SerializeToUtf8Bytes(new
{
    tag_name = "firmware-s3-zero", draft = false, prerelease = false,
    assets = items.Select(a => new { state = "uploaded", id = a.Id, name = a.Name, size = a.Size })
});
var replies = new Dictionary<string, byte[]> { ["firmware-s3-zero"] = Metadata(assets), ["1"] = manifest, ["2"] = signature, ["3"] = image };
using var service = new UpdateService(BuildInfo.Get("UpdateRepository"), BuildInfo.Get("UpdatePublicKey"), new FixtureHandler(replies));
var release = await service.CheckFirmwareAsync(true, default);
var device = new DeviceFirmware("VOLTRA-S3-ZERO", new(0,1,0), 1, "001122334455", 4194304, 65536, 65536);
void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine($"PASS {label}"); }
async Task Reject<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); } catch (T) { Console.WriteLine($"PASS rejects {label}"); return; }
    throw new Exception($"Accepted {label}");
}
Check(release.CanInstall(device) && !release.CanInstall(device with { Version = release.Version }), "production PSS signature and monotonic version");
Check(!release.CanInstall(device with { Hardware = "other" }) && !release.CanInstall(device with { BootOffset = 0 }), "incompatible controller and partition");
foreach (var (key, value) in new[] { ("hardware", (JsonNode)"other"), ("appOffset", (JsonNode)0), ("protocolVersion",(JsonNode)2) })
{
    var json = JsonNode.Parse(manifest)!; json[key] = value;
    await Reject<InvalidDataException>(() => Task.FromResult(service.ParseVerifiedManifest(JsonSerializer.SerializeToUtf8Bytes(json), assets)), key);
}
replies["2"] = new byte[signature.Length];
await Reject<CryptographicException>(() => service.CheckFirmwareAsync(true, default), "invalid signature");
replies["2"] = signature;
replies["firmware-s3-zero"] = Metadata(assets.Take(2));
await Reject<InvalidDataException>(() => service.CheckFirmwareAsync(true, default), "missing asset");
replies["firmware-s3-zero"] = Metadata(assets.Append(assets[0]));
await Reject<InvalidDataException>(() => service.CheckFirmwareAsync(true, default), "duplicate asset");
replies["firmware-s3-zero"] = Metadata(assets);
using (var slow = new UpdateService(BuildInfo.Get("UpdateRepository"), BuildInfo.Get("UpdatePublicKey"), new TimeoutHandler()))
using (var timeout = new CancellationTokenSource(50))
    await Reject<OperationCanceledException>(() => slow.CheckFirmwareAsync(true, timeout.Token), "network deadline");
var tmp = Directory.CreateTempSubdirectory();
try
{
    string file = Path.Combine(tmp.FullName, "app.bin");
    if (args.Length == 1)
    {
        await service.DownloadFirmwareAsync(release, device, file, new Progress<int>(), () => {}, default);
        Check(File.ReadAllBytes(file).SequenceEqual(image), "exact signed release binary");
        File.Delete(file);
    }
    replies["3"] = new byte[image.Length];
    await Reject<CryptographicException>(() => service.DownloadFirmwareAsync(release, device, file, new Progress<int>(), () => {}, default), "incorrect SHA-256");
    replies["3"] = [1];
    await Reject<InvalidDataException>(() => service.DownloadFirmwareAsync(release, device, file, new Progress<int>(), () => {}, default), "short package");
    Check(!File.Exists(file), "failed downloads leave no installable image");
    if (OperatingSystem.IsLinux())
    {
        string exe = Path.Combine(tmp.FullName, "mock-helper");
        async Task Script(string body)
        {
            await File.WriteAllTextAsync(exe, "#!/bin/sh\necho 'VOLTRA_PROTOCOL LINUX-1'\n" + body);
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Task<bool> Run(Func<Task<bool>> confirm) => LinuxFlashProcess.RunAsync(exe, device, "/sys/devices/test", file, release, _ => {}, confirm, default);
        await Script("echo 'VOLTRA_ERROR Incorrect ROM MAC'\nexit 1\n");
        await Reject<IOException>(() => Run(() => throw new Exception("unexpected write prompt")), "helper identity failure");
        await Script("echo VOLTRA_VERIFIED\n");
        await Reject<IOException>(() => Run(() => throw new Exception()), "unacknowledged write");
        await Script("echo 'VOLTRA_STAGE target-verified'\nread cmd || exit 1\n[ \"$cmd\" = WRITE ] || exit 1\necho 'VOLTRA_STAGE writing'\necho 'VOLTRA_STAGE verifying-flash'\necho VOLTRA_VERIFIED\n");
        await Reject<OperationCanceledException>(() => Run(() => Task.FromResult(false)), "canceled write");
        int confirmations = 0;
        Check(!await Run(() => Task.FromResult(++confirmations == 1)) && confirmations == 1, "explicit consent and verified helper result");
    }
    var current = new DeviceSnapshot("Connected", "", [], Port:"/dev/ttyACM1", Connection:ControllerConnection.Connected,
        Identity:device with { Version = release.Version });
    Check(await LinuxFirmwareUpdater.ConfirmAsync(() => current, device.Mac, release.Version, TimeSpan.FromSeconds(1)) == current, "same MAC and expected version after reset");
    await Reject<InvalidDataException>(() => LinuxFirmwareUpdater.ConfirmAsync(() => current, device.Mac, new(9,9,9), TimeSpan.FromSeconds(1)), "wrong installed version");
    await Reject<IOException>(() => LinuxFirmwareUpdater.ConfirmAsync(() => current with { Connection=ControllerConnection.Disconnected }, device.Mac, release.Version, TimeSpan.Zero), "disconnected after write");
}
finally { tmp.Delete(true); }

sealed class FixtureHandler(Dictionary<string, byte[]> replies) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(replies[request.RequestUri!.Segments[^1]]) });
}
sealed class TimeoutHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
}
