using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Currencies;
using Erp.Abstractions.Events;
using Erp.Abstractions.Persistence;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Currencies;
using Erp.Infrastructure.Events;
using Erp.Infrastructure.Persistence;
using Erp.ModuleSystem;
using Erp.Modules.Contacts;
using Erp.Modules.Products;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class AtomicProductsPostgresTests(AtomicProductsPostgresFixture fixture)
    : IClassFixture<AtomicProductsPostgresFixture>
{
    [Fact]
    public async Task ProductLifecycleCommitsBusinessAuditAndExplicitlyStagedTestEventsTogether()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        Assert.Same(scope.Session.Connection, scope.Platform.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Products.Database.GetDbConnection());
        var options = await scope.Service.GetOptionsAsync(CancellationToken.None);
        Assert.Equal("USD", options.BaseCurrencyCode);
        Assert.Equal("USD", Assert.Single(options.Currencies).Code);
        Assert.Contains(options.Units, unit => unit.Code == "EA");

        var events = new[] { scope.NewEvent(), scope.NewEvent(), scope.NewEvent(), scope.NewEvent() };
        scope.Audit.PendingEvent = events[0];
        var created = await scope.Service.CreateAsync(Request(" sku-1 ", "Original widget"), CancellationToken.None);
        scope.Audit.PendingEvent = events[1];
        var updated = await scope.Service.UpdateAsync(created.Id,
            Request(created.Sku, "Updated widget") with { SalePrice = 25m, ConcurrencyStamp = created.ConcurrencyStamp }, CancellationToken.None);
        scope.Audit.PendingEvent = events[2];
        var archived = await scope.Service.SetArchivedAsync(updated.Id, updated.ConcurrencyStamp, true, CancellationToken.None);
        scope.Audit.PendingEvent = events[3];
        var reactivated = await scope.Service.SetArchivedAsync(archived.Id, archived.ConcurrencyStamp, false, CancellationToken.None);

        Assert.Equal("SKU-1", created.Sku);
        Assert.True(archived.IsArchived);
        Assert.False(reactivated.IsArchived);
        Assert.Equal(4, new[] { created.ConcurrencyStamp, updated.ConcurrencyStamp, archived.ConcurrencyStamp, reactivated.ConcurrencyStamp }.Distinct().Count());
        await using var products = scope.ReadProducts();
        Assert.Equal(reactivated, ToDto(await products.Products.SingleAsync()));
        await using var platform = scope.ReadPlatform();
        var audits = await platform.AuditLogs.ToDictionaryAsync(entry => entry.Action);
        Assert.Equal(4, audits.Count);
        Assert.All(audits.Values, entry =>
        {
            Assert.Equal(scope.TenantId, entry.TenantId);
            Assert.Equal(scope.CompanyId, entry.CompanyId);
            Assert.Equal(scope.UserId, entry.UserId);
            Assert.Equal("Product", entry.EntityType);
            Assert.Equal(created.Id.ToString(), entry.EntityId);
            Assert.Equal("products-atomicity-test", entry.CorrelationId);
        });
        Assert.Null(audits["products.created"].BeforeJson);
        Assert.Equal(created, JsonSerializer.Deserialize<ProductDto>(audits["products.created"].AfterJson!));
        Assert.Equal(created, JsonSerializer.Deserialize<ProductDto>(audits["products.updated"].BeforeJson!));
        Assert.Equal(updated, JsonSerializer.Deserialize<ProductDto>(audits["products.updated"].AfterJson!));
        Assert.Equal(updated, JsonSerializer.Deserialize<ProductDto>(audits["products.archived"].BeforeJson!));
        Assert.Equal(archived, JsonSerializer.Deserialize<ProductDto>(audits["products.archived"].AfterJson!));
        Assert.Equal(archived, JsonSerializer.Deserialize<ProductDto>(audits["products.reactivated"].BeforeJson!));
        Assert.Equal(reactivated, JsonSerializer.Deserialize<ProductDto>(audits["products.reactivated"].AfterJson!));
        var messages = await platform.OutboxMessages.ToListAsync();
        Assert.Equal(events.Select(message => message.EventId).Order(), messages.Select(message => message.Id).Order());
        Assert.All(messages, message =>
        {
            Assert.Equal(scope.TenantId, message.TenantId);
            Assert.Equal(scope.CompanyId, message.CompanyId);
            Assert.Equal("tests.products.atomicity.v1", message.EventType);
        });
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("archive")]
    [InlineData("reactivate")]
    public async Task FailureAfterRealPlatformFlushRestoresTheWholeOperationAndAllowsSameScopeRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        ProductDto? original = null;
        var priorAudits = 0;
        if (operation != "create")
        {
            original = await scope.Service.CreateAsync(Request("SKU-1", "Original widget"), CancellationToken.None);
            priorAudits++;
        }
        if (operation == "reactivate")
        {
            original = await scope.Service.SetArchivedAsync(original!.Id, original.ConcurrencyStamp, true, CancellationToken.None);
            priorAudits++;
        }
        Task<ProductDto> Execute() => operation switch
        {
            "create" => scope.Service.CreateAsync(Request("SKU-1", "New widget"), CancellationToken.None),
            "update" => scope.Service.UpdateAsync(original!.Id,
                Request("SKU-CHANGED", "Changed widget") with
                {
                    Description = "Changed description",
                    Barcode = "changed-barcode",
                    SalePrice = 99.5m,
                    Cost = 9m,
                    SalesUnitCode = "KGM",
                    PurchaseUnitCode = "KGM",
                    ConcurrencyStamp = original.ConcurrencyStamp
                }, CancellationToken.None),
            _ => scope.Service.SetArchivedAsync(original!.Id, original.ConcurrencyStamp, operation == "archive", CancellationToken.None)
        };
        var failedEvent = scope.NewEvent();
        scope.Audit.PendingEvent = failedEvent;
        scope.Audit.FailAfterFlush = true;

        await Assert.ThrowsAsync<InjectedAuditException>(Execute);

        await AssertCounts(scope, original is null ? 0 : 1, priorAudits, 0);
        AssertClean(scope);
        await using (var reader = scope.ReadProducts())
        {
            var persisted = await reader.Products.SingleOrDefaultAsync();
            if (original is null) Assert.Null(persisted);
            else Assert.Equal(original, ToDto(Assert.IsType<Product>(persisted)));
        }
        var successfulEvent = scope.NewEvent();
        scope.Audit.PendingEvent = successfulEvent;
        var retried = await Execute();

        await AssertCounts(scope, 1, priorAudits + 1, 1);
        await using var platform = scope.ReadPlatform();
        Assert.Equal(successfulEvent.EventId, await platform.OutboxMessages.Select(message => message.Id).SingleAsync());
        Assert.False(await platform.OutboxMessages.AnyAsync(message => message.Id == failedEvent.EventId));
        await using var products = scope.ReadProducts();
        Assert.Equal(retried, ToDto(await products.Products.SingleAsync()));
    }

    [Fact]
    public async Task CancellationAfterRealPlatformFlushRollsBackAndAllowsUncancelledRetry()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        using var cancellation = new CancellationTokenSource();
        scope.Audit.PendingEvent = scope.NewEvent();
        scope.Audit.AfterFlush = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.Service.CreateAsync(Request("CANCELLED", "Cancelled widget"), cancellation.Token));

        await AssertCounts(scope, 0, 0, 0);
        AssertClean(scope);
        await scope.Service.CreateAsync(Request("CANCELLED", "Retried widget"), CancellationToken.None);
        await AssertCounts(scope, 1, 1, 0);
    }

    [Fact]
    public async Task InvalidCurrencyAndUnitReferencesLeaveNoWritesAndAllowSameScopeRetry()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        scope.Platform.CompanyCurrencies.Add(new CompanyCurrency
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            Code = "EUR",
            Name = "Euro",
            Symbol = "EUR",
            MinorUnits = 2,
            IsActive = false,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        });
        scope.Products.UnitsOfMeasure.Add(new UnitOfMeasure
        {
            Code = "INACTIVE-ATOMIC",
            Name = "Inactive atomicity unit",
            Category = "Count",
            DecimalPrecision = 0,
            IsActive = false
        });
        await scope.Platform.SaveChangesAsync();
        await scope.Products.SaveChangesAsync();
        scope.Platform.ChangeTracker.Clear();
        scope.Products.ChangeTracker.Clear();
        (ProductRequest Request, string Field)[] invalidRequests =
        [
            (Request("REFERENCE", "Unknown currency") with { CurrencyCode = "ZZZ" }, "currencyCode"),
            (Request("REFERENCE", "Inactive currency") with { CurrencyCode = "EUR" }, "currencyCode"),
            (Request("REFERENCE", "Unknown unit") with { SalesUnitCode = "UNKNOWN" }, "units"),
            (Request("REFERENCE", "Inactive unit") with { PurchaseUnitCode = "INACTIVE-ATOMIC" }, "units")
        ];

        foreach (var (request, field) in invalidRequests)
        {
            var failure = await Assert.ThrowsAsync<ProductValidationException>(() => scope.Service.CreateAsync(request, CancellationToken.None));
            Assert.Contains(field, failure.Errors.Keys);
            AssertClean(scope);
            await AssertCounts(scope, 0, 0, 0);
        }
        Assert.Equal(0, scope.Audit.FlushCalls);
        var created = await scope.Service.CreateAsync(Request("REFERENCE", "Valid reference retry"), CancellationToken.None);
        await AssertCounts(scope, 1, 1, 0);

        await Assert.ThrowsAsync<ProductConflictException>(() =>
            scope.Service.CreateAsync(Request(created.Sku, "Duplicate preflight"), CancellationToken.None));
        AssertClean(scope);
        await AssertCounts(scope, 1, 1, 0);
        await using var reader = scope.ReadProducts();
        Assert.Equal(created, ToDto(await reader.Products.SingleAsync()));
    }

    [Fact]
    public async Task StaleUpdateAndArchiveRequestsDoNotWriteAuditsOrPreventValidRetries()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var created = await scope.Service.CreateAsync(Request("STALE", "Original widget"), CancellationToken.None);
        var updated = await scope.Service.UpdateAsync(created.Id,
            Request(created.Sku, "Current widget") with { ConcurrencyStamp = created.ConcurrencyStamp }, CancellationToken.None);

        await Assert.ThrowsAsync<ProductConcurrencyException>(() => scope.Service.UpdateAsync(created.Id,
            Request(created.Sku, "Stale widget") with { ConcurrencyStamp = created.ConcurrencyStamp }, CancellationToken.None));
        AssertClean(scope);
        await Assert.ThrowsAsync<ProductConcurrencyException>(() =>
            scope.Service.SetArchivedAsync(created.Id, created.ConcurrencyStamp, true, CancellationToken.None));
        AssertClean(scope);
        await AssertCounts(scope, 1, 2, 0);
        await using (var reader = scope.ReadProducts()) Assert.Equal(updated, ToDto(await reader.Products.SingleAsync()));

        var archived = await scope.Service.SetArchivedAsync(updated.Id, updated.ConcurrencyStamp, true, CancellationToken.None);
        Assert.True(archived.IsArchived);
        await AssertCounts(scope, 1, 3, 0);
    }

    [Fact]
    public async Task ConcurrentDatabaseWriterRollsBackStagedOutboxAndFailedUpdateThenAllowsRetry()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await scope.Service.CreateAsync(Request("CONCURRENT", "Original widget"), CancellationToken.None);
        var winningStamp = Guid.NewGuid().ToString("N");
        scope.SaveHook.BeforeSave = async (_, cancellationToken) =>
        {
            await using var competing = scope.ReadProducts();
            var winner = await competing.Products.SingleAsync(cancellationToken);
            winner.Name = "Concurrent winner";
            winner.ConcurrencyStamp = winningStamp;
            await competing.SaveChangesAsync(cancellationToken);
        };
        await using (await scope.Transactions.BeginAsync(scope.Products, CancellationToken.None))
        {
            scope.Outbox.Add(scope.NewEvent());
            await Assert.ThrowsAsync<ProductConcurrencyException>(() => scope.Service.UpdateAsync(original.Id,
                Request(original.Sku, "Losing writer") with { ConcurrencyStamp = original.ConcurrencyStamp }, CancellationToken.None));
        }

        Assert.True(scope.SaveHook.Executed);
        Assert.IsType<DbUpdateConcurrencyException>(scope.SaveHook.LastFailure);
        AssertClean(scope);
        await AssertCounts(scope, 1, 1, 0);
        await using (var reader = scope.ReadProducts())
        {
            var winner = await reader.Products.SingleAsync();
            Assert.Equal("Concurrent winner", winner.Name);
            Assert.Equal(winningStamp, winner.ConcurrencyStamp);
        }
        var retried = await scope.Service.UpdateAsync(original.Id,
            Request(original.Sku, "Retried writer") with { ConcurrencyStamp = winningStamp }, CancellationToken.None);
        Assert.Equal("Retried writer", retried.Name);
        await AssertCounts(scope, 1, 2, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealDatabaseUniqueFailureClearsBusinessAndStagedPlatformRowsBeforeRetry(bool barcodeConflict)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        scope.SaveHook.BeforeSave = (database, cancellationToken) => InsertCompetingProduct(scope, database, barcodeConflict, cancellationToken);
        await using (await scope.Transactions.BeginAsync(scope.Products, CancellationToken.None))
        {
            scope.Outbox.Add(scope.NewEvent());
            await Assert.ThrowsAsync<ProductConflictException>(() => scope.Service.CreateAsync(
                Request("RACING-SKU", "Losing widget") with { Barcode = "racing-barcode" }, CancellationToken.None));
        }

        Assert.True(scope.SaveHook.Executed);
        var failure = Assert.IsType<DbUpdateException>(scope.SaveHook.LastFailure);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        Assert.Equal(0, scope.Audit.FlushCalls);
        AssertClean(scope);
        await AssertCounts(scope, 1, 0, 0);
        await using (var reader = scope.ReadProducts()) Assert.Equal("Competing widget", await reader.Products.Select(product => product.Name).SingleAsync());

        await scope.Service.CreateAsync(Request("RETRIED-SKU", "Valid retry"), CancellationToken.None);
        await AssertCounts(scope, 2, 1, 0);
    }

    [Fact]
    public async Task CsvImportCommitsEachSuccessfulRowAcrossRealUniqueAndInvalidRowFailures()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        scope.SaveHook.Matches = database => database.ChangeTracker.Entries<Product>()
            .Any(entry => entry.State == EntityState.Added && entry.Entity.Sku == "CSV-RACE");
        scope.SaveHook.BeforeSave = (database, cancellationToken) => InsertCompetingProduct(scope, database, false, cancellationToken);
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes(
            "type,sku,name,salePrice,cost,currencyCode,salesUnitCode,purchaseUnitCode,tracksInventory\r\n" +
            "Product,CSV-FIRST,First widget,10,5,USD,EA,EA,true\r\n" +
            "Product,CSV-RACE,Rejected duplicate,10,5,USD,EA,EA,true\r\n" +
            "Product,CSV-INVALID,Invalid price,-1,5,USD,EA,EA,true\r\n" +
            "Product,CSV-LAST,Last widget,12,6,USD,EA,EA,true\r\n"));

        var result = await scope.Service.ImportCsvAsync(csv, CancellationToken.None);

        Assert.Equal(2, result.Imported);
        Assert.Collection(result.Errors, error => Assert.StartsWith("Line 3:", error), error => Assert.StartsWith("Line 4:", error));
        var failure = Assert.IsType<DbUpdateException>(scope.SaveHook.LastFailure);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        await AssertCounts(scope, 3, 2, 0);
        await using var reader = scope.ReadProducts();
        var products = await reader.Products.ToDictionaryAsync(product => product.Sku);
        Assert.Equal("First widget", products["CSV-FIRST"].Name);
        Assert.Equal("Last widget", products["CSV-LAST"].Name);
        Assert.Equal("Competing widget", products["CSV-RACE"].Name);
        Assert.False(products.ContainsKey("CSV-INVALID"));
        await using var platform = scope.ReadPlatform();
        Assert.Equal(new[] { products["CSV-FIRST"].Id.ToString(), products["CSV-LAST"].Id.ToString() }.Order(),
            await platform.AuditLogs.Select(entry => entry.EntityId).OrderBy(id => id).ToArrayAsync());
    }

    [Fact]
    public async Task AtomicMutationsPreserveCompanyAndTenantIsolation()
    {
        await using var owner = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var otherCompany = await TestScope.CreateAsync(fixture.ConnectionString, owner.TenantId);
        await using var otherTenant = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await owner.Service.CreateAsync(Request("SHARED-SKU", "Owner widget"), CancellationToken.None);

        foreach (var hidden in new[] { otherCompany, otherTenant })
        {
            Assert.Null(await hidden.Service.GetAsync(original.Id, CancellationToken.None));
            Assert.Empty((await hidden.Service.SearchAsync(new(), CancellationToken.None)).Items);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Service.UpdateAsync(original.Id,
                Request(original.Sku, "Forbidden widget") with { ConcurrencyStamp = original.ConcurrencyStamp }, CancellationToken.None));
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                hidden.Service.SetArchivedAsync(original.Id, original.ConcurrencyStamp, true, CancellationToken.None));
            AssertClean(hidden);
            await hidden.Service.CreateAsync(Request(original.Sku, "Independent widget"), CancellationToken.None);
            await AssertCounts(hidden, 1, 1, 0);
        }
        await using (var tenantReader = owner.ReadProducts(otherTenant.TenantId, owner.CompanyId)) Assert.Empty(await tenantReader.Products.ToListAsync());
        await using (await otherCompany.Transactions.BeginAsync(otherCompany.Products, CancellationToken.None))
        {
            otherCompany.Products.Products.Add(NewProduct(owner.TenantId, owner.CompanyId, "FORBIDDEN", "Forbidden direct write"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => otherCompany.Products.SaveChangesAsync());
        }
        AssertClean(otherCompany);
        await AssertCounts(owner, 1, 1, 0);
        await using var reader = owner.ReadProducts();
        Assert.Equal(original, ToDto(await reader.Products.SingleAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductsAndContactsComposeUnderOneOuterTransaction(bool commit)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        Assert.Same(scope.Session.Connection, scope.Contacts.Database.GetDbConnection());
        await using (var outer = await scope.Transactions.BeginAsync(scope.Products, CancellationToken.None))
        {
            scope.Audit.PendingEvent = scope.NewEvent();
            await scope.Service.CreateAsync(Request("COMPOSED", "Composed widget"), CancellationToken.None);
            scope.Audit.PendingEvent = scope.NewEvent();
            await scope.ContactService.CreateAsync(new(ContactKind.Organization, "Composed customer", null, null,
                "Composed customer", null, null, null, null, IsCustomer: true), CancellationToken.None);
            await AssertCounts(scope, 0, 0, 0);
            await using (var reader = scope.ReadContacts()) Assert.Empty(await reader.Contacts.ToListAsync());
            if (commit) await outer.CommitAsync(CancellationToken.None);
        }

        await AssertCounts(scope, commit ? 1 : 0, commit ? 2 : 0, commit ? 2 : 0);
        await using (var reader = scope.ReadContacts()) Assert.Equal(commit ? 1 : 0, await reader.Contacts.CountAsync());
        if (!commit)
        {
            AssertClean(scope);
            Assert.Empty(scope.Contacts.ChangeTracker.Entries());
            Assert.Null(scope.Contacts.Database.CurrentTransaction);
            await scope.Service.CreateAsync(Request("COMPOSED", "Independent retry"), CancellationToken.None);
            await AssertCounts(scope, 1, 1, 0);
            await using var reader = scope.ReadContacts();
            Assert.Empty(await reader.Contacts.ToListAsync());
        }
    }

    private static ProductRequest Request(string sku, string name) =>
        new(ProductType.Product, sku, name, "Original description", null, 12.3456m, 4.25m, "usd", "ea", "ea", true);

    private static ProductDto ToDto(Product product) => new(product.Id, product.Type, product.Sku, product.Name,
        product.Description, product.Barcode, product.SalePrice, product.Cost, product.CurrencyCode,
        product.SalesUnitCode, product.PurchaseUnitCode, product.TracksInventory, product.IsArchived,
        product.CreatedAtUtc, product.UpdatedAtUtc, product.CreatedBy, product.UpdatedBy, product.ConcurrencyStamp);

    private static Product NewProduct(Guid tenantId, Guid companyId, string sku, string name, string? barcode = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        CompanyId = companyId,
        Type = ProductType.Product,
        Sku = sku,
        Name = name,
        Barcode = barcode,
        SalePrice = 1m,
        Cost = 1m,
        CurrencyCode = "USD",
        SalesUnitCode = "EA",
        PurchaseUnitCode = "EA",
        TracksInventory = true,
        CreatedAtUtc = FixedClock.Now,
        UpdatedAtUtc = FixedClock.Now,
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
    };

    private static async Task InsertCompetingProduct(TestScope scope, ProductsDbContext database, bool barcodeConflict, CancellationToken cancellationToken)
    {
        var pending = database.ChangeTracker.Entries<Product>().Single(entry => entry.State == EntityState.Added).Entity;
        await using var competing = scope.ReadProducts();
        competing.Products.Add(NewProduct(scope.TenantId, scope.CompanyId,
            barcodeConflict ? "COMPETING-SKU" : pending.Sku, "Competing widget", barcodeConflict ? pending.Barcode : null));
        await competing.SaveChangesAsync(cancellationToken);
    }

    private static void AssertClean(TestScope scope)
    {
        Assert.Empty(scope.Products.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Products.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    private static async Task AssertCounts(TestScope scope, int products, int audits, int events)
    {
        await using var productReader = scope.ReadProducts();
        await using var platformReader = scope.ReadPlatform();
        Assert.Equal(products, await productReader.Products.CountAsync());
        Assert.Equal(audits, await platformReader.AuditLogs.CountAsync(entry => entry.CompanyId == scope.CompanyId));
        Assert.Equal(events, await platformReader.OutboxMessages.CountAsync(message => message.CompanyId == scope.CompanyId));
    }

    private sealed class TestScope(ServiceProvider provider, AsyncServiceScope services, string connectionString,
        Guid tenantId, Guid companyId, Guid userId) : IAsyncDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid CompanyId { get; } = companyId;
        public Guid UserId { get; } = userId;
        public IRelationalDatabaseSession Session => services.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        public PlatformDbContext Platform => services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        public ProductsDbContext Products => services.ServiceProvider.GetRequiredService<ProductsDbContext>();
        public ContactsDbContext Contacts => services.ServiceProvider.GetRequiredService<ContactsDbContext>();
        public IProductService Service => services.ServiceProvider.GetRequiredService<IProductService>();
        public IContactService ContactService => services.ServiceProvider.GetRequiredService<IContactService>();
        public IBusinessTransactionCoordinator Transactions => services.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        public InjectingAudit Audit => services.ServiceProvider.GetRequiredService<InjectingAudit>();
        public IOutboxWriter Outbox => services.ServiceProvider.GetRequiredService<IOutboxWriter>();
        public OneShotSaveHook SaveHook => services.ServiceProvider.GetRequiredService<OneShotSaveHook>();

        public TestEvent NewEvent() => new(Guid.NewGuid(), services.ServiceProvider.GetRequiredService<IClock>().UtcNow);
        public PlatformDbContext ReadPlatform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId));
        public ProductsDbContext ReadProducts(Guid? tenant = null, Guid? company = null) => new(
            new DbContextOptionsBuilder<ProductsDbContext>().UseNpgsql(connectionString).Options,
            new TestTenant(tenant ?? TenantId), new TestCompany(company ?? CompanyId));
        public ContactsDbContext ReadContacts() => new(new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(connectionString).Options,
            new TestTenant(TenantId), new TestCompany(CompanyId));

        public static async Task<TestScope> CreateAsync(string connectionString, Guid? existingTenantId = null)
        {
            var tenantId = existingTenantId ?? Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var registry = new IntegrationEventRegistry();
            // This event is explicitly staged by the test; Products does not publish a production event contract.
            registry.Add<TestEvent>("tests.products.atomicity.v1");
            var registrations = new ServiceCollection();
            registrations.AddSingleton<ITenantContext>(new TestTenant(tenantId));
            registrations.AddSingleton<ICompanyContext>(new TestCompany(companyId));
            registrations.AddSingleton<IClock>(new FixedClock());
            registrations.AddSingleton<IIntegrationEventRegistry>(registry);
            registrations.AddSingleton<IHttpContextAccessor>(new TestHttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                    TraceIdentifier = "products-atomicity-test"
                }
            });
            registrations.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connectionString));
            registrations.AddDbContext<PlatformDbContext>((provider, options) => options.UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection));
            registrations.AddScoped<OneShotSaveHook>();
            registrations.AddDbContext<ProductsDbContext>((provider, options) => options
                .UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection)
                .AddInterceptors(provider.GetRequiredService<OneShotSaveHook>()));
            registrations.AddDbContext<ContactsDbContext>((provider, options) => options.UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection));
            registrations.AddScoped<IBusinessTransactionCoordinator, BusinessTransactionCoordinator>();
            registrations.AddScoped<AuditLogService>();
            registrations.AddScoped<IOutboxWriter, PostgresOutboxWriter>();
            registrations.AddScoped<InjectingAudit>();
            registrations.AddScoped<IAuditLogWriter>(provider => provider.GetRequiredService<InjectingAudit>());
            registrations.AddScoped<ICurrencyConfigurationService, CurrencyConfigurationService>();
            registrations.AddScoped<IProductService, ProductService>();
            registrations.AddScoped<IContactService, ContactService>();
            var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var services = provider.CreateAsyncScope();
            var scope = new TestScope(provider, services, connectionString, tenantId, companyId, userId);
            try
            {
                if (existingTenantId is null)
                    scope.Platform.Tenants.Add(new Tenant { Id = tenantId, Name = "Products atomicity", Slug = $"atomic-{tenantId:N}", TimeZoneId = "UTC", IsActive = true, CreatedAtUtc = FixedClock.Now });
                scope.Platform.Companies.Add(new Company { Id = companyId, TenantId = tenantId, Code = companyId.ToString("N")[..20], Name = "Products atomicity", BaseCurrencyCode = "USD", TimeZoneId = "UTC", IsActive = true });
                scope.Platform.CompanyCurrencies.Add(new CompanyCurrency
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CompanyId = companyId,
                    Code = "USD",
                    Name = "US Dollar",
                    Symbol = "$",
                    MinorUnits = 2,
                    IsActive = true,
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                });
                await scope.Platform.SaveChangesAsync();
                scope.Platform.ChangeTracker.Clear();
                return scope;
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class InjectingAudit(AuditLogService realAudit, IOutboxWriter outbox) : IAuditLogWriter
    {
        public int FlushCalls { get; private set; }
        public bool FailAfterFlush { get; set; }
        public Action? AfterFlush { get; set; }
        public TestEvent? PendingEvent { get; set; }

        public void Append(AuditWriteEntry entry) => realAudit.Append(entry);

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            if (PendingEvent is { } message) { PendingEvent = null; outbox.Add(message); }
            await realAudit.FlushAsync(cancellationToken);
            var afterFlush = AfterFlush;
            AfterFlush = null;
            afterFlush?.Invoke();
            if (FailAfterFlush) { FailAfterFlush = false; throw new InjectedAuditException(); }
        }
    }

    private sealed class OneShotSaveHook : SaveChangesInterceptor
    {
        public Func<ProductsDbContext, CancellationToken, Task>? BeforeSave { get; set; }
        public Func<ProductsDbContext, bool>? Matches { get; set; }
        public Exception? LastFailure { get; private set; }
        public bool Executed { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var database = (ProductsDbContext)eventData.Context!;
            if (BeforeSave is { } callback && (Matches is null || Matches(database)))
            {
                BeforeSave = null;
                Executed = true;
                await callback(database, cancellationToken);
            }
            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            LastFailure = eventData.Exception;
            return Task.CompletedTask;
        }

        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            LastFailure = eventData.Exception;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedAuditException : Exception;
    private sealed record TestEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
    private sealed class TestHttpContextAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class TestTenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class TestCompany(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }
}

public sealed class AtomicProductsPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("products_atomicity_tests");
        ConnectionString = _postgres.GetConnectionString();
        await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options);
        await platform.Database.MigrateAsync();
        await using var products = new ProductsDbContext(new DbContextOptionsBuilder<ProductsDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "products")).Options);
        await products.Database.MigrateAsync();
        await using var contacts = new ContactsDbContext(new DbContextOptionsBuilder<ContactsDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "contacts")).Options);
        await contacts.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}
