using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Erp.Abstractions.Modules;

namespace Erp.ModuleSystem;

public sealed record ModuleTrustPolicy(bool RequireSignatures, IReadOnlyDictionary<string, string> TrustedPublisherPublicKeys);

public sealed class ModulePackageManager : IModulePackageManager
{
    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions SignatureJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IModuleManifestParser _manifestParser;
    private readonly string _root;
    private readonly ModuleTrustPolicy? _trustPolicy;
    private readonly ModulePackageFileOperations _files;

    public ModulePackageManager(IModuleManifestParser manifestParser, string rootPath, ModuleTrustPolicy? trustPolicy = null)
        : this(manifestParser, rootPath, trustPolicy, new ModulePackageFileOperations())
    {
    }

    internal ModulePackageManager(
        IModuleManifestParser manifestParser,
        string rootPath,
        ModuleTrustPolicy? trustPolicy,
        ModulePackageFileOperations files)
    {
        _manifestParser = manifestParser;
        _root = Path.GetFullPath(rootPath);
        _trustPolicy = trustPolicy;
        _files = files;
    }

    public IReadOnlyList<ModulePackageInfo> List()
    {
        using var lease = ModuleRootSynchronization.Acquire(_root);
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
        using var lease = ModuleRootSynchronization.Acquire(_root);
        ReadState();
        var staged = StageAndValidate(archivePath);
        try { return staged.Info with { Installed = Directory.Exists(Path.Combine(_root, staged.Info.Id)) }; }
        finally { _files.DeleteDirectory(staged.Path); }
    }

    public ModulePackageInfo Install(string archivePath, bool update = false)
    {
        using var lease = ModuleRootSynchronization.Acquire(_root);
        Directory.CreateDirectory(_root);
        var state = ReadState();
        var staged = StageAndValidate(archivePath);
        var destination = Path.Combine(_root, staged.Info.Id);
        EnsureWithin(_root, destination);
        var exists = Directory.Exists(destination);
        string? backup = null;
        var promoted = false;
        try
        {
            if (exists != update)
                throw new InvalidOperationException(exists
                    ? $"Module '{staged.Info.Id}' is already installed; use update."
                    : $"Module '{staged.Info.Id}' is not installed; use install.");
            if (exists)
            {
                backup = Path.Combine(_root, $".{staged.Info.Id}.backup-{Guid.NewGuid():N}");
                _files.MoveDirectory(destination, backup);
            }
            _files.MoveDirectory(staged.Path, destination);
            promoted = true;
            state[staged.Info.Id] = true;
            WriteState(state);
        }
        catch (Exception failure)
        {
            var recoveryFailures = new List<Exception>();
            if (promoted && Directory.Exists(destination))
            {
                try { _files.DeleteDirectory(destination); }
                catch (Exception exception) { recoveryFailures.Add(exception); }
            }
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(destination))
            {
                try { _files.MoveDirectory(backup, destination); }
                catch (Exception exception) { recoveryFailures.Add(exception); }
            }
            if (Directory.Exists(staged.Path))
            {
                try { _files.DeleteDirectory(staged.Path); }
                catch (Exception exception) { recoveryFailures.Add(exception); }
            }
            if (recoveryFailures.Count > 0)
                throw new AggregateException(
                    "The module operation failed and recovery was incomplete. Inspect the module packages, state, and hidden backups before retrying.",
                    new[] { failure }.Concat(recoveryFailures));
            throw;
        }

        // State replacement is the commit point. Cleanup cannot turn a committed
        // package replacement into a failed operation or discard recovery data.
        if (backup is not null && Directory.Exists(backup))
        {
            try { _files.DeleteDirectory(backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return staged.Info with { Enabled = true, Installed = true, RestartRequired = true };
    }

    public ModulePackageInfo SetEnabled(string moduleId, bool enabled)
    {
        using var lease = ModuleRootSynchronization.Acquire(_root);
        ValidateId(moduleId);
        var path = Path.Combine(_root, moduleId);
        EnsureWithin(_root, path);
        if (!Directory.Exists(path)) throw new KeyNotFoundException($"Module '{moduleId}' is not installed.");
        var state = ReadState();
        var info = ReadInstalled(path, state);
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
        return info with { Enabled = enabled, RestartRequired = true };
    }

    internal static IReadOnlySet<string> DisabledModules(string rootPath)
    {
        var state = ModulePackageState.Read(Path.GetFullPath(rootPath));
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
            using (var stream = File.OpenRead(manifestPath)) manifest = _manifestParser.Parse(stream);
            ValidateId(manifest.Id);
            VerifyPublisherSignature(staged);
            new ExternalModuleCatalog(_manifestParser).LoadSingle(_root, staged);
            var state = ReadState();
            return (staged, new(manifest.Id, manifest.Name, manifest.Version, !state.TryGetValue(manifest.Id, out var enabled) || enabled, false, false));
        }
        catch (Exception failure)
        {
            try { _files.DeleteDirectory(staged); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Module validation failed and its staging directory could not be cleaned up.", failure, cleanupFailure);
            }
            throw;
        }
    }

    private void VerifyPublisherSignature(string stagedPath)
    {
        var signaturePath = Path.Combine(stagedPath, "module.signature.json");
        if (!File.Exists(signaturePath))
        {
            if (_trustPolicy?.RequireSignatures == true) throw new InvalidDataException("The module trust policy requires a publisher signature.");
            return;
        }
        if (_trustPolicy is null) throw new InvalidDataException("The package is signed, but no publisher trust policy is configured.");
        var signature = JsonSerializer.Deserialize<PublisherSignature>(File.ReadAllText(signaturePath), SignatureJsonOptions)
            ?? throw new InvalidDataException("module.signature.json is invalid.");
        if (!string.Equals(signature.Algorithm, "ECDSA-SHA256", StringComparison.Ordinal))
            throw new InvalidDataException("Only ECDSA-SHA256 publisher signatures are supported.");
        if (!_trustPolicy.TrustedPublisherPublicKeys.TryGetValue(signature.Publisher, out var publicKey))
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
        var manifest = _manifestParser.Parse(stream);
        return new(manifest.Id, manifest.Name, manifest.Version, !state.TryGetValue(manifest.Id, out var enabled) || enabled, true, false);
    }

    private Dictionary<string, bool> ReadState() => ModulePackageState.Read(_root);

    private Dictionary<string, ModuleManifest> ReadInstalledManifests()
    {
        if (!Directory.Exists(_root)) return new(StringComparer.OrdinalIgnoreCase);
        var manifests = new Dictionary<string, ModuleManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetDirectories(_root).Where(path => !Path.GetFileName(path).StartsWith('.')))
        {
            var manifestPath = Path.Combine(path, "module.json");
            if (!File.Exists(manifestPath)) continue;
            using var stream = File.OpenRead(manifestPath);
            var manifest = _manifestParser.Parse(stream);
            if (!manifests.TryAdd(manifest.Id, manifest)) throw new InvalidOperationException($"Duplicate installed module ID '{manifest.Id}'.");
        }
        return manifests;
    }

    private void WriteState(Dictionary<string, bool> state)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ModulePackageState.FileName);
        var temporary = Path.Combine(_root, $".{ModulePackageState.FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, StateJsonOptions);
                stream.Flush(flushToDisk: true);
            }
            _files.MoveFile(temporary, path, overwrite: true);
        }
        catch (Exception failure)
        {
            try { if (File.Exists(temporary)) _files.DeleteFile(temporary); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Module state could not be saved and its temporary file could not be cleaned up.", failure, cleanupFailure);
            }
            throw;
        }
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
