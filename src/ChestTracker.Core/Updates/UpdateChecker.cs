using System.Net.Http.Headers;
using System.Text.Json;

namespace ChestTracker.Core.Updates;

/// <summary>Fixed facts about where updates come from and what a release package looks like.</summary>
public static class UpdateSource
{
    public const string Repository = "sonikx06/FNChestTracker";
    public const string ApiBase = "https://api.github.com/";

    /// <summary>The Windows package every release must carry (a ZIP with one folder holding FortniteChestTracker.exe).</summary>
    public const string PackageAsset = "FortniteChestTracker-win-x64.zip";

    /// <summary>"&lt;sha256&gt;  &lt;file name&gt;" lines for the release's assets.</summary>
    public const string ChecksumAsset = "SHA256SUMS.txt";

    /// <summary>Base64 ECDSA P-256 / SHA-256 signature (IEEE P1363) of <see cref="ChecksumAsset"/>'s exact bytes.</summary>
    public const string SignatureAsset = "SHA256SUMS.txt.sig";

    /// <summary>
    /// Public key that release checksums must be signed with. The private key never leaves the maintainer's PC
    /// (see CONTRIBUTING.md); without a valid signature an update is never installed.
    /// </summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFWoB3aOwZbbM5NWu4iPGY3wKQo7U
        VZQnXHFdtBDRGNuvz4biarTbYc6+nAiCBTPCGcDyW7KOoFhBrH3p+RhCng==
        -----END PUBLIC KEY-----
        """;
}

/// <summary>A GitHub release that carries a complete, signable Windows package.</summary>
public sealed record ReleaseInfo(
    SemVersion Version,
    string Tag,
    string Title,
    string Notes,
    string PageUrl,
    bool Prerelease,
    Uri PackageUrl,
    long PackageSize,
    Uri ChecksumUrl,
    Uri SignatureUrl);

public enum UpdateCheckStatus
{
    UpToDate,
    Available,

    /// <summary>GitHub could not be reached or answered with an error. The app simply continues.</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, string? Error = null);

/// <summary>
/// Asks the GitHub Releases API for the newest published (non-draft) release with a complete Windows package.
/// Pre-releases count: the project publishes betas. Never throws: network or API problems become
/// <see cref="UpdateCheckStatus.Failed"/>.
/// </summary>
public sealed class UpdateChecker(HttpClient http, Uri? apiBase = null, string repository = UpdateSource.Repository)
{
    private readonly Uri _api = apiBase ?? new Uri(UpdateSource.ApiBase);

    public static HttpClient CreateHttpClient(string userAgentVersion, HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FortniteChestTracker", userAgentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<UpdateCheckResult> CheckAsync(SemVersion current, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(_api, $"repos/{repository}/releases?per_page=30"), ct);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: $"GitHub answered {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var newest = Releases(doc.RootElement).Where(r => r.Version > current).MaxBy(r => r.Version);
            return newest is null ? new UpdateCheckResult(UpdateCheckStatus.UpToDate) : new UpdateCheckResult(UpdateCheckStatus.Available, newest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
                                       or UriFormatException or NotSupportedException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>Published releases with a parseable tag and all three required assets.</summary>
    public static IEnumerable<ReleaseInfo> Releases(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var r in array.EnumerateArray())
        {
            if (Bool(r, "draft") || !SemVersion.TryParse(Str(r, "tag_name"), out var version))
            {
                continue;
            }

            var assets = r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().ToDictionary(x => Str(x, "name") ?? "", x => x, StringComparer.Ordinal)
                : [];
            if (!assets.TryGetValue(UpdateSource.PackageAsset, out var package)
                || !assets.TryGetValue(UpdateSource.ChecksumAsset, out var sums)
                || !assets.TryGetValue(UpdateSource.SignatureAsset, out var sig)
                || Url(package) is not { } packageUrl || Url(sums) is not { } sumsUrl || Url(sig) is not { } sigUrl)
            {
                continue; // a release without a complete signed Windows package is never offered
            }

            yield return new ReleaseInfo(version, Str(r, "tag_name")!, Str(r, "name") ?? Str(r, "tag_name")!, Str(r, "body") ?? "",
                Str(r, "html_url") ?? "", Bool(r, "prerelease"), packageUrl,
                package.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : -1, sumsUrl, sigUrl);
        }
    }

    /// <summary>HTTPS only (plain HTTP is accepted for a loopback test server).</summary>
    private static Uri? Url(JsonElement asset) =>
        Uri.TryCreate(Str(asset, "browser_download_url"), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) ? u : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
