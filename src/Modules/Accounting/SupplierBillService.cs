using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.Abstractions.Persistence;
using Erp.Modules.Purchasing.Contracts;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting;

public interface ISupplierBillService
{
    Task<IReadOnlyList<SupplierBillDto>> ListAsync(CancellationToken cancellationToken);
    Task<SupplierBillDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<SupplierBillDto> CreateFromPurchaseOrderAsync(CreateSupplierBillRequest request, CancellationToken cancellationToken);
    Task<SupplierBillDto> PostAsync(Guid id, PostSupplierBillRequest request, CancellationToken cancellationToken);
    Task<SupplierBillDto> CancelAsync(Guid id, AccountingActionRequest request, CancellationToken cancellationToken);
}

public sealed class SupplierBillService(AccountingDbContext db, IPurchaseOrderDirectory purchaseOrders, ITenantContext tenant,
    ICompanyContext company, IClock clock, IAuditLogWriter audit, IOutboxWriter outbox,
    IBusinessTransactionCoordinator transactions) : ISupplierBillService
{
    public async Task<IReadOnlyList<SupplierBillDto>> ListAsync(CancellationToken cancellationToken) => (await db.SupplierBills.AsNoTracking().Include(x => x.Lines).OrderByDescending(x => x.BillDate).ThenByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken)).Select(Dto).ToList();
    public async Task<SupplierBillDto> GetAsync(Guid id, CancellationToken cancellationToken) => Dto(await Find(id, cancellationToken));

    public async Task<SupplierBillDto> CreateFromPurchaseOrderAsync(CreateSupplierBillRequest request, CancellationToken cancellationToken)
    {
        var key = RequiredKey(request.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(request.SupplierReference) || request.SupplierReference.Trim().Length > 100) throw Error("supplierReference", "Supplier invoice reference is required and limited to 100 characters.");
        await using var transaction = await transactions.BeginAsync(db, cancellationToken);
        var existing = await db.SupplierBills.Include(x => x.Lines).SingleOrDefaultAsync(x => x.IdempotencyKey == key || x.PurchaseOrderId == request.PurchaseOrderId, cancellationToken);
        if (existing is not null)
        {
            var replay = Dto(existing);
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }
        if (request.BillDate == default || request.DueDate < request.BillDate) throw Error("dueDate", "Due date must be on or after the bill date.");
        var order = await purchaseOrders.FindAsync(request.PurchaseOrderId, cancellationToken) ?? throw Error("purchaseOrderId", "Purchase order was not found.");
        if (order.State is not ("Confirmed" or "PartiallyReceived" or "Received")) throw Error("purchaseOrderId", "Only confirmed or received purchase orders can be billed.");
        if (order.Lines.Count == 0) throw Error("purchaseOrderId", "The purchase order has no billable lines.");
        var now = clock.UtcNow; var id = Guid.NewGuid();
        var bill = new SupplierBill { Id = id, TenantId = Tenant(), CompanyId = Company(), Number = $"BILL-{now:yyyyMMdd}-{id.ToString("N")[..8].ToUpperInvariant()}", SupplierReference = request.SupplierReference.Trim(), PurchaseOrderId = order.Id, PurchaseOrderNumber = order.Number, SupplierId = order.SupplierId, SupplierName = order.SupplierName, SupplierTaxNumber = order.SupplierTaxNumber, BillDate = request.BillDate, DueDate = request.DueDate, CurrencyCode = order.CurrencyCode, BaseCurrencyCode = order.BaseCurrencyCode, ExchangeRate = order.ExchangeRate, Subtotal = order.Subtotal, DiscountTotal = order.DiscountTotal, TaxTotal = order.TaxTotal, Total = order.Total, AmountPaid = 0, State = SupplierBillState.Draft, IdempotencyKey = key, CreatedAtUtc = now, UpdatedAtUtc = now, ConcurrencyStamp = Stamp() };
        var sequence = 0;
        foreach (var line in order.Lines) bill.Lines.Add(new SupplierBillLine { Id = Guid.NewGuid(), TenantId = bill.TenantId, CompanyId = bill.CompanyId, SupplierBillId = id, Sequence = ++sequence, ProductId = line.ProductId, ProductSku = line.ProductSku, Description = line.Description, UnitCode = line.UnitCode, Quantity = line.Quantity, UnitPrice = line.UnitPrice, DiscountPercent = line.DiscountPercent, TaxPercent = line.TaxPercent, Subtotal = line.Subtotal, DiscountAmount = line.DiscountAmount, TaxAmount = line.TaxAmount, Total = line.Total });
        db.SupplierBills.Add(bill); await Save(cancellationToken); await Audit("accounting.bill.created", bill, cancellationToken);
        var result = Dto(bill);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<SupplierBillDto> PostAsync(Guid id, PostSupplierBillRequest request, CancellationToken cancellationToken)
    {
        var key = RequiredKey(request.IdempotencyKey);
        await using var transaction = await transactions.BeginAsync(db, cancellationToken);
        var bill = await Find(id, cancellationToken);
        if (bill.State != SupplierBillState.Draft)
        {
            if (bill.State is SupplierBillState.Posted or SupplierBillState.PartiallyPaid or SupplierBillState.Paid)
            {
                var replay = Dto(bill);
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }
            throw Error("state", "Only draft bills can be posted.");
        }
        Check(bill.ConcurrencyStamp, request.ConcurrencyStamp);
        if (!await db.FiscalPeriods.AsNoTracking().AnyAsync(x => x.State == FiscalPeriodState.Open && x.StartsOn <= bill.BillDate && x.EndsOn >= bill.BillDate, cancellationToken)) throw Error("billDate", "Bill date must be in an open fiscal period.");
        var journal = await db.Journals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.JournalId && x.IsActive, cancellationToken) ?? throw Error("journalId", "An active journal is required.");
        var ids = new[] { request.PayableAccountId, request.ExpenseAccountId, request.TaxRecoverableAccountId ?? Guid.Empty }.Where(x => x != Guid.Empty).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (accounts.Count != ids.Length || accounts.Values.Any(x => !x.IsActive || !x.AllowDirectPosting)) throw Error("accounts", "Posting accounts must be active direct-posting accounts.");
        if (bill.TaxTotal > 0 && request.TaxRecoverableAccountId is null) throw Error("taxRecoverableAccountId", "A recoverable-tax account is required when the bill includes tax.");
        var netExpense = Money(bill.Subtotal - bill.DiscountTotal); if (Money(netExpense + bill.TaxTotal) != Money(bill.Total)) throw Error("total", "Bill totals are inconsistent and cannot be posted.");
        var now = clock.UtcNow; var entryId = Guid.NewGuid();
        var entry = new JournalEntry { Id = entryId, TenantId = bill.TenantId, CompanyId = bill.CompanyId, Number = $"JE-{now:yyyyMMdd}-{entryId.ToString("N")[..8].ToUpperInvariant()}", JournalId = journal.Id, BusinessDate = bill.BillDate, Description = $"Supplier bill {bill.Number}", CurrencyCode = bill.CurrencyCode, ExchangeRate = bill.ExchangeRate, State = JournalEntryState.Posted, IdempotencyKey = $"bill:{bill.Id}", PostingKey = key, CreatedAtUtc = now, PostedAtUtc = now, ConcurrencyStamp = Stamp() };
        AddLine(entry, 1, accounts[request.ExpenseAccountId], $"Expense for {bill.Number}", netExpense, 0);
        if (bill.TaxTotal > 0) AddLine(entry, 2, accounts[request.TaxRecoverableAccountId!.Value], $"Tax for {bill.Number}", bill.TaxTotal, 0);
        AddLine(entry, entry.Lines.Count + 1, accounts[request.PayableAccountId], bill.SupplierName, 0, bill.Total);
        GeneralLedgerService.ValidateBalance(entry.Lines.Select(x => (x.Debit, x.Credit)));
        db.JournalEntries.Add(entry); bill.JournalEntryId = entry.Id; bill.State = SupplierBillState.Posted; bill.UpdatedAtUtc = now; bill.ConcurrencyStamp = Stamp(); await Save(cancellationToken); outbox.Add(new SupplierBillPostedEvent(Guid.NewGuid(), now, bill.Id, bill.Number, bill.SupplierId, bill.CurrencyCode, bill.Total)); outbox.Add(new JournalEntryPostedEvent(Guid.NewGuid(), now, entry.Id, entry.Number, entry.BusinessDate, entry.CurrencyCode)); await Audit("accounting.bill.posted", bill, cancellationToken);
        var result = Dto(bill);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<SupplierBillDto> CancelAsync(Guid id, AccountingActionRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(db, cancellationToken);
        var bill = await Find(id, cancellationToken); Check(bill.ConcurrencyStamp, request.ConcurrencyStamp); if (bill.State != SupplierBillState.Draft) throw Error("state", "Only draft bills can be cancelled; posted bills require a credit or reversal."); bill.State = SupplierBillState.Cancelled; bill.UpdatedAtUtc = clock.UtcNow; bill.ConcurrencyStamp = Stamp(); await Save(cancellationToken); await Audit("accounting.bill.cancelled", bill, cancellationToken);
        var result = Dto(bill);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
    private static void AddLine(JournalEntry entry, int sequence, Account account, string description, decimal debit, decimal credit) => entry.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = entry.TenantId, CompanyId = entry.CompanyId, JournalEntryId = entry.Id, Sequence = sequence, AccountId = account.Id, AccountCode = account.Code, AccountName = account.Name, Description = description, Debit = Money(debit), Credit = Money(credit), BaseDebit = Money(debit * entry.ExchangeRate), BaseCredit = Money(credit * entry.ExchangeRate) });
    private async Task<SupplierBill> Find(Guid id, CancellationToken cancellationToken) => await db.SupplierBills.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new KeyNotFoundException();
    private async Task Save(CancellationToken cancellationToken) { try { await db.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { throw new AccountingConcurrencyException(); } catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true) { throw Error("idempotencyKey", "This purchase order, supplier reference, or operation was already billed."); } }
    private async Task Audit(string action, SupplierBill bill, CancellationToken cancellationToken) { audit.Append(new(action, nameof(SupplierBill), bill.Id.ToString(), null, null, JsonSerializer.Serialize(Dto(bill)))); await audit.FlushAsync(cancellationToken); }
    private Guid Tenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); private Guid Company() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private static decimal Money(decimal value) => decimal.Round(value, 4, MidpointRounding.ToEven); private static string Stamp() => Guid.NewGuid().ToString("N"); private static string RequiredKey(string? value) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100) throw Error("idempotencyKey", "A unique operation key up to 100 characters is required."); return value.Trim(); }
    private static void Check(string actual, string supplied) { if (!StringComparer.Ordinal.Equals(actual, supplied)) throw new AccountingConcurrencyException(); }
    private static AccountingValidationException Error(string key, string message) => new(new Dictionary<string, string[]> { { key, [message] } });
    private static SupplierBillDto Dto(SupplierBill bill) => new(bill.Id, bill.Number, bill.SupplierReference, bill.PurchaseOrderId, bill.PurchaseOrderNumber, bill.SupplierId, bill.SupplierName, bill.SupplierTaxNumber, bill.BillDate, bill.DueDate, bill.CurrencyCode, bill.BaseCurrencyCode, bill.ExchangeRate, bill.Subtotal, bill.DiscountTotal, bill.TaxTotal, bill.Total, bill.AmountPaid, Money(bill.Total - bill.AmountPaid), bill.State, bill.JournalEntryId, bill.CreatedAtUtc, bill.UpdatedAtUtc, bill.ConcurrencyStamp, bill.Lines.OrderBy(x => x.Sequence).Select(x => new SupplierBillLineDto(x.Id, x.Sequence, x.ProductId, x.ProductSku, x.Description, x.UnitCode, x.Quantity, x.UnitPrice, x.DiscountPercent, x.TaxPercent, x.Subtotal, x.DiscountAmount, x.TaxAmount, x.Total)).ToList());
}
