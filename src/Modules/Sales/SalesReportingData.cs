using Erp.Modules.Sales.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales;

public sealed class SalesReportingData(SalesDbContext db) : ISalesReportingData
{
    public async Task<IReadOnlyList<SalesSummaryReportRow>> SummaryAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        Validate(startDate, endDate);
        var rows = await Base(startDate, endDate)
            .GroupBy(x => x.CurrencyCode)
            .Select(x => new { CurrencyCode = x.Key, OrderCount = x.Count(), Subtotal = x.Sum(v => v.Subtotal), Discount = x.Sum(v => v.DiscountTotal), Tax = x.Sum(v => v.TaxTotal), Total = x.Sum(v => v.Total) })
            .OrderBy(x => x.CurrencyCode)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new SalesSummaryReportRow(x.CurrencyCode, x.OrderCount, x.Subtotal, x.Discount, x.Tax, x.Total)).ToList();
    }
    public async Task<IReadOnlyList<SalesByCustomerReportRow>> ByCustomerAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        Validate(startDate, endDate);
        var rows = await Base(startDate, endDate)
            .GroupBy(x => new { x.CustomerId, x.CustomerName, x.CurrencyCode })
            .Select(x => new { x.Key.CustomerId, x.Key.CustomerName, x.Key.CurrencyCode, OrderCount = x.Count(), Total = x.Sum(v => v.Total) })
            .OrderByDescending(x => x.Total)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new SalesByCustomerReportRow(x.CustomerId, x.CustomerName, x.CurrencyCode, x.OrderCount, x.Total)).ToList();
    }
    public async Task<IReadOnlyList<SalesByProductReportRow>> ByProductAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        Validate(startDate, endDate);
        var rows = await Base(startDate, endDate)
            .SelectMany(x => x.Lines, (order, line) => new { order.CurrencyCode, Line = line })
            .GroupBy(x => new { x.Line.ProductId, x.Line.ProductSku, x.Line.Description, x.CurrencyCode })
            .Select(x => new { x.Key.ProductId, x.Key.ProductSku, x.Key.Description, x.Key.CurrencyCode, Quantity = x.Sum(v => v.Line.Quantity), Total = x.Sum(v => v.Line.Total) })
            .OrderByDescending(x => x.Total)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new SalesByProductReportRow(x.ProductId, x.ProductSku, x.Description, x.CurrencyCode, x.Quantity, x.Total)).ToList();
    }
    private IQueryable<SalesOrder> Base(DateOnly from, DateOnly to) => db.SalesOrders.AsNoTracking().Where(x => x.OrderDate >= from && x.OrderDate <= to && x.State != SalesOrderState.Draft && x.State != SalesOrderState.Cancelled);
    private static void Validate(DateOnly from, DateOnly to) { if (from == default || to == default || from > to) throw new ArgumentException("A valid business-date range is required."); }
}
