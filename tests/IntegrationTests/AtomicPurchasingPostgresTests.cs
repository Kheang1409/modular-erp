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
using Erp.Modules.Products;
using Erp.Modules.Purchasing;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class AtomicPurchasingPostgresTests(AtomicPurchasingPostgresFixture fixture)
    : IClassFixture<AtomicPurchasingPostgresFixture>
{
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("send")]
    [InlineData("confirm-draft")]
    [InlineData("confirm-sent")]
    [InlineData("cancel-draft")]
    [InlineData("cancel-sent")]
    [InlineData("cancel-confirmed")]
    [InlineData("archive-cancelled")]
    [InlineData("archive-received")]
    [InlineData("reactivate")]
    public async Task FailureAfterRealAuditAndOutboxFlushRestoresHeadersLinesVersionsAndSequencesThenAllowsRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        scope.Audit.FailAfterFlush = true;

        await Assert.ThrowsAsync<InjectedAuditException>(() => mutation.Execute(CancellationToken.None));

        Assert.Equal(1, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        var failedEventIds = scope.Outbox.Attempts.Select(message => message.EventId).ToArray();
        Assert.Equal(mutation.Events.Length, failedEventIds.Length);

        var committed = await mutation.Execute(CancellationToken.None);

        AssertOutcome(scope, operation, committed);
        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, 1, mutation.Events.Length);
        await using var platform = scope.ReadPlatform();
        var audit = await platform.AuditLogs.SingleAsync(entry => entry.CompanyId == scope.CompanyId);
        Assert.Equal(mutation.Action, audit.Action);
        Assert.Equal(scope.TenantId, audit.TenantId);
        Assert.Equal(scope.CompanyId, audit.CompanyId);
        Assert.Equal(scope.UserId, audit.UserId);
        Assert.Equal("purchasing-atomicity-test", audit.CorrelationId);
        Assert.Equal(FixedClock.Now, audit.TimestampUtc);
        AssertDto(committed, JsonSerializer.Deserialize<PurchaseOrderDto>(audit.AfterJson!)!);
        var messages = await platform.OutboxMessages.Where(entry => entry.CompanyId == scope.CompanyId).ToListAsync();
        Assert.Equal(mutation.Events.Order(), messages.Select(message => message.EventType).Order());
        foreach (var message in messages)
        {
            AssertEvent(scope, message, message.EventType, committed);
            Assert.Contains(message.Id, scope.Outbox.Attempts.Skip(failedEventIds.Length).Select(item => item.EventId));
            Assert.DoesNotContain(message.Id, failedEventIds);
        }
        if (operation == "create")
        {
            Assert.Equal(2, await platform.DocumentSequences.Where(item => item.CompanyId == scope.CompanyId && item.Key == "purchasing.order").Select(item => item.NextValue).SingleAsync());
            Assert.Equal(scope.Sequences.Allocations[0], scope.Sequences.Allocations[1]);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("confirm-draft")]
    [InlineData("archive-cancelled")]
    public async Task CancellationAfterRealPlatformFlushRollsBackEveryWriteFamilyAndAllowsUncancelledRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        using var cancellation = new CancellationTokenSource();
        scope.Audit.AfterFlush = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.Execute(cancellation.Token));

        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        scope.Audit.AfterFlush = null;
        await AssertDurableResult(scope, await mutation.Execute(CancellationToken.None));
        await AssertPlatformCounts(scope, 1, mutation.Events.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSequenceDefinitionAndCounterAllocationJoinBusinessRollback(bool existingDefinition)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        if (existingDefinition) await SeedSequence(scope, 41);
        var original = await Snapshot(scope);
        scope.Audit.FailAfterFlush = true;

        await Assert.ThrowsAsync<InjectedAuditException>(() => scope.Orders.CreateAsync(Request(scope), CancellationToken.None));

        Assert.Single(scope.Sequences.Allocations);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        var committed = await scope.Orders.CreateAsync(Request(scope), CancellationToken.None);
        Assert.Equal($"PO-2026-{(existingDefinition ? 41 : 1):D6}", committed.Number);
        Assert.Equal(scope.Sequences.Allocations[0], scope.Sequences.Allocations[1]);
        await using var platform = scope.ReadPlatform();
        var sequence = await platform.DocumentSequences.SingleAsync(item => item.CompanyId == scope.CompanyId && item.Key == "purchasing.order");
        Assert.Equal(existingDefinition ? 42 : 2, sequence.NextValue);
        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("archived", false)]
    [InlineData("archived", true)]
    [InlineData("foreign-company", false)]
    [InlineData("foreign-company", true)]
    [InlineData("foreign-tenant", false)]
    [InlineData("foreign-tenant", true)]
    [InlineData("quantity", false)]
    [InlineData("quantity", true)]
    [InlineData("price", false)]
    [InlineData("price", true)]
    [InlineData("discount", false)]
    [InlineData("discount", true)]
    [InlineData("tax", false)]
    [InlineData("tax", true)]
    [InlineData("currency", false)]
    [InlineData("currency", true)]
    public async Task LateProductOrLineValidationAfterRealAllocationRollsBackTheNumberAndAllowsRetry(string invalid, bool existingDefinition)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var foreign = await TestScope.CreateAsync(fixture.ConnectionString, invalid == "foreign-company" ? scope.TenantId : null);
        var archived = NewProduct(scope.TenantId, scope.CompanyId, "ARCHIVED");
        archived.IsArchived = true;
        scope.Products.Products.Add(archived);
        await scope.Products.SaveChangesAsync();
        scope.Products.ChangeTracker.Clear();
        if (existingDefinition) await SeedSequence(scope, 41);
        var request = Request(scope);
        var line = request.Lines[0];
        line = invalid switch
        {
            "missing" => line with { ProductId = Guid.NewGuid() },
            "archived" => line with { ProductId = archived.Id },
            "foreign-company" or "foreign-tenant" => line with { ProductId = foreign.ProductA.Id },
            "quantity" => line with { Quantity = 0 },
            "price" => line with { UnitPrice = -1 },
            "discount" => line with { DiscountPercent = 101 },
            "tax" => line with { TaxPercent = -1 },
            _ => line with { UnitPrice = null }
        };
        request = request with { CurrencyCode = invalid == "currency" ? "EUR" : "USD", Lines = [line] };
        var original = await Snapshot(scope);

        var failure = await Assert.ThrowsAsync<PurchasingValidationException>(() => scope.Orders.CreateAsync(request, CancellationToken.None));

        Assert.Contains("lines", failure.Errors.Keys);
        Assert.Single(scope.Sequences.Allocations);
        Assert.Equal($"PO-2026-{(existingDefinition ? 41 : 1):D6}", scope.Sequences.Allocations[0]);
        Assert.Equal(0, scope.Audit.FlushCalls);
        Assert.Empty(scope.Outbox.Attempts);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        var retried = await scope.Orders.CreateAsync(Request(scope), CancellationToken.None);
        Assert.Equal($"PO-2026-{(existingDefinition ? 41 : 1):D6}", retried.Number);
        await AssertDurableResult(scope, retried);
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RealSqlLineFailureRestoresDeletedLinesHeaderAndSequenceBeforeValidRetry(bool update, bool existingDefinition)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        if (existingDefinition) await SeedSequence(scope, 41);
        var original = await Snapshot(scope);
        var valid = Request(scope, update ? scope.SeedOrder.ConcurrencyStamp : null);
        var invalid = valid with { Lines = [valid.Lines[0] with { Description = new string('x', 501) }] };
        Task<PurchaseOrderDto> Execute(PurchaseOrderRequest request) => update
            ? scope.Orders.UpdateAsync(scope.SeedOrder.Id, request, CancellationToken.None)
            : scope.Orders.CreateAsync(request, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => Execute(invalid));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        Assert.Equal(original, await Snapshot(scope));
        Assert.Equal(0, scope.Audit.FlushCalls);
        Assert.Empty(scope.Outbox.Attempts);
        AssertClean(scope);
        var retried = await Execute(valid);
        if (!update)
        {
            Assert.Equal($"PO-2026-{(existingDefinition ? 41 : 1):D6}", retried.Number);
            Assert.Equal(scope.Sequences.Allocations[0], scope.Sequences.Allocations[1]);
        }
        else Assert.Empty(scope.Sequences.Allocations);
        await AssertDurableResult(scope, retried);
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Fact]
    public async Task FailedLineRebuildingAfterMarkingOriginalLinesDeletedClearsTrackingAndPreservesTheWholeDraft()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        var valid = Request(scope, scope.SeedOrder.ConcurrencyStamp);
        foreach (var line in new[] { valid.Lines[0] with { ProductId = Guid.NewGuid() }, valid.Lines[0] with { Quantity = 0 } })
        {
            await Assert.ThrowsAsync<PurchasingValidationException>(() => scope.Orders.UpdateAsync(scope.SeedOrder.Id, valid with { Lines = [line] }, CancellationToken.None));
            Assert.Equal(original, await Snapshot(scope));
            AssertClean(scope);
        }
        await AssertPlatformCounts(scope, 0, 0);
        await AssertDurableResult(scope, await scope.Orders.UpdateAsync(scope.SeedOrder.Id, valid, CancellationToken.None));
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailureWhileStagingEitherConfirmationEventRollsBackTheWholePairAndAllowsRetry(int failAfterAdd)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, "confirm-draft");
        var original = await Snapshot(scope);
        scope.Outbox.FailAfterAdd = failAfterAdd;

        await Assert.ThrowsAsync<InjectedOutboxException>(() => mutation.Execute(CancellationToken.None));

        Assert.Equal(failAfterAdd, scope.Outbox.Attempts.Count);
        Assert.Equal(0, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        var failedIds = scope.Outbox.Attempts.Select(message => message.EventId).ToArray();
        var retried = await mutation.Execute(CancellationToken.None);
        Assert.Equal(PurchaseOrderState.Confirmed, retried.State);
        await AssertDurableResult(scope, retried);
        await AssertPlatformCounts(scope, 1, 2);
        await using var platform = scope.ReadPlatform();
        var messages = await platform.OutboxMessages.Where(message => message.CompanyId == scope.CompanyId).ToListAsync();
        Assert.Equal(mutation.Events.Order(), messages.Select(message => message.EventType).Order());
        Assert.Equal(2, messages.Select(message => message.Id).Distinct().Count());
        foreach (var message in messages)
        {
            AssertEvent(scope, message, message.EventType, retried);
            Assert.DoesNotContain(message.Id, failedIds);
        }
    }

    [Fact]
    public async Task ExistingThreeEventContractsAreStoredOncePerSuccessfulLifecycleWithTheirActualBusinessPayloads()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var created = await scope.Orders.CreateAsync(Request(scope), CancellationToken.None);
        var updated = await scope.Orders.UpdateAsync(created.Id, Request(scope, created.ConcurrencyStamp), CancellationToken.None);
        var sent = await scope.Orders.TransitionAsync(updated.Id, PurchaseOrderState.Sent, updated.ConcurrencyStamp, CancellationToken.None);
        await AssertPlatformCounts(scope, 3, 0);
        var confirmed = await scope.Orders.TransitionAsync(sent.Id, PurchaseOrderState.Confirmed, sent.ConcurrencyStamp, CancellationToken.None);
        await AssertPlatformCounts(scope, 4, 2);
        var cancelled = await scope.Orders.TransitionAsync(confirmed.Id, PurchaseOrderState.Cancelled, confirmed.ConcurrencyStamp, CancellationToken.None);
        var archived = await scope.Orders.SetArchivedAsync(cancelled.Id, cancelled.ConcurrencyStamp, true, CancellationToken.None);
        var reactivated = await scope.Orders.SetArchivedAsync(archived.Id, archived.ConcurrencyStamp, false, CancellationToken.None);

        await AssertPlatformCounts(scope, 7, 3);
        await AssertDurableResult(scope, reactivated);
        await using var platform = scope.ReadPlatform();
        var messages = await platform.OutboxMessages.Where(message => message.CompanyId == scope.CompanyId).ToDictionaryAsync(message => message.EventType);
        AssertEvent(scope, messages["purchasing.purchase_order.confirmed.v1"], "purchasing.purchase_order.confirmed.v1", confirmed);
        AssertEvent(scope, messages["purchasing.goods_receipt.requested.v1"], "purchasing.goods_receipt.requested.v1", confirmed);
        AssertEvent(scope, messages["purchasing.purchase_order.cancelled.v1"], "purchasing.purchase_order.cancelled.v1", cancelled);
        Assert.Equal(scope.Outbox.Attempts.Select(message => message.EventId).Order(), messages.Values.Select(message => message.Id).Order());
        Assert.Equal(3, messages.Values.Select(message => message.Id).Distinct().Count());
        var audits = await platform.AuditLogs.Where(entry => entry.CompanyId == scope.CompanyId).ToDictionaryAsync(entry => entry.Action);
        AssertDto(created, JsonSerializer.Deserialize<PurchaseOrderDto>(audits["purchasing.purchase_order.created"].AfterJson!)!);
        AssertDto(updated, JsonSerializer.Deserialize<PurchaseOrderDto>(audits["purchasing.purchase_order.updated"].AfterJson!)!);
        AssertDto(confirmed, JsonSerializer.Deserialize<PurchaseOrderDto>(audits["purchasing.purchase_order.confirmed"].AfterJson!)!);
        AssertDto(reactivated, JsonSerializer.Deserialize<PurchaseOrderDto>(audits["purchasing.purchase_order.reactivated"].AfterJson!)!);
        Assert.Equal("TAX-ATOMIC", reactivated.SupplierTaxNumber);
        Assert.Equal(new SupplierAddressDto("Billing street", "Suite 1", "Chicago", "IL", "60601", "US"), reactivated.SupplierAddress);
        AssertCommercialSnapshot(updated, sent);
        AssertCommercialSnapshot(sent, confirmed);
        AssertCommercialSnapshot(confirmed, cancelled);
        AssertCommercialSnapshot(cancelled, archived);
        AssertCommercialSnapshot(archived, reactivated);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("send")]
    [InlineData("confirm-draft")]
    [InlineData("cancel-confirmed")]
    [InlineData("archive-cancelled")]
    public async Task StaleVersionsDoNotAllocateNumbersOrWriteAuditsAndPermitCurrentVersionRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);

        await Assert.ThrowsAsync<PurchasingConcurrencyException>(() => VersionedMutation(scope, operation, "stale-version"));

        Assert.Equal(original, await Snapshot(scope));
        Assert.Empty(scope.Sequences.Allocations);
        Assert.Empty(scope.Outbox.Attempts);
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        await AssertDurableResult(scope, await mutation.Execute(CancellationToken.None));
        await AssertPlatformCounts(scope, 1, mutation.Events.Length);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("confirm-draft")]
    [InlineData("archive-cancelled")]
    public async Task IndependentDatabaseWriterWinsAndFailedSaveRestoresLinesAndVersionBeforeRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var winnerStamp = Guid.NewGuid().ToString("N");
        string? winningSnapshot = null;
        scope.SaveHook.BeforeSave = async (_, token) =>
        {
            await using var competing = scope.ReadPurchasing();
            var winner = await competing.PurchaseOrders.SingleAsync(item => item.Id == scope.SeedOrder.Id, token);
            winner.Notes = "Independent winner";
            winner.ConcurrencyStamp = winnerStamp;
            await competing.SaveChangesAsync(token);
            winningSnapshot = await Snapshot(scope);
        };

        await Assert.ThrowsAsync<PurchasingConcurrencyException>(() => mutation.Execute(CancellationToken.None));

        Assert.True(scope.SaveHook.Executed);
        Assert.IsType<DbUpdateConcurrencyException>(scope.SaveHook.LastFailure);
        Assert.Equal(winningSnapshot, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        var retried = await VersionedMutation(scope, operation, winnerStamp);
        await AssertDurableResult(scope, retried);
        await AssertPlatformCounts(scope, 1, mutation.Events.Length);
    }

    [Fact]
    public async Task RealProductDirectorySupportsFiveHundredDistinctLinesAndRejectsFiveHundredAndOneBeforeAllocation()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var products = Enumerable.Range(1, 500).Select(index => NewProduct(scope.TenantId, scope.CompanyId, $"BATCH-{index:D3}")).ToArray();
        scope.Products.Products.AddRange(products);
        await scope.Products.SaveChangesAsync();
        scope.Products.ChangeTracker.Clear();
        var request = Request(scope) with { Lines = products.Select(product => new PurchaseOrderLineRequest(product.Id, 1)).ToArray() };
        var original = await Snapshot(scope);

        var failure = await Assert.ThrowsAsync<PurchasingValidationException>(() => scope.Orders.CreateAsync(
            request with { Lines = [.. request.Lines, new(scope.ProductA.Id, 1)] }, CancellationToken.None));

        Assert.Contains("lines", failure.Errors.Keys);
        Assert.Empty(scope.Sequences.Allocations);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        var created = await scope.Orders.CreateAsync(request, CancellationToken.None);
        Assert.Equal(500, created.Lines.Count);
        Assert.Equal(500, created.Lines.Select(line => line.ProductId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 500), created.Lines.Select(line => line.Sequence));
        Assert.Equal(2500m, created.Total);
        await AssertDurableResult(scope, created);
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Fact]
    public async Task RealSupplierCurrencyAndDateValidationRejectInvalidRequestsBeforeAllocatingAnyNumber()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var otherCompany = await TestScope.CreateAsync(fixture.ConnectionString, scope.TenantId);
        await using var otherTenant = await TestScope.CreateAsync(fixture.ConnectionString);
        var archived = NewContact(scope.TenantId, scope.CompanyId);
        archived.IsArchived = true;
        var customer = NewContact(scope.TenantId, scope.CompanyId);
        customer.IsSupplier = false;
        customer.IsCustomer = true;
        scope.Contacts.Contacts.AddRange(archived, customer);
        await scope.Contacts.SaveChangesAsync();
        scope.Contacts.ChangeTracker.Clear();
        var original = await Snapshot(scope);
        foreach (var supplierId in new[] { Guid.Empty, Guid.NewGuid(), archived.Id, customer.Id, otherCompany.SupplierId, otherTenant.SupplierId })
        {
            var failure = await Assert.ThrowsAsync<PurchasingValidationException>(() => scope.Orders.CreateAsync(Request(scope) with { SupplierId = supplierId }, CancellationToken.None));
            Assert.Contains("supplierId", failure.Errors.Keys);
            AssertClean(scope);
        }
        foreach (var currency in new[] { "ZZZ", "JPY", "GBP" })
        {
            var failure = await Assert.ThrowsAsync<PurchasingValidationException>(() => scope.Orders.CreateAsync(Request(scope) with { CurrencyCode = currency }, CancellationToken.None));
            Assert.Contains("currencyCode", failure.Errors.Keys);
            AssertClean(scope);
        }
        foreach (var invalid in new[]
        {
            (Request(scope) with { OrderDate = default }, "orderDate"),
            (Request(scope) with { ExpectedDeliveryDate = BusinessDate.AddDays(-1) }, "expectedDeliveryDate")
        })
        {
            var failure = await Assert.ThrowsAsync<PurchasingValidationException>(() => scope.Orders.CreateAsync(invalid.Item1, CancellationToken.None));
            Assert.Contains(invalid.Item2, failure.Errors.Keys);
            AssertClean(scope);
        }
        Assert.Empty(scope.Sequences.Allocations);
        Assert.Empty(scope.Outbox.Attempts);
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        await scope.Orders.CreateAsync(Request(scope), CancellationToken.None);
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicReadsAndAllWriteFamiliesPreserveTenantAndCompanyIsolation(bool differentTenant)
    {
        await using var owner = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var hidden = await TestScope.CreateAsync(fixture.ConnectionString, differentTenant ? null : owner.TenantId);
        var original = await Snapshot(owner);
        Assert.Null(await hidden.Orders.GetAsync(owner.SeedOrder.Id, CancellationToken.None));
        Assert.DoesNotContain((await hidden.Orders.SearchAsync(new(IncludeArchived: true), CancellationToken.None)).Items, item => item.Id == owner.SeedOrder.Id);
        await using (var reader = hidden.ReadPurchasing())
            Assert.False(await reader.PurchaseOrderLines.AnyAsync(line => line.PurchaseOrderId == owner.SeedOrder.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Orders.UpdateAsync(owner.SeedOrder.Id, Request(hidden, owner.SeedOrder.ConcurrencyStamp), CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Orders.TransitionAsync(owner.SeedOrder.Id, PurchaseOrderState.Sent, owner.SeedOrder.ConcurrencyStamp, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Orders.SetArchivedAsync(owner.SeedOrder.Id, owner.SeedOrder.ConcurrencyStamp, true, CancellationToken.None));
        var referenceFailure = await Assert.ThrowsAsync<PurchasingValidationException>(() => hidden.Orders.CreateAsync(Request(owner), CancellationToken.None));
        Assert.Contains("supplierId", referenceFailure.Errors.Keys);
        AssertClean(hidden);
        Assert.Empty(hidden.Sequences.Allocations);
        Assert.Equal(original, await Snapshot(owner));
        await AssertPlatformCounts(owner, 0, 0);
        await AssertPlatformCounts(hidden, 0, 0);
        await using (await hidden.Transactions.BeginAsync(hidden.Purchasing, CancellationToken.None))
        {
            hidden.Purchasing.PurchaseOrders.Add(NewOrder(owner, "CROSS-SCOPE"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => hidden.Purchasing.SaveChangesAsync());
        }
        AssertClean(hidden);
        var valid = await hidden.Orders.CreateAsync(Request(hidden), CancellationToken.None);
        Assert.Equal("PO-2026-000001", valid.Number);
        await AssertPlatformCounts(hidden, 1, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContactsProductsAndPurchasingComposeWithinOneOuterTransactionIncludingSequenceAndBothConfirmationEvents(bool commit)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        Assert.Same(scope.Session.Connection, scope.Platform.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Purchasing.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Contacts.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Products.Database.GetDbConnection());
        var original = await Snapshot(scope);
        Guid contactId;
        Guid productId;
        PurchaseOrderDto confirmed;
        await using (var outer = await scope.Transactions.BeginAsync(scope.Purchasing, CancellationToken.None))
        {
            var contact = await scope.ContactService.CreateAsync(new(ContactKind.Organization, "Composed supplier", null, null,
                "Composed supplier", null, null, null, null, IsSupplier: true), CancellationToken.None);
            var product = await scope.ProductService.CreateAsync(new(ProductType.Product, "COMPOSED", "Composed product", null, null,
                12m, 5m, "USD", "EA", "EA", true), CancellationToken.None);
            var order = await scope.Orders.CreateAsync(Request(scope) with { SupplierId = contact.Id, Lines = [new(product.Id, 2)] }, CancellationToken.None);
            confirmed = await scope.Orders.TransitionAsync(order.Id, PurchaseOrderState.Confirmed, order.ConcurrencyStamp, CancellationToken.None);
            Assert.Equal("Composed supplier", confirmed.SupplierName);
            Assert.Equal("Composed product", Assert.Single(confirmed.Lines).Description);
            Assert.Equal(10m, confirmed.Total);
            contactId = contact.Id;
            productId = product.Id;
            Assert.Equal(original, await Snapshot(scope));
            await using (var contacts = scope.ReadContacts()) Assert.False(await contacts.Contacts.AnyAsync(item => item.Id == contactId));
            await using (var products = scope.ReadProducts()) Assert.False(await products.Products.AnyAsync(item => item.Id == productId));
            await AssertPlatformCounts(scope, 0, 0);
            if (commit) await outer.CommitAsync(CancellationToken.None);
        }

        await using (var purchasing = scope.ReadPurchasing()) Assert.Equal(commit, await purchasing.PurchaseOrders.AnyAsync(item => item.Id == confirmed.Id));
        await using (var contacts = scope.ReadContacts()) Assert.Equal(commit, await contacts.Contacts.AnyAsync(item => item.Id == contactId));
        await using (var products = scope.ReadProducts()) Assert.Equal(commit, await products.Products.AnyAsync(item => item.Id == productId));
        await AssertPlatformCounts(scope, commit ? 4 : 0, commit ? 2 : 0);
        if (commit)
        {
            await AssertDurableResult(scope, confirmed);
            await using var platform = scope.ReadPlatform();
            foreach (var message in await platform.OutboxMessages.Where(item => item.CompanyId == scope.CompanyId).ToListAsync())
                AssertEvent(scope, message, message.EventType, confirmed);
        }
        else
        {
            AssertClean(scope);
            Assert.Empty(scope.Contacts.ChangeTracker.Entries());
            Assert.Empty(scope.Products.ChangeTracker.Entries());
            Assert.Null(scope.Contacts.Database.CurrentTransaction);
            Assert.Null(scope.Products.Database.CurrentTransaction);
            Assert.Equal(original, await Snapshot(scope));
            var retried = await scope.Orders.CreateAsync(Request(scope), CancellationToken.None);
            Assert.Equal("PO-2026-000001", retried.Number);
            var retriedConfirmation = await scope.Orders.TransitionAsync(retried.Id, PurchaseOrderState.Confirmed, retried.ConcurrencyStamp, CancellationToken.None);
            await AssertDurableResult(scope, retriedConfirmation);
            await AssertPlatformCounts(scope, 2, 2);
        }
    }

    [Fact]
    public async Task RepeatedDraftEditsReplaceAllLineIdsWithoutViolatingRealSequenceUniqueness()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var stamp = scope.SeedOrder.ConcurrencyStamp;
        var previousIds = scope.SeedOrder.Lines.Select(line => line.Id).ToArray();
        foreach (var lineCount in new[] { 3, 1, 2 })
        {
            var request = Request(scope, stamp) with
            {
                Lines = Enumerable.Range(1, lineCount).Select(index => new PurchaseOrderLineRequest(scope.ProductA.Id, index, 10m, Description: $"Replacement {lineCount}/{index}")).ToArray()
            };
            var updated = await scope.Orders.UpdateAsync(scope.SeedOrder.Id, request, CancellationToken.None);
            Assert.Equal(Enumerable.Range(1, lineCount), updated.Lines.Select(line => line.Sequence));
            Assert.DoesNotContain(updated.Lines, line => previousIds.Contains(line.Id));
            Assert.NotEqual(stamp, updated.ConcurrencyStamp);
            await AssertDurableResult(scope, updated);
            await using var reader = scope.ReadPurchasing();
            Assert.Equal(lineCount, await reader.PurchaseOrderLines.CountAsync(line => line.PurchaseOrderId == scope.SeedOrder.Id));
            Assert.False(await reader.PurchaseOrderLines.AnyAsync(line => previousIds.Contains(line.Id)));
            stamp = updated.ConcurrencyStamp;
            previousIds = updated.Lines.Select(line => line.Id).ToArray();
        }
        Assert.Empty(scope.Outbox.Attempts);
        await AssertPlatformCounts(scope, 3, 0);
    }

    [Fact]
    public async Task InvalidStateTransitionsEditsAndArchivingRemainRejectedWithoutAuditOrOutboxWrites()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        foreach (var (initial, target) in new[]
        {
            (PurchaseOrderState.Draft, PurchaseOrderState.Received),
            (PurchaseOrderState.Sent, PurchaseOrderState.Draft),
            (PurchaseOrderState.Confirmed, PurchaseOrderState.Sent),
            (PurchaseOrderState.PartiallyReceived, PurchaseOrderState.Confirmed),
            (PurchaseOrderState.Received, PurchaseOrderState.Cancelled),
            (PurchaseOrderState.Cancelled, PurchaseOrderState.Confirmed)
        })
        {
            await SetState(initial);
            var original = await Snapshot(scope);
            var failure = await Assert.ThrowsAsync<PurchasingValidationException>(() => scope.Orders.TransitionAsync(
                scope.SeedOrder.Id, target, scope.SeedOrder.ConcurrencyStamp, CancellationToken.None));
            Assert.Contains("state", failure.Errors.Keys);
            Assert.Equal(original, await Snapshot(scope));
            AssertClean(scope);
            if (initial != PurchaseOrderState.Draft)
            {
                var editFailure = await Assert.ThrowsAsync<PurchasingValidationException>(() => scope.Orders.UpdateAsync(
                    scope.SeedOrder.Id, Request(scope, scope.SeedOrder.ConcurrencyStamp), CancellationToken.None));
                Assert.Contains("state", editFailure.Errors.Keys);
                Assert.Equal(original, await Snapshot(scope));
                AssertClean(scope);
            }
            if (initial is not (PurchaseOrderState.Cancelled or PurchaseOrderState.Received))
            {
                var archiveFailure = await Assert.ThrowsAsync<PurchasingValidationException>(() => scope.Orders.SetArchivedAsync(
                    scope.SeedOrder.Id, scope.SeedOrder.ConcurrencyStamp, true, CancellationToken.None));
                Assert.Contains("state", archiveFailure.Errors.Keys);
                Assert.Equal(original, await Snapshot(scope));
                AssertClean(scope);
            }
        }
        Assert.Empty(scope.Sequences.Allocations);
        Assert.Empty(scope.Outbox.Attempts);
        await AssertPlatformCounts(scope, 0, 0);
        await SetState(PurchaseOrderState.Draft);
        await AssertDurableResult(scope, await scope.Orders.TransitionAsync(scope.SeedOrder.Id, PurchaseOrderState.Sent,
            scope.SeedOrder.ConcurrencyStamp, CancellationToken.None));
        await AssertPlatformCounts(scope, 1, 0);

        async Task SetState(PurchaseOrderState state)
        {
            await using var setup = scope.ReadPurchasing();
            (await setup.PurchaseOrders.SingleAsync(item => item.Id == scope.SeedOrder.Id)).State = state;
            await setup.SaveChangesAsync();
        }
    }

    private sealed record Mutation(string Action, string[] Events, Func<CancellationToken, Task<PurchaseOrderDto>> Execute);

    private static async Task<Mutation> PrepareMutation(TestScope scope, string operation)
    {
        await using (var setup = scope.ReadPurchasing())
        {
            var order = await setup.PurchaseOrders.SingleAsync(item => item.Id == scope.SeedOrder.Id);
            if (operation is "confirm-sent" or "cancel-sent") order.State = PurchaseOrderState.Sent;
            if (operation == "cancel-confirmed") order.State = PurchaseOrderState.Confirmed;
            if (operation is "archive-cancelled" or "reactivate") order.State = PurchaseOrderState.Cancelled;
            if (operation == "archive-received") order.State = PurchaseOrderState.Received;
            order.IsArchived = operation == "reactivate";
            await setup.SaveChangesAsync();
        }
        var stamp = scope.SeedOrder.ConcurrencyStamp;
        return operation switch
        {
            "create" => new("purchasing.purchase_order.created", [], token => scope.Orders.CreateAsync(Request(scope), token)),
            "update" => new("purchasing.purchase_order.updated", [], token => scope.Orders.UpdateAsync(scope.SeedOrder.Id, Request(scope, stamp), token)),
            "send" => Transition(PurchaseOrderState.Sent, []),
            "confirm-draft" or "confirm-sent" => Transition(PurchaseOrderState.Confirmed,
                ["purchasing.purchase_order.confirmed.v1", "purchasing.goods_receipt.requested.v1"]),
            "cancel-draft" or "cancel-sent" or "cancel-confirmed" => Transition(PurchaseOrderState.Cancelled, ["purchasing.purchase_order.cancelled.v1"]),
            "archive-cancelled" or "archive-received" or "reactivate" => new(operation == "reactivate" ? "purchasing.purchase_order.reactivated" : "purchasing.purchase_order.archived", [],
                token => scope.Orders.SetArchivedAsync(scope.SeedOrder.Id, stamp, operation != "reactivate", token)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        Mutation Transition(PurchaseOrderState state, string[] events) => new($"purchasing.purchase_order.{state.ToString().ToLowerInvariant()}", events,
            token => scope.Orders.TransitionAsync(scope.SeedOrder.Id, state, stamp, token));
    }

    private static Task<PurchaseOrderDto> VersionedMutation(TestScope scope, string operation, string stamp) => operation switch
    {
        "update" => scope.Orders.UpdateAsync(scope.SeedOrder.Id, Request(scope, stamp), CancellationToken.None),
        "send" => scope.Orders.TransitionAsync(scope.SeedOrder.Id, PurchaseOrderState.Sent, stamp, CancellationToken.None),
        "confirm-draft" => scope.Orders.TransitionAsync(scope.SeedOrder.Id, PurchaseOrderState.Confirmed, stamp, CancellationToken.None),
        "cancel-confirmed" => scope.Orders.TransitionAsync(scope.SeedOrder.Id, PurchaseOrderState.Cancelled, stamp, CancellationToken.None),
        _ => scope.Orders.SetArchivedAsync(scope.SeedOrder.Id, stamp, true, CancellationToken.None)
    };

    private static void AssertOutcome(TestScope scope, string operation, PurchaseOrderDto result)
    {
        Assert.NotEqual(scope.SeedOrder.ConcurrencyStamp, result.ConcurrencyStamp);
        Assert.Equal(scope.SupplierId, result.SupplierId);
        Assert.Equal("Atomic supplier", result.SupplierName);
        Assert.Equal("TAX-ATOMIC", result.SupplierTaxNumber);
        Assert.Equal(new SupplierAddressDto("Billing street", "Suite 1", "Chicago", "IL", "60601", "US"), result.SupplierAddress);
        Assert.Equal(2, result.Lines.Count);
        if (operation is "create" or "update")
        {
            Assert.Equal("USD", result.CurrencyCode);
            Assert.Equal(1m, result.ExchangeRate);
            Assert.Equal(45.4375m, result.Total);
            Assert.Equal("Changed notes", result.Notes);
            Assert.Equal("Changed first line", result.Lines[0].Description);
            Assert.Equal(BusinessDate, result.OrderDate);
            Assert.Equal(BusinessDate.AddDays(10), result.ExpectedDeliveryDate);
        }
        else AssertCommercialSnapshot(Dto(scope.SeedOrder), result);
        var state = operation switch
        {
            "send" => PurchaseOrderState.Sent,
            "confirm-draft" or "confirm-sent" => PurchaseOrderState.Confirmed,
            "cancel-draft" or "cancel-sent" or "cancel-confirmed" or "archive-cancelled" or "reactivate" => PurchaseOrderState.Cancelled,
            "archive-received" => PurchaseOrderState.Received,
            _ => PurchaseOrderState.Draft
        };
        Assert.Equal(state, result.State);
        Assert.Equal(operation is "archive-cancelled" or "archive-received", result.IsArchived);
    }

    private static void AssertCommercialSnapshot(PurchaseOrderDto expected, PurchaseOrderDto actual) =>
        AssertDto(expected, actual with
        {
            State = expected.State,
            IsArchived = expected.IsArchived,
            UpdatedAtUtc = expected.UpdatedAtUtc,
            ConcurrencyStamp = expected.ConcurrencyStamp
        });

    private static void AssertEvent(TestScope scope, OutboxMessage message, string eventType, PurchaseOrderDto result)
    {
        Assert.True(eventType is "purchasing.purchase_order.confirmed.v1" or "purchasing.purchase_order.cancelled.v1" or "purchasing.goods_receipt.requested.v1");
        Assert.Equal(eventType, message.EventType);
        Assert.Equal(scope.TenantId, message.TenantId);
        Assert.Equal(scope.CompanyId, message.CompanyId);
        Assert.Equal("purchasing-atomicity-test", message.CorrelationId);
        Assert.Equal(FixedClock.Now, message.OccurredAtUtc);
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var value = payload.RootElement;
        Assert.Equal(message.Id, value.GetProperty("EventId").GetGuid());
        Assert.Equal(FixedClock.Now, value.GetProperty("OccurredAtUtc").GetDateTimeOffset());
        Assert.Equal(result.Id, value.GetProperty("PurchaseOrderId").GetGuid());
        Assert.Equal(result.Number, value.GetProperty("Number").GetString());
        var detailed = eventType == "purchasing.purchase_order.confirmed.v1";
        Assert.Equal(detailed ? 7 : 4, value.EnumerateObject().Count());
        if (detailed)
        {
            Assert.Equal(result.SupplierId, value.GetProperty("SupplierId").GetGuid());
            Assert.Equal(result.CurrencyCode, value.GetProperty("CurrencyCode").GetString());
            Assert.Equal(result.Total, value.GetProperty("Total").GetDecimal());
        }
    }

    private static DateOnly BusinessDate => new(2026, 10, 18);
    private static PurchaseOrderRequest Request(TestScope scope, string? stamp = null) => new(scope.SupplierId, " usd ", BusinessDate,
        BusinessDate.AddDays(10), " Changed notes ",
        [new(scope.ProductA.Id, 3, 12.5m, 10, 5, " Changed first line "), new(scope.ProductB.Id, 2, 5m)], stamp);

    private static async Task AssertDurableResult(TestScope scope, PurchaseOrderDto expected)
    {
        await using var reader = scope.ReadPurchasing();
        var actual = Dto(await reader.PurchaseOrders.AsNoTracking().Include(item => item.Lines).SingleAsync(item => item.Id == expected.Id));
        AssertDto(expected, actual);
    }

    private static void AssertDto(PurchaseOrderDto expected, PurchaseOrderDto actual)
    {
        Assert.Equal(expected, actual with { Lines = expected.Lines });
        Assert.Equal<PurchaseOrderLineDto>(expected.Lines, actual.Lines);
    }

    private static PurchaseOrderDto Dto(PurchaseOrder item) => new(item.Id, item.Number, item.SupplierId, item.SupplierName,
        item.SupplierTaxNumber, new(item.AddressLine1, item.AddressLine2, item.City, item.StateOrProvince, item.PostalCode, item.CountryCode),
        item.CurrencyCode, item.BaseCurrencyCode, item.ExchangeRate, item.OrderDate, item.ExpectedDeliveryDate, item.State,
        item.Subtotal, item.DiscountTotal, item.TaxTotal, item.Total, item.Notes, item.IsArchived, item.CreatedAtUtc, item.UpdatedAtUtc,
        item.ConcurrencyStamp, item.Lines.OrderBy(line => line.Sequence).Select(line => new PurchaseOrderLineDto(line.Id, line.Sequence,
            line.ProductId, line.ProductSku, line.Description, line.UnitCode, line.Quantity, line.UnitPrice, line.DiscountPercent,
            line.TaxPercent, line.Subtotal, line.DiscountAmount, line.TaxAmount, line.Total)).ToArray());

    private static async Task<string> Snapshot(TestScope scope)
    {
        await using var purchasing = scope.ReadPurchasing();
        await using var platform = scope.ReadPlatform();
        return JsonSerializer.Serialize(new
        {
            Orders = await purchasing.PurchaseOrders.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Lines = await purchasing.PurchaseOrderLines.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
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
        Assert.Empty(scope.Purchasing.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Purchasing.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    private static async Task SeedSequence(TestScope scope, long next)
    {
        scope.Platform.DocumentSequences.Add(new DocumentSequence
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            Key = "purchasing.order",
            PrefixTemplate = "PO-{yyyy}-",
            Padding = 6,
            NextValue = next
        });
        await scope.Platform.SaveChangesAsync();
        scope.Platform.ChangeTracker.Clear();
    }

    private static Contact NewContact(Guid tenant, Guid company) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant,
        CompanyId = company,
        Kind = ContactKind.Organization,
        DisplayName = "Atomic supplier",
        OrganizationName = "Atomic supplier",
        IsSupplier = true,
        TaxNumber = "TAX-ATOMIC",
        BillingAddress = new() { Line1 = "Billing street", Line2 = "Suite 1", City = "Chicago", StateOrProvince = "IL", PostalCode = "60601", CountryCode = "US" },
        ShippingAddress = new() { Line1 = "Shipping street", City = "Austin", StateOrProvince = "TX", PostalCode = "78701", CountryCode = "US" },
        CreatedAtUtc = FixedClock.Now,
        UpdatedAtUtc = FixedClock.Now,
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
    };

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
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
    };

    private static PurchaseOrder NewOrder(TestScope scope, string number) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = scope.TenantId,
        CompanyId = scope.CompanyId,
        Number = number,
        SupplierId = scope.SupplierId,
        SupplierName = "Atomic supplier",
        SupplierTaxNumber = "TAX-ATOMIC",
        AddressLine1 = "Billing street",
        AddressLine2 = "Suite 1",
        City = "Chicago",
        StateOrProvince = "IL",
        PostalCode = "60601",
        CountryCode = "US",
        CurrencyCode = "USD",
        BaseCurrencyCode = "USD",
        ExchangeRate = 1m,
        OrderDate = BusinessDate.AddDays(-10),
        ExpectedDeliveryDate = BusinessDate.AddDays(10),
        State = PurchaseOrderState.Draft,
        Subtotal = 25m,
        DiscountTotal = 2m,
        TaxTotal = .9m,
        Total = 23.9m,
        Notes = "Original notes",
        CreatedAtUtc = FixedClock.Now,
        UpdatedAtUtc = FixedClock.Now,
        CreatedBy = scope.UserId,
        UpdatedBy = scope.UserId,
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
    };

    private sealed class TestScope(ServiceProvider provider, AsyncServiceScope services, string connectionString,
        Guid tenantId, Guid companyId, Guid userId) : IAsyncDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid CompanyId { get; } = companyId;
        public Guid UserId { get; } = userId;
        public Guid SupplierId { get; private set; }
        public Product ProductA { get; private set; } = null!;
        public Product ProductB { get; private set; } = null!;
        public PurchaseOrder SeedOrder { get; private set; } = null!;
        public IRelationalDatabaseSession Session => services.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        public PlatformDbContext Platform => services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        public PurchasingDbContext Purchasing => services.ServiceProvider.GetRequiredService<PurchasingDbContext>();
        public ContactsDbContext Contacts => services.ServiceProvider.GetRequiredService<ContactsDbContext>();
        public ProductsDbContext Products => services.ServiceProvider.GetRequiredService<ProductsDbContext>();
        public IPurchaseOrderService Orders => services.ServiceProvider.GetRequiredService<IPurchaseOrderService>();
        public IContactService ContactService => services.ServiceProvider.GetRequiredService<IContactService>();
        public IProductService ProductService => services.ServiceProvider.GetRequiredService<IProductService>();
        public IBusinessTransactionCoordinator Transactions => services.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        public InjectingAudit Audit => services.ServiceProvider.GetRequiredService<InjectingAudit>();
        public ObservingOutbox Outbox => services.ServiceProvider.GetRequiredService<ObservingOutbox>();
        public ObservingSequence Sequences => services.ServiceProvider.GetRequiredService<ObservingSequence>();
        public OneShotSaveHook SaveHook => services.ServiceProvider.GetRequiredService<OneShotSaveHook>();
        public PlatformDbContext ReadPlatform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId));
        public PurchasingDbContext ReadPurchasing() => new(new DbContextOptionsBuilder<PurchasingDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public ContactsDbContext ReadContacts() => new(new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public ProductsDbContext ReadProducts() => new(new DbContextOptionsBuilder<ProductsDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));

        public static async Task<TestScope> CreateAsync(string connectionString, Guid? existingTenant = null)
        {
            var scope = Build(connectionString, existingTenant ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            try
            {
                if (existingTenant is null)
                    scope.Platform.Tenants.Add(new Tenant { Id = scope.TenantId, Name = "Purchasing atomicity", Slug = $"atomic-{scope.TenantId:N}", TimeZoneId = "UTC", IsActive = true, CreatedAtUtc = FixedClock.Now });
                scope.Platform.Companies.Add(new Company
                {
                    Id = scope.CompanyId,
                    TenantId = scope.TenantId,
                    Name = "Purchasing atomicity",
                    Code = scope.CompanyId.ToString("N")[..20],
                    BaseCurrencyCode = "USD",
                    TimeZoneId = "UTC",
                    IsActive = true
                });
                foreach (var code in new[] { "USD", "EUR", "GBP", "JPY" })
                    scope.Platform.CompanyCurrencies.Add(new CompanyCurrency
                    {
                        Id = Guid.NewGuid(),
                        TenantId = scope.TenantId,
                        CompanyId = scope.CompanyId,
                        Code = code,
                        Name = code,
                        Symbol = code,
                        MinorUnits = 2,
                        IsActive = code != "JPY",
                        ConcurrencyStamp = Guid.NewGuid().ToString("N")
                    });
                scope.Platform.CompanyExchangeRates.Add(new CompanyExchangeRate
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    FromCurrencyCode = "EUR",
                    ToCurrencyCode = "USD",
                    Rate = 1.25m,
                    EffectiveDate = BusinessDate.AddDays(-30)
                });
                await scope.Platform.SaveChangesAsync();
                scope.Platform.ChangeTracker.Clear();
                var supplier = NewContact(scope.TenantId, scope.CompanyId);
                scope.SupplierId = supplier.Id;
                scope.Contacts.Contacts.Add(supplier);
                await scope.Contacts.SaveChangesAsync();
                scope.Contacts.ChangeTracker.Clear();
                scope.ProductA = NewProduct(scope.TenantId, scope.CompanyId, "SEED-A");
                scope.ProductB = NewProduct(scope.TenantId, scope.CompanyId, "SEED-B");
                scope.Products.Products.AddRange(scope.ProductA, scope.ProductB);
                await scope.Products.SaveChangesAsync();
                scope.Products.ChangeTracker.Clear();
                scope.SeedOrder = NewOrder(scope, "PO-SEED");
                scope.SeedOrder.Lines =
                [
                    new() { Id = Guid.NewGuid(), TenantId = scope.TenantId, CompanyId = scope.CompanyId, PurchaseOrderId = scope.SeedOrder.Id,
                        Sequence = 1, ProductId = scope.ProductA.Id, ProductSku = scope.ProductA.Sku, Description = "Original first line",
                        UnitCode = "EA", Quantity = 2, UnitPrice = 10, DiscountPercent = 10, TaxPercent = 5,
                        Subtotal = 20, DiscountAmount = 2, TaxAmount = .9m, Total = 18.9m },
                    new() { Id = Guid.NewGuid(), TenantId = scope.TenantId, CompanyId = scope.CompanyId, PurchaseOrderId = scope.SeedOrder.Id,
                        Sequence = 2, ProductId = scope.ProductB.Id, ProductSku = scope.ProductB.Sku, Description = "Original second line",
                        UnitCode = "EA", Quantity = 1, UnitPrice = 5, Subtotal = 5, Total = 5 }
                ];
                scope.Purchasing.PurchaseOrders.Add(scope.SeedOrder);
                await scope.Purchasing.SaveChangesAsync();
                scope.Purchasing.ChangeTracker.Clear();
                return scope;
            }
            catch { await scope.DisposeAsync(); throw; }
        }

        private static TestScope Build(string connection, Guid tenant, Guid company, Guid user)
        {
            var registry = new IntegrationEventRegistry();
            new PurchasingModule().ConfigureIntegrationEvents(registry);
            var registrations = new ServiceCollection();
            registrations.AddSingleton<ITenantContext>(new TestTenant(tenant));
            registrations.AddSingleton<ICompanyContext>(new TestCompany(company));
            registrations.AddSingleton<IClock>(new FixedClock());
            registrations.AddSingleton<IIntegrationEventRegistry>(registry);
            registrations.AddSingleton<IHttpContextAccessor>(new TestHttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test")),
                    TraceIdentifier = "purchasing-atomicity-test"
                }
            });
            registrations.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connection));
            registrations.AddDbContext<PlatformDbContext>((provider, options) => options.UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection));
            var configuration = new ConfigurationBuilder().Build();
            new ContactsModule().ConfigureServices(registrations, configuration);
            new ProductsModule().ConfigureServices(registrations, configuration);
            new PurchasingModule().ConfigureServices(registrations, configuration);
            registrations.AddScoped<OneShotSaveHook>();
            registrations.AddDbContext<PurchasingDbContext>((provider, options) => options.AddInterceptors(provider.GetRequiredService<OneShotSaveHook>()));
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
        public void Add<TEvent>(TEvent message) where TEvent : IIntegrationEvent
        {
            realOutbox.Add(message);
            Attempts.Add(message);
            if (Attempts.Count == FailAfterAdd)
            {
                FailAfterAdd = null;
                throw new InjectedOutboxException();
            }
        }
    }

    private sealed class ObservingSequence(PostgresSequenceService realSequences) : ISequenceService
    {
        public List<string> Allocations { get; } = [];
        public async Task<string> NextAsync(SequenceRequest request, CancellationToken cancellationToken)
        {
            var number = await realSequences.NextAsync(request, cancellationToken);
            Allocations.Add(number);
            return number;
        }
    }

    private sealed class OneShotSaveHook : SaveChangesInterceptor
    {
        public Func<PurchasingDbContext, CancellationToken, Task>? BeforeSave { get; set; }
        public Exception? LastFailure { get; private set; }
        public bool Executed { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is { } callback) { BeforeSave = null; Executed = true; await callback((PurchasingDbContext)eventData.Context!, cancellationToken); }
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

public sealed class AtomicPurchasingPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;
    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("purchasing_atomicity_tests");
        ConnectionString = _postgres.GetConnectionString();
        await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options);
        await platform.Database.MigrateAsync();
        await using var contacts = new ContactsDbContext(new DbContextOptionsBuilder<ContactsDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "contacts")).Options);
        await contacts.Database.MigrateAsync();
        await using var products = new ProductsDbContext(new DbContextOptionsBuilder<ProductsDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "products")).Options);
        await products.Database.MigrateAsync();
        await using var purchasing = new PurchasingDbContext(new DbContextOptionsBuilder<PurchasingDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "purchasing")).Options);
        await purchasing.Database.MigrateAsync();
    }
    public async Task DisposeAsync() { if (_postgres is not null) await _postgres.DisposeAsync(); }
}
