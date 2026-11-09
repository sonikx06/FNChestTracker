using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ChestTracker.Core.Updates;

/// <summary>The package is not trustworthy or incomplete; nothing from it is installed.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>
/// Downloads and verifies a release package, then unpacks it into a staging folder:
/// <list type="number">
/// <item>the release's SHA256SUMS.txt must carry a valid ECDSA P-256 signature from the project's key;</item>
/// <item>the package's SHA-256 must equal the signed checksum (and its size the published size);</item>
/// <item>the unpacked files must stay inside the staging folder and contain FortniteChestTracker.exe and the model.</item>
/// </list>
/// Only then is the staging folder handed to the installer. Interrupted downloads leave nothing behind.
/// </summary>
public sealed class UpdatePackage(HttpClient http, string publicKeyPem = UpdateSource.PublicKeyPem)
{
    public const string ExecutableName = "FortniteChestTracker.exe";
    public static readonly string ModelRelativePath = Path.Combine("models", "best.onnx");

    /// <returns>The folder that holds the new FortniteChestTracker.exe.</returns>
    public async Task<string> DownloadAndVerifyAsync(ReleaseInfo release, string stagingRoot, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }

        Directory.CreateDirectory(stagingRoot);
        try
        {
            // 1. Signed checksums.
            var sums = await http.GetByteArrayAsync(release.ChecksumUrl, ct);
            var signature = (await http.GetStringAsync(release.SignatureUrl, ct)).Trim();
            if (!VerifySignature(sums, signature, publicKeyPem))
            {
                throw new UpdateVerificationException("The release checksums are not signed with the project's key.");
            }

            var expected = ExpectedHash(Encoding.UTF8.GetString(sums), UpdateSource.PackageAsset)
                           ?? throw new UpdateVerificationException($"The signed checksums do not list {UpdateSource.PackageAsset}.");

            // 2. Package, hashed while downloading.
            var zip = Path.Combine(stagingRoot, UpdateSource.PackageAsset);
            await DownloadAsync(release.PackageUrl, zip, release.PackageSize, progress, ct);
            var actual = Convert.ToHexString(await HashFileAsync(zip, ct)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new UpdateVerificationException("The downloaded package does not match its signed checksum.");
            }

            // 3. Unpack safely and check the layout.
            var extract = Path.Combine(stagingRoot, "package");
            ExtractSafely(zip, extract);
            File.Delete(zip);
            return LocateApplication(extract);
        }
        catch
        {
            TryDelete(stagingRoot);
            throw;
        }
    }

    public static bool VerifySignature(byte[] data, string base64Signature, string publicKeyPem)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(data, Convert.FromBase64String(base64Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Hash for <paramref name="fileName"/> from "&lt;hex&gt;  &lt;name&gt;" lines (sha256sum format).</summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*') == fileName && parts[0].Length == 64
                && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    private async Task DownloadAsync(Uri url, string path, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        var part = path + ".part";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? expectedSize;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }

            if (expectedSize > 0 && done != expectedSize)
            {
                throw new UpdateVerificationException($"Download incomplete ({done} of {expectedSize} bytes).");
            }
        }

        File.Move(part, path, overwrite: true);
        progress?.Report(1.0);
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, ct);
    }

    /// <summary>Unzips, refusing any entry that would land outside <paramref name="destination"/> ("zip slip").</summary>
    public static void ExtractSafely(string zip, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateVerificationException($"The package contains an unsafe path: {entry.FullName}");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    /// <summary>The package root (the ZIP's top folder) with the executable and the built-in model.</summary>
    public static string LocateApplication(string extracted)
    {
        var candidates = new[] { extracted }.Concat(Directory.GetDirectories(extracted));
        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, ExecutableName)))
            {
                if (!File.Exists(Path.Combine(dir, ModelRelativePath)))
                {
                    throw new UpdateVerificationException("The package has no built-in chest model.");
                }

                return dir;
            }
        }

        throw new UpdateVerificationException($"The package has no {ExecutableName}.");
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
