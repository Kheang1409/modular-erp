namespace Erp.Modules.Inventory.Contracts;

public sealed record ProductionMaterialIssue(Guid ProductId, decimal Quantity);

/// <summary>Posts component issues and finished output for a manufacturing work order.</summary>
public interface IProductionStockWriter
{
    Task ReserveProductionMaterialsAsync(Guid workOrderId, Guid locationId,
        IReadOnlyList<ProductionMaterialIssue> materialRequirements, CancellationToken cancellationToken);
    Task ReleaseProductionMaterialsAsync(Guid workOrderId, CancellationToken cancellationToken);
    Task CompleteProductionAsync(Guid workOrderId, Guid locationId, DateOnly businessDate,
        IReadOnlyList<ProductionMaterialIssue> materialIssues, Guid outputProductId, decimal outputQuantity,
        CancellationToken cancellationToken);
}

public sealed class ProductionStockConflictException(string message) : Exception(message);

public sealed record StockBalanceReference(Guid ProductId, Guid LocationId, decimal OnHand, decimal Reserved, decimal Available);
public sealed record ProductionStockAvailability(Guid ProductId, Guid LocationId, decimal OnHand, decimal Reserved,
    decimal Available, decimal ReservedForWorkOrder);
public interface IStockAvailability
{
    Task<StockBalanceReference> GetAsync(Guid productId, Guid locationId, CancellationToken cancellationToken);
    Task<ProductionStockAvailability> GetForProductionAsync(Guid workOrderId, Guid productId, Guid locationId,
        CancellationToken cancellationToken);
}
public sealed record InventoryOnHandReportRow(Guid ProductId, string ProductSku, string UnitCode, Guid LocationId, string LocationCode, string LocationName, decimal OnHand, decimal Reserved, decimal Available);
public sealed record InventoryMovementReportRow(Guid MoveId, string Number, string Type, Guid ProductId, string ProductSku, string UnitCode, decimal Quantity, Guid? SourceLocationId, Guid? DestinationLocationId, DateOnly BusinessDate, string? ReferenceDocumentType, Guid? ReferenceDocumentId);
public sealed record InventoryMovementReportPage(int Page, int PageSize, int Total, IReadOnlyList<InventoryMovementReportRow> Items);
public interface IInventoryReportingData
{
    Task<IReadOnlyList<InventoryOnHandReportRow>> OnHandAsync(DateOnly asOf, CancellationToken cancellationToken);
    Task<InventoryMovementReportPage> MovementsAsync(DateOnly startDate, DateOnly endDate, int page, int pageSize, CancellationToken cancellationToken);
}
