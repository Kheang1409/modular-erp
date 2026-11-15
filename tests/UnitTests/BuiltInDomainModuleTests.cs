using Erp.Abstractions.Modules;
using Erp.ModuleSystem;
using Erp.Modules.Hcm;
using Erp.Modules.Manufacturing;
using Erp.Modules.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class BuiltInDomainModuleTests
{
    [Fact]
    public void HcmPermissionsAndEndpointsMatchManifest()
    {
        AssertModule(new HcmModule(), "/api/v1/hcm");
    }

    [Fact]
    public void ManufacturingPermissionsAndEndpointsMatchManifest()
    {
        AssertModule(new ManufacturingModule(), "/api/v1/manufacturing");
    }

    [Fact]
    public void ProjectPermissionsAndEndpointsMatchManifest()
    {
        AssertModule(new ProjectsModule(), "/api/v1/projects");
    }

    [Fact]
    public void NewModuleModelsMatchTheirMigrationSnapshots()
    {
        using var hcm = new HcmDbContext(new DbContextOptionsBuilder<HcmDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=erp").Options);
        using var manufacturing = new ManufacturingDbContext(new DbContextOptionsBuilder<ManufacturingDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=erp").Options);
        using var projects = new ProjectsDbContext(new DbContextOptionsBuilder<ProjectsDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=erp").Options);

        Assert.False(hcm.Database.HasPendingModelChanges());
        Assert.False(manufacturing.Database.HasPendingModelChanges());
        Assert.False(projects.Database.HasPendingModelChanges());
    }

    private static void AssertModule(IErpModule module, string routePrefix)
    {
        var permissionRegistry = new PermissionRegistry();
        module.ConfigurePermissions(permissionRegistry);
        var registeredPermissions = permissionRegistry.Permissions.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(module.Manifest.Permissions.Order(StringComparer.Ordinal), registeredPermissions.Order(StringComparer.Ordinal));

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        var app = builder.Build();
        module.ConfigureEndpoints(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(routePrefix, StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(x => x.Policy).ToArray();
            Assert.Single(policies);
            Assert.True(policies[0] is { } policy && registeredPermissions.Contains(policy));
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
        }
    }
}
