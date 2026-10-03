using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MagiDesk.Features.Updates;

// Offline signing tool. Never accepts a password in argv or the environment.
try
{
    if (args.Length < 2) throw new ArgumentException("Usage: init <repo> <private.key> | trust <repo> <private.key> | check-keys <repo> | sign <repo> <private.key> <version> <output-directory> <installer.exe> [installer.exe]");
    string repo = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
    string trustFile = Path.Combine(repo, "MagiDesk", "Features", "Updates", "UpdateKeys.json");
    if (!File.Exists(trustFile)) throw new ArgumentException("Repository UpdateKeys.json not found.");
    var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(trustFile)) ?? new();
    if (args[0] == "verify" && args.Length == 4)
    {
        string directory = Path.GetFullPath(args[3]);
        var verifiedManifest = SignedUpdateManifest.Verify(
            SignedUpdateManifest.ReadFile(Path.Combine(directory, SignedUpdateManifest.FileName), SignedUpdateManifest.MaxBytes),
            SignedUpdateManifest.ReadFile(Path.Combine(directory, SignedUpdateManifest.SignatureName), SignedUpdateManifest.SignatureBytes), keys);
        if (verifiedManifest.Version != args[2]) throw new InvalidDataException("Manifest does not match the release version.");
        foreach (var package in verifiedManifest.Packages)
        {
            using var stream = File.OpenRead(Path.Combine(directory, package.Name));
            if (stream.Length != package.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installer does not match the signed verifiedManifest: " + package.Name);
        }
        Console.WriteLine($"Verified release {verifiedManifest.Version}, {verifiedManifest.Packages.Length} installer(s).");
        return 0;
    }
    if (args[0] == "check-keys" && args.Length == 2)
    {
        if (keys.Count == 0) throw new InvalidOperationException("No release public key configured. Run init before building a distributable release.");
        foreach (var pair in keys)
        {
            using var check = ECDsa.Create();
            byte[] bytes = Convert.FromBase64String(pair.Value);
            check.ImportSubjectPublicKeyInfo(bytes, out int read);
            if (read != bytes.Length || SignedUpdateManifest.KeyId(bytes) != pair.Key || check.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new InvalidDataException("Invalid release public key.");
        }
        Console.WriteLine($"Validated {keys.Count} release public key(s).");
        return 0;
    }
    if (args.Length < 3 || args[0] is not ("init" or "trust" or "sign")) throw new ArgumentException("Invalid signing command.");
    if ((args[0] is "init" or "trust" && args.Length != 3) || (args[0] == "sign" && args.Length is not (6 or 7)))
        throw new ArgumentException("Invalid argument count.");
    string privatePath = Path.GetFullPath(args[2]);
    if (privatePath.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || privatePath == repo)
        throw new InvalidOperationException("The private key must be stored outside the repository.");
    if (args[0] == "init" && args.Length == 3)
    {
        if (File.Exists(privatePath)) throw new IOException("Private key already exists; use trust to re-export its public key.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        char[] password = ReadPassword("New private-key password (12+ characters): ");
        char[] confirm = [];
        try
        {
            confirm = ReadPassword("Confirm password: ");
            if (password.Length < 12 || !password.AsSpan().SequenceEqual(confirm)) throw new ArgumentException("Password too short or confirmation differs.");
            var encrypted = key.ExportEncryptedPkcs8PrivateKey(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600000));
            // CreateNew is essential: never destroy the user's existing signing key.
            Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
            using (var stream = new FileStream(privatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(encrypted);
            Trust(key, keys, trustFile);
            Console.WriteLine("Encrypted key created. Keep its password and an offline backup; never upload the private file. Rebuild MagiDesk before release.");
        }
        finally { Array.Clear(password); Array.Clear(confirm); }
        return 0;
    }
    using var signingKey = ECDsa.Create();
    char[] secret = ReadPassword("Private-key password: ");
    try
    {
        var encrypted = SignedUpdateManifest.ReadFile(privatePath, 16384);
        signingKey.ImportEncryptedPkcs8PrivateKey(secret, encrypted, out int read);
        if (read != encrypted.Length || signingKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new InvalidDataException("Expected a P-256 private key.");
    }
    finally { Array.Clear(secret); }
    if (args[0] == "trust") { Trust(signingKey, keys, trustFile); return 0; }

    string version = args[3];
    var parsed = ReleaseVersion.Parse(version) ?? throw new ArgumentException("Invalid release version.");
    var packages = new List<UpdatePackage>();
    foreach (string path in args.Skip(5))
    {
        string name = Path.GetFileName(path);
        string runtime = name.StartsWith($"MagiDesk-{version}-win-arm64-Setup-", StringComparison.Ordinal) ? "win-arm64" : "win-x64";
        using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        packages.Add(new(runtime, name, $"https://github.com/{UpdateRelease.Repository}/releases/download/v{version}/{name}", stream.Length, hash));
    }
    var manifest = new UpdateManifest(1, SignedUpdateManifest.KeyId(signingKey.ExportSubjectPublicKeyInfo()), version, parsed.Pre.Length == 0 ? "stable" : "preview", packages.ToArray());
    SignedUpdateManifest.Validate(manifest);
    byte[] content = JsonSerializer.SerializeToUtf8Bytes(manifest, SignedUpdateManifest.Json);
    Console.WriteLine(Encoding.UTF8.GetString(content)); // Public metadata only.
    Console.Write("Verify version, architecture and hashes above. Type SIGN to continue: ");
    if (Console.ReadLine() != "SIGN") throw new InvalidOperationException("Signing cancelled.");
    byte[] signature = signingKey.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    SignedUpdateManifest.Verify(content, signature, keys); // Refuse keys absent from the application's trust source.
    string output = Path.GetFullPath(args[4]);
    Directory.CreateDirectory(output);
    string jsonPath = Path.Combine(output, SignedUpdateManifest.FileName);
    string sigPath = Path.Combine(output, SignedUpdateManifest.SignatureName);
    // Reserve both paths before writing: reruns cannot silently replace a published pair.
    using (var json = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    using (var sig = new FileStream(sigPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    { json.Write(content); sig.Write(signature); }
    Console.WriteLine("Signed manifest written. Upload both files and the exact installers; do not edit the JSON.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static void Trust(ECDsa key, Dictionary<string, string> keys, string path)
{
    var publicKey = key.ExportSubjectPublicKeyInfo();
    string id = SignedUpdateManifest.KeyId(publicKey);
    keys[id] = Convert.ToBase64String(publicKey);
    string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        File.WriteAllText(staging, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(staging, path, true);
    }
    finally { if (File.Exists(staging)) File.Delete(staging); }
    Console.WriteLine("Public key ID: " + id);
}

static char[] ReadPassword(string prompt)
{
    if (Console.IsInputRedirected) throw new InvalidOperationException("Use a local interactive terminal; password input cannot be redirected.");
    Console.Write(prompt);
    char[] buffer = new char[1024]; int count = 0;
    try
    {
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer[..count]; }
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("Cancelled.");
            if (key.Key == ConsoleKey.Backspace) { if (count > 0) buffer[--count] = '\0'; }
            else if (!char.IsControl(key.KeyChar))
            { if (count == buffer.Length) throw new ArgumentException("Password too long."); buffer[count++] = key.KeyChar; }
        }
    }
    finally { Array.Clear(buffer); }
}
