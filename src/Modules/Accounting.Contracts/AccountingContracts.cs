namespace Erp.Modules.Accounting.Contracts;

public interface IAccountingPeriodStatus { Task<bool> IsOpenAsync(DateOnly businessDate, CancellationToken cancellationToken); }

public sealed record FinancialAccountReportRow(Guid AccountId, string Code, string Name, string Type, decimal Debit, decimal Credit, decimal Balance);
public sealed record GeneralLedgerReportRow(Guid EntryId, string EntryNumber, DateOnly BusinessDate, string Description, Guid AccountId, string AccountCode, string AccountName, decimal Debit, decimal Credit, decimal BaseDebit, decimal BaseCredit, string CurrencyCode);
public sealed record GeneralLedgerReportPage(int Page, int PageSize, int Total, IReadOnlyList<GeneralLedgerReportRow> Items);
public sealed record AgingReportRow(Guid PartyId, string PartyName, string CurrencyCode, decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total);
public sealed record CashSummaryRow(string Direction, string CurrencyCode, decimal Amount, int Count);
public interface IAccountingReportingData
{
    Task<IReadOnlyList<FinancialAccountReportRow>> TrialBalanceAsync(DateOnly asOf, CancellationToken cancellationToken);
    Task<GeneralLedgerReportPage> GeneralLedgerAsync(DateOnly startDate, DateOnly endDate, int page, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialAccountReportRow>> ProfitAndLossAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialAccountReportRow>> BalanceSheetAsync(DateOnly asOf, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgingReportRow>> ReceivablesAgingAsync(DateOnly asOf, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgingReportRow>> PayablesAgingAsync(DateOnly asOf, CancellationToken cancellationToken);
    Task<IReadOnlyList<CashSummaryRow>> CashSummaryAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);
}
