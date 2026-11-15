using Erp.Modules.Inventory.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory;

internal sealed class StockAvailability(InventoryDbContext database, IInventoryService service) : IStockAvailability
{
    public async Task<StockBalanceReference> GetAsync(Guid productId, Guid locationId, CancellationToken cancellationToken)
    {
        var balance = await service.BalanceAsync(productId, locationId, cancellationToken);
        return new(balance.ProductId, balance.LocationId, balance.OnHand, balance.Reserved, balance.Available);
    }

    public async Task<ProductionStockAvailability> GetForProductionAsync(Guid workOrderId, Guid productId,
        Guid locationId, CancellationToken cancellationToken)
    {
        var balance = await service.BalanceAsync(productId, locationId, cancellationToken);
        var reservedForWorkOrder = await database.Reservations.AsNoTracking()
            .Where(reservation => reservation.ReferenceDocumentType == "ManufacturingWorkOrder" &&
                reservation.ReferenceDocumentId == workOrderId && reservation.ProductId == productId &&
                reservation.LocationId == locationId && reservation.State == ReservationState.Active)
            .SumAsync(reservation => (decimal?)(reservation.Quantity - reservation.ConsumedQuantity), cancellationToken) ?? 0m;
        return new(balance.ProductId, balance.LocationId, balance.OnHand, balance.Reserved,
            balance.Available, reservedForWorkOrder);
    }
}
