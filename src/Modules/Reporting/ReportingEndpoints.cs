using System.Globalization;
using System.Reflection;
using System.Text;
using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Modules.Reporting;

internal static class ReportingEndpoints
{
    public static void MapReportingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/reports").WithTags("Reporting");
        group.MapGet("/sales/summary", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Run(() => service.SalesSummary(from, to, token))).RequirePermission(ReportingPermissions.Sales);
        group.MapGet("/sales/by-customer", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Run(() => service.SalesByCustomer(from, to, token))).RequirePermission(ReportingPermissions.Sales);
        group.MapGet("/sales/by-product", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Run(() => service.SalesByProduct(from, to, token))).RequirePermission(ReportingPermissions.Sales);
        group.MapGet("/purchases/summary", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Run(() => service.PurchaseSummary(from, to, token))).RequirePermission(ReportingPermissions.Purchasing);
        group.MapGet("/inventory/on-hand", (DateOnly asOf, IReportingService service, CancellationToken token) => Run(() => service.InventoryOnHand(asOf, token))).RequirePermission(ReportingPermissions.Inventory);
        group.MapGet("/inventory/movements", (DateOnly from, DateOnly to, int page, int pageSize, IReportingService service, CancellationToken token) => Run(() => service.InventoryMovements(from, to, page, pageSize, token))).RequirePermission(ReportingPermissions.Inventory);
        group.MapGet("/financial/trial-balance", (DateOnly asOf, IReportingService service, CancellationToken token) => Run(() => service.TrialBalance(asOf, token))).RequirePermission(ReportingPermissions.Financial);
        group.MapGet("/financial/general-ledger", (DateOnly from, DateOnly to, int page, int pageSize, IReportingService service, CancellationToken token) => Run(() => service.GeneralLedger(from, to, page, pageSize, token))).RequirePermission(ReportingPermissions.Financial);
        group.MapGet("/financial/profit-and-loss", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Run(() => service.ProfitAndLoss(from, to, token))).RequirePermission(ReportingPermissions.Financial);
        group.MapGet("/financial/balance-sheet", (DateOnly asOf, IReportingService service, CancellationToken token) => Run(() => service.BalanceSheet(asOf, token))).RequirePermission(ReportingPermissions.Financial);
        group.MapGet("/aging/receivables", (DateOnly asOf, IReportingService service, CancellationToken token) => Run(() => service.ReceivablesAging(asOf, token))).RequirePermission(ReportingPermissions.Receivables);
        group.MapGet("/aging/payables", (DateOnly asOf, IReportingService service, CancellationToken token) => Run(() => service.PayablesAging(asOf, token))).RequirePermission(ReportingPermissions.Payables);
        group.MapGet("/cash/summary", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Run(() => service.CashSummary(from, to, token))).RequirePermission(ReportingPermissions.Cash);

        group.MapGet("/sales/summary.csv", async (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv((await service.SalesSummary(from, to, token)).Data, "sales-summary.csv")).RequirePermission(ReportingPermissions.Sales).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/sales/by-customer.csv", async (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv((await service.SalesByCustomer(from, to, token)).Data, "sales-by-customer.csv")).RequirePermission(ReportingPermissions.Sales).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/sales/by-product.csv", async (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv((await service.SalesByProduct(from, to, token)).Data, "sales-by-product.csv")).RequirePermission(ReportingPermissions.Sales).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/purchases/summary.csv", async (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv((await service.PurchaseSummary(from, to, token)).Data, "purchase-summary.csv")).RequirePermission(ReportingPermissions.Purchasing).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/inventory/on-hand.csv", async (DateOnly asOf, IReportingService service, CancellationToken token) => Csv((await service.InventoryOnHand(asOf, token)).Data, "inventory-on-hand.csv")).RequirePermission(ReportingPermissions.Inventory).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/inventory/movements.csv", (DateOnly from, DateOnly to, IReportingService service) => new PagedCsvResult<Erp.Modules.Inventory.Contracts.InventoryMovementReportRow>(async (page, token) => { var data = (await service.InventoryMovements(from, to, page, 200, token)).Data; return (data.Items, data.Total); }, "inventory-movements.csv")).RequirePermission(ReportingPermissions.Inventory).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/financial/trial-balance.csv", async (DateOnly asOf, IReportingService service, CancellationToken token) => Csv((await service.TrialBalance(asOf, token)).Data, "trial-balance.csv")).RequirePermission(ReportingPermissions.Financial).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/financial/general-ledger.csv", (DateOnly from, DateOnly to, IReportingService service) => new PagedCsvResult<Erp.Modules.Accounting.Contracts.GeneralLedgerReportRow>(async (page, token) => { var data = (await service.GeneralLedger(from, to, page, 200, token)).Data; return (data.Items, data.Total); }, "general-ledger.csv")).RequirePermission(ReportingPermissions.Financial).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/financial/profit-and-loss.csv", async (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv((await service.ProfitAndLoss(from, to, token)).Data, "profit-and-loss.csv")).RequirePermission(ReportingPermissions.Financial).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/financial/balance-sheet.csv", async (DateOnly asOf, IReportingService service, CancellationToken token) => Csv((await service.BalanceSheet(asOf, token)).Data, "balance-sheet.csv")).RequirePermission(ReportingPermissions.Financial).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/aging/receivables.csv", async (DateOnly asOf, IReportingService service, CancellationToken token) => Csv((await service.ReceivablesAging(asOf, token)).Data, "receivables-aging.csv")).RequirePermission(ReportingPermissions.Receivables).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/aging/payables.csv", async (DateOnly asOf, IReportingService service, CancellationToken token) => Csv((await service.PayablesAging(asOf, token)).Data, "payables-aging.csv")).RequirePermission(ReportingPermissions.Payables).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/cash/summary.csv", async (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv((await service.CashSummary(from, to, token)).Data, "cash-summary.csv")).RequirePermission(ReportingPermissions.Cash).RequirePermission(ReportingPermissions.Export);
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> action) { try { return Results.Ok(await action()); } catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); } }
    private static CsvResult<T> Csv<T>(IEnumerable<T> rows, string fileName) => new(rows, fileName);
}

internal sealed class CsvResult<T>(IEnumerable<T> rows, string fileName) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "text/csv; charset=utf-8"; httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance); await ReportingCsv.Write(httpContext, string.Join(',', properties.Select(x => ReportingCsv.Quote(x.Name))));
        foreach (var row in rows) { httpContext.RequestAborted.ThrowIfCancellationRequested(); await ReportingCsv.Write(httpContext, string.Join(',', properties.Select(property => ReportingCsv.Quote(ReportingCsv.Format(property.GetValue(row)))))); }
    }
}

public static class ReportingCsv
{
    internal static async Task Write(HttpContext context, string value) { await context.Response.WriteAsync(value, context.RequestAborted); await context.Response.WriteAsync("\r\n", context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted); }
    internal static string Format(object? value) => value switch { null => string.Empty, DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeOffset instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty, _ => value.ToString() ?? string.Empty };
    public static string Quote(string value) { if (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' || value[0] is '\t' or '\r')) value = "'" + value; return '"' + value.Replace("\"", "\"\"") + '"'; }
}

internal sealed class PagedCsvResult<T>(Func<int, CancellationToken, Task<(IReadOnlyList<T> Items, int Total)>> fetch, string fileName) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "text/csv; charset=utf-8"; context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\""; var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance); await ReportingCsv.Write(context, string.Join(',', properties.Select(x => ReportingCsv.Quote(x.Name)))); var page = 1; var written = 0;
        do { var result = await fetch(page++, context.RequestAborted); foreach (var row in result.Items) { await ReportingCsv.Write(context, string.Join(',', properties.Select(property => ReportingCsv.Quote(ReportingCsv.Format(property.GetValue(row)))))); } written += result.Items.Count; if (result.Items.Count == 0 || written >= result.Total) break; } while (true);
    }
}
