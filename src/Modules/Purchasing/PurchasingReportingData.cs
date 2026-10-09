using Erp.Modules.Purchasing.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing;

public sealed class PurchasingReportingData(PurchasingDbContext db) : IPurchasingReportingData
{
    public async Task<IReadOnlyList<PurchaseSummaryReportRow>> SummaryAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        if (startDate == default || endDate == default || startDate > endDate) throw new ArgumentException("A valid business-date range is required.");
        var rows = await db.PurchaseOrders.AsNoTracking()
            .Where(x => x.OrderDate >= startDate && x.OrderDate <= endDate && x.State != PurchaseOrderState.Draft && x.State != PurchaseOrderState.Cancelled)
            .GroupBy(x => x.CurrencyCode)
            .Select(x => new { CurrencyCode = x.Key, OrderCount = x.Count(), Subtotal = x.Sum(v => v.Subtotal), Discount = x.Sum(v => v.DiscountTotal), Tax = x.Sum(v => v.TaxTotal), Total = x.Sum(v => v.Total) })
            .OrderBy(x => x.CurrencyCode)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new PurchaseSummaryReportRow(x.CurrencyCode, x.OrderCount, x.Subtotal, x.Discount, x.Tax, x.Total)).ToList();
    }
}
