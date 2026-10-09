namespace Erp.Modules.Sales;

using Erp.Abstractions.Events;

public enum QuotationState { Draft, Sent, Accepted, Rejected, Expired, Cancelled }
public enum SalesOrderState { Draft, Confirmed, PartiallyFulfilled, Fulfilled, Cancelled }

public sealed class Quotation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Number { get; set; }
    public Guid CustomerId { get; set; }
    public required string CustomerName { get; set; }
    public required string CurrencyCode { get; set; }
    public required string BaseCurrencyCode { get; set; }
    public decimal ExchangeRate { get; set; }
    public DateOnly QuotationDate { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public QuotationState State { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public string? Terms { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
    public List<QuotationLine> Lines { get; set; } = [];
}

public sealed class QuotationLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid QuotationId { get; set; }
    public int Sequence { get; set; }
    public Guid ProductId { get; set; }
    public required string ProductSku { get; set; }
    public required string Description { get; set; }
    public required string UnitCode { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
}

public sealed class SalesOrder
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid SourceQuotationId { get; set; }
    public required string Number { get; set; }
    public Guid CustomerId { get; set; }
    public required string CustomerName { get; set; }
    public string? CustomerTaxNumber { get; set; }
    public string? BillingLine1 { get; set; }
    public string? BillingLine2 { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingStateOrProvince { get; set; }
    public string? BillingPostalCode { get; set; }
    public string? BillingCountryCode { get; set; }
    public string? ShippingLine1 { get; set; }
    public string? ShippingLine2 { get; set; }
    public string? ShippingCity { get; set; }
    public string? ShippingStateOrProvince { get; set; }
    public string? ShippingPostalCode { get; set; }
    public string? ShippingCountryCode { get; set; }
    public required string CurrencyCode { get; set; }
    public required string BaseCurrencyCode { get; set; }
    public decimal ExchangeRate { get; set; }
    public DateOnly OrderDate { get; set; }
    public SalesOrderState State { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public string? Terms { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
    public List<SalesOrderLine> Lines { get; set; } = [];
}
public sealed class SalesOrderLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid SalesOrderId { get; set; }
    public int Sequence { get; set; }
    public Guid ProductId { get; set; }
    public required string ProductSku { get; set; }
    public required string Description { get; set; }
    public required string UnitCode { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
}

public sealed record QuotationLineRequest(Guid ProductId, decimal Quantity, decimal? UnitPrice = null, decimal DiscountPercent = 0, decimal TaxPercent = 0, string? Description = null);
public sealed record QuotationRequest(Guid CustomerId, string? CurrencyCode, DateOnly QuotationDate, DateOnly? ValidUntil, string? Notes, string? Terms, IReadOnlyList<QuotationLineRequest> Lines, string? ConcurrencyStamp = null);
public sealed record QuotationLineDto(Guid Id, int Sequence, Guid ProductId, string ProductSku, string Description, string UnitCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent, decimal Subtotal, decimal DiscountAmount, decimal TaxAmount, decimal Total);
public sealed record QuotationDto(Guid Id, string Number, Guid CustomerId, string CustomerName, string CurrencyCode, string BaseCurrencyCode, decimal ExchangeRate, DateOnly QuotationDate, DateOnly? ValidUntil, QuotationState State, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, string? Notes, string? Terms, bool IsArchived, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string ConcurrencyStamp, IReadOnlyList<QuotationLineDto> Lines);
public sealed record QuotationQuery(string? Search = null, QuotationState? State = null, bool IncludeArchived = false, string Sort = "updated", bool Descending = true, int Page = 1, int PageSize = 25);
public sealed record QuotationPage(int Page, int PageSize, int Total, IReadOnlyList<QuotationDto> Items);
public sealed record QuotationTransitionRequest(string ConcurrencyStamp);
public sealed record ConvertQuotationRequest(string ConcurrencyStamp, DateOnly OrderDate);
public sealed record SalesOrderLineDto(Guid Id, int Sequence, Guid ProductId, string ProductSku, string Description, string UnitCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent, decimal Subtotal, decimal DiscountAmount, decimal TaxAmount, decimal Total);
public sealed record AddressSnapshotDto(string? Line1, string? Line2, string? City, string? StateOrProvince, string? PostalCode, string? CountryCode);
public sealed record SalesOrderDto(Guid Id, Guid SourceQuotationId, string Number, Guid CustomerId, string CustomerName, string? CustomerTaxNumber, AddressSnapshotDto BillingAddress, AddressSnapshotDto ShippingAddress, string CurrencyCode, string BaseCurrencyCode, decimal ExchangeRate, DateOnly OrderDate, SalesOrderState State, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, string? Notes, string? Terms, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string ConcurrencyStamp, IReadOnlyList<SalesOrderLineDto> Lines);
public sealed record SalesOrderQuery(string? Search = null, SalesOrderState? State = null, string Sort = "updated", bool Descending = true, int Page = 1, int PageSize = 25);
public sealed record SalesOrderPage(int Page, int PageSize, int Total, IReadOnlyList<SalesOrderDto> Items);
public sealed class SalesValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Sales validation failed.") { public IReadOnlyDictionary<string, string[]> Errors { get; } = errors; }
public sealed class SalesConcurrencyException : Exception { public SalesConcurrencyException() : base("The quotation changed since it was loaded.") { } }
public sealed record QuotationCreatedEvent(Guid EventId, DateTimeOffset OccurredAtUtc, Guid QuotationId, string Number, Guid CustomerId, string CurrencyCode, decimal Total) : IIntegrationEvent;
public sealed record QuotationSentEvent(Guid EventId, DateTimeOffset OccurredAtUtc, Guid QuotationId, string Number) : IIntegrationEvent;
public sealed record QuotationAcceptedEvent(Guid EventId, DateTimeOffset OccurredAtUtc, Guid QuotationId, string Number, Guid CustomerId, string CurrencyCode, decimal Total) : IIntegrationEvent;
public sealed record SalesOrderConfirmedEvent(Guid EventId, DateTimeOffset OccurredAtUtc, Guid SalesOrderId, string Number, Guid CustomerId, string CurrencyCode, decimal Total) : IIntegrationEvent;
public sealed record SalesOrderCancelledEvent(Guid EventId, DateTimeOffset OccurredAtUtc, Guid SalesOrderId, string Number) : IIntegrationEvent;
