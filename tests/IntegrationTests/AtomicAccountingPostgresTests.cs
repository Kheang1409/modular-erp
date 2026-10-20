using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Currencies;
using Erp.Abstractions.Events;
using Erp.Abstractions.Persistence;
using Erp.Abstractions.Sequences;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Currencies;
using Erp.Infrastructure.Events;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Sequences;
using Erp.ModuleSystem;
using Erp.Modules.Accounting;
using Erp.Modules.Contacts;
using Erp.Modules.Products;
using Erp.Modules.Purchasing;
using Erp.Modules.Sales;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class AtomicAccountingPostgresTests(AtomicAccountingPostgresFixture fixture)
    : IClassFixture<AtomicAccountingPostgresFixture>
{
    [Theory]
    [InlineData("invoice-post")]
    [InlineData("customer-payment-post")]
    public async Task PostingFailureAfterRealPlatformFlushRollsBackDocumentsLedgerAllocationsAuditAndOutbox(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, operation);
        var original = await Snapshot(scope);
        scope.Audit.FailAfterFlush = true;

        await Assert.ThrowsAsync<InjectedAuditException>(() => operation == "invoice-post"
            ? Box(scope.Invoices.PostAsync(scope.Invoice.Id, new(scope.Journal.Id, scope.Receivable.Id, scope.Revenue.Id,
                scope.TaxPayable.Id, scope.Invoice.ConcurrencyStamp, "invoice-post"), CancellationToken.None))
            : Box(scope.Payments.PostAsync(scope.Payment.Id, new(scope.Journal.Id, scope.Bank.Id, scope.Receivable.Id,
                scope.Payment.ConcurrencyStamp, "payment-post"), CancellationToken.None)));

        Assert.Equal(1, scope.Audit.FlushCalls);
        var actual = await Snapshot(scope);
        await using var platform = scope.ReadPlatform();
        var auditCount = await platform.AuditLogs.CountAsync(item => item.CompanyId == scope.CompanyId);
        var eventCount = await platform.OutboxMessages.CountAsync(item => item.CompanyId == scope.CompanyId);
        Assert.True(original == actual && auditCount == 0 && eventCount == 0,
            $"Atomic rollback violated for {operation}: complete business/ledger/allocation snapshot restored={original == actual}; durable audits={auditCount}; durable events={eventCount}.");
        Assert.Empty(scope.Database.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Database.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    private static async Task<object> Box<T>(Task<T> operation) => (await operation)!;
    private static readonly string[] Operations =
    {
        "account-create", "account-update", "period-create", "period-update", "period-close", "period-reopen", "journal-create", "journal-update",
        "entry-create", "entry-post", "entry-reverse", "invoice-create", "invoice-post", "invoice-cancel", "bill-create", "bill-post", "bill-cancel",
        "customer-payment-create", "customer-payment-post", "customer-payment-reverse", "supplier-payment-create", "supplier-payment-post", "supplier-payment-reverse",
        "tax-create", "tax-update", "rate-add", "reconciliation-create", "reconciliation-finalize"
    };
    public static IEnumerable<object[]> MutationCases => Operations.Select(operation => new object[] { operation });

    [Theory]
    [MemberData(nameof(MutationCases))]
    public async Task EveryMutationRollsBackAfterRealAuditAndEventFlushThenAllowsSameScopeRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        scope.Audit.FailAfterFlush = true;

        await Assert.ThrowsAsync<InjectedAuditException>(() => mutation.Execute(CancellationToken.None));

        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        Assert.Equal(1, scope.Audit.FlushCalls);
        var rejectedIds = scope.Outbox.Attempts.Select(item => item.EventId).ToArray();
        Assert.Equal(mutation.Events.Length, rejectedIds.Length);
        var started = DateTimeOffset.UtcNow;
        var committed = await mutation.Execute(CancellationToken.None);
        var finished = DateTimeOffset.UtcNow;

        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, 1, mutation.Events.Length);
        await AssertAuditAndEvents(scope, mutation, committed, rejectedIds);
        await AssertOutcome(scope, operation, committed, started, finished);
    }

    [Theory]
    [MemberData(nameof(MutationCases))]
    public async Task CancellationAfterRealFlushRollsBackEveryMutationBeforeUncancelledSameScopeRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        using var cancellation = new CancellationTokenSource();
        scope.Audit.AfterFlush = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.Execute(cancellation.Token));

        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        scope.Audit.AfterFlush = null;
        await AssertDurableResult(scope, await mutation.Execute(CancellationToken.None));
        await AssertPlatformCounts(scope, 1, mutation.Events.Length);
    }

    [Theory]
    [InlineData("invoice-post", 1)]
    [InlineData("invoice-post", 2)]
    [InlineData("bill-post", 1)]
    [InlineData("bill-post", 2)]
    [InlineData("customer-payment-post", 1)]
    [InlineData("customer-payment-post", 2)]
    [InlineData("supplier-payment-post", 1)]
    [InlineData("supplier-payment-post", 2)]
    [InlineData("customer-payment-reverse", 1)]
    [InlineData("customer-payment-reverse", 2)]
    [InlineData("supplier-payment-reverse", 1)]
    [InlineData("supplier-payment-reverse", 2)]
    public async Task PartialPairedEventStagingCannotLeavePostedDocumentsOrAppliedAllocations(string operation, int failAt)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        scope.Outbox.FailAfterAdd = failAt;

        await Assert.ThrowsAsync<InjectedOutboxException>(() => mutation.Execute(CancellationToken.None));

        Assert.Equal(failAt, scope.Outbox.Attempts.Count);
        Assert.Equal(0, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        var rejectedIds = scope.Outbox.Attempts.Select(item => item.EventId).ToArray();
        var committed = await mutation.Execute(CancellationToken.None);
        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, 1, 2);
        await AssertAuditAndEvents(scope, mutation, committed, rejectedIds);
    }

    private static readonly string[] VersionedOperations =
    {
        "account-update", "period-update", "period-close", "period-reopen", "journal-update", "entry-post", "invoice-post", "invoice-cancel",
        "bill-post", "bill-cancel", "customer-payment-post", "supplier-payment-post", "tax-update", "reconciliation-finalize"
    };
    public static IEnumerable<object[]> VersionedCases => VersionedOperations.Select(operation => new object[] { operation });

    [Theory]
    [MemberData(nameof(VersionedCases))]
    public async Task StaleVersionsChangeNoFinancialRowsAndAllowFreshVersionRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);

        await Assert.ThrowsAsync<AccountingConcurrencyException>(() => VersionedMutation(scope, operation, "stale", CancellationToken.None));

        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        Assert.Empty(scope.Outbox.Attempts);
        await AssertDurableResult(scope, await mutation.Execute(CancellationToken.None));
        await AssertPlatformCounts(scope, 1, mutation.Events.Length);
    }

    [Theory]
    [MemberData(nameof(VersionedCases))]
    public async Task ARealWriterWinsBeforeSaveAndTheLoserCannotLeaveOrphanLedgerRowsOrEvents(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var peer = scope.CreatePeer();
        var mutation = await PrepareMutation(scope, operation);
        object? winner = null;
        string? winningSnapshot = null;
        scope.SaveHook.BeforeSave = async (_, token) =>
        {
            winner = await VersionedMutation(peer, operation, Version(scope, operation), token);
            winningSnapshot = await Snapshot(scope);
        };

        var failure = await Record.ExceptionAsync(() => mutation.Execute(CancellationToken.None));

        Assert.True(scope.SaveHook.Executed);
        if (failure is AccountingValidationException validation)
        {
            // Posting can hit its existing unique operation-key guard before the stale header UPDATE.
            Assert.Contains("idempotencyKey", validation.Errors.Keys);
            var persistenceFailure = Assert.IsType<DbUpdateException>(scope.SaveHook.LastFailure);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(persistenceFailure.InnerException).SqlState);
        }
        else
        {
            Assert.IsType<AccountingConcurrencyException>(failure);
            Assert.IsType<DbUpdateConcurrencyException>(scope.SaveHook.LastFailure);
        }
        Assert.NotNull(winner);
        Assert.Equal(winningSnapshot, await Snapshot(scope));
        await AssertDurableResult(scope, winner);
        await AssertPlatformCounts(scope, 1, mutation.Events.Length);
        Assert.Empty(scope.Outbox.Attempts);
        Assert.Equal(0, scope.Audit.FlushCalls);
        AssertClean(scope);
    }

    [Theory]
    [InlineData("entry-line")]
    [InlineData("reversal-description")]
    [InlineData("reversal-prefixed-key")]
    [InlineData("reconciliation-amount")]
    public async Task ActualSqlFailuresRollBackHeadersChildrenGeneratedNumbersAndPermitValidRetry(string invalid)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        Task<object> Execute(bool valid) => invalid switch
        {
            "entry-line" => Box(scope.Ledger.CreateAsync(EntryRequest(scope) with
            {
                Lines =
                [new(scope.Bank.Id, valid ? "Debit" : new string('x', 501), 20, 0), new(scope.Revenue.Id, "Credit", 0, 20)]
            }, CancellationToken.None)),
            "reversal-description" => Box(scope.Ledger.ReverseAsync(scope.PostedEntry.Id, new(BusinessDate, valid ? "Reversal" : new string('x', 501), "reverse"), CancellationToken.None)),
            "reversal-prefixed-key" => Box(scope.Ledger.ReverseAsync(scope.PostedEntry.Id, new(BusinessDate, "Reversal", valid ? "reverse" : new string('k', 100)), CancellationToken.None)),
            _ => Box(scope.Controls.CreateReconciliationAsync(new(scope.Bank.Id, BusinessDate, valid ? 0 : decimal.MaxValue,
                valid ? 20 : decimal.MaxValue, [scope.PostedEntry.Id]), CancellationToken.None))
        };

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => Execute(false));

        Assert.Equal(invalid == "reconciliation-amount" ? PostgresErrorCodes.NumericValueOutOfRange : PostgresErrorCodes.StringDataRightTruncation,
            Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        Assert.Empty(scope.Outbox.Attempts);
        await AssertDurableResult(scope, await Execute(true));
        await AssertPlatformCounts(scope, 1, invalid.StartsWith("reversal", StringComparison.Ordinal) ? 1 : 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ASecondInvalidAllocationRestoresTheFirstTrackedDocumentAndAllowsSameScopeRetry(bool supplier, bool reverse)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var payment = supplier ? reverse ? scope.PostedSupplierPayment : scope.SupplierPayment : reverse ? scope.PostedPayment : scope.Payment;
        var secondaryId = supplier ? reverse ? scope.OpenBill.Id : scope.PaidBill.Id : reverse ? scope.OpenInvoice.Id : scope.PaidInvoice.Id;
        await using (var setup = scope.ReadAccounting())
        {
            var tracked = await setup.Payments.Include(item => item.Allocations).SingleAsync(item => item.Id == payment.Id);
            Assert.Single(tracked.Allocations).Amount = supplier ? 25 : 30;
            var secondaryAllocation = new PaymentAllocation
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                PaymentId = payment.Id,
                CustomerInvoiceId = supplier ? null : secondaryId,
                SupplierBillId = supplier ? secondaryId : null,
                DocumentNumber = "SECONDARY",
                Amount = 30
            };
            tracked.Allocations.Add(secondaryAllocation);
            setup.PaymentAllocations.Add(secondaryAllocation);
            Assert.Equal(EntityState.Added, setup.Entry(secondaryAllocation).State);
            if (!reverse && !supplier) (await setup.CustomerInvoices.SingleAsync(item => item.Id == secondaryId)).AmountPaid = 100;
            await setup.SaveChangesAsync();
        }
        var trackedPayment = await scope.Database.Payments.Include(item => item.Allocations).SingleAsync(item => item.Id == payment.Id);
        trackedPayment.Allocations.Sort((left, right) => left.DocumentNumber == "SECONDARY" ? 1 : right.DocumentNumber == "SECONDARY" ? -1 : 0);
        CustomerInvoice? firstInvoice = null;
        SupplierBill? firstBill = null;
        if (supplier) firstBill = await scope.Database.SupplierBills.SingleAsync(item => item.Id == (reverse ? scope.PaidBill.Id : scope.OpenBill.Id));
        else firstInvoice = await scope.Database.CustomerInvoices.SingleAsync(item => item.Id == (reverse ? scope.PaidInvoice.Id : scope.OpenInvoice.Id));
        Assert.False(scope.Database.ChangeTracker.HasChanges());
        var original = await Snapshot(scope);
        Task<object> Execute() => reverse
            ? Box(scope.Payments.ReverseAsync(payment.Id, new(BusinessDate, "late-allocation-reverse"), CancellationToken.None))
            : Box(scope.Payments.PostAsync(payment.Id, new(scope.Journal.Id, scope.Bank.Id, supplier ? scope.Payable.Id : scope.Receivable.Id,
                payment.ConcurrencyStamp, "late-allocation-post"), CancellationToken.None));

        var failure = await Assert.ThrowsAsync<AccountingValidationException>(Execute);

        Assert.Contains("allocations", failure.Errors.Keys);
        Assert.Equal(supplier ? reverse ? 30 : 25 : 30, firstBill?.AmountPaid ?? firstInvoice!.AmountPaid);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        Assert.Empty(scope.Outbox.Attempts);
        await using (var setup = scope.ReadAccounting())
        {
            if (supplier)
            {
                var target = await setup.SupplierBills.SingleAsync(item => item.Id == secondaryId);
                target.AmountPaid = reverse ? 30 : 0;
                target.State = reverse ? SupplierBillState.PartiallyPaid : SupplierBillState.Posted;
            }
            else
            {
                var target = await setup.CustomerInvoices.SingleAsync(item => item.Id == secondaryId);
                target.AmountPaid = reverse ? 30 : 0;
                target.State = reverse ? CustomerInvoiceState.PartiallyPaid : CustomerInvoiceState.Posted;
            }
            await setup.SaveChangesAsync();
        }
        var committed = await Execute();
        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, 1, 2);
        await using (var reader = scope.ReadAccounting())
        {
            if (supplier) Assert.Equal(reverse ? 0 : 30, (await reader.SupplierBills.SingleAsync(item => item.Id == secondaryId)).AmountPaid);
            else Assert.Equal(reverse ? 0 : 30, (await reader.CustomerInvoices.SingleAsync(item => item.Id == secondaryId)).AmountPaid);
        }
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("invoice")]
    [InlineData("bill")]
    [InlineData("payment")]
    public async Task ClosedPeriodsRejectEveryPostingFamilyWithoutDirtyTracking(string document)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using (var setup = scope.ReadAccounting())
        {
            (await setup.FiscalPeriods.SingleAsync(item => item.StartsOn.Year == 2026)).State = FiscalPeriodState.Closed;
            await setup.SaveChangesAsync();
        }
        var operation = document switch { "entry" => "entry-post", "invoice" => "invoice-post", "bill" => "bill-post", _ => "customer-payment-post" };
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);

        await Assert.ThrowsAsync<AccountingValidationException>(() => mutation.Execute(CancellationToken.None));

        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        await using (var setup = scope.ReadAccounting())
        {
            (await setup.FiscalPeriods.SingleAsync(item => item.StartsOn.Year == 2026)).State = FiscalPeriodState.Open;
            await setup.SaveChangesAsync();
        }
        await AssertDurableResult(scope, await mutation.Execute(CancellationToken.None));
    }

    [Theory]
    [InlineData("invoice", "tax")]
    [InlineData("invoice", "account")]
    [InlineData("invoice", "total")]
    [InlineData("bill", "tax")]
    [InlineData("bill", "account")]
    [InlineData("bill", "total")]
    public async Task PostingTaxAccountAndCommercialTotalValidationPreservesTheCompleteDraft(string document, string invalid)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        if (invalid == "total")
        {
            await using var setup = scope.ReadAccounting();
            if (document == "invoice") (await setup.CustomerInvoices.SingleAsync(item => item.Id == scope.Invoice.Id)).Total += 1;
            else (await setup.SupplierBills.SingleAsync(item => item.Id == scope.Bill.Id)).Total += 1;
            await setup.SaveChangesAsync();
        }
        var original = await Snapshot(scope);
        Task<object> Execute() => document == "invoice"
            ? Box(scope.Invoices.PostAsync(scope.Invoice.Id, new(scope.Journal.Id, invalid == "account" ? Guid.NewGuid() : scope.Receivable.Id,
                scope.Revenue.Id, invalid == "tax" ? null : scope.TaxPayable.Id, scope.Invoice.ConcurrencyStamp, "invalid-invoice"), CancellationToken.None))
            : Box(scope.Bills.PostAsync(scope.Bill.Id, new(scope.Journal.Id, invalid == "account" ? Guid.NewGuid() : scope.Payable.Id,
                scope.Expense.Id, invalid == "tax" ? null : scope.TaxRecoverable.Id, scope.Bill.ConcurrencyStamp, "invalid-bill"), CancellationToken.None));

        await Assert.ThrowsAsync<AccountingValidationException>(Execute);

        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        Assert.Empty(scope.Outbox.Attempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PostedJournalHeadersAndLinesRejectBothEditsAndDeletes(bool line, bool delete)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        if (line)
        {
            var item = await scope.Database.JournalEntryLines.FirstAsync(value => value.JournalEntryId == scope.PostedEntry.Id);
            if (delete) scope.Database.Remove(item); else item.Description = "Illegal mutation";
        }
        else
        {
            var item = await scope.Database.JournalEntries.SingleAsync(value => value.Id == scope.PostedEntry.Id);
            if (delete) scope.Database.Remove(item); else item.Description = "Illegal mutation";
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Database.SaveChangesAsync());

        scope.Database.ChangeTracker.Clear();
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        var reversal = await scope.Ledger.ReverseAsync(scope.PostedEntry.Id, new(BusinessDate, "Valid correction", "immutable-reverse"), CancellationToken.None);
        Assert.Equal(scope.PostedEntry.Id, reversal.ReversesEntryId);
        await AssertDurableResult(scope, reversal);
    }

    [Theory]
    [InlineData("period-noop")]
    [InlineData("entry-create")]
    [InlineData("entry-post")]
    [InlineData("entry-post-state")]
    [InlineData("entry-reverse")]
    [InlineData("invoice-create")]
    [InlineData("invoice-post")]
    [InlineData("bill-create")]
    [InlineData("bill-post")]
    [InlineData("customer-payment-create")]
    [InlineData("customer-payment-post")]
    [InlineData("customer-payment-reverse")]
    [InlineData("reconciliation-finalize")]
    public async Task EverySuccessfulNoOpBranchPreservesStagedContactsProductsAndAccountingOuterWork(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = operation == "period-noop"
            ? new Mutation("none", [], token => Box(scope.Configuration.SetPeriodStateAsync(scope.ControlPeriod.Id,
                FiscalPeriodState.Open, scope.ControlPeriod.ConcurrencyStamp, token)))
            : await PrepareMutation(scope, operation == "entry-post-state" ? "entry-post" : operation);
        var expected = await mutation.Execute(CancellationToken.None);
        var original = await Snapshot(scope);
        var baseAudits = operation == "period-noop" ? 0 : 1;
        Guid contactId;
        Guid productId;
        AccountDto account;
        await using (var outer = await scope.Transactions.BeginAsync(scope.Database, CancellationToken.None))
        {
            var contact = await scope.ContactService.CreateAsync(new(ContactKind.Organization, "Replay partner", null, null,
                "Replay partner", null, null, null, null, IsCustomer: true, IsSupplier: true), CancellationToken.None);
            var product = await scope.ProductService.CreateAsync(new(ProductType.Product, "REPLAY", "Replay product", null, null,
                12m, 5m, "USD", "EA", "EA", true), CancellationToken.None);
            account = await scope.Configuration.SaveAccountAsync(null, new("OUTER", "Outer account", AccountType.Asset, NormalBalance.Debit), CancellationToken.None);
            var flushes = scope.Audit.FlushCalls;
            var events = scope.Outbox.Attempts.Count;
            var replay = operation switch
            {
                "entry-post-state" => await Box(scope.Ledger.PostAsync(scope.DraftEntry.Id, new(scope.DraftEntry.ConcurrencyStamp, "unused-posting-key"), CancellationToken.None)),
                "reconciliation-finalize" => await Box(scope.Controls.FinalizeReconciliationAsync(scope.Reconciliation.Id,
                    new(Assert.IsType<BankReconciliationDto>(expected).ConcurrencyStamp), CancellationToken.None)),
                _ => await mutation.Execute(CancellationToken.None)
            };
            AssertDto(expected, replay);
            Assert.Equal(flushes, scope.Audit.FlushCalls);
            Assert.Equal(events, scope.Outbox.Attempts.Count);
            Assert.Equal(original, await Snapshot(scope));
            await AssertPlatformCounts(scope, baseAudits, mutation.Events.Length);
            contactId = contact.Id;
            productId = product.Id;
            await using (var contacts = scope.ReadContacts()) Assert.False(await contacts.Contacts.AnyAsync(item => item.Id == contactId));
            await using (var products = scope.ReadProducts()) Assert.False(await products.Products.AnyAsync(item => item.Id == productId));
            await outer.CommitAsync(CancellationToken.None);
        }
        await using (var contacts = scope.ReadContacts()) Assert.True(await contacts.Contacts.AnyAsync(item => item.Id == contactId));
        await using (var products = scope.ReadProducts()) Assert.True(await products.Products.AnyAsync(item => item.Id == productId));
        await AssertDurableResult(scope, account);
        await AssertDurableResult(scope, expected);
        await AssertPlatformCounts(scope, baseAudits + 3, mutation.Events.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOuterTransactionComposesContactsProductsSalesPurchasingAndAccountingPostingOrRollsBackEverything(bool commit)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        foreach (var context in new DbContext[] { scope.Platform, scope.Contacts, scope.Products, scope.Sales, scope.Purchasing, scope.Database })
            Assert.Same(scope.Session.Connection, context.Database.GetDbConnection());
        var original = await Snapshot(scope);
        Guid contactId;
        Guid productId;
        Guid salesOrderId;
        Guid purchaseOrderId;
        CustomerInvoiceDto invoice;
        SupplierBillDto bill;
        PaymentDto payment;
        await using (var outer = await scope.Transactions.BeginAsync(scope.Database, CancellationToken.None))
        {
            var contact = await scope.ContactService.CreateAsync(new(ContactKind.Organization, "Composed partner", null, null,
                "Composed partner", null, null, null, null, IsCustomer: true, IsSupplier: true), CancellationToken.None);
            var product = await scope.ProductService.CreateAsync(new(ProductType.Product, "COMPOSED", "Composed product", null, null,
                10m, 5m, "USD", "EA", "EA", true), CancellationToken.None);
            var quote = await scope.Quotations.CreateAsync(new(contact.Id, "USD", BusinessDate, null, null, null, [new(product.Id, 2)]), CancellationToken.None);
            quote = await scope.Quotations.TransitionAsync(quote.Id, QuotationState.Sent, quote.ConcurrencyStamp, CancellationToken.None);
            quote = await scope.Quotations.TransitionAsync(quote.Id, QuotationState.Accepted, quote.ConcurrencyStamp, CancellationToken.None);
            var order = await scope.SalesOrders.ConvertAsync(quote.Id, new(quote.ConcurrencyStamp, BusinessDate), CancellationToken.None);
            order = await scope.SalesOrders.TransitionAsync(order.Id, SalesOrderState.Confirmed, order.ConcurrencyStamp, CancellationToken.None);
            var purchase = await scope.PurchaseOrders.CreateAsync(new(contact.Id, "USD", BusinessDate, null, null, [new(product.Id, 2)]), CancellationToken.None);
            purchase = await scope.PurchaseOrders.TransitionAsync(purchase.Id, PurchaseOrderState.Confirmed, purchase.ConcurrencyStamp, CancellationToken.None);
            invoice = await scope.Invoices.CreateFromSalesOrderAsync(new(order.Id, BusinessDate, BusinessDate.AddDays(30), "composed-invoice"), CancellationToken.None);
            invoice = await scope.Invoices.PostAsync(invoice.Id, new(scope.Journal.Id, scope.Receivable.Id, scope.Revenue.Id, null,
                invoice.ConcurrencyStamp, "composed-invoice-post"), CancellationToken.None);
            bill = await scope.Bills.CreateFromPurchaseOrderAsync(new(purchase.Id, "COMPOSED-SUP", BusinessDate, BusinessDate.AddDays(30), "composed-bill"), CancellationToken.None);
            bill = await scope.Bills.PostAsync(bill.Id, new(scope.Journal.Id, scope.Payable.Id, scope.Expense.Id, null,
                bill.ConcurrencyStamp, "composed-bill-post"), CancellationToken.None);
            payment = await scope.Payments.CreateAsync(new(PaymentDirection.CustomerReceipt, contact.Id, contact.DisplayName, BusinessDate, "USD", 1, 10,
                "COMPOSED-PAYMENT", [new(invoice.Id, 10)], "composed-payment"), CancellationToken.None);
            payment = await scope.Payments.PostAsync(payment.Id, new(scope.Journal.Id, scope.Bank.Id, scope.Receivable.Id,
                payment.ConcurrencyStamp, "composed-payment-post"), CancellationToken.None);
            invoice = await scope.Invoices.GetAsync(invoice.Id, CancellationToken.None);
            Assert.Equal("Composed partner", invoice.CustomerName);
            Assert.Equal(20, invoice.Total);
            Assert.Equal(10, invoice.AmountPaid);
            Assert.Equal(10, bill.Total);
            contactId = contact.Id; productId = product.Id; salesOrderId = order.Id; purchaseOrderId = purchase.Id;
            Assert.Equal(original, await Snapshot(scope));
            await AssertPlatformCounts(scope, 0, 0);
            await using (var contacts = scope.ReadContacts()) Assert.False(await contacts.Contacts.AnyAsync(item => item.Id == contactId));
            await using (var products = scope.ReadProducts()) Assert.False(await products.Products.AnyAsync(item => item.Id == productId));
            await using (var sales = scope.ReadSales()) Assert.False(await sales.SalesOrders.AnyAsync(item => item.Id == salesOrderId));
            await using (var purchasing = scope.ReadPurchasing()) Assert.False(await purchasing.PurchaseOrders.AnyAsync(item => item.Id == purchaseOrderId));
            if (commit) await outer.CommitAsync(CancellationToken.None);
        }
        await using (var contacts = scope.ReadContacts()) Assert.Equal(commit, await contacts.Contacts.AnyAsync(item => item.Id == contactId));
        await using (var products = scope.ReadProducts()) Assert.Equal(commit, await products.Products.AnyAsync(item => item.Id == productId));
        await using (var sales = scope.ReadSales()) Assert.Equal(commit, await sales.SalesOrders.AnyAsync(item => item.Id == salesOrderId));
        await using (var purchasing = scope.ReadPurchasing()) Assert.Equal(commit, await purchasing.PurchaseOrders.AnyAsync(item => item.Id == purchaseOrderId));
        await AssertPlatformCounts(scope, commit ? 15 : 0, commit ? 12 : 0);
        if (commit)
        {
            // ApplyAllocations uses the wall clock; PostgreSQL preserves microseconds, not 100-nanosecond ticks.
            await using (var reader = scope.ReadAccounting())
            {
                var persistedInvoice = await reader.CustomerInvoices.AsNoTracking().SingleAsync(item => item.Id == invoice.Id);
                Assert.Equal(0, persistedInvoice.UpdatedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond);
                Assert.InRange(invoice.UpdatedAtUtc - persistedInvoice.UpdatedAtUtc, TimeSpan.Zero,
                    TimeSpan.FromTicks(TimeSpan.TicksPerMicrosecond - 1));
            }
            await AssertDurableResult(scope, invoice with
            {
                UpdatedAtUtc = invoice.UpdatedAtUtc.AddTicks(-(invoice.UpdatedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond))
            });
            await AssertDurableResult(scope, bill);
            await AssertDurableResult(scope, payment);
        }
        else
        {
            Assert.Equal(original, await Snapshot(scope));
            AssertClean(scope);
            foreach (var context in new DbContext[] { scope.Contacts, scope.Products, scope.Sales, scope.Purchasing })
            {
                Assert.Empty(context.ChangeTracker.Entries());
                Assert.Null(context.Database.CurrentTransaction);
            }
            await AssertDurableResult(scope, await scope.Ledger.CreateAsync(EntryRequest(scope), CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenantAndCompanyFiltersHideFinancialAggregatesAndRejectForeignReferences(bool sameTenant)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var foreign = await TestScope.CreateAsync(fixture.ConnectionString, existingTenant: sameTenant ? scope.TenantId : null);
        var original = await Snapshot(scope);
        var foreignOriginal = await Snapshot(foreign);
        Assert.DoesNotContain(await scope.Configuration.AccountsAsync(CancellationToken.None), item => item.Id == foreign.Bank.Id);
        Assert.DoesNotContain(await scope.Configuration.PeriodsAsync(CancellationToken.None), item => item.Id == foreign.ControlPeriod.Id);
        Assert.DoesNotContain(await scope.Configuration.JournalsAsync(CancellationToken.None), item => item.Id == foreign.Journal.Id);
        Assert.DoesNotContain(await scope.Ledger.ListAsync(CancellationToken.None), item => item.Id == foreign.PostedEntry.Id);
        Assert.DoesNotContain(await scope.Invoices.ListAsync(CancellationToken.None), item => item.Id == foreign.Invoice.Id);
        Assert.DoesNotContain(await scope.Bills.ListAsync(CancellationToken.None), item => item.Id == foreign.Bill.Id);
        Assert.DoesNotContain(await scope.Payments.ListAsync(CancellationToken.None), item => item.Id == foreign.Payment.Id);
        Assert.DoesNotContain(await scope.Controls.TaxCodesAsync(CancellationToken.None), item => item.Id == foreign.Tax.Id);
        Assert.DoesNotContain(await scope.Controls.CurrencyRatesAsync(CancellationToken.None), item => item.Id == foreign.Rate.Id);
        Assert.DoesNotContain(await scope.Controls.ReconciliationsAsync(CancellationToken.None), item => item.Id == foreign.Reconciliation.Id);
        var missing = new Func<Task>[]
        {
            () => scope.Configuration.SaveAccountAsync(foreign.Bank.Id, new("1000", "Foreign", AccountType.Asset, NormalBalance.Debit, ConcurrencyStamp: foreign.Bank.ConcurrencyStamp), CancellationToken.None),
            () => scope.Configuration.SetPeriodStateAsync(foreign.ControlPeriod.Id, FiscalPeriodState.Closed, foreign.ControlPeriod.ConcurrencyStamp, CancellationToken.None),
            () => scope.Configuration.SaveJournalAsync(foreign.Journal.Id, new("GENERAL", "Foreign", true, foreign.Journal.ConcurrencyStamp), CancellationToken.None),
            () => scope.Ledger.PostAsync(foreign.DraftEntry.Id, new(foreign.DraftEntry.ConcurrencyStamp, "foreign"), CancellationToken.None),
            () => scope.Ledger.ReverseAsync(foreign.PostedEntry.Id, new(BusinessDate, "Foreign", "foreign"), CancellationToken.None),
            () => scope.Invoices.PostAsync(foreign.Invoice.Id, new(scope.Journal.Id, scope.Receivable.Id, scope.Revenue.Id, scope.TaxPayable.Id, foreign.Invoice.ConcurrencyStamp, "foreign"), CancellationToken.None),
            () => scope.Invoices.CancelAsync(foreign.Invoice.Id, new(foreign.Invoice.ConcurrencyStamp), CancellationToken.None),
            () => scope.Bills.PostAsync(foreign.Bill.Id, new(scope.Journal.Id, scope.Payable.Id, scope.Expense.Id, scope.TaxRecoverable.Id, foreign.Bill.ConcurrencyStamp, "foreign"), CancellationToken.None),
            () => scope.Bills.CancelAsync(foreign.Bill.Id, new(foreign.Bill.ConcurrencyStamp), CancellationToken.None),
            () => scope.Payments.PostAsync(foreign.Payment.Id, new(scope.Journal.Id, scope.Bank.Id, scope.Receivable.Id, foreign.Payment.ConcurrencyStamp, "foreign"), CancellationToken.None),
            () => scope.Payments.ReverseAsync(foreign.PostedPayment.Id, new(BusinessDate, "foreign"), CancellationToken.None),
            () => scope.Controls.SaveTaxCodeAsync(foreign.Tax.Id, new("VAT", "Foreign", 10, scope.TaxPayable.Id, scope.TaxRecoverable.Id, true, foreign.Tax.ConcurrencyStamp), CancellationToken.None),
            () => scope.Controls.FinalizeReconciliationAsync(foreign.Reconciliation.Id, new(foreign.Reconciliation.ConcurrencyStamp), CancellationToken.None)
        };
        foreach (var mutation in missing)
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(mutation);
            AssertClean(scope);
        }
        var invalid = new Func<Task>[]
        {
            () => scope.Ledger.CreateAsync(EntryRequest(scope) with { JournalId = foreign.Journal.Id }, CancellationToken.None),
            () => scope.Ledger.CreateAsync(EntryRequest(scope) with { Lines = [new(foreign.Bank.Id, "Foreign", 20, 0), new(scope.Revenue.Id, "Credit", 0, 20)] }, CancellationToken.None),
            () => scope.Invoices.CreateFromSalesOrderAsync(new(foreign.SourceSalesOrderId, BusinessDate, BusinessDate.AddDays(30), "foreign"), CancellationToken.None),
            () => scope.Bills.CreateFromPurchaseOrderAsync(new(foreign.SourcePurchaseOrderId, "FOREIGN", BusinessDate, BusinessDate.AddDays(30), "foreign"), CancellationToken.None),
            () => scope.Payments.CreateAsync(PaymentRequest(scope, false) with { Allocations = [new(foreign.OpenInvoice.Id, 20)] }, CancellationToken.None),
            () => scope.Controls.CreateReconciliationAsync(new(foreign.Bank.Id, BusinessDate, 0, 0, []), CancellationToken.None)
        };
        foreach (var mutation in invalid)
        {
            await Assert.ThrowsAsync<AccountingValidationException>(mutation);
            AssertClean(scope);
        }
        scope.Database.Accounts.Add(new()
        {
            Id = Guid.NewGuid(),
            TenantId = foreign.TenantId,
            CompanyId = foreign.CompanyId,
            Code = "ILLEGAL",
            Name = "Illegal",
            Type = AccountType.Asset,
            NormalBalance = NormalBalance.Debit,
            CreatedAtUtc = FixedClock.Now,
            UpdatedAtUtc = FixedClock.Now,
            ConcurrencyStamp = Stamp()
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Database.SaveChangesAsync());
        scope.Database.ChangeTracker.Clear();
        Assert.Equal(original, await Snapshot(scope));
        Assert.Equal(foreignOriginal, await Snapshot(foreign));
        await AssertPlatformCounts(scope, 0, 0);
        await AssertPlatformCounts(foreign, 0, 0);
    }

    [Fact]
    public async Task ListAndSingleReadReturnSameNullableReversalLinksBeforeAndAfterReversal()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var listed = Assert.Single(await scope.Ledger.ListAsync(CancellationToken.None), item => item.Id == scope.PostedEntry.Id);
        var single = await scope.Ledger.GetAsync(scope.PostedEntry.Id, CancellationToken.None);
        Assert.Null(listed.ReversedByEntryId);
        Assert.Null(single.ReversedByEntryId);
        AssertDto(single, listed);
        await AssertPlatformCounts(scope, 0, 0);

        var reversal = await scope.Ledger.ReverseAsync(scope.PostedEntry.Id, new(BusinessDate, "Correction", "link-parity-reverse"), CancellationToken.None);

        var all = await scope.Ledger.ListAsync(CancellationToken.None);
        listed = Assert.Single(all, item => item.Id == scope.PostedEntry.Id);
        single = await scope.Ledger.GetAsync(scope.PostedEntry.Id, CancellationToken.None);
        Assert.Equal(reversal.Id, listed.ReversedByEntryId);
        Assert.Equal(reversal.Id, single.ReversedByEntryId);
        AssertDto(single, listed);
        var listedReversal = Assert.Single(all, item => item.Id == reversal.Id);
        var singleReversal = await scope.Ledger.GetAsync(reversal.Id, CancellationToken.None);
        Assert.Null(listedReversal.ReversedByEntryId);
        Assert.Equal(scope.PostedEntry.Id, listedReversal.ReversesEntryId);
        AssertDto(singleReversal, listedReversal);
        await AssertDurableResult(scope, single);
        await AssertDurableResult(scope, reversal);
        Assert.Equal(1, scope.Audit.FlushCalls);
        Assert.Single(scope.Outbox.Attempts);
        await AssertPlatformCounts(scope, 1, 1);
    }

    [Theory]
    [InlineData("entry-post")]
    [InlineData("entry-reverse")]
    [InlineData("invoice-post")]
    [InlineData("bill-post")]
    [InlineData("customer-payment-post")]
    [InlineData("supplier-payment-post")]
    [InlineData("customer-payment-reverse")]
    [InlineData("supplier-payment-reverse")]
    public async Task IndependentConcurrentPostingAndReversalCannotDuplicateLedgerEntriesAuditEventsOrAllocations(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var first = scope.CreatePeer();
        await using var second = scope.CreatePeer();
        var mutation = await PrepareMutation(scope, operation);
        int entryCount;
        await using (var reader = scope.ReadAccounting()) entryCount = await reader.JournalEntries.CountAsync();
        Task<object> Execute(TestScope worker) => operation switch
        {
            "entry-reverse" => Box(worker.Ledger.ReverseAsync(scope.PostedEntry.Id, new(BusinessDate, " Atomic reversal ", "entry-reverse"), CancellationToken.None)),
            "customer-payment-reverse" or "supplier-payment-reverse" => Box(worker.Payments.ReverseAsync(operation.StartsWith("supplier", StringComparison.Ordinal)
                ? scope.PostedSupplierPayment.Id : scope.PostedPayment.Id, new(BusinessDate, "payment-reverse"), CancellationToken.None)),
            _ => VersionedMutation(worker, operation, Version(scope, operation), CancellationToken.None)
        };
        var started = DateTimeOffset.UtcNow;

        var attempts = await Task.WhenAll(Capture(() => Execute(first)), Capture(() => Execute(second)));

        var successful = attempts.Where(item => item.Result is not null).ToArray();
        Assert.NotEmpty(successful);
        var committed = successful[0].Result!;
        foreach (var attempt in attempts)
        {
            if (attempt.Result is not null) AssertDto(committed, attempt.Result);
            else Assert.True(attempt.Failure is AccountingConcurrencyException or AccountingValidationException,
                $"Unexpected concurrent error: {attempt.Failure?.GetType().Name}");
        }
        await AssertDurableResult(scope, committed);
        await AssertOutcome(scope, operation, committed, started, DateTimeOffset.UtcNow);
        await AssertPlatformCounts(scope, 1, mutation.Events.Length);
        await using (var reader = scope.ReadAccounting())
        {
            Assert.Equal(entryCount + (operation == "entry-post" ? 0 : 1), await reader.JournalEntries.CountAsync());
            var entries = await reader.JournalEntries.AsNoTracking().Include(item => item.Lines).Where(item => item.State == JournalEntryState.Posted).ToArrayAsync();
            Assert.All(entries, item => Assert.Equal(item.Lines.Sum(line => line.BaseDebit), item.Lines.Sum(line => line.BaseCredit)));
        }
        await using (var platform = scope.ReadPlatform())
        {
            var messages = await platform.OutboxMessages.Where(item => item.CompanyId == scope.CompanyId).ToArrayAsync();
            Assert.Equal(mutation.Events.Order(StringComparer.Ordinal), messages.Select(item => item.EventType).Order(StringComparer.Ordinal));
            foreach (var message in messages)
            {
                var attempted = Assert.Single(first.Outbox.Attempts.Concat(second.Outbox.Attempts), item => item.EventId == message.Id);
                Assert.Equal(attempted, JsonSerializer.Deserialize(message.PayloadJson, attempted.GetType()));
                await AssertEvent(scope, committed, attempted);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentPaymentsCannotOverpayTheSameInvoiceOrBill(bool supplier)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var first = scope.CreatePeer();
        await using var second = scope.CreatePeer();
        var amount = supplier ? 40m : 80m;
        var request = PaymentRequest(scope, supplier) with
        {
            Amount = amount,
            Allocations = [new(supplier ? scope.OpenBill.Id : scope.OpenInvoice.Id, amount)]
        };
        var firstPayment = await scope.Payments.CreateAsync(request with { IdempotencyKey = "first-independent-payment" }, CancellationToken.None);
        var secondPayment = await scope.Payments.CreateAsync(request with { IdempotencyKey = "second-independent-payment" }, CancellationToken.None);
        Task<object> Post(TestScope worker, PaymentDto payment) => Box(worker.Payments.PostAsync(payment.Id,
            new(scope.Journal.Id, scope.Bank.Id, supplier ? scope.Payable.Id : scope.Receivable.Id, payment.ConcurrencyStamp, $"post-{payment.Id:N}"), CancellationToken.None));

        var attempts = await Task.WhenAll(Capture(() => Post(first, firstPayment)), Capture(() => Post(second, secondPayment)));

        var winner = Assert.Single(attempts, item => item.Result is not null);
        var loser = Assert.Single(attempts, item => item.Failure is not null);
        Assert.True(loser.Failure is AccountingConcurrencyException or AccountingValidationException);
        await AssertDurableResult(scope, winner.Result!);
        await AssertPlatformCounts(scope, 3, 2);
        await using var database = scope.ReadAccounting();
        var paid = supplier
            ? (await database.SupplierBills.SingleAsync(item => item.Id == scope.OpenBill.Id)).AmountPaid
            : (await database.CustomerInvoices.SingleAsync(item => item.Id == scope.OpenInvoice.Id)).AmountPaid;
        Assert.Equal(amount, paid);
        var documentId = supplier ? scope.OpenBill.Id : scope.OpenInvoice.Id;
        var allocated = await (from allocation in database.PaymentAllocations
                               join payment in database.Payments on allocation.PaymentId equals payment.Id
                               where payment.State == PaymentState.Posted && (allocation.CustomerInvoiceId == documentId || allocation.SupplierBillId == documentId)
                               select allocation.Amount).SumAsync();
        Assert.Equal(paid, allocated);
        Assert.Single(await database.Payments.Where(item => (item.Id == firstPayment.Id || item.Id == secondPayment.Id) && item.State == PaymentState.Posted).ToArrayAsync());
    }

    [Fact]
    public async Task PostingAndReversalKeepExistingFourDecimalBankersRoundingAndBaseCurrencySnapshots()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var request = EntryRequest(scope) with
        {
            ExchangeRate = 1.23456789m,
            Lines = [new(scope.Bank.Id, "Rounded debit", 10.00005m, 0), new(scope.Revenue.Id, "Rounded credit", 0, 10.00005m)]
        };
        var draft = await scope.Ledger.CreateAsync(request, CancellationToken.None);
        var posted = await scope.Ledger.PostAsync(draft.Id, new(draft.ConcurrencyStamp, "rounded-post"), CancellationToken.None);
        var reversal = await scope.Ledger.ReverseAsync(posted.Id, new(BusinessDate, "Rounded reversal", "rounded-reverse"), CancellationToken.None);
        Assert.Equal(1.23456789m, posted.ExchangeRate);
        Assert.Equal(10.0000m, posted.Lines[0].Debit);
        Assert.Equal(decimal.Round(10.00005m * 1.23456789m, 4, MidpointRounding.ToEven), posted.Lines[0].BaseDebit);
        Assert.Equal(posted.Lines[0].BaseDebit, reversal.Lines[0].BaseCredit);
        Assert.Equal(posted.Lines[1].BaseCredit, reversal.Lines[1].BaseDebit);
        AssertDto(posted with { ReversedByEntryId = reversal.Id }, await scope.Ledger.GetAsync(posted.Id, CancellationToken.None));
        await AssertDurableResult(scope, draft with
        {
            State = posted.State,
            PostedAtUtc = posted.PostedAtUtc,
            ConcurrencyStamp = posted.ConcurrencyStamp,
            ReversedByEntryId = reversal.Id
        });
        await AssertDurableResult(scope, reversal);
        await AssertPlatformCounts(scope, 3, 2);
    }

    private sealed record Attempt(object? Result, Exception? Failure);
    private static async Task<Attempt> Capture(Func<Task<object>> operation)
    {
        try { return new(await operation(), null); }
        catch (Exception exception) { return new(null, exception); }
    }

    private sealed record Mutation(string Action, string[] Events, Func<CancellationToken, Task<object>> Execute);

    private static async Task<Mutation> PrepareMutation(TestScope scope, string operation)
    {
        if (operation == "period-reopen")
        {
            await using var database = scope.ReadAccounting();
            var period = await database.FiscalPeriods.SingleAsync(item => item.Id == scope.ControlPeriod.Id);
            period.State = FiscalPeriodState.Closed;
            period.ClosedAtUtc = FixedClock.Now;
            await database.SaveChangesAsync();
        }
        return operation switch
        {
            "account-create" => new("accounting.account.saved", [], token => Box(scope.Configuration.SaveAccountAsync(null,
                new(" NEW ", " New asset ", AccountType.Asset, NormalBalance.Debit), token))),
            "account-update" => Versioned("accounting.account.saved"),
            "period-create" => new("accounting.period.saved", [], token => Box(scope.Configuration.SavePeriodAsync(null,
                new(" FY28 ", new(2028, 1, 1), new(2028, 12, 31)), token))),
            "period-update" => Versioned("accounting.period.saved"),
            "period-close" => Versioned("accounting.period.closed", ["accounting.fiscal-period.closed.v1"]),
            "period-reopen" => Versioned("accounting.period.reopened"),
            "journal-create" => new("accounting.journal.saved", [], token => Box(scope.Configuration.SaveJournalAsync(null, new(" NEW ", " New journal "), token))),
            "journal-update" => Versioned("accounting.journal.saved"),
            "entry-create" => new("accounting.entry.created", [], token => Box(scope.Ledger.CreateAsync(EntryRequest(scope), token))),
            "entry-post" => Versioned("accounting.entry.posted", ["accounting.journal-entry.posted.v1"]),
            "entry-reverse" => new("accounting.entry.reversed", ["accounting.journal-entry.posted.v1"], token => Box(scope.Ledger.ReverseAsync(scope.PostedEntry.Id,
                new(BusinessDate, " Atomic reversal ", "entry-reverse"), token))),
            "invoice-create" => new("accounting.invoice.created", [], token => Box(scope.Invoices.CreateFromSalesOrderAsync(new(scope.SourceSalesOrderId,
                BusinessDate, BusinessDate.AddDays(30), "invoice-create"), token))),
            "invoice-post" => Versioned("accounting.invoice.posted", ["accounting.customer-invoice.posted.v1", "accounting.journal-entry.posted.v1"]),
            "invoice-cancel" => Versioned("accounting.invoice.cancelled"),
            "bill-create" => new("accounting.bill.created", [], token => Box(scope.Bills.CreateFromPurchaseOrderAsync(new(scope.SourcePurchaseOrderId,
                " SUP-NEW ", BusinessDate, BusinessDate.AddDays(30), "bill-create"), token))),
            "bill-post" => Versioned("accounting.bill.posted", ["accounting.supplier-bill.posted.v1", "accounting.journal-entry.posted.v1"]),
            "bill-cancel" => Versioned("accounting.bill.cancelled"),
            "customer-payment-create" or "supplier-payment-create" => new("accounting.payment.created", [], token => Box(scope.Payments.CreateAsync(PaymentRequest(scope,
                operation.StartsWith("supplier", StringComparison.Ordinal)), token))),
            "customer-payment-post" or "supplier-payment-post" => Versioned("accounting.payment.posted", ["accounting.payment.posted.v1", "accounting.journal-entry.posted.v1"]),
            "customer-payment-reverse" or "supplier-payment-reverse" => new("accounting.payment.reversed", ["accounting.payment.reversed.v1", "accounting.journal-entry.posted.v1"],
                token => Box(scope.Payments.ReverseAsync(operation.StartsWith("supplier", StringComparison.Ordinal) ? scope.PostedSupplierPayment.Id : scope.PostedPayment.Id,
                    new(BusinessDate, "payment-reverse"), token))),
            "tax-create" => new("accounting.tax-code.saved", [], token => Box(scope.Controls.SaveTaxCodeAsync(null,
                new(" NEW ", " New tax ", 12.5m, scope.TaxPayable.Id, scope.TaxRecoverable.Id), token))),
            "tax-update" => Versioned("accounting.tax-code.saved"),
            "rate-add" => new("accounting.currency-rate.added", [], token => Box(scope.Controls.AddCurrencyRateAsync(new(" usd ", " eur ", BusinessDate, 0.95m), token))),
            "reconciliation-create" => new("accounting.reconciliation.created", [], token => Box(scope.Controls.CreateReconciliationAsync(
                new(scope.Bank.Id, BusinessDate, 0, 20, [scope.PostedEntry.Id]), token))),
            "reconciliation-finalize" => Versioned("accounting.reconciliation.finalized"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        Mutation Versioned(string action, string[]? events = null) => new(action, events ?? [], token => VersionedMutation(scope, operation, Version(scope, operation), token));
    }

    private static string Version(TestScope scope, string operation) => operation switch
    {
        "account-update" => scope.Bank.ConcurrencyStamp,
        "period-update" or "period-close" or "period-reopen" => scope.ControlPeriod.ConcurrencyStamp,
        "journal-update" => scope.Journal.ConcurrencyStamp,
        "entry-post" => scope.DraftEntry.ConcurrencyStamp,
        "invoice-post" or "invoice-cancel" => scope.Invoice.ConcurrencyStamp,
        "bill-post" or "bill-cancel" => scope.Bill.ConcurrencyStamp,
        "customer-payment-post" => scope.Payment.ConcurrencyStamp,
        "supplier-payment-post" => scope.SupplierPayment.ConcurrencyStamp,
        "tax-update" => scope.Tax.ConcurrencyStamp,
        "reconciliation-finalize" => scope.Reconciliation.ConcurrencyStamp,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static Task<object> VersionedMutation(TestScope scope, string operation, string stamp, CancellationToken token) => operation switch
    {
        "account-update" => Box(scope.Configuration.SaveAccountAsync(scope.Bank.Id, new("1000", " Changed bank ", AccountType.Asset, NormalBalance.Debit, ConcurrencyStamp: stamp), token)),
        "period-update" => Box(scope.Configuration.SavePeriodAsync(scope.ControlPeriod.Id, new(" Changed period ", new(2027, 1, 1), new(2027, 12, 31), stamp), token)),
        "period-close" or "period-reopen" => Box(scope.Configuration.SetPeriodStateAsync(scope.ControlPeriod.Id, operation == "period-close" ? FiscalPeriodState.Closed : FiscalPeriodState.Open, stamp, token)),
        "journal-update" => Box(scope.Configuration.SaveJournalAsync(scope.Journal.Id, new("GENERAL", " Changed journal ", true, stamp), token)),
        "entry-post" => Box(scope.Ledger.PostAsync(scope.DraftEntry.Id, new(stamp, "entry-post"), token)),
        "invoice-post" => Box(scope.Invoices.PostAsync(scope.Invoice.Id, new(scope.Journal.Id, scope.Receivable.Id, scope.Revenue.Id, scope.TaxPayable.Id, stamp, "invoice-post"), token)),
        "invoice-cancel" => Box(scope.Invoices.CancelAsync(scope.Invoice.Id, new(stamp), token)),
        "bill-post" => Box(scope.Bills.PostAsync(scope.Bill.Id, new(scope.Journal.Id, scope.Payable.Id, scope.Expense.Id, scope.TaxRecoverable.Id, stamp, "bill-post"), token)),
        "bill-cancel" => Box(scope.Bills.CancelAsync(scope.Bill.Id, new(stamp), token)),
        "customer-payment-post" => Box(scope.Payments.PostAsync(scope.Payment.Id, new(scope.Journal.Id, scope.Bank.Id, scope.Receivable.Id, stamp, "payment-post"), token)),
        "supplier-payment-post" => Box(scope.Payments.PostAsync(scope.SupplierPayment.Id, new(scope.Journal.Id, scope.Bank.Id, scope.Payable.Id, stamp, "payment-post"), token)),
        "tax-update" => Box(scope.Controls.SaveTaxCodeAsync(scope.Tax.Id, new("VAT", " Changed VAT ", 12.5m, scope.TaxPayable.Id, scope.TaxRecoverable.Id, true, stamp), token)),
        "reconciliation-finalize" => Box(scope.Controls.FinalizeReconciliationAsync(scope.Reconciliation.Id, new(stamp), token)),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static JournalEntryRequest EntryRequest(TestScope scope) => new(scope.Journal.Id, BusinessDate, " Atomic journal entry ", " usd ", 1.25m,
        [new(scope.Bank.Id, " Atomic debit ", 20, 0), new(scope.Revenue.Id, " Atomic credit ", 0, 20)], "entry-create");
    private static CreatePaymentRequest PaymentRequest(TestScope scope, bool supplier) => new(supplier ? PaymentDirection.SupplierPayment : PaymentDirection.CustomerReceipt,
        scope.Invoice.CustomerId, supplier ? "Atomic supplier" : scope.Invoice.CustomerName, BusinessDate, " usd ", 1.25m, supplier ? 55 : 60,
        " Atomic reference ", [new(supplier ? scope.OpenBill.Id : scope.OpenInvoice.Id, supplier ? 55 : 60)], "payment-create");

    private static void AssertClean(TestScope scope)
    {
        Assert.Empty(scope.Database.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Database.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    private static async Task AssertPlatformCounts(TestScope scope, int audits, int events)
    {
        await using var platform = scope.ReadPlatform();
        Assert.Equal(audits, await platform.AuditLogs.CountAsync(item => item.CompanyId == scope.CompanyId));
        Assert.Equal(events, await platform.OutboxMessages.CountAsync(item => item.CompanyId == scope.CompanyId));
    }

    private static JournalEntryDto Dto(JournalEntry value, Guid? reversedBy = null) => new(value.Id, value.Number, value.JournalId, value.BusinessDate,
        value.Description, value.CurrencyCode, value.ExchangeRate, value.State, value.ReversesEntryId, reversedBy, value.CreatedAtUtc, value.PostedAtUtc,
        value.ConcurrencyStamp, value.Lines.OrderBy(item => item.Sequence).Select(item => new JournalEntryLineDto(item.Id, item.Sequence, item.AccountId,
            item.AccountCode, item.AccountName, item.Description, item.Debit, item.Credit, item.BaseDebit, item.BaseCredit)).ToArray());
    private static CustomerInvoiceDto Dto(CustomerInvoice value) => new(value.Id, value.Number, value.SalesOrderId, value.SalesOrderNumber,
        value.CustomerId, value.CustomerName, value.CustomerTaxNumber, value.InvoiceDate, value.DueDate, value.CurrencyCode, value.BaseCurrencyCode,
        value.ExchangeRate, value.Subtotal, value.DiscountTotal, value.TaxTotal, value.Total, value.AmountPaid, Money(value.Total - value.AmountPaid),
        value.State, value.JournalEntryId, value.CreatedAtUtc, value.UpdatedAtUtc, value.ConcurrencyStamp,
        value.Lines.OrderBy(item => item.Sequence).Select(item => new CustomerInvoiceLineDto(item.Id, item.Sequence, item.ProductId, item.ProductSku,
            item.Description, item.UnitCode, item.Quantity, item.UnitPrice, item.DiscountPercent, item.TaxPercent, item.Subtotal, item.DiscountAmount,
            item.TaxAmount, item.Total)).ToArray());
    private static SupplierBillDto Dto(SupplierBill value) => new(value.Id, value.Number, value.SupplierReference, value.PurchaseOrderId,
        value.PurchaseOrderNumber, value.SupplierId, value.SupplierName, value.SupplierTaxNumber, value.BillDate, value.DueDate, value.CurrencyCode,
        value.BaseCurrencyCode, value.ExchangeRate, value.Subtotal, value.DiscountTotal, value.TaxTotal, value.Total, value.AmountPaid,
        Money(value.Total - value.AmountPaid), value.State, value.JournalEntryId, value.CreatedAtUtc, value.UpdatedAtUtc, value.ConcurrencyStamp,
        value.Lines.OrderBy(item => item.Sequence).Select(item => new SupplierBillLineDto(item.Id, item.Sequence, item.ProductId, item.ProductSku,
            item.Description, item.UnitCode, item.Quantity, item.UnitPrice, item.DiscountPercent, item.TaxPercent, item.Subtotal, item.DiscountAmount,
            item.TaxAmount, item.Total)).ToArray());
    private static PaymentDto Dto(Payment value) => new(value.Id, value.Number, value.Direction, value.PartyId, value.PartyName, value.PaymentDate,
        value.CurrencyCode, value.ExchangeRate, value.Amount, Money(value.Allocations.Sum(item => item.Amount)),
        Money(value.Amount - value.Allocations.Sum(item => item.Amount)), value.Reference, value.State, value.JournalEntryId, value.ReversalJournalEntryId,
        value.CreatedAtUtc, value.UpdatedAtUtc, value.ConcurrencyStamp, value.Allocations.Select(item => new PaymentAllocationDto(item.Id,
            item.CustomerInvoiceId ?? item.SupplierBillId!.Value, item.DocumentNumber, item.Amount)).ToArray());
    private static BankReconciliationDto Dto(BankReconciliation value) => new(value.Id, value.Number, value.BankAccountId, value.StatementDate,
        value.OpeningBalance, value.ClosingBalance, Money(value.Lines.Sum(item => item.Amount)), Money(value.ClosingBalance - value.OpeningBalance - value.Lines.Sum(item => item.Amount)),
        value.State, value.CreatedAtUtc, value.ReconciledAtUtc, value.ConcurrencyStamp, value.Lines.OrderBy(item => item.BusinessDate).Select(item =>
            new BankReconciliationLineDto(item.Id, item.JournalEntryId, item.JournalEntryNumber, item.BusinessDate, item.Amount)).ToArray());
    private static decimal Money(decimal value) => decimal.Round(value, 4, MidpointRounding.ToEven);

    private static void AssertDto(object expected, object? actual)
    {
        switch (expected)
        {
            case JournalEntryDto entry:
                var actualEntry = Assert.IsType<JournalEntryDto>(actual);
                Assert.Equal(entry, actualEntry with { Lines = entry.Lines });
                Assert.Equal<JournalEntryLineDto>(entry.Lines, actualEntry.Lines);
                break;
            case CustomerInvoiceDto invoice:
                var actualInvoice = Assert.IsType<CustomerInvoiceDto>(actual);
                Assert.Equal(invoice, actualInvoice with { Lines = invoice.Lines });
                Assert.Equal<CustomerInvoiceLineDto>(invoice.Lines, actualInvoice.Lines);
                break;
            case SupplierBillDto bill:
                var actualBill = Assert.IsType<SupplierBillDto>(actual);
                Assert.Equal(bill, actualBill with { Lines = bill.Lines });
                Assert.Equal<SupplierBillLineDto>(bill.Lines, actualBill.Lines);
                break;
            case PaymentDto payment:
                var actualPayment = Assert.IsType<PaymentDto>(actual);
                Assert.Equal(payment, actualPayment with { Allocations = payment.Allocations });
                Assert.Equal<PaymentAllocationDto>(payment.Allocations.OrderBy(item => item.Id), actualPayment.Allocations.OrderBy(item => item.Id));
                break;
            case BankReconciliationDto reconciliation:
                var actualReconciliation = Assert.IsType<BankReconciliationDto>(actual);
                Assert.Equal(reconciliation, actualReconciliation with { Lines = reconciliation.Lines });
                Assert.Equal<BankReconciliationLineDto>(reconciliation.Lines.OrderBy(item => item.Id), actualReconciliation.Lines.OrderBy(item => item.Id));
                break;
            default: Assert.Equal(expected, actual); break;
        }
    }

    private static async Task AssertDurableResult(TestScope scope, object expected)
    {
        await using var database = scope.ReadAccounting();
        object actual;
        switch (expected)
        {
            case AccountDto value:
                var account = await database.Accounts.AsNoTracking().SingleAsync(item => item.Id == value.Id);
                actual = new AccountDto(account.Id, account.Code, account.Name, account.Type, account.NormalBalance, account.AllowDirectPosting, account.IsActive, account.ConcurrencyStamp);
                break;
            case FiscalPeriodDto value:
                var period = await database.FiscalPeriods.AsNoTracking().SingleAsync(item => item.Id == value.Id);
                actual = new FiscalPeriodDto(period.Id, period.Name, period.StartsOn, period.EndsOn, period.State, period.ClosedAtUtc, period.ConcurrencyStamp);
                break;
            case JournalDto value:
                var journal = await database.Journals.AsNoTracking().SingleAsync(item => item.Id == value.Id);
                actual = new JournalDto(journal.Id, journal.Code, journal.Name, journal.IsActive, journal.ConcurrencyStamp);
                break;
            case JournalEntryDto value:
                var reversedBy = await database.JournalEntries.AsNoTracking().Where(item => item.ReversesEntryId == value.Id).Select(item => (Guid?)item.Id).SingleOrDefaultAsync();
                actual = Dto(await database.JournalEntries.AsNoTracking().Include(item => item.Lines).SingleAsync(item => item.Id == value.Id), reversedBy);
                break;
            case CustomerInvoiceDto value: actual = Dto(await database.CustomerInvoices.AsNoTracking().Include(item => item.Lines).SingleAsync(item => item.Id == value.Id)); break;
            case SupplierBillDto value: actual = Dto(await database.SupplierBills.AsNoTracking().Include(item => item.Lines).SingleAsync(item => item.Id == value.Id)); break;
            case PaymentDto value: actual = Dto(await database.Payments.AsNoTracking().Include(item => item.Allocations).SingleAsync(item => item.Id == value.Id)); break;
            case TaxCodeDto value:
                var tax = await database.TaxCodes.AsNoTracking().SingleAsync(item => item.Id == value.Id);
                actual = new TaxCodeDto(tax.Id, tax.Code, tax.Name, tax.Rate, tax.OutputAccountId, tax.InputAccountId, tax.IsActive, tax.ConcurrencyStamp);
                break;
            case CurrencyRateDto value:
                var rate = await database.CurrencyRates.AsNoTracking().SingleAsync(item => item.Id == value.Id);
                actual = new CurrencyRateDto(rate.Id, rate.BaseCurrencyCode, rate.QuoteCurrencyCode, rate.EffectiveDate, rate.Rate, rate.ConcurrencyStamp);
                break;
            case BankReconciliationDto value: actual = Dto(await database.BankReconciliations.AsNoTracking().Include(item => item.Lines).SingleAsync(item => item.Id == value.Id)); break;
            default: throw new ArgumentOutOfRangeException(nameof(expected));
        }
        AssertDto(expected, actual);
    }

    private static Guid Id(object value) => value switch
    {
        AccountDto item => item.Id,
        FiscalPeriodDto item => item.Id,
        JournalDto item => item.Id,
        JournalEntryDto item => item.Id,
        CustomerInvoiceDto item => item.Id,
        SupplierBillDto item => item.Id,
        PaymentDto item => item.Id,
        TaxCodeDto item => item.Id,
        CurrencyRateDto item => item.Id,
        BankReconciliationDto item => item.Id,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static async Task AssertAuditAndEvents(TestScope scope, Mutation mutation, object result, Guid[] rejectedIds)
    {
        await using var platform = scope.ReadPlatform();
        var audit = await platform.AuditLogs.SingleAsync(item => item.CompanyId == scope.CompanyId);
        Assert.Equal(mutation.Action, audit.Action);
        Assert.Equal(scope.TenantId, audit.TenantId);
        Assert.Equal(scope.CompanyId, audit.CompanyId);
        Assert.Equal(scope.UserId, audit.UserId);
        Assert.Equal(FixedClock.Now, audit.TimestampUtc);
        Assert.Equal("accounting-atomicity-test", audit.CorrelationId);
        Assert.Equal(Id(result).ToString(), audit.EntityId);
        Assert.Equal(result is AccountDto or FiscalPeriodDto or JournalDto ? result.GetType().Name : result.GetType().Name[..^3], audit.EntityType);
        AssertDto(result, JsonSerializer.Deserialize(audit.AfterJson!, result.GetType()));
        var events = await platform.OutboxMessages.Where(item => item.CompanyId == scope.CompanyId).ToArrayAsync();
        Assert.Equal(mutation.Events.Order(StringComparer.Ordinal), events.Select(item => item.EventType).Order(StringComparer.Ordinal));
        foreach (var message in events)
        {
            Assert.DoesNotContain(message.Id, rejectedIds);
            Assert.Equal(scope.TenantId, message.TenantId);
            Assert.Equal(scope.CompanyId, message.CompanyId);
            Assert.Equal(FixedClock.Now, message.OccurredAtUtc);
            Assert.Equal("accounting-atomicity-test", message.CorrelationId);
            var attempted = Assert.Single(scope.Outbox.Attempts, item => item.EventId == message.Id);
            Assert.Equal(attempted, JsonSerializer.Deserialize(message.PayloadJson, attempted.GetType()));
            await AssertEvent(scope, result, attempted);
        }
    }

    private static async Task AssertEvent(TestScope scope, object result, IIntegrationEvent value)
    {
        Assert.Equal(FixedClock.Now, value.OccurredAtUtc);
        IIntegrationEvent expected;
        switch (value)
        {
            case FiscalPeriodClosedEvent:
                var period = Assert.IsType<FiscalPeriodDto>(result);
                expected = new FiscalPeriodClosedEvent(value.EventId, FixedClock.Now, period.Id, period.Name, period.StartsOn, period.EndsOn);
                break;
            case CustomerInvoicePostedEvent:
                var invoice = Assert.IsType<CustomerInvoiceDto>(result);
                expected = new CustomerInvoicePostedEvent(value.EventId, FixedClock.Now, invoice.Id, invoice.Number, invoice.CustomerId, invoice.CurrencyCode, invoice.Total);
                break;
            case SupplierBillPostedEvent:
                var bill = Assert.IsType<SupplierBillDto>(result);
                expected = new SupplierBillPostedEvent(value.EventId, FixedClock.Now, bill.Id, bill.Number, bill.SupplierId, bill.CurrencyCode, bill.Total);
                break;
            case PaymentPostedEvent:
                var posted = Assert.IsType<PaymentDto>(result);
                expected = new PaymentPostedEvent(value.EventId, FixedClock.Now, posted.Id, posted.Number, posted.Direction, posted.PartyId, posted.CurrencyCode, posted.Amount);
                break;
            case PaymentReversedEvent:
                var reversed = Assert.IsType<PaymentDto>(result);
                expected = new PaymentReversedEvent(value.EventId, FixedClock.Now, reversed.Id, reversed.Number);
                break;
            case JournalEntryPostedEvent:
                var entryId = result switch
                {
                    JournalEntryDto entry => entry.Id,
                    CustomerInvoiceDto item => item.JournalEntryId!.Value,
                    SupplierBillDto item => item.JournalEntryId!.Value,
                    PaymentDto item => (item.State == PaymentState.Reversed ? item.ReversalJournalEntryId : item.JournalEntryId)!.Value,
                    _ => throw new ArgumentOutOfRangeException(nameof(result))
                };
                await using (var database = scope.ReadAccounting())
                {
                    var entry = await database.JournalEntries.AsNoTracking().SingleAsync(item => item.Id == entryId);
                    expected = new JournalEntryPostedEvent(value.EventId, FixedClock.Now, entry.Id, entry.Number, entry.BusinessDate, entry.CurrencyCode);
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(value));
        }
        Assert.Equal(expected, value);
    }

    private static async Task AssertOutcome(TestScope scope, string operation, object result, DateTimeOffset started, DateTimeOffset finished)
    {
        switch (result)
        {
            case AccountDto value:
                Assert.Equal(operation == "account-create" ? "NEW" : "1000", value.Code);
                Assert.Equal(operation == "account-create" ? "New asset" : "Changed bank", value.Name);
                Assert.True(value.IsActive && value.AllowDirectPosting);
                Assert.Equal(AccountType.Asset, value.Type);
                Assert.Equal(NormalBalance.Debit, value.NormalBalance);
                break;
            case FiscalPeriodDto value:
                Assert.Equal(operation == "period-close" ? FiscalPeriodState.Closed : FiscalPeriodState.Open, value.State);
                Assert.Equal(operation == "period-close" ? FixedClock.Now : (DateTimeOffset?)null, value.ClosedAtUtc);
                Assert.Equal(operation == "period-create" ? "FY28" : operation == "period-update" ? "Changed period" : "FY27", value.Name);
                break;
            case JournalDto value:
                Assert.Equal(operation == "journal-create" ? "NEW" : "GENERAL", value.Code);
                Assert.Equal(operation == "journal-create" ? "New journal" : "Changed journal", value.Name);
                Assert.True(value.IsActive);
                break;
            case JournalEntryDto value:
                Assert.Equal(operation == "entry-create" ? JournalEntryState.Draft : JournalEntryState.Posted, value.State);
                Assert.Equal(value.Lines.Sum(item => item.Debit), value.Lines.Sum(item => item.Credit));
                Assert.Equal(value.Lines.Sum(item => item.BaseDebit), value.Lines.Sum(item => item.BaseCredit));
                Assert.Equal(1.25m, value.ExchangeRate);
                if (operation == "entry-reverse")
                {
                    Assert.Equal(scope.PostedEntry.Id, value.ReversesEntryId);
                    Assert.Equal("Atomic reversal", value.Description);
                    Assert.Equal(scope.PostedEntry.Lines.Select(item => item.Credit), value.Lines.Select(item => item.Debit));
                    Assert.Equal(scope.PostedEntry.Lines.Select(item => item.BaseCredit), value.Lines.Select(item => item.BaseDebit));
                }
                break;
            case CustomerInvoiceDto value:
                Assert.Equal(operation == "invoice-post" ? CustomerInvoiceState.Posted : operation == "invoice-cancel" ? CustomerInvoiceState.Cancelled : CustomerInvoiceState.Draft, value.State);
                Assert.Equal(operation == "invoice-create" ? 104.5m : 110m, value.Total);
                Assert.Equal(operation == "invoice-create" ? "Source customer" : scope.Invoice.CustomerName, value.CustomerName);
                Assert.Equal(operation == "invoice-create" ? "SOURCE-TAX" : scope.Invoice.CustomerTaxNumber, value.CustomerTaxNumber);
                Assert.Equal("USD", value.CurrencyCode);
                Assert.Equal("USD", value.BaseCurrencyCode);
                Assert.Equal(1.25m, value.ExchangeRate);
                break;
            case SupplierBillDto value:
                Assert.Equal(operation == "bill-post" ? SupplierBillState.Posted : operation == "bill-cancel" ? SupplierBillState.Cancelled : SupplierBillState.Draft, value.State);
                Assert.Equal(operation == "bill-create" ? 49.5m : 55m, value.Total);
                Assert.Equal(operation == "bill-create" ? "Source supplier" : scope.Bill.SupplierName, value.SupplierName);
                Assert.Equal(operation == "bill-create" ? "SOURCE-TAX" : scope.Bill.SupplierTaxNumber, value.SupplierTaxNumber);
                Assert.Equal("USD", value.CurrencyCode);
                Assert.Equal("USD", value.BaseCurrencyCode);
                Assert.Equal(1.25m, value.ExchangeRate);
                break;
            case PaymentDto value:
                var supplier = operation.StartsWith("supplier", StringComparison.Ordinal);
                Assert.Equal(supplier ? PaymentDirection.SupplierPayment : PaymentDirection.CustomerReceipt, value.Direction);
                Assert.Equal(operation.EndsWith("create", StringComparison.Ordinal) ? PaymentState.Draft : operation.EndsWith("post", StringComparison.Ordinal) ? PaymentState.Posted : PaymentState.Reversed, value.State);
                Assert.Equal(supplier ? 55 : 60, value.Amount);
                Assert.Equal(value.Amount, value.AllocatedAmount);
                Assert.Equal(0, value.UnallocatedAmount);
                Assert.Equal(1.25m, value.ExchangeRate);
                if (value.State != PaymentState.Draft)
                {
                    await using var database = scope.ReadAccounting();
                    var reversing = value.State == PaymentState.Reversed;
                    if (supplier)
                    {
                        var seed = reversing ? scope.PaidBill : scope.OpenBill;
                        var actual = Dto(await database.SupplierBills.AsNoTracking().Include(item => item.Lines).SingleAsync(item => item.Id == seed.Id));
                        AssertDto(Dto(seed) with
                        {
                            AmountPaid = reversing ? 0 : 55,
                            AmountDue = reversing ? 55 : 0,
                            State = reversing ? SupplierBillState.Posted : SupplierBillState.Paid,
                            UpdatedAtUtc = actual.UpdatedAtUtc,
                            ConcurrencyStamp = actual.ConcurrencyStamp
                        }, actual);
                        Assert.NotEqual(seed.ConcurrencyStamp, actual.ConcurrencyStamp);
                        Assert.InRange(actual.UpdatedAtUtc, started, finished);
                    }
                    else
                    {
                        var seed = reversing ? scope.PaidInvoice : scope.OpenInvoice;
                        var actual = Dto(await database.CustomerInvoices.AsNoTracking().Include(item => item.Lines).SingleAsync(item => item.Id == seed.Id));
                        AssertDto(Dto(seed) with
                        {
                            AmountPaid = reversing ? 0 : 60,
                            AmountDue = reversing ? 110 : 50,
                            State = reversing ? CustomerInvoiceState.Posted : CustomerInvoiceState.PartiallyPaid,
                            UpdatedAtUtc = actual.UpdatedAtUtc,
                            ConcurrencyStamp = actual.ConcurrencyStamp
                        }, actual);
                        Assert.NotEqual(seed.ConcurrencyStamp, actual.ConcurrencyStamp);
                        Assert.InRange(actual.UpdatedAtUtc, started, finished);
                    }
                }
                break;
            case TaxCodeDto value:
                Assert.Equal(operation == "tax-create" ? "NEW" : "VAT", value.Code);
                Assert.Equal(12.5m, value.Rate);
                Assert.Equal(scope.TaxPayable.Id, value.OutputAccountId);
                Assert.Equal(scope.TaxRecoverable.Id, value.InputAccountId);
                break;
            case CurrencyRateDto value:
                Assert.Equal("USD", value.BaseCurrencyCode);
                Assert.Equal("EUR", value.QuoteCurrencyCode);
                Assert.Equal(0.95m, value.Rate);
                Assert.Equal(BusinessDate, value.EffectiveDate);
                break;
            case BankReconciliationDto value:
                Assert.Equal(operation == "reconciliation-create" ? BankReconciliationState.Draft : BankReconciliationState.Reconciled, value.State);
                Assert.Equal(0, value.Difference);
                Assert.Equal(operation == "reconciliation-create" ? 20 : 0, value.ClearedChange);
                if (operation == "reconciliation-create") Assert.Equal(scope.PostedEntry.Id, Assert.Single(value.Lines).JournalEntryId);
                else Assert.Equal(FixedClock.Now, value.ReconciledAtUtc);
                break;
        }
        if (operation is "entry-create" or "entry-reverse" or "invoice-create" or "bill-create" or "customer-payment-create" or "supplier-payment-create" or "reconciliation-create")
        {
            var number = result switch
            {
                JournalEntryDto item => item.Number,
                CustomerInvoiceDto item => item.Number,
                SupplierBillDto item => item.Number,
                PaymentDto item => item.Number,
                BankReconciliationDto item => item.Number,
                _ => throw new ArgumentOutOfRangeException(nameof(result))
            };
            var prefix = operation switch { "entry-create" => "JE", "entry-reverse" => "RV", "invoice-create" => "INV", "bill-create" => "BILL", "reconciliation-create" => "REC", _ => "PAY" };
            Assert.Matches($"^{prefix}-20261019-[0-9A-F]{{8}}$", number);
        }
    }

    private static DateOnly BusinessDate => new(2026, 10, 19);
    private static string Stamp() => Guid.NewGuid().ToString("N");

    private static async Task<string> Snapshot(TestScope scope)
    {
        await using var database = scope.ReadAccounting();
        await using var platform = scope.ReadPlatform();
        return JsonSerializer.Serialize(new
        {
            Accounts = await database.Accounts.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Periods = await database.FiscalPeriods.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Journals = await database.Journals.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Entries = await database.JournalEntries.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            EntryLines = await database.JournalEntryLines.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Invoices = await database.CustomerInvoices.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            InvoiceLines = await database.CustomerInvoiceLines.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Bills = await database.SupplierBills.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            BillLines = await database.SupplierBillLines.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Payments = await database.Payments.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Allocations = await database.PaymentAllocations.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            TaxCodes = await database.TaxCodes.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Rates = await database.CurrencyRates.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Reconciliations = await database.BankReconciliations.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            ReconciliationLines = await database.BankReconciliationLines.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Sequences = await platform.DocumentSequences.AsNoTracking().Where(item => item.CompanyId == scope.CompanyId).OrderBy(item => item.Key).ToArrayAsync()
        });
    }

    private sealed class TestScope(ServiceProvider provider, AsyncServiceScope services, string connectionString,
        Guid tenantId, Guid companyId, Guid userId) : IAsyncDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid CompanyId { get; } = companyId;
        public Guid UserId { get; } = userId;
        public Account Bank { get; private set; } = null!;
        public Account Receivable { get; private set; } = null!;
        public Account Revenue { get; private set; } = null!;
        public Account TaxPayable { get; private set; } = null!;
        public Account Payable { get; private set; } = null!;
        public Account Expense { get; private set; } = null!;
        public Account TaxRecoverable { get; private set; } = null!;
        public Journal Journal { get; private set; } = null!;
        public FiscalPeriod ControlPeriod { get; private set; } = null!;
        public TaxCode Tax { get; private set; } = null!;
        public CurrencyRate Rate { get; private set; } = null!;
        public BankReconciliation Reconciliation { get; private set; } = null!;
        public JournalEntry DraftEntry { get; private set; } = null!;
        public JournalEntry PostedEntry { get; private set; } = null!;
        public CustomerInvoice Invoice { get; private set; } = null!;
        public CustomerInvoice OpenInvoice { get; private set; } = null!;
        public CustomerInvoice PaidInvoice { get; private set; } = null!;
        public SupplierBill Bill { get; private set; } = null!;
        public SupplierBill OpenBill { get; private set; } = null!;
        public SupplierBill PaidBill { get; private set; } = null!;
        public Payment Payment { get; private set; } = null!;
        public Payment SupplierPayment { get; private set; } = null!;
        public Payment PostedPayment { get; private set; } = null!;
        public Payment PostedSupplierPayment { get; private set; } = null!;
        public Guid SourceSalesOrderId { get; private set; }
        public Guid SourcePurchaseOrderId { get; private set; }
        public IRelationalDatabaseSession Session => services.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        public IBusinessTransactionCoordinator Transactions => services.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        public AccountingDbContext Database => services.ServiceProvider.GetRequiredService<AccountingDbContext>();
        public PlatformDbContext Platform => services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        public ContactsDbContext Contacts => services.ServiceProvider.GetRequiredService<ContactsDbContext>();
        public ProductsDbContext Products => services.ServiceProvider.GetRequiredService<ProductsDbContext>();
        public SalesDbContext Sales => services.ServiceProvider.GetRequiredService<SalesDbContext>();
        public PurchasingDbContext Purchasing => services.ServiceProvider.GetRequiredService<PurchasingDbContext>();
        public IAccountingConfigurationService Configuration => services.ServiceProvider.GetRequiredService<IAccountingConfigurationService>();
        public IGeneralLedgerService Ledger => services.ServiceProvider.GetRequiredService<IGeneralLedgerService>();
        public ICustomerInvoiceService Invoices => services.ServiceProvider.GetRequiredService<ICustomerInvoiceService>();
        public ISupplierBillService Bills => services.ServiceProvider.GetRequiredService<ISupplierBillService>();
        public IPaymentService Payments => services.ServiceProvider.GetRequiredService<IPaymentService>();
        public IFinancialControlsService Controls => services.ServiceProvider.GetRequiredService<IFinancialControlsService>();
        public IContactService ContactService => services.ServiceProvider.GetRequiredService<IContactService>();
        public IProductService ProductService => services.ServiceProvider.GetRequiredService<IProductService>();
        public IQuotationService Quotations => services.ServiceProvider.GetRequiredService<IQuotationService>();
        public ISalesOrderService SalesOrders => services.ServiceProvider.GetRequiredService<ISalesOrderService>();
        public IPurchaseOrderService PurchaseOrders => services.ServiceProvider.GetRequiredService<IPurchaseOrderService>();
        public InjectingAudit Audit => services.ServiceProvider.GetRequiredService<InjectingAudit>();
        public ObservingOutbox Outbox => services.ServiceProvider.GetRequiredService<ObservingOutbox>();
        public OneShotSaveHook SaveHook => services.ServiceProvider.GetRequiredService<OneShotSaveHook>();
        public PlatformDbContext ReadPlatform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId));
        public AccountingDbContext ReadAccounting() => new(new DbContextOptionsBuilder<AccountingDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public ContactsDbContext ReadContacts() => new(new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public ProductsDbContext ReadProducts() => new(new DbContextOptionsBuilder<ProductsDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public SalesDbContext ReadSales() => new(new DbContextOptionsBuilder<SalesDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public PurchasingDbContext ReadPurchasing() => new(new DbContextOptionsBuilder<PurchasingDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public TestScope CreatePeer()
        {
            var peer = Build(connectionString, TenantId, CompanyId, UserId);
            peer.Bank = Bank; peer.Receivable = Receivable; peer.Revenue = Revenue; peer.TaxPayable = TaxPayable;
            peer.Payable = Payable; peer.Expense = Expense; peer.TaxRecoverable = TaxRecoverable; peer.Journal = Journal;
            peer.ControlPeriod = ControlPeriod; peer.Tax = Tax; peer.Rate = Rate; peer.Reconciliation = Reconciliation;
            peer.DraftEntry = DraftEntry; peer.PostedEntry = PostedEntry; peer.Invoice = Invoice; peer.OpenInvoice = OpenInvoice;
            peer.PaidInvoice = PaidInvoice; peer.Bill = Bill; peer.OpenBill = OpenBill; peer.PaidBill = PaidBill;
            peer.Payment = Payment; peer.SupplierPayment = SupplierPayment; peer.PostedPayment = PostedPayment;
            peer.PostedSupplierPayment = PostedSupplierPayment; peer.SourceSalesOrderId = SourceSalesOrderId;
            peer.SourcePurchaseOrderId = SourcePurchaseOrderId;
            return peer;
        }

        public static async Task<TestScope> CreateAsync(string connectionString, string operation = "matrix", Guid? existingTenant = null)
        {
            var scope = Build(connectionString, existingTenant ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            try
            {
                if (existingTenant is null) scope.Platform.Tenants.Add(new Tenant { Id = scope.TenantId, Name = "Accounting atomicity", Slug = $"atomic-{scope.TenantId:N}", TimeZoneId = "UTC", IsActive = true, CreatedAtUtc = FixedClock.Now });
                scope.Platform.Companies.Add(new Company { Id = scope.CompanyId, TenantId = scope.TenantId, Name = "Accounting atomicity", Code = scope.CompanyId.ToString("N")[..20], BaseCurrencyCode = "USD", TimeZoneId = "UTC", IsActive = true });
                scope.Platform.CompanyCurrencies.Add(new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    Code = "USD",
                    Name = "US dollar",
                    Symbol = "$",
                    MinorUnits = 2,
                    IsActive = true,
                    ConcurrencyStamp = Stamp()
                });
                await scope.Platform.SaveChangesAsync();
                scope.Platform.ChangeTracker.Clear();
                Account Account(string code, string name, AccountType type, NormalBalance balance) => new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    Code = code,
                    Name = name,
                    Type = type,
                    NormalBalance = balance,
                    IsActive = true,
                    AllowDirectPosting = true,
                    CreatedAtUtc = FixedClock.Now,
                    UpdatedAtUtc = FixedClock.Now,
                    ConcurrencyStamp = Stamp()
                };
                scope.Bank = Account("1000", "Bank", AccountType.Asset, NormalBalance.Debit);
                scope.Receivable = Account("1100", "Receivable", AccountType.Asset, NormalBalance.Debit);
                scope.Revenue = Account("4000", "Revenue", AccountType.Revenue, NormalBalance.Credit);
                scope.TaxPayable = Account("2100", "Tax payable", AccountType.Liability, NormalBalance.Credit);
                scope.Database.Accounts.AddRange(scope.Bank, scope.Receivable, scope.Revenue, scope.TaxPayable);
                scope.Journal = new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    Code = "GENERAL",
                    Name = "General",
                    CreatedAtUtc = FixedClock.Now,
                    UpdatedAtUtc = FixedClock.Now,
                    ConcurrencyStamp = Stamp()
                };
                scope.Database.Journals.Add(scope.Journal);
                scope.Database.FiscalPeriods.Add(new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    Name = "FY26",
                    StartsOn = new(2026, 1, 1),
                    EndsOn = new(2026, 12, 31),
                    State = FiscalPeriodState.Open,
                    CreatedAtUtc = FixedClock.Now,
                    ConcurrencyStamp = Stamp()
                });
                scope.Invoice = new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    Number = "INV-SEED",
                    SalesOrderId = Guid.NewGuid(),
                    SalesOrderNumber = "SO-SEED",
                    CustomerId = Guid.NewGuid(),
                    CustomerName = "Atomic customer",
                    CustomerTaxNumber = "TEST-TAX",
                    InvoiceDate = BusinessDate,
                    DueDate = BusinessDate.AddDays(30),
                    CurrencyCode = "USD",
                    BaseCurrencyCode = "USD",
                    ExchangeRate = 1.25m,
                    Subtotal = 100,
                    DiscountTotal = 0,
                    TaxTotal = 10,
                    Total = 110,
                    AmountPaid = 0,
                    State = operation == "customer-payment-post" ? CustomerInvoiceState.Posted : CustomerInvoiceState.Draft,
                    CreatedAtUtc = FixedClock.Now,
                    UpdatedAtUtc = FixedClock.Now,
                    ConcurrencyStamp = Stamp()
                };
                scope.Invoice.Lines.Add(new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    CustomerInvoiceId = scope.Invoice.Id,
                    Sequence = 1,
                    ProductId = Guid.NewGuid(),
                    ProductSku = "SKU-SEED",
                    Description = "Atomic item",
                    UnitCode = "EA",
                    Quantity = 1,
                    UnitPrice = 100,
                    TaxPercent = 10,
                    Subtotal = 100,
                    TaxAmount = 10,
                    Total = 110
                });
                if (operation == "customer-payment-post")
                {
                    var entry = new JournalEntry
                    {
                        Id = Guid.NewGuid(),
                        TenantId = scope.TenantId,
                        CompanyId = scope.CompanyId,
                        Number = "JE-SEED",
                        JournalId = scope.Journal.Id,
                        BusinessDate = BusinessDate,
                        Description = "Posted invoice",
                        CurrencyCode = "USD",
                        ExchangeRate = 1.25m,
                        State = JournalEntryState.Posted,
                        CreatedAtUtc = FixedClock.Now,
                        PostedAtUtc = FixedClock.Now,
                        ConcurrencyStamp = Stamp()
                    };
                    void AddLine(Account account, int sequence, decimal debit, decimal credit) => entry.Lines.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        TenantId = scope.TenantId,
                        CompanyId = scope.CompanyId,
                        JournalEntryId = entry.Id,
                        Sequence = sequence,
                        AccountId = account.Id,
                        AccountCode = account.Code,
                        AccountName = account.Name,
                        Description = account.Name,
                        Debit = debit,
                        Credit = credit,
                        BaseDebit = debit * 1.25m,
                        BaseCredit = credit * 1.25m
                    });
                    AddLine(scope.Receivable, 1, 110, 0);
                    AddLine(scope.Revenue, 2, 0, 100);
                    AddLine(scope.TaxPayable, 3, 0, 10);
                    scope.Database.JournalEntries.Add(entry);
                    scope.Invoice.JournalEntryId = entry.Id;
                    scope.Payment = new()
                    {
                        Id = Guid.NewGuid(),
                        TenantId = scope.TenantId,
                        CompanyId = scope.CompanyId,
                        Number = "PAY-SEED",
                        Direction = PaymentDirection.CustomerReceipt,
                        PartyId = scope.Invoice.CustomerId,
                        PartyName = scope.Invoice.CustomerName,
                        PaymentDate = BusinessDate,
                        CurrencyCode = "USD",
                        ExchangeRate = 1.25m,
                        Amount = 60,
                        Reference = "TEST-RECEIPT",
                        State = PaymentState.Draft,
                        IdempotencyKey = "seed-payment",
                        CreatedAtUtc = FixedClock.Now,
                        UpdatedAtUtc = FixedClock.Now,
                        ConcurrencyStamp = Stamp()
                    };
                    scope.Payment.Allocations.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        TenantId = scope.TenantId,
                        CompanyId = scope.CompanyId,
                        PaymentId = scope.Payment.Id,
                        CustomerInvoiceId = scope.Invoice.Id,
                        DocumentNumber = scope.Invoice.Number,
                        Amount = 60
                    });
                    scope.Database.Payments.Add(scope.Payment);
                }
                scope.Database.CustomerInvoices.Add(scope.Invoice);
                await scope.Database.SaveChangesAsync();
                scope.Database.ChangeTracker.Clear();
                if (operation == "matrix") await scope.SeedFullMatrix();
                return scope;
            }
            catch { await scope.DisposeAsync(); throw; }
        }

        private async Task SeedFullMatrix()
        {
            Account Account(string code, string name, AccountType type, NormalBalance balance) => new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Code = code,
                Name = name,
                Type = type,
                NormalBalance = balance,
                IsActive = true,
                AllowDirectPosting = true,
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            Payable = Account("2000", "Payable", AccountType.Liability, NormalBalance.Credit);
            Expense = Account("5000", "Expense", AccountType.Expense, NormalBalance.Debit);
            TaxRecoverable = Account("1200", "Tax recoverable", AccountType.Asset, NormalBalance.Debit);
            Database.Accounts.AddRange(Payable, Expense, TaxRecoverable);
            ControlPeriod = new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Name = "FY27",
                StartsOn = new(2027, 1, 1),
                EndsOn = new(2027, 12, 31),
                State = FiscalPeriodState.Open,
                CreatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            Database.FiscalPeriods.Add(ControlPeriod);
            Tax = new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Code = "VAT",
                Name = "VAT",
                Rate = 10,
                OutputAccountId = TaxPayable.Id,
                InputAccountId = TaxRecoverable.Id,
                IsActive = true,
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            Rate = new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                BaseCurrencyCode = "USD",
                QuoteCurrencyCode = "EUR",
                EffectiveDate = BusinessDate.AddDays(-1),
                Rate = 0.9m,
                CreatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            Reconciliation = new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Number = "REC-SEED",
                BankAccountId = Bank.Id,
                StatementDate = BusinessDate.AddDays(-1),
                OpeningBalance = 0,
                ClosingBalance = 0,
                State = BankReconciliationState.Draft,
                CreatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            Database.AddRange(Tax, Rate, Reconciliation);
            DraftEntry = NewEntry("JE-DRAFT", JournalEntryState.Draft, 20, Bank, Revenue);
            PostedEntry = NewEntry("JE-POSTED", JournalEntryState.Posted, 20, Bank, Revenue);
            Database.JournalEntries.AddRange(DraftEntry, PostedEntry);
            OpenInvoice = NewInvoice("INV-OPEN", 0);
            PaidInvoice = NewInvoice("INV-PAID", 60);
            Database.CustomerInvoices.AddRange(OpenInvoice, PaidInvoice);
            Bill = NewBill("BILL-DRAFT", SupplierBillState.Draft, 0);
            OpenBill = NewBill("BILL-OPEN", SupplierBillState.Posted, 0);
            PaidBill = NewBill("BILL-PAID", SupplierBillState.Paid, 55);
            Database.SupplierBills.AddRange(Bill, OpenBill, PaidBill);
            Payment = NewPayment("PAY-DRAFT", PaymentDirection.CustomerReceipt, PaymentState.Draft, OpenInvoice.Id, OpenInvoice.Number, 60);
            SupplierPayment = NewPayment("SUPPAY-DRAFT", PaymentDirection.SupplierPayment, PaymentState.Draft, OpenBill.Id, OpenBill.Number, 55);
            PostedPayment = NewPayment("PAY-POSTED", PaymentDirection.CustomerReceipt, PaymentState.Posted, PaidInvoice.Id, PaidInvoice.Number, 60);
            PostedSupplierPayment = NewPayment("SUPPAY-POSTED", PaymentDirection.SupplierPayment, PaymentState.Posted, PaidBill.Id, PaidBill.Number, 55);
            Database.Payments.AddRange(Payment, SupplierPayment, PostedPayment, PostedSupplierPayment);
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
            await SeedPublicSourceDocuments();
        }

        private JournalEntry NewEntry(string number, JournalEntryState state, decimal amount, Account debit, Account credit)
        {
            var entry = new JournalEntry
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Number = number,
                JournalId = Journal.Id,
                BusinessDate = BusinessDate,
                Description = number,
                CurrencyCode = "USD",
                ExchangeRate = 1.25m,
                State = state,
                CreatedAtUtc = FixedClock.Now,
                PostedAtUtc = state == JournalEntryState.Posted ? FixedClock.Now : null,
                ConcurrencyStamp = Stamp()
            };
            entry.Lines.Add(new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                JournalEntryId = entry.Id,
                Sequence = 1,
                AccountId = debit.Id,
                AccountCode = debit.Code,
                AccountName = debit.Name,
                Description = debit.Name,
                Debit = amount,
                BaseDebit = amount * 1.25m
            });
            entry.Lines.Add(new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                JournalEntryId = entry.Id,
                Sequence = 2,
                AccountId = credit.Id,
                AccountCode = credit.Code,
                AccountName = credit.Name,
                Description = credit.Name,
                Credit = amount,
                BaseCredit = amount * 1.25m
            });
            return entry;
        }

        private CustomerInvoice NewInvoice(string number, decimal paid)
        {
            var invoice = new CustomerInvoice
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Number = number,
                SalesOrderId = Guid.NewGuid(),
                SalesOrderNumber = $"SO-{number}",
                CustomerId = Invoice.CustomerId,
                CustomerName = Invoice.CustomerName,
                CustomerTaxNumber = Invoice.CustomerTaxNumber,
                InvoiceDate = BusinessDate,
                DueDate = BusinessDate.AddDays(30),
                CurrencyCode = "USD",
                BaseCurrencyCode = "USD",
                ExchangeRate = 1.25m,
                Subtotal = 100,
                TaxTotal = 10,
                Total = 110,
                AmountPaid = paid,
                State = paid == 0 ? CustomerInvoiceState.Posted : CustomerInvoiceState.PartiallyPaid,
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            invoice.Lines.Add(new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                CustomerInvoiceId = invoice.Id,
                Sequence = 1,
                ProductId = Guid.NewGuid(),
                ProductSku = "SKU-SEED",
                Description = "Atomic item",
                UnitCode = "EA",
                Quantity = 1,
                UnitPrice = 100,
                TaxPercent = 10,
                Subtotal = 100,
                TaxAmount = 10,
                Total = 110
            });
            return invoice;
        }

        private SupplierBill NewBill(string number, SupplierBillState state, decimal paid)
        {
            var bill = new SupplierBill
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Number = number,
                SupplierReference = $"SUP-{number}",
                PurchaseOrderId = Guid.NewGuid(),
                PurchaseOrderNumber = $"PO-{number}",
                SupplierId = Invoice.CustomerId,
                SupplierName = "Atomic supplier",
                SupplierTaxNumber = "SUP-TAX",
                BillDate = BusinessDate,
                DueDate = BusinessDate.AddDays(30),
                CurrencyCode = "USD",
                BaseCurrencyCode = "USD",
                ExchangeRate = 1.25m,
                Subtotal = 50,
                TaxTotal = 5,
                Total = 55,
                AmountPaid = paid,
                State = state,
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            bill.Lines.Add(new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                SupplierBillId = bill.Id,
                Sequence = 1,
                ProductId = Guid.NewGuid(),
                ProductSku = "SKU-BILL",
                Description = "Atomic materials",
                UnitCode = "EA",
                Quantity = 1,
                UnitPrice = 50,
                TaxPercent = 10,
                Subtotal = 50,
                TaxAmount = 5,
                Total = 55
            });
            return bill;
        }

        private Payment NewPayment(string number, PaymentDirection direction, PaymentState state, Guid documentId, string documentNumber, decimal amount)
        {
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Number = number,
                Direction = direction,
                PartyId = Invoice.CustomerId,
                PartyName = direction == PaymentDirection.CustomerReceipt ? Invoice.CustomerName : "Atomic supplier",
                PaymentDate = BusinessDate,
                CurrencyCode = "USD",
                ExchangeRate = 1.25m,
                Amount = amount,
                Reference = number,
                State = state,
                IdempotencyKey = $"create-{number}",
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            payment.Allocations.Add(new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                PaymentId = payment.Id,
                CustomerInvoiceId = direction == PaymentDirection.CustomerReceipt ? documentId : null,
                SupplierBillId = direction == PaymentDirection.SupplierPayment ? documentId : null,
                DocumentNumber = documentNumber,
                Amount = amount
            });
            if (state == PaymentState.Posted)
            {
                var entry = NewEntry($"JE-{number}", JournalEntryState.Posted, amount,
                    direction == PaymentDirection.CustomerReceipt ? Bank : Payable,
                    direction == PaymentDirection.CustomerReceipt ? Receivable : Bank);
                Database.JournalEntries.Add(entry);
                payment.JournalEntryId = entry.Id;
                payment.PostingKey = $"post-{number}";
            }
            return payment;
        }

        private async Task SeedPublicSourceDocuments()
        {
            Contacts.Contacts.Add(new()
            {
                Id = Invoice.CustomerId,
                TenantId = TenantId,
                CompanyId = CompanyId,
                Kind = ContactKind.Organization,
                DisplayName = "Atomic partner",
                OrganizationName = "Atomic partner",
                TaxNumber = "SOURCE-TAX",
                IsCustomer = true,
                IsSupplier = true,
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            });
            await Contacts.SaveChangesAsync();
            Contacts.ChangeTracker.Clear();
            var product = new Product
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Type = ProductType.Product,
                Sku = "SOURCE-SKU",
                Name = "Source item",
                SalePrice = 100,
                Cost = 50,
                CurrencyCode = "USD",
                SalesUnitCode = "EA",
                PurchaseUnitCode = "EA",
                TracksInventory = true,
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            Products.Products.Add(product);
            await Products.SaveChangesAsync();
            Products.ChangeTracker.Clear();
            var quote = new Quotation
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Number = "QUO-SOURCE",
                CustomerId = Invoice.CustomerId,
                CustomerName = "Source customer",
                CurrencyCode = "USD",
                BaseCurrencyCode = "USD",
                ExchangeRate = 1.25m,
                QuotationDate = BusinessDate,
                State = QuotationState.Accepted,
                Subtotal = 100,
                DiscountTotal = 5,
                TaxTotal = 9.5m,
                Total = 104.5m,
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            var sales = new SalesOrder
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                SourceQuotationId = quote.Id,
                Number = "SO-SOURCE",
                CustomerId = Invoice.CustomerId,
                CustomerName = "Source customer",
                CustomerTaxNumber = "SOURCE-TAX",
                CurrencyCode = "USD",
                BaseCurrencyCode = "USD",
                ExchangeRate = 1.25m,
                OrderDate = BusinessDate,
                State = SalesOrderState.Confirmed,
                Subtotal = 100,
                DiscountTotal = 5,
                TaxTotal = 9.5m,
                Total = 104.5m,
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            sales.Lines.Add(new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                SalesOrderId = sales.Id,
                Sequence = 1,
                ProductId = product.Id,
                ProductSku = product.Sku,
                Description = "Source sales line",
                UnitCode = "EA",
                Quantity = 1,
                UnitPrice = 100,
                DiscountPercent = 5,
                TaxPercent = 10,
                Subtotal = 100,
                DiscountAmount = 5,
                TaxAmount = 9.5m,
                Total = 104.5m
            });
            SourceSalesOrderId = sales.Id;
            Sales.Quotations.Add(quote);
            Sales.SalesOrders.Add(sales);
            await Sales.SaveChangesAsync();
            Sales.ChangeTracker.Clear();
            var purchase = new PurchaseOrder
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Number = "PO-SOURCE",
                SupplierId = Invoice.CustomerId,
                SupplierName = "Source supplier",
                SupplierTaxNumber = "SOURCE-TAX",
                CurrencyCode = "USD",
                BaseCurrencyCode = "USD",
                ExchangeRate = 1.25m,
                OrderDate = BusinessDate,
                State = PurchaseOrderState.Confirmed,
                Subtotal = 50,
                DiscountTotal = 5,
                TaxTotal = 4.5m,
                Total = 49.5m,
                CreatedAtUtc = FixedClock.Now,
                UpdatedAtUtc = FixedClock.Now,
                ConcurrencyStamp = Stamp()
            };
            purchase.Lines.Add(new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                PurchaseOrderId = purchase.Id,
                Sequence = 1,
                ProductId = product.Id,
                ProductSku = product.Sku,
                Description = "Source purchase line",
                UnitCode = "EA",
                Quantity = 1,
                UnitPrice = 50,
                DiscountPercent = 10,
                TaxPercent = 10,
                Subtotal = 50,
                DiscountAmount = 5,
                TaxAmount = 4.5m,
                Total = 49.5m
            });
            SourcePurchaseOrderId = purchase.Id;
            Purchasing.PurchaseOrders.Add(purchase);
            await Purchasing.SaveChangesAsync();
            Purchasing.ChangeTracker.Clear();
        }

        private static TestScope Build(string connectionString, Guid tenant, Guid company, Guid user)
        {
            var registry = new IntegrationEventRegistry();
            new AccountingModule().ConfigureIntegrationEvents(registry);
            new SalesModule().ConfigureIntegrationEvents(registry);
            new PurchasingModule().ConfigureIntegrationEvents(registry);
            var registrations = new ServiceCollection();
            registrations.AddSingleton<ITenantContext>(new TestTenant(tenant));
            registrations.AddSingleton<ICompanyContext>(new TestCompany(company));
            registrations.AddSingleton<IClock>(new FixedClock());
            registrations.AddSingleton<IIntegrationEventRegistry>(registry);
            registrations.AddSingleton<IHttpContextAccessor>(new TestHttpContextAccessor
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test")), TraceIdentifier = "accounting-atomicity-test" }
            });
            registrations.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connectionString));
            registrations.AddDbContext<PlatformDbContext>((provider, options) => options.UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString
            }).Build();
            new ContactsModule().ConfigureServices(registrations, configuration);
            new ProductsModule().ConfigureServices(registrations, configuration);
            new SalesModule().ConfigureServices(registrations, configuration);
            new PurchasingModule().ConfigureServices(registrations, configuration);
            new AccountingModule().ConfigureServices(registrations, configuration);
            registrations.AddScoped<OneShotSaveHook>();
            registrations.AddDbContext<AccountingDbContext>((provider, options) => options.AddInterceptors(provider.GetRequiredService<OneShotSaveHook>()));
            registrations.AddScoped<IBusinessTransactionCoordinator, BusinessTransactionCoordinator>();
            registrations.AddScoped<AuditLogService>();
            registrations.AddScoped<InjectingAudit>();
            registrations.AddScoped<IAuditLogWriter>(provider => provider.GetRequiredService<InjectingAudit>());
            registrations.AddScoped<PostgresOutboxWriter>();
            registrations.AddScoped<ObservingOutbox>();
            registrations.AddScoped<IOutboxWriter>(provider => provider.GetRequiredService<ObservingOutbox>());
            registrations.AddScoped<ISequenceService, PostgresSequenceService>();
            registrations.AddScoped<ICurrencyConfigurationService, CurrencyConfigurationService>();
            var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            return new(provider, provider.CreateAsyncScope(), connectionString, tenant, company, user);
        }

        public async ValueTask DisposeAsync() { await services.DisposeAsync(); await provider.DisposeAsync(); }
    }

    private sealed class InjectingAudit(AuditLogService realAudit) : IAuditLogWriter
    {
        public int FlushCalls { get; private set; }
        public bool FailAfterFlush { get; set; }
        public Action? AfterFlush { get; set; }
        public void Append(AuditWriteEntry entry) => realAudit.Append(entry);
        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            await realAudit.FlushAsync(cancellationToken);
            AfterFlush?.Invoke();
            if (FailAfterFlush) { FailAfterFlush = false; throw new InjectedAuditException(); }
        }
    }

    private sealed class ObservingOutbox(PostgresOutboxWriter realOutbox) : IOutboxWriter
    {
        public List<IIntegrationEvent> Attempts { get; } = [];
        public int? FailAfterAdd { get; set; }
        public void Add<TEvent>(TEvent value) where TEvent : IIntegrationEvent
        {
            realOutbox.Add(value);
            Attempts.Add(value);
            if (Attempts.Count == FailAfterAdd) { FailAfterAdd = null; throw new InjectedOutboxException(); }
        }
    }
    private sealed class OneShotSaveHook : SaveChangesInterceptor
    {
        public Func<AccountingDbContext, CancellationToken, Task>? BeforeSave { get; set; }
        public Exception? LastFailure { get; private set; }
        public bool Executed { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is { } callback) { BeforeSave = null; Executed = true; await callback((AccountingDbContext)eventData.Context!, cancellationToken); }
            return result;
        }
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        { LastFailure = eventData.Exception; return Task.CompletedTask; }
        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(ConcurrencyExceptionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { LastFailure = eventData.Exception; return ValueTask.FromResult(result); }
    }
    private sealed class InjectedAuditException : Exception;
    private sealed class InjectedOutboxException : Exception;
    private sealed class TestHttpContextAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class TestTenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class TestCompany(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }
}

public sealed class AtomicAccountingPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;
    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("accounting_atomicity_tests");
        ConnectionString = _postgres.GetConnectionString();
        await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options);
        await platform.Database.MigrateAsync();
        await using var contacts = new ContactsDbContext(new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "contacts")).Options);
        await contacts.Database.MigrateAsync();
        await using var products = new ProductsDbContext(new DbContextOptionsBuilder<ProductsDbContext>().UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "products")).Options);
        await products.Database.MigrateAsync();
        await using var sales = new SalesDbContext(new DbContextOptionsBuilder<SalesDbContext>().UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "sales")).Options);
        await sales.Database.MigrateAsync();
        await using var purchasing = new PurchasingDbContext(new DbContextOptionsBuilder<PurchasingDbContext>().UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "purchasing")).Options);
        await purchasing.Database.MigrateAsync();
        await using var accounting = new AccountingDbContext(new DbContextOptionsBuilder<AccountingDbContext>().UseNpgsql(ConnectionString,
            options => options.MigrationsHistoryTable("__EFMigrationsHistory", "accounting")).Options);
        await accounting.Database.MigrateAsync();
    }
    public async Task DisposeAsync() { if (_postgres is not null) await _postgres.DisposeAsync(); }
}
