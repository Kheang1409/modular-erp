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

public sealed class AtomicSalesPostgresTests(AtomicSalesPostgresFixture fixture)
    : IClassFixture<AtomicSalesPostgresFixture>
{
    [Theory]
    [InlineData("quote-create")]
    [InlineData("quote-update")]
    [InlineData("quote-send")]
    [InlineData("quote-accept")]
    [InlineData("quote-reject")]
    [InlineData("quote-expire-draft")]
    [InlineData("quote-expire-sent")]
    [InlineData("quote-cancel-draft")]
    [InlineData("quote-cancel-sent")]
    [InlineData("quote-archive")]
    [InlineData("quote-reactivate")]
    [InlineData("order-convert")]
    [InlineData("order-confirm")]
    [InlineData("order-cancel-draft")]
    [InlineData("order-cancel-confirmed")]
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

        var committed = await mutation.Execute(CancellationToken.None);

        AssertOutcome(scope, operation, committed);
        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, 1, mutation.EventType is null ? 0 : 1);
        await using var platform = scope.ReadPlatform();
        var audit = await platform.AuditLogs.SingleAsync(entry => entry.CompanyId == scope.CompanyId);
        Assert.Equal(mutation.Action, audit.Action);
        Assert.Equal(scope.TenantId, audit.TenantId);
        Assert.Equal(scope.CompanyId, audit.CompanyId);
        Assert.Equal(scope.UserId, audit.UserId);
        Assert.Equal("sales-atomicity-test", audit.CorrelationId);
        Assert.Equal(FixedClock.Now, audit.TimestampUtc);
        var audited = JsonSerializer.Deserialize(audit.AfterJson!, committed.GetType());
        Assert.NotNull(audited);
        AssertDto(committed, audited);
        if (mutation.EventType is not null)
        {
            var message = await platform.OutboxMessages.SingleAsync(entry => entry.CompanyId == scope.CompanyId);
            AssertEvent(scope, message, mutation.EventType, committed);
            Assert.Equal(scope.Outbox.Attempts.Last().EventId, message.Id);
            Assert.DoesNotContain(message.Id, failedEventIds);
        }
        else Assert.Empty(scope.Outbox.Attempts);
        if (operation is "quote-create" or "order-convert")
        {
            var key = operation == "quote-create" ? "sales.quotation" : "sales.order";
            Assert.Equal(2, await platform.DocumentSequences.Where(item => item.CompanyId == scope.CompanyId && item.Key == key).Select(item => item.NextValue).SingleAsync());
            Assert.Equal(scope.Sequences.Allocations[0], scope.Sequences.Allocations[1]);
        }
    }

    [Theory]
    [InlineData("quote-create")]
    [InlineData("quote-update")]
    [InlineData("quote-send")]
    [InlineData("quote-archive")]
    [InlineData("order-convert")]
    [InlineData("order-confirm")]
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
        await AssertPlatformCounts(scope, 1, mutation.EventType is null ? 0 : 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealSequenceDefinitionAndCounterAllocationJoinBusinessRollback(bool order, bool existingDefinition)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, order ? "order-convert" : "quote-create");
        var key = order ? "sales.order" : "sales.quotation";
        if (existingDefinition) await SeedSequence(scope, key, order ? "SO-{yyyy}-" : "QUO-{yyyy}-", 41);
        var original = await Snapshot(scope);
        scope.Audit.FailAfterFlush = true;

        await Assert.ThrowsAsync<InjectedAuditException>(() => mutation.Execute(CancellationToken.None));

        Assert.Single(scope.Sequences.Allocations);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        var committed = await mutation.Execute(CancellationToken.None);
        var number = committed is QuotationDto quotation ? quotation.Number : Assert.IsType<SalesOrderDto>(committed).Number;
        Assert.Equal($"{(order ? "SO" : "QUO")}-2026-{(existingDefinition ? 41 : 1):D6}", number);
        Assert.Equal(scope.Sequences.Allocations[0], scope.Sequences.Allocations[1]);
        await using var platform = scope.ReadPlatform();
        var sequence = await platform.DocumentSequences.SingleAsync(item => item.CompanyId == scope.CompanyId && item.Key == key);
        Assert.Equal(existingDefinition ? 42 : 2, sequence.NextValue);
        await AssertDurableResult(scope, committed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("archived")]
    [InlineData("foreign-company")]
    [InlineData("foreign-tenant")]
    [InlineData("quantity")]
    [InlineData("price")]
    [InlineData("discount")]
    [InlineData("tax")]
    [InlineData("currency")]
    public async Task LateProductOrLineValidationAfterRealAllocationRollsBackTheNumberAndAllowsRetry(string invalid)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var foreign = await TestScope.CreateAsync(fixture.ConnectionString, invalid == "foreign-company" ? scope.TenantId : null);
        var archived = NewProduct(scope.TenantId, scope.CompanyId, "ARCHIVED");
        archived.IsArchived = true;
        scope.Products.Products.Add(archived);
        await scope.Products.SaveChangesAsync();
        scope.Products.ChangeTracker.Clear();
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

        var failure = await Assert.ThrowsAsync<SalesValidationException>(() => scope.Quotes.CreateAsync(request, CancellationToken.None));

        Assert.Contains("lines", failure.Errors.Keys);
        Assert.Single(scope.Sequences.Allocations);
        Assert.Equal("QUO-2026-000001", scope.Sequences.Allocations[0]);
        Assert.Equal(0, scope.Audit.FlushCalls);
        Assert.Empty(scope.Outbox.Attempts);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        var retried = await scope.Quotes.CreateAsync(Request(scope), CancellationToken.None);
        Assert.Equal("QUO-2026-000001", retried.Number);
        await AssertDurableResult(scope, retried);
        await AssertPlatformCounts(scope, 1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSqlLineFailureRestoresDeletedLinesHeaderAndSequenceBeforeValidRetry(bool update)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        var valid = Request(scope, update ? scope.SeedQuote.ConcurrencyStamp : null);
        var invalid = valid with { Lines = [valid.Lines[0] with { Description = new string('x', 501) }] };
        Task<QuotationDto> Execute(QuotationRequest request) => update
            ? scope.Quotes.UpdateAsync(scope.SeedQuote.Id, request, CancellationToken.None)
            : scope.Quotes.CreateAsync(request, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => Execute(invalid));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        Assert.Equal(original, await Snapshot(scope));
        Assert.Equal(0, scope.Audit.FlushCalls);
        Assert.Empty(scope.Outbox.Attempts);
        AssertClean(scope);
        await AssertDurableResult(scope, await Execute(valid));
        await AssertPlatformCounts(scope, 1, update ? 0 : 1);
    }

    [Fact]
    public async Task RepeatedDraftEditsReplaceAllLineIdsWithoutViolatingRealSequenceUniqueness()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var stamp = scope.SeedQuote.ConcurrencyStamp;
        var previousIds = scope.SeedQuote.Lines.Select(line => line.Id).ToArray();
        foreach (var lineCount in new[] { 3, 1, 2 })
        {
            var request = Request(scope, stamp) with
            {
                Lines = Enumerable.Range(1, lineCount).Select(index => new QuotationLineRequest(scope.ProductA.Id, index, 10m, Description: $"Replacement {lineCount}/{index}")).ToArray()
            };
            var updated = await scope.Quotes.UpdateAsync(scope.SeedQuote.Id, request, CancellationToken.None);
            Assert.Equal(Enumerable.Range(1, lineCount), updated.Lines.Select(line => line.Sequence));
            Assert.DoesNotContain(updated.Lines, line => previousIds.Contains(line.Id));
            Assert.NotEqual(stamp, updated.ConcurrencyStamp);
            await AssertDurableResult(scope, updated);
            await using var reader = scope.ReadSales();
            Assert.Equal(lineCount, await reader.QuotationLines.CountAsync(line => line.QuotationId == scope.SeedQuote.Id));
            Assert.False(await reader.QuotationLines.AnyAsync(line => previousIds.Contains(line.Id)));
            stamp = updated.ConcurrencyStamp;
            previousIds = updated.Lines.Select(line => line.Id).ToArray();
        }
        Assert.Empty(scope.Outbox.Attempts);
        await AssertPlatformCounts(scope, 3, 0);
    }

    [Fact]
    public async Task FailedLineRebuildingAfterMarkingOriginalLinesDeletedClearsTrackingAndPreservesTheWholeDraft()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        var valid = Request(scope, scope.SeedQuote.ConcurrencyStamp);
        foreach (var line in new[] { valid.Lines[0] with { ProductId = Guid.NewGuid() }, valid.Lines[0] with { Quantity = 0 } })
        {
            await Assert.ThrowsAsync<SalesValidationException>(() => scope.Quotes.UpdateAsync(scope.SeedQuote.Id, valid with { Lines = [line] }, CancellationToken.None));
            Assert.Equal(original, await Snapshot(scope));
            AssertClean(scope);
        }
        await AssertPlatformCounts(scope, 0, 0);
        await AssertDurableResult(scope, await scope.Quotes.UpdateAsync(scope.SeedQuote.Id, valid, CancellationToken.None));
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Fact]
    public async Task ExistingFiveEventContractsAreDurableExactlyOnceWithTheirActualBusinessPayloads()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var created = await scope.Quotes.CreateAsync(Request(scope), CancellationToken.None);
        var updated = await scope.Quotes.UpdateAsync(created.Id, Request(scope, created.ConcurrencyStamp), CancellationToken.None);
        var sent = await scope.Quotes.TransitionAsync(updated.Id, QuotationState.Sent, updated.ConcurrencyStamp, CancellationToken.None);
        var accepted = await scope.Quotes.TransitionAsync(sent.Id, QuotationState.Accepted, sent.ConcurrencyStamp, CancellationToken.None);
        var order = await scope.Orders.ConvertAsync(accepted.Id, new(accepted.ConcurrencyStamp, BusinessDate), CancellationToken.None);
        var confirmed = await scope.Orders.TransitionAsync(order.Id, SalesOrderState.Confirmed, order.ConcurrencyStamp, CancellationToken.None);
        var cancelled = await scope.Orders.TransitionAsync(order.Id, SalesOrderState.Cancelled, confirmed.ConcurrencyStamp, CancellationToken.None);

        await AssertPlatformCounts(scope, 7, 5);
        await AssertDurableResult(scope, accepted);
        await AssertDurableResult(scope, cancelled);
        await using var platform = scope.ReadPlatform();
        var messages = await platform.OutboxMessages.Where(message => message.CompanyId == scope.CompanyId).ToDictionaryAsync(message => message.EventType);
        AssertEvent(scope, messages["sales.quotation.created.v1"], "sales.quotation.created.v1", created);
        AssertEvent(scope, messages["sales.quotation.sent.v1"], "sales.quotation.sent.v1", sent);
        AssertEvent(scope, messages["sales.quotation.accepted.v1"], "sales.quotation.accepted.v1", accepted);
        AssertEvent(scope, messages["sales.order.confirmed.v1"], "sales.order.confirmed.v1", confirmed);
        AssertEvent(scope, messages["sales.order.cancelled.v1"], "sales.order.cancelled.v1", cancelled);
        Assert.Equal(scope.Outbox.Attempts.Select(message => message.EventId).Order(), messages.Values.Select(message => message.Id).Order());
        var audits = await platform.AuditLogs.Where(entry => entry.CompanyId == scope.CompanyId).ToDictionaryAsync(entry => entry.Action);
        AssertDto(updated, JsonSerializer.Deserialize<QuotationDto>(audits["sales.quotation.updated"].AfterJson!)!);
        AssertDto(order, JsonSerializer.Deserialize<SalesOrderDto>(audits["sales.order.created"].AfterJson!)!);
        Assert.Equal(accepted.Id, order.SourceQuotationId);
        Assert.Equal(accepted.Total, order.Total);
        Assert.Equal("TAX-ATOMIC", order.CustomerTaxNumber);
        Assert.Equal(new AddressSnapshotDto("Billing street", "Suite 1", "Chicago", "IL", "60601", "US"), order.BillingAddress);
        Assert.Equal(new AddressSnapshotDto("Shipping street", null, "Austin", "TX", "78701", "US"), order.ShippingAddress);
        Assert.Equal(accepted.Lines.Select(line => (line.ProductId, line.ProductSku, line.Description, line.UnitCode, line.Quantity, line.UnitPrice, line.DiscountPercent, line.TaxPercent, line.Total)),
            order.Lines.Select(line => (line.ProductId, line.ProductSku, line.Description, line.UnitCode, line.Quantity, line.UnitPrice, line.DiscountPercent, line.TaxPercent, line.Total)));
    }

    [Theory]
    [InlineData("quote-update")]
    [InlineData("quote-send")]
    [InlineData("quote-archive")]
    [InlineData("order-convert")]
    [InlineData("order-confirm")]
    public async Task StaleVersionsDoNotAllocateNumbersOrWriteAuditsAndPermitCurrentVersionRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);

        await Assert.ThrowsAsync<SalesConcurrencyException>(() => VersionedMutation(scope, operation, "stale-version"));

        Assert.Equal(original, await Snapshot(scope));
        Assert.Empty(scope.Sequences.Allocations);
        Assert.Empty(scope.Outbox.Attempts);
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        await AssertDurableResult(scope, await mutation.Execute(CancellationToken.None));
        await AssertPlatformCounts(scope, 1, mutation.EventType is null ? 0 : 1);
    }

    [Theory]
    [InlineData("quote-update")]
    [InlineData("quote-send")]
    [InlineData("order-confirm")]
    public async Task IndependentDatabaseWriterWinsAndFailedSaveRestoresLinesAndVersionBeforeRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var winnerStamp = Guid.NewGuid().ToString("N");
        string? winningSnapshot = null;
        scope.SaveHook.BeforeSave = async (_, token) =>
        {
            await using var competing = scope.ReadSales();
            if (operation == "order-confirm")
            {
                var winner = await competing.SalesOrders.SingleAsync(token);
                winner.Notes = "Independent winner"; winner.ConcurrencyStamp = winnerStamp;
            }
            else
            {
                var winner = await competing.Quotations.SingleAsync(item => item.Id == scope.SeedQuote.Id, token);
                winner.Notes = "Independent winner"; winner.ConcurrencyStamp = winnerStamp;
            }
            await competing.SaveChangesAsync(token);
            winningSnapshot = await Snapshot(scope);
        };

        await Assert.ThrowsAsync<SalesConcurrencyException>(() => mutation.Execute(CancellationToken.None));

        Assert.True(scope.SaveHook.Executed);
        Assert.IsType<DbUpdateConcurrencyException>(scope.SaveHook.LastFailure);
        Assert.Equal(winningSnapshot, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        var retried = await VersionedMutation(scope, operation, winnerStamp);
        await AssertDurableResult(scope, retried);
        await AssertPlatformCounts(scope, 1, mutation.EventType is null ? 0 : 1);
    }

    [Fact]
    public async Task RealCompetingConversionAfterPreflightHitsUniqueConstraintAndRollsBackOnlyTheLosingAllocation()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await PrepareMutation(scope, "order-convert");
        SalesOrderDto? winner = null;
        await using var peer = scope.CreatePeer();
        scope.Sequences.BeforeAllocation = async (_, token) =>
        {
            // This runs after the loser's duplicate preflight and before it can lock the sequence row.
            winner = await peer.Orders.ConvertAsync(scope.SeedQuote.Id, new(scope.SeedQuote.ConcurrencyStamp, BusinessDate), token);
        };

        var failure = await Assert.ThrowsAsync<SalesValidationException>(() => scope.Orders.ConvertAsync(scope.SeedQuote.Id,
            new(scope.SeedQuote.ConcurrencyStamp, BusinessDate), CancellationToken.None));

        Assert.Contains("conflict", failure.Errors.Keys);
        var databaseFailure = Assert.IsType<DbUpdateException>(scope.SaveHook.LastFailure);
        var postgres = Assert.IsType<PostgresException>(databaseFailure.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_sales_orders_TenantId_CompanyId_SourceQuotationId", postgres.ConstraintName);
        Assert.NotNull(winner);
        Assert.Equal("SO-2026-000001", winner.Number);
        Assert.Equal("SO-2026-000002", Assert.Single(scope.Sequences.Allocations));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 1, 0);
        await AssertDurableResult(scope, winner);
        await using (var platform = scope.ReadPlatform())
            Assert.Equal(2, await platform.DocumentSequences.Where(item => item.CompanyId == scope.CompanyId && item.Key == "sales.order").Select(item => item.NextValue).SingleAsync());
        await using (var reader = scope.ReadSales())
        {
            Assert.Single(await reader.SalesOrders.Where(item => item.SourceQuotationId == scope.SeedQuote.Id).ToListAsync());
            Assert.Equal(QuotationState.Accepted, (await reader.Quotations.SingleAsync(item => item.Id == scope.SeedQuote.Id)).State);
            Assert.Equal(scope.SeedQuote.ConcurrencyStamp, (await reader.Quotations.SingleAsync(item => item.Id == scope.SeedQuote.Id)).ConcurrencyStamp);
        }
        var duplicate = await Assert.ThrowsAsync<SalesValidationException>(() => scope.Orders.ConvertAsync(scope.SeedQuote.Id,
            new(scope.SeedQuote.ConcurrencyStamp, BusinessDate), CancellationToken.None));
        Assert.Contains("quotationId", duplicate.Errors.Keys);
        AssertClean(scope);
        Assert.Single(scope.Sequences.Allocations);
        var additional = NewQuotation(scope, "QUO-RETRY", QuotationState.Accepted);
        await using (var setup = scope.ReadSales()) { setup.Quotations.Add(additional); await setup.SaveChangesAsync(); }
        var retried = await scope.Orders.ConvertAsync(additional.Id, new(additional.ConcurrencyStamp, BusinessDate), CancellationToken.None);
        Assert.Equal("SO-2026-000002", retried.Number);
        await AssertDurableResult(scope, retried);
        await AssertPlatformCounts(scope, 2, 0);
    }

    [Fact]
    public async Task RealProductDirectorySupportsFiveHundredDistinctLinesAndRejectsFiveHundredAndOneBeforeAllocation()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var products = Enumerable.Range(1, 500).Select(index => NewProduct(scope.TenantId, scope.CompanyId, $"BATCH-{index:D3}")).ToArray();
        scope.Products.Products.AddRange(products);
        await scope.Products.SaveChangesAsync();
        scope.Products.ChangeTracker.Clear();
        var request = Request(scope) with { Lines = products.Select(product => new QuotationLineRequest(product.Id, 1)).ToArray() };
        var original = await Snapshot(scope);

        var failure = await Assert.ThrowsAsync<SalesValidationException>(() => scope.Quotes.CreateAsync(
            request with { Lines = [.. request.Lines, new(scope.ProductA.Id, 1)] }, CancellationToken.None));

        Assert.Contains("lines", failure.Errors.Keys);
        Assert.Empty(scope.Sequences.Allocations);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        var created = await scope.Quotes.CreateAsync(request, CancellationToken.None);
        Assert.Equal(500, created.Lines.Count);
        Assert.Equal(500, created.Lines.Select(line => line.ProductId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 500), created.Lines.Select(line => line.Sequence));
        Assert.Equal(5000m, created.Total);
        await AssertDurableResult(scope, created);
        await AssertPlatformCounts(scope, 1, 1);
    }

    [Fact]
    public async Task RealCustomerAndCurrencyDirectoriesRejectInvalidReferencesBeforeAllocatingAnyNumber()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var otherCompany = await TestScope.CreateAsync(fixture.ConnectionString, scope.TenantId);
        await using var otherTenant = await TestScope.CreateAsync(fixture.ConnectionString);
        var archived = NewContact(scope.TenantId, scope.CompanyId);
        archived.IsArchived = true;
        var supplier = NewContact(scope.TenantId, scope.CompanyId);
        supplier.IsCustomer = false; supplier.IsSupplier = true;
        scope.Contacts.Contacts.AddRange(archived, supplier);
        await scope.Contacts.SaveChangesAsync();
        scope.Contacts.ChangeTracker.Clear();
        var original = await Snapshot(scope);
        foreach (var customerId in new[] { Guid.NewGuid(), archived.Id, supplier.Id, otherCompany.CustomerId, otherTenant.CustomerId })
        {
            var failure = await Assert.ThrowsAsync<SalesValidationException>(() => scope.Quotes.CreateAsync(Request(scope) with { CustomerId = customerId }, CancellationToken.None));
            Assert.Contains("customerId", failure.Errors.Keys);
            AssertClean(scope);
        }
        foreach (var currency in new[] { "ZZZ", "JPY", "GBP" })
        {
            var failure = await Assert.ThrowsAsync<SalesValidationException>(() => scope.Quotes.CreateAsync(Request(scope) with { CurrencyCode = currency }, CancellationToken.None));
            Assert.Contains("currencyCode", failure.Errors.Keys);
            AssertClean(scope);
        }
        Assert.Empty(scope.Sequences.Allocations);
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        await scope.Quotes.CreateAsync(Request(scope), CancellationToken.None);
        await AssertPlatformCounts(scope, 1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicReadsAndAllWriteFamiliesPreserveTenantAndCompanyIsolation(bool differentTenant)
    {
        await using var owner = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var hidden = await TestScope.CreateAsync(fixture.ConnectionString, differentTenant ? null : owner.TenantId);
        var original = await Snapshot(owner);
        Assert.Null(await hidden.Quotes.GetAsync(owner.SeedQuote.Id, CancellationToken.None));
        Assert.Null(await hidden.Orders.GetAsync(owner.SeedOrder.Id, CancellationToken.None));
        Assert.DoesNotContain((await hidden.Quotes.SearchAsync(new(IncludeArchived: true), CancellationToken.None)).Items, item => item.Id == owner.SeedQuote.Id);
        Assert.DoesNotContain((await hidden.Orders.SearchAsync(new(), CancellationToken.None)).Items, item => item.Id == owner.SeedOrder.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Quotes.UpdateAsync(owner.SeedQuote.Id, Request(hidden, owner.SeedQuote.ConcurrencyStamp), CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Quotes.TransitionAsync(owner.SeedQuote.Id, QuotationState.Sent, owner.SeedQuote.ConcurrencyStamp, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Quotes.SetArchivedAsync(owner.SeedQuote.Id, owner.SeedQuote.ConcurrencyStamp, true, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Orders.ConvertAsync(owner.SeedQuote.Id, new(owner.SeedQuote.ConcurrencyStamp, BusinessDate), CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Orders.TransitionAsync(owner.SeedOrder.Id, SalesOrderState.Confirmed, owner.SeedOrder.ConcurrencyStamp, CancellationToken.None));
        var referenceFailure = await Assert.ThrowsAsync<SalesValidationException>(() => hidden.Quotes.CreateAsync(Request(owner), CancellationToken.None));
        Assert.Contains("customerId", referenceFailure.Errors.Keys);
        AssertClean(hidden);
        Assert.Empty(hidden.Sequences.Allocations);
        Assert.Equal(original, await Snapshot(owner));
        await AssertPlatformCounts(owner, 0, 0);
        await AssertPlatformCounts(hidden, 0, 0);
        await using (await hidden.Transactions.BeginAsync(hidden.Sales, CancellationToken.None))
        {
            hidden.Sales.Quotations.Add(NewQuotation(owner, "CROSS-SCOPE", QuotationState.Draft));
            await Assert.ThrowsAsync<InvalidOperationException>(() => hidden.Sales.SaveChangesAsync());
        }
        AssertClean(hidden);
        var valid = await hidden.Quotes.CreateAsync(Request(hidden), CancellationToken.None);
        Assert.Equal("QUO-2026-000001", valid.Number);
        await AssertPlatformCounts(hidden, 1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContactsProductsAndSalesComposeWithinOneOuterTransactionIncludingSequenceAndOutbox(bool commit)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        Assert.Same(scope.Session.Connection, scope.Platform.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Sales.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Contacts.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Products.Database.GetDbConnection());
        var original = await Snapshot(scope);
        Guid contactId;
        Guid productId;
        Guid quotationId;
        await using (var outer = await scope.Transactions.BeginAsync(scope.Sales, CancellationToken.None))
        {
            var contact = await scope.ContactService.CreateAsync(new(ContactKind.Organization, "Composed customer", null, null,
                "Composed customer", null, null, null, null, IsCustomer: true), CancellationToken.None);
            var product = await scope.ProductService.CreateAsync(new(ProductType.Product, "COMPOSED", "Composed product", null, null,
                12m, 5m, "USD", "EA", "EA", true), CancellationToken.None);
            var quotation = await scope.Quotes.CreateAsync(Request(scope) with { CustomerId = contact.Id, Lines = [new(product.Id, 2)] }, CancellationToken.None);
            Assert.Equal("Composed customer", quotation.CustomerName);
            Assert.Equal("Composed product", Assert.Single(quotation.Lines).Description);
            contactId = contact.Id; productId = product.Id; quotationId = quotation.Id;
            Assert.Equal(original, await Snapshot(scope));
            await using (var contacts = scope.ReadContacts()) Assert.False(await contacts.Contacts.AnyAsync(item => item.Id == contactId));
            await using (var products = scope.ReadProducts()) Assert.False(await products.Products.AnyAsync(item => item.Id == productId));
            await AssertPlatformCounts(scope, 0, 0);
            if (commit) await outer.CommitAsync(CancellationToken.None);
        }

        await using (var sales = scope.ReadSales()) Assert.Equal(commit, await sales.Quotations.AnyAsync(item => item.Id == quotationId));
        await using (var contacts = scope.ReadContacts()) Assert.Equal(commit, await contacts.Contacts.AnyAsync(item => item.Id == contactId));
        await using (var products = scope.ReadProducts()) Assert.Equal(commit, await products.Products.AnyAsync(item => item.Id == productId));
        await AssertPlatformCounts(scope, commit ? 3 : 0, commit ? 1 : 0);
        if (!commit)
        {
            AssertClean(scope);
            Assert.Empty(scope.Contacts.ChangeTracker.Entries());
            Assert.Empty(scope.Products.ChangeTracker.Entries());
            Assert.Null(scope.Contacts.Database.CurrentTransaction);
            Assert.Null(scope.Products.Database.CurrentTransaction);
            Assert.Equal(original, await Snapshot(scope));
            var retried = await scope.Quotes.CreateAsync(Request(scope), CancellationToken.None);
            Assert.Equal("QUO-2026-000001", retried.Number);
            await AssertPlatformCounts(scope, 1, 1);
        }
    }

    private sealed record Mutation(string Action, string? EventType, Func<CancellationToken, Task<object>> Execute);

    private static async Task<Mutation> PrepareMutation(TestScope scope, string operation)
    {
        await using (var setup = scope.ReadSales())
        {
            var quote = await setup.Quotations.SingleAsync(item => item.Id == scope.SeedQuote.Id);
            if (operation is "quote-accept" or "quote-reject" or "quote-expire-sent" or "quote-cancel-sent") quote.State = QuotationState.Sent;
            if (operation is "quote-expire-draft" or "quote-expire-sent") quote.ValidUntil = BusinessDate.AddDays(-1);
            if (operation is "quote-archive" or "quote-reactivate") { quote.State = QuotationState.Cancelled; quote.IsArchived = operation == "quote-reactivate"; }
            if (operation == "order-convert") quote.State = QuotationState.Accepted;
            if (operation == "order-cancel-confirmed") (await setup.SalesOrders.SingleAsync()).State = SalesOrderState.Confirmed;
            await setup.SaveChangesAsync();
        }
        var stamp = scope.SeedQuote.ConcurrencyStamp;
        var orderStamp = scope.SeedOrder.ConcurrencyStamp;
        return operation switch
        {
            "quote-create" => new("sales.quotation.created", "sales.quotation.created.v1", token => Box(scope.Quotes.CreateAsync(Request(scope), token))),
            "quote-update" => new("sales.quotation.updated", null, token => Box(scope.Quotes.UpdateAsync(scope.SeedQuote.Id, Request(scope, stamp), token))),
            "quote-send" => QuoteTransition(QuotationState.Sent, "sales.quotation.sent.v1"),
            "quote-accept" => QuoteTransition(QuotationState.Accepted, "sales.quotation.accepted.v1"),
            "quote-reject" => QuoteTransition(QuotationState.Rejected, null),
            "quote-expire-draft" or "quote-expire-sent" => QuoteTransition(QuotationState.Expired, null),
            "quote-cancel-draft" or "quote-cancel-sent" => QuoteTransition(QuotationState.Cancelled, null),
            "quote-archive" or "quote-reactivate" => new(operation == "quote-archive" ? "sales.quotation.archived" : "sales.quotation.reactivated", null,
                token => Box(scope.Quotes.SetArchivedAsync(scope.SeedQuote.Id, stamp, operation == "quote-archive", token))),
            "order-convert" => new("sales.order.created", null, token => Box(scope.Orders.ConvertAsync(scope.SeedQuote.Id, new(stamp, BusinessDate), token))),
            "order-confirm" => new("sales.order.confirmed", "sales.order.confirmed.v1", token => Box(scope.Orders.TransitionAsync(scope.SeedOrder.Id, SalesOrderState.Confirmed, orderStamp, token))),
            "order-cancel-draft" or "order-cancel-confirmed" => new("sales.order.cancelled", "sales.order.cancelled.v1", token => Box(scope.Orders.TransitionAsync(scope.SeedOrder.Id, SalesOrderState.Cancelled, orderStamp, token))),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        Mutation QuoteTransition(QuotationState state, string? eventType) => new($"sales.quotation.{state.ToString().ToLowerInvariant()}", eventType,
            token => Box(scope.Quotes.TransitionAsync(scope.SeedQuote.Id, state, stamp, token)));
    }

    private static Task<object> VersionedMutation(TestScope scope, string operation, string stamp) => operation switch
    {
        "quote-update" => Box(scope.Quotes.UpdateAsync(scope.SeedQuote.Id, Request(scope, stamp), CancellationToken.None)),
        "quote-send" => Box(scope.Quotes.TransitionAsync(scope.SeedQuote.Id, QuotationState.Sent, stamp, CancellationToken.None)),
        "quote-archive" => Box(scope.Quotes.SetArchivedAsync(scope.SeedQuote.Id, stamp, true, CancellationToken.None)),
        "order-convert" => Box(scope.Orders.ConvertAsync(scope.SeedQuote.Id, new(stamp, BusinessDate), CancellationToken.None)),
        _ => Box(scope.Orders.TransitionAsync(scope.SeedOrder.Id, SalesOrderState.Confirmed, stamp, CancellationToken.None))
    };

    private static async Task<object> Box<T>(Task<T> task) => (await task)!;
    private static DateOnly BusinessDate => new(2026, 10, 18);
    private static QuotationRequest Request(TestScope scope, string? stamp = null) => new(scope.CustomerId, " usd ", BusinessDate,
        BusinessDate.AddDays(10), " Changed notes ", " Changed terms ",
        [new(scope.ProductA.Id, 3, 12.5m, 10, 5, " Changed first line "), new(scope.ProductB.Id, 2, 5m)], stamp);

    private static void AssertOutcome(TestScope scope, string operation, object result)
    {
        if (result is QuotationDto quote)
        {
            Assert.NotEqual(scope.SeedQuote.ConcurrencyStamp, quote.ConcurrencyStamp);
            Assert.Equal(scope.CustomerId, quote.CustomerId);
            Assert.Equal("Atomic customer", quote.CustomerName);
            if (operation is "quote-create" or "quote-update")
            {
                Assert.Equal("USD", quote.CurrencyCode);
                Assert.Equal(1m, quote.ExchangeRate);
                Assert.Equal(45.4375m, quote.Total);
                Assert.Equal("Changed notes", quote.Notes);
                Assert.Equal("Changed terms", quote.Terms);
                Assert.Equal("Changed first line", quote.Lines[0].Description);
                Assert.Equal(2, quote.Lines.Count);
            }
            var state = operation switch
            {
                "quote-send" => QuotationState.Sent,
                "quote-accept" => QuotationState.Accepted,
                "quote-reject" => QuotationState.Rejected,
                "quote-expire-draft" or "quote-expire-sent" => QuotationState.Expired,
                "quote-cancel-draft" or "quote-cancel-sent" or "quote-archive" or "quote-reactivate" => QuotationState.Cancelled,
                _ => QuotationState.Draft
            };
            Assert.Equal(state, quote.State);
            Assert.Equal(operation == "quote-archive", quote.IsArchived);
        }
        else
        {
            var order = Assert.IsType<SalesOrderDto>(result);
            Assert.NotEqual(scope.SeedOrder.ConcurrencyStamp, order.ConcurrencyStamp);
            Assert.Equal(operation == "order-convert" ? SalesOrderState.Draft : operation == "order-confirm" ? SalesOrderState.Confirmed : SalesOrderState.Cancelled, order.State);
            Assert.Equal(2, order.Lines.Count);
            if (operation == "order-convert")
            {
                Assert.Equal(scope.SeedQuote.Id, order.SourceQuotationId);
                Assert.Equal(scope.SeedQuote.Total, order.Total);
                Assert.Equal("TAX-ATOMIC", order.CustomerTaxNumber);
                Assert.Equal("Billing street", order.BillingAddress.Line1);
            }
        }
    }

    private static void AssertEvent(TestScope scope, OutboxMessage message, string eventType, object result)
    {
        Assert.Equal(eventType, message.EventType);
        Assert.Equal(scope.TenantId, message.TenantId);
        Assert.Equal(scope.CompanyId, message.CompanyId);
        Assert.Equal("sales-atomicity-test", message.CorrelationId);
        Assert.Equal(FixedClock.Now, message.OccurredAtUtc);
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var value = payload.RootElement;
        Assert.Equal(message.Id, value.GetProperty("EventId").GetGuid());
        Assert.Equal(FixedClock.Now, value.GetProperty("OccurredAtUtc").GetDateTimeOffset());
        var (id, number, customer, currency, total) = result switch
        {
            QuotationDto quote => (quote.Id, quote.Number, quote.CustomerId, quote.CurrencyCode, quote.Total),
            SalesOrderDto order => (order.Id, order.Number, order.CustomerId, order.CurrencyCode, order.Total),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        Assert.Equal(id, value.GetProperty(result is QuotationDto ? "QuotationId" : "SalesOrderId").GetGuid());
        Assert.Equal(number, value.GetProperty("Number").GetString());
        var detailed = eventType is "sales.quotation.created.v1" or "sales.quotation.accepted.v1" or "sales.order.confirmed.v1";
        Assert.Equal(detailed ? 7 : 4, value.EnumerateObject().Count());
        if (detailed)
        {
            Assert.Equal(customer, value.GetProperty("CustomerId").GetGuid());
            Assert.Equal(currency, value.GetProperty("CurrencyCode").GetString());
            Assert.Equal(total, value.GetProperty("Total").GetDecimal());
        }
    }

    private static async Task AssertDurableResult(TestScope scope, object expected)
    {
        await using var reader = scope.ReadSales();
        object actual = expected switch
        {
            QuotationDto quote => QuotationDto(await reader.Quotations.AsNoTracking().Include(item => item.Lines).SingleAsync(item => item.Id == quote.Id)),
            SalesOrderDto order => OrderDto(await reader.SalesOrders.AsNoTracking().Include(item => item.Lines).SingleAsync(item => item.Id == order.Id)),
            _ => throw new ArgumentOutOfRangeException(nameof(expected))
        };
        AssertDto(expected, actual);
    }

    private static void AssertDto(object expected, object actual)
    {
        switch (expected)
        {
            case QuotationDto quotation:
                var actualQuotation = Assert.IsType<QuotationDto>(actual);
                Assert.Equal(quotation, actualQuotation with { Lines = quotation.Lines });
                Assert.Equal<QuotationLineDto>(quotation.Lines, actualQuotation.Lines);
                break;
            case SalesOrderDto order:
                var actualOrder = Assert.IsType<SalesOrderDto>(actual);
                Assert.Equal(order, actualOrder with { Lines = order.Lines });
                Assert.Equal<SalesOrderLineDto>(order.Lines, actualOrder.Lines);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expected));
        }
    }

    private static QuotationDto QuotationDto(Quotation item) => new(item.Id, item.Number, item.CustomerId, item.CustomerName,
        item.CurrencyCode, item.BaseCurrencyCode, item.ExchangeRate, item.QuotationDate, item.ValidUntil, item.State, item.Subtotal,
        item.DiscountTotal, item.TaxTotal, item.Total, item.Notes, item.Terms, item.IsArchived, item.CreatedAtUtc, item.UpdatedAtUtc,
        item.ConcurrencyStamp, item.Lines.OrderBy(line => line.Sequence).Select(line => new QuotationLineDto(line.Id, line.Sequence,
            line.ProductId, line.ProductSku, line.Description, line.UnitCode, line.Quantity, line.UnitPrice, line.DiscountPercent,
            line.TaxPercent, line.Subtotal, line.DiscountAmount, line.TaxAmount, line.Total)).ToArray());

    private static SalesOrderDto OrderDto(SalesOrder item) => new(item.Id, item.SourceQuotationId, item.Number, item.CustomerId,
        item.CustomerName, item.CustomerTaxNumber, new(item.BillingLine1, item.BillingLine2, item.BillingCity, item.BillingStateOrProvince,
            item.BillingPostalCode, item.BillingCountryCode), new(item.ShippingLine1, item.ShippingLine2, item.ShippingCity,
            item.ShippingStateOrProvince, item.ShippingPostalCode, item.ShippingCountryCode), item.CurrencyCode, item.BaseCurrencyCode,
        item.ExchangeRate, item.OrderDate, item.State, item.Subtotal, item.DiscountTotal, item.TaxTotal, item.Total, item.Notes, item.Terms,
        item.CreatedAtUtc, item.UpdatedAtUtc, item.ConcurrencyStamp, item.Lines.OrderBy(line => line.Sequence).Select(line => new SalesOrderLineDto(
            line.Id, line.Sequence, line.ProductId, line.ProductSku, line.Description, line.UnitCode, line.Quantity, line.UnitPrice,
            line.DiscountPercent, line.TaxPercent, line.Subtotal, line.DiscountAmount, line.TaxAmount, line.Total)).ToArray());

    private static async Task<string> Snapshot(TestScope scope)
    {
        await using var sales = scope.ReadSales();
        await using var platform = scope.ReadPlatform();
        return JsonSerializer.Serialize(new
        {
            Quotations = await sales.Quotations.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            QuotationLines = await sales.QuotationLines.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Orders = await sales.SalesOrders.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            OrderLines = await sales.SalesOrderLines.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
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
        Assert.Empty(scope.Sales.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Sales.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    private static async Task SeedSequence(TestScope scope, string key, string prefix, long next)
    {
        scope.Platform.DocumentSequences.Add(new DocumentSequence
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            Key = key,
            PrefixTemplate = prefix,
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
        DisplayName = "Atomic customer",
        OrganizationName = "Atomic customer",
        IsCustomer = true,
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

    private static Quotation NewQuotation(TestScope scope, string number, QuotationState state)
    {
        var item = new Quotation
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            Number = number,
            CustomerId = scope.CustomerId,
            CustomerName = "Atomic customer",
            CurrencyCode = "USD",
            BaseCurrencyCode = "USD",
            ExchangeRate = 1m,
            QuotationDate = BusinessDate.AddDays(-10),
            ValidUntil = BusinessDate.AddDays(10),
            State = state,
            Subtotal = 25m,
            DiscountTotal = 2m,
            TaxTotal = .9m,
            Total = 23.9m,
            Notes = "Original notes",
            Terms = "Original terms",
            CreatedAtUtc = FixedClock.Now,
            UpdatedAtUtc = FixedClock.Now,
            CreatedBy = scope.UserId,
            UpdatedBy = scope.UserId,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
        item.Lines =
        [
            new() { Id = Guid.NewGuid(), TenantId = scope.TenantId, CompanyId = scope.CompanyId, QuotationId = item.Id,
                Sequence = 1, ProductId = scope.ProductA.Id, ProductSku = scope.ProductA.Sku, Description = "Original first line",
                UnitCode = "EA", Quantity = 2, UnitPrice = 10, DiscountPercent = 10, TaxPercent = 5, Subtotal = 20,
                DiscountAmount = 2, TaxAmount = .9m, Total = 18.9m },
            new() { Id = Guid.NewGuid(), TenantId = scope.TenantId, CompanyId = scope.CompanyId, QuotationId = item.Id,
                Sequence = 2, ProductId = scope.ProductB.Id, ProductSku = scope.ProductB.Sku, Description = "Original second line",
                UnitCode = "EA", Quantity = 1, UnitPrice = 5, Subtotal = 5, Total = 5 }
        ];
        return item;
    }

    private static SalesOrder NewOrder(TestScope scope, Quotation source) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = scope.TenantId,
        CompanyId = scope.CompanyId,
        SourceQuotationId = source.Id,
        Number = "SO-SEED",
        CustomerId = scope.CustomerId,
        CustomerName = source.CustomerName,
        CustomerTaxNumber = "TAX-ATOMIC",
        BillingLine1 = "Billing street",
        ShippingLine1 = "Shipping street",
        CurrencyCode = source.CurrencyCode,
        BaseCurrencyCode = source.BaseCurrencyCode,
        ExchangeRate = source.ExchangeRate,
        OrderDate = BusinessDate,
        State = SalesOrderState.Draft,
        Subtotal = source.Subtotal,
        DiscountTotal = source.DiscountTotal,
        TaxTotal = source.TaxTotal,
        Total = source.Total,
        Notes = source.Notes,
        Terms = source.Terms,
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
        public Guid CustomerId { get; private set; }
        public Product ProductA { get; private set; } = null!;
        public Product ProductB { get; private set; } = null!;
        public Quotation SeedQuote { get; private set; } = null!;
        public SalesOrder SeedOrder { get; private set; } = null!;
        public IRelationalDatabaseSession Session => services.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        public PlatformDbContext Platform => services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        public SalesDbContext Sales => services.ServiceProvider.GetRequiredService<SalesDbContext>();
        public ContactsDbContext Contacts => services.ServiceProvider.GetRequiredService<ContactsDbContext>();
        public ProductsDbContext Products => services.ServiceProvider.GetRequiredService<ProductsDbContext>();
        public IQuotationService Quotes => services.ServiceProvider.GetRequiredService<IQuotationService>();
        public ISalesOrderService Orders => services.ServiceProvider.GetRequiredService<ISalesOrderService>();
        public IContactService ContactService => services.ServiceProvider.GetRequiredService<IContactService>();
        public IProductService ProductService => services.ServiceProvider.GetRequiredService<IProductService>();
        public IBusinessTransactionCoordinator Transactions => services.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        public InjectingAudit Audit => services.ServiceProvider.GetRequiredService<InjectingAudit>();
        public ObservingOutbox Outbox => services.ServiceProvider.GetRequiredService<ObservingOutbox>();
        public ObservingSequence Sequences => services.ServiceProvider.GetRequiredService<ObservingSequence>();
        public OneShotSaveHook SaveHook => services.ServiceProvider.GetRequiredService<OneShotSaveHook>();
        public PlatformDbContext ReadPlatform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId));
        public SalesDbContext ReadSales() => new(new DbContextOptionsBuilder<SalesDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public ContactsDbContext ReadContacts() => new(new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public ProductsDbContext ReadProducts() => new(new DbContextOptionsBuilder<ProductsDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));
        public TestScope CreatePeer() => Build(connectionString, TenantId, CompanyId, UserId);

        public static async Task<TestScope> CreateAsync(string connectionString, Guid? existingTenant = null)
        {
            var scope = Build(connectionString, existingTenant ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            try
            {
                if (existingTenant is null)
                    scope.Platform.Tenants.Add(new Tenant { Id = scope.TenantId, Name = "Sales atomicity", Slug = $"atomic-{scope.TenantId:N}", TimeZoneId = "UTC", IsActive = true, CreatedAtUtc = FixedClock.Now });
                scope.Platform.Companies.Add(new Company
                {
                    Id = scope.CompanyId,
                    TenantId = scope.TenantId,
                    Name = "Sales atomicity",
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
                var customer = NewContact(scope.TenantId, scope.CompanyId);
                scope.CustomerId = customer.Id;
                scope.Contacts.Contacts.Add(customer);
                await scope.Contacts.SaveChangesAsync();
                scope.Contacts.ChangeTracker.Clear();
                scope.ProductA = NewProduct(scope.TenantId, scope.CompanyId, "SEED-A");
                scope.ProductB = NewProduct(scope.TenantId, scope.CompanyId, "SEED-B");
                scope.Products.Products.AddRange(scope.ProductA, scope.ProductB);
                await scope.Products.SaveChangesAsync();
                scope.Products.ChangeTracker.Clear();
                scope.SeedQuote = NewQuotation(scope, "QUO-SEED-DRAFT", QuotationState.Draft);
                var source = NewQuotation(scope, "QUO-SEED-ACCEPTED", QuotationState.Accepted);
                scope.SeedOrder = NewOrder(scope, source);
                scope.SeedOrder.Lines = source.Lines.Select(line => new SalesOrderLine
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    CompanyId = scope.CompanyId,
                    SalesOrderId = scope.SeedOrder.Id,
                    Sequence = line.Sequence,
                    ProductId = line.ProductId,
                    ProductSku = line.ProductSku,
                    Description = line.Description,
                    UnitCode = line.UnitCode,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    TaxPercent = line.TaxPercent,
                    Subtotal = line.Subtotal,
                    DiscountAmount = line.DiscountAmount,
                    TaxAmount = line.TaxAmount,
                    Total = line.Total
                }).ToList();
                scope.Sales.Quotations.AddRange(scope.SeedQuote, source);
                scope.Sales.SalesOrders.Add(scope.SeedOrder);
                await scope.Sales.SaveChangesAsync();
                scope.Sales.ChangeTracker.Clear();
                return scope;
            }
            catch { await scope.DisposeAsync(); throw; }
        }

        private static TestScope Build(string connection, Guid tenant, Guid company, Guid user)
        {
            var registry = new IntegrationEventRegistry();
            new SalesModule().ConfigureIntegrationEvents(registry);
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
                    TraceIdentifier = "sales-atomicity-test"
                }
            });
            registrations.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connection));
            registrations.AddDbContext<PlatformDbContext>((provider, options) => options.UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection));
            var configuration = new ConfigurationBuilder().Build();
            new ContactsModule().ConfigureServices(registrations, configuration);
            new ProductsModule().ConfigureServices(registrations, configuration);
            new SalesModule().ConfigureServices(registrations, configuration);
            registrations.AddScoped<OneShotSaveHook>();
            registrations.AddDbContext<SalesDbContext>((provider, options) => options.AddInterceptors(provider.GetRequiredService<OneShotSaveHook>()));
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
        public void Add<TEvent>(TEvent message) where TEvent : IIntegrationEvent { realOutbox.Add(message); Attempts.Add(message); }
    }

    private sealed class ObservingSequence(PostgresSequenceService realSequences) : ISequenceService
    {
        public List<string> Allocations { get; } = [];
        public Func<SequenceRequest, CancellationToken, Task>? BeforeAllocation { get; set; }
        public async Task<string> NextAsync(SequenceRequest request, CancellationToken cancellationToken)
        {
            if (BeforeAllocation is { } callback) { BeforeAllocation = null; await callback(request, cancellationToken); }
            var number = await realSequences.NextAsync(request, cancellationToken);
            Allocations.Add(number);
            return number;
        }
    }

    private sealed class OneShotSaveHook : SaveChangesInterceptor
    {
        public Func<SalesDbContext, CancellationToken, Task>? BeforeSave { get; set; }
        public Exception? LastFailure { get; private set; }
        public bool Executed { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is { } callback) { BeforeSave = null; Executed = true; await callback((SalesDbContext)eventData.Context!, cancellationToken); }
            return result;
        }
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        { LastFailure = eventData.Exception; return Task.CompletedTask; }
        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(ConcurrencyExceptionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { LastFailure = eventData.Exception; return ValueTask.FromResult(result); }
    }

    private sealed class InjectedAuditException : Exception;
    private sealed class TestHttpContextAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class TestTenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class TestCompany(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }
}

public sealed class AtomicSalesPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;
    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("sales_atomicity_tests");
        ConnectionString = _postgres.GetConnectionString();
        await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options);
        await platform.Database.MigrateAsync();
        await using var contacts = new ContactsDbContext(new DbContextOptionsBuilder<ContactsDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "contacts")).Options);
        await contacts.Database.MigrateAsync();
        await using var products = new ProductsDbContext(new DbContextOptionsBuilder<ProductsDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "products")).Options);
        await products.Database.MigrateAsync();
        await using var sales = new SalesDbContext(new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "sales")).Options);
        await sales.Database.MigrateAsync();
    }
    public async Task DisposeAsync() { if (_postgres is not null) await _postgres.DisposeAsync(); }
}
