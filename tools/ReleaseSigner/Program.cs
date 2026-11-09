// Signs release checksums for the in-app updater (ECDSA P-256, SHA-256, IEEE P1363 signature, base64).
//
//   ReleaseSigner keygen [privateKey.pem]       create a key pair; prints the public key for UpdateSource.PublicKeyPem
//   ReleaseSigner sign <file> [privateKey.pem]   writes <file>.sig
//   ReleaseSigner verify <file>                  checks <file>.sig against the public key built into the app
//
// The private key defaults to %USERPROFILE%\.fnchesttracker\update-signing-key.pem. Never commit it; back it up:
// without it, future releases cannot be installed by the updater.
using System.Security.Cryptography;
using ChestTracker.Core.Updates;

var defaultKey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fnchesttracker", "update-signing-key.pem");
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ReleaseSigner keygen [key.pem] | sign <file> [key.pem] | verify <file>");
    return 2;
}

switch (args[0])
{
    case "keygen":
    {
        var path = args.Length > 1 ? args[1] : defaultKey;
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{path} already exists; refusing to overwrite a signing key.");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        Console.WriteLine($"Private key written to {path} (keep it secret, back it up).");
        Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
        return 0;
    }

    case "sign":
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ReleaseSigner sign <file> [key.pem]");
            return 2;
        }

        var keyPath = args.Length > 2 ? args[2] : defaultKey;
        if (!File.Exists(keyPath))
        {
            Console.Error.WriteLine($"Signing key not found: {keyPath}");
            return 1;
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));
        var data = File.ReadAllBytes(args[1]);
        var signature = Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        File.WriteAllText(args[1] + ".sig", signature + "\n");
        if (!UpdatePackage.VerifySignature(data, signature, UpdateSource.PublicKeyPem))
        {
            Console.Error.WriteLine("Signed, but the signature does NOT match the public key built into the app (UpdateSource.PublicKeyPem).");
            return 1;
        }

        Console.WriteLine($"Signed {args[1]} -> {args[1]}.sig (verified against the app's public key)");
        return 0;
    }

    case "verify":
    {
        var data = File.ReadAllBytes(args[1]);
        var ok = UpdatePackage.VerifySignature(data, File.ReadAllText(args[1] + ".sig").Trim(), UpdateSource.PublicKeyPem);
        Console.WriteLine(ok ? "valid" : "INVALID");
        return ok ? 0 : 1;
    }

    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}
