using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChestTracker.Core.Updates;

namespace ChestTracker.Tests;

/// <summary>GitHub update check, verification, download and install with rollback (no network: a fake GitHub).</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ct-update-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publicKey;

    public UpdateTests()
    {
        Directory.CreateDirectory(_dir);
        _publicKey = _key.ExportSubjectPublicKeyInfoPem();
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ fake GitHub

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();
        public bool Offline { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("No such host is known.");
            var key = request.RequestUri!.PathAndQuery;
            return Task.FromResult(Routes.TryGetValue(key, out var r) ? r() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Stream that fails after some bytes (connection dropped mid-download).</summary>
    private sealed class BrokenStream(byte[] data, int failAfter) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            Position >= failAfter ? throw new IOException("The connection was reset.") : base.Read(buffer, offset, Math.Min(count, failAfter - (int)Position));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Position >= failAfter ? throw new IOException("The connection was reset.") : base.ReadAsync(buffer[..Math.Min(buffer.Length, failAfter - (int)Position)], ct);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    private byte[] PackageZip(string version, bool withModel = true, string? evilEntry = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }

            Add("FortniteChestTracker/FortniteChestTracker.exe", "exe " + version);
            if (withModel) Add("FortniteChestTracker/models/best.onnx", "model " + version);
            if (evilEntry is not null) Add(evilEntry, "evil");
        }

        return ms.ToArray();
    }

    private (FakeGitHub Hub, byte[] Zip) Publish(string tag, bool prerelease = false, byte[]? zip = null, bool sign = true, bool tamper = false,
        bool draft = false, string api = "https://api.github.com")
    {
        zip ??= PackageZip(tag);
        var hash = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var sums = Encoding.UTF8.GetBytes($"{hash}  {UpdateSource.PackageAsset}\n0000  FortniteChestTracker-Server-win-x64.zip\n");
        var sig = Convert.ToBase64String(_key.SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        if (!sign) sig = Convert.ToBase64String(new byte[64]);
        var served = tamper ? zip.Concat(new byte[] { 1 }).ToArray() : zip;

        var hub = new FakeGitHub();
        var dl = $"https://github.com/sonikx06/FNChestTracker/releases/download/{tag}";
        hub.Routes[$"/repos/{UpdateSource.Repository}/releases?per_page=30"] = () => Json($$"""
            [ { "tag_name": "{{tag}}", "name": "Fortnite Chest Tracker {{tag}}", "body": "Notes for {{tag}}", "draft": {{(draft ? "true" : "false")}},
                "prerelease": {{(prerelease ? "true" : "false")}}, "html_url": "https://github.com/x/releases/tag/{{tag}}",
                "assets": [
                  { "name": "{{UpdateSource.PackageAsset}}", "size": {{served.Length}}, "browser_download_url": "{{dl}}/{{UpdateSource.PackageAsset}}" },
                  { "name": "{{UpdateSource.ChecksumAsset}}", "size": {{sums.Length}}, "browser_download_url": "{{dl}}/{{UpdateSource.ChecksumAsset}}" },
                  { "name": "{{UpdateSource.SignatureAsset}}", "size": 90, "browser_download_url": "{{dl}}/{{UpdateSource.SignatureAsset}}" } ] },
              { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "assets": [] } ]
            """);
        hub.Routes[$"/sonikx06/FNChestTracker/releases/download/{tag}/{UpdateSource.PackageAsset}"] = () => Bytes(served);
        hub.Routes[$"/sonikx06/FNChestTracker/releases/download/{tag}/{UpdateSource.ChecksumAsset}"] = () => Bytes(sums);
        hub.Routes[$"/sonikx06/FNChestTracker/releases/download/{tag}/{UpdateSource.SignatureAsset}"] = () => Bytes(Encoding.ASCII.GetBytes(sig + "\n"));
        return (hub, zip);
    }

    private static readonly SemVersion Installed = SemVersion.Parse("1.3.0");

    // ------------------------------------------------------------------ versions and checking

    [Theory]
    [InlineData("1.3.0", "1.2.9", 1)]
    [InlineData("v1.10.0", "1.9.9", 1)]
    [InlineData("1.3.0", "1.3.0-beta.2", 1)]
    [InlineData("1.3.0-beta.10", "1.3.0-beta.2", 1)]
    [InlineData("1.3.0-beta", "1.3.0-alpha", 1)]
    [InlineData("1.3.0.0", "1.3.0", 0)]
    [InlineData("1.3.0+abc123", "1.3.0", 0)]
    public void Versions_compare_semantically(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(SemVersion.Parse(a).CompareTo(SemVersion.Parse(b))));

    [Fact]
    public async Task No_update_when_the_newest_release_is_not_newer()
    {
        var (hub, _) = Publish("v1.3.0");
        using var http = new HttpClient(hub);
        var r = await new UpdateChecker(http).CheckAsync(Installed);
        Assert.Equal(UpdateCheckStatus.UpToDate, r.Status);
    }

    [Fact]
    public async Task Newer_release_is_found_with_its_notes_and_assets()
    {
        var (hub, _) = Publish("v1.4.0-beta.1", prerelease: true);
        using var http = new HttpClient(hub);
        var r = await new UpdateChecker(http).CheckAsync(Installed);
        Assert.Equal(UpdateCheckStatus.Available, r.Status);
        Assert.Equal("1.4.0-beta.1", r.Release!.Version.ToString());
        Assert.Equal("Notes for v1.4.0-beta.1", r.Release.Notes);
        Assert.True(r.Release.Prerelease);
        Assert.EndsWith(UpdateSource.PackageAsset, r.Release.PackageUrl.AbsolutePath);
    }

    [Fact]
    public async Task Drafts_and_releases_without_a_signed_package_are_ignored()
    {
        var (hub, _) = Publish("v1.5.0", draft: true);
        using var http = new HttpClient(hub);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await new UpdateChecker(http).CheckAsync(Installed)).Status);
    }

    [Fact]
    public async Task GitHub_unreachable_or_failing_never_throws()
    {
        var (hub, _) = Publish("v1.4.0");
        hub.Offline = true;
        using var http = new HttpClient(hub);
        var r = await new UpdateChecker(http).CheckAsync(Installed);
        Assert.Equal(UpdateCheckStatus.Failed, r.Status);

        hub.Offline = false;
        hub.Routes[$"/repos/{UpdateSource.Repository}/releases?per_page=30"] = () => new HttpResponseMessage(HttpStatusCode.Forbidden); // rate limit
        Assert.Equal(UpdateCheckStatus.Failed, (await new UpdateChecker(http).CheckAsync(Installed)).Status);
        hub.Routes[$"/repos/{UpdateSource.Repository}/releases?per_page=30"] = () => Json("<html>not json");
        Assert.Equal(UpdateCheckStatus.Failed, (await new UpdateChecker(http).CheckAsync(Installed)).Status);
    }

    // ------------------------------------------------------------------ download and verification

    private async Task<(ReleaseInfo Release, HttpClient Http)> Found(FakeGitHub hub)
    {
        var http = new HttpClient(hub);
        var r = await new UpdateChecker(http).CheckAsync(Installed);
        return (r.Release!, http);
    }

    [Fact]
    public async Task Signed_package_with_matching_checksum_is_unpacked_with_progress()
    {
        var (hub, _) = Publish("v1.4.0");
        var (release, http) = await Found(hub);
        var reports = new List<double>();
        var app = await new UpdatePackage(http, _publicKey).DownloadAndVerifyAsync(release, Path.Combine(_dir, "staging"), new SyncProgress(reports));
        Assert.Equal("exe v1.4.0", File.ReadAllText(Path.Combine(app, "FortniteChestTracker.exe")));
        Assert.True(File.Exists(Path.Combine(app, "models", "best.onnx")));
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public async Task Invalid_signature_or_wrong_key_is_rejected_and_nothing_is_kept()
    {
        var staging = Path.Combine(_dir, "staging");
        var (hub, _) = Publish("v1.4.0", sign: false);
        var (release, http) = await Found(hub);
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdatePackage(http, _publicKey).DownloadAndVerifyAsync(release, staging));
        Assert.Contains("not signed", ex.Message);
        Assert.False(Directory.Exists(staging));

        // A correctly signed release, but by another key (e.g. a hijacked account): also rejected.
        var (hub2, _) = Publish("v1.4.0");
        var (release2, http2) = await Found(hub2);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            new UpdatePackage(http2, other.ExportSubjectPublicKeyInfoPem()).DownloadAndVerifyAsync(release2, staging));
    }

    [Fact]
    public async Task Package_not_matching_its_signed_checksum_is_rejected()
    {
        var (hub, _) = Publish("v1.4.0", tamper: true);
        var (release, http) = await Found(hub);
        var staging = Path.Combine(_dir, "staging");
        await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdatePackage(http, _publicKey).DownloadAndVerifyAsync(release, staging));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task Interrupted_download_fails_cleanly()
    {
        var (hub, zip) = Publish("v1.4.0");
        hub.Routes[$"/sonikx06/FNChestTracker/releases/download/v1.4.0/{UpdateSource.PackageAsset}"] =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(zip, zip.Length / 2)) };
        var (release, http) = await Found(hub);
        var staging = Path.Combine(_dir, "staging");
        await Assert.ThrowsAsync<IOException>(() => new UpdatePackage(http, _publicKey).DownloadAndVerifyAsync(release, staging));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task Packages_with_unsafe_paths_or_without_the_model_are_rejected()
    {
        var (hub, _) = Publish("v1.4.0", zip: PackageZip("v1.4.0", evilEntry: "../../evil.txt"));
        var (release, http) = await Found(hub);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdatePackage(http, _publicKey).DownloadAndVerifyAsync(release, Path.Combine(_dir, "s1")));
        Assert.False(File.Exists(Path.Combine(_dir, "..", "evil.txt")));

        var (hub2, _) = Publish("v1.4.0", zip: PackageZip("v1.4.0", withModel: false));
        var (release2, http2) = await Found(hub2);
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdatePackage(http2, _publicKey).DownloadAndVerifyAsync(release2, Path.Combine(_dir, "s2")));
        Assert.Contains("model", ex.Message);
    }

    // ------------------------------------------------------------------ installation

    private (string Source, string Target, string Backup, string Data) Layout()
    {
        var source = Path.Combine(_dir, "new");
        var target = Path.Combine(_dir, "app");
        var backup = Path.Combine(_dir, "backup");
        var data = Path.Combine(_dir, "LocalAppData", "FortniteChestTracker");
        Directory.CreateDirectory(Path.Combine(source, "models"));
        File.WriteAllText(Path.Combine(source, "FortniteChestTracker.exe"), "new exe");
        File.WriteAllText(Path.Combine(source, "models", "best.onnx"), "new model");
        Directory.CreateDirectory(Path.Combine(target, "models"));
        File.WriteAllText(Path.Combine(target, "FortniteChestTracker.exe"), "old exe");
        File.WriteAllText(Path.Combine(target, "models", "best.onnx"), "old model");
        File.WriteAllText(Path.Combine(target, "old-runtime.dll"), "old dll");   // files of the old layout
        Directory.CreateDirectory(Path.Combine(data, "backups"));
        File.WriteAllText(Path.Combine(data, "chests.db"), "statistics");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"hotkeys\":1}");
        return (source, target, Path.Combine(backup, "b"), data);
    }

    [Fact]
    public void Successful_install_replaces_the_app_folder_and_never_touches_user_data()
    {
        var (source, target, backup, data) = Layout();
        var before = Directory.GetFiles(data, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);

        UpdateInstaller.Apply(source, target, backup);

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(target, "FortniteChestTracker.exe")));
        Assert.Equal("new model", File.ReadAllText(Path.Combine(target, "models", "best.onnx")));
        Assert.False(File.Exists(Path.Combine(target, "old-runtime.dll")));              // old layout leftovers are gone
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(backup, "FortniteChestTracker.exe")));
        Assert.Equal(before, Directory.GetFiles(data, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText));
    }

    [Fact]
    public void Failed_install_restores_the_previous_version()
    {
        var (source, target, backup, _) = Layout();
        // A file of the new version cannot be read (locked by another program): copying fails half-way.
        File.WriteAllText(Path.Combine(source, "zz-locked.dll"), "x");
        using (var locked = new FileStream(Path.Combine(source, "zz-locked.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Apply(source, target, backup));
        }

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(target, "FortniteChestTracker.exe")));
        Assert.Equal("old model", File.ReadAllText(Path.Combine(target, "models", "best.onnx")));
        Assert.True(File.Exists(Path.Combine(target, "old-runtime.dll")));
        Assert.False(File.Exists(Path.Combine(target, "zz-locked.dll")));
    }

    [Fact]
    public void Install_fails_without_changes_when_the_old_app_is_still_running()
    {
        var (source, target, backup, _) = Layout();
        using (new FileStream(Path.Combine(target, "FortniteChestTracker.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Apply(source, target, backup));
        }

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(target, "FortniteChestTracker.exe")));
        Assert.True(File.Exists(Path.Combine(target, "old-runtime.dll")));
        Assert.Equal("old model", File.ReadAllText(Path.Combine(target, "models", "best.onnx")));
    }

    [Fact]
    public void Rollback_after_a_failed_start_restores_the_old_files()
    {
        var (source, target, backup, _) = Layout();
        UpdateInstaller.Apply(source, target, backup);
        UpdateInstaller.Rollback(target, backup);           // the new version did not report a successful start
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(target, "FortniteChestTracker.exe")));
        Assert.True(File.Exists(Path.Combine(target, "old-runtime.dll")));
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
