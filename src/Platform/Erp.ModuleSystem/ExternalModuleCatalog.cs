using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Erp.Abstractions.Modules;

namespace Erp.ModuleSystem;

#pragma warning disable CA1822
public sealed class ExternalModuleCatalog(IModuleManifestParser manifestParser)
{
    public IReadOnlyList<Assembly> Load(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        using var lease = ModuleRootSynchronization.Acquire(root);
        if (!Directory.Exists(root)) return [];
        var disabled = ModulePackageManager.DisabledModules(root);
        var assemblies = new List<Assembly>();
        foreach (var packagePath in Directory.GetDirectories(root)
                     .Where(path => !Path.GetFileName(path).StartsWith('.') && !disabled.Contains(Path.GetFileName(path)))
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            assemblies.AddRange(LoadPackage(root, packagePath));
        return assemblies;
    }

    internal Assembly[] LoadSingle(string root, string packagePath) => LoadPackage(Path.GetFullPath(root), Path.GetFullPath(packagePath));

    private Assembly[] LoadPackage(string root, string packagePath)
    {
        EnsureWithin(root, packagePath);
        var manifestPath = Path.Combine(packagePath, "module.json");
        var checksumPath = Path.Combine(packagePath, "module.sha256");
        if (!File.Exists(manifestPath) || !File.Exists(checksumPath))
            throw new InvalidOperationException($"External module folder '{Path.GetFileName(packagePath)}' requires module.json and module.sha256.");
        ModuleManifest manifest;
        using (var stream = File.OpenRead(manifestPath)) manifest = manifestParser.Parse(stream);

        var expectedChecksums = ParseChecksums(packagePath, checksumPath);
        var libraryPath = Path.Combine(packagePath, "lib");
        if (!Directory.Exists(libraryPath)) throw new InvalidOperationException($"External module '{manifest.Id}' has no lib directory.");
        var dllPaths = Directory.GetFiles(libraryPath, "*.dll", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (dllPaths.Length != 1) throw new InvalidOperationException($"External module '{manifest.Id}' must contain exactly one module assembly under lib.");
        var actualRelativePaths = dllPaths.Select(path => Relative(packagePath, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualRelativePaths.SetEquals(expectedChecksums.Keys))
            throw new InvalidOperationException($"External module '{manifest.Id}' checksum inventory does not exactly match its DLL files.");
        foreach (var dllPath in dllPaths)
        {
            using var stream = File.OpenRead(dllPath);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual), Convert.FromHexString(expectedChecksums[Relative(packagePath, dllPath)])))
                throw new InvalidOperationException($"External module '{manifest.Id}' failed checksum validation for '{Relative(packagePath, dllPath)}'.");
        }

        var loadContext = new ModuleLoadContext(dllPaths[0]);
        Assembly[] loaded;
        using (var assemblyStream = File.OpenRead(dllPaths[0]))
            loaded = [loadContext.LoadFromStream(assemblyStream)];
        var moduleTypes = loaded.SelectMany(assembly => assembly.DefinedTypes)
            .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IErpModule).IsAssignableFrom(type))
            .ToArray();
        if (moduleTypes.Length != 1)
            throw new InvalidOperationException($"External module '{manifest.Id}' must contain exactly one IErpModule implementation.");
        if (Activator.CreateInstance(moduleTypes[0].AsType()) is not IErpModule module)
            throw new InvalidOperationException($"External module '{manifest.Id}' requires a public parameterless module constructor.");
        if (!ManifestMatches(manifest, module.Manifest))
            throw new InvalidOperationException($"External module '{manifest.Id}' assembly metadata does not match module.json.");
        return loaded;
    }

    private static Dictionary<string, string> ParseChecksums(string packagePath, string checksumPath)
    {
        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(checksumPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64)
                throw new InvalidOperationException("module.sha256 contains an invalid checksum line.");
            try
            {
                if (Convert.FromHexString(line[..64]).Length != 32)
                    throw new InvalidOperationException("module.sha256 contains an invalid checksum line.");
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("module.sha256 contains an invalid checksum line.", exception);
            }
            var relative = line[(separator + 2)..].Replace('\\', '/');
            if (!relative.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("module.sha256 may reference only files beneath lib.");
            var fullPath = Path.GetFullPath(Path.Combine(packagePath, relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureWithin(packagePath, fullPath);
            if (!checksums.TryAdd(relative, line[..64].ToUpperInvariant()))
                throw new InvalidOperationException($"module.sha256 contains duplicate path '{relative}'.");
        }
        return checksums;
    }

    private static bool ManifestMatches(ModuleManifest left, ModuleManifest right) =>
        left.Id == right.Id && left.Name == right.Name && left.Version == right.Version &&
        left.Description == right.Description && left.Author == right.Author && left.License == right.License &&
        left.MinimumPlatformVersion == right.MinimumPlatformVersion &&
        left.Dependencies.SequenceEqual(right.Dependencies) && left.Permissions.SequenceEqual(right.Permissions);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void EnsureWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("External module path escapes the configured module root.");
    }

    private sealed class ModuleLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, typeof(IErpModule).Assembly.GetName().Name, StringComparison.Ordinal))
                return typeof(IErpModule).Assembly;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
