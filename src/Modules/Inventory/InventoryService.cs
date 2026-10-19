using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.Abstractions.Persistence;
using Erp.Abstractions.Sequences;
using Erp.Modules.Products.Contracts;
using Erp.Modules.Purchasing.Contracts;
using Erp.Modules.Sales.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
namespace Erp.Modules.Inventory;

public interface IInventoryService
{
    Task<IReadOnlyList<WarehouseDto>> WarehousesAsync(CancellationToken t); Task<WarehouseDto> SaveWarehouseAsync(Guid? id, WarehouseRequest r, CancellationToken t);
    Task<IReadOnlyList<LocationDto>> LocationsAsync(CancellationToken t); Task<LocationDto> SaveLocationAsync(Guid? id, LocationRequest r, CancellationToken t);
    Task<StockMovePage> SearchAsync(StockMoveQuery q, CancellationToken t); Task<StockMoveDto?> GetAsync(Guid id, CancellationToken t); Task<StockMoveDto> CreateAsync(StockMoveRequest r, CancellationToken t); Task<StockMoveBatchDto> CreateBatchAsync(StockMoveBatchRequest r, CancellationToken t); Task<StockMoveDto> CancelAsync(Guid id, string stamp, CancellationToken t); Task<StockMoveDto> CompleteAsync(Guid id, CompleteMoveRequest r, CancellationToken t); Task<StockMoveDto> CorrectAsync(Guid id, CancellationToken t);
    Task<StockBalanceDto> BalanceAsync(Guid productId, Guid locationId, CancellationToken t); Task<StockBalancePage> BalancesAsync(StockBalanceQuery q, CancellationToken t); Task<ReservationPage> ReservationsAsync(ReservationQuery q, CancellationToken t); Task<ReservationDto?> ReservationAsync(Guid id, CancellationToken t); Task<ReservationDto> ReserveAsync(ReserveStockRequest r, CancellationToken t); Task<ReservationDto> ReleaseAsync(Guid id, ReservationActionRequest r, CancellationToken t);
}

public sealed class InventoryService(InventoryDbContext db, ITenantContext tenant, ICompanyContext company,
    IProductDirectory products, ISalesOrderDirectory sales, IPurchaseOrderDirectory purchasing, ISequenceService sequences,
    IClock clock, IAuditLogWriter audit, IOutboxWriter outbox, IHttpContextAccessor http,
    IBusinessTransactionCoordinator transactions) : IInventoryService
{
    public async Task<IReadOnlyList<WarehouseDto>> WarehousesAsync(CancellationToken t) => (await db.Warehouses.AsNoTracking().OrderBy(x => x.Code).ToListAsync(t)).Select(WarehouseDtoOf).ToList();
    public async Task<WarehouseDto> SaveWarehouseAsync(Guid? id, WarehouseRequest r, CancellationToken t)
    {
        ValidateText(r.Code, r.Name, r.ConcurrencyStamp, id.HasValue);
        await using var transaction = await transactions.BeginAsync(db, t);
        Warehouse x; if (id is null) { x = new() { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), Code = r.Code!.Trim().ToUpperInvariant(), Name = r.Name!.Trim(), IsActive = r.IsActive, ConcurrencyStamp = Stamp() }; db.Add(x); } else { x = await db.Warehouses.SingleOrDefaultAsync(v => v.Id == id, t) ?? throw new KeyNotFoundException(); Check(x.ConcurrencyStamp, r.ConcurrencyStamp!); if (x.IsActive && !r.IsActive && await db.Locations.AnyAsync(v => v.WarehouseId == x.Id && v.IsActive, t)) throw Error("isActive", "Deactivate all warehouse locations first."); x.Code = r.Code!.Trim().ToUpperInvariant(); x.Name = r.Name!.Trim(); x.IsActive = r.IsActive; x.ConcurrencyStamp = Stamp(); }
        await Save(t); await Audit("inventory.warehouse.saved", x.Id, WarehouseDtoOf(x), t);
        var result = WarehouseDtoOf(x);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<IReadOnlyList<LocationDto>> LocationsAsync(CancellationToken t) => (await db.Locations.AsNoTracking().OrderBy(x => x.Code).ToListAsync(t)).Select(LocationDtoOf).ToList();
    public async Task<LocationDto> SaveLocationAsync(Guid? id, LocationRequest r, CancellationToken t)
    {
        ValidateText(r.Code, r.Name, r.ConcurrencyStamp, id.HasValue); if (r.Type == LocationType.Internal && r.WarehouseId is null) throw Error("warehouseId", "Internal locations require a warehouse.");
        await using var transaction = await transactions.BeginAsync(db, t);
        if (r.WarehouseId is not null && !await db.Warehouses.AnyAsync(x => x.Id == r.WarehouseId && (!r.IsActive || x.IsActive), t)) throw Error("warehouseId", "An active warehouse in the current company is required.");
        StockLocation x; if (id is null) { x = new() { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), WarehouseId = r.WarehouseId, Code = r.Code!.Trim().ToUpperInvariant(), Name = r.Name!.Trim(), Type = r.Type, IsActive = r.IsActive, ConcurrencyStamp = Stamp() }; db.Add(x); } else { x = await db.Locations.SingleOrDefaultAsync(v => v.Id == id, t) ?? throw new KeyNotFoundException(); Check(x.ConcurrencyStamp, r.ConcurrencyStamp!); if (x.IsActive && !r.IsActive) { var hasReservations = await db.Reservations.AnyAsync(v => v.LocationId == x.Id && v.State == ReservationState.Active, t); var hasDraftMoves = await db.StockMoves.AnyAsync(v => v.State == StockMoveState.Draft && (v.SourceLocationId == x.Id || v.DestinationLocationId == x.Id), t); if (hasReservations || hasDraftMoves) throw Error("isActive", "Release active reservations and complete or cancel draft movements before deactivating this location."); } x.WarehouseId = r.WarehouseId; x.Code = r.Code!.Trim().ToUpperInvariant(); x.Name = r.Name!.Trim(); x.Type = r.Type; x.IsActive = r.IsActive; x.ConcurrencyStamp = Stamp(); }
        await Save(t); await Audit("inventory.location.saved", x.Id, LocationDtoOf(x), t);
        var result = LocationDtoOf(x);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<StockMovePage> SearchAsync(StockMoveQuery q, CancellationToken t) { if (q.Page < 1 || q.PageSize is < 1 or > 100) throw Error("page", "Invalid paging parameters."); if (q.FromDate is DateOnly from && q.ToDate is DateOnly to && from > to) throw Error("date", "From date must not be after to date."); var s = db.StockMoves.AsNoTracking().AsQueryable(); var search = Clean(q.Search); if (search is not null) { var pattern = $"%{search}%"; s = s.Where(x => EF.Functions.ILike(x.Number, pattern) || EF.Functions.ILike(x.ProductSku, pattern) || x.Notes != null && EF.Functions.ILike(x.Notes, pattern)); } if (q.ProductId is not null) s = s.Where(x => x.ProductId == q.ProductId); if (q.WarehouseId is Guid warehouse) { var locations = db.Locations.Where(x => x.WarehouseId == warehouse).Select(x => x.Id); s = s.Where(x => x.SourceLocationId != null && locations.Contains(x.SourceLocationId.Value) || x.DestinationLocationId != null && locations.Contains(x.DestinationLocationId.Value)); } if (q.LocationId is not null) s = s.Where(x => x.SourceLocationId == q.LocationId || x.DestinationLocationId == q.LocationId); if (q.Type is not null) s = s.Where(x => x.Type == q.Type); if (q.State is not null) s = s.Where(x => x.State == q.State); if (q.FromDate is DateOnly minimum) s = s.Where(x => x.BusinessDate >= minimum); if (q.ToDate is DateOnly maximum) s = s.Where(x => x.BusinessDate <= maximum); if (!string.IsNullOrWhiteSpace(q.ReferenceDocumentType)) s = s.Where(x => x.ReferenceDocumentType == CanonicalDocumentType(q.ReferenceDocumentType)); if (q.ReferenceDocumentId is not null) s = s.Where(x => x.ReferenceDocumentId == q.ReferenceDocumentId); s = (q.Sort.ToLowerInvariant(), q.Descending) switch { ("number", false) => s.OrderBy(x => x.Number), ("number", true) => s.OrderByDescending(x => x.Number), ("date", false) => s.OrderBy(x => x.BusinessDate).ThenBy(x => x.Number), ("date", true) => s.OrderByDescending(x => x.BusinessDate).ThenByDescending(x => x.Number), ("sku", false) => s.OrderBy(x => x.ProductSku).ThenBy(x => x.Number), ("sku", true) => s.OrderByDescending(x => x.ProductSku).ThenByDescending(x => x.Number), ("created", false) => s.OrderBy(x => x.CreatedAtUtc), ("created", true) => s.OrderByDescending(x => x.CreatedAtUtc), _ => throw Error("sort", "Sort must be created, number, date, or sku.") }; var count = await s.CountAsync(t); var rows = await s.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(t); return new(q.Page, q.PageSize, count, rows.Select(MoveDto).ToList()); }
    public async Task<StockMoveDto?> GetAsync(Guid id, CancellationToken t) => (await db.StockMoves.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, t)) is { } x ? MoveDto(x) : null;
    public async Task<StockMoveDto> CreateAsync(StockMoveRequest r, CancellationToken t)
    {
        await using var transaction = await transactions.BeginAsync(db, t);
        await ValidateMove(r, t); var x = await NewMove(r, t); db.Add(x); await Save(t); await Audit("inventory.move.created", x.Id, MoveDto(x), t);
        var result = MoveDto(x);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<StockMoveBatchDto> CreateBatchAsync(StockMoveBatchRequest r, CancellationToken t)
    {
        if (r.Lines is not { Count: > 0 and <= 100 }) throw Error("lines", "A batch requires between 1 and 100 movement lines.");
        if (r.Lines.Select(x => x.ProductId).Distinct().Count() != r.Lines.Count) throw Error("lines", "Each product may appear only once in a movement batch.");
        var requests = r.Lines.Select(x => new StockMoveRequest(r.Type, x.ProductId, x.Quantity, r.SourceLocationId, r.DestinationLocationId, r.BusinessDate, r.ReferenceDocumentType, r.ReferenceDocumentId, r.Notes)).ToList();
        await using var transaction = await transactions.BeginAsync(db, t);
        foreach (var request in requests) await ValidateMove(request, t);
        var moves = new List<StockMove>(requests.Count);
        foreach (var request in requests) { var move = await NewMove(request, t); moves.Add(move); db.Add(move); }
        await Save(t);
        foreach (var move in moves) await Audit("inventory.move.created", move.Id, MoveDto(move), t);
        var result = new StockMoveBatchDto(moves.Select(MoveDto).ToList());
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<StockMoveDto> CancelAsync(Guid id, string stamp, CancellationToken t)
    {
        await using var transaction = await transactions.BeginAsync(db, t);
        var x = await FindMove(id, t); Check(x.ConcurrencyStamp, stamp); if (x.State != StockMoveState.Draft) throw Error("state", "Only draft moves can be cancelled."); x.State = StockMoveState.Cancelled; x.ConcurrencyStamp = Stamp(); await Save(t); await Audit("inventory.move.cancelled", x.Id, MoveDto(x), t);
        var result = MoveDto(x);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<StockMoveDto> CompleteAsync(Guid id, CompleteMoveRequest r, CancellationToken t)
    {
        if (string.IsNullOrWhiteSpace(r.IdempotencyKey) || r.IdempotencyKey.Length > 100) throw Error("idempotencyKey", "An idempotency key up to 100 characters is required.");
        await using var transaction = await transactions.BeginAsync(db, t);
        var existing = await db.StockMoves.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, t) ?? throw new KeyNotFoundException();
        if (existing.State == StockMoveState.Completed && existing.CompletionKey == r.IdempotencyKey.Trim())
        {
            var replay = MoveDto(existing);
            await transaction.CommitAsync(t);
            return replay;
        }
        Check(existing.ConcurrencyStamp, r.ConcurrencyStamp); if (existing.State != StockMoveState.Draft) throw Error("state", "Only draft moves can be completed.");
        var x = await db.StockMoves.SingleAsync(v => v.Id == id, t); var sourceType = await ValidateStoredMoveLocations(x, t); FulfillmentProgress? progress = null;
        if (x.ReferenceDocumentId is Guid documentId && x.ReferenceDocumentType is "PurchaseOrder" or "SalesOrder") { await LockDocument(x.ReferenceDocumentType, documentId, x.ProductId, t); progress = await ValidateFulfillment(x, documentId, t); }
        if (x.SourceLocationId is Guid source && sourceType == LocationType.Internal) { await Lock(x.ProductId, source, t); if (x.Type == StockMoveType.Delivery && x.ReferenceDocumentId is Guid orderId && x.ReferenceDocumentType == "SalesOrder") await ConsumeReservations(x, orderId, source, t); else { var balance = await BalanceAsync(x.ProductId, source, t); if (balance.Available < x.Quantity) throw Error("quantity", "Insufficient available stock."); } }
        x.State = StockMoveState.Completed; x.CompletionKey = r.IdempotencyKey.Trim(); x.CompletedAtUtc = clock.UtcNow; x.CompletedBy = User(); x.ConcurrencyStamp = Stamp(); await Save(t); AddEvent(x, progress, sourceType); await Audit("inventory.move.completed", x.Id, MoveDto(x), t);
        var result = MoveDto(x);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<StockMoveDto> CorrectAsync(Guid id, CancellationToken t)
    {
        await using var transaction = await transactions.BeginAsync(db, t);
        await LockCorrection(id, t);
        if (await db.StockMoves.AsNoTracking().SingleOrDefaultAsync(x => x.ReversesMoveId == id, t) is { } prior)
        {
            var replay = MoveDto(prior);
            await transaction.CommitAsync(t);
            return replay;
        }
        var original = await db.StockMoves.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, t) ?? throw new KeyNotFoundException();
        if (original.State != StockMoveState.Completed) throw Error("state", "Only completed moves can be corrected.");
        var reverse = new StockMove { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), Number = await sequences.NextAsync(new("inventory.move", "MOVE-{yyyy}-", 7, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)), t), Type = ReverseType(original), State = StockMoveState.Draft, ProductId = original.ProductId, ProductSku = original.ProductSku, UnitCode = original.UnitCode, Quantity = original.Quantity, SourceLocationId = original.DestinationLocationId, DestinationLocationId = original.SourceLocationId, ReferenceDocumentType = "StockMove", ReferenceDocumentId = original.Id, BusinessDate = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), Notes = $"Compensating reversal of {original.Number}", ReversesMoveId = original.Id, CreatedAtUtc = clock.UtcNow, CreatedBy = User(), ConcurrencyStamp = Stamp() };
        db.StockMoves.Add(reverse);
        await Save(t);
        await Audit("inventory.move.correction_created", reverse.Id, MoveDto(reverse), t);
        var result = MoveDto(reverse);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<StockBalanceDto> BalanceAsync(Guid productId, Guid locationId, CancellationToken t) { var inbound = await db.StockMoves.Where(x => x.ProductId == productId && x.DestinationLocationId == locationId && x.State == StockMoveState.Completed).SumAsync(x => (decimal?)x.Quantity, t) ?? 0; var outbound = await db.StockMoves.Where(x => x.ProductId == productId && x.SourceLocationId == locationId && x.State == StockMoveState.Completed).SumAsync(x => (decimal?)x.Quantity, t) ?? 0; var reserved = await db.Reservations.Where(x => x.ProductId == productId && x.LocationId == locationId && x.State == ReservationState.Active).SumAsync(x => (decimal?)(x.Quantity - x.ConsumedQuantity), t) ?? 0; var onHand = inbound - outbound; return new(productId, locationId, onHand, reserved, onHand - reserved); }
    public async Task<StockBalancePage> BalancesAsync(StockBalanceQuery q, CancellationToken t)
    {
        if (q.Page < 1 || q.PageSize is < 1 or > 100) throw Error("page", "Invalid paging parameters.");
        var locations = await (from location in db.Locations.AsNoTracking() join warehouse in db.Warehouses.AsNoTracking() on location.WarehouseId equals warehouse.Id where location.Type == LocationType.Internal select new { Location = location, Warehouse = warehouse }).ToListAsync(t);
        var locationIds = locations.Select(x => x.Location.Id).ToHashSet();
        var moves = await db.StockMoves.AsNoTracking().Where(x => x.State == StockMoveState.Completed && (x.SourceLocationId != null && locationIds.Contains(x.SourceLocationId.Value) || x.DestinationLocationId != null && locationIds.Contains(x.DestinationLocationId.Value))).Select(x => new { x.ProductId, x.ProductSku, x.UnitCode, x.Quantity, x.SourceLocationId, x.DestinationLocationId }).ToListAsync(t);
        var reservations = await db.Reservations.AsNoTracking().Where(x => x.State == ReservationState.Active && locationIds.Contains(x.LocationId)).Select(x => new { x.ProductId, x.LocationId, Remaining = x.Quantity - x.ConsumedQuantity }).ToListAsync(t);
        var productDetails = moves.GroupBy(x => x.ProductId).ToDictionary(x => x.Key, x => (x.First().ProductSku, x.First().UnitCode));
        var keys = moves.SelectMany(x => new[] { x.SourceLocationId, x.DestinationLocationId }.Where(id => id is Guid value && locationIds.Contains(value)).Select(id => (x.ProductId, LocationId: id!.Value))).Concat(reservations.Select(x => (x.ProductId, x.LocationId))).Distinct().ToList();
        var local = keys.Select(key =>
        {
            var location = locations.Single(x => x.Location.Id == key.LocationId);
            var onHand = moves.Where(x => x.ProductId == key.ProductId && x.DestinationLocationId == key.LocationId).Sum(x => x.Quantity) - moves.Where(x => x.ProductId == key.ProductId && x.SourceLocationId == key.LocationId).Sum(x => x.Quantity);
            var reserved = reservations.Where(x => x.ProductId == key.ProductId && x.LocationId == key.LocationId).Sum(x => x.Remaining);
            var details = productDetails[key.ProductId];
            return new { key.ProductId, details.ProductSku, details.UnitCode, Location = location.Location, Warehouse = location.Warehouse, OnHand = onHand, Reserved = reserved };
        }).ToList();
        var warehouseTotals = local.GroupBy(x => (x.ProductId, x.Warehouse.Id)).ToDictionary(x => x.Key, x => (OnHand: x.Sum(v => v.OnHand), Reserved: x.Sum(v => v.Reserved)));
        var rows = local.Select(x => { var total = warehouseTotals[(x.ProductId, x.Warehouse.Id)]; return new StockBalanceRowDto(x.ProductId, x.ProductSku, x.UnitCode, x.Location.Id, x.Location.Code, x.Location.Name, x.Warehouse.Id, x.Warehouse.Code, x.Warehouse.Name, x.OnHand, x.Reserved, x.OnHand - x.Reserved, total.OnHand, total.Reserved, total.OnHand - total.Reserved); }).AsEnumerable();
        var search = Clean(q.Search); if (search is not null) rows = rows.Where(x => x.ProductSku.Contains(search, StringComparison.OrdinalIgnoreCase) || x.LocationCode.Contains(search, StringComparison.OrdinalIgnoreCase) || x.LocationName.Contains(search, StringComparison.OrdinalIgnoreCase) || x.WarehouseCode.Contains(search, StringComparison.OrdinalIgnoreCase) || x.WarehouseName.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (q.ProductId is not null) rows = rows.Where(x => x.ProductId == q.ProductId); if (q.WarehouseId is not null) rows = rows.Where(x => x.WarehouseId == q.WarehouseId); if (q.LocationId is not null) rows = rows.Where(x => x.LocationId == q.LocationId); if (!q.IncludeZero) rows = rows.Where(x => x.OnHand != 0 || x.Reserved != 0);
        rows = (q.Sort.ToLowerInvariant(), q.Descending) switch { ("sku", false) => rows.OrderBy(x => x.ProductSku).ThenBy(x => x.LocationCode), ("sku", true) => rows.OrderByDescending(x => x.ProductSku).ThenByDescending(x => x.LocationCode), ("location", false) => rows.OrderBy(x => x.LocationCode).ThenBy(x => x.ProductSku), ("location", true) => rows.OrderByDescending(x => x.LocationCode).ThenByDescending(x => x.ProductSku), ("warehouse", false) => rows.OrderBy(x => x.WarehouseCode).ThenBy(x => x.ProductSku).ThenBy(x => x.LocationCode), ("warehouse", true) => rows.OrderByDescending(x => x.WarehouseCode).ThenByDescending(x => x.ProductSku).ThenByDescending(x => x.LocationCode), ("available", false) => rows.OrderBy(x => x.Available).ThenBy(x => x.ProductSku), ("available", true) => rows.OrderByDescending(x => x.Available).ThenBy(x => x.ProductSku), _ => throw Error("sort", "Sort must be sku, location, warehouse, or available.") };
        var materialized = rows.ToList(); return new(q.Page, q.PageSize, materialized.Count, materialized.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToList());
    }
    public async Task<ReservationPage> ReservationsAsync(ReservationQuery q, CancellationToken t) { if (q.Page < 1 || q.PageSize is < 1 or > 100) throw Error("page", "Invalid paging parameters."); var query = db.Reservations.AsNoTracking().AsQueryable(); if (q.State is not null) query = query.Where(x => x.State == q.State); if (q.ProductId is not null) query = query.Where(x => x.ProductId == q.ProductId); if (q.LocationId is not null) query = query.Where(x => x.LocationId == q.LocationId); if (q.ReferenceDocumentId is not null) query = query.Where(x => x.ReferenceDocumentId == q.ReferenceDocumentId); var total = await query.CountAsync(t); var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(t); return new(q.Page, q.PageSize, total, rows.Select(ReservationDtoOf).ToList()); }
    public async Task<ReservationDto?> ReservationAsync(Guid id, CancellationToken t) => (await db.Reservations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, t)) is { } x ? ReservationDtoOf(x) : null;
    public async Task<ReservationDto> ReserveAsync(ReserveStockRequest r, CancellationToken t)
    {
        if (r.Quantity <= 0) throw Error("quantity", "Quantity must be greater than zero.");
        ValidateKey(r.IdempotencyKey);
        await using var transaction = await transactions.BeginAsync(db, t);
        await Product(r.ProductId, t);
        await InternalLocation(r.LocationId, t);
        if (!string.Equals(r.ReferenceDocumentType, "SalesOrder", StringComparison.OrdinalIgnoreCase) || r.ReferenceDocumentId is not Guid orderId) throw Error("referenceDocumentId", "Reservations must reference a confirmed sales order.");
        var order = await sales.FindAsync(orderId, t);
        if (order is null || order.State is not ("Confirmed" or "PartiallyFulfilled")) throw Error("referenceDocumentId", "A confirmed sales order is required.");
        ValidateDocumentLine(order.Lines.Select(x => (x.ProductId, x.UnitCode, x.Quantity)), r.ProductId, r.Quantity);
        var key = r.IdempotencyKey.Trim();
        await LockReservationKey(key, t);
        if (await db.Reservations.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, t) is { } prior)
        {
            if (prior.ProductId != r.ProductId || prior.LocationId != r.LocationId || prior.Quantity != r.Quantity || prior.ReferenceDocumentType != "SalesOrder" || prior.ReferenceDocumentId != orderId) throw Error("idempotencyKey", "The idempotency key has already been used for a different reservation request.");
            var replay = ReservationDtoOf(prior);
            await transaction.CommitAsync(t);
            return replay;
        }
        await LockDocument("SalesOrder", orderId, r.ProductId, t);
        await Lock(r.ProductId, r.LocationId, t);
        var fulfilled = await db.StockMoves.Where(x => x.ReferenceDocumentType == "SalesOrder" && x.ReferenceDocumentId == orderId && x.ProductId == r.ProductId && x.Type == StockMoveType.Delivery && x.State == StockMoveState.Completed).SumAsync(x => (decimal?)x.Quantity, t) ?? 0;
        var activeForOrder = await db.Reservations.Where(x => x.ReferenceDocumentId == orderId && x.ProductId == r.ProductId && x.State == ReservationState.Active).SumAsync(x => (decimal?)(x.Quantity - x.ConsumedQuantity), t) ?? 0;
        var ordered = order.Lines.Where(x => x.ProductId == r.ProductId).Sum(x => x.Quantity);
        if (fulfilled + activeForOrder + r.Quantity > ordered) throw Error("quantity", "Reservation exceeds the remaining sales-order quantity.");
        var balance = await BalanceAsync(r.ProductId, r.LocationId, t);
        if (balance.Available < r.Quantity) throw Error("quantity", "Reservation exceeds available stock.");
        var x = new StockReservation { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), ProductId = r.ProductId, LocationId = r.LocationId, Quantity = r.Quantity, State = ReservationState.Active, IdempotencyKey = key, ReferenceDocumentType = "SalesOrder", ReferenceDocumentId = orderId, CreatedAtUtc = clock.UtcNow, ConcurrencyStamp = Stamp() };
        db.Add(x);
        await Save(t);
        outbox.Add(new StockReservedEvent(Guid.NewGuid(), clock.UtcNow, x.Id, x.ProductId, x.LocationId, x.Quantity));
        await Audit("inventory.stock.reserved", x.Id, ReservationDtoOf(x), t);
        var result = ReservationDtoOf(x);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<ReservationDto> ReleaseAsync(Guid id, ReservationActionRequest r, CancellationToken t)
    {
        ValidateKey(r.IdempotencyKey);
        await using var transaction = await transactions.BeginAsync(db, t);
        var existing = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, t) ?? throw new KeyNotFoundException();
        if (existing.State == ReservationState.Released && existing.ReleaseKey == r.IdempotencyKey.Trim())
        {
            var replay = ReservationDtoOf(existing);
            await transaction.CommitAsync(t);
            return replay;
        }
        Check(existing.ConcurrencyStamp, r.ConcurrencyStamp); if (existing.State != ReservationState.Active) throw Error("state", "Only active reservations can be released.");
        await Lock(existing.ProductId, existing.LocationId, t);
        var x = await db.Reservations.SingleAsync(v => v.Id == id, t);
        await db.Entry(x).ReloadAsync(t);
        if (x.State == ReservationState.Released && x.ReleaseKey == r.IdempotencyKey.Trim())
        {
            var replay = ReservationDtoOf(x);
            await transaction.CommitAsync(t);
            return replay;
        }
        Check(x.ConcurrencyStamp, r.ConcurrencyStamp); if (x.State != ReservationState.Active) throw Error("state", "Only active reservations can be released.");
        x.State = ReservationState.Released; x.ReleaseKey = r.IdempotencyKey.Trim(); x.ReleasedAtUtc = clock.UtcNow; x.ConcurrencyStamp = Stamp(); await Save(t); outbox.Add(new StockReservationReleasedEvent(Guid.NewGuid(), clock.UtcNow, x.Id, x.Quantity - x.ConsumedQuantity)); await Audit("inventory.stock.released", x.Id, ReservationDtoOf(x), t);
        var result = ReservationDtoOf(x);
        await transaction.CommitAsync(t);
        return result;
    }
    private async Task ValidateMove(StockMoveRequest r, CancellationToken t) { if (r.Quantity <= 0) throw Error("quantity", "Quantity must be greater than zero."); if (r.BusinessDate == default) throw Error("businessDate", "Business date is required."); if (r.ReversesMoveId is not null) throw Error("reversesMoveId", "Corrections must be created through the correction endpoint."); await Product(r.ProductId, t); var sourceType = await LocationTypeOf(r.SourceLocationId, t); var destinationType = await LocationTypeOf(r.DestinationLocationId, t); ValidateLocationMatrix(r.Type, sourceType, destinationType); if (r.ReferenceDocumentId is Guid doc && string.Equals(r.ReferenceDocumentType, "PurchaseOrder", StringComparison.OrdinalIgnoreCase)) { if (r.Type != StockMoveType.Receipt) throw Error("type", "Purchase orders can only be fulfilled by receipts."); if (sourceType != LocationType.Supplier) throw Error("sourceLocationId", "Purchase-order receipts must originate at a supplier location."); var po = await purchasing.FindAsync(doc, t); if (po is null || po.State is not ("Confirmed" or "PartiallyReceived")) throw Error("referenceDocumentId", "A confirmed purchase order is required."); ValidateDocumentLine(po.Lines.Select(x => (x.ProductId, x.UnitCode, x.Quantity)), r.ProductId, r.Quantity); } if (r.ReferenceDocumentId is Guid so && string.Equals(r.ReferenceDocumentType, "SalesOrder", StringComparison.OrdinalIgnoreCase)) { if (r.Type != StockMoveType.Delivery) throw Error("type", "Sales orders can only be fulfilled by deliveries."); if (destinationType != LocationType.Customer) throw Error("destinationLocationId", "Sales-order deliveries must terminate at a customer location."); var order = await sales.FindAsync(so, t); if (order is null || order.State is not ("Confirmed" or "PartiallyFulfilled")) throw Error("referenceDocumentId", "A confirmed sales order is required."); ValidateDocumentLine(order.Lines.Select(x => (x.ProductId, x.UnitCode, x.Quantity)), r.ProductId, r.Quantity); } }
    private async Task<LocationType?> ValidateStoredMoveLocations(StockMove move, CancellationToken t)
    {
        var sourceType = await LocationTypeOf(move.SourceLocationId, t);
        var destinationType = await LocationTypeOf(move.DestinationLocationId, t);
        if (move.ReversesMoveId is Guid originalId)
        {
            var original = await db.StockMoves.AsNoTracking().SingleOrDefaultAsync(x => x.Id == originalId && x.State == StockMoveState.Completed, t) ?? throw Error("reversesMoveId", "The completed movement being corrected was not found.");
            if (move.ReferenceDocumentType != "StockMove" || move.ReferenceDocumentId != original.Id || move.ProductId != original.ProductId || move.Quantity != original.Quantity || move.SourceLocationId != original.DestinationLocationId || move.DestinationLocationId != original.SourceLocationId || move.Type != ReverseType(original)) throw Error("reversesMoveId", "The correction no longer matches its original movement.");
            return sourceType;
        }
        ValidateLocationMatrix(move.Type, sourceType, destinationType);
        return sourceType;
    }
    private async Task<LocationType?> LocationTypeOf(Guid? id, CancellationToken t)
    {
        if (id is null) return null;
        var location = await db.Locations.AsNoTracking().Where(x => x.Id == id.Value && x.IsActive).Select(x => new { x.Type }).SingleOrDefaultAsync(t);
        return location?.Type ?? throw Error("locationId", "An active location in the current company is required.");
    }
    private static void ValidateLocationMatrix(StockMoveType moveType, LocationType? sourceType, LocationType? destinationType)
    {
        var valid = moveType switch
        {
            StockMoveType.Receipt => sourceType is LocationType.Supplier or LocationType.InventoryGain && destinationType == LocationType.Internal,
            StockMoveType.Delivery => sourceType == LocationType.Internal && destinationType is LocationType.Customer or LocationType.InventoryLoss,
            StockMoveType.Transfer => sourceType == LocationType.Internal && destinationType == LocationType.Internal,
            StockMoveType.Adjustment => sourceType == LocationType.InventoryGain && destinationType == LocationType.Internal || sourceType == LocationType.Internal && destinationType == LocationType.InventoryLoss,
            _ => false
        };
        if (!valid) throw Error("locations", "Move locations do not match the permitted source and destination types.");
    }
    private async Task Product(Guid id, CancellationToken t) { var p = await products.FindAsync(id, t); if (p is null || p.IsArchived || !p.TracksInventory) throw Error("productId", "An active inventory-tracked product is required."); }
    private async Task<StockMove> NewMove(StockMoveRequest r, CancellationToken t) { var p = await products.FindAsync(r.ProductId, t) ?? throw Error("productId", "Product was not found."); return new() { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), Number = await sequences.NextAsync(new("inventory.move", "MOVE-{yyyy}-", 7, r.BusinessDate), t), Type = r.Type, State = StockMoveState.Draft, ProductId = p.Id, ProductSku = p.Sku, UnitCode = p.SalesUnitCode, Quantity = r.Quantity, SourceLocationId = r.SourceLocationId, DestinationLocationId = r.DestinationLocationId, ReferenceDocumentType = CanonicalDocumentType(r.ReferenceDocumentType), ReferenceDocumentId = r.ReferenceDocumentId, BusinessDate = r.BusinessDate, Notes = Clean(r.Notes), ReversesMoveId = r.ReversesMoveId, CreatedAtUtc = clock.UtcNow, CreatedBy = User(), ConcurrencyStamp = Stamp() }; }
    private async Task InternalLocation(Guid id, CancellationToken t) { if (!await db.Locations.AnyAsync(x => x.Id == id && x.IsActive && x.Type == LocationType.Internal, t)) throw Error("locationId", "An active internal location is required."); }
    private async Task ConsumeReservations(StockMove move, Guid orderId, Guid locationId, CancellationToken t)
    {
        var balance = await BalanceAsync(move.ProductId, locationId, t);
        if (balance.OnHand < move.Quantity) throw Error("quantity", "Insufficient on-hand stock.");
        var reservations = await db.Reservations.Where(x => x.ProductId == move.ProductId && x.LocationId == locationId && x.ReferenceDocumentId == orderId && x.ReferenceDocumentType == "SalesOrder" && x.State == ReservationState.Active).OrderBy(x => x.CreatedAtUtc).ToListAsync(t);
        if (reservations.Sum(x => x.Quantity - x.ConsumedQuantity) < move.Quantity) throw Error("quantity", "Delivery exceeds stock reserved for this sales order.");
        var remaining = move.Quantity;
        foreach (var reservation in reservations)
        {
            var consumed = Math.Min(remaining, reservation.Quantity - reservation.ConsumedQuantity);
            if (consumed <= 0) continue;
            reservation.ConsumedQuantity += consumed;
            reservation.ConcurrencyStamp = Stamp();
            remaining -= consumed;
            if (reservation.ConsumedQuantity == reservation.Quantity) { reservation.State = ReservationState.Consumed; reservation.ConsumedAtUtc = clock.UtcNow; }
            outbox.Add(new StockReservationConsumedEvent(Guid.NewGuid(), clock.UtcNow, reservation.Id, move.Id, consumed));
            if (remaining == 0) break;
        }
    }
    private async Task<FulfillmentProgress> ValidateFulfillment(StockMove move, Guid documentId, CancellationToken t)
    {
        decimal ordered;
        if (move.ReferenceDocumentType == "PurchaseOrder")
        {
            var document = await purchasing.FindAsync(documentId, t) ?? throw Error("referenceDocumentId", "Purchase order was not found.");
            ordered = document.Lines.Where(x => x.ProductId == move.ProductId).Sum(x => x.Quantity);
        }
        else
        {
            var document = await sales.FindAsync(documentId, t) ?? throw Error("referenceDocumentId", "Sales order was not found.");
            ordered = document.Lines.Where(x => x.ProductId == move.ProductId).Sum(x => x.Quantity);
        }
        var fulfilled = await db.StockMoves.Where(x => x.Id != move.Id && x.ReferenceDocumentType == move.ReferenceDocumentType && x.ReferenceDocumentId == documentId && x.ProductId == move.ProductId && x.Type == move.Type && x.State == StockMoveState.Completed).SumAsync(x => (decimal?)x.Quantity, t) ?? 0;
        if (ordered <= 0 || fulfilled + move.Quantity > ordered) throw Error("quantity", "Movement exceeds the remaining source-document quantity.");
        return new(ordered, fulfilled + move.Quantity);
    }
    private static void ValidateDocumentLine(IEnumerable<(Guid ProductId, string UnitCode, decimal Quantity)> lines, Guid productId, decimal quantity)
    {
        var ordered = lines.Where(x => x.ProductId == productId).Sum(x => x.Quantity);
        if (ordered <= 0) throw Error("productId", "Product is not present on the source document.");
        if (quantity > ordered) throw Error("quantity", "Movement exceeds the ordered quantity.");
    }
    private Task<int> Lock(Guid p, Guid l, CancellationToken t) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({Tenant() + ":" + Company() + ":" + p + ":" + l}, 0))", t);
    private Task<int> LockDocument(string type, Guid documentId, Guid productId, CancellationToken t) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({Tenant() + ":" + Company() + ":" + type + ":" + documentId + ":" + productId}, 0))", t);
    private Task<int> LockReservationKey(string key, CancellationToken t) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({Tenant() + ":" + Company() + ":ReservationKey:" + key}, 0))", t);
    private Task<int> LockCorrection(Guid moveId, CancellationToken t) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({Tenant() + ":" + Company() + ":StockMoveCorrection:" + moveId}, 0))", t);
    private static StockMoveType ReverseType(StockMove original) => original.Type switch { StockMoveType.Receipt => StockMoveType.Delivery, StockMoveType.Delivery => StockMoveType.Receipt, StockMoveType.Transfer => StockMoveType.Transfer, _ => StockMoveType.Adjustment };
    private void AddEvent(StockMove x, FulfillmentProgress? progress, LocationType? sourceType) { var now = clock.UtcNow; switch (x.Type) { case StockMoveType.Receipt: outbox.Add(new GoodsReceivedEvent(Guid.NewGuid(), now, x.Id, x.ProductId, x.DestinationLocationId!.Value, x.Quantity, x.ReferenceDocumentType == "PurchaseOrder" ? x.ReferenceDocumentId : null)); if (x.ReferenceDocumentId is Guid po && progress is not null) outbox.Add(new PurchaseOrderFulfillmentProgressEvent(Guid.NewGuid(), now, po, x.ProductId, progress.Ordered, progress.Fulfilled, progress.Ordered == progress.Fulfilled)); break; case StockMoveType.Delivery: outbox.Add(new ShipmentCompletedEvent(Guid.NewGuid(), now, x.Id, x.ProductId, x.SourceLocationId!.Value, x.Quantity, x.ReferenceDocumentType == "SalesOrder" ? x.ReferenceDocumentId : null)); if (x.ReferenceDocumentId is Guid so && progress is not null) outbox.Add(new SalesOrderFulfillmentProgressEvent(Guid.NewGuid(), now, so, x.ProductId, progress.Ordered, progress.Fulfilled, progress.Ordered == progress.Fulfilled)); break; case StockMoveType.Transfer: outbox.Add(new TransferCompletedEvent(Guid.NewGuid(), now, x.Id, x.ProductId, x.SourceLocationId!.Value, x.DestinationLocationId!.Value, x.Quantity)); break; default: outbox.Add(new StockAdjustedEvent(Guid.NewGuid(), now, x.Id, x.ProductId, sourceType == LocationType.Internal ? -x.Quantity : x.Quantity)); break; } }
    private async Task<StockMove> FindMove(Guid id, CancellationToken t) => await db.StockMoves.SingleOrDefaultAsync(x => x.Id == id, t) ?? throw new KeyNotFoundException(); private async Task Save(CancellationToken t) { try { await db.SaveChangesAsync(t); } catch (DbUpdateConcurrencyException) { throw new InventoryConcurrencyException(); } }
    private async Task Audit(string action, Guid id, object value, CancellationToken t) { audit.Append(new(action, value.GetType().Name, id.ToString(), null, null, JsonSerializer.Serialize(value))); await audit.FlushAsync(t); }
    private Guid Tenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); private Guid Company() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required."); private Guid? User() => Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private static void ValidateText(string? code, string? name, string? stamp, bool update) { var e = new Dictionary<string, string[]>(); if (string.IsNullOrWhiteSpace(code) || code.Length > 50) e["code"] = ["Code is required and limited to 50 characters."]; if (string.IsNullOrWhiteSpace(name) || name.Length > 200) e["name"] = ["Name is required and limited to 200 characters."]; if (update && string.IsNullOrWhiteSpace(stamp)) e["concurrencyStamp"] = ["Concurrency stamp is required."]; if (e.Count > 0) throw new InventoryValidationException(e); }
    private static void Check(string actual, string supplied) { if (!StringComparer.Ordinal.Equals(actual, supplied)) throw new InventoryConcurrencyException(); }
    private static void ValidateKey(string key) { if (string.IsNullOrWhiteSpace(key) || key.Length > 100) throw Error("idempotencyKey", "An idempotency key up to 100 characters is required."); }
    private static string Stamp() => Guid.NewGuid().ToString("N"); private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim(); private static string? CanonicalDocumentType(string? value) => value?.Trim().ToUpperInvariant() switch { "PURCHASEORDER" => "PurchaseOrder", "SALESORDER" => "SalesOrder", _ => Clean(value) }; private static InventoryValidationException Error(string k, string m) => new(new Dictionary<string, string[]> { { k, [m] } });
    private sealed record FulfillmentProgress(decimal Ordered, decimal Fulfilled);
    private static WarehouseDto WarehouseDtoOf(Warehouse x) => new(x.Id, x.Code, x.Name, x.IsActive, x.ConcurrencyStamp); private static LocationDto LocationDtoOf(StockLocation x) => new(x.Id, x.WarehouseId, x.Code, x.Name, x.Type, x.IsActive, x.ConcurrencyStamp); private static StockMoveDto MoveDto(StockMove x) => new(x.Id, x.Number, x.Type, x.State, x.ProductId, x.ProductSku, x.UnitCode, x.Quantity, x.SourceLocationId, x.DestinationLocationId, x.ReferenceDocumentType, x.ReferenceDocumentId, x.BusinessDate, x.Notes, x.ReversesMoveId, x.CreatedAtUtc, x.CompletedAtUtc, x.ConcurrencyStamp); private static ReservationDto ReservationDtoOf(StockReservation x) => new(x.Id, x.ProductId, x.LocationId, x.Quantity, x.ConsumedQuantity, x.Quantity - x.ConsumedQuantity, x.State, x.IdempotencyKey, x.ReferenceDocumentType, x.ReferenceDocumentId, x.CreatedAtUtc, x.ReleasedAtUtc, x.ConsumedAtUtc, x.ConcurrencyStamp);
}
