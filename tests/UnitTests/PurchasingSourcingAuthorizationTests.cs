using Erp.Modules.Purchasing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class PurchasingSourcingAuthorizationTests
{
    [Fact]
    public void SupplierSourcingEndpointsRequireSpecificPermissions()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<IPurchaseOrderService>(_ => null!);
        builder.Services.AddScoped<ISupplierSourcingService>(_ => null!);
        var app = builder.Build();
        new PurchasingModule().ConfigureEndpoints(app);

        var expected = new Dictionary<(string Route, string Method), string>
        {
            [("/api/v1/purchasing/suppliers/contracts", "GET")] = PurchasingPermissions.SupplierContractRead,
            [("/api/v1/purchasing/suppliers/contracts/{id:guid}", "GET")] = PurchasingPermissions.SupplierContractRead,
            [("/api/v1/purchasing/suppliers/contracts", "POST")] = PurchasingPermissions.SupplierContractManage,
            [("/api/v1/purchasing/suppliers/contracts/{id:guid}/activate", "POST")] = PurchasingPermissions.SupplierContractManage,
            [("/api/v1/purchasing/suppliers/contracts/{id:guid}/terminate", "POST")] = PurchasingPermissions.SupplierContractManage,
            [("/api/v1/purchasing/suppliers/evaluations", "GET")] = PurchasingPermissions.SupplierEvaluationRead,
            [("/api/v1/purchasing/suppliers/evaluations/{id:guid}", "GET")] = PurchasingPermissions.SupplierEvaluationRead,
            [("/api/v1/purchasing/suppliers/evaluations", "POST")] = PurchasingPermissions.SupplierEvaluate
        };
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/purchasing/suppliers", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(expected.Count, endpoints.Length);
        foreach (var endpoint in endpoints)
        {
            var method = Assert.Single(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            var route = endpoint.RoutePattern.RawText!;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(x => x.Policy).ToArray();
            Assert.Equal(expected[(route, method)], Assert.Single(policies));
        }
    }
}
