using Erp.Modules.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class InventoryAuthorizationTests
{
    [Fact]
    public void EveryInventoryEndpointDeclaresExpectedPermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<IInventoryService>(_ => null!);
        var app = builder.Build();
        new InventoryModule().ConfigureEndpoints(app);

        var expected = new Dictionary<(string Route, string Method), string>
        {
            [("/api/v1/inventory/warehouses", "GET")] = InventoryPermissions.Read,
            [("/api/v1/inventory/warehouses", "POST")] = InventoryPermissions.Configure,
            [("/api/v1/inventory/warehouses/{id:guid}", "PUT")] = InventoryPermissions.Configure,
            [("/api/v1/inventory/locations", "GET")] = InventoryPermissions.Read,
            [("/api/v1/inventory/source-documents", "GET")] = InventoryPermissions.Read,
            [("/api/v1/inventory/locations", "POST")] = InventoryPermissions.Configure,
            [("/api/v1/inventory/locations/{id:guid}", "PUT")] = InventoryPermissions.Configure,
            [("/api/v1/inventory/moves", "GET")] = InventoryPermissions.Read,
            [("/api/v1/inventory/moves/{id:guid}", "GET")] = InventoryPermissions.Read,
            [("/api/v1/inventory/moves", "POST")] = InventoryPermissions.Manage,
            [("/api/v1/inventory/moves/batch", "POST")] = InventoryPermissions.Manage,
            [("/api/v1/inventory/moves/{id:guid}/cancel", "POST")] = InventoryPermissions.Manage,
            [("/api/v1/inventory/moves/{id:guid}/complete", "POST")] = InventoryPermissions.Complete,
            [("/api/v1/inventory/moves/{id:guid}/correct", "POST")] = InventoryPermissions.Adjust,
            [("/api/v1/inventory/balances/{productId:guid}/{locationId:guid}", "GET")] = InventoryPermissions.Read,
            [("/api/v1/inventory/balances", "GET")] = InventoryPermissions.Read,
            [("/api/v1/inventory/reservations", "POST")] = InventoryPermissions.Reserve,
            [("/api/v1/inventory/reservations", "GET")] = InventoryPermissions.Read,
            [("/api/v1/inventory/reservations/{id:guid}", "GET")] = InventoryPermissions.Read,
            [("/api/v1/inventory/reservations/{id:guid}/release", "POST")] = InventoryPermissions.Reserve
        };

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText?.StartsWith("/api/v1/inventory", StringComparison.Ordinal) == true).ToArray();
        foreach (var endpoint in endpoints)
        {
            var method = Assert.Single(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(expected[(endpoint.RoutePattern.RawText!, method)], authorization.Policy);
        }
        Assert.Equal(expected.Count, endpoints.Length);
    }
}
