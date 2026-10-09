namespace Erp.Modules.Purchasing.Contracts;

public sealed record PurchaseOrderLineReference(Guid Id, Guid ProductId, string ProductSku, string Description, string UnitCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent, decimal Subtotal, decimal DiscountAmount, decimal TaxAmount, decimal Total)
{
    public PurchaseOrderLineReference(Guid id, Guid productId, string productSku, string unitCode, decimal quantity) : this(id, productId, productSku, productSku, unitCode, quantity, 0, 0, 0, 0, 0, 0, 0) { }
}
public sealed record PurchaseOrderReference(Guid Id, string Number, Guid SupplierId, string SupplierName, string? SupplierTaxNumber, string CurrencyCode, string BaseCurrencyCode, decimal ExchangeRate, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, string State, IReadOnlyList<PurchaseOrderLineReference> Lines)
{
    public PurchaseOrderReference(Guid id, string number, Guid supplierId, string currencyCode, decimal total, string state, IReadOnlyList<PurchaseOrderLineReference> lines) : this(id, number, supplierId, string.Empty, null, currencyCode, currencyCode, 1, total, 0, 0, total, state, lines) { }
}
public interface IPurchaseOrderDirectory
{
    Task<PurchaseOrderReference?> FindAsync(Guid purchaseOrderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PurchaseOrderLookup>> SearchReceivableAsync(string? search, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PurchaseOrderLookup>>([]);
}
public sealed record PurchaseOrderLookup(Guid Id, string Number, string SupplierName, IReadOnlyList<Guid> ProductIds);
public sealed record PurchaseSummaryReportRow(string CurrencyCode, int OrderCount, decimal Subtotal, decimal Discount, decimal Tax, decimal Total);
public interface IPurchasingReportingData { Task<IReadOnlyList<PurchaseSummaryReportRow>> SummaryAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken); }
