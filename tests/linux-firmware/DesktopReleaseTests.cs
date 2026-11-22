using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Voltra;

internal static class DesktopReleaseTests
{
    internal static async Task RunAsync()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            string repo = "zixxi/release-voltra";
            var next = new Version(8, 7, 6);
            string linux = UpdateService.LinuxAssetName(next)!;
            string win = UpdateService.InstallerAssetName(next)!;
            byte[] blob = "test package"u8.ToArray();
            using var rsa = RSA.Create(2048); // ephemeral test identity, never exported to disk
            var data = new Dictionary<string, byte[]>();
            using var service = new UpdateService(repo, Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), new FixtureHandler(data));
            var entries = new List<object>();
            int id = 0;
            void Asset(string name, byte[] bytes)
            {
                data[(++id).ToString()] = bytes;
                entries.Add(new { id, name, size = bytes.Length, state = "uploaded" });
            }
            void Latest(string tag, bool pre = false) => data["latest"] = JsonSerializer.SerializeToUtf8Bytes(new
                { tag_name = tag, prerelease = pre, draft = false, body = "Notes", assets = entries });
            foreach (string name in new[] { linux, win })
            {
                byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { product="Voltra", repository=repo, version=next.ToString(),
                    asset=name, size=blob.Length, sha256=Convert.ToHexString(SHA256.HashData(blob)) });
                Asset(name, blob);
                Asset(name + ".json", manifest);
                Asset(name + ".json.sig", rsa.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            }
            Asset("Voltra-S3-Zero-Firmware-0.1.1.bin", blob);
            Latest("v" + next);
            var lin = (await service.CheckLinuxAsync(default))!;
            var windows = (await service.CheckAsync(default))!;
            void Assert(bool ok, string message)
            { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
            Assert(lin.Package?.Name == linux && windows.Package?.Name == win, "unified release chooses platform by exact name");
            string file = Path.Combine(temp.FullName, "download");
            var progress = new Progress<int>();
            await service.DownloadLinuxAsync(lin, file, progress, default);
            Assert(File.ReadAllBytes(file).SequenceEqual(blob), "signed Linux manifest and archive verified");
            File.Delete(file);
            await service.DownloadAsync(windows, file, progress, default);
            Assert(File.ReadAllBytes(file).SequenceEqual(blob), "signed Windows installer selected from mixed release");
            File.Delete(file);
            async Task Reject<T>(Func<Task> operation, string label) where T : Exception
            {
                try { await operation(); } catch (T) { Assert(!File.Exists(file), label); return; }
                throw new Exception(label);
            }
            await Reject<InvalidOperationException>(() => service.DownloadLinuxAsync(windows, file, progress, default), "Windows package rejected by Linux");
            data["1"] = new byte[blob.Length];
            await Reject<CryptographicException>(() => service.DownloadLinuxAsync(lin, file, progress, default), "tampered Linux archive rejected");
            data["3"] = new byte[data["3"].Length];
            await Reject<CryptographicException>(() => service.DownloadLinuxAsync(lin, file, progress, default), "tampered Linux signature rejected");
            Latest("v" + next, true);
            Assert(await service.CheckLinuxAsync(default) == null, "prerelease skipped");
            foreach (var v in new[] { "v0.0.1", "v" + BuildInfo.Version })
            {
                Latest(v);
                Assert(await service.CheckLinuxAsync(default) == null, "older or installed version skipped " + v);
            }
            Latest("v99.1.02");
            await Reject<InvalidDataException>(() => service.CheckLinuxAsync(default), "invalid version rejected");
            entries.Add(entries[0]); Latest("v" + next);
            await Reject<InvalidDataException>(() => service.CheckLinuxAsync(default), "duplicate Linux package rejected");
            entries.Clear(); Latest("v" + next);
            Assert((await service.CheckLinuxAsync(default))!.Package == null && (await service.CheckAsync(default))!.Package == null,
                "missing platform packages cannot install");
            data["releases"] = data["latest"] = JsonSerializer.SerializeToUtf8Bytes(Array.Empty<object>());
            Latest("firmware-s3-zero");
            Assert(await service.CheckLinuxAsync(default) == null && await service.CheckAsync(default) == null, "firmware channel isolation");
            using var slow = new UpdateService(repo, null, new TimeoutHandler());
            using var cancel = new CancellationTokenSource(50);
            await Reject<OperationCanceledException>(() => slow.CheckLinuxAsync(cancel.Token), "desktop discovery honors cancellation");
        }
        finally { temp.Delete(true); }
    }
}
