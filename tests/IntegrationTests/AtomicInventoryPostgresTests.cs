using System.Data.Common;
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
using Erp.Modules.Contacts;
using Erp.Modules.Inventory;
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

public sealed class AtomicInventoryPostgresTests(AtomicInventoryPostgresFixture fixture)
    : IClassFixture<AtomicInventoryPostgresFixture>
{
    [Theory]
    [InlineData("warehouse-create")]
    [InlineData("warehouse-update")]
    [InlineData("location-create")]
    [InlineData("location-update")]
    [InlineData("create")]
    [InlineData("batch")]
    [InlineData("cancel")]
    [InlineData("receipt")]
    [InlineData("receipt-partial")]
    [InlineData("receipt-full")]
    [InlineData("delivery")]
    [InlineData("delivery-partial")]
    [InlineData("delivery-full")]
    [InlineData("transfer")]
    [InlineData("gain")]
    [InlineData("loss")]
    [InlineData("correction")]
    [InlineData("reserve")]
    [InlineData("release")]
    [InlineData("release-partial")]
    public async Task FailureAfterRealAuditFlushRollsBackEveryWriteFamilyAndAllowsSameScopeRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        scope.Audit.FailOnFlush = mutation.Audits;

        await Assert.ThrowsAsync<InjectedAuditException>(() => mutation.Execute(CancellationToken.None));

        Assert.Equal(mutation.Audits, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        var failedIds = scope.Outbox.Attempts.Select(item => item.EventId).ToArray();
        Assert.Equal(mutation.Events.Length, failedIds.Length);

        var committed = await mutation.Execute(CancellationToken.None);

        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, mutation.Audits, mutation.Events.Length);
        await AssertAuditAndEvents(scope, mutation, committed, failedIds);
        await AssertOutcome(scope, operation, committed);
        if (operation is "create" or "batch" or "correction")
        {
            var count = operation == "batch" ? 2 : 1;
            Assert.Equal(scope.Sequences.Allocations.Take(count), scope.Sequences.Allocations.Skip(count));
        }
    }

    [Theory]
    [InlineData("warehouse-create")]
    [InlineData("warehouse-update")]
    [InlineData("location-create")]
    [InlineData("location-update")]
    [InlineData("create")]
    [InlineData("batch")]
    [InlineData("cancel")]
    [InlineData("delivery-partial")]
    [InlineData("correction")]
    [InlineData("reserve")]
    [InlineData("release")]
    [InlineData("release-partial")]
    public async Task CancellationAfterRealPlatformFlushRollsBackAndAllowsUncancelledRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        using var cancellation = new CancellationTokenSource();
        scope.Audit.AfterFlush = () => { if (scope.Audit.FlushCalls == mutation.Audits) cancellation.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.Execute(cancellation.Token));

        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        scope.Audit.AfterFlush = null;
        var committed = await mutation.Execute(CancellationToken.None);
        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, mutation.Audits, mutation.Events.Length);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("batch", false)]
    [InlineData("batch", true)]
    [InlineData("correction", false)]
    [InlineData("correction", true)]
    public async Task RealSequenceDefinitionsAndCountersRollBackWithSingleBatchAndCorrectionWrites(string operation, bool existing)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        if (existing) await SeedSequence(scope, 41);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        scope.Audit.FailOnFlush = mutation.Audits;

        await Assert.ThrowsAsync<InjectedAuditException>(() => mutation.Execute(CancellationToken.None));

        var count = operation == "batch" ? 2 : 1;
        var first = existing ? 41 : 1;
        Assert.Equal(Enumerable.Range(first, count).Select(number => $"MOVE-2026-{number:D7}"), scope.Sequences.Allocations);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        var committed = await mutation.Execute(CancellationToken.None);
        await AssertDurableResult(scope, committed);
        Assert.Equal(scope.Sequences.Allocations.Take(count), scope.Sequences.Allocations.Skip(count));
        await using var platform = scope.ReadPlatform();
        Assert.Equal(first + count, await platform.DocumentSequences.Where(item => item.CompanyId == scope.CompanyId && item.Key == "inventory.move")
            .Select(item => item.NextValue).SingleAsync());
        await AssertPlatformCounts(scope, mutation.Audits, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSqlMovementFailureRestoresTheEntireBatchAndNumbersBeforeValidRetry(bool batch)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        var invalid = new string('x', 2001);
        Task<object> Execute(string? notes) => batch
            ? Box(scope.Inventory.CreateBatchAsync(BatchRequest(scope) with { Notes = notes }, CancellationToken.None))
            : Box(scope.Inventory.CreateAsync(MoveRequest(scope) with { Notes = notes }, CancellationToken.None));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => Execute(invalid));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        Assert.Equal(batch ? 2 : 1, scope.Sequences.Allocations.Count);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        Assert.Empty(scope.Outbox.Attempts);
        await AssertDurableResult(scope, await Execute("Valid retry"));
        await AssertPlatformCounts(scope, batch ? 2 : 1, 0);
        Assert.Equal(scope.Sequences.Allocations.Take(batch ? 2 : 1), scope.Sequences.Allocations.Skip(batch ? 2 : 1));
    }

    [Fact]
    public async Task LateBatchProductDisappearanceAfterFirstAllocationRollsBackTheAlreadyStagedMoveAndNumber()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        scope.Sequences.AfterAllocation = async () =>
        {
            await using var products = scope.ReadProducts();
            products.Products.Remove(await products.Products.SingleAsync(item => item.Id == scope.ProductB.Id));
            await products.SaveChangesAsync();
        };

        var failure = await Assert.ThrowsAsync<InventoryValidationException>(() => scope.Inventory.CreateBatchAsync(BatchRequest(scope), CancellationToken.None));

        Assert.Contains("productId", failure.Errors.Keys);
        Assert.Single(scope.Sequences.Allocations);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        await using (var products = scope.ReadProducts())
        {
            products.Products.Add(scope.ProductB);
            await products.SaveChangesAsync();
        }
        var committed = await scope.Inventory.CreateBatchAsync(BatchRequest(scope), CancellationToken.None);
        Assert.Collection(committed.Items,
            item => Assert.Equal("MOVE-2026-0000001", item.Number),
            item => Assert.Equal("MOVE-2026-0000002", item.Number));
        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, 2, 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PartialReservationAndShipmentEventStagingRollsBackBeforeAuditAndCanRetry(int failAtEvent)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, "delivery-partial");
        var original = await Snapshot(scope);
        scope.Outbox.FailAfterAdd = failAtEvent;

        await Assert.ThrowsAsync<InjectedOutboxException>(() => mutation.Execute(CancellationToken.None));

        Assert.Equal(failAtEvent, scope.Outbox.Attempts.Count);
        Assert.Equal(0, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        var failedIds = scope.Outbox.Attempts.Select(item => item.EventId).ToArray();
        var committed = await mutation.Execute(CancellationToken.None);
        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, 1, 3);
        await AssertAuditAndEvents(scope, mutation, committed, failedIds);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReservationReleaseRechecksVersionAndReplayAfterACompetingWriterWinsTheStockLock(bool sameKey, bool pretracked)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var peer = scope.CreatePeer();
        if (pretracked) _ = await scope.Database.Reservations.SingleAsync(item => item.Id == scope.SeedReservation.Id);
        ReservationDto? winner = null;
        string? winningSnapshot = null;
        scope.CommandHook.BeforeAdvisoryLock = async token =>
        {
            winner = await peer.Inventory.ReleaseAsync(scope.SeedReservation.Id,
                new(scope.SeedReservation.ConcurrencyStamp, "winning-release"), token);
            winningSnapshot = await Snapshot(scope);
        };
        var request = new ReservationActionRequest(scope.SeedReservation.ConcurrencyStamp, sameKey ? "winning-release" : "losing-release");

        if (sameKey)
        {
            var replay = await scope.Inventory.ReleaseAsync(scope.SeedReservation.Id, request, CancellationToken.None);
            Assert.Equal(winner, replay);
        }
        else
            await Assert.ThrowsAsync<InventoryConcurrencyException>(() => scope.Inventory.ReleaseAsync(scope.SeedReservation.Id, request, CancellationToken.None));

        Assert.True(scope.CommandHook.Executed);
        Assert.NotNull(winner);
        Assert.Equal(winningSnapshot, await Snapshot(scope));
        await AssertDurableResult(scope, winner);
        await AssertPlatformCounts(scope, 1, 1);
        Assert.Empty(scope.Outbox.Attempts);
        Assert.Equal(0, scope.Audit.FlushCalls);
        if (!sameKey) AssertClean(scope);
    }

    [Theory]
    [InlineData("warehouse-update")]
    [InlineData("location-update")]
    [InlineData("cancel")]
    [InlineData("gain")]
    [InlineData("release")]
    public async Task StaleVersionsCannotChangeAnyVersionedWriteFamily(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);

        await Assert.ThrowsAsync<InventoryConcurrencyException>(() => VersionedMutation(scope, operation, "stale", CancellationToken.None));

        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        var stamp = operation switch
        {
            "warehouse-update" => scope.Warehouse.ConcurrencyStamp,
            "location-update" => scope.InternalA.ConcurrencyStamp,
            "release" => scope.SeedReservation.ConcurrencyStamp,
            _ => scope.SeedMove.ConcurrencyStamp
        };
        await AssertDurableResult(scope, await VersionedMutation(scope, operation, stamp, CancellationToken.None));
    }

    [Theory]
    [InlineData("warehouse-update")]
    [InlineData("location-update")]
    [InlineData("cancel")]
    [InlineData("gain")]
    public async Task ARealIndependentWriterWinsBeforeSaveAndTheLosingMutationPublishesNothing(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var peer = scope.CreatePeer();
        var mutation = await PrepareMutation(scope, operation);
        object? winner = null;
        string? winningSnapshot = null;
        scope.SaveHook.BeforeSave = async (_, token) =>
        {
            winner = operation switch
            {
                "warehouse-update" => await peer.Inventory.SaveWarehouseAsync(scope.Warehouse.Id,
                    new("MAIN", "Winning warehouse", true, scope.Warehouse.ConcurrencyStamp), token),
                "location-update" => await peer.Inventory.SaveLocationAsync(scope.InternalA.Id,
                    new(scope.Warehouse.Id, "INTERNAL-A", "Winning location", LocationType.Internal, true, scope.InternalA.ConcurrencyStamp), token),
                _ => await peer.Inventory.CancelAsync(scope.SeedMove.Id, scope.SeedMove.ConcurrencyStamp, token)
            };
            winningSnapshot = await Snapshot(scope);
        };

        await Assert.ThrowsAsync<InventoryConcurrencyException>(() => mutation.Execute(CancellationToken.None));

        Assert.True(scope.SaveHook.Executed);
        Assert.IsType<DbUpdateConcurrencyException>(scope.SaveHook.LastFailure);
        Assert.NotNull(winner);
        Assert.Equal(winningSnapshot, await Snapshot(scope));
        await AssertDurableResult(scope, winner);
        await AssertPlatformCounts(scope, 1, 0);
        Assert.Empty(scope.Outbox.Attempts);
        Assert.Equal(0, scope.Audit.FlushCalls);
        AssertClean(scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenantAndCompanyBoundariesHideEveryInventoryAggregateAndRejectForeignReferences(bool sameTenant)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var foreign = await TestScope.CreateAsync(fixture.ConnectionString, sameTenant ? scope.TenantId : null);
        var original = await Snapshot(scope);
        var foreignOriginal = await Snapshot(foreign);
        Assert.Null(await scope.Inventory.GetAsync(foreign.SeedMove.Id, CancellationToken.None));
        Assert.Null(await scope.Inventory.ReservationAsync(foreign.SeedReservation.Id, CancellationToken.None));
        Assert.DoesNotContain(await scope.Inventory.WarehousesAsync(CancellationToken.None), item => item.Id == foreign.Warehouse.Id);
        Assert.DoesNotContain(await scope.Inventory.LocationsAsync(CancellationToken.None), item => item.Id == foreign.InternalA.Id);
        Assert.Empty((await scope.Inventory.SearchAsync(new(ProductId: foreign.ProductA.Id), CancellationToken.None)).Items);
        Assert.Empty((await scope.Inventory.ReservationsAsync(new(ProductId: foreign.ProductA.Id), CancellationToken.None)).Items);
        Assert.Equal(0, (await scope.Inventory.BalanceAsync(foreign.ProductA.Id, foreign.InternalA.Id, CancellationToken.None)).OnHand);
        var missingWrites = new Func<Task>[]
        {
            () => scope.Inventory.SaveWarehouseAsync(foreign.Warehouse.Id, new("X", "Foreign", true, foreign.Warehouse.ConcurrencyStamp), CancellationToken.None),
            () => scope.Inventory.SaveLocationAsync(foreign.InternalA.Id, new(scope.Warehouse.Id, "X", "Foreign", LocationType.Internal, true, foreign.InternalA.ConcurrencyStamp), CancellationToken.None),
            () => scope.Inventory.CancelAsync(foreign.SeedMove.Id, foreign.SeedMove.ConcurrencyStamp, CancellationToken.None),
            () => scope.Inventory.CompleteAsync(foreign.SeedMove.Id, new(foreign.SeedMove.ConcurrencyStamp, "foreign"), CancellationToken.None),
            () => scope.Inventory.CorrectAsync(foreign.CompletedMove.Id, CancellationToken.None),
            () => scope.Inventory.ReleaseAsync(foreign.SeedReservation.Id, new(foreign.SeedReservation.ConcurrencyStamp, "foreign"), CancellationToken.None)
        };
        foreach (var mutation in missingWrites)
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(mutation);
            AssertClean(scope);
        }
        var invalidWrites = new Func<Task>[]
        {
            () => scope.Inventory.CreateAsync(MoveRequest(scope) with { ProductId = foreign.ProductA.Id }, CancellationToken.None),
            () => scope.Inventory.CreateAsync(MoveRequest(scope) with { DestinationLocationId = foreign.InternalA.Id }, CancellationToken.None),
            () => scope.Inventory.SaveLocationAsync(null, new(foreign.Warehouse.Id, "X", "Foreign", LocationType.Internal), CancellationToken.None),
            () => scope.Inventory.ReserveAsync(ReserveRequest(scope) with { ReferenceDocumentId = foreign.SalesOrderId }, CancellationToken.None)
        };
        foreach (var mutation in invalidWrites)
        {
            await Assert.ThrowsAsync<InventoryValidationException>(mutation);
            AssertClean(scope);
        }
        scope.Database.Warehouses.Add(new()
        {
            Id = Guid.NewGuid(),
            TenantId = foreign.TenantId,
            CompanyId = foreign.CompanyId,
            Code = "ILLEGAL",
            Name = "Illegal",
            ConcurrencyStamp = Stamp()
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Database.SaveChangesAsync());
        scope.Database.ChangeTracker.Clear();
        Assert.Equal(original, await Snapshot(scope));
        Assert.Equal(foreignOriginal, await Snapshot(foreign));
        Assert.Empty(scope.Sequences.Allocations);
        await AssertPlatformCounts(scope, 0, 0);
        await AssertPlatformCounts(foreign, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedLedgerRowsRejectBothEditsAndDeletes(bool delete)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        var completed = await scope.Database.StockMoves.SingleAsync(item => item.Id == scope.CompletedMove.Id);
        if (delete) scope.Database.Remove(completed); else completed.Notes = "Illegal edit";

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Database.SaveChangesAsync());

        scope.Database.ChangeTracker.Clear();
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        var correction = await scope.Inventory.CorrectAsync(scope.CompletedMove.Id, CancellationToken.None);
        Assert.Equal(scope.CompletedMove.Id, correction.ReversesMoveId);
        await AssertDurableResult(scope, correction);
        await using var reader = scope.ReadInventory();
        Assert.Equal(MoveDto(scope.CompletedMove), MoveDto(await reader.StockMoves.SingleAsync(item => item.Id == scope.CompletedMove.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContactsProductsAndInventoryComposeAcrossAnOuterTransactionWithoutLeakingIntermediateWrites(bool commit)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        foreach (var context in new DbContext[] { scope.Platform, scope.Contacts, scope.Products, scope.Sales, scope.Purchasing, scope.Database })
            Assert.Same(scope.Session.Connection, context.Database.GetDbConnection());
        var original = await Snapshot(scope);
        Guid contactId;
        Guid productId;
        StockMoveDto completed;
        await using (var outer = await scope.Transactions.BeginAsync(scope.Database, CancellationToken.None))
        {
            var contact = await scope.ContactService.CreateAsync(new(ContactKind.Organization, "Composed partner", null, null,
                "Composed partner", null, null, null, null, IsSupplier: true), CancellationToken.None);
            var product = await scope.ProductService.CreateAsync(new(ProductType.Product, "COMPOSED", "Composed product", null, null,
                12m, 5m, "USD", "EA", "EA", true), CancellationToken.None);
            var move = await scope.Inventory.CreateAsync(MoveRequest(scope) with { ProductId = product.Id }, CancellationToken.None);
            completed = await scope.Inventory.CompleteAsync(move.Id, new(move.ConcurrencyStamp, "composed-completion"), CancellationToken.None);
            Assert.Equal(product.Sku, completed.ProductSku);
            Assert.Equal(StockMoveState.Completed, completed.State);
            contactId = contact.Id;
            productId = product.Id;
            Assert.Equal(original, await Snapshot(scope));
            await using (var contacts = scope.ReadContacts()) Assert.False(await contacts.Contacts.AnyAsync(item => item.Id == contactId));
            await using (var products = scope.ReadProducts()) Assert.False(await products.Products.AnyAsync(item => item.Id == productId));
            await AssertPlatformCounts(scope, 0, 0);
            if (commit) await outer.CommitAsync(CancellationToken.None);
        }
        await using (var contacts = scope.ReadContacts()) Assert.Equal(commit, await contacts.Contacts.AnyAsync(item => item.Id == contactId));
        await using (var products = scope.ReadProducts()) Assert.Equal(commit, await products.Products.AnyAsync(item => item.Id == productId));
        await using (var inventory = scope.ReadInventory()) Assert.Equal(commit, await inventory.StockMoves.AnyAsync(item => item.Id == completed.Id));
        await AssertPlatformCounts(scope, commit ? 4 : 0, commit ? 1 : 0);
        if (commit) await AssertDurableResult(scope, completed);
        else
        {
            Assert.Equal(original, await Snapshot(scope));
            AssertClean(scope);
            Assert.Empty(scope.Contacts.ChangeTracker.Entries());
            Assert.Empty(scope.Products.ChangeTracker.Entries());
            Assert.Null(scope.Contacts.Database.CurrentTransaction);
            Assert.Null(scope.Products.Database.CurrentTransaction);
            var retried = await scope.Inventory.CreateAsync(MoveRequest(scope), CancellationToken.None);
            Assert.Equal("MOVE-2026-0000001", retried.Number);
        }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("correction")]
    [InlineData("reserve")]
    [InlineData("release")]
    public async Task SuccessfulIdempotentNoOpScopesDoNotPoisonAnOuterTransaction(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation == "complete" ? "gain" : operation);
        var expected = await mutation.Execute(CancellationToken.None);
        var original = await Snapshot(scope);
        Guid contactId;
        Guid productId;
        await using (var outer = await scope.Transactions.BeginAsync(scope.Database, CancellationToken.None))
        {
            var contact = await scope.ContactService.CreateAsync(new(ContactKind.Organization, "Replay partner", null, null,
                "Replay partner", null, null, null, null, IsSupplier: true), CancellationToken.None);
            var product = await scope.ProductService.CreateAsync(new(ProductType.Product, "REPLAY", "Replay product", null, null,
                12m, 5m, "USD", "EA", "EA", true), CancellationToken.None);
            Assert.Equal(expected, await mutation.Execute(CancellationToken.None));
            Assert.Equal(original, await Snapshot(scope));
            await AssertPlatformCounts(scope, mutation.Audits, mutation.Events.Length);
            contactId = contact.Id;
            productId = product.Id;
            await outer.CommitAsync(CancellationToken.None);
        }
        await using (var contacts = scope.ReadContacts()) Assert.True(await contacts.Contacts.AnyAsync(item => item.Id == contactId));
        await using (var products = scope.ReadProducts()) Assert.True(await products.Products.AnyAsync(item => item.Id == productId));
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, mutation.Audits + 2, mutation.Events.Length);
        await AssertDurableResult(scope, expected);
    }

    [Theory]
    [InlineData("reserve")]
    [InlineData("delivery")]
    [InlineData("receipt-progress")]
    [InlineData("delivery-reservation")]
    public async Task IndependentConcurrentOperationsCannotOverdrawOverfulfillOrDoubleConsume(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var first = scope.CreatePeer();
        await using var second = scope.CreatePeer();
        StockMove[] moves = [];
        if (operation != "reserve")
        {
            moves = Enumerable.Range(1, 2).Select(index => NewMove(scope, $"RACE-{index}",
                operation == "receipt-progress" ? StockMoveType.Receipt : StockMoveType.Delivery, StockMoveState.Draft,
                operation == "receipt-progress" ? 30 : operation == "delivery-reservation" ? 3 : 5,
                operation == "receipt-progress" ? scope.Supplier.Id : scope.InternalA.Id,
                operation == "receipt-progress" ? scope.InternalA.Id : scope.Customer.Id)).ToArray();
            foreach (var move in moves)
            {
                if (operation == "receipt-progress") { move.ReferenceDocumentType = "PurchaseOrder"; move.ReferenceDocumentId = scope.PurchaseOrderId; }
                if (operation == "delivery-reservation") { move.ReferenceDocumentType = "SalesOrder"; move.ReferenceDocumentId = scope.SalesOrderId; }
            }
            scope.Database.StockMoves.AddRange(moves);
            await scope.Database.SaveChangesAsync();
            scope.Database.ChangeTracker.Clear();
        }
        Task<Exception?> Attempt(TestScope worker, int index) => Capture(async () =>
        {
            if (operation == "reserve") await worker.Inventory.ReserveAsync(ReserveRequest(scope, $"race-{index}") with { Quantity = 5 }, CancellationToken.None);
            else await worker.Inventory.CompleteAsync(moves[index].Id, new(moves[index].ConcurrencyStamp, $"complete-race-{index}"), CancellationToken.None);
        });

        var attempts = await Task.WhenAll(Attempt(first, 0), Attempt(second, 1));

        Assert.Single(attempts, item => item is null);
        var failure = Assert.Single(attempts.OfType<InventoryValidationException>());
        Assert.Contains("quantity", failure.Errors.Keys);
        var balance = await scope.Inventory.BalanceAsync(scope.ProductA.Id, scope.InternalA.Id, CancellationToken.None);
        Assert.Equal(operation switch { "reserve" => 11m, "receipt-progress" => 41m, "delivery-reservation" => 8m, _ => 6m }, balance.OnHand);
        Assert.Equal(operation switch { "reserve" => 9m, "delivery-reservation" => 1m, _ => 4m }, balance.Reserved);
        Assert.True(balance.Available >= 0);
        var eventCount = operation switch { "receipt-progress" => 2, "delivery-reservation" => 3, _ => 1 };
        await AssertPlatformCounts(scope, 1, eventCount);
        await using var reader = scope.ReadInventory();
        if (operation != "reserve") Assert.Single(await reader.StockMoves.Where(item => moves.Select(move => move.Id).Contains(item.Id) && item.State == StockMoveState.Completed).ToListAsync());
        if (operation == "delivery-reservation")
        {
            var reservation = await reader.Reservations.SingleAsync(item => item.Id == scope.SeedReservation.Id);
            Assert.Equal(3, reservation.ConsumedQuantity);
            Assert.Equal(ReservationState.Active, reservation.State);
        }
    }

    [Theory]
    [InlineData("reserve")]
    [InlineData("correction")]
    public async Task IndependentConcurrentSameKeyRequestsProduceOneDurableResultAndOneAudit(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var first = scope.CreatePeer();
        await using var second = scope.CreatePeer();
        Task<object> Execute(TestScope worker) => operation == "reserve"
            ? Box(worker.Inventory.ReserveAsync(ReserveRequest(scope, "concurrent-identical"), CancellationToken.None))
            : Box(worker.Inventory.CorrectAsync(scope.CompletedMove.Id, CancellationToken.None));

        var results = await Task.WhenAll(Execute(first), Execute(second));

        Assert.Equal(results[0], results[1]);
        await AssertDurableResult(scope, results[0]);
        await AssertPlatformCounts(scope, 1, operation == "reserve" ? 1 : 0);
        await using var reader = scope.ReadInventory();
        if (operation == "reserve") Assert.Equal(1, await reader.Reservations.CountAsync(item => item.IdempotencyKey == "concurrent-identical"));
        else
        {
            Assert.Equal(1, await reader.StockMoves.CountAsync(item => item.ReversesMoveId == scope.CompletedMove.Id));
            await using var platform = scope.ReadPlatform();
            Assert.Equal(2, await platform.DocumentSequences.Where(item => item.CompanyId == scope.CompanyId && item.Key == "inventory.move").Select(item => item.NextValue).SingleAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionReplayCanonicalizesPaddedKeysBeforeCheckingTheOriginalStaleVersion(bool trimmedReplay)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var originalRequest = new CompleteMoveRequest(scope.SeedMove.ConcurrencyStamp, " padded-complete ");
        var completed = await scope.Inventory.CompleteAsync(scope.SeedMove.Id, originalRequest, CancellationToken.None);
        Assert.NotEqual(originalRequest.ConcurrencyStamp, completed.ConcurrencyStamp);
        Assert.Equal(StockMoveState.Completed, completed.State);
        await AssertDurableResult(scope, completed);
        var original = await Snapshot(scope);
        var originalBalance = await scope.Inventory.BalanceAsync(scope.ProductA.Id, scope.InternalA.Id, CancellationToken.None);
        string originalPlatform;
        await using (var platform = scope.ReadPlatform())
        {
            originalPlatform = JsonSerializer.Serialize(new
            {
                Audit = await platform.AuditLogs.AsNoTracking().SingleAsync(item => item.CompanyId == scope.CompanyId),
                Outbox = await platform.OutboxMessages.AsNoTracking().SingleAsync(item => item.CompanyId == scope.CompanyId)
            });
        }

        var replay = await scope.Inventory.CompleteAsync(scope.SeedMove.Id,
            originalRequest with { IdempotencyKey = trimmedReplay ? "padded-complete" : originalRequest.IdempotencyKey }, CancellationToken.None);

        Assert.Equal(completed, replay);
        await AssertDurableResult(scope, replay);
        Assert.Equal(original, await Snapshot(scope));
        Assert.Equal(originalBalance, await scope.Inventory.BalanceAsync(scope.ProductA.Id, scope.InternalA.Id, CancellationToken.None));
        Assert.Empty(scope.Sequences.Allocations);
        Assert.Single(scope.Outbox.Attempts);
        Assert.Equal(1, scope.Audit.FlushCalls);
        await AssertPlatformCounts(scope, 1, 1);
        await using (var platform = scope.ReadPlatform())
        {
            Assert.Equal(originalPlatform, JsonSerializer.Serialize(new
            {
                Audit = await platform.AuditLogs.AsNoTracking().SingleAsync(item => item.CompanyId == scope.CompanyId),
                Outbox = await platform.OutboxMessages.AsNoTracking().SingleAsync(item => item.CompanyId == scope.CompanyId)
            }));
        }
    }

    private static DateOnly BusinessDate => new(2026, 10, 18);
    private static string Stamp() => Guid.NewGuid().ToString("N");
    private static StockMoveRequest MoveRequest(TestScope scope) => new(StockMoveType.Adjustment, scope.ProductA.Id, 2,
        scope.Gain.Id, scope.InternalA.Id, BusinessDate, Notes: " Atomic movement ");
    private static ReserveStockRequest ReserveRequest(TestScope scope, string key = "new-reservation") =>
        new(scope.ProductA.Id, scope.InternalA.Id, 2, key, "SalesOrder", scope.SalesOrderId);

    private static StockMoveBatchRequest BatchRequest(TestScope scope) => new(StockMoveType.Adjustment,
        scope.Gain.Id, scope.InternalA.Id, BusinessDate, [new(scope.ProductA.Id, 2), new(scope.ProductB.Id, 3)], Notes: " Atomic batch ");

    private static async Task<object> Box<T>(Task<T> operation) => (await operation)!;
    private static async Task<Exception?> Capture(Func<Task> operation)
    {
        try { await operation(); return null; }
        catch (Exception failure) { return failure; }
    }

    private sealed record Mutation(string Action, int Audits, string[] Events, Func<CancellationToken, Task<object>> Execute);

    private static async Task<Mutation> PrepareMutation(TestScope scope, string operation)
    {
        if (operation == "release-partial")
        {
            await using var setup = scope.ReadInventory();
            (await setup.Reservations.SingleAsync(item => item.Id == scope.SeedReservation.Id)).ConsumedQuantity = 1;
            await setup.SaveChangesAsync();
        }
        if (operation is "receipt" or "receipt-partial" or "receipt-full" or "delivery" or "delivery-partial" or "delivery-full" or "transfer" or "gain" or "loss")
        {
            await using var setup = scope.ReadInventory();
            var move = await setup.StockMoves.SingleAsync(item => item.Id == scope.SeedMove.Id);
            move.Type = operation switch
            {
                "receipt" or "receipt-partial" or "receipt-full" => StockMoveType.Receipt,
                "delivery" or "delivery-partial" or "delivery-full" => StockMoveType.Delivery,
                "transfer" => StockMoveType.Transfer,
                _ => StockMoveType.Adjustment
            };
            move.Quantity = operation == "receipt-full" ? 50 : operation == "delivery-full" ? 4 : 2;
            move.SourceLocationId = operation switch
            {
                "receipt" or "receipt-partial" or "receipt-full" => scope.Supplier.Id,
                "gain" => scope.Gain.Id,
                _ => scope.InternalA.Id
            };
            move.DestinationLocationId = operation switch
            {
                "delivery" or "delivery-partial" or "delivery-full" => scope.Customer.Id,
                "transfer" => scope.InternalB.Id,
                "loss" => scope.Loss.Id,
                _ => scope.InternalA.Id
            };
            if (operation is "receipt-partial" or "receipt-full") { move.ReferenceDocumentType = "PurchaseOrder"; move.ReferenceDocumentId = scope.PurchaseOrderId; }
            if (operation is "delivery-partial" or "delivery-full") { move.ReferenceDocumentType = "SalesOrder"; move.ReferenceDocumentId = scope.SalesOrderId; }
            await setup.SaveChangesAsync();
            if (operation == "delivery-full")
            {
                await using var sales = scope.ReadSales();
                (await sales.SalesOrderLines.SingleAsync(item => item.SalesOrderId == scope.SalesOrderId)).Quantity = 4;
                await sales.SaveChangesAsync();
            }
        }
        return operation switch
        {
            "warehouse-create" => new("inventory.warehouse.saved", 1, [], token => Box(scope.Inventory.SaveWarehouseAsync(null, new(" NEW ", " New warehouse "), token))),
            "warehouse-update" => new("inventory.warehouse.saved", 1, [], token => VersionedMutation(scope, operation, scope.Warehouse.ConcurrencyStamp, token)),
            "location-create" => new("inventory.location.saved", 1, [], token => Box(scope.Inventory.SaveLocationAsync(null,
                new(scope.Warehouse.Id, " NEW ", " New location ", LocationType.Internal), token))),
            "location-update" => new("inventory.location.saved", 1, [], token => VersionedMutation(scope, operation, scope.InternalA.ConcurrencyStamp, token)),
            "create" => new("inventory.move.created", 1, [], token => Box(scope.Inventory.CreateAsync(MoveRequest(scope), token))),
            "batch" => new("inventory.move.created", 2, [], token => Box(scope.Inventory.CreateBatchAsync(BatchRequest(scope), token))),
            "cancel" => new("inventory.move.cancelled", 1, [], token => VersionedMutation(scope, operation, scope.SeedMove.ConcurrencyStamp, token)),
            "correction" => new("inventory.move.correction_created", 1, [], token => Box(scope.Inventory.CorrectAsync(scope.CompletedMove.Id, token))),
            "reserve" => new("inventory.stock.reserved", 1, ["inventory.stock.reserved.v1"], token => Box(scope.Inventory.ReserveAsync(ReserveRequest(scope), token))),
            "release" or "release-partial" => new("inventory.stock.released", 1, ["inventory.stock.reservation_released.v1"], token => VersionedMutation(scope, operation, scope.SeedReservation.ConcurrencyStamp, token)),
            "receipt" => Complete(["inventory.goods.received.v1"]),
            "receipt-partial" or "receipt-full" => Complete(["inventory.goods.received.v1", "inventory.purchase_order.fulfillment_progress.v1"]),
            "delivery" => Complete(["inventory.shipment.completed.v1"]),
            "delivery-partial" or "delivery-full" => Complete(["inventory.stock.reservation_consumed.v1", "inventory.shipment.completed.v1", "inventory.sales_order.fulfillment_progress.v1"]),
            "transfer" => Complete(["inventory.transfer.completed.v1"]),
            "gain" or "loss" => Complete(["inventory.stock.adjusted.v1"]),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        Mutation Complete(string[] events) => new("inventory.move.completed", 1, events,
            token => VersionedMutation(scope, operation, scope.SeedMove.ConcurrencyStamp, token));
    }

    private static Task<object> VersionedMutation(TestScope scope, string operation, string stamp, CancellationToken token) => operation switch
    {
        "warehouse-update" => Box(scope.Inventory.SaveWarehouseAsync(scope.Warehouse.Id, new(" MAIN ", " Changed warehouse ", true, stamp), token)),
        "location-update" => Box(scope.Inventory.SaveLocationAsync(scope.InternalA.Id,
            new(scope.Warehouse.Id, " INTERNAL-A ", " Changed location ", LocationType.Internal, true, stamp), token)),
        "cancel" => Box(scope.Inventory.CancelAsync(scope.SeedMove.Id, stamp, token)),
        "release" or "release-partial" => Box(scope.Inventory.ReleaseAsync(scope.SeedReservation.Id, new(stamp, "release-seed"), token)),
        _ => Box(scope.Inventory.CompleteAsync(scope.SeedMove.Id, new(stamp, "complete-seed"), token))
    };

    private static async Task AssertOutcome(TestScope scope, string operation, object result)
    {
        switch (result)
        {
            case WarehouseDto warehouse:
                Assert.Equal(operation == "warehouse-create" ? "NEW" : "MAIN", warehouse.Code);
                Assert.Equal(operation == "warehouse-create" ? "New warehouse" : "Changed warehouse", warehouse.Name);
                Assert.True(warehouse.IsActive);
                Assert.NotEqual(scope.Warehouse.ConcurrencyStamp, warehouse.ConcurrencyStamp);
                break;
            case LocationDto location:
                Assert.Equal(operation == "location-create" ? "NEW" : "INTERNAL-A", location.Code);
                Assert.Equal(operation == "location-create" ? "New location" : "Changed location", location.Name);
                Assert.Equal(scope.Warehouse.Id, location.WarehouseId);
                Assert.Equal(LocationType.Internal, location.Type);
                Assert.True(location.IsActive);
                Assert.NotEqual(scope.InternalA.ConcurrencyStamp, location.ConcurrencyStamp);
                break;
            case StockMoveBatchDto batch:
                Assert.Equal(2, batch.Items.Count);
                Assert.Equal(new[] { scope.ProductA.Id, scope.ProductB.Id }, batch.Items.Select(item => item.ProductId));
                Assert.Collection(batch.Items,
                    item => Assert.Equal(2m, item.Quantity),
                    item => Assert.Equal(3m, item.Quantity));
                Assert.All(batch.Items, item => { Assert.Equal(StockMoveState.Draft, item.State); Assert.Equal("Atomic batch", item.Notes); });
                break;
            case StockMoveDto move:
                Assert.Equal(scope.ProductA.Id, move.ProductId);
                Assert.Equal(scope.ProductA.Sku, move.ProductSku);
                Assert.Equal("EA", move.UnitCode);
                Assert.NotEqual(scope.SeedMove.ConcurrencyStamp, move.ConcurrencyStamp);
                Assert.Equal(operation switch { "create" or "correction" => StockMoveState.Draft, "cancel" => StockMoveState.Cancelled, _ => StockMoveState.Completed }, move.State);
                if (move.State == StockMoveState.Completed) Assert.Equal(FixedClock.Now, move.CompletedAtUtc);
                if (operation == "create") { Assert.Equal("Atomic movement", move.Notes); Assert.Equal(2, move.Quantity); }
                if (operation == "correction")
                {
                    Assert.Equal(scope.CompletedMove.Id, move.ReversesMoveId);
                    Assert.Equal(scope.CompletedMove.DestinationLocationId, move.SourceLocationId);
                    Assert.Equal(scope.CompletedMove.SourceLocationId, move.DestinationLocationId);
                    Assert.Equal(scope.CompletedMove.Quantity, move.Quantity);
                    Assert.Equal(StockMoveType.Delivery, move.Type);
                    Assert.Equal("StockMove", move.ReferenceDocumentType);
                }
                break;
            case ReservationDto reservation:
                Assert.Equal(operation == "reserve" ? ReservationState.Active : ReservationState.Released, reservation.State);
                Assert.Equal(operation == "reserve" ? 2 : 4, reservation.Quantity);
                Assert.Equal(operation == "release-partial" ? 1 : 0, reservation.ConsumedQuantity);
                Assert.Equal(scope.ProductA.Id, reservation.ProductId);
                Assert.Equal(scope.InternalA.Id, reservation.LocationId);
                Assert.Equal(scope.SalesOrderId, reservation.ReferenceDocumentId);
                Assert.Equal("SalesOrder", reservation.ReferenceDocumentType);
                break;
        }
        if (operation is "delivery-partial" or "delivery-full")
        {
            await using var reader = scope.ReadInventory();
            var reservation = await reader.Reservations.SingleAsync(item => item.Id == scope.SeedReservation.Id);
            Assert.Equal(operation == "delivery-full" ? 4 : 2, reservation.ConsumedQuantity);
            Assert.Equal(operation == "delivery-full" ? ReservationState.Consumed : ReservationState.Active, reservation.State);
            Assert.Equal(operation == "delivery-full" ? FixedClock.Now : (DateTimeOffset?)null, reservation.ConsumedAtUtc);
            Assert.NotEqual(scope.SeedReservation.ConcurrencyStamp, reservation.ConcurrencyStamp);
        }
    }

    private static async Task AssertAuditAndEvents(TestScope scope, Mutation mutation, object result, Guid[] rejectedIds)
    {
        await using var platform = scope.ReadPlatform();
        var audits = await platform.AuditLogs.Where(item => item.CompanyId == scope.CompanyId).ToArrayAsync();
        var results = result is StockMoveBatchDto batch ? batch.Items.Cast<object>().ToArray() : [result];
        Assert.Equal(results.Select(item => Id(item).ToString()).Order(StringComparer.Ordinal),
            audits.Select(item => item.EntityId).Order(StringComparer.Ordinal));
        foreach (var audit in audits)
        {
            Assert.Equal(mutation.Action, audit.Action);
            Assert.Equal(scope.TenantId, audit.TenantId);
            Assert.Equal(scope.CompanyId, audit.CompanyId);
            Assert.Equal(scope.UserId, audit.UserId);
            Assert.Equal(FixedClock.Now, audit.TimestampUtc);
            Assert.Equal("inventory-atomicity-test", audit.CorrelationId);
            var expected = Assert.Single(results, item => Id(item).ToString() == audit.EntityId);
            Assert.Equal(expected.GetType().Name, audit.EntityType);
            Assert.Equal(expected, JsonSerializer.Deserialize(audit.AfterJson!, expected.GetType()));
        }
        var messages = await platform.OutboxMessages.Where(item => item.CompanyId == scope.CompanyId).ToArrayAsync();
        Assert.Equal(mutation.Events.Order(), messages.Select(item => item.EventType).Order());
        foreach (var message in messages)
        {
            Assert.DoesNotContain(message.Id, rejectedIds);
            Assert.Equal(scope.TenantId, message.TenantId);
            Assert.Equal(scope.CompanyId, message.CompanyId);
            Assert.Equal(FixedClock.Now, message.OccurredAtUtc);
            Assert.Equal("inventory-atomicity-test", message.CorrelationId);
            var attempted = Assert.Single(scope.Outbox.Attempts, item => item.EventId == message.Id);
            Assert.Equal(attempted, JsonSerializer.Deserialize(message.PayloadJson, attempted.GetType()));
            AssertEvent(scope, result, attempted);
        }
    }

    private static Guid Id(object value) => value switch
    {
        WarehouseDto warehouse => warehouse.Id,
        LocationDto location => location.Id,
        StockMoveDto move => move.Id,
        ReservationDto reservation => reservation.Id,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static void AssertEvent(TestScope scope, object result, IIntegrationEvent value)
    {
        Assert.Equal(FixedClock.Now, value.OccurredAtUtc);
        switch (value)
        {
            case StockReservedEvent reserved:
                var reservation = Assert.IsType<ReservationDto>(result);
                Assert.Equal(new StockReservedEvent(value.EventId, FixedClock.Now, reservation.Id, reservation.ProductId, reservation.LocationId, reservation.Quantity), reserved);
                break;
            case StockReservationReleasedEvent released:
                var release = Assert.IsType<ReservationDto>(result);
                Assert.Equal(new StockReservationReleasedEvent(value.EventId, FixedClock.Now, release.Id, release.RemainingQuantity), released);
                break;
            default:
                var move = Assert.IsType<StockMoveDto>(result);
                IIntegrationEvent expected = value switch
                {
                    StockReservationConsumedEvent => new StockReservationConsumedEvent(value.EventId, FixedClock.Now, scope.SeedReservation.Id, move.Id, move.Quantity),
                    GoodsReceivedEvent => new GoodsReceivedEvent(value.EventId, FixedClock.Now, move.Id, move.ProductId, move.DestinationLocationId!.Value,
                        move.Quantity, move.ReferenceDocumentType == "PurchaseOrder" ? move.ReferenceDocumentId : null),
                    ShipmentCompletedEvent => new ShipmentCompletedEvent(value.EventId, FixedClock.Now, move.Id, move.ProductId, move.SourceLocationId!.Value,
                        move.Quantity, move.ReferenceDocumentType == "SalesOrder" ? move.ReferenceDocumentId : null),
                    TransferCompletedEvent => new TransferCompletedEvent(value.EventId, FixedClock.Now, move.Id, move.ProductId, move.SourceLocationId!.Value, move.DestinationLocationId!.Value, move.Quantity),
                    StockAdjustedEvent => new StockAdjustedEvent(value.EventId, FixedClock.Now, move.Id, move.ProductId, move.SourceLocationId == scope.InternalA.Id ? -move.Quantity : move.Quantity),
                    PurchaseOrderFulfillmentProgressEvent => new PurchaseOrderFulfillmentProgressEvent(value.EventId, FixedClock.Now, scope.PurchaseOrderId, move.ProductId, 50, move.Quantity, move.Quantity == 50),
                    SalesOrderFulfillmentProgressEvent => new SalesOrderFulfillmentProgressEvent(value.EventId, FixedClock.Now, scope.SalesOrderId, move.ProductId,
                        move.Quantity == 4 ? 4 : 50, move.Quantity, move.Quantity == 4),
                    _ => throw new ArgumentOutOfRangeException(nameof(value))
                };
                Assert.Equal(expected, value);
                break;
        }
    }

    private static async Task SeedSequence(TestScope scope, long next)
    {
        scope.Platform.DocumentSequences.Add(new DocumentSequence
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            Key = "inventory.move",
            PrefixTemplate = "MOVE-{yyyy}-",
            Padding = 7,
            NextValue = next
        });
        await scope.Platform.SaveChangesAsync();
        scope.Platform.ChangeTracker.Clear();
    }

    private static StockMoveDto MoveDto(StockMove item) => new(item.Id, item.Number, item.Type, item.State, item.ProductId,
        item.ProductSku, item.UnitCode, item.Quantity, item.SourceLocationId, item.DestinationLocationId, item.ReferenceDocumentType,
        item.ReferenceDocumentId, item.BusinessDate, item.Notes, item.ReversesMoveId, item.CreatedAtUtc, item.CompletedAtUtc, item.ConcurrencyStamp);
    private static ReservationDto ReservationDto(StockReservation item) => new(item.Id, item.ProductId, item.LocationId,
        item.Quantity, item.ConsumedQuantity, item.Quantity - item.ConsumedQuantity, item.State, item.IdempotencyKey,
        item.ReferenceDocumentType, item.ReferenceDocumentId, item.CreatedAtUtc, item.ReleasedAtUtc, item.ConsumedAtUtc, item.ConcurrencyStamp);

    private static async Task AssertDurableResult(TestScope scope, object expected)
    {
        if (expected is StockMoveBatchDto batch)
        {
            foreach (var move in batch.Items) await AssertDurableResult(scope, move);
            return;
        }
        await using var reader = scope.ReadInventory();
        object actual = expected switch
        {
            WarehouseDto warehouse => await reader.Warehouses.AsNoTracking().Where(item => item.Id == warehouse.Id)
                .Select(item => new WarehouseDto(item.Id, item.Code, item.Name, item.IsActive, item.ConcurrencyStamp)).SingleAsync(),
            LocationDto location => await reader.Locations.AsNoTracking().Where(item => item.Id == location.Id)
                .Select(item => new LocationDto(item.Id, item.WarehouseId, item.Code, item.Name, item.Type, item.IsActive, item.ConcurrencyStamp)).SingleAsync(),
            StockMoveDto move => MoveDto(await reader.StockMoves.AsNoTracking().SingleAsync(item => item.Id == move.Id)),
            ReservationDto reservation => ReservationDto(await reader.Reservations.AsNoTracking().SingleAsync(item => item.Id == reservation.Id)),
            _ => throw new ArgumentOutOfRangeException(nameof(expected))
        };
        Assert.Equal(expected, actual);
    }

    private static async Task<string> Snapshot(TestScope scope)
    {
        await using var inventory = scope.ReadInventory();
        await using var platform = scope.ReadPlatform();
        return JsonSerializer.Serialize(new
        {
            Warehouses = await inventory.Warehouses.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Locations = await inventory.Locations.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Moves = await inventory.StockMoves.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Reservations = await inventory.Reservations.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Sequences = await platform.DocumentSequences.AsNoTracking().Where(item => item.CompanyId == scope.CompanyId).OrderBy(item => item.Key).ToArrayAsync()
        });
    }

    private static async Task AssertPlatformCounts(TestScope scope, int audits, int events)
    {
        await using var platform = scope.ReadPlatform();
        Assert.Equal(audits, await platform.AuditLogs.CountAsync(item => item.CompanyId == scope.CompanyId));
        Assert.Equal(events, await platform.OutboxMessages.CountAsync(item => item.CompanyId == scope.CompanyId));
    }

    private static void AssertClean(TestScope scope)
    {
        Assert.Empty(scope.Database.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Database.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    private static Product NewProduct(Guid tenant, Guid company, string sku) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant,
        CompanyId = company,
        Type = ProductType.Product,
        Sku = sku,
        Name = $"Atomic product {sku}",
        SalePrice = 10m,
        Cost = 5m,
        CurrencyCode = "USD",
        SalesUnitCode = "EA",
        PurchaseUnitCode = "EA",
        TracksInventory = true,
        CreatedAtUtc = FixedClock.Now,
        UpdatedAtUtc = FixedClock.Now,
        ConcurrencyStamp = Stamp()
    };

    private static StockMove NewMove(TestScope scope, string number, StockMoveType type, StockMoveState state,
        decimal quantity, Guid? source, Guid? destination) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            Number = number,
            Type = type,
            State = state,
            ProductId = scope.ProductA.Id,
            ProductSku = scope.ProductA.Sku,
            UnitCode = "EA",
            Quantity = quantity,
            SourceLocationId = source,
            DestinationLocationId = destination,
            BusinessDate = BusinessDate,
            CreatedAtUtc = FixedClock.Now,
            CompletedAtUtc = state == StockMoveState.Completed ? FixedClock.Now : null,
            CreatedBy = scope.UserId,
            CompletedBy = state == StockMoveState.Completed ? scope.UserId : null,
            ConcurrencyStamp = Stamp()
        };

    private sealed class TestScope(ServiceProvider provider, AsyncServiceScope services, string connectionString,
        Guid tenantId, Guid companyId, Guid userId) : IAsyncDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid CompanyId { get; } = companyId;
        public Guid UserId { get; } = userId;
        public Guid ContactId { get; private set; }
        public Guid SalesOrderId { get; private set; }
        public Guid PurchaseOrderId { get; private set; }
        public Product ProductA { get; private set; } = null!;
        public Product ProductB { get; private set; } = null!;
        public Warehouse Warehouse { get; private set; } = null!;
        public StockLocation InternalA { get; private set; } = null!;
        public StockLocation InternalB { get; private set; } = null!;
        public StockLocation Supplier { get; private set; } = null!;
        public StockLocation Customer { get; private set; } = null!;
        public StockLocation Gain { get; private set; } = null!;
        public StockLocation Loss { get; private set; } = null!;
        public StockMove SeedMove { get; private set; } = null!;
        public StockMove CompletedMove { get; private set; } = null!;
        public StockReservation SeedReservation { get; private set; } = null!;
        public IRelationalDatabaseSession Session => services.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        public PlatformDbContext Platform => services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        public InventoryDbContext Database => services.ServiceProvider.GetRequiredService<InventoryDbContext>();
        public ContactsDbContext Contacts => services.ServiceProvider.GetRequiredService<ContactsDbContext>();
        public ProductsDbContext Products => services.ServiceProvider.GetRequiredService<ProductsDbContext>();
        public SalesDbContext Sales => services.ServiceProvider.GetRequiredService<SalesDbContext>();
        public PurchasingDbContext Purchasing => services.ServiceProvider.GetRequiredService<PurchasingDbContext>();
        public IInventoryService Inventory => services.ServiceProvider.GetRequiredService<IInventoryService>();
        public IContactService ContactService => services.ServiceProvider.GetRequiredService<IContactService>();
        public IProductService ProductService => services.ServiceProvider.GetRequiredService<IProductService>();
        public IBusinessTransactionCoordinator Transactions => services.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        public InjectingAudit Audit => services.ServiceProvider.GetRequiredService<InjectingAudit>();
        public ObservingOutbox Outbox => services.ServiceProvider.GetRequiredService<ObservingOutbox>();
        public ObservingSequence Sequences => services.ServiceProvider.GetRequiredService<ObservingSequence>();
        public OneShotCommandHook CommandHook => services.ServiceProvider.GetRequiredService<OneShotCommandHook>();
        public OneShotSaveHook SaveHook => services.ServiceProvider.GetRequiredService<OneShotSaveHook>();
        public PlatformDbContext ReadPlatform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId));
        public InventoryDbContext ReadInventory() => new(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public SalesDbContext ReadSales() => new(new DbContextOptionsBuilder<SalesDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public PurchasingDbContext ReadPurchasing() => new(new DbContextOptionsBuilder<PurchasingDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public ContactsDbContext ReadContacts() => new(new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public ProductsDbContext ReadProducts() => new(new DbContextOptionsBuilder<ProductsDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public TestScope CreatePeer() => Build(connectionString, TenantId, CompanyId, UserId);

        public static async Task<TestScope> CreateAsync(string connectionString, Guid? existingTenant = null)
        {
            var scope = Build(connectionString, existingTenant ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            try
            {
                if (existingTenant is null)
                    scope.Platform.Tenants.Add(new Tenant { Id = scope.TenantId, Name = "Inventory atomicity", Slug = $"atomic-{scope.TenantId:N}", TimeZoneId = "UTC", IsActive = true, CreatedAtUtc = FixedClock.Now });
                scope.Platform.Companies.Add(new Company { Id = scope.CompanyId, TenantId = scope.TenantId, Name = "Inventory atomicity", Code = scope.CompanyId.ToString("N")[..20], BaseCurrencyCode = "USD", TimeZoneId = "UTC", IsActive = true });
                scope.Platform.CompanyCurrencies.Add(new CompanyCurrency { Id = Guid.NewGuid(), TenantId = scope.TenantId, CompanyId = scope.CompanyId, Code = "USD", Name = "US dollar", Symbol = "$", MinorUnits = 2, IsActive = true, ConcurrencyStamp = Stamp() });
                await scope.Platform.SaveChangesAsync();
                scope.Platform.ChangeTracker.Clear();
                var contact = new Contact
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    Kind = ContactKind.Organization,
                    DisplayName = "Atomic partner",
                    OrganizationName = "Atomic partner",
                    IsCustomer = true,
                    IsSupplier = true,
                    CreatedAtUtc = FixedClock.Now,
                    UpdatedAtUtc = FixedClock.Now,
                    ConcurrencyStamp = Stamp()
                };
                scope.ContactId = contact.Id;
                scope.Contacts.Contacts.Add(contact);
                await scope.Contacts.SaveChangesAsync();
                scope.Contacts.ChangeTracker.Clear();
                scope.ProductA = NewProduct(scope.TenantId, scope.CompanyId, "SEED-A");
                scope.ProductB = NewProduct(scope.TenantId, scope.CompanyId, "SEED-B");
                scope.Products.Products.AddRange(scope.ProductA, scope.ProductB);
                await scope.Products.SaveChangesAsync();
                scope.Products.ChangeTracker.Clear();
                await scope.SeedDocuments();
                scope.Warehouse = new() { Id = Guid.NewGuid(), TenantId = scope.TenantId, CompanyId = scope.CompanyId, Code = "MAIN", Name = "Main warehouse", ConcurrencyStamp = Stamp() };
                StockLocation Location(string code, LocationType type) => new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    WarehouseId = type == LocationType.Internal ? scope.Warehouse.Id : null,
                    Code = code,
                    Name = code,
                    Type = type,
                    ConcurrencyStamp = Stamp()
                };
                scope.InternalA = Location("INTERNAL-A", LocationType.Internal);
                scope.InternalB = Location("INTERNAL-B", LocationType.Internal);
                scope.Supplier = Location("SUPPLIER", LocationType.Supplier);
                scope.Customer = Location("CUSTOMER", LocationType.Customer);
                scope.Gain = Location("GAIN", LocationType.InventoryGain);
                scope.Loss = Location("LOSS", LocationType.InventoryLoss);
                scope.Database.Warehouses.Add(scope.Warehouse);
                scope.Database.Locations.AddRange(scope.InternalA, scope.InternalB, scope.Supplier, scope.Customer, scope.Gain, scope.Loss);
                var opening = NewMove(scope, "OPENING", StockMoveType.Adjustment, StockMoveState.Completed, 10, scope.Gain.Id, scope.InternalA.Id);
                scope.CompletedMove = NewMove(scope, "RECEIPT-SEED", StockMoveType.Receipt, StockMoveState.Completed, 1, scope.Supplier.Id, scope.InternalA.Id);
                scope.SeedMove = NewMove(scope, "DRAFT-SEED", StockMoveType.Adjustment, StockMoveState.Draft, 2, scope.Gain.Id, scope.InternalA.Id);
                scope.SeedReservation = new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    ProductId = scope.ProductA.Id,
                    LocationId = scope.InternalA.Id,
                    Quantity = 4,
                    State = ReservationState.Active,
                    IdempotencyKey = "seed-reservation",
                    ReferenceDocumentType = "SalesOrder",
                    ReferenceDocumentId = scope.SalesOrderId,
                    CreatedAtUtc = FixedClock.Now.AddMinutes(-1),
                    ConcurrencyStamp = Stamp()
                };
                scope.Database.StockMoves.AddRange(opening, scope.CompletedMove, scope.SeedMove);
                scope.Database.Reservations.Add(scope.SeedReservation);
                await scope.Database.SaveChangesAsync();
                scope.Database.ChangeTracker.Clear();
                return scope;
            }
            catch { await scope.DisposeAsync(); throw; }
        }

        private async Task SeedDocuments()
        {
            var quote = new Quotation
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Number = "QUO-SEED",
                CustomerId = ContactId,
                CustomerName = "Atomic partner",
                CurrencyCode = "USD",
                BaseCurrencyCode = "USD",
                ExchangeRate = 1,
                QuotationDate = BusinessDate,
                State = QuotationState.Accepted,
                Subtotal = 500,
                Total = 500,
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
                Number = "SO-SEED",
                CustomerId = ContactId,
                CustomerName = "Atomic partner",
                CurrencyCode = "USD",
                BaseCurrencyCode = "USD",
                ExchangeRate = 1,
                OrderDate = BusinessDate,
                State = SalesOrderState.Confirmed,
                Subtotal = 500,
                Total = 500,
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
                ProductId = ProductA.Id,
                ProductSku = ProductA.Sku,
                Description = ProductA.Name,
                UnitCode = "EA",
                Quantity = 50,
                UnitPrice = 10,
                Subtotal = 500,
                Total = 500
            });
            SalesOrderId = sales.Id;
            Sales.Quotations.Add(quote);
            Sales.SalesOrders.Add(sales);
            await Sales.SaveChangesAsync();
            Sales.ChangeTracker.Clear();
            var purchase = new PurchaseOrder
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                CompanyId = CompanyId,
                Number = "PO-SEED",
                SupplierId = ContactId,
                SupplierName = "Atomic partner",
                CurrencyCode = "USD",
                BaseCurrencyCode = "USD",
                ExchangeRate = 1,
                OrderDate = BusinessDate,
                State = PurchaseOrderState.Confirmed,
                Subtotal = 250,
                Total = 250,
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
                ProductId = ProductA.Id,
                ProductSku = ProductA.Sku,
                Description = ProductA.Name,
                UnitCode = "EA",
                Quantity = 50,
                UnitPrice = 5,
                Subtotal = 250,
                Total = 250
            });
            PurchaseOrderId = purchase.Id;
            Purchasing.PurchaseOrders.Add(purchase);
            await Purchasing.SaveChangesAsync();
            Purchasing.ChangeTracker.Clear();
        }

        private static TestScope Build(string connection, Guid tenant, Guid company, Guid user)
        {
            var registry = new IntegrationEventRegistry();
            new InventoryModule().ConfigureIntegrationEvents(registry);
            var registrations = new ServiceCollection();
            registrations.AddSingleton<ITenantContext>(new TestTenant(tenant));
            registrations.AddSingleton<ICompanyContext>(new TestCompany(company));
            registrations.AddSingleton<IClock>(new FixedClock());
            registrations.AddSingleton<IIntegrationEventRegistry>(registry);
            registrations.AddSingleton<IHttpContextAccessor>(new TestHttpContextAccessor
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test")), TraceIdentifier = "inventory-atomicity-test" }
            });
            registrations.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connection));
            registrations.AddDbContext<PlatformDbContext>((provider, options) => options.UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection));
            var configuration = new ConfigurationBuilder().Build();
            new ContactsModule().ConfigureServices(registrations, configuration);
            new ProductsModule().ConfigureServices(registrations, configuration);
            new SalesModule().ConfigureServices(registrations, configuration);
            new PurchasingModule().ConfigureServices(registrations, configuration);
            new InventoryModule().ConfigureServices(registrations, configuration);
            registrations.AddScoped<OneShotSaveHook>();
            registrations.AddScoped<OneShotCommandHook>();
            registrations.AddDbContext<InventoryDbContext>((provider, options) => options.AddInterceptors(provider.GetRequiredService<OneShotSaveHook>(), provider.GetRequiredService<OneShotCommandHook>()));
            registrations.AddScoped<IBusinessTransactionCoordinator, BusinessTransactionCoordinator>();
            registrations.AddScoped<AuditLogService>();
            registrations.AddScoped<InjectingAudit>();
            registrations.AddScoped<IAuditLogWriter>(provider => provider.GetRequiredService<InjectingAudit>());
            registrations.AddScoped<PostgresOutboxWriter>();
            registrations.AddScoped<ObservingOutbox>();
            registrations.AddScoped<IOutboxWriter>(provider => provider.GetRequiredService<ObservingOutbox>());
            registrations.AddScoped<PostgresSequenceService>();
            registrations.AddScoped<ObservingSequence>();
            registrations.AddScoped<ISequenceService>(provider => provider.GetRequiredService<ObservingSequence>());
            registrations.AddScoped<ICurrencyConfigurationService, CurrencyConfigurationService>();
            var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            return new(provider, provider.CreateAsyncScope(), connection, tenant, company, user);
        }

        public async ValueTask DisposeAsync() { await services.DisposeAsync(); await provider.DisposeAsync(); }
    }

    private sealed class InjectingAudit(AuditLogService realAudit) : IAuditLogWriter
    {
        public int FlushCalls { get; private set; }
        public int? FailOnFlush { get; set; }
        public Action? AfterFlush { get; set; }
        public void Append(AuditWriteEntry entry) => realAudit.Append(entry);
        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            await realAudit.FlushAsync(cancellationToken);
            AfterFlush?.Invoke();
            if (FlushCalls == FailOnFlush) { FailOnFlush = null; throw new InjectedAuditException(); }
        }
    }

    private sealed class ObservingOutbox(PostgresOutboxWriter realOutbox) : IOutboxWriter
    {
        public List<IIntegrationEvent> Attempts { get; } = [];
        public int? FailAfterAdd { get; set; }
        public void Add<TEvent>(TEvent message) where TEvent : IIntegrationEvent
        {
            realOutbox.Add(message);
            Attempts.Add(message);
            if (Attempts.Count == FailAfterAdd) { FailAfterAdd = null; throw new InjectedOutboxException(); }
        }
    }

    private sealed class ObservingSequence(PostgresSequenceService realSequences) : ISequenceService
    {
        public List<string> Allocations { get; } = [];
        public Func<Task>? AfterAllocation { get; set; }
        public async Task<string> NextAsync(SequenceRequest request, CancellationToken cancellationToken)
        {
            var number = await realSequences.NextAsync(request, cancellationToken);
            Allocations.Add(number);
            if (AfterAllocation is { } callback) { AfterAllocation = null; await callback(); }
            return number;
        }
    }

    private sealed class OneShotCommandHook : DbCommandInterceptor
    {
        public Func<CancellationToken, Task>? BeforeAdvisoryLock { get; set; }
        public bool Executed { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal) && BeforeAdvisoryLock is { } callback)
            { BeforeAdvisoryLock = null; Executed = true; await callback(cancellationToken); }
            return result;
        }
    }

    private sealed class OneShotSaveHook : SaveChangesInterceptor
    {
        public Func<InventoryDbContext, CancellationToken, Task>? BeforeSave { get; set; }
        public Exception? LastFailure { get; private set; }
        public bool Executed { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is { } callback) { BeforeSave = null; Executed = true; await callback((InventoryDbContext)eventData.Context!, cancellationToken); }
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
        public static DateTimeOffset Now { get; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }
}

public sealed class AtomicInventoryPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;
    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("inventory_atomicity_tests");
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
        await using var inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "inventory")).Options);
        await inventory.Database.MigrateAsync();
    }
    public async Task DisposeAsync() { if (_postgres is not null) await _postgres.DisposeAsync(); }
}
