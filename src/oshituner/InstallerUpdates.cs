using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace oshituner;

internal sealed record InstallerRelease(Version Version, long Size, byte[] Sha256, Uri Download);

internal static class InstallerUpdates
{
    private const string Repository = "benjaminkwilliams/oshituner";
    private const long MaxInstallerBytes = 512 * 1024 * 1024;
    private static readonly HttpClient Http = CreateClient();
    private static readonly Uri Feed = new($"https://api.github.com/repos/{Repository}/releases/latest");

    public static bool IsInstalled(string folder) => Directory.EnumerateFiles(folder, "unins???.exe")
        .Any(path => File.Exists(Path.ChangeExtension(path, ".dat")));

    public static async Task<InstallerRelease?> CheckAsync(Version current, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Http.GetAsync(Feed, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(timeout.Token);
        return SelectInstaller(release, current);
    }

    internal static InstallerRelease? SelectInstaller(Release? release, Version current)
    {
        if (release is not { Draft: false, Prerelease: false, Tag: { } tag } ||
            !tag.StartsWith('v') || !Version.TryParse(tag.AsSpan(1), out var version) ||
            tag != $"v{version}" || version.Revision < 0 || version <= current) return null;

        var name = $"Oshi-Tuner-{version}-win-x64-Setup.exe";
        var asset = release.Assets?.FirstOrDefault(asset => asset.Name == name && asset.State == "uploaded");
        if (asset is null) return null;
        var url = new Uri($"https://github.com/{Repository}/releases/download/{tag}/{name}");
        if (asset.Url != url.AbsoluteUri || asset.Size is <= 0 or > MaxInstallerBytes ||
            asset.Digest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.Skip(7).Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("GitHub did not provide valid verification information for the installer.");
        return new(version, asset.Size, Convert.FromHexString(digest.AsSpan(7)), url);
    }

    public static void DeletePastDownloads(InstallProfile profile)
    {
        var folder = profile.PathFor("updates");
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder, "oshi-update-*.exe")) TryDelete(path);
    }

    public static async Task<string> DownloadAsync(InstallProfile profile, InstallerRelease release, CancellationToken token)
    {
        var folder = profile.PathFor("updates");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"oshi-update-{Guid.NewGuid():N}.exe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var response = await OpenDownloadAsync(release.Download, timeout.Token).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await CopyVerifiedAsync(source, file, release, timeout.Token).ConfigureAwait(false);
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    internal static async Task CopyVerifiedAsync(Stream source, Stream target, InstallerRelease release, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long remaining = release.Size;
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            remaining -= count;
            if (remaining < 0) throw new InvalidDataException("The downloaded installer is larger than expected.");
            hash.AppendData(buffer, 0, count);
            await target.WriteAsync(buffer.AsMemory(0, count), token);
        }

        if (remaining != 0 || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), release.Sha256))
            throw new InvalidDataException("The downloaded installer failed SHA-256 verification.");
    }

    private static async Task<HttpResponseMessage> OpenDownloadAsync(Uri url, CancellationToken token)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if ((int)response.StatusCode is < 300 or >= 400 ||
                    response.Headers.Location is not { IsAbsoluteUri: true } next || next.Scheme != "https" ||
                    !next.IsDefaultPort || next.UserInfo.Length != 0 ||
                    next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
                    throw new HttpRequestException("GitHub did not return the expected installer download.", null, response.StatusCode);
                url = next;
            }
        }
        throw new HttpRequestException("The installer download had too many redirects.");
    }

    public static Process Start(string path, InstallProfile profile, DesktopLaunchOptions options)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = profile.InstallFolder };
        foreach (var option in new[] { "/SP-", "/SILENT", "/NORESTART", "/OSHIUPDATE=1", $"/DIR={profile.InstallFolder}",
            $"/OSHIWATCH={(options.StartOshiWatch ? 1 : 0)}", $"/OSHITRAY={(options.StartInTray ? 1 : 0)}",
            $"/OSHIMCP={(options.McpEnabled ? 1 : 0)}", $"/OSHIRPC={(options.RpcEnabled ? 1 : 0)}" })
            start.ArgumentList.Add(option);
        return Process.Start(start) ?? throw new Win32Exception("The installer could not be started.");
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            MaxResponseContentBufferSize = 1024 * 1024,
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OshiTuner-Updater");
        return client;
    }

    internal sealed record Release([property: JsonPropertyName("tag_name")] string? Tag,
        bool Draft, bool Prerelease, Asset[]? Assets);
    internal sealed record Asset(string Name, string State, long Size, string? Digest,
        [property: JsonPropertyName("browser_download_url")] string? Url);
}
