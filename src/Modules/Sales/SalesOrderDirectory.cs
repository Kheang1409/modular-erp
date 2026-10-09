using Erp.Modules.Sales.Contracts;
using Microsoft.EntityFrameworkCore;
namespace Erp.Modules.Sales;

internal sealed class SalesOrderDirectory(SalesDbContext database) : ISalesOrderDirectory
{
    public Task<SalesOrderReference?> FindAsync(Guid id, CancellationToken token) => database.SalesOrders.AsNoTracking().Where(x => x.Id == id).Select(x => new SalesOrderReference(x.Id, x.Number, x.CustomerId, x.CustomerName, x.CustomerTaxNumber, x.CurrencyCode, x.BaseCurrencyCode, x.ExchangeRate, x.Subtotal, x.DiscountTotal, x.TaxTotal, x.Total, x.State.ToString(), x.Lines.OrderBy(l => l.Sequence).Select(l => new SalesOrderLineReference(l.Id, l.ProductId, l.ProductSku, l.Description, l.UnitCode, l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxPercent, l.Subtotal, l.DiscountAmount, l.TaxAmount, l.Total)).ToList())).SingleOrDefaultAsync(token);
    public async Task<IReadOnlyList<SalesOrderLookup>> SearchFulfillableAsync(string? search, int limit, CancellationToken token)
    {
        limit = Math.Clamp(limit, 1, 100);
        var query = database.SalesOrders.AsNoTracking().Where(x => x.State == SalesOrderState.Confirmed);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => EF.Functions.ILike(x.Number, $"%{term}%") || EF.Functions.ILike(x.CustomerName, $"%{term}%")); }
        return await query.OrderByDescending(x => x.OrderDate).ThenBy(x => x.Number).Take(limit).Select(x => new SalesOrderLookup(x.Id, x.Number, x.CustomerName, x.Lines.Select(line => line.ProductId).Distinct().ToList())).ToListAsync(token);
    }
}
