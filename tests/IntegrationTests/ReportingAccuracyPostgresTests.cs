using Erp.Abstractions.Context;
using Erp.Modules.Accounting;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Inventory;
using Erp.Modules.Inventory.Contracts;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class ReportingAccuracyPostgresTests(ReportingAccuracyPostgresFixture fixture)
    : IClassFixture<ReportingAccuracyPostgresFixture>
{
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 31);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GeneralLedgerPagesKeepEveryRepeatedAccountLineInSequenceAndExcludeOtherScopesDatesAndDrafts()
    {
        var scope = NewScope();
        var expected = await SeedLedger(scope);
        var otherCompany = scope with { CompanyId = Guid.NewGuid() };
        var expectedOtherCompany = await SeedLedger(otherCompany, repeatedLineCount: 2);
        var otherTenant = scope with { TenantId = Guid.NewGuid() };
        var expectedOtherTenant = await SeedLedger(otherTenant, repeatedLineCount: 2);
        Assert.Equal(454, expected.Count);

        await using var reader = Accounting(scope);
        var reporting = new AccountingReportingData(reader);
        var collected = new List<GeneralLedgerReportRow>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await reporting.GeneralLedgerAsync(From, To, page, 200, default);
            Assert.Equal((page, 200, expected.Count), (result.Page, result.PageSize, result.Total));
            Assert.Equal(expected.Skip((page - 1) * 200).Take(200), result.Items);
            collected.AddRange(result.Items);
            var repeated = await reporting.GeneralLedgerAsync(From, To, page, 200, default);
            Assert.Equal(result.Items, repeated.Items);
        }
        Assert.Equal(expected, collected);
        Assert.Equal(expected.Count, collected.Distinct().Count());
        Assert.Equal(collected.Sum(row => row.Debit), collected.Sum(row => row.Credit));
        Assert.Equal(450, collected.Count(row => row.EntryNumber == "JE-0002"));
        Assert.Equal(1m, collected[2].Debit);
        Assert.Equal(1m, collected[3].Credit);
        Assert.Equal(225m, collected[^3].Credit);
        var pastEnd = await reporting.GeneralLedgerAsync(From, To, 4, 200, default);
        Assert.Equal(expected.Count, pastEnd.Total);
        Assert.Empty(pastEnd.Items);
        Assert.Empty(reader.ChangeTracker.Entries());

        await using var companyReader = Accounting(otherCompany);
        Assert.Equal(expectedOtherCompany, (await new AccountingReportingData(companyReader).GeneralLedgerAsync(From, To, 1, 200, default)).Items);
        await using var tenantReader = Accounting(otherTenant);
        Assert.Equal(expectedOtherTenant, (await new AccountingReportingData(tenantReader).GeneralLedgerAsync(From, To, 1, 200, default)).Items);
        await using var missingReader = Accounting(NewScope());
        var missing = await new AccountingReportingData(missingReader).GeneralLedgerAsync(From, To, 1, 200, default);
        Assert.Equal(0, missing.Total);
        Assert.Empty(missing.Items);
    }

    [Fact]
    public async Task InventoryPagesKeepEveryCompletedMovementInDateAndNumberOrderAndExcludeOtherScopesAndStates()
    {
        var scope = NewScope();
        var expected = await SeedInventory(scope);
        var otherCompany = scope with { CompanyId = Guid.NewGuid() };
        var expectedOtherCompany = await SeedInventory(otherCompany, movementCount: 2);
        var otherTenant = scope with { TenantId = Guid.NewGuid() };
        var expectedOtherTenant = await SeedInventory(otherTenant, movementCount: 2);
        Assert.Equal(451, expected.Count);

        await using var reader = Inventory(scope);
        var reporting = new InventoryReportingData(reader);
        var collected = new List<InventoryMovementReportRow>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await reporting.MovementsAsync(From, To, page, 200, default);
            Assert.Equal((page, 200, expected.Count), (result.Page, result.PageSize, result.Total));
            Assert.Equal(expected.Skip((page - 1) * 200).Take(200), result.Items);
            collected.AddRange(result.Items);
            var repeated = await reporting.MovementsAsync(From, To, page, 200, default);
            Assert.Equal(result.Items, repeated.Items);
        }
        Assert.Equal(expected, collected);
        Assert.Equal(expected.Count, collected.Select(row => row.MoveId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 451).Sum(index => (decimal)index), collected.Sum(row => row.Quantity));
        Assert.Equal(From, collected[0].BusinessDate);
        Assert.Equal(To, collected[^1].BusinessDate);
        var pastEnd = await reporting.MovementsAsync(From, To, 4, 200, default);
        Assert.Equal(expected.Count, pastEnd.Total);
        Assert.Empty(pastEnd.Items);
        Assert.Empty(reader.ChangeTracker.Entries());

        await using var companyReader = Inventory(otherCompany);
        Assert.Equal(expectedOtherCompany, (await new InventoryReportingData(companyReader).MovementsAsync(From, To, 1, 200, default)).Items);
        await using var tenantReader = Inventory(otherTenant);
        Assert.Equal(expectedOtherTenant, (await new InventoryReportingData(tenantReader).MovementsAsync(From, To, 1, 200, default)).Items);
        await using var missingReader = Inventory(NewScope());
        var missing = await new InventoryReportingData(missingReader).MovementsAsync(From, To, 1, 200, default);
        Assert.Equal(0, missing.Total);
        Assert.Empty(missing.Items);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-10, 250, 1, 200)]
    [InlineData(2, int.MinValue, 2, 1)]
    [InlineData(2, 25, 2, 25)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 200)]
    public async Task GeneralLedgerNormalizesPagingAndExtremeOffsetsReturnEmptyWithoutLosingTheTotal(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var scope = NewScope();
        var expected = await SeedLedger(scope);
        await using var reader = Accounting(scope);

        var result = await new AccountingReportingData(reader).GeneralLedgerAsync(From, To, page, pageSize, default);

        Assert.Equal((expectedPage, expectedSize, expected.Count), (result.Page, result.PageSize, result.Total));
        var offset = ((long)expectedPage - 1) * expectedSize;
        var expectedItems = offset >= expected.Count ? [] : expected.Skip((int)offset).Take(expectedSize).ToArray();
        Assert.Equal(expectedItems, result.Items);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-10, 250, 1, 200)]
    [InlineData(2, int.MinValue, 2, 1)]
    [InlineData(2, 25, 2, 25)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 200)]
    public async Task InventoryNormalizesPagingAndExtremeOffsetsReturnEmptyWithoutLosingTheTotal(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var scope = NewScope();
        var expected = await SeedInventory(scope);
        await using var reader = Inventory(scope);

        var result = await new InventoryReportingData(reader).MovementsAsync(From, To, page, pageSize, default);

        Assert.Equal((expectedPage, expectedSize, expected.Count), (result.Page, result.PageSize, result.Total));
        var offset = ((long)expectedPage - 1) * expectedSize;
        var expectedItems = offset >= expected.Count ? [] : expected.Skip((int)offset).Take(expectedSize).ToArray();
        Assert.Equal(expectedItems, result.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PagedReportsRejectReversedAndMissingBusinessDates(bool inventory)
    {
        var scope = NewScope();
        if (inventory)
        {
            await using var reader = Inventory(scope);
            var reporting = new InventoryReportingData(reader);
            await Assert.ThrowsAsync<ArgumentException>(() => reporting.MovementsAsync(To, From, 1, 200, default));
            await Assert.ThrowsAsync<ArgumentException>(() => reporting.MovementsAsync(default, To, 1, 200, default));
            await Assert.ThrowsAsync<ArgumentException>(() => reporting.MovementsAsync(From, default, 1, 200, default));
        }
        else
        {
            await using var reader = Accounting(scope);
            var reporting = new AccountingReportingData(reader);
            await Assert.ThrowsAsync<ArgumentException>(() => reporting.GeneralLedgerAsync(To, From, 1, 200, default));
            await Assert.ThrowsAsync<ArgumentException>(() => reporting.GeneralLedgerAsync(default, To, 1, 200, default));
            await Assert.ThrowsAsync<ArgumentException>(() => reporting.GeneralLedgerAsync(From, default, 1, 200, default));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PagedReportsHonorAnAlreadyCancelledQuery(bool inventory)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var scope = NewScope();
        if (inventory)
        {
            await using var reader = Inventory(scope);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InventoryReportingData(reader).MovementsAsync(From, To, 1, 200, cancellation.Token));
        }
        else
        {
            await using var reader = Accounting(scope);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AccountingReportingData(reader).GeneralLedgerAsync(From, To, 1, 200, cancellation.Token));
        }
    }

    private async Task<List<GeneralLedgerReportRow>> SeedLedger(ReportScope scope, int repeatedLineCount = 450)
    {
        await using var write = Accounting(scope);
        var accountId = Guid.NewGuid();
        var journalId = Guid.NewGuid();
        write.Accounts.Add(new Account { Id = accountId, TenantId = scope.TenantId, CompanyId = scope.CompanyId, Code = "1000", Name = "Repeated cash account", Type = AccountType.Asset, NormalBalance = NormalBalance.Debit, AllowDirectPosting = true, IsActive = true, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() });
        write.Journals.Add(new Journal { Id = journalId, TenantId = scope.TenantId, CompanyId = scope.CompanyId, Code = "GJ", Name = "Paging fixture", IsActive = true, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() });
        var entries = new[]
        {
            Entry(scope, journalId, accountId, "JE-0002", From, repeatedLineCount),
            Entry(scope, journalId, accountId, "JE-0001", From, 2),
            Entry(scope, journalId, accountId, "JE-0000", To, 2),
            Entry(scope, journalId, accountId, "EXCLUDED-BEFORE", From.AddDays(-1), 2),
            Entry(scope, journalId, accountId, "EXCLUDED-AFTER", To.AddDays(1), 2),
            Entry(scope, journalId, accountId, "EXCLUDED-DRAFT", From.AddDays(1), 2, JournalEntryState.Draft)
        };
        write.JournalEntries.AddRange(entries);
        await write.SaveChangesAsync();
        return entries.Where(entry => entry.State == JournalEntryState.Posted && entry.BusinessDate >= From && entry.BusinessDate <= To)
            .SelectMany(entry => entry.Lines.Select(line => (Entry: entry, Line: line)))
            .OrderBy(row => row.Entry.BusinessDate).ThenBy(row => row.Entry.Number, StringComparer.Ordinal).ThenBy(row => row.Line.AccountCode, StringComparer.Ordinal).ThenBy(row => row.Line.Sequence).ThenBy(row => row.Line.Id)
            .Select(row => new GeneralLedgerReportRow(row.Entry.Id, row.Entry.Number, row.Entry.BusinessDate, row.Entry.Description, row.Line.AccountId, row.Line.AccountCode, row.Line.AccountName, row.Line.Debit, row.Line.Credit, row.Line.BaseDebit, row.Line.BaseCredit, row.Entry.CurrencyCode)).ToList();
    }

    private static JournalEntry Entry(ReportScope scope, Guid journalId, Guid accountId, string number, DateOnly date, int lineCount, JournalEntryState state = JournalEntryState.Posted)
    {
        var entry = new JournalEntry { Id = Guid.NewGuid(), TenantId = scope.TenantId, CompanyId = scope.CompanyId, Number = number, JournalId = journalId, BusinessDate = date, Description = number + " sequence fixture", CurrencyCode = "USD", ExchangeRate = 1, State = state, CreatedAtUtc = Now, PostedAtUtc = state == JournalEntryState.Posted ? Now : null, ConcurrencyStamp = Stamp() };
        // Reverse physical insertion order. Account/date/number are deliberately equal
        // across 450 lines, so paging must retain the unique line sequence tie-breaker.
        for (var sequence = lineCount; sequence >= 1; sequence--)
        {
            var amount = (sequence + 1) / 2m;
            if (sequence % 2 == 0) amount = sequence / 2m;
            var debit = sequence % 2 == 1 ? amount : 0;
            var credit = sequence % 2 == 0 ? amount : 0;
            entry.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = scope.TenantId, CompanyId = scope.CompanyId, JournalEntryId = entry.Id, Sequence = sequence, AccountId = accountId, AccountCode = "1000", AccountName = "Repeated cash account", Description = "Sequence " + sequence, Debit = debit, Credit = credit, BaseDebit = debit, BaseCredit = credit });
        }
        return entry;
    }

    private async Task<List<InventoryMovementReportRow>> SeedInventory(ReportScope scope, int movementCount = 451)
    {
        await using var write = Inventory(scope);
        var productId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var referenceId = Guid.NewGuid();
        write.Locations.Add(new StockLocation { Id = sourceId, TenantId = scope.TenantId, CompanyId = scope.CompanyId, Code = "SUP", Name = "Supplier", Type = LocationType.Supplier, ConcurrencyStamp = Stamp() });
        write.Locations.Add(new StockLocation { Id = destinationId, TenantId = scope.TenantId, CompanyId = scope.CompanyId, Code = "MAIN", Name = "Internal", Type = LocationType.Internal, ConcurrencyStamp = Stamp() });
        var moves = Enumerable.Range(1, movementCount).Reverse().Select(index => Move(scope, productId, sourceId, destinationId, referenceId, $"MOVE-{index:D4}", index % 3 == 0 ? From : index % 3 == 1 ? From.AddDays(14) : To, index)).ToList();
        moves.Add(Move(scope, productId, sourceId, destinationId, referenceId, "EXCLUDED-BEFORE", From.AddDays(-1), 10000));
        moves.Add(Move(scope, productId, sourceId, destinationId, referenceId, "EXCLUDED-AFTER", To.AddDays(1), 10000));
        moves.Add(Move(scope, productId, sourceId, destinationId, referenceId, "EXCLUDED-DRAFT", From.AddDays(1), 10000, StockMoveState.Draft));
        moves.Add(Move(scope, productId, sourceId, destinationId, referenceId, "EXCLUDED-CANCELLED", From.AddDays(1), 10000, StockMoveState.Cancelled));
        write.StockMoves.AddRange(moves);
        await write.SaveChangesAsync();
        return moves.Where(move => move.State == StockMoveState.Completed && move.BusinessDate >= From && move.BusinessDate <= To)
            .OrderBy(move => move.BusinessDate).ThenBy(move => move.Number, StringComparer.Ordinal).ThenBy(move => move.Id)
            .Select(move => new InventoryMovementReportRow(move.Id, move.Number, move.Type.ToString(), move.ProductId, move.ProductSku, move.UnitCode, move.Quantity, move.SourceLocationId, move.DestinationLocationId, move.BusinessDate, move.ReferenceDocumentType, move.ReferenceDocumentId)).ToList();
    }

    private static StockMove Move(ReportScope scope, Guid productId, Guid sourceId, Guid destinationId, Guid referenceId, string number, DateOnly date, decimal quantity, StockMoveState state = StockMoveState.Completed) =>
        new() { Id = Guid.NewGuid(), TenantId = scope.TenantId, CompanyId = scope.CompanyId, Number = number, Type = StockMoveType.Receipt, State = state, ProductId = productId, ProductSku = "SKU-PAGING", UnitCode = "EA", Quantity = quantity, SourceLocationId = sourceId, DestinationLocationId = destinationId, ReferenceDocumentType = "PurchaseOrder", ReferenceDocumentId = referenceId, BusinessDate = date, CreatedAtUtc = Now, CompletedAtUtc = state == StockMoveState.Completed ? Now : null, ConcurrencyStamp = Stamp() };

    private AccountingDbContext Accounting(ReportScope scope) => new(new DbContextOptionsBuilder<AccountingDbContext>().UseNpgsql(fixture.ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "accounting")).Options, new Tenant(scope.TenantId), new Company(scope.CompanyId));
    private InventoryDbContext Inventory(ReportScope scope) => new(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(fixture.ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "inventory")).Options, new Tenant(scope.TenantId), new Company(scope.CompanyId));
    private static ReportScope NewScope() => new(Guid.NewGuid(), Guid.NewGuid());
    private static string Stamp() => Guid.NewGuid().ToString("N");
    private sealed record ReportScope(Guid TenantId, Guid CompanyId);
    private sealed class Tenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class Company(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
}

public sealed class ReportingAccuracyPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("reporting_accuracy_tests");
        ConnectionString = _postgres.GetConnectionString();
        await using var accounting = new AccountingDbContext(new DbContextOptionsBuilder<AccountingDbContext>().UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "accounting")).Options);
        await accounting.Database.MigrateAsync();
        await using var inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "inventory")).Options);
        await inventory.Database.MigrateAsync();
    }

    public async Task DisposeAsync() { if (_postgres is not null) await _postgres.DisposeAsync(); }
}
