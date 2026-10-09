using Erp.Modules.Crm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class CrmAuthorizationTests
{
    [Fact]
    public void EveryCrmEndpointDeclaresAPermission()
    {
        var builder = WebApplication.CreateBuilder(); builder.Services.AddAuthorization(); builder.Services.AddScoped<ILeadService>(_ => null!); builder.Services.AddScoped<IOpportunityService>(_ => null!); builder.Services.AddScoped<IActivityService>(_ => null!); var app = builder.Build(); new CrmModule().ConfigureEndpoints(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().Where(x => x.RoutePattern.RawText?.StartsWith("/api/v1/crm/leads", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(11, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.False(string.IsNullOrWhiteSpace(Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy)));
        Assert.Equal(2, endpoints.Count(endpoint => Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == CrmPermissions.Read));
        Assert.Equal(2, endpoints.Count(endpoint => Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == CrmPermissions.Archive));
        Assert.Equal(6, endpoints.Count(endpoint => Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == CrmPermissions.Manage));
        Assert.Single(endpoints, endpoint => Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == CrmPermissions.Assign);
    }
}
