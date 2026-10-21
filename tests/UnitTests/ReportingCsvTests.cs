using System.Globalization;
using System.Text;
using System.Text.Json;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Contracts;
using Erp.Modules.Reporting;
using Erp.Modules.Sales.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class ReportingCsvTests
{
    private static readonly DateOnly ReportDate = new(2026, 10, 19);
    private static readonly Guid AccountId = new("58a2a463-228d-4137-a119-05d6ca79f28e");

    public static TheoryData<string, string> Routes => new()
    {
        { "sales/summary", ReportingPermissions.Sales },
        { "sales/by-customer", ReportingPermissions.Sales },
        { "sales/by-product", ReportingPermissions.Sales },
        { "purchases/summary", ReportingPermissions.Purchasing },
        { "inventory/on-hand", ReportingPermissions.Inventory },
        { "inventory/movements", ReportingPermissions.Inventory },
        { "financial/trial-balance", ReportingPermissions.Financial },
        { "financial/general-ledger", ReportingPermissions.Financial },
        { "financial/profit-and-loss", ReportingPermissions.Financial },
        { "financial/balance-sheet", ReportingPermissions.Financial },
        { "aging/receivables", ReportingPermissions.Receivables },
        { "aging/payables", ReportingPermissions.Payables },
        { "cash/summary", ReportingPermissions.Cash }
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryCsvValidatesBeforeWritingHeadersAndRetainsItsExactPermissions(string route, string permission)
    {
        var service = new ReportService { InitialFailure = new ArgumentException("The report date range is invalid.") };
        await using var harness = new EndpointHarness(service);
        var csvEndpoint = harness.Endpoint(route + ".csv");
        Assert.Equal([permission, ReportingPermissions.Export], Policies(csvEndpoint));
        Assert.Equal([permission], Policies(harness.Endpoint(route)));

        await csvEndpoint.RequestDelegate!(harness.Context);

        Assert.Equal(StatusCodes.Status400BadRequest, harness.Context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", harness.Context.Response.ContentType);
        Assert.Equal(0, harness.Context.Response.Headers.ContentDisposition.Count);
        using var body = JsonDocument.Parse(harness.Body.Bytes);
        Assert.Equal("The report date range is invalid.", body.RootElement.GetProperty("error").GetString());
        Assert.Single(service.Calls);
        Assert.False(harness.Body.IsDisposed);
    }

    [Theory]
    [InlineData("financial/general-ledger")]
    [InlineData("inventory/movements")]
    public async Task PagedExportsWriteEveryRowInOrderWithBoundedFlushesAndNoPrefetch(string route)
    {
        var service = new ReportService();
        await using var harness = new EndpointHarness(service);
        var pages = new List<int>();
        ConfigurePages(service, route, (page, size, token) =>
        {
            Assert.Equal(200, size);
            Assert.Equal(harness.Context.RequestAborted, token);
            pages.Add(page);
            if (page > 1)
            {
                Assert.True(harness.Body.FlushCount >= page - 1);
                Assert.Equal(1 + Math.Min((page - 1) * 200, 451), Parse(harness.Body.Text).Count);
            }
        }, total: 451);

        await harness.Endpoint(route + ".csv").RequestDelegate!(harness.Context);

        Assert.Equal([1, 2, 3], pages);
        AssertCsvHeaders(harness, route == "financial/general-ledger" ? "general-ledger.csv" : "inventory-movements.csv");
        Assert.InRange(harness.Body.FlushCount, 3, 4);
        Assert.InRange(harness.Body.LargestWrite, 1, 64 * 1024);
        Assert.False(harness.Body.IsDisposed);
        var records = Parse(harness.Body.Text);
        Assert.Equal(452, records.Count);
        if (route == "financial/general-ledger")
        {
            Assert.Equal(["EntryId", "EntryNumber", "BusinessDate", "Description", "AccountId", "AccountCode", "AccountName", "Debit", "Credit", "BaseDebit", "BaseCredit", "CurrencyCode"], records[0]);
            for (var index = 1; index <= 451; index++)
                Assert.Equal([RowId(index).ToString(), $"JE-{index:D4}", "2026-10-19", index == 1 ? "'=SUM(A1:A2)" : index == 2 ? "comma, quote\" and\r\nnew line ☃" : $"Entry {index}", AccountId.ToString(), "1100", "Cash ☃", "1234.56", "0", "1234.56", "0", "USD"], records[index]);
        }
        else
        {
            Assert.Equal(["MoveId", "Number", "Type", "ProductId", "ProductSku", "UnitCode", "Quantity", "SourceLocationId", "DestinationLocationId", "BusinessDate", "ReferenceDocumentType", "ReferenceDocumentId"], records[0]);
            for (var index = 1; index <= 451; index++)
                Assert.Equal([RowId(index).ToString(), $"MOVE-{index:D4}", "Receipt", AccountId.ToString(), "SKU-☃", "EA", "1234.56", "", AccountId.ToString(), "2026-10-19", index == 1 ? "'@reference" : "PurchaseOrder", ""], records[index]);
        }
    }

    [Fact]
    public async Task NonPagedExportsKeepInvariantDecimalsFormulaProtectionAndBatchTheirWrites()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var service = new ReportService
            {
                Products = Enumerable.Range(1, 451).Select(index => new SalesByProductReportRow(RowId(index), index == 1 ? "+SKU" : $"SKU-{index:D4}", index == 2 ? "quote\" comma,\r\n☃" : "Description", "USD", 12.34m, -56.78m)).ToArray()
            };
            await using var harness = new EndpointHarness(service);

            await harness.Endpoint("sales/by-product.csv").RequestDelegate!(harness.Context);

            AssertCsvHeaders(harness, "sales-by-product.csv");
            Assert.InRange(harness.Body.FlushCount, 3, 4);
            var records = Parse(harness.Body.Text);
            Assert.Equal(452, records.Count);
            Assert.Equal(["ProductId", "ProductSku", "Description", "CurrencyCode", "Quantity", "Total"], records[0]);
            for (var index = 1; index <= 451; index++)
                Assert.Equal([RowId(index).ToString(), index == 1 ? "'+SKU" : $"SKU-{index:D4}", index == 2 ? "quote\" comma,\r\n☃" : "Description", "USD", "12.34", "'-56.78"], records[index]);
            Assert.False(harness.Body.IsDisposed);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public async Task AnOversizedQuotedCellRetainsNonBmpUnicodeAndItsNeighboringRecordsByteForByte()
    {
        var oversized = new string('x', 16 * 1024 - 1) + "\U0001F680, \"quoted\"\r\n" + new string('y', 2048) + "\U0001F9FE";
        var service = new ReportService
        {
            Products =
            [
                new(RowId(1), "BEFORE", "Neighbor \U0001F680", "USD", 1m, 2m),
                new(RowId(2), "LARGE", oversized, "USD", 3m, 4m),
                new(RowId(3), "AFTER", "Neighbor \U0001F9FE", "USD", 5m, 6m)
            ]
        };
        await using var harness = new EndpointHarness(service);

        await harness.Endpoint("sales/by-product.csv").RequestDelegate!(harness.Context);

        AssertCsvHeaders(harness, "sales-by-product.csv");
        var expected = "\"ProductId\",\"ProductSku\",\"Description\",\"CurrencyCode\",\"Quantity\",\"Total\"\r\n"
            + $"\"{RowId(1)}\",\"BEFORE\",\"Neighbor \U0001F680\",\"USD\",\"1\",\"2\"\r\n"
            + $"\"{RowId(2)}\",\"LARGE\",\"{oversized.Replace("\"", "\"\"", StringComparison.Ordinal)}\",\"USD\",\"3\",\"4\"\r\n"
            + $"\"{RowId(3)}\",\"AFTER\",\"Neighbor \U0001F9FE\",\"USD\",\"5\",\"6\"\r\n";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), harness.Body.Bytes);
        Assert.Equal(expected, new UTF8Encoding(false, true).GetString(harness.Body.Bytes));
        var records = Parse(harness.Body.Text);
        Assert.Equal(4, records.Count);
        Assert.Equal(oversized, records[2][2]);
        Assert.Equal(["BEFORE", "LARGE", "AFTER"], records.Skip(1).Select(record => record[1]));
        Assert.True(harness.Body.LargestWrite > 16 * 1024, "The fixture must exercise the oversized single-record path.");
        Assert.Equal(1, harness.Body.FlushCount);
        Assert.False(harness.Body.IsDisposed);
    }

    [Theory]
    [InlineData("financial/general-ledger")]
    [InlineData("inventory/movements")]
    public async Task EmptyPagedExportsContainOnlyTheHeaderAndFetchOnce(string route)
    {
        var service = new ReportService();
        await using var harness = new EndpointHarness(service);
        var pages = new List<int>();
        ConfigurePages(service, route, (page, _, _) => pages.Add(page), total: 0);

        await harness.Endpoint(route + ".csv").RequestDelegate!(harness.Context);

        Assert.Equal([1], pages);
        Assert.Single(Parse(harness.Body.Text));
        Assert.EndsWith("\r\n", harness.Body.Text, StringComparison.Ordinal);
        Assert.InRange(harness.Body.FlushCount, 1, 2);
        Assert.False(harness.Body.IsDisposed);
    }

    [Theory]
    [InlineData("financial/general-ledger")]
    [InlineData("inventory/movements")]
    public async Task CancellationAfterTheFirstPageStopsBeforeAnotherQueryAndLeavesTheResponseStreamOpen(string route)
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ReportService();
        await using var harness = new EndpointHarness(service);
        harness.Context.RequestAborted = cancellation.Token;
        harness.Body.AfterFlush = () => cancellation.Cancel();
        var pages = new List<int>();
        ConfigurePages(service, route, (page, _, token) => { pages.Add(page); Assert.Equal(cancellation.Token, token); }, total: 451);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Endpoint(route + ".csv").RequestDelegate!(harness.Context));

        Assert.Equal([1], pages);
        Assert.Equal(201, Parse(harness.Body.Text).Count);
        Assert.False(harness.Body.IsDisposed);
    }

    [Theory]
    [InlineData("financial/general-ledger")]
    [InlineData("inventory/movements")]
    public async Task LaterQueryFailuresPropagateWithoutAppendingJsonOrFetchingFurtherPages(string route)
    {
        var service = new ReportService();
        await using var harness = new EndpointHarness(service);
        var pages = new List<int>();
        ConfigurePages(service, route, (page, _, _) =>
        {
            pages.Add(page);
            if (page == 2) throw new ArgumentException("A later query failed.");
        }, total: 451);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => harness.Endpoint(route + ".csv").RequestDelegate!(harness.Context));

        Assert.Equal("A later query failed.", exception.Message);
        Assert.Equal([1, 2], pages);
        Assert.Equal(201, Parse(harness.Body.Text).Count);
        Assert.Equal("text/csv; charset=utf-8", harness.Context.Response.ContentType);
        Assert.DoesNotContain("A later query failed.", harness.Body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"error\"", harness.Body.Text, StringComparison.Ordinal);
        Assert.False(harness.Body.IsDisposed);
    }

    [Theory]
    [InlineData("sales/by-product")]
    [InlineData("financial/general-ledger")]
    public async Task UnexpectedInitialQueryFailuresAreNotMisreportedAsValidationErrors(string route)
    {
        var service = new ReportService { InitialFailure = new InvalidOperationException("Database fixture failure.") };
        await using var harness = new EndpointHarness(service);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Endpoint(route + ".csv").RequestDelegate!(harness.Context));

        Assert.Equal("Database fixture failure.", exception.Message);
        Assert.Empty(harness.Body.Bytes);
        Assert.Null(harness.Context.Response.ContentType);
        Assert.Equal(0, harness.Context.Response.Headers.ContentDisposition.Count);
        Assert.False(harness.Body.IsDisposed);
    }

    [Theory]
    [InlineData("sales/by-product")]
    [InlineData("financial/general-ledger")]
    public async Task AlreadyCancelledExportsWriteNoCsvHeadersOrRows(string route)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var harness = new EndpointHarness(new ReportService());
        harness.Context.RequestAborted = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Endpoint(route + ".csv").RequestDelegate!(harness.Context));

        Assert.Empty(harness.Body.Bytes);
        Assert.Null(harness.Context.Response.ContentType);
        Assert.Equal(0, harness.Context.Response.Headers.ContentDisposition.Count);
        Assert.False(harness.Body.IsDisposed);
    }

    [Fact]
    public async Task CancellationWithinABufferDoesNotFlushPendingRowsWithoutTheRequestToken()
    {
        using var cancellation = new CancellationTokenSource();
        var rows = Enumerable.Range(1, 3).Select(index => new SalesByProductReportRow(RowId(index), "SKU", "Buffered row", "USD", 1m, 2m)).ToArray();
        var service = new ReportService
        {
            Products = new InterceptedRows<SalesByProductReportRow>(rows, index => { if (index == 1) cancellation.Cancel(); })
        };
        await using var harness = new EndpointHarness(service);
        harness.Context.RequestAborted = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Endpoint("sales/by-product.csv").RequestDelegate!(harness.Context));

        Assert.Empty(harness.Body.Bytes);
        Assert.Equal(0, harness.Body.FlushCount);
        Assert.False(harness.Body.IsDisposed);
    }

    [Fact]
    public async Task ABufferedEnumerationFailureIsNotMaskedByACleanupFlushFailure()
    {
        var primaryFailure = new InvalidOperationException("The row source failed.");
        var rows = Enumerable.Range(1, 3).Select(index => new SalesByProductReportRow(RowId(index), "SKU", "Buffered row", "USD", 1m, 2m)).ToArray();
        var service = new ReportService
        {
            Products = new InterceptedRows<SalesByProductReportRow>(rows, index => { if (index == 1) throw primaryFailure; })
        };
        await using var harness = new EndpointHarness(service);
        harness.Body.FlushFailure = new IOException("A cleanup flush would fail.");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Endpoint("sales/by-product.csv").RequestDelegate!(harness.Context));

        Assert.Same(primaryFailure, exception);
        Assert.Empty(harness.Body.Bytes);
        Assert.Equal(0, harness.Body.FlushCount);
        Assert.False(harness.Body.IsDisposed);
    }

    private static string[] Policies(RouteEndpoint endpoint) => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(policy => Assert.IsType<string>(policy.Policy)).ToArray();

    private static Guid RowId(int index) => new(index, 0, 0, new byte[8]);

    private static void AssertCsvHeaders(EndpointHarness harness, string filename)
    {
        Assert.Equal(StatusCodes.Status200OK, harness.Context.Response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", harness.Context.Response.ContentType);
        Assert.Equal($"attachment; filename=\"{filename}\"", harness.Context.Response.Headers.ContentDisposition);
        Assert.False(harness.Body.Bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.EndsWith("\r\n", harness.Body.Text, StringComparison.Ordinal);
    }

    private static void ConfigurePages(ReportService service, string route, Action<int, int, CancellationToken> onFetch, int total)
    {
        if (route == "financial/general-ledger")
        {
            service.Ledger = (page, size, token) =>
            {
                onFetch(page, size, token);
                var rows = Enumerable.Range(1, total).Skip((page - 1) * size).Take(size).Select(index => new GeneralLedgerReportRow(RowId(index), $"JE-{index:D4}", ReportDate, index == 1 ? "=SUM(A1:A2)" : index == 2 ? "comma, quote\" and\r\nnew line ☃" : $"Entry {index}", AccountId, "1100", "Cash ☃", 1234.56m, 0m, 1234.56m, 0m, "USD")).ToArray();
                return new GeneralLedgerReportPage(page, size, total, rows);
            };
        }
        else
        {
            service.Movements = (page, size, token) =>
            {
                onFetch(page, size, token);
                var rows = Enumerable.Range(1, total).Skip((page - 1) * size).Take(size).Select(index => new InventoryMovementReportRow(RowId(index), $"MOVE-{index:D4}", "Receipt", AccountId, "SKU-☃", "EA", 1234.56m, null, AccountId, ReportDate, index == 1 ? "@reference" : "PurchaseOrder", null)).ToArray();
                return new InventoryMovementReportPage(page, size, total, rows);
            };
        }
    }

    private static List<string[]> Parse(string csv)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (character == '"')
            {
                if (quoted && index + 1 < csv.Length && csv[index + 1] == '"') { field.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (!quoted && character == ',') { fields.Add(field.ToString()); field.Clear(); }
            else if (!quoted && character == '\r')
            {
                Assert.True(index + 1 < csv.Length && csv[++index] == '\n', "CSV record separators must be CRLF.");
                fields.Add(field.ToString()); field.Clear(); records.Add(fields.ToArray()); fields.Clear();
            }
            else { Assert.True(quoted || character != '\n', "Bare LF is not a CSV record separator."); field.Append(character); }
        }
        Assert.False(quoted, "CSV must not end within a quoted field.");
        Assert.Empty(fields);
        Assert.Equal(0, field.Length);
        return records;
    }

    private sealed class EndpointHarness : IAsyncDisposable
    {
        private readonly WebApplication _application;
        public TrackingResponseStream Body { get; } = new();
        public DefaultHttpContext Context { get; }

        public EndpointHarness(IReportingService service)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(service);
            _application = builder.Build();
            new ReportingModule().ConfigureEndpoints(_application);
            Context = new DefaultHttpContext { RequestServices = _application.Services };
            Context.Request.Method = HttpMethods.Get;
            Context.Request.QueryString = new QueryString("?from=2026-10-20&to=2026-10-19&asOf=2026-10-19&page=1&pageSize=200");
            Context.Response.Body = Body;
        }

        public RouteEndpoint Endpoint(string route) => Assert.Single(((IEndpointRouteBuilder)_application).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(), endpoint => endpoint.RoutePattern.RawText == "/api/v1/reports/" + route);

        public async ValueTask DisposeAsync() { await _application.DisposeAsync(); await Body.DisposeAsync(); }
    }

    private sealed class TrackingResponseStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public byte[] Bytes => _inner.ToArray();
        public string Text => Encoding.UTF8.GetString(Bytes);
        public int FlushCount { get; private set; }
        public int LargestWrite { get; private set; }
        public bool IsDisposed { get; private set; }
        public Action? AfterFlush { get; set; }
        public IOException? FlushFailure { get; set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !IsDisposed;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { FlushCount++; if (FlushFailure is not null) throw FlushFailure; _inner.Flush(); AfterFlush?.Invoke(); }
        public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
        public override void Write(byte[] buffer, int offset, int count) { LargestWrite = Math.Max(LargestWrite, count); _inner.Write(buffer, offset, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); LargestWrite = Math.Max(LargestWrite, buffer.Length); return _inner.WriteAsync(buffer, cancellationToken); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { IsDisposed = true; _inner.Dispose(); } base.Dispose(disposing); }
    }

    private sealed class InterceptedRows<T>(IReadOnlyList<T> rows, Action<int> beforeRow) : IReadOnlyList<T>
    {
        public int Count => rows.Count;
        public T this[int index] => rows[index];
        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < rows.Count; index++) { beforeRow(index); yield return rows[index]; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ReportService : IReportingService
    {
        public Exception? InitialFailure { get; init; }
        public List<string> Calls { get; } = [];
        public IReadOnlyList<SalesByProductReportRow> Products { get; init; } = [];
        public Func<int, int, CancellationToken, GeneralLedgerReportPage> Ledger { get; set; } = (page, size, _) => new(page, size, 0, []);
        public Func<int, int, CancellationToken, InventoryMovementReportPage> Movements { get; set; } = (page, size, _) => new(page, size, 0, []);

        private Task<ReportEnvelope<T>> Result<T>(string name, T data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(name);
            if (InitialFailure is not null) throw InitialFailure;
            return Task.FromResult(new ReportEnvelope<T>(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), null, null, ReportDate, data));
        }

        public Task<ReportEnvelope<IReadOnlyList<SalesSummaryReportRow>>> SalesSummary(DateOnly from, DateOnly to, CancellationToken token) => Result<IReadOnlyList<SalesSummaryReportRow>>(nameof(SalesSummary), [], token);
        public Task<ReportEnvelope<IReadOnlyList<SalesByCustomerReportRow>>> SalesByCustomer(DateOnly from, DateOnly to, CancellationToken token) => Result<IReadOnlyList<SalesByCustomerReportRow>>(nameof(SalesByCustomer), [], token);
        public Task<ReportEnvelope<IReadOnlyList<SalesByProductReportRow>>> SalesByProduct(DateOnly from, DateOnly to, CancellationToken token) => Result(nameof(SalesByProduct), Products, token);
        public Task<ReportEnvelope<IReadOnlyList<PurchaseSummaryReportRow>>> PurchaseSummary(DateOnly from, DateOnly to, CancellationToken token) => Result<IReadOnlyList<PurchaseSummaryReportRow>>(nameof(PurchaseSummary), [], token);
        public Task<ReportEnvelope<IReadOnlyList<InventoryOnHandReportRow>>> InventoryOnHand(DateOnly asOf, CancellationToken token) => Result<IReadOnlyList<InventoryOnHandReportRow>>(nameof(InventoryOnHand), [], token);
        public Task<ReportEnvelope<InventoryMovementReportPage>> InventoryMovements(DateOnly from, DateOnly to, int page, int pageSize, CancellationToken token) => Result(nameof(InventoryMovements), InitialFailure is null ? Movements(page, pageSize, token) : new InventoryMovementReportPage(page, pageSize, 0, []), token);
        public Task<ReportEnvelope<IReadOnlyList<FinancialAccountReportRow>>> TrialBalance(DateOnly asOf, CancellationToken token) => Result<IReadOnlyList<FinancialAccountReportRow>>(nameof(TrialBalance), [], token);
        public Task<ReportEnvelope<GeneralLedgerReportPage>> GeneralLedger(DateOnly from, DateOnly to, int page, int pageSize, CancellationToken token) => Result(nameof(GeneralLedger), InitialFailure is null ? Ledger(page, pageSize, token) : new GeneralLedgerReportPage(page, pageSize, 0, []), token);
        public Task<ReportEnvelope<IReadOnlyList<FinancialAccountReportRow>>> ProfitAndLoss(DateOnly from, DateOnly to, CancellationToken token) => Result<IReadOnlyList<FinancialAccountReportRow>>(nameof(ProfitAndLoss), [], token);
        public Task<ReportEnvelope<IReadOnlyList<FinancialAccountReportRow>>> BalanceSheet(DateOnly asOf, CancellationToken token) => Result<IReadOnlyList<FinancialAccountReportRow>>(nameof(BalanceSheet), [], token);
        public Task<ReportEnvelope<IReadOnlyList<AgingReportRow>>> ReceivablesAging(DateOnly asOf, CancellationToken token) => Result<IReadOnlyList<AgingReportRow>>(nameof(ReceivablesAging), [], token);
        public Task<ReportEnvelope<IReadOnlyList<AgingReportRow>>> PayablesAging(DateOnly asOf, CancellationToken token) => Result<IReadOnlyList<AgingReportRow>>(nameof(PayablesAging), [], token);
        public Task<ReportEnvelope<IReadOnlyList<CashSummaryRow>>> CashSummary(DateOnly from, DateOnly to, CancellationToken token) => Result<IReadOnlyList<CashSummaryRow>>(nameof(CashSummary), [], token);
    }
}
