namespace Erp.Modules.Products.Contracts;

public enum ProductReferenceType { Product, Service }

public sealed record ProductReference(
    Guid Id,
    ProductReferenceType Type,
    string Sku,
    string Name,
    decimal SalePrice,
    string CurrencyCode,
    string SalesUnitCode,
    bool TracksInventory,
    bool IsArchived);

public interface IProductDirectory
{
    Task<ProductReference?> FindAsync(Guid productId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, ProductReference>> FindAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken);
}
public sealed record PurchasingProductReference(Guid Id, string Sku, string Name, decimal Cost, string CurrencyCode, string PurchaseUnitCode, bool IsArchived);
public interface IPurchasingProductDirectory
{
    Task<IReadOnlyDictionary<Guid, PurchasingProductReference>> FindAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}
