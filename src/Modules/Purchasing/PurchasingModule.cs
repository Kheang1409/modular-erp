using System.Reflection;
#pragma warning disable CA1725
using Erp.Abstractions.Events;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Persistence;
using Erp.Modules.Purchasing.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Erp.Modules.Purchasing;

public sealed class PurchasingModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new("erp.purchasing", "Purchasing", "0.1.0", "Tenant-safe purchase orders, supplier sourcing, and contract workflow.", "ERP Community", "Apache-2.0", "0.1.0", [new("erp.contacts", ">=0.1.0 <1.0.0"), new("erp.products", ">=0.1.0 <1.0.0")], [PurchasingPermissions.Read, PurchasingPermissions.Manage, PurchasingPermissions.Approve, PurchasingPermissions.Cancel, PurchasingPermissions.Archive, PurchasingPermissions.SupplierContractRead, PurchasingPermissions.SupplierContractManage, PurchasingPermissions.SupplierEvaluationRead, PurchasingPermissions.SupplierEvaluate]); public Assembly ComponentAssembly => typeof(PurchasingModule).Assembly;
    public void ConfigureServices(IServiceCollection s, IConfiguration c)
    {
        s.AddDbContext<PurchasingDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IRelationalDatabaseSession>().Connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "purchasing")));
        s.AddScoped<IPurchaseOrderService, PurchaseOrderService>(); s.AddScoped<IPurchaseOrderDirectory, PurchaseOrderDirectory>();
        s.AddScoped<IPurchaseOrderFulfillmentStatusWriter, PurchaseOrderFulfillmentStatusWriter>();
        s.AddScoped<ISupplierSourcingService, SupplierSourcingService>();
        s.AddScoped<IPurchasingReportingData, PurchasingReportingData>(); s.AddScoped<IModuleDatabaseInitializer, PurchasingDatabaseInitializer>();
    }
    public void ConfigureEndpoints(IEndpointRouteBuilder e) => e.MapPurchasingEndpoints(); public void ConfigureIntegrationEvents(IIntegrationEventRegistry e) { e.Add<PurchaseOrderConfirmedEvent>("purchasing.purchase_order.confirmed.v1"); e.Add<PurchaseOrderCancelledEvent>("purchasing.purchase_order.cancelled.v1"); e.Add<GoodsReceiptRequestedEvent>("purchasing.goods_receipt.requested.v1"); }
    public void ConfigurePermissions(IPermissionRegistry p) { p.Add(new(PurchasingPermissions.Read, "View purchase orders", Manifest.Id)); p.Add(new(PurchasingPermissions.Manage, "Manage draft purchase orders", Manifest.Id)); p.Add(new(PurchasingPermissions.Approve, "Send and confirm purchase orders", Manifest.Id)); p.Add(new(PurchasingPermissions.Cancel, "Cancel purchase orders", Manifest.Id)); p.Add(new(PurchasingPermissions.Archive, "Archive purchase orders", Manifest.Id)); p.Add(new(PurchasingPermissions.SupplierContractRead, "View supplier contracts", Manifest.Id)); p.Add(new(PurchasingPermissions.SupplierContractManage, "Create and activate supplier contracts", Manifest.Id)); p.Add(new(PurchasingPermissions.SupplierEvaluationRead, "View supplier evaluations", Manifest.Id)); p.Add(new(PurchasingPermissions.SupplierEvaluate, "Record supplier evaluations", Manifest.Id)); }
    public void ConfigureNavigation(INavigationRegistry n) { n.Add(new("Purchase orders", "/purchasing/orders", "Purchasing", 70, PurchasingPermissions.Read)); n.Add(new("Supplier contracts", "/purchasing/suppliers/contracts", "Purchasing", 71, PurchasingPermissions.SupplierContractRead)); n.Add(new("Supplier evaluations", "/purchasing/suppliers/evaluations", "Purchasing", 72, PurchasingPermissions.SupplierEvaluationRead)); }
}
public static class PurchasingPermissions { public const string Read = "purchasing.order.read"; public const string Manage = "purchasing.order.manage"; public const string Approve = "purchasing.order.approve"; public const string Cancel = "purchasing.order.cancel"; public const string Archive = "purchasing.order.archive"; public const string SupplierContractRead = "purchasing.contract.read"; public const string SupplierContractManage = "purchasing.contract.manage"; public const string SupplierEvaluationRead = "purchasing.supplier.evaluate.read"; public const string SupplierEvaluate = "purchasing.supplier.evaluate"; }
internal sealed class PurchasingDatabaseInitializer(PurchasingDbContext db) : IModuleDatabaseInitializer { public string ModuleId => "erp.purchasing"; public Task MigrateAsync(CancellationToken t) => db.Database.MigrateAsync(t); }
