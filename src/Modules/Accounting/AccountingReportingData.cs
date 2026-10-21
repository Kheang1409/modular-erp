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
                line.Sequence,
                LineId = line.Id,
                entry.CurrencyCode
            });
        var total = await query.CountAsync(cancellationToken);
        var offset = ((long)page - 1) * pageSize;
        if (offset >= total) return new(page, pageSize, total, []);
        var data = await query.OrderBy(x => x.BusinessDate).ThenBy(x => x.EntryNumber).ThenBy(x => x.AccountCode)
            .ThenBy(x => x.Sequence).ThenBy(x => x.LineId)
            .Skip((int)offset).Take(pageSize).ToListAsync(cancellationToken);
        var items = data.Select(x => new GeneralLedgerReportRow(x.EntryId, x.EntryNumber, x.BusinessDate, x.Description, x.AccountId, x.AccountCode, x.AccountName, x.Debit, x.Credit, x.BaseDebit, x.BaseCredit, x.CurrencyCode)).ToList();
        return new(page, pageSize, total, items);
    }

    public Task<IReadOnlyList<FinancialAccountReportRow>> ProfitAndLossAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) => Statement(from, to, [AccountType.Revenue, AccountType.Expense], cancellationToken);
    public Task<IReadOnlyList<FinancialAccountReportRow>> BalanceSheetAsync(DateOnly asOf, CancellationToken cancellationToken) => Statement(DateOnly.MinValue, asOf, [AccountType.Asset, AccountType.Liability, AccountType.Equity], cancellationToken, allowMinimumStart: true);

    public Task<IReadOnlyList<AgingReportRow>> ReceivablesAgingAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var amounts = AllocatedAmounts(asOf, PaymentDirection.CustomerReceipt);
        var documents = from invoice in db.CustomerInvoices.AsNoTracking()
                        where invoice.InvoiceDate <= asOf && invoice.State != CustomerInvoiceState.Draft && invoice.State != CustomerInvoiceState.Cancelled
                        join paid in amounts on invoice.Id equals paid.DocumentId into payments
                        from paid in payments.DefaultIfEmpty()
                        select new AgingDocument
                        {
                            PartyId = invoice.CustomerId,
                            PartyName = invoice.CustomerName,
                            CurrencyCode = invoice.CurrencyCode,
                            DueDate = invoice.DueDate,
                            OpenAmount = invoice.Total - ((decimal?)paid.Amount ?? 0)
                        };
        return Aging(documents, asOf, cancellationToken);
    }

    public Task<IReadOnlyList<AgingReportRow>> PayablesAgingAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var amounts = AllocatedAmounts(asOf, PaymentDirection.SupplierPayment);
        var documents = from bill in db.SupplierBills.AsNoTracking()
                        where bill.BillDate <= asOf && bill.State != SupplierBillState.Draft && bill.State != SupplierBillState.Cancelled
                        join paid in amounts on bill.Id equals paid.DocumentId into payments
                        from paid in payments.DefaultIfEmpty()
                        select new AgingDocument
                        {
                            PartyId = bill.SupplierId,
                            PartyName = bill.SupplierName,
                            CurrencyCode = bill.CurrencyCode,
                            DueDate = bill.DueDate,
                            OpenAmount = bill.Total - ((decimal?)paid.Amount ?? 0)
                        };
        return Aging(documents, asOf, cancellationToken);
    }

    private IQueryable<AllocatedAmount> AllocatedAmounts(DateOnly asOf, PaymentDirection direction)
    {
        var allocations = from allocation in db.PaymentAllocations.AsNoTracking()
                          join payment in db.Payments.AsNoTracking() on allocation.PaymentId equals payment.Id
                          where payment.Direction == direction && (payment.State == PaymentState.Posted || payment.State == PaymentState.Reversed)
                          select new
                          {
                              DocumentId = direction == PaymentDirection.CustomerReceipt ? allocation.CustomerInvoiceId : allocation.SupplierBillId,
                              allocation.Amount,
                              payment.PaymentDate,
                              payment.State,
                              payment.ReversalJournalEntryId
                          };
        var paid = allocations.Where(x => x.DocumentId != null && x.PaymentDate <= asOf)
            .Select(x => new AllocatedAmount { DocumentId = x.DocumentId!.Value, Amount = x.Amount });
        // Original and compensating effects have independent business dates. Do not use
        // today's AmountPaid cache or drop a reversed payment's earlier allocation.
        var reversed = from allocation in allocations
                       join entry in db.JournalEntries.AsNoTracking() on allocation.ReversalJournalEntryId equals (Guid?)entry.Id
                       where allocation.DocumentId != null && allocation.State == PaymentState.Reversed
                           && entry.State == JournalEntryState.Posted && entry.BusinessDate <= asOf
                       select new AllocatedAmount { DocumentId = allocation.DocumentId!.Value, Amount = -allocation.Amount };
        return paid.Concat(reversed).GroupBy(x => x.DocumentId)
            .Select(x => new AllocatedAmount { DocumentId = x.Key, Amount = x.Sum(v => v.Amount) });
    }

    private static async Task<IReadOnlyList<AgingReportRow>> Aging(IQueryable<AgingDocument> documents, DateOnly asOf, CancellationToken cancellationToken)
    {
        if (asOf == default) throw new ArgumentException("A valid as-of business date is required.", nameof(asOf));
        var d30 = DateOnly.FromDayNumber(Math.Max(0, asOf.DayNumber - 30));
        var d60 = DateOnly.FromDayNumber(Math.Max(0, asOf.DayNumber - 60));
        var d90 = DateOnly.FromDayNumber(Math.Max(0, asOf.DayNumber - 90));
        var rows = await documents.Where(x => x.OpenAmount > 0)
            .GroupBy(x => new { x.PartyId, x.PartyName, x.CurrencyCode })
            .Select(x => new
            {
                x.Key.PartyId,
                x.Key.PartyName,
                x.Key.CurrencyCode,
                Current = x.Sum(v => v.DueDate >= asOf ? v.OpenAmount : 0),
                Days1To30 = x.Sum(v => v.DueDate < asOf && v.DueDate >= d30 ? v.OpenAmount : 0),
                Days31To60 = x.Sum(v => v.DueDate < d30 && v.DueDate >= d60 ? v.OpenAmount : 0),
                Days61To90 = x.Sum(v => v.DueDate < d60 && v.DueDate >= d90 ? v.OpenAmount : 0),
                Over90 = x.Sum(v => v.DueDate < d90 ? v.OpenAmount : 0),
                Total = x.Sum(v => v.OpenAmount)
            })
            .OrderBy(x => x.PartyName).ThenBy(x => x.PartyId).ThenBy(x => x.CurrencyCode)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new AgingReportRow(x.PartyId, x.PartyName, x.CurrencyCode,
            x.Current, x.Days1To30, x.Days31To60, x.Days61To90, x.Over90, x.Total)).ToList();
    }

    private sealed class AllocatedAmount
    {
        public Guid DocumentId { get; init; }
        public decimal Amount { get; init; }
    }

    private sealed class AgingDocument
    {
        public Guid PartyId { get; init; }
        public required string PartyName { get; init; }
        public required string CurrencyCode { get; init; }
        public DateOnly DueDate { get; init; }
        public decimal OpenAmount { get; init; }
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
