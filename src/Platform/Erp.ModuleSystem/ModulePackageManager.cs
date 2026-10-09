using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Erp.Abstractions.Modules;

namespace Erp.ModuleSystem;

public sealed record ModuleTrustPolicy(bool RequireSignatures, IReadOnlyDictionary<string, string> TrustedPublisherPublicKeys);

public sealed class ModulePackageManager(IModuleManifestParser manifestParser, string rootPath, ModuleTrustPolicy? trustPolicy = null) : IModulePackageManager
{
    private const string StateFileName = "modules.state.json";
    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions SignatureJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _root = Path.GetFullPath(rootPath);

    public IReadOnlyList<ModulePackageInfo> List()
    {
        if (!Directory.Exists(_root)) return [];
        var state = ReadState();
        return Directory.GetDirectories(_root)
            .Where(path => !Path.GetFileName(path).StartsWith('.'))
            .Select(path => ReadInstalled(path, state))
            .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ModulePackageInfo Validate(string archivePath)
    {
        var staged = StageAndValidate(archivePath);
        try { return staged.Info with { Installed = Directory.Exists(Path.Combine(_root, staged.Info.Id)) }; }
        finally { Directory.Delete(staged.Path, recursive: true); }
    }

    public ModulePackageInfo Install(string archivePath, bool update = false)
    {
        Directory.CreateDirectory(_root);
        var staged = StageAndValidate(archivePath);
        var destination = Path.Combine(_root, staged.Info.Id);
        EnsureWithin(_root, destination);
        var exists = Directory.Exists(destination);
        if (exists != update)
        {
            Directory.Delete(staged.Path, recursive: true);
            throw new InvalidOperationException(exists
                ? $"Module '{staged.Info.Id}' is already installed; use update."
                : $"Module '{staged.Info.Id}' is not installed; use install.");
        }

        string? backup = null;
        try
        {
            if (exists)
            {
                backup = Path.Combine(_root, $".{staged.Info.Id}.backup-{Guid.NewGuid():N}");
                Directory.Move(destination, backup);
            }
            Directory.Move(staged.Path, destination);
            if (backup is not null) Directory.Delete(backup, recursive: true);
            var state = ReadState();
            state[staged.Info.Id] = true;
            WriteState(state);
            return staged.Info with { Enabled = true, Installed = true, RestartRequired = true };
        }
        catch
        {
            if (Directory.Exists(staged.Path)) Directory.Delete(staged.Path, recursive: true);
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(destination)) Directory.Move(backup, destination);
            throw;
        }
    }

    public ModulePackageInfo SetEnabled(string moduleId, bool enabled)
    {
        ValidateId(moduleId);
        var path = Path.Combine(_root, moduleId);
        EnsureWithin(_root, path);
        if (!Directory.Exists(path)) throw new KeyNotFoundException($"Module '{moduleId}' is not installed.");
        var state = ReadState();
        var installed = ReadInstalledManifests();
        if (enabled)
        {
            var manifest = installed[moduleId];
            var disabledDependency = manifest.Dependencies.FirstOrDefault(dependency =>
                installed.ContainsKey(dependency.ModuleId) && state.TryGetValue(dependency.ModuleId, out var dependencyEnabled) && !dependencyEnabled);
            if (disabledDependency is not null)
                throw new InvalidOperationException($"Module '{moduleId}' requires enabled module '{disabledDependency.ModuleId}'.");
        }
        else
        {
            var dependent = installed.Values.FirstOrDefault(manifest =>
                (!state.TryGetValue(manifest.Id, out var dependentEnabled) || dependentEnabled) &&
                manifest.Dependencies.Any(dependency => string.Equals(dependency.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)));
            if (dependent is not null)
                throw new InvalidOperationException($"Module '{moduleId}' is required by enabled module '{dependent.Id}'.");
        }
        state[moduleId] = enabled;
        WriteState(state);
        var info = ReadInstalled(path, state);
        return info with { RestartRequired = true };
    }

    internal static IReadOnlySet<string> DisabledModules(string rootPath)
    {
        var path = Path.Combine(Path.GetFullPath(rootPath), StateFileName);
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? [];
        return state.Where(item => !item.Value).Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private (string Path, ModulePackageInfo Info) StageAndValidate(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var archive = Path.GetFullPath(archivePath);
        if (!File.Exists(archive)) throw new FileNotFoundException("Module package was not found.", archive);
        var stagingRoot = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(stagingRoot);
        var staged = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            if (zip.Entries.Count == 0) throw new InvalidDataException("Module package is empty.");
            foreach (var entry in zip.Entries)
            {
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.Length == 0 || relative.StartsWith('/') || relative.Contains("../", StringComparison.Ordinal) ||
                    relative is not ("module.json" or "module.sha256" or "module.signature.json") && !relative.StartsWith("lib/", StringComparison.Ordinal))
                    throw new InvalidDataException($"Module package contains an unexpected path '{entry.FullName}'.");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Module packages may not contain symbolic links.");
                var target = Path.GetFullPath(Path.Combine(staged, relative.Replace('/', Path.DirectorySeparatorChar)));
                EnsureWithin(staged, target);
                if (relative.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
            var manifestPath = Path.Combine(staged, "module.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("Module package requires module.json.");
            ModuleManifest manifest;
            using (var stream = File.OpenRead(manifestPath)) manifest = manifestParser.Parse(stream);
            ValidateId(manifest.Id);
            VerifyPublisherSignature(staged);
            new ExternalModuleCatalog(manifestParser).LoadSingle(_root, staged);
            var state = ReadState();
            return (staged, new(manifest.Id, manifest.Name, manifest.Version, !state.TryGetValue(manifest.Id, out var enabled) || enabled, false, false));
        }
        catch
        {
            Directory.Delete(staged, recursive: true);
            throw;
        }
    }

    private void VerifyPublisherSignature(string stagedPath)
    {
        var signaturePath = Path.Combine(stagedPath, "module.signature.json");
        if (!File.Exists(signaturePath))
        {
            if (trustPolicy?.RequireSignatures == true) throw new InvalidDataException("The module trust policy requires a publisher signature.");
            return;
        }
        if (trustPolicy is null) throw new InvalidDataException("The package is signed, but no publisher trust policy is configured.");
        var signature = JsonSerializer.Deserialize<PublisherSignature>(File.ReadAllText(signaturePath), SignatureJsonOptions)
            ?? throw new InvalidDataException("module.signature.json is invalid.");
        if (!string.Equals(signature.Algorithm, "ECDSA-SHA256", StringComparison.Ordinal))
            throw new InvalidDataException("Only ECDSA-SHA256 publisher signatures are supported.");
        if (!trustPolicy.TrustedPublisherPublicKeys.TryGetValue(signature.Publisher, out var publicKey))
            throw new InvalidDataException($"Publisher '{signature.Publisher}' is not trusted.");
        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(signature.Signature); }
        catch (FormatException exception) { throw new InvalidDataException("The publisher signature is not valid base64.", exception); }
        using var ecdsa = ECDsa.Create();
        try { ecdsa.ImportFromPem(publicKey); }
        catch (ArgumentException exception) { throw new InvalidDataException($"Trusted key for publisher '{signature.Publisher}' is invalid.", exception); }
        if (!ecdsa.VerifyData(SignaturePayload(stagedPath), signatureBytes, HashAlgorithmName.SHA256))
            throw new InvalidDataException("Publisher signature validation failed.");
    }

    public static byte[] SignaturePayload(string packagePath)
    {
        var manifest = File.ReadAllBytes(Path.Combine(packagePath, "module.json"));
        var checksums = File.ReadAllBytes(Path.Combine(packagePath, "module.sha256"));
        var payload = new byte[manifest.Length + 1 + checksums.Length];
        Buffer.BlockCopy(manifest, 0, payload, 0, manifest.Length);
        payload[manifest.Length] = (byte)'\n';
        Buffer.BlockCopy(checksums, 0, payload, manifest.Length + 1, checksums.Length);
        return payload;
    }

    private ModulePackageInfo ReadInstalled(string path, Dictionary<string, bool> state)
    {
        using var stream = File.OpenRead(Path.Combine(path, "module.json"));
        var manifest = manifestParser.Parse(stream);
        return new(manifest.Id, manifest.Name, manifest.Version, !state.TryGetValue(manifest.Id, out var enabled) || enabled, true, false);
    }

    private Dictionary<string, bool> ReadState()
    {
        var path = Path.Combine(_root, StateFileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? new(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, ModuleManifest> ReadInstalledManifests()
    {
        if (!Directory.Exists(_root)) return new(StringComparer.OrdinalIgnoreCase);
        var manifests = new Dictionary<string, ModuleManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetDirectories(_root).Where(path => !Path.GetFileName(path).StartsWith('.')))
        {
            var manifestPath = Path.Combine(path, "module.json");
            if (!File.Exists(manifestPath)) continue;
            using var stream = File.OpenRead(manifestPath);
            var manifest = manifestParser.Parse(stream);
            if (!manifests.TryAdd(manifest.Id, manifest)) throw new InvalidOperationException($"Duplicate installed module ID '{manifest.Id}'.");
        }
        return manifests;
    }

    private void WriteState(Dictionary<string, bool> state)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, StateFileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, StateJsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void ValidateId(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || moduleId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new InvalidDataException("Module ID contains invalid path characters.");
    }

    private static void EnsureWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Module package path escapes its configured root.");
    }

    private sealed record PublisherSignature(string Publisher, string Algorithm, string Signature);
}
