using System.Data.Common;
using Erp.Abstractions.Context;
using Erp.Modules.Accounting;
using Erp.Modules.Accounting.Contracts;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class HistoricalAgingPostgresTests(HistoricalAgingPostgresFixture fixture)
    : IClassFixture<HistoricalAgingPostgresFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettledDocumentsRetainDatedPartialPaymentsAndReversalHistory(bool customer)
    {
        var scope = NewScope();
        var documentId = Guid.NewGuid();
        var partyId = Guid.NewGuid();
        var day = new DateOnly(2026, 1, 1);
        await using (var seed = Open(scope))
        {
            AddDocument(seed, scope, customer, documentId, partyId, day, day, "USD", 100, 100);
            var reversal = AddReversal(seed, scope, day.AddDays(2));
            AddPayment(seed, scope, customer, documentId, partyId, day.AddDays(1), 40, PaymentState.Reversed, reversal);
            AddPayment(seed, scope, customer, documentId, partyId, day.AddDays(3), 100, PaymentState.Posted);
            AddPayment(seed, scope, customer, documentId, partyId, day, 80, PaymentState.Draft);
            AddPayment(seed, scope, customer, null, partyId, day, 500, PaymentState.Posted);
            await seed.SaveChangesAsync();
        }

        var commands = new ReadCommands();
        await using var read = Open(scope, commands);
        var reporting = new AccountingReportingData(read);
        foreach (var (offset, expected) in new[] { (0, 100m), (1, 60m), (2, 100m), (3, 0m) })
        {
            var before = commands.Count;
            var rows = await Aging(reporting, customer, day.AddDays(offset));
            Assert.Equal(1, commands.Count - before);
            if (expected == 0) Assert.Empty(rows);
            else
            {
                var row = Assert.Single(rows);
                Assert.Equal((partyId, "USD", expected), (row.PartyId, row.CurrencyCode, row.Total));
                Assert.Equal(expected, row.Current + row.Days1To30 + row.Days31To60 + row.Days61To90 + row.Over90);
            }
        }
        Assert.Empty(read.ChangeTracker.Entries());
        Assert.Equal(100m, customer
            ? await read.CustomerInvoices.Where(x => x.Id == documentId).Select(x => x.AmountPaid).SingleAsync()
            : await read.SupplierBills.Where(x => x.Id == documentId).Select(x => x.AmountPaid).SingleAsync());
        Assert.Equal(4, await read.Payments.CountAsync());
        Assert.Equal(3, await read.PaymentAllocations.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BucketEdgesCurrenciesDocumentStatesAndBothScopeDimensionsRemainSeparate(bool customer)
    {
        var scope = NewScope();
        var partyId = Guid.NewGuid();
        var day = new DateOnly(2026, 7, 1);
        await using (var seed = Open(scope))
        {
            var offsets = new[] { -1, 0, 1, 30, 31, 60, 61, 90, 91 };
            for (var index = 0; index < offsets.Length; index++)
                AddDocument(seed, scope, customer, Guid.NewGuid(), partyId, day.AddDays(-100), day.AddDays(-offsets[index]), "USD", index + 1, 0);
            AddDocument(seed, scope, customer, Guid.NewGuid(), partyId, day, day, "EUR", 200, 0);
            AddDocument(seed, scope, customer, Guid.NewGuid(), partyId, day.AddDays(1), day.AddDays(1), "USD", 1000, 0);
            AddDocument(seed, scope, customer, Guid.NewGuid(), partyId, day, day, "USD", 1000, 0, draft: true);
            AddDocument(seed, scope, customer, Guid.NewGuid(), partyId, day, day, "USD", 1000, 0, cancelled: true);
            await seed.SaveChangesAsync();
        }
        foreach (var foreign in new[] { (scope.Tenant, Guid.NewGuid()), (Guid.NewGuid(), scope.Company) })
        {
            await using var seed = Open(foreign);
            AddDocument(seed, foreign, customer, Guid.NewGuid(), partyId, day, day, "USD", 9999, 0);
            await seed.SaveChangesAsync();
        }

        await using var read = Open(scope);
        var rows = await Aging(new(read), customer, day);
        Assert.Equal(2, rows.Count);
        var usd = Assert.Single(rows, x => x.CurrencyCode == "USD");
        Assert.Equal((3m, 7m, 11m, 15m, 9m, 45m),
            (usd.Current, usd.Days1To30, usd.Days31To60, usd.Days61To90, usd.Over90, usd.Total));
        var eur = Assert.Single(rows, x => x.CurrencyCode == "EUR");
        Assert.Equal((200m, 200m), (eur.Current, eur.Total));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignAllocationsCannotReduceLocalDocumentsAndOnlyAllocatedAmountsReduceAging(bool customer)
    {
        var scope = NewScope();
        var documentId = Guid.NewGuid();
        var partyId = Guid.NewGuid();
        var day = new DateOnly(2026, 1, 1);
        await using (var seed = Open(scope))
        {
            AddDocument(seed, scope, customer, documentId, partyId, day, day, "USD", 100, 20);
            AddPayment(seed, scope, customer, documentId, partyId, day, 20, PaymentState.Posted, paymentAmount: 90);
            await seed.SaveChangesAsync();
        }
        foreach (var foreign in new[] { (scope.Tenant, Guid.NewGuid()), (Guid.NewGuid(), scope.Company) })
        {
            await using var seed = Open(foreign);
            // The allocation has a valid foreign payment FK but deliberately targets a local document ID.
            AddPayment(seed, foreign, customer, documentId, partyId, day, 99, PaymentState.Posted);
            await seed.SaveChangesAsync();
        }
        await using var read = Open(scope);
        Assert.Equal(80m, Assert.Single(await Aging(new(read), customer, day)).Total);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompensatingJournalBusinessDateIsIndependentOfOriginalPaymentDate(bool customer)
    {
        var scope = NewScope();
        var day = new DateOnly(2026, 1, 1);
        var documentId = Guid.NewGuid();
        var partyId = Guid.NewGuid();
        await using (var seed = Open(scope))
        {
            AddDocument(seed, scope, customer, documentId, partyId, day, day, "USD", 100, 0);
            var reversal = AddReversal(seed, scope, day.AddDays(1));
            AddPayment(seed, scope, customer, documentId, partyId, day.AddDays(2), 40, PaymentState.Reversed, reversal);
            await seed.SaveChangesAsync();
        }
        await using var read = Open(scope);
        var reports = new AccountingReportingData(read);
        Assert.Equal(100m, Assert.Single(await Aging(reports, customer, day)).Total);
        Assert.Equal(140m, Assert.Single(await Aging(reports, customer, day.AddDays(1))).Total);
        Assert.Equal(100m, Assert.Single(await Aging(reports, customer, day.AddDays(2))).Total);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidCalendarEdgesDoNotUnderflowAndDefaultAndCancellationAreRejected(bool customer)
    {
        var scope = NewScope();
        var first = DateOnly.MinValue.AddDays(1);
        await using (var seed = Open(scope))
        {
            AddDocument(seed, scope, customer, Guid.NewGuid(), Guid.NewGuid(), first, first, "USD", 10, 0);
            await seed.SaveChangesAsync();
        }
        await using var read = Open(scope);
        var reports = new AccountingReportingData(read);
        Assert.Equal(10m, Assert.Single(await Aging(reports, customer, first)).Current);
        Assert.Equal(10m, Assert.Single(await Aging(reports, customer, DateOnly.MaxValue)).Over90);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Aging(reports, customer, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => customer
            ? reports.ReceivablesAgingAsync(first, cancelled.Token)
            : reports.PayablesAgingAsync(first, cancelled.Token));
    }

    private AccountingDbContext Open((Guid Tenant, Guid Company) scope, ReadCommands? commands = null)
    {
        var builder = new DbContextOptionsBuilder<AccountingDbContext>().UseNpgsql(fixture.ConnectionString);
        if (commands is not null) builder.AddInterceptors(commands);
        return new(builder.Options, new Tenant(scope.Tenant), new Company(scope.Company));
    }

    private static Task<IReadOnlyList<AgingReportRow>> Aging(AccountingReportingData data, bool customer, DateOnly day) => customer
        ? data.ReceivablesAgingAsync(day, CancellationToken.None)
        : data.PayablesAgingAsync(day, CancellationToken.None);

    private static void AddDocument(AccountingDbContext db, (Guid Tenant, Guid Company) scope, bool customer,
        Guid id, Guid partyId, DateOnly date, DateOnly due, string currency, decimal total, decimal paid,
        bool draft = false, bool cancelled = false)
    {
        var number = id.ToString("N");
        if (customer)
            db.CustomerInvoices.Add(new CustomerInvoice
            {
                Id = id,
                TenantId = scope.Tenant,
                CompanyId = scope.Company,
                Number = number,
                SalesOrderId = Guid.NewGuid(),
                SalesOrderNumber = number,
                CustomerId = partyId,
                CustomerName = "Aging party",
                InvoiceDate = date,
                DueDate = due,
                CurrencyCode = currency,
                BaseCurrencyCode = "USD",
                ExchangeRate = 1,
                Subtotal = total,
                Total = total,
                AmountPaid = paid,
                State = draft ? CustomerInvoiceState.Draft : cancelled ? CustomerInvoiceState.Cancelled : paid == total ? CustomerInvoiceState.Paid : paid > 0 ? CustomerInvoiceState.PartiallyPaid : CustomerInvoiceState.Posted,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ConcurrencyStamp = number
            });
        else
            db.SupplierBills.Add(new SupplierBill
            {
                Id = id,
                TenantId = scope.Tenant,
                CompanyId = scope.Company,
                Number = number,
                SupplierReference = number,
                PurchaseOrderId = Guid.NewGuid(),
                PurchaseOrderNumber = number,
                SupplierId = partyId,
                SupplierName = "Aging party",
                BillDate = date,
                DueDate = due,
                CurrencyCode = currency,
                BaseCurrencyCode = "USD",
                ExchangeRate = 1,
                Subtotal = total,
                Total = total,
                AmountPaid = paid,
                State = draft ? SupplierBillState.Draft : cancelled ? SupplierBillState.Cancelled : paid == total ? SupplierBillState.Paid : paid > 0 ? SupplierBillState.PartiallyPaid : SupplierBillState.Posted,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ConcurrencyStamp = number
            });
    }

    private static void AddPayment(AccountingDbContext db, (Guid Tenant, Guid Company) scope, bool customer,
        Guid? documentId, Guid partyId, DateOnly date, decimal allocated, PaymentState state,
        Guid? reversal = null, decimal? paymentAmount = null)
    {
        var id = Guid.NewGuid();
        var payment = new Payment
        {
            Id = id,
            TenantId = scope.Tenant,
            CompanyId = scope.Company,
            Number = id.ToString("N"),
            Direction = customer ? PaymentDirection.CustomerReceipt : PaymentDirection.SupplierPayment,
            PartyId = partyId,
            PartyName = "Aging party",
            PaymentDate = date,
            CurrencyCode = "USD",
            ExchangeRate = 1,
            Amount = paymentAmount ?? allocated,
            Reference = "Aging fixture",
            State = state,
            ReversalJournalEntryId = reversal,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ConcurrencyStamp = id.ToString("N")
        };
        if (documentId.HasValue)
            payment.Allocations.Add(new PaymentAllocation
            {
                Id = Guid.NewGuid(),
                TenantId = scope.Tenant,
                CompanyId = scope.Company,
                PaymentId = id,
                CustomerInvoiceId = customer ? documentId : null,
                SupplierBillId = customer ? null : documentId,
                DocumentNumber = documentId.Value.ToString("N"),
                Amount = allocated
            });
        db.Payments.Add(payment);
    }

    private static Guid AddReversal(AccountingDbContext db, (Guid Tenant, Guid Company) scope, DateOnly day)
    {
        var id = Guid.NewGuid();
        var journalId = Guid.NewGuid();
        db.Journals.Add(new Journal
        {
            Id = journalId,
            TenantId = scope.Tenant,
            CompanyId = scope.Company,
            Code = "GJ",
            Name = "General",
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ConcurrencyStamp = journalId.ToString("N")
        });
        db.JournalEntries.Add(new JournalEntry
        {
            Id = id,
            TenantId = scope.Tenant,
            CompanyId = scope.Company,
            Number = id.ToString("N"),
            JournalId = journalId,
            BusinessDate = day,
            Description = "Compensating allocation",
            CurrencyCode = "USD",
            ExchangeRate = 1,
            State = JournalEntryState.Posted,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PostedAtUtc = DateTimeOffset.UtcNow,
            ConcurrencyStamp = id.ToString("N")
        });
        return id;
    }

    private static (Guid Tenant, Guid Company) NewScope() => (Guid.NewGuid(), Guid.NewGuid());
    private sealed class Tenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class Company(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }

    private sealed class ReadCommands : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

public sealed class HistoricalAgingPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString => _postgres!.GetConnectionString();

    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("historical_aging_tests");
        await using var migration = new AccountingDbContext(new DbContextOptionsBuilder<AccountingDbContext>()
            .UseNpgsql(ConnectionString).Options);
        await migration.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}
