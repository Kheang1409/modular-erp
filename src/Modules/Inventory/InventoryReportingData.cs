using Erp.Modules.Inventory.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory;

public sealed class InventoryReportingData(InventoryDbContext db) : IInventoryReportingData
{
    public async Task<IReadOnlyList<InventoryOnHandReportRow>> OnHandAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var incoming = db.StockMoves.AsNoTracking().Where(x => x.State == StockMoveState.Completed && x.BusinessDate <= asOf && x.DestinationLocationId != null).Select(x => new { x.ProductId, x.ProductSku, x.UnitCode, LocationId = x.DestinationLocationId!.Value, Quantity = x.Quantity });
        var outgoing = db.StockMoves.AsNoTracking().Where(x => x.State == StockMoveState.Completed && x.BusinessDate <= asOf && x.SourceLocationId != null).Select(x => new { x.ProductId, x.ProductSku, x.UnitCode, LocationId = x.SourceLocationId!.Value, Quantity = -x.Quantity });
        var stock = incoming.Concat(outgoing).GroupBy(x => new { x.ProductId, x.ProductSku, x.UnitCode, x.LocationId }).Select(x => new { x.Key.ProductId, x.Key.ProductSku, x.Key.UnitCode, x.Key.LocationId, OnHand = x.Sum(v => v.Quantity) });
        var reserved = db.Reservations.AsNoTracking().Where(x => x.State == ReservationState.Active).GroupBy(x => new { x.ProductId, x.LocationId }).Select(x => new { x.Key.ProductId, x.Key.LocationId, Reserved = x.Sum(v => v.Quantity - v.ConsumedQuantity) });
        var query = stock
            .Join(db.Locations.AsNoTracking().Where(x => x.Type == LocationType.Internal), x => x.LocationId, x => x.Id, (balance, location) => new { balance, location })
            .GroupJoin(reserved, x => new { x.balance.ProductId, x.balance.LocationId }, x => new { x.ProductId, x.LocationId }, (x, reservations) => new { x, reservations })
            .SelectMany(x => x.reservations.DefaultIfEmpty(), (x, reservation) => new
            {
                x.x.balance.ProductId,
                x.x.balance.ProductSku,
                x.x.balance.UnitCode,
                x.x.balance.LocationId,
                LocationCode = x.x.location.Code,
                LocationName = x.x.location.Name,
                x.x.balance.OnHand,
                Reserved = reservation == null ? (decimal?)null : reservation.Reserved
            })
            .Where(x => x.OnHand != 0 || (x.Reserved ?? 0) != 0)
            .OrderBy(x => x.ProductSku)
            .ThenBy(x => x.LocationCode);
        var data = await query.ToListAsync(cancellationToken);
        return data.Select(x => new InventoryOnHandReportRow(x.ProductId, x.ProductSku, x.UnitCode, x.LocationId, x.LocationCode, x.LocationName, x.OnHand, x.Reserved ?? 0, x.OnHand - (x.Reserved ?? 0))).ToList();
    }

    public async Task<InventoryMovementReportPage> MovementsAsync(DateOnly startDate, DateOnly endDate, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (startDate == default || endDate == default || startDate > endDate) throw new ArgumentException("A valid business-date range is required.");
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.StockMoves.AsNoTracking().Where(x => x.State == StockMoveState.Completed && x.BusinessDate >= startDate && x.BusinessDate <= endDate);
        var total = await query.CountAsync(cancellationToken);
        var offset = ((long)page - 1) * pageSize;
        if (offset >= total) return new(page, pageSize, total, []);
        var data = await query.OrderBy(x => x.BusinessDate).ThenBy(x => x.Number).ThenBy(x => x.Id)
            .Skip((int)offset).Take(pageSize).ToListAsync(cancellationToken);
        return new(page, pageSize, total, data.Select(x => new InventoryMovementReportRow(x.Id, x.Number, x.Type.ToString(), x.ProductId, x.ProductSku, x.UnitCode, x.Quantity, x.SourceLocationId, x.DestinationLocationId, x.BusinessDate, x.ReferenceDocumentType, x.ReferenceDocumentId)).ToList());
    }
}
