using System.Reflection;
using Erp.Abstractions.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Erp.Abstractions.Jobs;
using Erp.Abstractions.Events;

namespace Erp.ModuleSystem;

public static class ModuleServiceCollectionExtensions
{
    public static ModuleComposition AddErpModules(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] moduleAssemblies)
    {
        var discovery = new AssemblyModuleDiscovery();
        var registry = new ModuleRegistry();
        var permissions = new PermissionRegistry();
        var navigation = new NavigationRegistry();
        var jobs = new BackgroundJobRegistry();
        var events = new IntegrationEventRegistry();

        var externalPath = configuration["Modules:ExternalPath"] ?? "modules/external";
        var externalAssemblies = new ExternalModuleCatalog(new ModuleManifestParser()).Load(externalPath);
        foreach (var descriptor in discovery.Discover(moduleAssemblies.Concat(externalAssemblies)))
        {
            var module = (IErpModule)Activator.CreateInstance(descriptor.ModuleType)!;
            module.ConfigureServices(services, configuration);
            var existingPermissions = permissions.Permissions.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            module.ConfigurePermissions(permissions);
            var registeredPermissions = permissions.Permissions
                .Where(x => !existingPermissions.Contains(x.Name))
                .ToArray();
            if (registeredPermissions.Any(x => !string.Equals(x.ModuleId, module.Manifest.Id, StringComparison.OrdinalIgnoreCase)) ||
                !registeredPermissions.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(module.Manifest.Permissions))
            {
                throw new InvalidOperationException(
                    $"Module '{module.Manifest.Id}' registered permissions that do not match its manifest.");
            }
            module.ConfigureNavigation(navigation);
            module.ConfigureBackgroundJobs(jobs);
            module.ConfigureIntegrationEvents(events);
            registry.Add(module);
        }

        services.AddSingleton<IModuleDiscovery>(discovery);
        services.AddSingleton<IModuleManifestParser, ModuleManifestParser>();
        services.AddSingleton<IModuleRegistry>(registry);
        services.AddSingleton<IPermissionRegistry>(permissions);
        services.AddSingleton<INavigationRegistry>(navigation);
        services.AddSingleton<IBackgroundJobRegistry>(jobs);
        services.AddSingleton<IIntegrationEventRegistry>(events);
        return new(registry, permissions, navigation, jobs, events);
    }
}

public sealed record ModuleComposition(
    ModuleRegistry Modules,
    PermissionRegistry Permissions,
    NavigationRegistry Navigation,
    BackgroundJobRegistry Jobs,
    IntegrationEventRegistry Events);
