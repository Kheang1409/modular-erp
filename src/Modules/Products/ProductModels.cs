namespace Erp.Modules.Products;

public enum ProductType { Product, Service }

public sealed class Product
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public ProductType Type { get; set; }
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Barcode { get; set; }
    public decimal SalePrice { get; set; }
    public decimal Cost { get; set; }
    public required string CurrencyCode { get; set; }
    public required string SalesUnitCode { get; set; }
    public required string PurchaseUnitCode { get; set; }
    public bool TracksInventory { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed class UnitOfMeasure
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public int DecimalPrecision { get; set; }
    public bool IsActive { get; set; }
}

public sealed record ProductRequest(
    ProductType Type, string? Sku, string? Name, string? Description, string? Barcode,
    decimal SalePrice, decimal Cost, string? CurrencyCode, string? SalesUnitCode,
    string? PurchaseUnitCode, bool TracksInventory, string? ConcurrencyStamp = null);

public sealed record ProductDto(
    Guid Id, ProductType Type, string Sku, string Name, string? Description, string? Barcode,
    decimal SalePrice, decimal Cost, string CurrencyCode, string SalesUnitCode,
    string PurchaseUnitCode, bool TracksInventory, bool IsArchived,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, Guid? CreatedBy, Guid? UpdatedBy,
    string ConcurrencyStamp);

public sealed record ProductQuery(
    string? Search = null, ProductType? Type = null, bool IncludeArchived = false,
    bool? TracksInventory = null, string Sort = "name", bool Descending = false,
    int Page = 1, int PageSize = 25);

public sealed record ProductPage(int Page, int PageSize, int Total, IReadOnlyList<ProductDto> Items);
public sealed record ProductOptions(
    string BaseCurrencyCode,
    IReadOnlyList<ProductCurrencyOption> Currencies,
    IReadOnlyList<ProductUnitOption> Units);
public sealed record ProductCurrencyOption(string Code, string Name, string Symbol);
public sealed record ProductUnitOption(string Code, string Name, string Category, int DecimalPrecision);
public sealed record ProductCsvImportResult(int Imported, IReadOnlyList<string> Errors);

public sealed class ProductValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception(CreateMessage(errors))
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    private static string CreateMessage(IReadOnlyDictionary<string, string[]> errors)
    {
        var messages = errors.Values.SelectMany(value => value)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        return messages.Length == 0 ? "Product validation failed." : string.Join(" ", messages);
    }
}

public sealed class ProductConcurrencyException : Exception
{
    public ProductConcurrencyException() : base("The product changed since it was loaded.") { }
}

public sealed class ProductConflictException(string message) : Exception(message);
