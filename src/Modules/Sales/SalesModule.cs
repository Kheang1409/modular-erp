using System.Reflection;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Events;
using Erp.Abstractions.Persistence;
using Erp.Modules.Sales.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Sales;

public sealed class SalesModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new("erp.sales", "Sales", "0.1.0", "Tenant-safe quotations and sales workflow.", "ERP Community", "Apache-2.0", "0.1.0", [new("erp.contacts", ">=0.1.0 <1.0.0"), new("erp.products", ">=0.1.0 <1.0.0")], [SalesPermissions.QuotationRead, SalesPermissions.QuotationManage, SalesPermissions.QuotationConfirm, SalesPermissions.OrderRead, SalesPermissions.OrderManage, SalesPermissions.OrderConfirm, SalesPermissions.OrderCancel]);
    public Assembly ComponentAssembly => typeof(SalesModule).Assembly;
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<SalesDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IRelationalDatabaseSession>().Connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "sales")));
        services.AddScoped<IQuotationService, QuotationService>(); services.AddScoped<IQuotationDirectory, QuotationDirectory>();
        services.AddScoped<ISalesOrderService, SalesOrderService>(); services.AddScoped<ISalesOrderDirectory, SalesOrderDirectory>();
        services.AddScoped<ISalesReportingData, SalesReportingData>(); services.AddScoped<IModuleDatabaseInitializer, SalesDatabaseInitializer>();
    }
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapSalesEndpoints();
    public void ConfigureIntegrationEvents(IIntegrationEventRegistry events) { events.Add<QuotationCreatedEvent>("sales.quotation.created.v1"); events.Add<QuotationSentEvent>("sales.quotation.sent.v1"); events.Add<QuotationAcceptedEvent>("sales.quotation.accepted.v1"); events.Add<SalesOrderConfirmedEvent>("sales.order.confirmed.v1"); events.Add<SalesOrderCancelledEvent>("sales.order.cancelled.v1"); }
    public void ConfigurePermissions(IPermissionRegistry permissions) { permissions.Add(new(SalesPermissions.QuotationRead, "View quotations", Manifest.Id)); permissions.Add(new(SalesPermissions.QuotationManage, "Manage draft quotations", Manifest.Id)); permissions.Add(new(SalesPermissions.QuotationConfirm, "Send, accept, reject, expire, or cancel quotations", Manifest.Id)); permissions.Add(new(SalesPermissions.OrderRead, "View sales orders", Manifest.Id)); permissions.Add(new(SalesPermissions.OrderManage, "Create sales orders", Manifest.Id)); permissions.Add(new(SalesPermissions.OrderConfirm, "Confirm sales orders", Manifest.Id)); permissions.Add(new(SalesPermissions.OrderCancel, "Cancel sales orders", Manifest.Id)); }
    public void ConfigureNavigation(INavigationRegistry navigation) { navigation.Add(new("Quotations", "/sales/quotations", "Sales", 60, SalesPermissions.QuotationRead)); navigation.Add(new("Sales orders", "/sales/orders", "Sales", 61, SalesPermissions.OrderRead)); }
}
public static class SalesPermissions { public const string QuotationRead = "sales.quotation.read"; public const string QuotationManage = "sales.quotation.manage"; public const string QuotationConfirm = "sales.quotation.confirm"; public const string OrderRead = "sales.order.read"; public const string OrderManage = "sales.order.manage"; public const string OrderConfirm = "sales.order.confirm"; public const string OrderCancel = "sales.order.cancel"; }
internal sealed class SalesDatabaseInitializer(SalesDbContext database) : IModuleDatabaseInitializer { public string ModuleId => "erp.sales"; public Task MigrateAsync(CancellationToken token) => database.Database.MigrateAsync(token); }
