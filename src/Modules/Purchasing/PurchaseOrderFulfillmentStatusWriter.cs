using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using Erp.Modules.Purchasing.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing;

public sealed class PurchaseOrderFulfillmentStatusWriter(
    PurchasingDbContext database,
    IBusinessTransactionCoordinator transactions,
    IAuditLogWriter audit,
    IClock clock) : IPurchaseOrderFulfillmentStatusWriter
{
    public async Task RecordInventoryFulfillmentAsync(
        Guid purchaseOrderId,
        bool isComplete,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var order = await database.PurchaseOrders.SingleOrDefaultAsync(
            item => item.Id == purchaseOrderId, cancellationToken)
            ?? throw new KeyNotFoundException($"Purchase order '{purchaseOrderId}' was not found.");

        var nextState = order.State switch
        {
            PurchaseOrderState.Confirmed when isComplete => PurchaseOrderState.Received,
            PurchaseOrderState.Confirmed => PurchaseOrderState.PartiallyReceived,
            PurchaseOrderState.PartiallyReceived when isComplete => PurchaseOrderState.Received,
            PurchaseOrderState.PartiallyReceived => order.State,
            PurchaseOrderState.Received or PurchaseOrderState.Cancelled => order.State,
            _ => throw new InvalidOperationException(
                $"Inventory fulfillment cannot update a purchase order in state '{order.State}'.")
        };

        if (nextState != order.State)
        {
            var previousState = order.State;
            order.State = nextState;
            order.UpdatedAtUtc = clock.UtcNow;
            order.UpdatedBy = null;
            order.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            await database.SaveChangesAsync(cancellationToken);
            audit.Append(new(
                "purchasing.purchase_order.fulfillment_updated",
                "PurchaseOrder",
                order.Id.ToString(),
                null,
                JsonSerializer.Serialize(new { State = previousState.ToString() }),
                JsonSerializer.Serialize(new { State = nextState.ToString() })));
            await audit.FlushAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
