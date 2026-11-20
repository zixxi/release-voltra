using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Voltra;

namespace Voltra.Linux;

internal sealed record LinuxRelease(Version Version, string Notes, Uri Package);

internal static class LinuxUpdateService
{
    internal const string Repository = "zixxi/release-voltra";
    internal static Version Version { get; } = new(typeof(LinuxUpdateService).Assembly
        .GetName().Version!.ToString(3));
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    internal static async Task<LinuxRelease?> CheckAsync(CancellationToken token)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=100");
        request.Headers.UserAgent.ParseAdd($"Voltra-Linux/{Version}");
        using var response = await Http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        return FindLatest(body, Version);
    }

    internal static LinuxRelease? FindLatest(JsonElement body, Version current)
    {
        LinuxRelease? latest = null;
        foreach (var item in body.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean()) continue;
            string tag = item.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith("linux-v", StringComparison.Ordinal) || !SerialProtocol.StableVersion().IsMatch(tag[7..]) ||
                !System.Version.TryParse(tag[7..], out var version) || version <= current ||
                (latest is not null && version <= latest.Version)) continue;

            string package = $"Voltra-{version}-linux-x64.tar.gz";
            if (!item.GetProperty("assets").EnumerateArray().Any(asset => asset.GetProperty("name").GetString() == package &&
                asset.GetProperty("state").GetString() == "uploaded")) continue;
            latest = new(version, item.GetProperty("body").GetString() ?? "",
                new Uri($"https://github.com/{Repository}/releases/download/{tag}/{package}"));
        }
        return latest;
    }
}
