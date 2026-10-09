using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Erp.Modules.Purchasing.Contracts;
using Erp.Modules.Sales.Contracts;
using Microsoft.AspNetCore.Mvc;
namespace Erp.Modules.Inventory;

internal static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder e)
    {
        var g = e.MapGroup("/api/v1/inventory").WithTags("Inventory");
        g.MapGet("/warehouses", (IInventoryService s, CancellationToken t) => Run(() => s.WarehousesAsync(t))).RequirePermission(InventoryPermissions.Read);
        g.MapPost("/warehouses", (WarehouseRequest r, IInventoryService s, CancellationToken t) => Run(() => s.SaveWarehouseAsync(null, r, t), true, "/api/v1/inventory/warehouses")).RequirePermission(InventoryPermissions.Configure);
        g.MapPut("/warehouses/{id:guid}", (Guid id, WarehouseRequest r, IInventoryService s, CancellationToken t) => Run(() => s.SaveWarehouseAsync(id, r, t))).RequirePermission(InventoryPermissions.Configure);
        g.MapGet("/locations", (IInventoryService s, CancellationToken t) => Run(() => s.LocationsAsync(t))).RequirePermission(InventoryPermissions.Read);
        g.MapGet("/source-documents", async (StockMoveType type, string? search, [FromServices] ISalesOrderDirectory sales, [FromServices] IPurchaseOrderDirectory purchasing, CancellationToken token) =>
        {
            if (type == StockMoveType.Receipt) return Results.Ok((await purchasing.SearchReceivableAsync(search, 50, token)).Select(x => new SourceDocumentOption(x.Id, x.Number, x.SupplierName, x.ProductIds)));
            if (type == StockMoveType.Delivery) return Results.Ok((await sales.SearchFulfillableAsync(search, 50, token)).Select(x => new SourceDocumentOption(x.Id, x.Number, x.CustomerName, x.ProductIds)));
            return Results.Ok(Array.Empty<SourceDocumentOption>());
        }).RequirePermission(InventoryPermissions.Read);
        g.MapPost("/locations", (LocationRequest r, IInventoryService s, CancellationToken t) => Run(() => s.SaveLocationAsync(null, r, t), true, "/api/v1/inventory/locations")).RequirePermission(InventoryPermissions.Configure);
        g.MapPut("/locations/{id:guid}", (Guid id, LocationRequest r, IInventoryService s, CancellationToken t) => Run(() => s.SaveLocationAsync(id, r, t))).RequirePermission(InventoryPermissions.Configure);
        g.MapGet("/moves", (string? search, Guid? productId, Guid? warehouseId, Guid? locationId, StockMoveType? type, StockMoveState? state, DateOnly? fromDate, DateOnly? toDate, string? referenceDocumentType, Guid? referenceDocumentId, string? sort, bool descending, int page, int pageSize, IInventoryService s, CancellationToken t) => Run(() => s.SearchAsync(new(search, productId, warehouseId, locationId, type, state, fromDate, toDate, referenceDocumentType, referenceDocumentId, sort ?? "created", descending, page < 1 ? 1 : page, pageSize < 1 ? 25 : pageSize), t))).RequirePermission(InventoryPermissions.Read);
        g.MapGet("/moves/{id:guid}", async (Guid id, IInventoryService s, CancellationToken t) => await s.GetAsync(id, t) is { } x ? Results.Ok(x) : Results.NotFound()).RequirePermission(InventoryPermissions.Read);
        g.MapPost("/moves", (StockMoveRequest r, IInventoryService s, CancellationToken t) => Run(() => s.CreateAsync(r, t), true, "/api/v1/inventory/moves")).RequirePermission(InventoryPermissions.Manage);
        g.MapPost("/moves/batch", (StockMoveBatchRequest r, IInventoryService s, CancellationToken t) => Run(() => s.CreateBatchAsync(r, t), true, "/api/v1/inventory/moves")).RequirePermission(InventoryPermissions.Manage);
        g.MapPost("/moves/{id:guid}/cancel", (Guid id, MoveActionRequest r, IInventoryService s, CancellationToken t) => Run(() => s.CancelAsync(id, r.ConcurrencyStamp, t))).RequirePermission(InventoryPermissions.Manage);
        g.MapPost("/moves/{id:guid}/complete", (Guid id, CompleteMoveRequest r, IInventoryService s, CancellationToken t) => Run(() => s.CompleteAsync(id, r, t))).RequirePermission(InventoryPermissions.Complete);
        g.MapPost("/moves/{id:guid}/correct", (Guid id, IInventoryService s, CancellationToken t) => Run(() => s.CorrectAsync(id, t), true, "/api/v1/inventory/moves")).RequirePermission(InventoryPermissions.Adjust);
        g.MapGet("/balances/{productId:guid}/{locationId:guid}", (Guid productId, Guid locationId, IInventoryService s, CancellationToken t) => Run(() => s.BalanceAsync(productId, locationId, t))).RequirePermission(InventoryPermissions.Read);
        g.MapGet("/balances", (string? search, Guid? productId, Guid? warehouseId, Guid? locationId, bool includeZero, string? sort, bool descending, int page, int pageSize, IInventoryService s, CancellationToken t) => Run(() => s.BalancesAsync(new(search, productId, warehouseId, locationId, includeZero, sort ?? "sku", descending, page < 1 ? 1 : page, pageSize < 1 ? 25 : pageSize), t))).RequirePermission(InventoryPermissions.Read);
        g.MapPost("/reservations", (ReserveStockRequest r, IInventoryService s, CancellationToken t) => Run(() => s.ReserveAsync(r, t), true, "/api/v1/inventory/reservations")).RequirePermission(InventoryPermissions.Reserve);
        g.MapGet("/reservations", (ReservationState? state, Guid? productId, Guid? locationId, Guid? referenceDocumentId, int page, int pageSize, IInventoryService s, CancellationToken t) => Run(() => s.ReservationsAsync(new(state, productId, locationId, referenceDocumentId, page < 1 ? 1 : page, pageSize < 1 ? 25 : pageSize), t))).RequirePermission(InventoryPermissions.Read);
        g.MapGet("/reservations/{id:guid}", async (Guid id, IInventoryService s, CancellationToken t) => await s.ReservationAsync(id, t) is { } x ? Results.Ok(x) : Results.NotFound()).RequirePermission(InventoryPermissions.Read);
        g.MapPost("/reservations/{id:guid}/release", (Guid id, ReservationActionRequest r, IInventoryService s, CancellationToken t) => Run(() => s.ReleaseAsync(id, r, t))).RequirePermission(InventoryPermissions.Reserve);
    }
    private static async Task<IResult> Run<T>(Func<Task<T>> action, bool created = false, string? location = null) { try { var x = await action(); return created ? Results.Created(location, x) : Results.Ok(x); } catch (InventoryValidationException ex) { return Results.ValidationProblem(ex.Errors); } catch (InventoryConcurrencyException ex) { return Results.Conflict(new { error = ex.Message }); } catch (KeyNotFoundException) { return Results.NotFound(); } catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); } }
}
internal sealed record SourceDocumentOption(Guid Id, string Number, string PartyName, IReadOnlyList<Guid> ProductIds);
