using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Erp.Abstractions.Context;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Reporting;
using Erp.Modules.Reporting.Components;
using Erp.Modules.Reporting.Pages;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.UnitTests;

public sealed class DashboardVisualizationTests
{
    [Theory]
    [InlineData(ReportingPermissions.Sales)]
    [InlineData(ReportingPermissions.Purchasing)]
    [InlineData(ReportingPermissions.Inventory)]
    [InlineData(ReportingPermissions.Financial)]
    [InlineData(ReportingPermissions.Receivables)]
    [InlineData(ReportingPermissions.Payables)]
    [InlineData(ReportingPermissions.Cash)]
    public void EachExistingReportPermissionGrantsAuthenticatedDashboardAccess(string permission) =>
        Assert.True(ReportingPermissions.CanViewDashboard(User(permission)));

    [Fact]
    public void ExportAloneOrUnauthenticatedReportClaimsDoNotGrantDashboardAccess()
    {
        Assert.False(ReportingPermissions.CanViewDashboard(User(ReportingPermissions.Export)));
        Assert.False(ReportingPermissions.CanViewDashboard(new(new ClaimsIdentity([new Claim("permission", ReportingPermissions.Sales)]))));
        Assert.False(ReportingPermissions.CanViewDashboard(User()));
    }

    [Fact]
    public void CustomerRankingAggregatesOnlySelectedCurrencyAndLimitsToSix()
    {
        var customer = Guid.NewGuid();
        var rows = new List<SalesByCustomerReportRow>
        {
            new(customer, "Primary", "USD", 1, 50m),
            new(customer, "Primary", "usd", 1, 25m),
            new(customer, "Primary", "EUR", 1, 99999m)
        };
        rows.AddRange(Enumerable.Range(1, 7).Select(index => new SalesByCustomerReportRow(Guid.NewGuid(), $"Customer {index}", "USD", 1, index)));

        var points = DashboardCalculations.TopCustomers(rows, "USD");

        Assert.Equal(6, points.Count);
        Assert.Equal(new DashboardChartPoint("Primary", 75m), points[0]);
        Assert.Equal(7m, points[1].Value);
        Assert.Equal(3m, points[^1].Value);
        Assert.Empty(DashboardCalculations.TopCustomers(rows, "GBP"));
    }

    [Fact]
    public void ProductRankingKeepsCurrenciesSeparateAndPreservesReturns()
    {
        var product = Guid.NewGuid();
        SalesByProductReportRow[] rows = [new(product, "SKU-A", "Item", "USD", 2, 12m), new(product, "SKU-A", "Item", "USD", -1, -2m), new(product, "SKU-A", "Item", "EUR", 1, 500m)];

        Assert.Equal(new DashboardChartPoint("SKU-A", 10m, "teal"), Assert.Single(DashboardCalculations.TopProducts(rows, "usd")));
        Assert.Equal(500m, Assert.Single(DashboardCalculations.TopProducts(rows, "EUR")).Value);
    }

    [Fact]
    public void AgingBucketsUseOnlySelectedCurrencyAndRetainZeroBuckets()
    {
        AgingReportRow[] rows = [new(Guid.NewGuid(), "Customer", "USD", 10m, 0, 3m, 0, 5m, 18m), new(Guid.NewGuid(), "Other", "EUR", 1000m, 1000m, 1000m, 1000m, 1000m, 5000m)];

        var points = DashboardCalculations.AgingBuckets(rows, "usd", "rose");

        Assert.Equal([10m, 0m, 3m, 0m, 5m], points.Select(point => point.Value));
        Assert.All(points, point => Assert.Equal("rose", point.Color));
        Assert.Empty(DashboardCalculations.AgingBuckets(rows, "GBP"));
    }

    [Fact]
    public void ProfitAndLossUsesNaturalBalancesAndRevenueLessExpenses()
    {
        FinancialAccountReportRow[] rows = [Account("Revenue", 100m), Account("Revenue", -10m), Account("Expense", 25m), Account("Expense", -5m), Account("Asset", 9999m)];

        Assert.Equal((90m, 20m, 70m), DashboardCalculations.ProfitAndLoss(rows));
        Assert.Equal((0m, 0m, 0m), DashboardCalculations.ProfitAndLoss([]));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public async Task SignedZeroAndFractionalChartValuesHaveInvariantSvgCoordinatesAndExactAccessibleData(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var html = await RenderAsync<BarChart>(new()
            {
                [nameof(BarChart.Title)] = "Signed balances",
                [nameof(BarChart.Description)] = "Balances include returns and zero activity",
                [nameof(BarChart.UnitLabel)] = "USD",
                [nameof(BarChart.Points)] = new DashboardChartPoint[] { new("Return", -5.5m), new("Zero", 0), new("Sale", 11m), new("Fraction", 0.0000001m) }
            });

            Assert.Contains("role=\"img\"", html);
            Assert.Contains("aria-labelledby=\"chart-", html);
            Assert.Contains("<caption>Signed balances", html);
            Assert.Contains("View chart data", html);
            Assert.Contains("scope=\"row\">Fraction", html);
            Assert.Contains(cultureName == "fr-FR" ? "0,0000001" : "0.0000001", html);
            Assert.DoesNotContain("NaN", html);
            Assert.DoesNotContain("Infinity", html);
            var rects = Regex.Matches(html, "<rect[^>]* x=\"(?<x>[^\"]+)\"[^>]* width=\"(?<width>[^\"]+)\"");
            Assert.Equal(4, rects.Count);
            Assert.All(rects.Cast<Match>(), rect =>
            {
                Assert.DoesNotContain(",", rect.Groups["x"].Value);
                Assert.DoesNotContain(",", rect.Groups["width"].Value);
                Assert.InRange(double.Parse(rect.Groups["x"].Value, CultureInfo.InvariantCulture), 190, 465);
                Assert.InRange(double.Parse(rect.Groups["width"].Value, CultureInfo.InvariantCulture), 0, 275);
            });
            Assert.Equal("0", rects[1].Groups["width"].Value);
            var secondRender = await RenderAsync<BarChart>(new() { [nameof(BarChart.Title)] = "Other", [nameof(BarChart.Points)] = new DashboardChartPoint[] { new("One", 1) } });
            Assert.NotEqual(Regex.Match(html, "id=\"(?<id>chart-[^\"]+-title)\"").Groups["id"].Value, Regex.Match(secondRender, "id=\"(?<id>chart-[^\"]+-title)\"").Groups["id"].Value);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public async Task EmptyChartsUseTruthfulEmptyStateRatherThanFabricatedSvg()
    {
        var html = await RenderAsync<BarChart>(new() { [nameof(BarChart.Title)] = "Payments", [nameof(BarChart.Points)] = Array.Empty<DashboardChartPoint>() });

        Assert.Contains("class=\"chart-empty\"", html);
        Assert.Contains("No activity for these filters.", html);
        Assert.DoesNotContain("<svg", html);
    }

    [Fact]
    public async Task InventoryVisualizationNeverAddsUnlikeUnits()
    {
        var html = await RenderAsync<ReportVisualizations>(new()
        {
            [nameof(ReportVisualizations.Kind)] = ReportVisualizationKind.Inventory,
            [nameof(ReportVisualizations.Inventory)] = new InventoryOnHandReportRow[]
            {
                new(Guid.NewGuid(), "EA-item", "EA", Guid.NewGuid(), "A", "Stock", 7m, 2m, 5m),
                new(Guid.NewGuid(), "KG-item", "KG", Guid.NewGuid(), "A", "Stock", 100m, 20m, 80m)
            }
        });

        Assert.Contains("value=\"EA\"", html);
        Assert.Contains("value=\"KG\"", html);
        Assert.Contains("Value (EA)", html);
        Assert.Contains(">7.00</td>", html);
        Assert.DoesNotContain(">107.00</td>", html);
        Assert.DoesNotContain(">100.00</td>", html);
    }

    [Fact]
    public async Task SalesVisualizationNeverAddsDifferentCurrencies()
    {
        var customer = Guid.NewGuid();
        var html = await RenderAsync<ReportVisualizations>(new()
        {
            [nameof(ReportVisualizations.Kind)] = ReportVisualizationKind.Sales,
            [nameof(ReportVisualizations.Customers)] = new SalesByCustomerReportRow[] { new(customer, "Client", "EUR", 1, 7m), new(customer, "Client", "USD", 1, 100m) }
        });

        Assert.Contains("Value (EUR)", html);
        Assert.Contains(">7.00</td>", html);
        Assert.DoesNotContain(">107.00</td>", html);
        Assert.DoesNotContain(">100.00</td>", html);
    }

    [Fact]
    public async Task FinancialVisualizationLabelsBaseCurrencyAndPreservesLossSign()
    {
        var html = await RenderAsync<ReportVisualizations>(new()
        {
            [nameof(ReportVisualizations.Kind)] = ReportVisualizationKind.Financial,
            [nameof(ReportVisualizations.Profit)] = new FinancialAccountReportRow[] { Account("Revenue", 10m), Account("Expense", 15m) }
        });

        Assert.Contains("Value (Company base currency)", html);
        Assert.Contains("scope=\"row\">Net income</th><td>-5.00</td>", html);
    }

    [Fact]
    public async Task SalesOnlyDashboardFetchesAndDisplaysOnlyAuthorizedReportCategories()
    {
        using var handler = new ReadOnlyReportsHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://erp.example.test/") };
        var html = await RenderAsync<ReportDashboard>(new(), services =>
        {
            services.AddSingleton(http);
            services.AddSingleton<AuthenticationStateProvider>(new FixedAuthentication(User(ReportingPermissions.Sales)));
            services.AddSingleton<IOrganizationContextReader, FixedOrganization>();
        });

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, path => Assert.StartsWith("/api/v1/reports/sales/", path));
        Assert.Contains("Confirmed sales", html);
        Assert.Contains("Company performance", html);
        Assert.DoesNotContain("href=\"/reports/purchasing\"", html);
        Assert.DoesNotContain("href=\"/reports/cash\"", html);
        Assert.DoesNotContain("href=\"/reports/financial\"", html);
        Assert.DoesNotContain("Some report categories could not be loaded", html);
    }

    [Fact]
    public async Task DashboardWithoutReportPermissionsDoesNotFetchAnyBusinessData()
    {
        using var handler = new ReadOnlyReportsHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://erp.example.test/") };
        var html = await RenderAsync<ReportDashboard>(new(), services =>
        {
            services.AddSingleton(http);
            services.AddSingleton<AuthenticationStateProvider>(new FixedAuthentication(User(ReportingPermissions.Export)));
            services.AddSingleton<IOrganizationContextReader, FixedOrganization>();
        });

        Assert.Empty(handler.Requests);
        Assert.Contains("Reporting access is required", html);
        Assert.DoesNotContain("Confirmed sales", html);
    }

    private static FinancialAccountReportRow Account(string type, decimal balance) => new(Guid.NewGuid(), "ACCOUNT", type, type, 0, 0, balance);

    private static ClaimsPrincipal User(params string[] permissions) => new(new ClaimsIdentity(
        permissions.Select(permission => new Claim("permission", permission)).Append(new(ClaimTypes.NameIdentifier, "5ea64a9a-3a37-419b-946e-83d81615e405")), "Test"));

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters, Action<IServiceCollection>? configure = null) where T : IComponent
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<NavigationManager, TestNavigation>();
        configure?.Invoke(collection);
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://erp.example.test/", "https://erp.example.test/reports/dashboard");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Read-only dashboard rendering must not navigate.");
    }

    private sealed class FixedAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class FixedOrganization : IOrganizationContextReader
    {
        public Task<OrganizationContext?> GetAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult<OrganizationContext?>(
            new(new(Guid.NewGuid()), "Organization", new(Guid.NewGuid()), "Test company", "TEST", "USD", "UTC"));
    }

    private sealed class ReadOnlyReportsHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            Assert.StartsWith("/api/v1/reports/sales/", path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { GeneratedAtUtc = DateTimeOffset.UnixEpoch, From = (DateOnly?)null, To = (DateOnly?)null, AsOf = (DateOnly?)null, Data = Array.Empty<object>() })
            });
        }
    }
}
