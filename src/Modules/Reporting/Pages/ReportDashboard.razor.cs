using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Context;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;

namespace Erp.Modules.Reporting.Pages;

public partial class ReportDashboard
{
    [Inject] private HttpClient Http { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private IOrganizationContextReader OrganizationReader { get; set; } = null!;
    private OrganizationContext? _organization;
    private readonly DashboardFilter _filter = new();
    private HashSet<string> _permissions = [];
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private IReadOnlyList<SalesSummaryReportRow> _sales = [];
    private IReadOnlyList<SalesByCustomerReportRow> _customers = [];
    private IReadOnlyList<SalesByProductReportRow> _products = [];
    private IReadOnlyList<PurchaseSummaryReportRow> _purchases = [];
    private IReadOnlyList<CashSummaryRow> _cash = [];
    private IReadOnlyList<AgingReportRow> _receivables = [], _payables = [];
    private IReadOnlyList<FinancialAccountReportRow> _financial = [];
    private IReadOnlyList<InventoryOnHandReportRow> _inventory = [];
    private List<string> _currencies = ["USD"];
    private string _currency = "USD";
    private string? _validationError;
    private bool _loading = true, _hasReports;
    private DateOnly _appliedFrom, _appliedTo;
    private DateTimeOffset _loadedAt;
    private string BaseCurrency => _organization?.BaseCurrencyCode ?? "base currency";
    private bool Allowed(string permission) => _permissions.Contains(permission);
    private bool Failed(string permission) => _errors.ContainsKey(permission);
    private bool Ready(string permission) => Allowed(permission) && !Failed(permission);
    private decimal SalesTotal => _sales.Where(row => DashboardCalculations.SameCurrency(row.CurrencyCode, _currency)).Sum(row => row.Total);
    private int SalesOrders => _sales.Where(row => DashboardCalculations.SameCurrency(row.CurrencyCode, _currency)).Sum(row => row.OrderCount);
    private decimal PurchaseTotal => _purchases.Where(row => DashboardCalculations.SameCurrency(row.CurrencyCode, _currency)).Sum(row => row.Total);
    private int PurchaseOrders => _purchases.Where(row => DashboardCalculations.SameCurrency(row.CurrencyCode, _currency)).Sum(row => row.OrderCount);
    private decimal CashReceived => _cash.Where(row => row.Direction == "CustomerReceipt" && DashboardCalculations.SameCurrency(row.CurrencyCode, _currency)).Sum(row => row.Amount);
    private decimal CashPaid => _cash.Where(row => row.Direction == "SupplierPayment" && DashboardCalculations.SameCurrency(row.CurrencyCode, _currency)).Sum(row => row.Amount);
    private decimal OverdueReceivables => _receivables.Where(row => DashboardCalculations.SameCurrency(row.CurrencyCode, _currency)).Sum(row => row.Total - row.Current);
    private decimal OpenPayables => _payables.Where(row => DashboardCalculations.SameCurrency(row.CurrencyCode, _currency)).Sum(row => row.Total);
    private (decimal Revenue, decimal Expenses, decimal NetIncome) Profit => DashboardCalculations.ProfitAndLoss(_financial);

    private IReadOnlyList<DashboardChartPoint> TradingPoints
    {
        get
        {
            var points = new List<DashboardChartPoint>();
            if (Ready(ReportingPermissions.Sales) && _sales.Any(row => DashboardCalculations.SameCurrency(row.CurrencyCode, _currency))) points.Add(new("Sales", SalesTotal));
            if (Ready(ReportingPermissions.Purchasing) && _purchases.Any(row => DashboardCalculations.SameCurrency(row.CurrencyCode, _currency))) points.Add(new("Purchases", PurchaseTotal, "teal"));
            return points;
        }
    }
    private IReadOnlyList<DashboardChartPoint> CashPoints => _cash.Where(row => DashboardCalculations.SameCurrency(row.CurrencyCode, _currency))
        .Select(row => new DashboardChartPoint(row.Direction == "CustomerReceipt" ? "Customer receipts" : "Supplier payments", row.Amount, row.Direction == "CustomerReceipt" ? "teal" : "rose")).ToList();
    private IReadOnlyList<DashboardChartPoint> FinancialPoints => _financial.Count == 0 ? [] :
        [new("Revenue", Profit.Revenue), new("Expenses", Profit.Expenses, "amber"), new("Net income", Profit.NetIncome, "teal")];

    protected override async Task OnInitializedAsync()
    {
        var user = (await Authentication.GetAuthenticationStateAsync()).User;
        _permissions = user.FindAll("permission").Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal);
        _hasReports = ReportingPermissions.CanViewDashboard(user);
        if (!_hasReports) { _loading = false; return; }
        if (Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            _organization = await OrganizationReader.GetAsync(new UserId(userId), CancellationToken.None);
        _currency = _organization?.BaseCurrencyCode ?? "USD";
        _currencies = [_currency];
        var today = CompanyToday();
        _filter.From = new(today.Year, 1, 1);
        _filter.To = today;
        await Load();
    }

    private DateOnly CompanyToday()
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(_organization?.TimeZoneId ?? "UTC")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(DateTime.UtcNow); }
        catch (InvalidTimeZoneException) { return DateOnly.FromDateTime(DateTime.UtcNow); }
    }

    private async Task ApplyFilters(EditContext form)
    {
        if (_loading) return;
        if (!form.Validate()) { _validationError = "Choose valid From and To dates."; return; }
        _validationError = _filter.From == default || _filter.To == default ? "Choose valid From and To dates." :
            _filter.From > _filter.To ? "From date must be on or before To date." : null;
        if (_validationError is not null) return;
        await Load();
    }

    private async Task Load()
    {
        _loading = true;
        _errors.Clear();
        _appliedFrom = _filter.From;
        _appliedTo = _filter.To;
        var range = $"from={_appliedFrom:yyyy-MM-dd}&to={_appliedTo:yyyy-MM-dd}";
        var asOf = $"asOf={_appliedTo:yyyy-MM-dd}";
        // Serial requests avoid concurrent operations against scoped module DbContexts.
        await LoadCategory(ReportingPermissions.Sales, "Sales reports could not be loaded.", async () =>
        {
            _sales = await Fetch<SalesSummaryReportRow>($"sales/summary?{range}");
            _customers = await Fetch<SalesByCustomerReportRow>($"sales/by-customer?{range}");
            _products = await Fetch<SalesByProductReportRow>($"sales/by-product?{range}");
        });
        await LoadCategory(ReportingPermissions.Purchasing, "Purchasing reports could not be loaded.", async () => _purchases = await Fetch<PurchaseSummaryReportRow>($"purchases/summary?{range}"));
        await LoadCategory(ReportingPermissions.Cash, "Cash and payment reports could not be loaded.", async () => _cash = await Fetch<CashSummaryRow>($"cash/summary?{range}"));
        await LoadCategory(ReportingPermissions.Receivables, "Receivables aging could not be loaded.", async () => _receivables = await Fetch<AgingReportRow>($"aging/receivables?{asOf}"));
        await LoadCategory(ReportingPermissions.Payables, "Payables aging could not be loaded.", async () => _payables = await Fetch<AgingReportRow>($"aging/payables?{asOf}"));
        await LoadCategory(ReportingPermissions.Financial, "Financial performance could not be loaded.", async () => _financial = await Fetch<FinancialAccountReportRow>($"financial/profit-and-loss?{range}"));
        await LoadCategory(ReportingPermissions.Inventory, "Inventory reports could not be loaded.", async () => _inventory = await Fetch<InventoryOnHandReportRow>($"inventory/on-hand?{asOf}"));
        _currencies = SuccessfulCurrencies().Append(_organization?.BaseCurrencyCode ?? "USD").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(currency => currency, StringComparer.Ordinal).ToList();
        if (!_currencies.Contains(_currency, StringComparer.OrdinalIgnoreCase)) _currency = _organization?.BaseCurrencyCode ?? _currencies[0];
        _loadedAt = DateTimeOffset.UtcNow;
        _loading = false;
    }

    private async Task<IReadOnlyList<T>> Fetch<T>(string path) =>
        (await Http.GetFromJsonAsync<ReportEnvelope<List<T>>>($"api/v1/reports/{path}"))?.Data ?? throw new HttpRequestException("The report response was empty.");

    private async Task LoadCategory(string permission, string errorMessage, Func<Task> fetch)
    {
        if (!Allowed(permission)) return;
        try { await fetch(); }
        catch (HttpRequestException) { _errors[permission] = errorMessage; }
        catch (JsonException) { _errors[permission] = errorMessage; }
        catch (TaskCanceledException) { _errors[permission] = errorMessage; }
    }

    private IEnumerable<string> SuccessfulCurrencies()
    {
        if (Ready(ReportingPermissions.Sales)) foreach (var row in _sales) yield return row.CurrencyCode;
        if (Ready(ReportingPermissions.Purchasing)) foreach (var row in _purchases) yield return row.CurrencyCode;
        if (Ready(ReportingPermissions.Cash)) foreach (var row in _cash) yield return row.CurrencyCode;
        if (Ready(ReportingPermissions.Receivables)) foreach (var row in _receivables) yield return row.CurrencyCode;
        if (Ready(ReportingPermissions.Payables)) foreach (var row in _payables) yield return row.CurrencyCode;
    }

    private sealed class DashboardFilter
    {
        public DateOnly From { get; set; }
        public DateOnly To { get; set; }
    }
}
