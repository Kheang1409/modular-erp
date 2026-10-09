using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting;

public interface ICustomerInvoiceService
{
    Task<IReadOnlyList<CustomerInvoiceDto>> ListAsync(CancellationToken cancellationToken);
    Task<CustomerInvoiceDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<CustomerInvoiceDto> CreateFromSalesOrderAsync(CreateCustomerInvoiceRequest request, CancellationToken cancellationToken);
    Task<CustomerInvoiceDto> PostAsync(Guid id, PostCustomerInvoiceRequest request, CancellationToken cancellationToken);
    Task<CustomerInvoiceDto> CancelAsync(Guid id, AccountingActionRequest request, CancellationToken cancellationToken);
}

public sealed class CustomerInvoiceService(AccountingDbContext db, ISalesOrderDirectory salesOrders, ITenantContext tenant, ICompanyContext company, IClock clock, IAuditLogWriter audit, IOutboxWriter outbox) : ICustomerInvoiceService
{
    public async Task<IReadOnlyList<CustomerInvoiceDto>> ListAsync(CancellationToken cancellationToken) =>
        (await db.CustomerInvoices.AsNoTracking().Include(x => x.Lines).OrderByDescending(x => x.InvoiceDate).ThenByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken)).Select(Dto).ToList();

    public async Task<CustomerInvoiceDto> GetAsync(Guid id, CancellationToken cancellationToken) => Dto(await Find(id, cancellationToken));

    public async Task<CustomerInvoiceDto> CreateFromSalesOrderAsync(CreateCustomerInvoiceRequest request, CancellationToken cancellationToken)
    {
        var key = RequiredKey(request.IdempotencyKey);
        var existing = await db.CustomerInvoices.Include(x => x.Lines).SingleOrDefaultAsync(x => x.IdempotencyKey == key || x.SalesOrderId == request.SalesOrderId, cancellationToken);
        if (existing is not null) return Dto(existing);
        if (request.InvoiceDate == default || request.DueDate < request.InvoiceDate) throw Error("dueDate", "Due date must be on or after the invoice date.");
        var order = await salesOrders.FindAsync(request.SalesOrderId, cancellationToken) ?? throw Error("salesOrderId", "Sales order was not found.");
        if (order.State is not ("Confirmed" or "PartiallyFulfilled" or "Fulfilled")) throw Error("salesOrderId", "Only confirmed or fulfilled sales orders can be invoiced.");
        if (order.Lines.Count == 0) throw Error("salesOrderId", "The sales order has no invoiceable lines.");
        var now = clock.UtcNow;
        var invoiceId = Guid.NewGuid();
        var invoice = new CustomerInvoice { Id = invoiceId, TenantId = Tenant(), CompanyId = Company(), Number = $"INV-{now:yyyyMMdd}-{invoiceId.ToString("N")[..8].ToUpperInvariant()}", SalesOrderId = order.Id, SalesOrderNumber = order.Number, CustomerId = order.CustomerId, CustomerName = order.CustomerName, CustomerTaxNumber = order.CustomerTaxNumber, InvoiceDate = request.InvoiceDate, DueDate = request.DueDate, CurrencyCode = order.CurrencyCode, BaseCurrencyCode = order.BaseCurrencyCode, ExchangeRate = order.ExchangeRate, Subtotal = order.Subtotal, DiscountTotal = order.DiscountTotal, TaxTotal = order.TaxTotal, Total = order.Total, AmountPaid = 0, State = CustomerInvoiceState.Draft, IdempotencyKey = key, CreatedAtUtc = now, UpdatedAtUtc = now, ConcurrencyStamp = Stamp() };
        var sequence = 0;
        foreach (var line in order.Lines) invoice.Lines.Add(new CustomerInvoiceLine { Id = Guid.NewGuid(), TenantId = invoice.TenantId, CompanyId = invoice.CompanyId, CustomerInvoiceId = invoiceId, Sequence = ++sequence, ProductId = line.ProductId, ProductSku = line.ProductSku, Description = line.Description, UnitCode = line.UnitCode, Quantity = line.Quantity, UnitPrice = line.UnitPrice, DiscountPercent = line.DiscountPercent, TaxPercent = line.TaxPercent, Subtotal = line.Subtotal, DiscountAmount = line.DiscountAmount, TaxAmount = line.TaxAmount, Total = line.Total });
        db.CustomerInvoices.Add(invoice);
        await Save(cancellationToken);
        await Audit("accounting.invoice.created", invoice, cancellationToken);
        return Dto(invoice);
    }

    public async Task<CustomerInvoiceDto> PostAsync(Guid id, PostCustomerInvoiceRequest request, CancellationToken cancellationToken)
    {
        var key = RequiredKey(request.IdempotencyKey);
        var invoice = await Find(id, cancellationToken);
        if (invoice.State != CustomerInvoiceState.Draft)
        {
            if (invoice.State is CustomerInvoiceState.Posted or CustomerInvoiceState.PartiallyPaid or CustomerInvoiceState.Paid) return Dto(invoice);
            throw Error("state", "Only draft invoices can be posted.");
        }
        Check(invoice.ConcurrencyStamp, request.ConcurrencyStamp);
        if (!await db.FiscalPeriods.AsNoTracking().AnyAsync(x => x.State == FiscalPeriodState.Open && x.StartsOn <= invoice.InvoiceDate && x.EndsOn >= invoice.InvoiceDate, cancellationToken)) throw Error("invoiceDate", "Invoice date must be in an open fiscal period.");
        var journal = await db.Journals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.JournalId && x.IsActive, cancellationToken) ?? throw Error("journalId", "An active journal is required.");
        var accountIds = new[] { request.ReceivableAccountId, request.RevenueAccountId, request.TaxPayableAccountId ?? Guid.Empty }.Where(x => x != Guid.Empty).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking().Where(x => accountIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (accounts.Count != accountIds.Length || accounts.Values.Any(x => !x.IsActive || !x.AllowDirectPosting)) throw Error("accounts", "Posting accounts must be active direct-posting accounts.");
        if (invoice.TaxTotal > 0 && request.TaxPayableAccountId is null) throw Error("taxPayableAccountId", "A tax payable account is required when the invoice includes tax.");
        var netRevenue = Money(invoice.Subtotal - invoice.DiscountTotal);
        if (Money(netRevenue + invoice.TaxTotal) != Money(invoice.Total)) throw Error("total", "Invoice totals are inconsistent and cannot be posted.");
        var now = clock.UtcNow;
        var entryId = Guid.NewGuid();
        var entry = new JournalEntry { Id = entryId, TenantId = invoice.TenantId, CompanyId = invoice.CompanyId, Number = $"JE-{now:yyyyMMdd}-{entryId.ToString("N")[..8].ToUpperInvariant()}", JournalId = journal.Id, BusinessDate = invoice.InvoiceDate, Description = $"Customer invoice {invoice.Number}", CurrencyCode = invoice.CurrencyCode, ExchangeRate = invoice.ExchangeRate, State = JournalEntryState.Posted, IdempotencyKey = $"invoice:{invoice.Id}", PostingKey = key, CreatedAtUtc = now, PostedAtUtc = now, ConcurrencyStamp = Stamp() };
        AddLine(entry, 1, accounts[request.ReceivableAccountId], invoice.CustomerName, invoice.Total, 0);
        AddLine(entry, 2, accounts[request.RevenueAccountId], $"Revenue for {invoice.Number}", 0, netRevenue);
        if (invoice.TaxTotal > 0) AddLine(entry, 3, accounts[request.TaxPayableAccountId!.Value], $"Tax for {invoice.Number}", 0, invoice.TaxTotal);
        GeneralLedgerService.ValidateBalance(entry.Lines.Select(x => (x.Debit, x.Credit)));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.JournalEntries.Add(entry);
        invoice.JournalEntryId = entry.Id;
        invoice.State = CustomerInvoiceState.Posted;
        invoice.UpdatedAtUtc = now;
        invoice.ConcurrencyStamp = Stamp();
        await Save(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        outbox.Add(new CustomerInvoicePostedEvent(Guid.NewGuid(), now, invoice.Id, invoice.Number, invoice.CustomerId, invoice.CurrencyCode, invoice.Total));
        outbox.Add(new JournalEntryPostedEvent(Guid.NewGuid(), now, entry.Id, entry.Number, entry.BusinessDate, entry.CurrencyCode));
        await Audit("accounting.invoice.posted", invoice, cancellationToken);
        return Dto(invoice);
    }

    public async Task<CustomerInvoiceDto> CancelAsync(Guid id, AccountingActionRequest request, CancellationToken cancellationToken)
    {
        var invoice = await Find(id, cancellationToken);
        Check(invoice.ConcurrencyStamp, request.ConcurrencyStamp);
        if (invoice.State != CustomerInvoiceState.Draft) throw Error("state", "Only draft invoices can be cancelled; posted invoices require a credit or reversal.");
        invoice.State = CustomerInvoiceState.Cancelled; invoice.UpdatedAtUtc = clock.UtcNow; invoice.ConcurrencyStamp = Stamp();
        await Save(cancellationToken); await Audit("accounting.invoice.cancelled", invoice, cancellationToken); return Dto(invoice);
    }

    private static void AddLine(JournalEntry entry, int sequence, Account account, string description, decimal debit, decimal credit) => entry.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = entry.TenantId, CompanyId = entry.CompanyId, JournalEntryId = entry.Id, Sequence = sequence, AccountId = account.Id, AccountCode = account.Code, AccountName = account.Name, Description = description, Debit = Money(debit), Credit = Money(credit), BaseDebit = Money(debit * entry.ExchangeRate), BaseCredit = Money(credit * entry.ExchangeRate) });
    private async Task<CustomerInvoice> Find(Guid id, CancellationToken cancellationToken) => await db.CustomerInvoices.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new KeyNotFoundException();
    private async Task Save(CancellationToken cancellationToken) { try { await db.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { throw new AccountingConcurrencyException(); } catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true) { throw Error("idempotencyKey", "This sales order or operation was already invoiced."); } }
    private async Task Audit(string action, CustomerInvoice invoice, CancellationToken cancellationToken) { audit.Append(new(action, nameof(CustomerInvoice), invoice.Id.ToString(), null, null, JsonSerializer.Serialize(Dto(invoice)))); await audit.FlushAsync(cancellationToken); }
    private Guid Tenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); private Guid Company() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private static decimal Money(decimal value) => decimal.Round(value, 4, MidpointRounding.ToEven); private static string Stamp() => Guid.NewGuid().ToString("N"); private static string RequiredKey(string? value) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100) throw Error("idempotencyKey", "A unique operation key up to 100 characters is required."); return value.Trim(); }
    private static void Check(string actual, string supplied) { if (!StringComparer.Ordinal.Equals(actual, supplied)) throw new AccountingConcurrencyException(); }
    private static AccountingValidationException Error(string key, string message) => new(new Dictionary<string, string[]> { { key, [message] } });
    private static CustomerInvoiceDto Dto(CustomerInvoice invoice) => new(invoice.Id, invoice.Number, invoice.SalesOrderId, invoice.SalesOrderNumber, invoice.CustomerId, invoice.CustomerName, invoice.CustomerTaxNumber, invoice.InvoiceDate, invoice.DueDate, invoice.CurrencyCode, invoice.BaseCurrencyCode, invoice.ExchangeRate, invoice.Subtotal, invoice.DiscountTotal, invoice.TaxTotal, invoice.Total, invoice.AmountPaid, Money(invoice.Total - invoice.AmountPaid), invoice.State, invoice.JournalEntryId, invoice.CreatedAtUtc, invoice.UpdatedAtUtc, invoice.ConcurrencyStamp, invoice.Lines.OrderBy(x => x.Sequence).Select(x => new CustomerInvoiceLineDto(x.Id, x.Sequence, x.ProductId, x.ProductSku, x.Description, x.UnitCode, x.Quantity, x.UnitPrice, x.DiscountPercent, x.TaxPercent, x.Subtotal, x.DiscountAmount, x.TaxAmount, x.Total)).ToList());
}
