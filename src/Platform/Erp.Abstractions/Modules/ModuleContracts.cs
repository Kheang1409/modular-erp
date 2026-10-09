using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Erp.Abstractions.Jobs;
using Erp.Abstractions.Events;

namespace Erp.Abstractions.Modules;

public interface IErpModule
{
    ModuleManifest Manifest { get; }
    Assembly ComponentAssembly { get; }
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
    void ConfigureEndpoints(IEndpointRouteBuilder endpoints);
    void ConfigurePermissions(IPermissionRegistry permissions);
    void ConfigureNavigation(INavigationRegistry navigation);
    void ConfigureBackgroundJobs(IBackgroundJobRegistry jobs) { }
    void ConfigureIntegrationEvents(IIntegrationEventRegistry events) { }
}

public sealed record ModuleManifest(
    string Id,
    string Name,
    string Version,
    string Description,
    string Author,
    string License,
    string MinimumPlatformVersion,
    IReadOnlyList<ModuleDependency> Dependencies,
    IReadOnlyList<string> Permissions);

public sealed record ModuleDependency(string ModuleId, string VersionRange);

public sealed record ModuleDescriptor(ModuleManifest Manifest, Type ModuleType, Assembly Assembly);

public interface IModuleDiscovery
{
    IReadOnlyList<ModuleDescriptor> Discover(IEnumerable<Assembly> assemblies);
}

public interface IModuleManifestParser
{
    ModuleManifest Parse(Stream json);
}

public interface IModuleRegistry
{
    IReadOnlyList<IErpModule> Modules { get; }
    void Add(IErpModule erpModule);
}

public interface IModuleDatabaseInitializer
{
    string ModuleId { get; }
    Task MigrateAsync(CancellationToken cancellationToken);
}

public interface IModulePackageManager
{
    IReadOnlyList<ModulePackageInfo> List();
    ModulePackageInfo Validate(string archivePath);
    ModulePackageInfo Install(string archivePath, bool update = false);
    ModulePackageInfo SetEnabled(string moduleId, bool enabled);
}

public sealed record ModulePackageInfo(
    string Id,
    string Name,
    string Version,
    bool Enabled,
    bool Installed,
    bool RestartRequired);

public interface IPermissionRegistry
{
    IReadOnlyCollection<PermissionDefinition> Permissions { get; }
    void Add(PermissionDefinition permission);
}

public sealed record PermissionDefinition(string Name, string DisplayName, string ModuleId);

public interface INavigationRegistry
{
    IReadOnlyCollection<NavigationItem> Items { get; }
    void Add(NavigationItem item);
}

public sealed record NavigationItem(string Text, string Href, string Group, int Order, string? RequiredPermission = null);

public static class PermissionEndpointExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireAuthorization(permission);
}
