using System.ComponentModel.DataAnnotations;

namespace Erp.Modules.Products.Pages;

public sealed class ProductForm : IValidatableObject
{
    public ProductType Type { get; set; }
    [Required, StringLength(100)] public string? Sku { get; set; }
    [Required, StringLength(200)] public string? Name { get; set; }
    [StringLength(4000)] public string? Description { get; set; }
    [StringLength(100)] public string? Barcode { get; set; }
    [Range(typeof(decimal), "0", "99999999999999")] public decimal SalePrice { get; set; }
    [Range(typeof(decimal), "0", "99999999999999")] public decimal Cost { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string CurrencyCode { get; set; } = "USD";
    [Required] public string SalesUnitCode { get; set; } = "EA";
    [Required] public string PurchaseUnitCode { get; set; } = "EA";
    public bool TracksInventory { get; set; }
    public string? ConcurrencyStamp { get; set; }

    public ProductRequest ToRequest() => new(Type, Sku, Name, Description, Barcode, SalePrice, Cost,
        CurrencyCode, SalesUnitCode, PurchaseUnitCode, TracksInventory, ConcurrencyStamp);

    public static ProductForm From(ProductDto product) => new()
    {
        Type = product.Type,
        Sku = product.Sku,
        Name = product.Name,
        Description = product.Description,
        Barcode = product.Barcode,
        SalePrice = product.SalePrice,
        Cost = product.Cost,
        CurrencyCode = product.CurrencyCode,
        SalesUnitCode = product.SalesUnitCode,
        PurchaseUnitCode = product.PurchaseUnitCode,
        TracksInventory = product.TracksInventory,
        ConcurrencyStamp = product.ConcurrencyStamp
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type == ProductType.Service && TracksInventory)
            yield return new ValidationResult("Services cannot track inventory.", [nameof(TracksInventory)]);
        if (DecimalPlaces(SalePrice) > 4)
            yield return new ValidationResult("Sale price supports at most four decimal places.", [nameof(SalePrice)]);
        if (DecimalPlaces(Cost) > 4)
            yield return new ValidationResult("Cost supports at most four decimal places.", [nameof(Cost)]);
    }

    private static int DecimalPlaces(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0x7F;
}
