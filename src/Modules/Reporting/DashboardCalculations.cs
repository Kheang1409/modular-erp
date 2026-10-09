using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Sales.Contracts;

namespace Erp.Modules.Reporting;

public sealed record DashboardChartPoint(string Label, decimal Value, string Color = "blue");

/// <summary>Display calculations retain each source currency and accounting sign convention.</summary>
public static class DashboardCalculations
{
    public static IReadOnlyList<DashboardChartPoint> TopCustomers(IEnumerable<SalesByCustomerReportRow> rows, string currency) =>
        rows.Where(row => SameCurrency(row.CurrencyCode, currency))
            .GroupBy(row => new { row.CustomerId, row.CustomerName })
            .Select(group => new DashboardChartPoint(group.Key.CustomerName, group.Sum(row => row.Total)))
            .OrderByDescending(point => point.Value).ThenBy(point => point.Label, StringComparer.Ordinal)
            .Take(6).ToList();

    public static IReadOnlyList<DashboardChartPoint> TopProducts(IEnumerable<SalesByProductReportRow> rows, string currency) =>
        rows.Where(row => SameCurrency(row.CurrencyCode, currency))
            .GroupBy(row => new { row.ProductId, row.ProductSku })
            .Select(group => new DashboardChartPoint(group.Key.ProductSku, group.Sum(row => row.Total), "teal"))
            .OrderByDescending(point => point.Value).ThenBy(point => point.Label, StringComparer.Ordinal)
            .Take(6).ToList();

    public static IReadOnlyList<DashboardChartPoint> AgingBuckets(IEnumerable<AgingReportRow> rows, string currency, string color = "blue")
    {
        var selected = rows.Where(row => SameCurrency(row.CurrencyCode, currency)).ToList();
        if (selected.Count == 0) return [];
        return [new("Current", selected.Sum(row => row.Current), color),
            new("1–30 days", selected.Sum(row => row.Days1To30), color),
            new("31–60 days", selected.Sum(row => row.Days31To60), color),
            new("61–90 days", selected.Sum(row => row.Days61To90), color),
            new("Over 90 days", selected.Sum(row => row.Over90), color)];
    }

    public static (decimal Revenue, decimal Expenses, decimal NetIncome) ProfitAndLoss(IEnumerable<FinancialAccountReportRow> rows)
    {
        var materialized = rows.ToList();
        var revenue = materialized.Where(row => row.Type == "Revenue").Sum(row => row.Balance);
        var expenses = materialized.Where(row => row.Type == "Expense").Sum(row => row.Balance);
        return (revenue, expenses, revenue - expenses);
    }

    public static bool SameCurrency(string source, string selected) => string.Equals(source, selected, StringComparison.OrdinalIgnoreCase);
}
