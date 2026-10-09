using System.Reflection;
using Erp.Abstractions.Modules;

namespace Erp.ModuleSystem;

public sealed class AssemblyModuleDiscovery(string platformVersion = "0.1.0") : IModuleDiscovery
{
    public IReadOnlyList<ModuleDescriptor> Discover(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var descriptors = assemblies
            .Distinct()
            .SelectMany(assembly => assembly.DefinedTypes
                .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IErpModule).IsAssignableFrom(type))
                .Select(type => CreateDescriptor(type, assembly)))
            .ToArray();
        return new ModuleDependencyGraph(platformVersion).Order(descriptors);
    }

    private static ModuleDescriptor CreateDescriptor(TypeInfo type, Assembly assembly)
    {
        if (Activator.CreateInstance(type.AsType()) is not IErpModule module)
            throw new InvalidOperationException($"Module '{type.FullName}' must have a public parameterless constructor.");
        ModuleManifestValidator.Validate(module.Manifest);
        return new(module.Manifest, type.AsType(), assembly);
    }
}
