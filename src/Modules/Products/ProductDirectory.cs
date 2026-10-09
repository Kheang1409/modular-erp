using Erp.Modules.Products.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Products;

internal sealed class ProductDirectory(ProductsDbContext database) : IProductDirectory
{
    public async Task<ProductReference?> FindAsync(Guid productId, CancellationToken cancellationToken) =>
        await database.Products.AsNoTracking().Where(x => x.Id == productId)
            .Select(x => new ProductReference(x.Id,
                x.Type == ProductType.Product ? ProductReferenceType.Product : ProductReferenceType.Service,
                x.Sku, x.Name, x.SalePrice, x.CurrencyCode, x.SalesUnitCode, x.TracksInventory, x.IsArchived))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ProductReference>> FindAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count > 100) throw new ArgumentOutOfRangeException(nameof(productIds), "At most 100 products may be resolved at once.");
        var ids = productIds.Distinct().ToArray();
        return await database.Products.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new ProductReference(x.Id,
                x.Type == ProductType.Product ? ProductReferenceType.Product : ProductReferenceType.Service,
                x.Sku, x.Name, x.SalePrice, x.CurrencyCode, x.SalesUnitCode, x.TracksInventory, x.IsArchived))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
    }

}
internal sealed class PurchasingProductDirectory(ProductsDbContext database) : IPurchasingProductDirectory
{
    public async Task<IReadOnlyDictionary<Guid, PurchasingProductReference>> FindAsync(IReadOnlyCollection<Guid> productIds, CancellationToken token) { if (productIds.Count > 100) throw new ArgumentOutOfRangeException(nameof(productIds)); var ids = productIds.Distinct().ToArray(); return await database.Products.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new PurchasingProductReference(x.Id, x.Sku, x.Name, x.Cost, x.CurrencyCode, x.PurchaseUnitCode, x.IsArchived)).ToDictionaryAsync(x => x.Id, token); }
}
