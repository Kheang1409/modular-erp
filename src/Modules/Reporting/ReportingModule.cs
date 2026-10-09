using System.Reflection;
using System.Security.Claims;
using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Reporting;

public sealed class ReportingModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new("erp.reporting", "Reporting", "0.1.0", "Scoped operational and financial reporting over verified transactions.", "ERP Community", "Apache-2.0", "0.1.0", [new("erp.sales", ">=0.1.0 <1.0.0"), new("erp.purchasing", ">=0.1.0 <1.0.0"), new("erp.inventory", ">=0.1.0 <1.0.0"), new("erp.accounting", ">=0.1.0 <1.0.0")], [ReportingPermissions.Sales, ReportingPermissions.Purchasing, ReportingPermissions.Inventory, ReportingPermissions.Financial, ReportingPermissions.Receivables, ReportingPermissions.Payables, ReportingPermissions.Cash, ReportingPermissions.Export]);
    public Assembly ComponentAssembly => typeof(ReportingModule).Assembly;
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IReportingService, ReportingService>();
        services.AddAuthorizationBuilder().AddPolicy(ReportingPermissions.DashboardPolicy, policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context => ReportingPermissions.CanViewDashboard(context.User)));
    }
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapReportingEndpoints();
    public void ConfigurePermissions(IPermissionRegistry permissions)
    {
        permissions.Add(new(ReportingPermissions.Sales, "View sales reports", Manifest.Id)); permissions.Add(new(ReportingPermissions.Purchasing, "View purchasing reports", Manifest.Id)); permissions.Add(new(ReportingPermissions.Inventory, "View inventory reports", Manifest.Id)); permissions.Add(new(ReportingPermissions.Financial, "View financial statements and ledger reports", Manifest.Id)); permissions.Add(new(ReportingPermissions.Receivables, "View receivables aging", Manifest.Id)); permissions.Add(new(ReportingPermissions.Payables, "View payables aging", Manifest.Id)); permissions.Add(new(ReportingPermissions.Cash, "View cash and payment reports", Manifest.Id)); permissions.Add(new(ReportingPermissions.Export, "Export report data", Manifest.Id));
    }
    public void ConfigureNavigation(INavigationRegistry navigation) { navigation.Add(new("Performance dashboard", "/reports/dashboard", "Reports", 99, ReportingPermissions.DashboardPolicy)); navigation.Add(new("Sales", "/reports/sales", "Reports", 100, ReportingPermissions.Sales)); navigation.Add(new("Purchasing", "/reports/purchasing", "Reports", 101, ReportingPermissions.Purchasing)); navigation.Add(new("Inventory", "/reports/inventory", "Reports", 102, ReportingPermissions.Inventory)); navigation.Add(new("Financial", "/reports/financial", "Reports", 103, ReportingPermissions.Financial)); navigation.Add(new("Receivables aging", "/reports/receivables", "Reports", 104, ReportingPermissions.Receivables)); navigation.Add(new("Payables aging", "/reports/payables", "Reports", 105, ReportingPermissions.Payables)); navigation.Add(new("Cash summary", "/reports/cash", "Reports", 106, ReportingPermissions.Cash)); }
}
public static class ReportingPermissions
{
    // Derived policy, deliberately not registered as an assignable role permission.
    public const string DashboardPolicy = "reporting.dashboard";
    public const string Sales = "reporting.sales";
    public const string Purchasing = "reporting.purchasing";
    public const string Inventory = "reporting.inventory";
    public const string Financial = "reporting.financial";
    public const string Receivables = "reporting.receivables";
    public const string Payables = "reporting.payables";
    public const string Cash = "reporting.cash";
    public const string Export = "reporting.export";

    public static bool CanViewDashboard(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true &&
        (user.HasClaim("permission", Sales) || user.HasClaim("permission", Purchasing) || user.HasClaim("permission", Inventory) ||
         user.HasClaim("permission", Financial) || user.HasClaim("permission", Receivables) || user.HasClaim("permission", Payables) || user.HasClaim("permission", Cash));
}
