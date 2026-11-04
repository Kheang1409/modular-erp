using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using Erp.Modules.Sales.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales;

public sealed class SalesOrderFulfillmentStatusWriter(
    SalesDbContext database,
    IBusinessTransactionCoordinator transactions,
    IAuditLogWriter audit,
    IClock clock) : ISalesOrderFulfillmentStatusWriter
{
    public async Task RecordInventoryFulfillmentAsync(
        Guid salesOrderId,
        bool isComplete,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var order = await database.SalesOrders.SingleOrDefaultAsync(
            item => item.Id == salesOrderId, cancellationToken)
            ?? throw new KeyNotFoundException($"Sales order '{salesOrderId}' was not found.");

        var nextState = order.State switch
        {
            SalesOrderState.Confirmed when isComplete => SalesOrderState.Fulfilled,
            SalesOrderState.Confirmed => SalesOrderState.PartiallyFulfilled,
            SalesOrderState.PartiallyFulfilled when isComplete => SalesOrderState.Fulfilled,
            SalesOrderState.PartiallyFulfilled => order.State,
            SalesOrderState.Fulfilled or SalesOrderState.Cancelled => order.State,
            _ => throw new InvalidOperationException(
                $"Inventory fulfillment cannot update a sales order in state '{order.State}'.")
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
                "sales.order.fulfillment_updated",
                "SalesOrder",
                order.Id.ToString(),
                null,
                JsonSerializer.Serialize(new { State = previousState.ToString() }),
                JsonSerializer.Serialize(new { State = nextState.ToString() })));
            await audit.FlushAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
