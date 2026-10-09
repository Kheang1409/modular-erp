namespace Erp.Modules.Sales.Contracts;

public sealed record QuotationReference(Guid Id, string Number, Guid CustomerId, string CurrencyCode, decimal Total, string State);

public interface IQuotationDirectory
{
    Task<QuotationReference?> FindAsync(Guid quotationId, CancellationToken cancellationToken);
}

public sealed record SalesOrderLineReference(Guid Id, Guid ProductId, string ProductSku, string Description, string UnitCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent, decimal Subtotal, decimal DiscountAmount, decimal TaxAmount, decimal Total)
{
    public SalesOrderLineReference(Guid id, Guid productId, string productSku, string unitCode, decimal quantity) : this(id, productId, productSku, productSku, unitCode, quantity, 0, 0, 0, 0, 0, 0, 0) { }
}
public sealed record SalesOrderReference(Guid Id, string Number, Guid CustomerId, string CustomerName, string? CustomerTaxNumber, string CurrencyCode, string BaseCurrencyCode, decimal ExchangeRate, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, string State, IReadOnlyList<SalesOrderLineReference> Lines)
{
    public SalesOrderReference(Guid id, string number, Guid customerId, string currencyCode, decimal total, string state, IReadOnlyList<SalesOrderLineReference> lines) : this(id, number, customerId, string.Empty, null, currencyCode, currencyCode, 1, total, 0, 0, total, state, lines) { }
}
public interface ISalesOrderDirectory
{
    Task<SalesOrderReference?> FindAsync(Guid salesOrderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SalesOrderLookup>> SearchFulfillableAsync(string? search, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SalesOrderLookup>>([]);
}
public sealed record SalesOrderLookup(Guid Id, string Number, string CustomerName, IReadOnlyList<Guid> ProductIds);
public sealed record SalesSummaryReportRow(string CurrencyCode, int OrderCount, decimal Subtotal, decimal Discount, decimal Tax, decimal Total);
public sealed record SalesByCustomerReportRow(Guid CustomerId, string CustomerName, string CurrencyCode, int OrderCount, decimal Total);
public sealed record SalesByProductReportRow(Guid ProductId, string ProductSku, string Description, string CurrencyCode, decimal Quantity, decimal Total);
public interface ISalesReportingData
{
    Task<IReadOnlyList<SalesSummaryReportRow>> SummaryAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);
    Task<IReadOnlyList<SalesByCustomerReportRow>> ByCustomerAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);
    Task<IReadOnlyList<SalesByProductReportRow>> ByProductAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);
}
