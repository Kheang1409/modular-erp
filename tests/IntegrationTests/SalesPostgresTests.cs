using Erp.Abstractions.Context;
using Erp.Modules.Sales;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Erp.IntegrationTests;

public sealed class SalesPostgresTests
{
    [Fact]
    public async Task QuotationsAndLinesAreCompanyIsolatedAndConcurrent()
    {
        var postgres = await IntegrationContainers.StartPostgresAsync("sales_tests");
        await using (postgres)
        {
            var tenant = Guid.NewGuid(); var companyA = Guid.NewGuid(); var companyB = Guid.NewGuid(); var quoteId = Guid.NewGuid(); var orderId = Guid.NewGuid(); var options = new DbContextOptionsBuilder<SalesDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            await using (var migration = new SalesDbContext(options)) await migration.Database.MigrateAsync();
            await using (var write = new SalesDbContext(options, new TenantContext(tenant), new CompanyContext(companyA))) { var now = DateTimeOffset.UtcNow; var customerId = Guid.NewGuid(); var quote = new Quotation { Id = quoteId, TenantId = tenant, CompanyId = companyA, Number = "QUO-TEST-000001", CustomerId = customerId, CustomerName = "Customer", CurrencyCode = "USD", BaseCurrencyCode = "USD", ExchangeRate = 1, QuotationDate = DateOnly.FromDateTime(now.UtcDateTime), State = QuotationState.Accepted, Subtotal = 10, Total = 10, CreatedAtUtc = now, UpdatedAtUtc = now, ConcurrencyStamp = Guid.NewGuid().ToString("N") }; quote.Lines.Add(new() { Id = Guid.NewGuid(), TenantId = tenant, CompanyId = companyA, QuotationId = quoteId, Sequence = 1, ProductId = Guid.NewGuid(), ProductSku = "SKU", Description = "Item", UnitCode = "EA", Quantity = 1, UnitPrice = 10, Subtotal = 10, Total = 10 }); var order = new SalesOrder { Id = orderId, TenantId = tenant, CompanyId = companyA, SourceQuotationId = quoteId, Number = "SO-TEST-000001", CustomerId = customerId, CustomerName = "Customer", CurrencyCode = "USD", BaseCurrencyCode = "USD", ExchangeRate = 1, OrderDate = DateOnly.FromDateTime(now.UtcDateTime), State = SalesOrderState.Draft, Subtotal = 10, Total = 10, CreatedAtUtc = now, UpdatedAtUtc = now, ConcurrencyStamp = Guid.NewGuid().ToString("N") }; order.Lines.Add(new() { Id = Guid.NewGuid(), TenantId = tenant, CompanyId = companyA, SalesOrderId = orderId, Sequence = 1, ProductId = Guid.NewGuid(), ProductSku = "SKU", Description = "Snapshot", UnitCode = "EA", Quantity = 1, UnitPrice = 10, Subtotal = 10, Total = 10 }); write.Quotations.Add(quote); write.SalesOrders.Add(order); await write.SaveChangesAsync(); }
            await using var visible = new SalesDbContext(options, new TenantContext(tenant), new CompanyContext(companyA)); await using var hidden = new SalesDbContext(options, new TenantContext(tenant), new CompanyContext(companyB)); Assert.Single((await visible.Quotations.Include(x => x.Lines).SingleAsync()).Lines); Assert.Single((await visible.SalesOrders.Include(x => x.Lines).SingleAsync()).Lines); Assert.Empty(await hidden.Quotations.ToListAsync()); Assert.Empty(await hidden.QuotationLines.ToListAsync()); Assert.Empty(await hidden.SalesOrders.ToListAsync()); Assert.Empty(await hidden.SalesOrderLines.ToListAsync());
            var first = await visible.Quotations.SingleAsync(); await using (var competing = new SalesDbContext(options, new TenantContext(tenant), new CompanyContext(companyA))) { var second = await competing.Quotations.SingleAsync(); second.Notes = "Changed"; second.ConcurrencyStamp = Guid.NewGuid().ToString("N"); await competing.SaveChangesAsync(); }
            first.Notes = "Stale"; first.ConcurrencyStamp = Guid.NewGuid().ToString("N"); await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => visible.SaveChangesAsync());
        }
    }
    private sealed class TenantContext(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class CompanyContext(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
}
