using Erp.Modules.Purchasing.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing;

internal sealed class PurchaseOrderDirectory(PurchasingDbContext db) : IPurchaseOrderDirectory
{
    public Task<PurchaseOrderReference?> FindAsync(Guid id, CancellationToken t) => db.PurchaseOrders.AsNoTracking().Where(x => x.Id == id).Select(x => new PurchaseOrderReference(x.Id, x.Number, x.SupplierId, x.SupplierName, x.SupplierTaxNumber, x.CurrencyCode, x.BaseCurrencyCode, x.ExchangeRate, x.Subtotal, x.DiscountTotal, x.TaxTotal, x.Total, x.State.ToString(), x.Lines.OrderBy(l => l.Sequence).Select(l => new PurchaseOrderLineReference(l.Id, l.ProductId, l.ProductSku, l.Description, l.UnitCode, l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxPercent, l.Subtotal, l.DiscountAmount, l.TaxAmount, l.Total)).ToList())).SingleOrDefaultAsync(t);
    public async Task<IReadOnlyList<PurchaseOrderLookup>> SearchReceivableAsync(string? search, int limit, CancellationToken token)
    {
        limit = Math.Clamp(limit, 1, 100);
        var query = db.PurchaseOrders.AsNoTracking().Where(x => x.State == PurchaseOrderState.Confirmed && !x.IsArchived);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => EF.Functions.ILike(x.Number, $"%{term}%") || EF.Functions.ILike(x.SupplierName, $"%{term}%")); }
        return await query.OrderByDescending(x => x.OrderDate).ThenBy(x => x.Number).Take(limit).Select(x => new PurchaseOrderLookup(x.Id, x.Number, x.SupplierName, x.Lines.Select(line => line.ProductId).Distinct().ToList())).ToListAsync(token);
    }
}
