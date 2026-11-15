using Erp.Modules.Hcm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hcm.UnitTests;

public sealed class PayrollAdjustmentEndpointTests
{
    [Fact]
    public void AdjustmentRoutesHaveExplicitPermissionPolicies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        new HcmModule().ConfigureEndpoints(app);

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText?.Contains("payroll-adjustments", StringComparison.Ordinal) == true)
            .ToDictionary(endpoint => (endpoint.RoutePattern.RawText!, endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()));

        Assert.Equal(HcmPermissions.PayrollRead, Policy("GET", "/api/v1/hcm/payroll-adjustments"));
        Assert.Equal(HcmPermissions.PayrollRead, Policy("GET", "/api/v1/hcm/payroll-adjustments/{id:guid}"));
        Assert.Equal(HcmPermissions.PayrollAdjust, Policy("POST", "/api/v1/hcm/payroll-adjustments"));
        Assert.Equal(HcmPermissions.PayrollAdjust, Policy("POST", "/api/v1/hcm/payroll-adjustments/recalculate"));
        Assert.Equal(HcmPermissions.PayrollAdjust, Policy("POST", "/api/v1/hcm/payroll-adjustments/{id:guid}/submit"));
        Assert.Equal(HcmPermissions.PayrollApprove, Policy("POST", "/api/v1/hcm/payroll-adjustments/{id:guid}/approve"));
        Assert.Equal(HcmPermissions.PayrollApprove, Policy("POST", "/api/v1/hcm/payroll-adjustments/{id:guid}/reject"));
        Assert.Equal(HcmPermissions.PayrollApprove, Policy("POST", "/api/v1/hcm/payroll-adjustments/{id:guid}/post"));
        Assert.Equal(HcmPermissions.PayrollApprove, Policy("POST", "/api/v1/hcm/payroll-adjustments/{id:guid}/reverse"));
        Assert.Equal(HcmPermissions.PayrollRead, Policy("GET", "/api/v1/hcm/payroll-adjustments/{id:guid}/history"));

        string? Policy(string method, string route) => routes[(route, method)]
            .Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;
    }
}
