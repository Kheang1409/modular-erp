using Erp.Modules.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class ReportingAuthorizationTests
{
    [Fact]
    public void EveryReportEndpointHasReportPermissionAndExportsRequireExportPermission()
    {
        var builder = WebApplication.CreateBuilder(); builder.Services.AddAuthorization(); builder.Services.AddScoped<IReportingService>(_ => null!); var app = builder.Build(); new ReportingModule().ConfigureEndpoints(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().Where(x => x.RoutePattern.RawText?.StartsWith("/api/v1/reports", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(26, endpoints.Length);
        foreach (var endpoint in endpoints)
        {
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(x => x.Policy).ToArray(); Assert.NotEmpty(policies); Assert.DoesNotContain(policies, string.IsNullOrWhiteSpace);
            if (endpoint.RoutePattern.RawText!.EndsWith(".csv", StringComparison.Ordinal)) Assert.Contains(ReportingPermissions.Export, policies);
        }
    }
}
