using Erp.Modules.Sales;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class SalesAuthorizationTests
{
    [Fact]
    public void EverySalesEndpointDeclaresExpectedPermission()
    {
        var builder = WebApplication.CreateBuilder(); builder.Services.AddAuthorization(); builder.Services.AddScoped<IQuotationService>(_ => null!); builder.Services.AddScoped<ISalesOrderService>(_ => null!); var app = builder.Build(); new SalesModule().ConfigureEndpoints(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().Where(x => x.RoutePattern.RawText?.StartsWith("/api/v1/sales", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(16, endpoints.Length); Assert.All(endpoints, x => Assert.False(string.IsNullOrWhiteSpace(Assert.Single(x.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy)));
        Assert.Equal(2, endpoints.Count(x => Assert.Single(x.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == SalesPermissions.QuotationRead));
        Assert.Equal(4, endpoints.Count(x => Assert.Single(x.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == SalesPermissions.QuotationManage));
        Assert.Equal(5, endpoints.Count(x => Assert.Single(x.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == SalesPermissions.QuotationConfirm));
        Assert.Equal(2, endpoints.Count(x => Assert.Single(x.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == SalesPermissions.OrderRead));
        Assert.Single(endpoints, x => Assert.Single(x.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == SalesPermissions.OrderManage);
        Assert.Single(endpoints, x => Assert.Single(x.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == SalesPermissions.OrderConfirm);
        Assert.Single(endpoints, x => Assert.Single(x.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy == SalesPermissions.OrderCancel);
    }
}
