using Erp.Abstractions.Modules;

namespace Erp.ModuleSystem;

public sealed class ModuleDependencyGraph
{
    private readonly SemanticVersion _platformVersion;

    public ModuleDependencyGraph(string platformVersion)
    {
        if (!SemanticVersion.TryParse(platformVersion, out var parsed))
            throw new ArgumentException("Platform version must be a semantic version.", nameof(platformVersion));
        _platformVersion = parsed!;
    }

    public IReadOnlyList<ModuleDescriptor> Order(IEnumerable<ModuleDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var modules = descriptors.ToArray();
        var duplicate = modules.GroupBy(x => x.Manifest.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate module ID '{duplicate.Key}'.");

        var byId = modules.ToDictionary(x => x.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModuleDescriptor>(modules.Length);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules) Visit(module);
        return result;

        void Visit(ModuleDescriptor module)
        {
            if (visited.Contains(module.Manifest.Id)) return;
            ModuleManifestValidator.Validate(module.Manifest);
            if (!SemanticVersion.TryParse(module.Manifest.MinimumPlatformVersion, out var minimum) ||
                _platformVersion.CompareTo(minimum!) < 0)
                throw new InvalidOperationException(
                    $"Module '{module.Manifest.Id}' requires platform {module.Manifest.MinimumPlatformVersion} or later.");
            if (!visiting.Add(module.Manifest.Id))
                throw new InvalidOperationException($"Circular module dependency detected at '{module.Manifest.Id}'.");

            foreach (var dependency in module.Manifest.Dependencies)
            {
                if (!byId.TryGetValue(dependency.ModuleId, out var required))
                    throw new InvalidOperationException(
                        $"Module '{module.Manifest.Id}' requires missing module '{dependency.ModuleId}'.");
                if (!SemanticVersion.TryParse(required.Manifest.Version, out var installed) ||
                    !SemanticVersionRange.Contains(dependency.VersionRange, installed!))
                    throw new InvalidOperationException(
                        $"Module '{module.Manifest.Id}' requires '{dependency.ModuleId}' version '{dependency.VersionRange}', but found '{required.Manifest.Version}'.");
                Visit(required);
            }

            visiting.Remove(module.Manifest.Id);
            visited.Add(module.Manifest.Id);
            result.Add(module);
        }
    }
}
