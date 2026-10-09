using Erp.Abstractions.Context;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Currencies;
using Erp.Abstractions.Files;
using Erp.Abstractions.Security;
using Erp.Abstractions.Operations;
using Erp.Host.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class PlatformModuleAuthorizationTests
{
    [Fact]
    public void EveryModuleManagementEndpointRequiresModuleManagePermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IModulePackageManager>(_ => null!);
        builder.Services.AddScoped<IAuditLogWriter>(_ => null!);
        var app = builder.Build();
        PlatformEndpoints.MapModuleEndpoints(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/platform/modules", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(6, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => authorization.Policy == PlatformPermissions.ModuleManage));
    }

    [Fact]
    public void EveryPlatformApiEndpointRequiresAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IOrganizationContextReader>(_ => null!);
        builder.Services.AddSingleton<IOrganizationAdministrationService>(_ => null!);
        builder.Services.AddSingleton<IRoleAdministrationService>(_ => null!);
        builder.Services.AddSingleton<IUserAdministrationService>(_ => null!);
        builder.Services.AddSingleton<ICurrencyConfigurationService>(_ => null!);
        builder.Services.AddSingleton<IAttachmentService>(_ => null!);
        builder.Services.AddSingleton<IFileStorage>(_ => null!);
        builder.Services.AddSingleton<IAuditLogReader>(_ => null!);
        builder.Services.AddSingleton<IAuditLogWriter>(_ => null!);
        builder.Services.AddSingleton<IModulePackageManager>(_ => null!);
        builder.Services.AddSingleton<IQueueOperationsService>(_ => null!);
        builder.Services.AddSingleton<IDataMaintenanceService>(_ => null!);
        var app = builder.Build();
        app.MapPlatformEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/platform", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint => Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => !string.IsNullOrWhiteSpace(authorization.Policy)));
    }
}
