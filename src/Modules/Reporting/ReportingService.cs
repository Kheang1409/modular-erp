using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Contracts;
using Erp.Modules.Sales.Contracts;

#pragma warning disable CA1716, CA1725

namespace Erp.Modules.Reporting;

public sealed record ReportEnvelope<T>(DateTimeOffset GeneratedAtUtc, DateOnly? From, DateOnly? To, DateOnly? AsOf, T Data);
public interface IReportingService
{
    Task<ReportEnvelope<IReadOnlyList<SalesSummaryReportRow>>> SalesSummary(DateOnly from, DateOnly to, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<SalesByCustomerReportRow>>> SalesByCustomer(DateOnly from, DateOnly to, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<SalesByProductReportRow>>> SalesByProduct(DateOnly from, DateOnly to, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<PurchaseSummaryReportRow>>> PurchaseSummary(DateOnly from, DateOnly to, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<InventoryOnHandReportRow>>> InventoryOnHand(DateOnly asOf, CancellationToken token);
    Task<ReportEnvelope<InventoryMovementReportPage>> InventoryMovements(DateOnly from, DateOnly to, int page, int pageSize, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<FinancialAccountReportRow>>> TrialBalance(DateOnly asOf, CancellationToken token);
    Task<ReportEnvelope<GeneralLedgerReportPage>> GeneralLedger(DateOnly from, DateOnly to, int page, int pageSize, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<FinancialAccountReportRow>>> ProfitAndLoss(DateOnly from, DateOnly to, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<FinancialAccountReportRow>>> BalanceSheet(DateOnly asOf, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<AgingReportRow>>> ReceivablesAging(DateOnly asOf, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<AgingReportRow>>> PayablesAging(DateOnly asOf, CancellationToken token);
    Task<ReportEnvelope<IReadOnlyList<CashSummaryRow>>> CashSummary(DateOnly from, DateOnly to, CancellationToken token);
}
public sealed class ReportingService(ISalesReportingData sales, IPurchasingReportingData purchasing, IInventoryReportingData inventory, IAccountingReportingData accounting, TimeProvider time) : IReportingService
{
    private DateTimeOffset Now => time.GetUtcNow();
    public async Task<ReportEnvelope<IReadOnlyList<SalesSummaryReportRow>>> SalesSummary(DateOnly from, DateOnly to, CancellationToken token) => new(Now, from, to, null, await sales.SummaryAsync(from, to, token));
    public async Task<ReportEnvelope<IReadOnlyList<SalesByCustomerReportRow>>> SalesByCustomer(DateOnly from, DateOnly to, CancellationToken token) => new(Now, from, to, null, await sales.ByCustomerAsync(from, to, token));
    public async Task<ReportEnvelope<IReadOnlyList<SalesByProductReportRow>>> SalesByProduct(DateOnly from, DateOnly to, CancellationToken token) => new(Now, from, to, null, await sales.ByProductAsync(from, to, token));
    public async Task<ReportEnvelope<IReadOnlyList<PurchaseSummaryReportRow>>> PurchaseSummary(DateOnly from, DateOnly to, CancellationToken token) => new(Now, from, to, null, await purchasing.SummaryAsync(from, to, token));
    public async Task<ReportEnvelope<IReadOnlyList<InventoryOnHandReportRow>>> InventoryOnHand(DateOnly asOf, CancellationToken token) => new(Now, null, null, asOf, await inventory.OnHandAsync(asOf, token));
    public async Task<ReportEnvelope<InventoryMovementReportPage>> InventoryMovements(DateOnly from, DateOnly to, int page, int pageSize, CancellationToken token) => new(Now, from, to, null, await inventory.MovementsAsync(from, to, page, pageSize, token));
    public async Task<ReportEnvelope<IReadOnlyList<FinancialAccountReportRow>>> TrialBalance(DateOnly asOf, CancellationToken token) => new(Now, null, null, asOf, await accounting.TrialBalanceAsync(asOf, token));
    public async Task<ReportEnvelope<GeneralLedgerReportPage>> GeneralLedger(DateOnly from, DateOnly to, int page, int pageSize, CancellationToken token) => new(Now, from, to, null, await accounting.GeneralLedgerAsync(from, to, page, pageSize, token));
    public async Task<ReportEnvelope<IReadOnlyList<FinancialAccountReportRow>>> ProfitAndLoss(DateOnly from, DateOnly to, CancellationToken token) => new(Now, from, to, null, await accounting.ProfitAndLossAsync(from, to, token));
    public async Task<ReportEnvelope<IReadOnlyList<FinancialAccountReportRow>>> BalanceSheet(DateOnly asOf, CancellationToken token) => new(Now, null, null, asOf, await accounting.BalanceSheetAsync(asOf, token));
    public async Task<ReportEnvelope<IReadOnlyList<AgingReportRow>>> ReceivablesAging(DateOnly asOf, CancellationToken token) => new(Now, null, null, asOf, await accounting.ReceivablesAgingAsync(asOf, token));
    public async Task<ReportEnvelope<IReadOnlyList<AgingReportRow>>> PayablesAging(DateOnly asOf, CancellationToken token) => new(Now, null, null, asOf, await accounting.PayablesAgingAsync(asOf, token));
    public async Task<ReportEnvelope<IReadOnlyList<CashSummaryRow>>> CashSummary(DateOnly from, DateOnly to, CancellationToken token) => new(Now, from, to, null, await accounting.CashSummaryAsync(from, to, token));
}
