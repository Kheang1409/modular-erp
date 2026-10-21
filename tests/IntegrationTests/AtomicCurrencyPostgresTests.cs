using System.Security.Claims;
using System.Data.Common;
using System.Diagnostics;
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
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class AtomicCurrencyPostgresTests(AtomicCurrencyPostgresFixture fixture)
    : IClassFixture<AtomicCurrencyPostgresFixture>
{
    [Fact]
    public async Task BaseCurrencyUpdateAndAuditAppendFailureRollBackTogetherAndAllowSameScopeRetry()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await SnapshotAsync(scope);
        scope.Audit.FailAfterAppend = true;

        await Assert.ThrowsAsync<InjectedFailureException>(() => scope.Service.SetBaseCurrencyAsync(
            new("KHR", "USD"), CancellationToken.None));

        Assert.Equal(original, await SnapshotAsync(scope));
        AssertClean(scope);
        var changed = await scope.Service.SetBaseCurrencyAsync(new("KHR", "USD"), CancellationToken.None);
        Assert.Equal("KHR", changed.BaseCurrencyCode);
        await using var reader = scope.ReadPlatform();
        Assert.Equal("currency.base.changed", (await reader.AuditLogs.SingleAsync()).Action);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("change", false)]
    [InlineData("base", false)]
    [InlineData("rate", false)]
    [InlineData("create", true)]
    [InlineData("change", true)]
    [InlineData("base", true)]
    [InlineData("rate", true)]
    public async Task FailureOrCancellationAfterRealSaveRestoresWholeOperationAndAllowsSameScopeRetry(
        string operation, bool cancellation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var khr = (await scope.Service.GetAsync(CancellationToken.None)).Currencies.Single(value => value.Code == "KHR");
        var original = await SnapshotAsync(scope);
        using var cancelled = new CancellationTokenSource();
        scope.Hook.AfterSave = token =>
        {
            if (!cancellation) return Task.FromException(new InjectedFailureException());
            cancelled.Cancel();
            return Task.FromCanceled(token);
        };

        if (cancellation)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(scope, operation, khr.ConcurrencyStamp, cancelled.Token));
        else
            await Assert.ThrowsAsync<InjectedFailureException>(() => ExecuteAsync(scope, operation, khr.ConcurrencyStamp, cancelled.Token));

        Assert.Equal(original, await SnapshotAsync(scope));
        AssertClean(scope);
        await ExecuteAsync(scope, operation, khr.ConcurrencyStamp, CancellationToken.None);
        var retried = await SnapshotAsync(scope);
        Assert.NotEqual(original, retried);
        Assert.Equal(1, retried.Audits);
        Assert.Equal(0, retried.Events);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    [Fact]
    public async Task RealAuditDatabaseConstraintFailureRollsBackBaseUpdateAndAllowsSameScopeRetry()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await SnapshotAsync(scope);
        scope.Hook.BeforeSave = database => database.AuditLogs.Local.Single().CorrelationId = new string('x', 101);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => scope.Service.SetBaseCurrencyAsync(
            new("KHR", "USD"), CancellationToken.None));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        Assert.Equal(original, await SnapshotAsync(scope));
        AssertClean(scope);
        await scope.Service.SetBaseCurrencyAsync(new("KHR", "USD"), CancellationToken.None);
        Assert.Equal(1, (await SnapshotAsync(scope)).Audits);
    }

    [Fact]
    public async Task CurrencyLifecycleCommitsCorrectCompanyAuditSnapshots()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = (await scope.Service.GetAsync(CancellationToken.None)).Currencies.Single(value => value.Code == "KHR");
        foreach (var operation in new[] { "create", "change", "base", "rate" })
            await ExecuteAsync(scope, operation, original.ConcurrencyStamp, CancellationToken.None);

        await using var reader = scope.ReadPlatform();
        var audits = await reader.AuditLogs.Where(value => value.CompanyId == scope.CompanyId).ToDictionaryAsync(value => value.Action);
        Assert.Equal(4, audits.Count);
        Assert.All(audits.Values, entry =>
        {
            Assert.Equal(scope.TenantId, entry.TenantId);
            Assert.Equal(scope.CompanyId, entry.CompanyId);
            Assert.Equal(scope.UserId, entry.UserId);
            Assert.Equal("currencies-atomicity-test", entry.CorrelationId);
            Assert.Equal(FixedClock.Now, entry.TimestampUtc);
        });
        var created = audits["currency.configured"];
        Assert.Equal("Currency", created.EntityType);
        Assert.Equal("EUR", created.EntityId);
        Assert.Null(created.BeforeJson);
        Assert.Equal("EUR", JsonString(created.AfterJson!, "Code"));
        var changed = audits["currency.changed"];
        Assert.Equal("KHR", changed.EntityId);
        Assert.Equal("Cambodian Riel", JsonString(changed.BeforeJson!, "Name"));
        Assert.Equal("Updated riel", JsonString(changed.AfterJson!, "Name"));
        var baseChanged = audits["currency.base.changed"];
        Assert.Equal("Company", baseChanged.EntityType);
        Assert.Equal(scope.CompanyId.ToString(), baseChanged.EntityId);
        Assert.Equal("USD", JsonString(baseChanged.BeforeJson!, "BaseCurrencyCode"));
        Assert.Equal("KHR", JsonString(baseChanged.AfterJson!, "BaseCurrencyCode"));
        var rate = audits["exchange_rate.created"];
        Assert.Equal("ExchangeRate", rate.EntityType);
        Assert.Null(rate.BeforeJson);
        Assert.Equal("USD", JsonString(rate.AfterJson!, "From"));
        Assert.Equal("KHR", JsonString(rate.AfterJson!, "To"));
        Assert.Equal((await reader.CompanyExchangeRates.SingleAsync(value => value.CompanyId == scope.CompanyId)).Id.ToString(), rate.EntityId);
        Assert.Equal("KHR", (await scope.Service.GetAsync(CancellationToken.None)).BaseCurrencyCode);
        Assert.Empty(await reader.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OuterTransactionOwnsCurrencyChangesAuditAndExplicitTestOutbox(bool commit)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await SnapshotAsync(scope);
        var khr = (await scope.Service.GetAsync(CancellationToken.None)).Currencies.Single(value => value.Code == "KHR");
        var message = scope.NewEvent();
        await using (var outer = await scope.Transactions.BeginAsync(scope.Platform, CancellationToken.None))
        {
            scope.Outbox.Add(message);
            foreach (var operation in new[] { "create", "change", "base", "rate" })
                await ExecuteAsync(scope, operation, khr.ConcurrencyStamp, CancellationToken.None);
            if (commit) await outer.CommitAsync(CancellationToken.None);
        }

        if (commit)
        {
            var saved = await SnapshotAsync(scope);
            Assert.Equal("KHR", saved.BaseCurrency);
            Assert.Equal(4, saved.Audits);
            Assert.Equal(1, saved.Events);
            await using var reader = scope.ReadPlatform();
            var persisted = await reader.OutboxMessages.SingleAsync();
            Assert.Equal(message.EventId, persisted.Id);
            Assert.Equal(scope.TenantId, persisted.TenantId);
            Assert.Equal(scope.CompanyId, persisted.CompanyId);
            Assert.Equal("tests.currencies.atomicity.v1", persisted.EventType);
        }
        else
        {
            Assert.Equal(original, await SnapshotAsync(scope));
            AssertClean(scope);
            await scope.Service.SetBaseCurrencyAsync(new("KHR", "USD"), CancellationToken.None);
            Assert.Equal(1, (await SnapshotAsync(scope)).Audits);
        }
    }

    [Fact]
    public async Task PendingCallerOwnedWorkIsRejectedWithoutSavingOrDiscardingIt()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await SnapshotAsync(scope);
        var message = scope.NewEvent();
        scope.Outbox.Add(message);
        var staged = Assert.Single(scope.Platform.ChangeTracker.Entries());

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Service.SetBaseCurrencyAsync(
            new("KHR", "USD"), CancellationToken.None));

        Assert.Same(staged.Entity, Assert.Single(scope.Platform.ChangeTracker.Entries()).Entity);
        Assert.Equal(EntityState.Added, staged.State);
        Assert.Equal(original, await SnapshotAsync(scope));
        Assert.Null(scope.Platform.Database.CurrentTransaction);
        scope.Platform.ChangeTracker.Clear(); // The caller, not the rejected service, owns this staged test row.
        await scope.Service.SetBaseCurrencyAsync(new("KHR", "USD"), CancellationToken.None);
        Assert.Equal(0, (await SnapshotAsync(scope)).Events);
    }

    [Fact]
    public async Task UnmanagedCallerTransactionIsRejectedAndRemainsUsable()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await SnapshotAsync(scope);
        await using (var unmanaged = await scope.Platform.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Service.SetBaseCurrencyAsync(
                new("KHR", "USD"), CancellationToken.None));
            Assert.Same(unmanaged, scope.Platform.Database.CurrentTransaction);
            await scope.Platform.Database.ExecuteSqlRawAsync("SELECT 1");
            await unmanaged.RollbackAsync();
        }
        Assert.Equal(original, await SnapshotAsync(scope));
        AssertClean(scope);
        await scope.Service.SetBaseCurrencyAsync(new("KHR", "USD"), CancellationToken.None);
        Assert.Equal(1, (await SnapshotAsync(scope)).Audits);
    }

    [Theory]
    [InlineData("stale-base")]
    [InlineData("missing-base")]
    [InlineData("inactive-base")]
    [InlineData("foreign-company-base")]
    [InlineData("foreign-tenant-base")]
    [InlineData("deactivate-base")]
    [InlineData("stale-currency")]
    [InlineData("inactive-rate")]
    [InlineData("duplicate-rate")]
    public async Task InvalidStaleAndForeignTargetsLeaveNoChangesOrAudit(string scenario)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var khr = (await scope.Service.GetAsync(CancellationToken.None)).Currencies.Single(value => value.Code == "KHR");
        if (scenario is "inactive-base" or "inactive-rate")
            await scope.Service.ConfigureAsync(new("KHR", khr.Name, khr.Symbol, khr.MinorUnits, false, khr.ConcurrencyStamp), CancellationToken.None);
        if (scenario == "duplicate-rate") await ExecuteAsync(scope, "rate", khr.ConcurrencyStamp, CancellationToken.None);
        if (scenario is "foreign-company-base" or "foreign-tenant-base")
        {
            await using var other = await TestScope.CreateAsync(fixture.ConnectionString,
                scenario == "foreign-company-base" ? scope.TenantId : null);
            await other.Service.ConfigureAsync(new("EUR", "Euro", "EUR", 2, true), CancellationToken.None);
        }
        var original = await SnapshotAsync(scope);
        var usd = (await scope.Service.GetAsync(CancellationToken.None)).Currencies.Single(value => value.Code == "USD");
        Task Act() => scenario switch
        {
            "stale-base" => scope.Service.SetBaseCurrencyAsync(new("KHR", "EUR"), CancellationToken.None),
            "missing-base" or "foreign-company-base" or "foreign-tenant-base" => scope.Service.SetBaseCurrencyAsync(new("EUR", "USD"), CancellationToken.None),
            "inactive-base" => scope.Service.SetBaseCurrencyAsync(new("KHR", "USD"), CancellationToken.None),
            "deactivate-base" => scope.Service.ConfigureAsync(new("USD", usd.Name, usd.Symbol, usd.MinorUnits, false, usd.ConcurrencyStamp), CancellationToken.None),
            "stale-currency" => scope.Service.ConfigureAsync(new("KHR", "Stale update", "KHR", 0, true, "stale"), CancellationToken.None),
            "inactive-rate" or "duplicate-rate" => ExecuteAsync(scope, "rate", khr.ConcurrencyStamp, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        if (scenario is "missing-base" or "inactive-base" or "foreign-company-base" or "foreign-tenant-base" or "inactive-rate")
            await Assert.ThrowsAsync<ArgumentException>(Act);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(Act);

        Assert.Equal(original, await SnapshotAsync(scope));
        AssertClean(scope);
    }

    [Fact]
    public async Task MismatchedTenantAndCompanyContextCannotLockOrChangeAnotherTenantsCompany()
    {
        await using var tenantA = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var tenantB = await TestScope.CreateAsync(fixture.ConnectionString);
        var originalA = await SnapshotAsync(tenantA);
        var originalB = await SnapshotAsync(tenantB);
        await using var mismatched = await TestScope.CreateAsync(fixture.ConnectionString, tenantA.TenantId, tenantB.CompanyId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mismatched.Service.SetBaseCurrencyAsync(
            new("KHR", "USD"), CancellationToken.None));

        AssertClean(mismatched);
        Assert.Equal(originalA, await SnapshotAsync(tenantA));
        Assert.Equal(originalB, await SnapshotAsync(tenantB));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("deactivate")]
    [InlineData("setter")]
    public async Task CompetingCompanyWritersWaitForRealDatabaseLockAndPreserveActiveBase(string winner)
    {
        await using var first = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var second = await TestScope.CreateAsync(fixture.ConnectionString, first.TenantId, first.CompanyId);
        var khr = (await first.Service.GetAsync(CancellationToken.None)).Currencies.Single(value => value.Code == "KHR");
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Hook.AfterSave = async token =>
        {
            saved.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        };

        Task SetBase(TestScope scope) => scope.Service.SetBaseCurrencyAsync(new("KHR", "USD"), CancellationToken.None);
        Task Deactivate(TestScope scope) => scope.Service.ConfigureAsync(
            new("KHR", khr.Name, khr.Symbol, khr.MinorUnits, false, khr.ConcurrencyStamp), CancellationToken.None);
        var firstWrite = winner == "deactivate" ? Deactivate(first) : SetBase(first);
        Task? secondWrite = null;
        Exception? observationFailure = null;
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(15));
            secondWrite = winner == "base" ? Deactivate(second) : SetBase(second);
            var waitingPid = await second.LockProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForBlockingAsync(fixture.ConnectionString, waitingPid, first.BackendPid);
            Assert.False(secondWrite.IsCompleted);
        }
        catch (Exception exception)
        {
            observationFailure = exception;
        }
        finally
        {
            release.TrySetResult();
        }

        // Always join the writers before disposing their connections, including failed lock observations.
        var firstFailure = await Record.ExceptionAsync(() => firstWrite.WaitAsync(TimeSpan.FromSeconds(15)));
        var secondFailure = secondWrite is null ? null : await Record.ExceptionAsync(() => secondWrite.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Null(observationFailure);
        Assert.Null(firstFailure);
        if (winner == "deactivate") Assert.IsType<ArgumentException>(secondFailure);
        else Assert.IsType<InvalidOperationException>(secondFailure);
        AssertClean(second);

        await using var reader = first.ReadPlatform();
        var company = await reader.Companies.SingleAsync(value => value.Id == first.CompanyId);
        var target = await reader.CompanyCurrencies.SingleAsync(value => value.CompanyId == first.CompanyId && value.Code == "KHR");
        Assert.Equal(winner == "deactivate" ? "USD" : "KHR", company.BaseCurrencyCode);
        Assert.Equal(winner != "deactivate", target.IsActive);
        Assert.True(await reader.CompanyCurrencies.AnyAsync(value => value.CompanyId == first.CompanyId
            && value.Code == company.BaseCurrencyCode && value.IsActive));
        var audit = await reader.AuditLogs.SingleAsync(value => value.CompanyId == first.CompanyId);
        Assert.Equal(winner == "deactivate" ? "currency.changed" : "currency.base.changed", audit.Action);
    }

    private static async Task WaitForBlockingAsync(string connectionString, int blockedPid, int blockingPid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand("SELECT @blocking = ANY(pg_blocking_pids(@blocked))", observer);
        command.Parameters.AddWithValue("blocking", blockingPid);
        command.Parameters.AddWithValue("blocked", blockedPid);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (await command.ExecuteScalarAsync(timeout.Token) is true) return;
            await Task.Yield();
        }
        Assert.Fail("The competing currency write never waited for the first company's real PostgreSQL row lock.");
    }

    private static Task ExecuteAsync(TestScope scope, string operation, string stamp, CancellationToken token) => operation switch
    {
        "create" => scope.Service.ConfigureAsync(new("EUR", "Euro", "EUR", 2, true), token),
        "change" => scope.Service.ConfigureAsync(new("KHR", "Updated riel", "KHR", 0, true, stamp), token),
        "base" => scope.Service.SetBaseCurrencyAsync(new("KHR", "USD"), token),
        "rate" => scope.Service.AddExchangeRateAsync(new("USD", "KHR", 4100.25m, new(2026, 10, 21)), token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static string JsonString(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).GetString()!;
    }

    private static void AssertClean(TestScope scope)
    {
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    private static async Task<Snapshot> SnapshotAsync(TestScope scope)
    {
        await using var reader = scope.ReadPlatform();
        var company = await reader.Companies.AsNoTracking().SingleAsync(value => value.Id == scope.CompanyId);
        var currencies = await reader.CompanyCurrencies.AsNoTracking().Where(value => value.CompanyId == scope.CompanyId)
            .OrderBy(value => value.Code).Select(value => new
            {
                value.Id,
                value.Code,
                value.Name,
                value.Symbol,
                value.MinorUnits,
                value.IsActive,
                value.ConcurrencyStamp
            }).ToListAsync();
        var rates = await reader.CompanyExchangeRates.AsNoTracking().Where(value => value.CompanyId == scope.CompanyId)
            .OrderBy(value => value.Id).Select(value => new
            {
                value.Id,
                value.FromCurrencyCode,
                value.ToCurrencyCode,
                value.Rate,
                value.EffectiveDate
            }).ToListAsync();
        return new(company.BaseCurrencyCode, JsonSerializer.Serialize(currencies), JsonSerializer.Serialize(rates),
            await reader.AuditLogs.CountAsync(value => value.CompanyId == scope.CompanyId),
            await reader.OutboxMessages.CountAsync(value => value.CompanyId == scope.CompanyId));
    }

    private sealed record Snapshot(string BaseCurrency, string Currencies, string Rates, int Audits, int Events);

    private sealed class TestScope(ServiceProvider provider, AsyncServiceScope services, string connectionString,
        Guid tenantId, Guid companyId, Guid userId) : IAsyncDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid CompanyId { get; } = companyId;
        public Guid UserId { get; } = userId;
        public PlatformDbContext Platform => services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        public ICurrencyConfigurationService Service => services.ServiceProvider.GetRequiredService<ICurrencyConfigurationService>();
        public IBusinessTransactionCoordinator Transactions => services.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        public IOutboxWriter Outbox => services.ServiceProvider.GetRequiredService<IOutboxWriter>();
        public InjectingAudit Audit => services.ServiceProvider.GetRequiredService<InjectingAudit>();
        public OneShotSaveHook Hook => services.ServiceProvider.GetRequiredService<OneShotSaveHook>();
        public CompanyLockProbe LockProbe => services.ServiceProvider.GetRequiredService<CompanyLockProbe>();
        public int BackendPid => ((NpgsqlConnection)Platform.Database.GetDbConnection()).ProcessID;
        public TestEvent NewEvent() => new(Guid.NewGuid(), services.ServiceProvider.GetRequiredService<IClock>().UtcNow);
        public PlatformDbContext ReadPlatform() => new(
            new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId));

        public static async Task<TestScope> CreateAsync(string connectionString, Guid? existingTenantId = null, Guid? existingCompanyId = null)
        {
            var tenantId = existingTenantId ?? Guid.NewGuid();
            var companyId = existingCompanyId ?? Guid.NewGuid();
            var userId = Guid.NewGuid();
            var registry = new IntegrationEventRegistry();
            // Explicit test event; currency configuration does not publish a production integration event.
            registry.Add<TestEvent>("tests.currencies.atomicity.v1");
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
                    TraceIdentifier = "currencies-atomicity-test"
                }
            });
            registrations.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connectionString));
            registrations.AddScoped<OneShotSaveHook>();
            registrations.AddScoped<CompanyLockProbe>();
            registrations.AddDbContext<PlatformDbContext>((provider, options) => options
                .UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection)
                .AddInterceptors(provider.GetRequiredService<OneShotSaveHook>(), provider.GetRequiredService<CompanyLockProbe>()));
            registrations.AddScoped<IBusinessTransactionCoordinator, BusinessTransactionCoordinator>();
            registrations.AddScoped<AuditLogService>();
            registrations.AddScoped<InjectingAudit>();
            registrations.AddScoped<IAuditLogWriter>(provider => provider.GetRequiredService<InjectingAudit>());
            registrations.AddScoped<IOutboxWriter, PostgresOutboxWriter>();
            registrations.AddScoped<ICurrencyConfigurationService, CurrencyConfigurationService>();
            var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var services = provider.CreateAsyncScope();
            var scope = new TestScope(provider, services, connectionString, tenantId, companyId, userId);
            try
            {
                if (existingCompanyId is not null) return scope;
                if (existingTenantId is null) scope.Platform.Tenants.Add(new Tenant
                {
                    Id = tenantId,
                    Name = "Currency atomicity",
                    Slug = $"currency-{tenantId:N}",
                    TimeZoneId = "UTC",
                    IsActive = true,
                    CreatedAtUtc = FixedClock.Now
                });
                scope.Platform.Companies.Add(new Company
                {
                    Id = companyId,
                    TenantId = tenantId,
                    Code = companyId.ToString("N")[..20],
                    Name = "Currency atomicity",
                    BaseCurrencyCode = "USD",
                    TimeZoneId = "UTC",
                    IsActive = true
                });
                foreach (var code in new[] { "USD", "KHR" })
                    scope.Platform.CompanyCurrencies.Add(new CompanyCurrency
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        CompanyId = companyId,
                        Code = code,
                        Name = code == "USD" ? "US Dollar" : "Cambodian Riel",
                        Symbol = code,
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

    private sealed class InjectingAudit(AuditLogService realAudit) : IAuditLogWriter
    {
        public bool FailAfterAppend { get; set; }

        public void Append(AuditWriteEntry entry)
        {
            realAudit.Append(entry);
            if (FailAfterAppend)
            {
                FailAfterAppend = false;
                throw new InjectedFailureException();
            }
        }

        public Task FlushAsync(CancellationToken cancellationToken) => realAudit.FlushAsync(cancellationToken);
    }

    private sealed class OneShotSaveHook : SaveChangesInterceptor
    {
        public Action<PlatformDbContext>? BeforeSave { get; set; }
        public Func<CancellationToken, Task>? AfterSave { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is { } callback)
            {
                BeforeSave = null;
                callback((PlatformDbContext)eventData.Context!);
            }
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (AfterSave is { } callback)
            {
                AfterSave = null;
                await callback(cancellationToken);
            }
            return result;
        }
    }

    private sealed class CompanyLockProbe : DbCommandInterceptor
    {
        public TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
                Started.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedFailureException : Exception;
    private sealed record TestEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
    private sealed class TestHttpContextAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class TestTenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class TestCompany(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }
}

public sealed class AtomicCurrencyPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("currencies_atomicity_tests");
        ConnectionString = _postgres.GetConnectionString();
        await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options);
        await platform.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}
