using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.Abstractions.Jobs;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Events;
using Erp.Infrastructure.Jobs;
using Erp.Infrastructure.Operations;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Security;
using Erp.ModuleSystem;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class QueueLeasePostgresTests(QueueLeasePostgresFixture fixture)
    : IClassFixture<QueueLeasePostgresFixture>
{
    [Theory]
    [InlineData(true, false, "succeeded")]
    [InlineData(true, true, "succeeded")]
    [InlineData(true, false, "running")]
    [InlineData(true, true, "running")]
    [InlineData(true, false, "deadletter")]
    [InlineData(true, true, "deadletter")]
    [InlineData(true, false, "queuedretry")]
    [InlineData(true, true, "queuedretry")]
    [InlineData(false, false, "succeeded")]
    [InlineData(false, true, "succeeded")]
    [InlineData(false, false, "running")]
    [InlineData(false, true, "running")]
    [InlineData(false, false, "deadletter")]
    [InlineData(false, true, "deadletter")]
    [InlineData(false, false, "queuedretry")]
    [InlineData(false, true, "queuedretry")]
    public async Task StaleCompletionCannotOverwriteNewerLease(bool outbox, bool oldSucceeds, string newerState)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, outbox, newerState == "queuedretry" ? 3 : 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var first = scope.ExecuteAsync(timeout.Token);
        Task<bool>? second = null;
        try
        {
            var stale = await scope.Gate.NextAsync(timeout.Token);
            scope.AssertInvocation(stale, 1);
            scope.Time.Advance(TimeSpan.FromSeconds(11));
            second = scope.ExecuteAsync(timeout.Token);
            var current = await scope.Gate.NextAsync(timeout.Token);
            scope.AssertInvocation(current, 2);
            if (newerState != "running")
            {
                current.Complete(newerState == "succeeded");
                Assert.True(await second);
            }
            var beforeStaleCompletion = await scope.SnapshotAsync();
            await scope.AssertPhaseAsync(newerState, 2);
            var acceptedLogs = scope.Log.AcceptedCount;
            var staleLogs = scope.Log.StaleCount;

            stale.Complete(oldSucceeds);
            Assert.True(await first);

            Assert.Equal(beforeStaleCompletion, await scope.SnapshotAsync());
            Assert.Equal(acceptedLogs, scope.Log.AcceptedCount);
            Assert.Equal(staleLogs + 1, scope.Log.StaleCount);
            if (newerState == "running")
            {
                current.Complete(succeeded: true);
                Assert.True(await second);
                await scope.AssertPhaseAsync("succeeded", 2);
            }
            else if (newerState == "queuedretry")
            {
                // The accepted second failure keeps its normal backoff and retry budget.
                scope.Time.Advance(TimeSpan.FromSeconds(2));
                second = scope.ExecuteAsync(timeout.Token);
                var last = await scope.Gate.NextAsync(timeout.Token);
                scope.AssertInvocation(last, 3);
                last.Complete(succeeded: false);
                Assert.True(await second);
                await scope.AssertPhaseAsync("deadletter", 3);
            }
            Assert.False(await scope.ExecuteAsync(timeout.Token));
        }
        finally
        {
            scope.Gate.ReleaseAll();
            Task[] pending = second is null ? [first] : [first, second];
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplayUsesFreshLeaseEvenWhenClockAndAttemptRepeat(bool outbox)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, outbox, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var originalTime = scope.Time.GetUtcNow();
        var first = scope.ExecuteAsync(timeout.Token);
        Task<bool>? second = null;
        Task<bool>? third = null;
        try
        {
            var stale = await scope.Gate.NextAsync(timeout.Token);
            scope.AssertInvocation(stale, 1);
            var originalLease = await scope.ClaimAsync();
            Assert.NotNull(originalLease.LeaseId);
            scope.Time.Advance(TimeSpan.FromSeconds(11));
            second = scope.ExecuteAsync(timeout.Token);
            var newer = await scope.Gate.NextAsync(timeout.Token);
            scope.AssertInvocation(newer, 2);
            newer.Complete(succeeded: false);
            Assert.True(await second);
            await scope.AssertPhaseAsync("deadletter", 2);

            // A wall-clock rollback plus replay can reuse both old timestamp and attempt.
            scope.Time.Set(originalTime);
            Assert.True(await scope.ReplayAsync(timeout.Token));
            third = scope.ExecuteAsync(timeout.Token);
            var replayed = await scope.Gate.NextAsync(timeout.Token);
            scope.AssertInvocation(replayed, 1);
            var replayLease = await scope.ClaimAsync();
            Assert.Equal(originalLease.Attempt, replayLease.Attempt);
            Assert.Equal(originalLease.LockedUntilUtc, replayLease.LockedUntilUtc);
            Assert.NotNull(replayLease.LeaseId);
            Assert.NotEqual(originalLease.LeaseId, replayLease.LeaseId);
            var beforeStaleCompletion = await scope.SnapshotAsync();
            var acceptedLogs = scope.Log.AcceptedCount;
            var staleLogs = scope.Log.StaleCount;

            stale.Complete(succeeded: true);
            Assert.True(await first);

            Assert.Equal(beforeStaleCompletion, await scope.SnapshotAsync());
            Assert.Equal(acceptedLogs, scope.Log.AcceptedCount);
            Assert.Equal(staleLogs + 1, scope.Log.StaleCount);
            replayed.Complete(succeeded: true);
            Assert.True(await third);
            await scope.AssertPhaseAsync("succeeded", 1);
            Assert.False(await scope.ExecuteAsync(timeout.Token));
        }
        finally
        {
            scope.Gate.ReleaseAll();
            var pending = new List<Task> { first };
            if (second is not null) pending.Add(second);
            if (third is not null) pending.Add(third);
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationLeavesCurrentLeaseUnchangedAndExpiryAllowsRecovery(bool outbox)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, outbox, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var first = scope.ExecuteAsync(cancelled.Token);
        Task<bool>? second = null;
        try
        {
            scope.AssertInvocation(await scope.Gate.NextAsync(timeout.Token), 1);
            var originalLease = await scope.ClaimAsync();
            var original = await scope.SnapshotAsync();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

            Assert.Equal(original, await scope.SnapshotAsync());
            Assert.Equal(0, scope.Log.AcceptedCount);
            Assert.Equal(0, scope.Log.StaleCount);
            scope.Time.Advance(TimeSpan.FromSeconds(11));
            second = scope.ExecuteAsync(timeout.Token);
            var recovered = await scope.Gate.NextAsync(timeout.Token);
            scope.AssertInvocation(recovered, 2);
            var recoveredLease = await scope.ClaimAsync();
            Assert.NotNull(recoveredLease.LeaseId);
            Assert.NotEqual(originalLease.LeaseId, recoveredLease.LeaseId);
            recovered.Complete(succeeded: true);
            Assert.True(await second);
            await scope.AssertPhaseAsync("succeeded", 2);
        }
        finally
        {
            scope.Gate.ReleaseAll();
            try { await first.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeaseIsNotReclaimableAtExactDeadlineButIsOneMicrosecondLater(bool outbox)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, outbox, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var first = scope.ExecuteAsync(timeout.Token);
        Task<bool>? second = null;
        try
        {
            var stale = await scope.Gate.NextAsync(timeout.Token);
            scope.AssertInvocation(stale, 1);
            var original = await scope.SnapshotAsync();
            scope.Time.Advance(TimeSpan.FromSeconds(10));

            Assert.False(await scope.ExecuteAsync(timeout.Token));
            Assert.Equal(original, await scope.SnapshotAsync());

            scope.Time.Advance(TimeSpan.FromMicroseconds(1));
            second = scope.ExecuteAsync(timeout.Token);
            var current = await scope.Gate.NextAsync(timeout.Token);
            scope.AssertInvocation(current, 2);
            var replacement = await scope.SnapshotAsync();
            stale.Complete(succeeded: true);
            Assert.True(await first);
            Assert.Equal(replacement, await scope.SnapshotAsync());
            current.Complete(succeeded: true);
            Assert.True(await second);
            await scope.AssertPhaseAsync("succeeded", 2);
        }
        finally
        {
            scope.Gate.ReleaseAll();
            Task[] pending = second is null ? [first] : [first, second];
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiredButUnreplacedOwnerCanStillComplete(bool outbox)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString, outbox, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var execution = scope.ExecuteAsync(timeout.Token);
        try
        {
            var owner = await scope.Gate.NextAsync(timeout.Token);
            scope.AssertInvocation(owner, 1);
            scope.Time.Advance(TimeSpan.FromSeconds(11));

            owner.Complete(succeeded: true);

            Assert.True(await execution);
            await scope.AssertPhaseAsync("succeeded", 1);
            Assert.Equal(1, scope.Log.AcceptedCount);
            Assert.Equal(0, scope.Log.StaleCount);
            Assert.False(await scope.ExecuteAsync(timeout.Token));
        }
        finally
        {
            scope.Gate.ReleaseAll();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class TestScope(ServiceProvider provider, DbContextOptions<PlatformDbContext> options,
        Guid tenantId, Guid companyId, Guid itemId, bool outbox, ControlledTime time, HandlerGate gate, QueueLog log) : IAsyncDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid CompanyId { get; } = companyId;
        public Guid ItemId { get; } = itemId;
        public ControlledTime Time { get; } = time;
        public HandlerGate Gate { get; } = gate;
        public QueueLog Log { get; } = log;

        public Task<bool> ExecuteAsync(CancellationToken cancellationToken) => outbox
            ? provider.GetRequiredService<PostgresOutboxWorker>().PublishNextAsync(cancellationToken)
            : provider.GetRequiredService<PostgresBackgroundJobWorker>().ExecuteNextAsync(cancellationToken);

        public void AssertInvocation(Invocation invocation, int attempt)
        {
            Assert.Equal(ItemId, invocation.Id);
            Assert.Equal(attempt, invocation.Attempt);
            Assert.Equal(TenantId, invocation.TenantId);
            Assert.Equal(CompanyId, invocation.CompanyId);
            Assert.Equal(TenantId, invocation.ExecutionTenantId);
            Assert.Equal(CompanyId, invocation.ExecutionCompanyId);
            Assert.Equal("queue-lease-test", invocation.CorrelationId);
        }

        public async Task<string> SnapshotAsync()
        {
            await using var reader = new PlatformDbContext(options);
            return outbox
                ? JsonSerializer.Serialize(await reader.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == ItemId))
                : JsonSerializer.Serialize(await reader.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == ItemId));
        }

        public async Task<Claim> ClaimAsync()
        {
            await using var reader = new PlatformDbContext(options);
            if (outbox)
            {
                var item = await reader.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == ItemId);
                return new(item.RetryCount, item.LockedUntilUtc, item.LeaseId);
            }
            var job = await reader.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == ItemId);
            return new(job.Attempts, job.LockedUntilUtc, job.LeaseId);
        }

        public async Task AssertPhaseAsync(string phase, int attempt)
        {
            await using var reader = new PlatformDbContext(options);
            if (outbox)
            {
                var item = await reader.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == ItemId);
                Assert.Equal(TenantId, item.TenantId);
                Assert.Equal(CompanyId, item.CompanyId);
                Assert.Equal(attempt, item.RetryCount);
                Assert.Equal(phase == "succeeded" ? Time.GetUtcNow() : (DateTimeOffset?)null, item.ProcessedAtUtc);
                Assert.Equal(phase == "deadletter" ? Time.GetUtcNow() : (DateTimeOffset?)null, item.DeadLetteredAtUtc);
                AssertLease(phase, item.LeaseId, item.LockedUntilUtc);
                Assert.Equal(phase is "deadletter" or "queuedretry" ? nameof(ControlledFailureException) : null, item.LastError);
                if (phase == "queuedretry") Assert.Equal(Time.GetUtcNow().AddSeconds(2), item.NextAttemptAtUtc);
                return;
            }
            var job = await reader.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == ItemId);
            Assert.Equal(TenantId, job.TenantId);
            Assert.Equal(CompanyId, job.CompanyId);
            Assert.Equal(attempt, job.Attempts);
            Assert.Equal(phase switch { "deadletter" => "dead_letter", "queuedretry" => "queued", _ => phase }, job.State);
            Assert.Equal(phase is "succeeded" or "deadletter" ? Time.GetUtcNow() : (DateTimeOffset?)null, job.CompletedAtUtc);
            AssertLease(phase, job.LeaseId, job.LockedUntilUtc);
            Assert.Equal(phase is "deadletter" or "queuedretry" ? nameof(ControlledFailureException) : null, job.LastError);
            if (phase == "queuedretry") Assert.Equal(Time.GetUtcNow().AddSeconds(2), job.ExecuteAfterUtc);
        }

        private void AssertLease(string phase, Guid? leaseId, DateTimeOffset? lockedUntil)
        {
            if (phase == "running")
            {
                Assert.NotNull(leaseId);
                Assert.NotEqual(Guid.Empty, leaseId);
                Assert.Equal(Time.GetUtcNow().AddSeconds(10), lockedUntil);
            }
            else
            {
                Assert.Null(leaseId);
                Assert.Null(lockedUntil);
            }
        }

        public async Task<bool> ReplayAsync(CancellationToken cancellationToken)
        {
            await using var database = new PlatformDbContext(options, new TestTenant(TenantId));
            var company = new TestCompany(CompanyId);
            var clock = new ProviderClock(Time);
            var audit = new AuditLogService(database, new TestTenant(TenantId), company, new HttpContextAccessor(), clock);
            var service = new QueueOperationsService(database, company, clock, audit, new BusinessTransactionCoordinator(database));
            var replayed = outbox
                ? await service.ReplayOutboxAsync(ItemId, cancellationToken)
                : await service.ReplayJobAsync(ItemId, cancellationToken);
            Assert.Equal(1, await database.AuditLogs.CountAsync(value => value.CompanyId == CompanyId, cancellationToken));
            Assert.False(database.ChangeTracker.HasChanges());
            Assert.Null(database.Database.CurrentTransaction);
            return replayed;
        }

        public static async Task<TestScope> CreateAsync(string connectionString, bool outbox, int maximumAttempts)
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options;
            var tenantId = Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var time = new ControlledTime();
            var gate = new HandlerGate();
            var log = new QueueLog();
            await using (var seed = new PlatformDbContext(options))
            {
                seed.Tenants.Add(new Tenant
                {
                    Id = tenantId,
                    Name = "Queue lease tests",
                    Slug = "queue-" + tenantId.ToString("N"),
                    TimeZoneId = "UTC",
                    IsActive = true,
                    CreatedAtUtc = time.GetUtcNow()
                });
                seed.Companies.Add(new Company
                {
                    Id = companyId,
                    TenantId = tenantId,
                    Code = companyId.ToString("N")[..20],
                    Name = "Queue lease tests",
                    BaseCurrencyCode = "USD",
                    TimeZoneId = "UTC",
                    IsActive = true
                });
                if (outbox)
                    seed.OutboxMessages.Add(new OutboxMessage
                    {
                        Id = itemId,
                        TenantId = tenantId,
                        CompanyId = companyId,
                        OccurredAtUtc = time.GetUtcNow(),
                        EventType = "tests.queue.lease.v1",
                        PayloadJson = JsonSerializer.Serialize(new WorkEvent(itemId, time.GetUtcNow())),
                        CorrelationId = "queue-lease-test",
                        NextAttemptAtUtc = time.GetUtcNow()
                    });
                else
                    seed.BackgroundJobs.Add(new BackgroundJob
                    {
                        Id = itemId,
                        TenantId = tenantId,
                        CompanyId = companyId,
                        Name = "tests.queue.lease",
                        PayloadJson = JsonSerializer.Serialize(new WorkPayload(itemId)),
                        State = "queued",
                        Attempts = 0,
                        MaxAttempts = maximumAttempts,
                        CreatedAtUtc = time.GetUtcNow(),
                        ExecuteAfterUtc = time.GetUtcNow(),
                        CorrelationId = "queue-lease-test"
                    });
                await seed.SaveChangesAsync();
            }

            var events = new IntegrationEventRegistry();
            events.Add<WorkEvent>("tests.queue.lease.v1");
            var jobs = new BackgroundJobRegistry();
            jobs.Add<ControlledJob, WorkPayload>("tests.queue.lease");
            var registrations = new ServiceCollection();
            registrations.AddSingleton(options);
            registrations.AddSingleton(gate);
            registrations.AddSingleton<TimeProvider>(time);
            registrations.AddSingleton<IIntegrationEventRegistry>(events);
            registrations.AddSingleton<IBackgroundJobRegistry>(jobs);
            registrations.AddHttpContextAccessor();
            registrations.AddScoped<ExecutionOrganizationContext>();
            registrations.AddScoped<ControlledJob>();
            registrations.AddScoped<IIntegrationEventConsumer<WorkEvent>, ControlledConsumer>();
            registrations.AddSingleton(provider => new PostgresOutboxWorker(
                provider.GetRequiredService<IServiceScopeFactory>(), events,
                Options.Create(new OutboxOptions { LeaseSeconds = 10, MaxAttempts = maximumAttempts }), time,
                new CapturingLogger<PostgresOutboxWorker>(log)));
            registrations.AddSingleton(provider => new PostgresBackgroundJobWorker(
                provider.GetRequiredService<IServiceScopeFactory>(), jobs,
                Options.Create(new BackgroundJobOptions { LeaseSeconds = 10 }), time,
                new CapturingLogger<PostgresBackgroundJobWorker>(log)));
            return new(registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }),
                options, tenantId, companyId, itemId, outbox, time, gate, log);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await using var cleanup = new PlatformDbContext(options);
            await cleanup.OutboxMessages.IgnoreQueryFilters().Where(value => value.Id == ItemId && value.TenantId == TenantId).ExecuteDeleteAsync();
            await cleanup.BackgroundJobs.IgnoreQueryFilters().Where(value => value.Id == ItemId && value.TenantId == TenantId).ExecuteDeleteAsync();
        }
    }

    private sealed class ControlledTime : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
    }

    private sealed record Claim(int Attempt, DateTimeOffset? LockedUntilUtc, Guid? LeaseId);

    private sealed class QueueLog
    {
        private readonly ConcurrentQueue<int> _events = new();
        public int AcceptedCount => _events.Count(id => id is 3101 or 3102 or 3301 or 3302);
        public int StaleCount => _events.Count(id => id is 3103 or 3303);
        public void Add(int id) => _events.Enqueue(id);
    }

    private sealed class CapturingLogger<T>(QueueLog log) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            log.Add(eventId.Id);
    }

    private sealed class TestTenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class TestCompany(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class ProviderClock(ControlledTime time) : IClock { public DateTimeOffset UtcNow => time.GetUtcNow(); }

    private sealed class HandlerGate
    {
        private readonly Channel<Invocation> _arrivals = Channel.CreateUnbounded<Invocation>();
        private readonly List<Invocation> _all = [];

        public async Task EnterAsync(Guid id, int attempt, TenantId tenant, CompanyId company, string correlation,
            ExecutionOrganizationContext execution, CancellationToken cancellationToken)
        {
            var invocation = new Invocation(id, attempt, tenant.Value, company.Value,
                execution.TenantId?.Value, execution.CompanyId?.Value, correlation);
            lock (_all) _all.Add(invocation);
            Assert.True(_arrivals.Writer.TryWrite(invocation));
            await invocation.Completion.Task.WaitAsync(cancellationToken);
        }

        public async Task<Invocation> NextAsync(CancellationToken cancellationToken) => await _arrivals.Reader.ReadAsync(cancellationToken);

        public void ReleaseAll()
        {
            lock (_all)
                foreach (var invocation in _all) invocation.Complete(succeeded: true);
        }
    }

    private sealed record Invocation(Guid Id, int Attempt, Guid TenantId, Guid CompanyId,
        Guid? ExecutionTenantId, Guid? ExecutionCompanyId, string CorrelationId)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(bool succeeded)
        {
            if (succeeded) Completion.TrySetResult();
            else Completion.TrySetException(new ControlledFailureException());
        }
    }

    private sealed class ControlledFailureException : Exception;
    private sealed record WorkPayload(Guid Id);
    private sealed record WorkEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

    private sealed class ControlledJob(HandlerGate gate, ExecutionOrganizationContext execution) : IBackgroundJobHandler<WorkPayload>
    {
        public Task ExecuteAsync(WorkPayload payload, BackgroundJobExecutionContext context, CancellationToken cancellationToken) =>
            gate.EnterAsync(payload.Id, context.Attempt, context.TenantId, context.CompanyId, context.CorrelationId, execution, cancellationToken);
    }

    private sealed class ControlledConsumer(HandlerGate gate, ExecutionOrganizationContext execution) : IIntegrationEventConsumer<WorkEvent>
    {
        public Task HandleAsync(WorkEvent integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken) =>
            gate.EnterAsync(integrationEvent.EventId, context.Attempt, context.TenantId, context.CompanyId, context.CorrelationId, execution, cancellationToken);
    }
}

public sealed class QueueLeasePostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("queue_lease_tests");
        ConnectionString = _postgres.GetConnectionString();
        await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options);
        await platform.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}
