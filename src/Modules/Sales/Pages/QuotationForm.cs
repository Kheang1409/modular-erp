using System.ComponentModel.DataAnnotations;
namespace Erp.Modules.Sales.Pages;

public sealed class QuotationForm
{
    [Required] public string? CustomerId { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string CurrencyCode { get; set; } = "USD";
    public DateOnly QuotationDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? ValidUntil { get; set; }
    [StringLength(4000)] public string? Notes { get; set; }
    [StringLength(4000)] public string? Terms { get; set; }
    public string? ConcurrencyStamp { get; set; }
    public List<QuotationLineForm> Lines { get; set; } = [new()];
    public QuotationRequest ToRequest() => new(Guid.TryParse(CustomerId, out var customer) ? customer : Guid.Empty, CurrencyCode, QuotationDate, ValidUntil, Notes, Terms, Lines.Select(x => x.ToRequest()).ToList(), ConcurrencyStamp);
    public static QuotationForm From(QuotationDto item) => new() { CustomerId = item.CustomerId.ToString(), CurrencyCode = item.CurrencyCode, QuotationDate = item.QuotationDate, ValidUntil = item.ValidUntil, Notes = item.Notes, Terms = item.Terms, ConcurrencyStamp = item.ConcurrencyStamp, Lines = item.Lines.Select(x => new QuotationLineForm { ProductId = x.ProductId.ToString(), Quantity = x.Quantity, UnitPrice = x.UnitPrice, DiscountPercent = x.DiscountPercent, TaxPercent = x.TaxPercent, Description = x.Description }).ToList() };
}
public sealed class QuotationLineForm
{
    [Required] public string? ProductId { get; set; }
    [Range(typeof(decimal), "0.0001", "999999999")] public decimal Quantity { get; set; } = 1;
    [Range(typeof(decimal), "0", "999999999")] public decimal? UnitPrice { get; set; }
    [Range(typeof(decimal), "0", "100")] public decimal DiscountPercent { get; set; }
    [Range(typeof(decimal), "0", "100")] public decimal TaxPercent { get; set; }
    [StringLength(500)] public string? Description { get; set; }
    public QuotationLineRequest ToRequest() => new(Guid.TryParse(ProductId, out var product) ? product : Guid.Empty, Quantity, UnitPrice, DiscountPercent, TaxPercent, Description);
}
