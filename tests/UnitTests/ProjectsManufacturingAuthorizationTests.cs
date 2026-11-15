using Erp.Modules.Manufacturing;
using Erp.Modules.Manufacturing.Contracts;
using Erp.Modules.Projects;
using Erp.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class ProjectsManufacturingAuthorizationTests
{
    [Fact]
    public void ProjectDataModelMatchesItsCommittedMigrationSnapshot()
    {
        using var database = new ProjectsDbContext(new DbContextOptionsBuilder<ProjectsDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=erp").Options);

        Assert.False(database.Database.HasPendingModelChanges());
    }

    [Fact]
    public void ManufacturingDataModelMatchesItsCommittedMigrationSnapshot()
    {
        using var database = new ManufacturingDbContext(new DbContextOptionsBuilder<ManufacturingDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=erp").Options);

        Assert.False(database.Database.HasPendingModelChanges());
    }

    [Fact]
    public void EveryProjectEndpointRequiresTheReadOrManagePermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<IProjectsService>(_ => null!);
        var app = builder.Build();
        new ProjectsModule().ConfigureEndpoints(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/projects", StringComparison.Ordinal) == true).ToArray();

        Assert.Equal(13, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Contains(
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy,
            new[] { ProjectsPermissions.Read, ProjectsPermissions.Manage }));
        Assert.All(endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Any(method => method is "POST" or "PUT")), endpoint =>
            Assert.Equal(ProjectsPermissions.Manage, Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy));
    }

    [Fact]
    public void EveryManufacturingEndpointRequiresItsOperationPermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<IManufacturingService>(_ => null!);
        var app = builder.Build();
        new ManufacturingModule().ConfigureEndpoints(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/manufacturing", StringComparison.Ordinal) == true).ToArray();

        Assert.Equal(16, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.False(string.IsNullOrWhiteSpace(
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy)));
        Assert.Equal(ManufacturingPermissions.WorkOrderTransition,
            Assert.Single(endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/v1/manufacturing/work-orders/{id:guid}/transition")
                .Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        Assert.Equal(ManufacturingPermissions.Read,
            Assert.Single(endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/v1/manufacturing/work-orders/{id:guid}/materials")
                .Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        Assert.Equal(ManufacturingPermissions.QualityManage,
            Assert.Single(endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/v1/manufacturing/work-orders/{id:guid}/inspections" &&
                    endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"))
                .Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
    }
}
