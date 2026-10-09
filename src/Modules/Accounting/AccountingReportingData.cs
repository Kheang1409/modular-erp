using Erp.Modules.Accounting.Contracts;
using Microsoft.EntityFrameworkCore;

#pragma warning disable CA1725

namespace Erp.Modules.Accounting;

public sealed class AccountingReportingData(AccountingDbContext db) : IAccountingReportingData
{
    public async Task<IReadOnlyList<FinancialAccountReportRow>> TrialBalanceAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var rows = await db.JournalEntries.AsNoTracking().Where(x => x.State == JournalEntryState.Posted && x.BusinessDate <= asOf).SelectMany(x => x.Lines).GroupBy(x => new { x.AccountId, x.AccountCode, x.AccountName }).Select(x => new { x.Key.AccountId, x.Key.AccountCode, x.Key.AccountName, Debit = x.Sum(v => v.BaseDebit), Credit = x.Sum(v => v.BaseCredit) }).OrderBy(x => x.AccountCode).ToListAsync(cancellationToken);
        var types = await db.Accounts.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Type, cancellationToken);
        return rows.Select(x => new FinancialAccountReportRow(x.AccountId, x.AccountCode, x.AccountName, types.GetValueOrDefault(x.AccountId).ToString(), x.Debit, x.Credit, x.Debit - x.Credit)).ToList();
    }

    public async Task<GeneralLedgerReportPage> GeneralLedgerAsync(DateOnly from, DateOnly to, int page, int pageSize, CancellationToken cancellationToken)
    {
        ValidateRange(from, to); page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.JournalEntries.AsNoTracking()
            .Where(x => x.State == JournalEntryState.Posted && x.BusinessDate >= from && x.BusinessDate <= to)
            .SelectMany(entry => entry.Lines, (entry, line) => new
            {
                EntryId = entry.Id,
                EntryNumber = entry.Number,
                entry.BusinessDate,
                entry.Description,
                line.AccountId,
                line.AccountCode,
                line.AccountName,
                line.Debit,
                line.Credit,
                line.BaseDebit,
                line.BaseCredit,
                entry.CurrencyCode
            });
        var total = await query.CountAsync(cancellationToken);
        var data = await query.OrderBy(x => x.BusinessDate).ThenBy(x => x.EntryNumber).ThenBy(x => x.AccountCode)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = data.Select(x => new GeneralLedgerReportRow(x.EntryId, x.EntryNumber, x.BusinessDate, x.Description, x.AccountId, x.AccountCode, x.AccountName, x.Debit, x.Credit, x.BaseDebit, x.BaseCredit, x.CurrencyCode)).ToList();
        return new(page, pageSize, total, items);
    }

    public Task<IReadOnlyList<FinancialAccountReportRow>> ProfitAndLossAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) => Statement(from, to, [AccountType.Revenue, AccountType.Expense], cancellationToken);
    public Task<IReadOnlyList<FinancialAccountReportRow>> BalanceSheetAsync(DateOnly asOf, CancellationToken cancellationToken) => Statement(DateOnly.MinValue, asOf, [AccountType.Asset, AccountType.Liability, AccountType.Equity], cancellationToken, allowMinimumStart: true);

    public async Task<IReadOnlyList<AgingReportRow>> ReceivablesAgingAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var d30 = asOf.AddDays(-30); var d60 = asOf.AddDays(-60); var d90 = asOf.AddDays(-90);
        var rows = await db.CustomerInvoices.AsNoTracking().Where(x => x.InvoiceDate <= asOf && x.State != CustomerInvoiceState.Draft && x.State != CustomerInvoiceState.Cancelled && x.Total > x.AmountPaid).GroupBy(x => new { x.CustomerId, x.CustomerName, x.CurrencyCode }).Select(x => new { PartyId = x.Key.CustomerId, PartyName = x.Key.CustomerName, x.Key.CurrencyCode, Current = x.Sum(v => v.DueDate >= asOf ? v.Total - v.AmountPaid : 0), Days1To30 = x.Sum(v => v.DueDate < asOf && v.DueDate >= d30 ? v.Total - v.AmountPaid : 0), Days31To60 = x.Sum(v => v.DueDate < d30 && v.DueDate >= d60 ? v.Total - v.AmountPaid : 0), Days61To90 = x.Sum(v => v.DueDate < d60 && v.DueDate >= d90 ? v.Total - v.AmountPaid : 0), Over90 = x.Sum(v => v.DueDate < d90 ? v.Total - v.AmountPaid : 0), Total = x.Sum(v => v.Total - v.AmountPaid) }).OrderBy(x => x.PartyName).ToListAsync(cancellationToken);
        return rows.Select(x => new AgingReportRow(x.PartyId, x.PartyName, x.CurrencyCode, x.Current, x.Days1To30, x.Days31To60, x.Days61To90, x.Over90, x.Total)).ToList();
    }

    public async Task<IReadOnlyList<AgingReportRow>> PayablesAgingAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var d30 = asOf.AddDays(-30); var d60 = asOf.AddDays(-60); var d90 = asOf.AddDays(-90);
        var rows = await db.SupplierBills.AsNoTracking().Where(x => x.BillDate <= asOf && x.State != SupplierBillState.Draft && x.State != SupplierBillState.Cancelled && x.Total > x.AmountPaid).GroupBy(x => new { x.SupplierId, x.SupplierName, x.CurrencyCode }).Select(x => new { PartyId = x.Key.SupplierId, PartyName = x.Key.SupplierName, x.Key.CurrencyCode, Current = x.Sum(v => v.DueDate >= asOf ? v.Total - v.AmountPaid : 0), Days1To30 = x.Sum(v => v.DueDate < asOf && v.DueDate >= d30 ? v.Total - v.AmountPaid : 0), Days31To60 = x.Sum(v => v.DueDate < d30 && v.DueDate >= d60 ? v.Total - v.AmountPaid : 0), Days61To90 = x.Sum(v => v.DueDate < d60 && v.DueDate >= d90 ? v.Total - v.AmountPaid : 0), Over90 = x.Sum(v => v.DueDate < d90 ? v.Total - v.AmountPaid : 0), Total = x.Sum(v => v.Total - v.AmountPaid) }).OrderBy(x => x.PartyName).ToListAsync(cancellationToken);
        return rows.Select(x => new AgingReportRow(x.PartyId, x.PartyName, x.CurrencyCode, x.Current, x.Days1To30, x.Days31To60, x.Days61To90, x.Over90, x.Total)).ToList();
    }

    public async Task<IReadOnlyList<CashSummaryRow>> CashSummaryAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        ValidateRange(from, to); var rows = await db.Payments.AsNoTracking().Where(x => x.State == PaymentState.Posted && x.PaymentDate >= from && x.PaymentDate <= to).GroupBy(x => new { x.Direction, x.CurrencyCode }).Select(x => new { x.Key.Direction, x.Key.CurrencyCode, Amount = x.Sum(v => v.Amount), Count = x.Count() }).ToListAsync(cancellationToken); return rows.Select(x => new CashSummaryRow(x.Direction.ToString(), x.CurrencyCode, x.Amount, x.Count)).ToList();
    }

    private async Task<IReadOnlyList<FinancialAccountReportRow>> Statement(DateOnly from, DateOnly to, AccountType[] types, CancellationToken cancellationToken, bool allowMinimumStart = false)
    {
        ValidateRange(from, to, allowMinimumStart); var rows = await db.JournalEntries.AsNoTracking().Where(x => x.State == JournalEntryState.Posted && x.BusinessDate >= from && x.BusinessDate <= to).SelectMany(x => x.Lines).Join(db.Accounts.AsNoTracking().Where(x => types.Contains(x.Type)), line => line.AccountId, account => account.Id, (line, account) => new { Line = line, Account = account }).GroupBy(x => new { x.Account.Id, x.Account.Code, x.Account.Name, x.Account.Type }).Select(x => new { AccountId = x.Key.Id, x.Key.Code, x.Key.Name, x.Key.Type, Debit = x.Sum(v => v.Line.BaseDebit), Credit = x.Sum(v => v.Line.BaseCredit) }).OrderBy(x => x.Code).ToListAsync(cancellationToken);
        return rows.Select(x => new FinancialAccountReportRow(x.AccountId, x.Code, x.Name, x.Type.ToString(), x.Debit, x.Credit, x.Type is AccountType.Revenue or AccountType.Liability or AccountType.Equity ? x.Credit - x.Debit : x.Debit - x.Credit)).ToList();
    }
    private static void ValidateRange(DateOnly from, DateOnly to, bool allowMinimumStart = false) { if ((!allowMinimumStart && from == default) || to == default || from > to) throw new ArgumentException("A valid business-date range is required."); }
}
