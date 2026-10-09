using Erp.Abstractions.Events;
namespace Erp.Modules.Purchasing;

public enum PurchaseOrderState { Draft, Sent, Confirmed, PartiallyReceived, Received, Cancelled }
public sealed class PurchaseOrder
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Number { get; set; }
    public Guid SupplierId { get; set; }
    public required string SupplierName { get; set; }
    public string? SupplierTaxNumber { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? StateOrProvince { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; }
    public required string CurrencyCode { get; set; }
    public required string BaseCurrencyCode { get; set; }
    public decimal ExchangeRate { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? ExpectedDeliveryDate { get; set; }
    public PurchaseOrderState State { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
}
public sealed class PurchaseOrderLine { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid CompanyId { get; set; } public Guid PurchaseOrderId { get; set; } public int Sequence { get; set; } public Guid ProductId { get; set; } public required string ProductSku { get; set; } public required string Description { get; set; } public required string UnitCode { get; set; } public decimal Quantity { get; set; } public decimal UnitPrice { get; set; } public decimal DiscountPercent { get; set; } public decimal TaxPercent { get; set; } public decimal Subtotal { get; set; } public decimal DiscountAmount { get; set; } public decimal TaxAmount { get; set; } public decimal Total { get; set; } }
public sealed record PurchaseOrderLineRequest(Guid ProductId, decimal Quantity, decimal? UnitPrice = null, decimal DiscountPercent = 0, decimal TaxPercent = 0, string? Description = null);
public sealed record PurchaseOrderRequest(Guid SupplierId, string? CurrencyCode, DateOnly OrderDate, DateOnly? ExpectedDeliveryDate, string? Notes, IReadOnlyList<PurchaseOrderLineRequest> Lines, string? ConcurrencyStamp = null);
public sealed record PurchaseOrderLineDto(Guid Id, int Sequence, Guid ProductId, string ProductSku, string Description, string UnitCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent, decimal Subtotal, decimal DiscountAmount, decimal TaxAmount, decimal Total);
public sealed record SupplierAddressDto(string? Line1, string? Line2, string? City, string? StateOrProvince, string? PostalCode, string? CountryCode);
public sealed record PurchaseOrderDto(Guid Id, string Number, Guid SupplierId, string SupplierName, string? SupplierTaxNumber, SupplierAddressDto SupplierAddress, string CurrencyCode, string BaseCurrencyCode, decimal ExchangeRate, DateOnly OrderDate, DateOnly? ExpectedDeliveryDate, PurchaseOrderState State, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, string? Notes, bool IsArchived, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string ConcurrencyStamp, IReadOnlyList<PurchaseOrderLineDto> Lines);
public sealed record PurchaseOrderQuery(string? Search = null, PurchaseOrderState? State = null, bool IncludeArchived = false, string Sort = "updated", bool Descending = true, int Page = 1, int PageSize = 25);
public sealed record PurchaseOrderPage(int Page, int PageSize, int Total, IReadOnlyList<PurchaseOrderDto> Items);
public sealed record PurchaseOrderTransitionRequest(string ConcurrencyStamp);
public sealed class PurchasingValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Purchasing validation failed.") { public IReadOnlyDictionary<string, string[]> Errors { get; } = errors; }
public sealed class PurchasingConcurrencyException : Exception { public PurchasingConcurrencyException() : base("The purchase order changed since it was loaded.") { } }
public sealed record PurchaseOrderConfirmedEvent(Guid EventId, DateTimeOffset OccurredAtUtc, Guid PurchaseOrderId, string Number, Guid SupplierId, string CurrencyCode, decimal Total) : IIntegrationEvent;
public sealed record PurchaseOrderCancelledEvent(Guid EventId, DateTimeOffset OccurredAtUtc, Guid PurchaseOrderId, string Number) : IIntegrationEvent;
public sealed record GoodsReceiptRequestedEvent(Guid EventId, DateTimeOffset OccurredAtUtc, Guid PurchaseOrderId, string Number) : IIntegrationEvent;
