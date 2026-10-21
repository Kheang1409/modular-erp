using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Reporting;
using Erp.Modules.Reporting.Components;
using Erp.Modules.Reporting.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.UnitTests;

public sealed class ReportingPaginationTests
{
    [Theory]
    [InlineData(1, 0, 0, 100, 1, 0, 0, false, false)]
    [InlineData(1, 202, 100, 100, 3, 1, 100, false, true)]
    [InlineData(2, 202, 100, 100, 3, 101, 200, true, true)]
    [InlineData(3, 202, 2, 100, 3, 201, 202, true, false)]
    [InlineData(2, 26, 1, 25, 2, 26, 26, true, false)]
    [InlineData(2, 101, 50, 50, 3, 51, 100, true, true)]
    [InlineData(0, -1, -1, 100, 1, 0, 0, false, false)]
    [InlineData(int.MaxValue, int.MaxValue, 100, 100, 21474837, 2147483601, int.MaxValue, true, false)]
    public void PageStateReportsExactRangesAndSafeBoundaries(int page, int total, int count, int size, int pages, int first, int last, bool previous, bool next)
    {
        var state = new ReportPaginationState();

        state.SetPage(page, total, count, size);

        Assert.Equal(size, state.PageSize);
        Assert.Equal(pages, state.PageCount);
        Assert.Equal(first, state.FirstRow);
        Assert.Equal(last, state.LastRow);
        Assert.Equal(previous, state.HasPrevious);
        Assert.Equal(next, state.HasNext);
        Assert.InRange(state.Page, 1, state.PageCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    [InlineData(101)]
    public void UnsupportedPageSizesAreRejectedWithoutChangingState(int size)
    {
        var state = new ReportPaginationState();
        state.SetPage(2, 101, 50, 50);

        Assert.Throws<ArgumentOutOfRangeException>(() => state.SetPage(1, 101, 25, size));

        Assert.Equal(2, state.Page);
        Assert.Equal(50, state.PageSize);
        Assert.Equal(101, state.Total);
    }

    [Fact]
    public void EmptyResultsResetAnOutOfRangePageAndClearItsRange()
    {
        var state = new ReportPaginationState();
        state.SetPage(3, 202, 2);

        state.SetPage(3, 0, 0);

        Assert.Equal(1, state.Page);
        Assert.Equal(1, state.PageCount);
        Assert.Equal(0, state.FirstRow);
        Assert.Equal(0, state.LastRow);
        Assert.False(state.HasPrevious);
        Assert.False(state.HasNext);
    }

    [Theory]
    [InlineData(false, 1, "Rows 1-100 of 202", true, false)]
    [InlineData(false, 3, "Rows 201-202 of 202", false, true)]
    [InlineData(true, 2, "Rows 101-200 of 202", true, true)]
    public async Task PagerHasNamedControlsAndDisablesUnavailableOrBusyNavigation(bool busy, int page, string range, bool previousDisabled, bool nextDisabled)
    {
        var state = new ReportPaginationState();
        state.SetPage(page, 202, page == 3 ? 2 : 100);
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(ReportPagination.State)] = state,
            [nameof(ReportPagination.Label)] = "General ledger pagination",
            [nameof(ReportPagination.PageSizeLabel)] = "General ledger rows per page",
            [nameof(ReportPagination.Busy)] = busy
        });
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ReportPagination>(parameters)).ToHtmlString());

        Assert.Contains("aria-label=\"General ledger pagination\"", html);
        Assert.Contains("aria-label=\"General ledger rows per page\"", html);
        Assert.Contains("role=\"status\" aria-live=\"polite\"", html);
        Assert.Contains(range, WebUtility.HtmlDecode(html));
        Assert.Contains($"Page {page} of 3", html);
        Assert.Equal(previousDisabled, Regex.IsMatch(html, "<button[^>]*disabled[^>]*>Previous</button>"));
        Assert.Equal(nextDisabled, Regex.IsMatch(html, "<button[^>]*disabled[^>]*>Next</button>"));
        Assert.Equal(busy, Regex.IsMatch(html, "<select[^>]*disabled"));
        Assert.Contains("value=\"25\"", html);
        Assert.Contains("value=\"50\"", html);
        Assert.Contains("value=\"100\"", html);
    }

    [Theory]
    [InlineData(typeof(FinancialReportsPage), 4, "General ledger table", "General ledger pagination", "Rows 1-100 of 202")]
    [InlineData(typeof(InventoryReportsPage), 2, "Movement history table", "Movement history pagination", "Rows 1-100 of 101")]
    [InlineData(typeof(ReceivablesReportPage), 1, "Receivables aging table", null, null)]
    [InlineData(typeof(PayablesReportPage), 1, "Payables aging table", null, null)]
    public async Task ReportPagesLoadSequentiallyAndRenderAccessiblePopulatedTables(Type pageType, int requests, string region, string? pager, string? range)
    {
        using var handler = new ReportsHandler();
        await using var rendering = new PageRendering(pageType, handler);
        var pending = rendering.RenderAsync();
        try
        {
            await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await rendering.Dispatcher.InvokeAsync(() => Assert.Single(handler.Requests));
            handler.ReleaseFirst.TrySetResult();
            var html = await pending.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(requests, handler.Requests.Count);
            Assert.Contains($"role=\"region\" aria-label=\"{region}\" tabindex=\"0\"", html);
            Assert.Contains("<caption>", html);
            Assert.Contains("<th scope=\"col\">", html);
            Assert.DoesNotContain("role=\"alert\"", html);
            Assert.DoesNotContain(">_error<", html);
            Assert.Contains(".csv?", html);
            if (pager is not null)
            {
                Assert.Contains($"aria-label=\"{pager}\"", html);
                Assert.Contains(range!, WebUtility.HtmlDecode(html));
                var pageRequest = Assert.Single(handler.Requests, request => request.Query.Contains("pageSize=100", StringComparison.Ordinal));
                Assert.Contains("page=1", pageRequest.Query);
                Assert.Contains("Applied period:", html);
            }
        }
        finally
        {
            handler.ReleaseFirst.TrySetResult();
        }
    }

    [Theory]
    [InlineData(typeof(FinancialReportsPage), "Financial reports could not be loaded.")]
    [InlineData(typeof(InventoryReportsPage), "Inventory reports could not be loaded.")]
    [InlineData(typeof(ReceivablesReportPage), "Receivables aging could not be loaded.")]
    [InlineData(typeof(PayablesReportPage), "Payables aging could not be loaded.")]
    public async Task FailedReportReadsRenderTheActualErrorAndHideExportLinks(Type pageType, string expected)
    {
        using var handler = new ReportsHandler(fail: true);
        handler.ReleaseFirst.TrySetResult();
        await using var rendering = new PageRendering(pageType, handler);

        var html = await rendering.RenderAsync();

        Assert.Contains($"role=\"alert\">{expected}", html);
        Assert.DoesNotContain(">_error<", html);
        Assert.DoesNotContain(".csv?", html);
    }

    [Theory]
    [InlineData(typeof(FinancialReportsPage))]
    [InlineData(typeof(InventoryReportsPage))]
    [InlineData(typeof(ReceivablesReportPage))]
    [InlineData(typeof(PayablesReportPage))]
    public async Task ReportPagesPreserveTheExistingExportPermissionGate(Type pageType)
    {
        using var handler = new ReportsHandler();
        handler.ReleaseFirst.TrySetResult();
        await using var rendering = new PageRendering(pageType, handler, allowExport: false);

        var html = await rendering.RenderAsync();

        Assert.Contains("<caption>", html);
        Assert.DoesNotContain(".csv?", html);
    }

    private sealed class PageRendering : IAsyncDisposable
    {
        private readonly Type _pageType;
        private readonly HttpClient _http;
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        public Dispatcher Dispatcher => _renderer.Dispatcher;

        public PageRendering(Type pageType, ReportsHandler handler, bool allowExport = true)
        {
            _pageType = pageType;
            _http = new(handler, disposeHandler: false) { BaseAddress = new("https://erp.example.test/") };
            var registrations = new ServiceCollection();
            registrations.AddLogging();
            registrations.AddSingleton(_http);
            registrations.AddSingleton<NavigationManager, TestNavigation>();
            registrations.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(allowExport));
            registrations.AddAuthorizationCore(options => options.AddPolicy(ReportingPermissions.Export, policy => policy.RequireClaim("permission", ReportingPermissions.Export)));
            _services = registrations.BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public Task<string> RenderAsync() => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment child = builder =>
            {
                builder.OpenComponent(0, _pageType);
                builder.CloseComponent();
            };
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CascadingAuthenticationState.ChildContent)] = child });
            return (await _renderer.RenderComponentAsync<CascadingAuthenticationState>(parameters)).ToHtmlString();
        });

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
            _http.Dispose();
        }
    }

    private sealed class TestAuthentication(bool allowExport) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new(new ClaimsIdentity(
            allowExport ? [new Claim("permission", ReportingPermissions.Export)] : [], "Test"))));
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://erp.example.test/", "https://erp.example.test/reports/financial");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Report rendering must not navigate.");
    }

    private sealed class ReportsHandler(bool fail = false) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (Requests.Count == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            if (fail) return new(HttpStatusCode.ServiceUnavailable);

            var date = DateOnly.FromDateTime(DateTime.Today);
            object data = uri.AbsolutePath switch
            {
                "/api/v1/reports/financial/general-ledger" => new GeneralLedgerReportPage(1, 100, 202,
                    Enumerable.Range(1, 100).Select(index => new GeneralLedgerReportRow(Guid.NewGuid(), $"JE-{index}", date, "Posted entry", Guid.NewGuid(), "1000", "Cash", 1m, 0m, 1m, 0m, "USD")).ToArray()),
                "/api/v1/reports/inventory/on-hand" => new InventoryOnHandReportRow[] { new(Guid.NewGuid(), "SKU", "EA", Guid.NewGuid(), "MAIN", "Stock", 10m, 2m, 8m) },
                "/api/v1/reports/inventory/movements" => new InventoryMovementReportPage(1, 100, 101,
                    Enumerable.Range(1, 100).Select(index => new InventoryMovementReportRow(Guid.NewGuid(), $"MOV-{index}", "Adjustment", Guid.NewGuid(), "SKU", "EA", 1m, null, Guid.NewGuid(), date, null, null)).ToArray()),
                "/api/v1/reports/aging/receivables" or "/api/v1/reports/aging/payables" => new AgingReportRow[] { new(Guid.NewGuid(), "Trading party", "USD", 10m, 0m, 0m, 0m, 0m, 10m) },
                _ => new FinancialAccountReportRow[] { new(Guid.NewGuid(), "1000", "Cash", "Asset", 10m, 0m, 10m) }
            };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ReportEnvelope<object>(DateTimeOffset.UnixEpoch, null, null, null, data)) };
        }
    }
}
