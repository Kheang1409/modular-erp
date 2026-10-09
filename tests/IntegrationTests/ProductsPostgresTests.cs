using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Currencies;
using Erp.Modules.Products;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class ProductsPostgresTests
{
    [Fact]
    public async Task ProductWorkflowEnforcesIsolationUniquenessConcurrencyAndAudit()
    {
        PostgreSqlContainer postgres;
        try
        {
            postgres = new PostgreSqlBuilder("postgres:18-alpine").WithDatabase("products_tests")
                .WithUsername("erp").WithPassword("integration_only_password").Build();
            await postgres.StartAsync(CancellationToken.None);
        }
        catch (Exception) when (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using (postgres)
        {
            var tenant = Guid.NewGuid();
            var companyA = Guid.NewGuid();
            var companyB = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<ProductsDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            await using (var migration = new ProductsDbContext(options)) await migration.Database.MigrateAsync();

            var audit = new RecordingAudit();
            ProductDto created;
            await using (var context = new ProductsDbContext(options, new TenantContext(tenant), new CompanyContext(companyA)))
            {
                var service = CreateService(context, tenant, companyA, audit);
                var productOptions = await service.GetOptionsAsync(CancellationToken.None);
                Assert.Equal("USD", productOptions.BaseCurrencyCode);
                Assert.Equal("USD", Assert.Single(productOptions.Currencies).Code);
                Assert.Contains(productOptions.Units, unit => unit.Code == "EA" && unit.Name == "Each");
                created = await service.CreateAsync(new(ProductType.Product, " widget-1 ", "Widget", "Stock item", "012345",
                    12.3456m, 4.25m, "usd", "ea", "ea", true), CancellationToken.None);
                Assert.Equal("WIDGET-1", created.Sku);
                Assert.Equal(created.Id, Assert.Single((await service.SearchAsync(new(Search: "widget", TracksInventory: true), CancellationToken.None)).Items).Id);

                await Assert.ThrowsAsync<ProductConflictException>(() => service.CreateAsync(new(ProductType.Product,
                    "WIDGET-1", "Duplicate", null, null, 1m, 1m, "USD", "EA", "EA", true), CancellationToken.None));

                var changed = await service.UpdateAsync(created.Id, new(ProductType.Product, created.Sku, "Widget Pro",
                    created.Description, created.Barcode, 15m, created.Cost, created.CurrencyCode, created.SalesUnitCode,
                    created.PurchaseUnitCode, created.TracksInventory, created.ConcurrencyStamp), CancellationToken.None);
                var archived = await service.SetArchivedAsync(changed.Id, changed.ConcurrencyStamp, true, CancellationToken.None);
                var reactivated = await service.SetArchivedAsync(archived.Id, archived.ConcurrencyStamp, false, CancellationToken.None);
                Assert.False(reactivated.IsArchived);
                Assert.Equal(["products.created", "products.updated", "products.archived", "products.reactivated"],
                    audit.Entries.Select(entry => entry.Action));
            }

            await using (var hidden = new ProductsDbContext(options, new TenantContext(tenant), new CompanyContext(companyB)))
                Assert.Empty(await hidden.Products.ToListAsync());

            await using var firstContext = new ProductsDbContext(options, new TenantContext(tenant), new CompanyContext(companyA));
            await using var secondContext = new ProductsDbContext(options, new TenantContext(tenant), new CompanyContext(companyA));
            var first = await firstContext.Products.SingleAsync(product => product.Id == created.Id);
            var second = await secondContext.Products.SingleAsync(product => product.Id == created.Id);
            second.Name = "Second writer";
            second.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            await secondContext.SaveChangesAsync();
            first.Name = "Stale writer";
            first.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => firstContext.SaveChangesAsync());
        }
    }

    private static ProductService CreateService(ProductsDbContext context, Guid tenant, Guid company, RecordingAudit audit) =>
        new(context, new TenantContext(tenant), new CompanyContext(company), new CurrencyService(), new FixedClock(), audit,
            new HttpContextAccessor());

    private sealed class TenantContext(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class CompanyContext(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero); }
    private sealed class CurrencyService : ICurrencyConfigurationService
    {
        public Task<CurrencyConfiguration> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new CurrencyConfiguration(
            "USD", [new("USD", "US Dollar", "$", 2, true, "stamp")], []));
        public Task<CurrencyDefinition> ConfigureAsync(ConfigureCurrencyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CurrencyConfiguration> SetBaseCurrencyAsync(SetBaseCurrencyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExchangeRateDefinition> AddExchangeRateAsync(AddExchangeRateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class RecordingAudit : IAuditLogWriter
    {
        public List<AuditWriteEntry> Entries { get; } = [];
        public void Append(AuditWriteEntry entry) => Entries.Add(entry);
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
