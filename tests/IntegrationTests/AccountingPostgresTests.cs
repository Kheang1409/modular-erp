using Erp.Abstractions.Context;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Events;
using Erp.Modules.Accounting;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Purchasing.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using System.Diagnostics;

namespace Erp.IntegrationTests;

public sealed class AccountingPostgresTests
{
    [Fact]
    public async Task PostingReceivablesPayablesPaymentsAndReversalsPreserveLedgerInvariants()
    {
        PostgreSqlContainer? postgres = null;
        var connectionString = Environment.GetEnvironmentVariable("ERP_TEST_POSTGRES_CONNECTION");
        try
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                postgres = new PostgreSqlBuilder("postgres:18-alpine")
                    .WithDatabase("accounting_workflow_tests")
                    .WithUsername("erp")
                    .WithPassword("integration_only_password")
                    .Build();
                await postgres.StartAsync();
                connectionString = postgres.GetConnectionString();
            }
        }
        catch (Exception) when (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var tenantId = Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var journalId = Guid.NewGuid();
            var bankId = Guid.NewGuid();
            var receivableId = Guid.NewGuid();
            var revenueId = Guid.NewGuid();
            var taxPayableId = Guid.NewGuid();
            var payableId = Guid.NewGuid();
            var expenseId = Guid.NewGuid();
            var taxRecoverableId = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<AccountingDbContext>().UseNpgsql(connectionString).Options;
            await using (var migration = new AccountingDbContext(options)) await migration.Database.MigrateAsync();
            await using (var seed = Context(options, tenantId, companyId))
            {
                var now = TestClock.Now;
                seed.FiscalPeriods.Add(new FiscalPeriod { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, Name = "FY26", StartsOn = new(2026, 1, 1), EndsOn = new(2026, 12, 31), State = FiscalPeriodState.Open, CreatedAtUtc = now, ConcurrencyStamp = Stamp() });
                seed.Journals.Add(new Journal { Id = journalId, TenantId = tenantId, CompanyId = companyId, Code = "GJ", Name = "General", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now, ConcurrencyStamp = Stamp() });
                AddAccount(seed, tenantId, companyId, bankId, "1000", "Bank", AccountType.Asset, NormalBalance.Debit);
                AddAccount(seed, tenantId, companyId, receivableId, "1100", "Receivable", AccountType.Asset, NormalBalance.Debit);
                AddAccount(seed, tenantId, companyId, revenueId, "4000", "Revenue", AccountType.Revenue, NormalBalance.Credit);
                AddAccount(seed, tenantId, companyId, taxPayableId, "2100", "Tax payable", AccountType.Liability, NormalBalance.Credit);
                AddAccount(seed, tenantId, companyId, payableId, "2000", "Payable", AccountType.Liability, NormalBalance.Credit);
                AddAccount(seed, tenantId, companyId, expenseId, "5000", "Expense", AccountType.Expense, NormalBalance.Debit);
                AddAccount(seed, tenantId, companyId, taxRecoverableId, "1200", "Tax recoverable", AccountType.Asset, NormalBalance.Debit);
                await seed.SaveChangesAsync();
            }

            var audit = new Audit();
            var outbox = new Outbox();
            var salesOrder = SalesOrder();
            var purchaseOrder = PurchaseOrder();
            await using var db = Context(options, tenantId, companyId);
            var invoiceService = new CustomerInvoiceService(db, new Sales(salesOrder), new Tenant(tenantId), new Company(companyId), new TestClock(), audit, outbox);
            var invoice = await invoiceService.CreateFromSalesOrderAsync(new(salesOrder.Id, new(2026, 6, 1), new(2026, 6, 30), "invoice-create"), default);
            var sameInvoice = await invoiceService.CreateFromSalesOrderAsync(new(salesOrder.Id, new(2026, 6, 1), new(2026, 6, 30), "invoice-create"), default);
            Assert.Equal(invoice.Id, sameInvoice.Id);
            invoice = await invoiceService.PostAsync(invoice.Id, new(journalId, receivableId, revenueId, taxPayableId, invoice.ConcurrencyStamp, "invoice-post"), default);
            Assert.Equal(CustomerInvoiceState.Posted, invoice.State);
            Assert.Equal(110m, invoice.AmountDue);

            var billService = new SupplierBillService(db, new Purchasing(purchaseOrder), new Tenant(tenantId), new Company(companyId), new TestClock(), audit, outbox);
            var bill = await billService.CreateFromPurchaseOrderAsync(new(purchaseOrder.Id, "SUP-001", new(2026, 6, 2), new(2026, 7, 2), "bill-create"), default);
            bill = await billService.PostAsync(bill.Id, new(journalId, payableId, expenseId, taxRecoverableId, bill.ConcurrencyStamp, "bill-post"), default);
            Assert.Equal(SupplierBillState.Posted, bill.State);

            var payments = new PaymentService(db, new Tenant(tenantId), new Company(companyId), new TestClock(), audit, outbox);
            await Assert.ThrowsAsync<AccountingValidationException>(() => payments.CreateAsync(new(PaymentDirection.CustomerReceipt, salesOrder.CustomerId, salesOrder.CustomerName, new(2026, 6, 5), "USD", 1.25m, 111m, "TOO-MUCH", [new(invoice.Id, 111m)], "payment-overallocate"), default));
            var receipt = await payments.CreateAsync(new(PaymentDirection.CustomerReceipt, salesOrder.CustomerId, salesOrder.CustomerName, new(2026, 6, 5), "USD", 1.25m, 60m, "RCPT-001", [new(invoice.Id, 60m)], "payment-receipt"), default);
            receipt = await payments.PostAsync(receipt.Id, new(journalId, bankId, receivableId, receipt.ConcurrencyStamp, "payment-receipt-post"), default);
            var repeatedReceipt = await payments.PostAsync(receipt.Id, new(journalId, bankId, receivableId, receipt.ConcurrencyStamp, "payment-receipt-post"), default);
            Assert.Equal(receipt.Id, repeatedReceipt.Id);
            Assert.Equal(CustomerInvoiceState.PartiallyPaid, (await invoiceService.GetAsync(invoice.Id, default)).State);
            receipt = await payments.ReverseAsync(receipt.Id, new(new(2026, 6, 6), "payment-receipt-reverse"), default);
            Assert.Equal(PaymentState.Reversed, receipt.State);
            Assert.Equal(CustomerInvoiceState.Posted, (await invoiceService.GetAsync(invoice.Id, default)).State);
            Assert.Equal(110m, (await invoiceService.GetAsync(invoice.Id, default)).AmountDue);

            var supplierPayment = await payments.CreateAsync(new(PaymentDirection.SupplierPayment, purchaseOrder.SupplierId, purchaseOrder.SupplierName, new(2026, 6, 7), "USD", 1.25m, 55m, "PAY-001", [new(bill.Id, 55m)], "supplier-payment"), default);
            supplierPayment = await payments.PostAsync(supplierPayment.Id, new(journalId, bankId, payableId, supplierPayment.ConcurrencyStamp, "supplier-payment-post"), default);
            Assert.Equal(SupplierBillState.Paid, (await billService.GetAsync(bill.Id, default)).State);

            var ledger = new GeneralLedgerService(db, new Tenant(tenantId), new Company(companyId), new TestClock(), audit, outbox);
            await Assert.ThrowsAsync<AccountingValidationException>(() => ledger.CreateAsync(new(journalId, new(2026, 6, 8), "Unbalanced", "USD", 1, [new(bankId, "Debit", 10, 0), new(revenueId, "Credit", 0, 9)]), default));
            var draft = await ledger.CreateAsync(new(journalId, new(2026, 6, 8), "Manual entry", "USD", 1.25m, [new(bankId, "Debit", 20, 0), new(revenueId, "Credit", 0, 20)], "manual-create"), default);

            async Task<(JournalEntryDto? Value, Exception? Error)> PostConcurrently()
            {
                try
                {
                    await using var concurrentDb = Context(options, tenantId, companyId);
                    var service = new GeneralLedgerService(concurrentDb, new Tenant(tenantId), new Company(companyId), new TestClock(), new Audit(), new Outbox());
                    return (await service.PostAsync(draft.Id, new(draft.ConcurrencyStamp, "manual-post"), default), null);
                }
                catch (Exception exception) { return (null, exception); }
            }
            var concurrentPosts = await Task.WhenAll(PostConcurrently(), PostConcurrently());
            Assert.Contains(concurrentPosts, result => result.Value?.State == JournalEntryState.Posted);
            Assert.All(concurrentPosts.Where(result => result.Error is not null), result => Assert.IsType<AccountingConcurrencyException>(result.Error));
            Assert.All(concurrentPosts.Where(result => result.Value is not null), result => Assert.Equal(draft.Id, result.Value!.Id));
            Assert.Equal(1, await db.JournalEntries.CountAsync(entry => entry.Id == draft.Id));
            await db.Entry(await db.JournalEntries.SingleAsync(x => x.Id == draft.Id)).ReloadAsync();
            var posted = await ledger.PostAsync(draft.Id, new((await ledger.GetAsync(draft.Id, default)).ConcurrencyStamp, "manual-post"), default);
            var reversal = await ledger.ReverseAsync(posted.Id, new(new(2026, 6, 9), "Correction", "manual-reverse"), default);
            var repeatedReversal = await ledger.ReverseAsync(posted.Id, new(new(2026, 6, 9), "Correction", "manual-reverse"), default);
            Assert.Equal(reversal.Id, repeatedReversal.Id);
            Assert.Equal(posted.ExchangeRate, reversal.ExchangeRate);
            Assert.Equal(posted.Lines.Sum(x => x.Debit), reversal.Lines.Sum(x => x.Credit));
            Assert.Equal(posted.Lines.Sum(x => x.Credit), reversal.Lines.Sum(x => x.Debit));

            var closedDateDraft = await ledger.CreateAsync(new(journalId, new(2027, 1, 1), "Closed date", "USD", 1, [new(bankId, "Debit", 1, 0), new(revenueId, "Credit", 0, 1)], "closed-create"), default);
            await Assert.ThrowsAsync<AccountingValidationException>(() => ledger.PostAsync(closedDateDraft.Id, new(closedDateDraft.ConcurrencyStamp, "closed-post"), default));

            var entries = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).Where(x => x.State == JournalEntryState.Posted).ToListAsync();
            Assert.All(entries, entry => Assert.Equal(entry.Lines.Sum(x => x.BaseDebit), entry.Lines.Sum(x => x.BaseCredit)));
            Assert.Contains(audit.Entries, item => item.Action == "accounting.invoice.posted");
            Assert.Contains(audit.Entries, item => item.Action == "accounting.bill.posted");
            Assert.Contains(audit.Entries, item => item.Action == "accounting.payment.reversed");
            Assert.Contains(outbox.Events, item => item is CustomerInvoicePostedEvent);
            Assert.Contains(outbox.Events, item => item is SupplierBillPostedEvent);
            Assert.Contains(outbox.Events, item => item is PaymentPostedEvent);
            Assert.Contains(outbox.Events, item => item is PaymentReversedEvent);
        }
        finally
        {
            if (postgres is not null) await postgres.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConfigurationIsCompanyIsolatedConcurrentAndPeriodAware()
    {
        PostgreSqlContainer postgres;
        try
        {
            postgres = new PostgreSqlBuilder("postgres:18-alpine")
                .WithDatabase("accounting_tests")
                .WithUsername("erp")
                .WithPassword("integration_only_password")
                .Build();
            await postgres.StartAsync();
        }
        catch (Exception) when (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using (postgres)
        {
            var tenantId = Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<AccountingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            await using (var migration = new AccountingDbContext(options)) await migration.Database.MigrateAsync();

            var periodId = Guid.NewGuid();
            var cashAccountId = Guid.NewGuid();
            var revenueAccountId = Guid.NewGuid();
            var journalId = Guid.NewGuid();
            var entryId = Guid.NewGuid();
            await using (var write = Context(options, tenantId, companyId))
            {
                write.Accounts.Add(new Account { Id = cashAccountId, TenantId = tenantId, CompanyId = companyId, Code = "1000", Name = "Cash", Type = AccountType.Asset, NormalBalance = NormalBalance.Debit, AllowDirectPosting = true, IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Stamp() });
                write.Accounts.Add(new Account { Id = revenueAccountId, TenantId = tenantId, CompanyId = companyId, Code = "4000", Name = "Revenue", Type = AccountType.Revenue, NormalBalance = NormalBalance.Credit, AllowDirectPosting = true, IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Stamp() });
                write.Journals.Add(new Journal { Id = journalId, TenantId = tenantId, CompanyId = companyId, Code = "GJ", Name = "General", IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Stamp() });
                write.FiscalPeriods.Add(new FiscalPeriod { Id = periodId, TenantId = tenantId, CompanyId = companyId, Name = "FY26", StartsOn = new(2026, 1, 1), EndsOn = new(2026, 12, 31), State = FiscalPeriodState.Open, CreatedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Stamp() });
                var entry = new JournalEntry { Id = entryId, TenantId = tenantId, CompanyId = companyId, Number = "JE-TEST-1", JournalId = journalId, BusinessDate = new(2026, 6, 1), Description = "Immutable posted entry", CurrencyCode = "USD", ExchangeRate = 1, State = JournalEntryState.Posted, CreatedAtUtc = DateTimeOffset.UtcNow, PostedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Stamp() };
                entry.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, JournalEntryId = entryId, Sequence = 1, AccountId = cashAccountId, AccountCode = "1000", AccountName = "Cash", Description = "Cash", Debit = 25, Credit = 0, BaseDebit = 25, BaseCredit = 0 });
                entry.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, JournalEntryId = entryId, Sequence = 2, AccountId = revenueAccountId, AccountCode = "4000", AccountName = "Revenue", Description = "Revenue", Debit = 0, Credit = 25, BaseDebit = 0, BaseCredit = 25 });
                write.JournalEntries.Add(entry);
                await write.SaveChangesAsync();
            }

            await using var visible = Context(options, tenantId, companyId);
            await using var hidden = Context(options, tenantId, Guid.NewGuid());
            Assert.Equal(2, await visible.Accounts.CountAsync());
            Assert.Empty(await hidden.Accounts.ToListAsync());
            Assert.True(await new AccountingPeriodStatus(visible).IsOpenAsync(new(2026, 6, 1), default));
            Assert.False(await new AccountingPeriodStatus(visible).IsOpenAsync(new(2027, 1, 1), default));

            await using (var immutable = Context(options, tenantId, companyId))
            {
                var posted = await immutable.JournalEntries.SingleAsync(x => x.Id == entryId);
                posted.Description = "Illegal mutation";
                await Assert.ThrowsAsync<InvalidOperationException>(() => immutable.SaveChangesAsync());
            }

            var stale = await visible.FiscalPeriods.SingleAsync();
            await using (var other = Context(options, tenantId, companyId))
            {
                var current = await other.FiscalPeriods.SingleAsync();
                current.State = FiscalPeriodState.Closed;
                current.ConcurrencyStamp = Stamp();
                await other.SaveChangesAsync();
            }
            stale.Name = "Stale update";
            stale.ConcurrencyStamp = Stamp();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => visible.SaveChangesAsync());

            await using (var volume = Context(options, tenantId, companyId))
            {
                for (var index = 0; index < 2_000; index++)
                {
                    var id = Guid.NewGuid();
                    var amount = 10m + index % 7;
                    var entry = new JournalEntry { Id = id, TenantId = tenantId, CompanyId = companyId, Number = $"JE-LOAD-{index:D5}", JournalId = journalId, BusinessDate = new(2026, 6, 2), Description = "Reporting load row", CurrencyCode = "USD", ExchangeRate = 1, State = JournalEntryState.Posted, CreatedAtUtc = DateTimeOffset.UtcNow, PostedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Stamp() };
                    entry.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, JournalEntryId = id, Sequence = 1, AccountId = cashAccountId, AccountCode = "1000", AccountName = "Cash", Description = "Cash", Debit = amount, Credit = 0, BaseDebit = amount, BaseCredit = 0 });
                    entry.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, JournalEntryId = id, Sequence = 2, AccountId = revenueAccountId, AccountCode = "4000", AccountName = "Revenue", Description = "Revenue", Debit = 0, Credit = amount, BaseDebit = 0, BaseCredit = amount });
                    volume.JournalEntries.Add(entry);
                }
                await volume.SaveChangesAsync();
            }

            await using var reportContext = Context(options, tenantId, companyId);
            var reporting = new AccountingReportingData(reportContext);
            var stopwatch = Stopwatch.StartNew();
            var trialBalance = await reporting.TrialBalanceAsync(new(2026, 12, 31), CancellationToken.None);
            var ledger = await reporting.GeneralLedgerAsync(new(2026, 1, 1), new(2026, 12, 31), 1, 200, CancellationToken.None);
            stopwatch.Stop();
            Assert.Equal(trialBalance.Sum(row => row.Debit), trialBalance.Sum(row => row.Credit));
            Assert.Equal(4_002, ledger.Total);
            Assert.Equal(200, ledger.Items.Count);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Reporting queries took {stopwatch.Elapsed}.");
        }
    }

    private static AccountingDbContext Context(DbContextOptions<AccountingDbContext> options, Guid tenantId, Guid companyId) =>
        new(options, new Tenant(tenantId), new Company(companyId));
    private static void AddAccount(AccountingDbContext db, Guid tenantId, Guid companyId, Guid id, string code, string name, AccountType type, NormalBalance normalBalance) =>
        db.Accounts.Add(new Account { Id = id, TenantId = tenantId, CompanyId = companyId, Code = code, Name = name, Type = type, NormalBalance = normalBalance, AllowDirectPosting = true, IsActive = true, CreatedAtUtc = TestClock.Now, UpdatedAtUtc = TestClock.Now, ConcurrencyStamp = Stamp() });
    private static SalesOrderReference SalesOrder()
    {
        var productId = Guid.NewGuid();
        return new(Guid.NewGuid(), "SO-ACCEPT-1", Guid.NewGuid(), "Customer", "CUST-TAX", "USD", "USD", 1.25m, 100m, 0m, 10m, 110m, "Confirmed", [new(Guid.NewGuid(), productId, "SKU-1", "Service", "EA", 1, 100m, 0m, 10m, 100m, 0m, 10m, 110m)]);
    }
    private static PurchaseOrderReference PurchaseOrder()
    {
        var productId = Guid.NewGuid();
        return new(Guid.NewGuid(), "PO-ACCEPT-1", Guid.NewGuid(), "Supplier", "SUP-TAX", "USD", "USD", 1.25m, 50m, 0m, 5m, 55m, "Confirmed", [new(Guid.NewGuid(), productId, "SKU-2", "Materials", "EA", 1, 50m, 0m, 10m, 50m, 0m, 5m, 55m)]);
    }
    private static string Stamp() => Guid.NewGuid().ToString("N");
    private sealed class Tenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class Company(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class TestClock : IClock { public static DateTimeOffset Now => new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero); public DateTimeOffset UtcNow => Now; }
    private sealed class Sales(SalesOrderReference order) : ISalesOrderDirectory { public Task<SalesOrderReference?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<SalesOrderReference?>(id == order.Id ? order : null); }
    private sealed class Purchasing(PurchaseOrderReference order) : IPurchaseOrderDirectory { public Task<PurchaseOrderReference?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<PurchaseOrderReference?>(id == order.Id ? order : null); }
    private sealed class Audit : IAuditLogWriter { public System.Collections.Concurrent.ConcurrentBag<AuditWriteEntry> Entries { get; } = []; public void Append(AuditWriteEntry entry) => Entries.Add(entry); public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class Outbox : IOutboxWriter { public System.Collections.Concurrent.ConcurrentBag<IIntegrationEvent> Events { get; } = []; public void Add<TEvent>(TEvent integrationEvent) where TEvent : IIntegrationEvent => Events.Add(integrationEvent); }
}
