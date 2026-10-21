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

        group.MapGet("/sales/summary.csv", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv(async () => (await service.SalesSummary(from, to, token)).Data, "sales-summary.csv")).RequirePermission(ReportingPermissions.Sales).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/sales/by-customer.csv", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv(async () => (await service.SalesByCustomer(from, to, token)).Data, "sales-by-customer.csv")).RequirePermission(ReportingPermissions.Sales).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/sales/by-product.csv", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv(async () => (await service.SalesByProduct(from, to, token)).Data, "sales-by-product.csv")).RequirePermission(ReportingPermissions.Sales).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/purchases/summary.csv", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv(async () => (await service.PurchaseSummary(from, to, token)).Data, "purchase-summary.csv")).RequirePermission(ReportingPermissions.Purchasing).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/inventory/on-hand.csv", (DateOnly asOf, IReportingService service, CancellationToken token) => Csv(async () => (await service.InventoryOnHand(asOf, token)).Data, "inventory-on-hand.csv")).RequirePermission(ReportingPermissions.Inventory).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/inventory/movements.csv", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => PagedCsv<Erp.Modules.Inventory.Contracts.InventoryMovementReportRow>(async (page, pageToken) => { var data = (await service.InventoryMovements(from, to, page, ReportingCsv.BatchSize, pageToken)).Data; return (data.Items, data.Total); }, "inventory-movements.csv", token)).RequirePermission(ReportingPermissions.Inventory).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/financial/trial-balance.csv", (DateOnly asOf, IReportingService service, CancellationToken token) => Csv(async () => (await service.TrialBalance(asOf, token)).Data, "trial-balance.csv")).RequirePermission(ReportingPermissions.Financial).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/financial/general-ledger.csv", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => PagedCsv<Erp.Modules.Accounting.Contracts.GeneralLedgerReportRow>(async (page, pageToken) => { var data = (await service.GeneralLedger(from, to, page, ReportingCsv.BatchSize, pageToken)).Data; return (data.Items, data.Total); }, "general-ledger.csv", token)).RequirePermission(ReportingPermissions.Financial).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/financial/profit-and-loss.csv", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv(async () => (await service.ProfitAndLoss(from, to, token)).Data, "profit-and-loss.csv")).RequirePermission(ReportingPermissions.Financial).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/financial/balance-sheet.csv", (DateOnly asOf, IReportingService service, CancellationToken token) => Csv(async () => (await service.BalanceSheet(asOf, token)).Data, "balance-sheet.csv")).RequirePermission(ReportingPermissions.Financial).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/aging/receivables.csv", (DateOnly asOf, IReportingService service, CancellationToken token) => Csv(async () => (await service.ReceivablesAging(asOf, token)).Data, "receivables-aging.csv")).RequirePermission(ReportingPermissions.Receivables).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/aging/payables.csv", (DateOnly asOf, IReportingService service, CancellationToken token) => Csv(async () => (await service.PayablesAging(asOf, token)).Data, "payables-aging.csv")).RequirePermission(ReportingPermissions.Payables).RequirePermission(ReportingPermissions.Export);
        group.MapGet("/cash/summary.csv", (DateOnly from, DateOnly to, IReportingService service, CancellationToken token) => Csv(async () => (await service.CashSummary(from, to, token)).Data, "cash-summary.csv")).RequirePermission(ReportingPermissions.Cash).RequirePermission(ReportingPermissions.Export);
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> action) { try { return Results.Ok(await action()); } catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); } }
    private static async Task<IResult> Csv<T>(Func<Task<IReadOnlyList<T>>> fetch, string fileName)
    {
        try { return new CsvResult<T>(await fetch(), fileName); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    }

    private static async Task<IResult> PagedCsv<T>(Func<int, CancellationToken, Task<(IReadOnlyList<T> Items, int Total)>> fetch, string fileName, CancellationToken token)
    {
        // Validate and fetch before the result writes CSV headers or starts the response.
        // Once streaming begins a later query failure must terminate the export, not append JSON.
        try { return new PagedCsvResult<T>(await fetch(1, token), fetch, fileName); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    }
}

internal sealed class CsvResult<T>(IEnumerable<T> rows, string fileName) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.RequestAborted.ThrowIfCancellationRequested();
        var writer = ReportingCsv.CreateWriter(httpContext, fileName);
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        await ReportingCsv.WriteHeader(writer, properties, httpContext.RequestAborted);
        var bufferedRows = 0;
        foreach (var row in rows)
        {
            await ReportingCsv.WriteRow(writer, properties, row, httpContext.RequestAborted);
            if (++bufferedRows == ReportingCsv.BatchSize)
            {
                await writer.FlushAsync(httpContext.RequestAborted);
                bufferedRows = 0;
            }
        }
        await writer.FlushAsync(httpContext.RequestAborted);
    }
}

public static class ReportingCsv
{
    internal const int BatchSize = 200;

    internal static ReportingCsvWriter CreateWriter(HttpContext context, string fileName)
    {
        context.Response.ContentType = "text/csv; charset=utf-8";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        return new ReportingCsvWriter(context);
    }

    internal static Task WriteHeader(ReportingCsvWriter writer, PropertyInfo[] properties, CancellationToken token) =>
        writer.WriteLineAsync(string.Join(',', properties.Select(x => Quote(x.Name))), token);

    internal static Task WriteRow<T>(ReportingCsvWriter writer, PropertyInfo[] properties, T row, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return writer.WriteLineAsync(string.Join(',', properties.Select(property => Quote(Format(property.GetValue(row))))), token);
    }
    internal static string Format(object? value) => value switch { null => string.Empty, DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeOffset instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty, _ => value.ToString() ?? string.Empty };
    public static string Quote(string value) { if (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' || value[0] is '\t' or '\r')) value = "'" + value; return '"' + value.Replace("\"", "\"\"") + '"'; }
}

internal sealed class ReportingCsvWriter(HttpContext context)
{
    private const int BufferCharacters = 16 * 1024;
    private readonly StringBuilder _buffer = new(BufferCharacters);

    public async Task WriteLineAsync(string value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_buffer.Length + value.Length + 2 > BufferCharacters) await WriteBufferAsync(token);
        _buffer.Append(value).Append("\r\n");
        // A single serialized record can exceed the normal buffer. Never retain
        // more than one such record or collect the entire export in memory.
        if (_buffer.Length >= BufferCharacters) await WriteBufferAsync(token);
    }

    public async Task FlushAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await WriteBufferAsync(token);
        await context.Response.Body.FlushAsync(token);
    }

    private async Task WriteBufferAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_buffer.Length == 0) return;
        // Encoding.GetBytes does not emit a preamble. The writer owns only a
        // managed buffer, so failure/cancellation never triggers a cleanup flush
        // without the request token or closes the host-owned response stream.
        var bytes = Encoding.UTF8.GetBytes(_buffer.ToString());
        await context.Response.Body.WriteAsync(bytes, token);
        _buffer.Clear();
    }
}

internal sealed class PagedCsvResult<T>((IReadOnlyList<T> Items, int Total) firstPage, Func<int, CancellationToken, Task<(IReadOnlyList<T> Items, int Total)>> fetch, string fileName) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        var writer = ReportingCsv.CreateWriter(context, fileName);
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        await ReportingCsv.WriteHeader(writer, properties, context.RequestAborted);
        var page = 1;
        var written = 0;
        var result = firstPage;
        while (true)
        {
            foreach (var row in result.Items)
                await ReportingCsv.WriteRow(writer, properties, row, context.RequestAborted);
            await writer.FlushAsync(context.RequestAborted);
            written += result.Items.Count;
            if (result.Items.Count == 0 || written >= result.Total) break;
            context.RequestAborted.ThrowIfCancellationRequested();
            result = await fetch(++page, context.RequestAborted);
        }
    }
}
