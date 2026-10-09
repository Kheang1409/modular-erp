using Erp.Modules.Products;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class ProductsAuthorizationTests
{
    [Fact]
    public void EveryProductsEndpointDeclaresExpectedPermission()
    {
        var builder = WebApplication.CreateBuilder(); builder.Services.AddAuthorization();
        builder.Services.AddScoped<IProductService>(_ => null!); var app = builder.Build();
        new ProductsModule().ConfigureEndpoints(app);
        var expected = new Dictionary<(string, string), string>
        {
            [("/api/v1/products/options", "GET")] = ProductsPermissions.Read,
            [("/api/v1/products/", "GET")] = ProductsPermissions.Read,
            [("/api/v1/products/{id:guid}", "GET")] = ProductsPermissions.Read,
            [("/api/v1/products/", "POST")] = ProductsPermissions.Manage,
            [("/api/v1/products/{id:guid}", "PUT")] = ProductsPermissions.Manage,
            [("/api/v1/products/{id:guid}/archive", "POST")] = ProductsPermissions.Archive,
            [("/api/v1/products/{id:guid}/reactivate", "POST")] = ProductsPermissions.Archive,
            [("/api/v1/products/import", "POST")] = ProductsPermissions.Import,
            [("/api/v1/products/export", "GET")] = ProductsPermissions.Export
        };
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText?.StartsWith("/api/v1/products", StringComparison.Ordinal) == true).ToArray();
        foreach (var endpoint in endpoints)
        {
            var method = Assert.Single(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            Assert.Equal(expected[(endpoint.RoutePattern.RawText!, method)],
                Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        }
        Assert.Equal(expected.Count, endpoints.Length);
    }
}
