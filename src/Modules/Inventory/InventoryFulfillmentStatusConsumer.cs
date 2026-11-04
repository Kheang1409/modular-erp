using Erp.Abstractions.Events;
using Erp.Modules.Purchasing.Contracts;
using Erp.Modules.Sales.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory;

internal sealed class InventoryFulfillmentStatusConsumer(
    InventoryDbContext database,
    IPurchaseOrderDirectory purchaseOrders,
    ISalesOrderDirectory salesOrders,
    IPurchaseOrderFulfillmentStatusWriter purchaseOrderStatus,
    ISalesOrderFulfillmentStatusWriter salesOrderStatus) :
    IIntegrationEventConsumer<PurchaseOrderFulfillmentProgressEvent>,
    IIntegrationEventConsumer<SalesOrderFulfillmentProgressEvent>
{
    public async Task HandleAsync(
        PurchaseOrderFulfillmentProgressEvent integrationEvent,
        IntegrationEventContext context,
        CancellationToken cancellationToken)
    {
        ValidateProgress(integrationEvent.OrderedQuantity, integrationEvent.ReceivedQuantity, integrationEvent.IsComplete);
        var order = await purchaseOrders.FindAsync(integrationEvent.PurchaseOrderId, cancellationToken)
            ?? throw new KeyNotFoundException($"Purchase order '{integrationEvent.PurchaseOrderId}' was not found.");
        var required = order.Lines
            .GroupBy(line => line.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        var complete = await IsCompleteAsync(
            "PurchaseOrder",
            integrationEvent.PurchaseOrderId,
            StockMoveType.Receipt,
            integrationEvent.ProductId,
            integrationEvent.OrderedQuantity,
            integrationEvent.ReceivedQuantity,
            required,
            cancellationToken);
        await purchaseOrderStatus.RecordInventoryFulfillmentAsync(
            integrationEvent.PurchaseOrderId, complete, cancellationToken);
    }

    public async Task HandleAsync(
        SalesOrderFulfillmentProgressEvent integrationEvent,
        IntegrationEventContext context,
        CancellationToken cancellationToken)
    {
        ValidateProgress(integrationEvent.OrderedQuantity, integrationEvent.DeliveredQuantity, integrationEvent.IsComplete);
        var order = await salesOrders.FindAsync(integrationEvent.SalesOrderId, cancellationToken)
            ?? throw new KeyNotFoundException($"Sales order '{integrationEvent.SalesOrderId}' was not found.");
        var required = order.Lines
            .GroupBy(line => line.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        var complete = await IsCompleteAsync(
            "SalesOrder",
            integrationEvent.SalesOrderId,
            StockMoveType.Delivery,
            integrationEvent.ProductId,
            integrationEvent.OrderedQuantity,
            integrationEvent.DeliveredQuantity,
            required,
            cancellationToken);
        await salesOrderStatus.RecordInventoryFulfillmentAsync(
            integrationEvent.SalesOrderId, complete, cancellationToken);
    }

    private async Task<bool> IsCompleteAsync(
        string documentType,
        Guid documentId,
        StockMoveType moveType,
        Guid eventProductId,
        decimal eventOrderedQuantity,
        decimal eventFulfilledQuantity,
        IReadOnlyDictionary<Guid, decimal> required,
        CancellationToken cancellationToken)
    {
        if (!required.TryGetValue(eventProductId, out var orderedQuantity)
            || orderedQuantity != eventOrderedQuantity)
            throw new InvalidDataException("The fulfillment event does not match its source document.");
        if (required.Count == 0 || required.Values.Any(quantity => quantity <= 0))
            throw new InvalidDataException("The source document has no valid fulfillment lines.");

        var fulfilled = await database.StockMoves
            .Where(move => move.ReferenceDocumentType == documentType
                && move.ReferenceDocumentId == documentId
                && move.Type == moveType
                && move.State == StockMoveState.Completed)
            .GroupBy(move => move.ProductId)
            .Select(group => new { ProductId = group.Key, Quantity = group.Sum(move => move.Quantity) })
            .ToDictionaryAsync(item => item.ProductId, item => item.Quantity, cancellationToken);

        if (!fulfilled.TryGetValue(eventProductId, out var currentQuantity)
            || currentQuantity < eventFulfilledQuantity
            || currentQuantity > orderedQuantity)
            throw new InvalidDataException("The fulfillment event is inconsistent with the committed inventory ledger.");

        return required.All(line => fulfilled.GetValueOrDefault(line.Key) >= line.Value);
    }

    private static void ValidateProgress(decimal ordered, decimal fulfilled, bool productComplete)
    {
        if (ordered <= 0 || fulfilled <= 0 || fulfilled > ordered || productComplete != (fulfilled == ordered))
            throw new InvalidDataException("The fulfillment event contains invalid product progress.");
    }
}
