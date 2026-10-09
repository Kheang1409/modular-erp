namespace Erp.Modules.Inventory.Contracts;

public sealed record StockBalanceReference(Guid ProductId, Guid LocationId, decimal OnHand, decimal Reserved, decimal Available);
public interface IStockAvailability { Task<StockBalanceReference> GetAsync(Guid productId, Guid locationId, CancellationToken cancellationToken); }
public sealed record InventoryOnHandReportRow(Guid ProductId, string ProductSku, string UnitCode, Guid LocationId, string LocationCode, string LocationName, decimal OnHand, decimal Reserved, decimal Available);
public sealed record InventoryMovementReportRow(Guid MoveId, string Number, string Type, Guid ProductId, string ProductSku, string UnitCode, decimal Quantity, Guid? SourceLocationId, Guid? DestinationLocationId, DateOnly BusinessDate, string? ReferenceDocumentType, Guid? ReferenceDocumentId);
public sealed record InventoryMovementReportPage(int Page, int PageSize, int Total, IReadOnlyList<InventoryMovementReportRow> Items);
public interface IInventoryReportingData
{
    Task<IReadOnlyList<InventoryOnHandReportRow>> OnHandAsync(DateOnly asOf, CancellationToken cancellationToken);
    Task<InventoryMovementReportPage> MovementsAsync(DateOnly startDate, DateOnly endDate, int page, int pageSize, CancellationToken cancellationToken);
}
