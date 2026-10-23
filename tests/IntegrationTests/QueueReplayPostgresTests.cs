using System.Data.Common;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Operations;
using Erp.Abstractions.Persistence;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Operations;
using Erp.Infrastructure.Persistence;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class QueueReplayPostgresTests(QueueReplayPostgresFixture fixture)
    : IClassFixture<QueueReplayPostgresFixture>
{
    [Theory]
    [InlineData("outbox")]
    [InlineData("job")]
    public async Task SuccessfulReplayResetsOnlyEligibleQueueStateAndWritesScopedAudit(string kind)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, kind);
        Assert.True(await ReplayAsync(scope, kind));

        await using var reader = scope.ReadPlatform();
        if (kind == "outbox")
        {
            var row = await reader.OutboxMessages.SingleAsync(value => value.Id == scope.QueueId);
            Assert.Null(row.DeadLetteredAtUtc);
            Assert.Null(row.ProcessedAtUtc);
            Assert.Null(row.LeaseId);
            Assert.Null(row.LockedUntilUtc);
            Assert.Null(row.LastError);
            Assert.Equal(0, row.RetryCount);
            Assert.Equal(FixedClock.Now, row.NextAttemptAtUtc);
            Assert.Equal("{}", row.PayloadJson);
            Assert.Equal("original-correlation", row.CorrelationId);
        }
        else
        {
            var row = await reader.BackgroundJobs.SingleAsync(value => value.Id == scope.QueueId);
            Assert.Equal("queued", row.State);
            Assert.Equal(0, row.Attempts);
            Assert.Equal(3, row.MaxAttempts);
            Assert.Null(row.LeaseId);
            Assert.Null(row.LockedUntilUtc);
            Assert.Null(row.CompletedAtUtc);
            Assert.Null(row.LastError);
            Assert.Equal(FixedClock.Now, row.ExecuteAfterUtc);
            Assert.Equal("{}", row.PayloadJson);
            Assert.Equal("original-correlation", row.CorrelationId);
            Assert.Equal("retained-idempotency-key", row.IdempotencyKey);
        }
        var audit = await reader.AuditLogs.SingleAsync();
        Assert.Equal(kind == "outbox" ? "platform.outbox.replayed" : "platform.background_job.replayed", audit.Action);
        Assert.Equal(kind == "outbox" ? "OutboxMessage" : "BackgroundJob", audit.EntityType);
        Assert.Equal(scope.QueueId.ToString(), audit.EntityId);
        Assert.Equal(scope.TenantId, audit.TenantId);
        Assert.Equal(scope.CompanyId, audit.CompanyId);
        Assert.Equal(scope.UserId, audit.UserId);
        Assert.Equal(FixedClock.Now, audit.TimestampUtc);
        Assert.Equal("queue-replay-test", audit.CorrelationId);
        Assert.Null(audit.MetadataJson);
        Assert.Null(audit.BeforeJson);
        Assert.Null(audit.AfterJson);
        var replayed = await SnapshotAsync(scope, kind);
        Assert.False(await ReplayAsync(scope, kind));
        Assert.Equal(replayed, await SnapshotAsync(scope, kind));
    }

    [Theory]
    [InlineData("outbox", "failure")]
    [InlineData("job", "failure")]
    [InlineData("outbox", "cancellation")]
    [InlineData("job", "cancellation")]
    [InlineData("outbox", "append")]
    [InlineData("job", "append")]
    [InlineData("outbox", "constraint")]
    [InlineData("job", "constraint")]
    public async Task ReplayFailureRollsBackQueueAndRealAuditAndAllowsSameScopeRetry(string kind, string failure)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, kind);
        var original = await SnapshotAsync(scope, kind);
        using var cancelled = new CancellationTokenSource();
        if (failure == "append") scope.Audit.FailAfterAppend = true;
        else if (failure == "constraint")
            scope.Hook.BeforeSave = database => database.AuditLogs.Local.Single().CorrelationId = new string('x', 101);
        else scope.Hook.AfterSave = token =>
        {
            if (failure != "cancellation") return Task.FromException(new InjectedFailureException());
            cancelled.Cancel();
            return Task.FromCanceled(token);
        };

        if (failure == "cancellation")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReplayAsync(scope, kind, token: cancelled.Token));
        else if (failure == "constraint")
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => ReplayAsync(scope, kind));
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        else await Assert.ThrowsAsync<InjectedFailureException>(() => ReplayAsync(scope, kind));

        Assert.Equal(original, await SnapshotAsync(scope, kind));
        AssertClean(scope);
        Assert.True(await ReplayAsync(scope, kind));
        Assert.Equal(1, (await SnapshotAsync(scope, kind)).Audits);
        Assert.False(await ReplayAsync(scope, kind));
        Assert.Equal(1, (await SnapshotAsync(scope, kind)).Audits);
    }

    [Theory]
    [InlineData("outbox")]
    [InlineData("job")]
    public async Task ConcurrentReplayWaitsForTheRealRowLockAndOnlyOneRequestAudits(string kind)
    {
        await using var first = await TestScope.CreateAsync(fixture.ConnectionString, kind);
        await using var second = await TestScope.OpenAsync(fixture.ConnectionString, first);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Hook.AfterSave = async token =>
        {
            saved.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        };
        var firstReplay = ReplayAsync(first, kind);
        Task<bool>? secondReplay = null;
        Exception? observationFailure = null;
        try
        {
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(15));
            secondReplay = ReplayAsync(second, kind);
            var waitingPid = await second.Probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForBlockingAsync(fixture.ConnectionString, waitingPid, first.BackendPid);
            Assert.False(secondReplay.IsCompleted);
        }
        catch (Exception exception) { observationFailure = exception; }
        finally { release.TrySetResult(); }

        var firstFailure = await Record.ExceptionAsync(() => firstReplay.WaitAsync(TimeSpan.FromSeconds(15)));
        var secondFailure = secondReplay is null ? null : await Record.ExceptionAsync(() => secondReplay.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Null(observationFailure);
        Assert.Null(firstFailure);
        Assert.Null(secondFailure);
        Assert.True(await firstReplay);
        Assert.NotNull(secondReplay);
        Assert.False(await secondReplay);
        Assert.Equal(1, (await SnapshotAsync(first, kind)).Audits);
        Assert.Null(first.Platform.Database.CurrentTransaction);
        Assert.Null(second.Platform.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("outbox")]
    [InlineData("job")]
    public async Task StaleReplayCannotOverwriteAWorkerClaimAfterAnotherReplayWins(string kind)
    {
        await using var stale = await TestScope.CreateAsync(fixture.ConnectionString, kind);
        await using var winner = await TestScope.OpenAsync(fixture.ConnectionString, stale);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stale.Probe.BeforeUpdate = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        };
        var staleReplay = ReplayAsync(stale, kind);
        Snapshot? claimed = null;
        Exception? observationFailure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(await ReplayAsync(winner, kind));
            await ClaimForWorkerAsync(winner, kind);
            claimed = await SnapshotAsync(winner, kind);
        }
        catch (Exception exception) { observationFailure = exception; }
        finally { release.TrySetResult(); }

        var staleFailure = await Record.ExceptionAsync(() => staleReplay.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Null(observationFailure);
        Assert.Null(staleFailure);
        Assert.False(await staleReplay);
        Assert.NotNull(claimed);
        Assert.Equal(claimed, await SnapshotAsync(winner, kind));
        Assert.Null(stale.Platform.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("outbox", false)]
    [InlineData("job", false)]
    [InlineData("outbox", true)]
    [InlineData("job", true)]
    public async Task OuterTransactionOwnsReplayAndAuditCommitOrRollback(string kind, bool commit)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, kind);
        var original = await SnapshotAsync(scope, kind);
        await using (var outer = await scope.Transactions.BeginAsync(scope.Platform, CancellationToken.None))
        {
            AddOuterAudit(scope);
            Assert.True(await ReplayAsync(scope, kind));
            if (commit) await outer.CommitAsync(CancellationToken.None);
        }
        if (commit) Assert.Equal(2, (await SnapshotAsync(scope, kind)).Audits);
        else
        {
            Assert.Equal(original, await SnapshotAsync(scope, kind));
            AssertClean(scope);
            Assert.True(await ReplayAsync(scope, kind));
            Assert.Equal(1, (await SnapshotAsync(scope, kind)).Audits);
        }
    }

    [Theory]
    [InlineData("outbox")]
    [InlineData("job")]
    public async Task MissingNoOpCompletesItsNestedScopeAndDoesNotPoisonOuterWork(string kind)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, kind);
        var original = await SnapshotAsync(scope, kind);
        await using (var outer = await scope.Transactions.BeginAsync(scope.Platform, CancellationToken.None))
        {
            AddOuterAudit(scope);
            Assert.False(await ReplayAsync(scope, kind, Guid.NewGuid()));
            await outer.CommitAsync(CancellationToken.None);
        }
        var actual = await SnapshotAsync(scope, kind);
        Assert.Equal(original.Entity, actual.Entity);
        Assert.Equal(1, actual.Audits);
        await using var reader = scope.ReadPlatform();
        Assert.Equal("tests.queue.outer", (await reader.AuditLogs.SingleAsync()).Action);
    }

    [Theory]
    [InlineData("outbox")]
    [InlineData("job")]
    public async Task PendingCallerWorkIsNeitherSavedNorDiscardedByRejectedReplay(string kind)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, kind);
        var original = await SnapshotAsync(scope, kind);
        AddOuterAudit(scope);
        var staged = Assert.Single(scope.Platform.ChangeTracker.Entries());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReplayAsync(scope, kind));
        Assert.Same(staged.Entity, Assert.Single(scope.Platform.ChangeTracker.Entries()).Entity);
        Assert.Equal(EntityState.Added, staged.State);
        Assert.Equal(original, await SnapshotAsync(scope, kind));
        Assert.Null(scope.Platform.Database.CurrentTransaction);
        scope.Platform.ChangeTracker.Clear(); // The caller owns this explicitly staged test row.
        Assert.True(await ReplayAsync(scope, kind));
        Assert.Equal(1, (await SnapshotAsync(scope, kind)).Audits);
    }

    [Theory]
    [InlineData("outbox")]
    [InlineData("job")]
    public async Task UnmanagedCallerTransactionIsRejectedWithoutTakingOwnership(string kind)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, kind);
        var original = await SnapshotAsync(scope, kind);
        await using (var unmanaged = await scope.Platform.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReplayAsync(scope, kind));
            Assert.Same(unmanaged, scope.Platform.Database.CurrentTransaction);
            await scope.Platform.Database.ExecuteSqlRawAsync("SELECT 1");
            await unmanaged.RollbackAsync();
        }
        Assert.Equal(original, await SnapshotAsync(scope, kind));
        AssertClean(scope);
        Assert.True(await ReplayAsync(scope, kind));
    }

    [Theory]
    [InlineData("outbox", "company")]
    [InlineData("job", "company")]
    [InlineData("outbox", "tenant")]
    [InlineData("job", "tenant")]
    [InlineData("outbox", "mismatch")]
    [InlineData("job", "mismatch")]
    public async Task ForeignRowsAndMismatchedContextCannotBeReplayedOrAudited(string kind, string scenario)
    {
        await using var own = await TestScope.CreateAsync(fixture.ConnectionString, kind);
        await using var foreign = await TestScope.CreateAsync(fixture.ConnectionString, kind,
            scenario == "company" ? own.TenantId : null);
        var originalOwn = await SnapshotAsync(own, kind);
        var originalForeign = await SnapshotAsync(foreign, kind);
        await using var mismatched = await TestScope.OpenAsync(fixture.ConnectionString, foreign, own.TenantId);
        var actor = scenario == "mismatch" ? mismatched : own;

        Assert.False(await ReplayAsync(actor, kind, foreign.QueueId));

        Assert.Equal(originalOwn, await SnapshotAsync(own, kind));
        Assert.Equal(originalForeign, await SnapshotAsync(foreign, kind));
        AssertClean(actor);
    }

    private static Task<bool> ReplayAsync(TestScope scope, string kind, Guid? id = null, CancellationToken token = default) =>
        kind == "outbox" ? scope.Service.ReplayOutboxAsync(id ?? scope.QueueId, token)
            : scope.Service.ReplayJobAsync(id ?? scope.QueueId, token);

    private static async Task ClaimForWorkerAsync(TestScope scope, string kind)
    {
        // Represent the worker's atomic claim; delivery/result ownership is covered by the worker-specific suite.
        await using var writer = scope.ReadPlatform();
        var lease = Guid.NewGuid();
        var until = FixedClock.Now.AddMinutes(1);
        var affected = kind == "outbox"
            ? await writer.OutboxMessages.Where(value => value.Id == scope.QueueId && value.CompanyId == scope.CompanyId
                    && value.DeadLetteredAtUtc == null && value.ProcessedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseId, lease)
                    .SetProperty(value => value.LockedUntilUtc, until).SetProperty(value => value.RetryCount, 1))
            : await writer.BackgroundJobs.Where(value => value.Id == scope.QueueId && value.CompanyId == scope.CompanyId && value.State == "queued")
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseId, lease)
                    .SetProperty(value => value.LockedUntilUtc, until).SetProperty(value => value.Attempts, 1)
                    .SetProperty(value => value.State, "running"));
        Assert.Equal(1, affected);
    }

    private static void AddOuterAudit(TestScope scope) => scope.Platform.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        TenantId = scope.TenantId,
        CompanyId = scope.CompanyId,
        Action = "tests.queue.outer",
        EntityType = "Queue",
        EntityId = scope.QueueId.ToString(),
        TimestampUtc = FixedClock.Now,
        CorrelationId = "outer-work"
    });

    private static void AssertClean(TestScope scope)
    {
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    private static async Task<Snapshot> SnapshotAsync(TestScope scope, string kind)
    {
        await using var reader = scope.ReadPlatform();
        var entity = kind == "outbox"
            ? JsonSerializer.Serialize(await reader.OutboxMessages.AsNoTracking().SingleAsync(value => value.Id == scope.QueueId))
            : JsonSerializer.Serialize(await reader.BackgroundJobs.AsNoTracking().SingleAsync(value => value.Id == scope.QueueId));
        return new(entity, await reader.AuditLogs.CountAsync(value => value.CompanyId == scope.CompanyId));
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
        Assert.Fail("The competing replay never waited for the first request's real PostgreSQL row lock.");
    }

    private sealed record Snapshot(string Entity, int Audits);

    private sealed class TestScope(ServiceProvider provider, AsyncServiceScope services, string connectionString,
        Guid tenantId, Guid companyId, Guid queueId, Guid userId) : IAsyncDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid CompanyId { get; } = companyId;
        public Guid QueueId { get; } = queueId;
        public Guid UserId { get; } = userId;
        public PlatformDbContext Platform => services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        public IQueueOperationsService Service => services.ServiceProvider.GetRequiredService<IQueueOperationsService>();
        public IBusinessTransactionCoordinator Transactions => services.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        public InjectingAudit Audit => services.ServiceProvider.GetRequiredService<InjectingAudit>();
        public SaveHook Hook => services.ServiceProvider.GetRequiredService<SaveHook>();
        public QueueWriteProbe Probe => services.ServiceProvider.GetRequiredService<QueueWriteProbe>();
        public int BackendPid => ((NpgsqlConnection)Platform.Database.GetDbConnection()).ProcessID;
        public PlatformDbContext ReadPlatform() => new(
            new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId));

        public static Task<TestScope> OpenAsync(string connectionString, TestScope existing, Guid? tenantOverride = null) =>
            CreateAsync(connectionString, "outbox", tenantOverride ?? existing.TenantId, existing.CompanyId, existing.QueueId);

        public static async Task<TestScope> CreateAsync(string connectionString, string kind, Guid? existingTenantId = null,
            Guid? existingCompanyId = null, Guid? existingQueueId = null)
        {
            var tenantId = existingTenantId ?? Guid.NewGuid();
            var companyId = existingCompanyId ?? Guid.NewGuid();
            var queueId = existingQueueId ?? Guid.NewGuid();
            var userId = Guid.NewGuid();
            var registrations = new ServiceCollection();
            registrations.AddSingleton<ITenantContext>(new TestTenant(tenantId));
            registrations.AddSingleton<ICompanyContext>(new TestCompany(companyId));
            registrations.AddSingleton<IClock>(new FixedClock());
            registrations.AddSingleton<IHttpContextAccessor>(new TestHttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                    TraceIdentifier = "queue-replay-test"
                }
            });
            registrations.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connectionString));
            registrations.AddScoped<SaveHook>();
            registrations.AddScoped<QueueWriteProbe>();
            registrations.AddDbContext<PlatformDbContext>((provider, options) => options
                .UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection)
                .AddInterceptors(provider.GetRequiredService<SaveHook>(), provider.GetRequiredService<QueueWriteProbe>()));
            registrations.AddScoped<IBusinessTransactionCoordinator, BusinessTransactionCoordinator>();
            registrations.AddScoped<AuditLogService>();
            registrations.AddScoped<InjectingAudit>();
            registrations.AddScoped<IAuditLogWriter>(provider => provider.GetRequiredService<InjectingAudit>());
            registrations.AddScoped<IQueueOperationsService, QueueOperationsService>();
            var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var services = provider.CreateAsyncScope();
            var scope = new TestScope(provider, services, connectionString, tenantId, companyId, queueId, userId);
            try
            {
                if (existingCompanyId is not null) return scope;
                if (existingTenantId is null) scope.Platform.Tenants.Add(new Tenant
                {
                    Id = tenantId,
                    Name = "Queue replay",
                    Slug = $"queue-{tenantId:N}",
                    TimeZoneId = "UTC",
                    IsActive = true,
                    CreatedAtUtc = FixedClock.Now
                });
                scope.Platform.Companies.Add(new Company
                {
                    Id = companyId,
                    TenantId = tenantId,
                    Code = companyId.ToString("N")[..20],
                    Name = "Queue replay",
                    BaseCurrencyCode = "USD",
                    TimeZoneId = "UTC",
                    IsActive = true
                });
                if (kind == "outbox") scope.Platform.OutboxMessages.Add(new OutboxMessage
                {
                    Id = queueId,
                    TenantId = tenantId,
                    CompanyId = companyId,
                    EventType = "tests.queue.replay.v1",
                    PayloadJson = "{}",
                    CorrelationId = "original-correlation",
                    OccurredAtUtc = FixedClock.Now.AddDays(-10),
                    NextAttemptAtUtc = FixedClock.Now.AddHours(-1),
                    DeadLetteredAtUtc = FixedClock.Now.AddHours(-1),
                    RetryCount = 3,
                    LastError = "RecordedFailure",
                    LeaseId = Guid.NewGuid(),
                    LockedUntilUtc = FixedClock.Now.AddMinutes(-5)
                });
                else scope.Platform.BackgroundJobs.Add(new BackgroundJob
                {
                    Id = queueId,
                    TenantId = tenantId,
                    CompanyId = companyId,
                    Name = "tests.queue.replay",
                    PayloadJson = "{}",
                    CorrelationId = "original-correlation",
                    CreatedAtUtc = FixedClock.Now.AddDays(-10),
                    ExecuteAfterUtc = FixedClock.Now.AddHours(-1),
                    CompletedAtUtc = FixedClock.Now.AddHours(-1),
                    State = "dead_letter",
                    Attempts = 3,
                    MaxAttempts = 3,
                    LastError = "RecordedFailure",
                    LeaseId = Guid.NewGuid(),
                    LockedUntilUtc = FixedClock.Now.AddMinutes(-5),
                    IdempotencyKey = "retained-idempotency-key"
                });
                await scope.Platform.SaveChangesAsync();
                scope.Platform.ChangeTracker.Clear();
                scope.Probe.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return scope;
            }
            catch { await scope.DisposeAsync(); throw; }
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
            if (!FailAfterAppend) return;
            FailAfterAppend = false;
            throw new InjectedFailureException();
        }
        public Task FlushAsync(CancellationToken cancellationToken) => realAudit.FlushAsync(cancellationToken);
    }

    private sealed class SaveHook : SaveChangesInterceptor
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

    private sealed class QueueWriteProbe : DbCommandInterceptor
    {
        public TaskCompletionSource<int> Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task>? BeforeUpdate { get; set; }
        private async Task ObserveAsync(DbCommand command, CancellationToken token)
        {
            if (!command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !(command.CommandText.Contains("outbox_messages", StringComparison.OrdinalIgnoreCase)
                    || command.CommandText.Contains("background_jobs", StringComparison.OrdinalIgnoreCase))) return;
            Started.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            if (BeforeUpdate is not { } callback) return;
            BeforeUpdate = null;
            await callback(token);
        }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command, cancellationToken);
            return result;
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command, cancellationToken);
            return result;
        }
    }

    private sealed class InjectedFailureException : Exception;
    private sealed class TestHttpContextAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class TestTenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class TestCompany(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }
}

public sealed class QueueReplayPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;
    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("queue_replay_tests");
        ConnectionString = _postgres.GetConnectionString();
        try
        {
            await using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options);
            await database.Database.MigrateAsync();
        }
        catch { await _postgres.DisposeAsync(); throw; }
    }
    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}
