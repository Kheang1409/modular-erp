using System.Reflection;
#pragma warning disable CA1725
using Erp.Abstractions.Events;
using Erp.Abstractions.Modules;
using Erp.Modules.Inventory.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Erp.Modules.Inventory;

public sealed class InventoryModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new("erp.inventory", "Inventory", "0.1.0", "Ledger-derived stock, reservations and warehouse movements.", "ERP Community", "Apache-2.0", "0.1.0", [new("erp.products", ">=0.1.0 <1.0.0"), new("erp.sales", ">=0.1.0 <1.0.0"), new("erp.purchasing", ">=0.1.0 <1.0.0")], [InventoryPermissions.Read, InventoryPermissions.Configure, InventoryPermissions.Manage, InventoryPermissions.Complete, InventoryPermissions.Reserve, InventoryPermissions.Adjust]);
    public Assembly ComponentAssembly => typeof(InventoryModule).Assembly;
    public void ConfigureServices(IServiceCollection s, IConfiguration c) { var cs = c.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required."); s.AddDbContext<InventoryDbContext>(o => o.UseNpgsql(cs, p => p.MigrationsHistoryTable("__EFMigrationsHistory", "inventory"))); s.AddScoped<IInventoryService, InventoryService>(); s.AddScoped<IStockAvailability, StockAvailability>(); s.AddScoped<IInventoryReportingData, InventoryReportingData>(); s.AddScoped<IModuleDatabaseInitializer, InventoryDatabaseInitializer>(); }
    public void ConfigureEndpoints(IEndpointRouteBuilder e) => e.MapInventoryEndpoints();
    public void ConfigurePermissions(IPermissionRegistry p) { p.Add(new(InventoryPermissions.Read, "View inventory", Manifest.Id)); p.Add(new(InventoryPermissions.Configure, "Configure warehouses", Manifest.Id)); p.Add(new(InventoryPermissions.Manage, "Manage stock moves", Manifest.Id)); p.Add(new(InventoryPermissions.Complete, "Complete stock moves", Manifest.Id)); p.Add(new(InventoryPermissions.Reserve, "Reserve stock", Manifest.Id)); p.Add(new(InventoryPermissions.Adjust, "Adjust stock", Manifest.Id)); }
    public void ConfigureNavigation(INavigationRegistry n) { n.Add(new("Movements", "/inventory/moves", "Inventory", 80, InventoryPermissions.Read)); n.Add(new("Balances", "/inventory/balances", "Inventory", 81, InventoryPermissions.Read)); n.Add(new("Reservations", "/inventory/reservations", "Inventory", 82, InventoryPermissions.Read)); }
    public void ConfigureIntegrationEvents(IIntegrationEventRegistry e) { e.Add<StockReservedEvent>("inventory.stock.reserved.v1"); e.Add<StockReservationReleasedEvent>("inventory.stock.reservation_released.v1"); e.Add<StockReservationConsumedEvent>("inventory.stock.reservation_consumed.v1"); e.Add<GoodsReceivedEvent>("inventory.goods.received.v1"); e.Add<ShipmentCompletedEvent>("inventory.shipment.completed.v1"); e.Add<PurchaseOrderFulfillmentProgressEvent>("inventory.purchase_order.fulfillment_progress.v1"); e.Add<SalesOrderFulfillmentProgressEvent>("inventory.sales_order.fulfillment_progress.v1"); e.Add<StockAdjustedEvent>("inventory.stock.adjusted.v1"); e.Add<TransferCompletedEvent>("inventory.transfer.completed.v1"); }
}
public static class InventoryPermissions { public const string Read = "inventory.read"; public const string Configure = "inventory.configure"; public const string Manage = "inventory.move.manage"; public const string Complete = "inventory.move.complete"; public const string Reserve = "inventory.reserve"; public const string Adjust = "inventory.adjust"; }
internal sealed class InventoryDatabaseInitializer(InventoryDbContext db) : IModuleDatabaseInitializer { public string ModuleId => "erp.inventory"; public Task MigrateAsync(CancellationToken t) => db.Database.MigrateAsync(t); }
