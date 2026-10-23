using System.Runtime.ExceptionServices;
using System.Text.Json;
using Erp.Abstractions.Events;
using Erp.Abstractions.Jobs;
using Erp.Infrastructure.Events;
using Erp.Infrastructure.Jobs;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Persistence.Migrations;
using Erp.Infrastructure.Security;
using Erp.ModuleSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Erp.IntegrationTests;

public sealed class QueueLeaseMigrationPostgresTests
{
    private const string PreviousMigration = "20260914040542_TransactionalOutbox";
    private const string LeaseMigration = "20261021143000_QueueLeaseOwnership";
    private const string JobName = "tests.migration.legacy";
    private const string EventName = "tests.migration.legacy.v1";
    private static readonly DateTimeOffset Baseline = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LegacyQueueUpgradePreservesEveryColumnAndRecoversOnlyAfterExistingLeasesExpire()
    {
        // This test owns its database. No application worker or business database is used.
        await using var postgres = await IntegrationContainers.StartPostgresAsync("erp_queue_lease_migration_tests");
        var connectionString = postgres.GetConnectionString();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options;
        await using var database = new PlatformDbContext(options);
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        Assert.DoesNotContain(LeaseMigration, await database.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await LeaseColumnsAsync(connectionString));

        var legacy = await SeedLegacyAsync(connectionString);
        var before = await SnapshotAsync(connectionString);
        var schemaBefore = await ExistingColumnsAsync(connectionString);

        await migrator.MigrateAsync();

        Assert.Contains(LeaseMigration, await database.Database.GetAppliedMigrationsAsync());
        Assert.False(database.Database.HasPendingModelChanges());
        Assert.Equal(before, await SnapshotAsync(connectionString));
        Assert.Equal(schemaBefore, await ExistingColumnsAsync(connectionString));
        Assert.Equal(["background_jobs:uuid:YES:NULL", "outbox_messages:uuid:YES:NULL"],
            await LeaseColumnsAsync(connectionString));
        Assert.Collection(new QueueLeaseOwnership().UpOperations,
            operation => AssertLeaseAddition(operation, "background_jobs"),
            operation => AssertLeaseAddition(operation, "outbox_messages"));
        var jobs = await database.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var messages = await database.OutboxMessages.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(4, jobs.Count);
        Assert.Equal(4, messages.Count);
        Assert.All(jobs, row => Assert.Null(row.LeaseId));
        Assert.All(messages, row => Assert.Null(row.LeaseId));
        Assert.Equal(["dead_letter", "queued", "running", "succeeded"], jobs.Select(row => row.State).Order());

        var clock = new ControlledTimeProvider(Baseline);
        var jobGate = new ExecutionGate();
        var outboxGate = new ExecutionGate();
        var jobRegistry = new BackgroundJobRegistry();
        jobRegistry.Add<MigrationJob, LegacyPayload>(JobName);
        var eventRegistry = new IntegrationEventRegistry();
        eventRegistry.Add<LegacyEvent>(EventName);
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddHttpContextAccessor();
        services.AddScoped<ExecutionOrganizationContext>();
        services.AddScoped(_ => new MigrationJob(jobGate));
        services.AddScoped<IIntegrationEventConsumer<LegacyEvent>>(_ => new MigrationConsumer(outboxGate));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var jobWorker = new PostgresBackgroundJobWorker(provider.GetRequiredService<IServiceScopeFactory>(), jobRegistry,
            Options.Create(new BackgroundJobOptions { Enabled = true, LeaseSeconds = 30 }), clock,
            NullLogger<PostgresBackgroundJobWorker>.Instance);
        using var outboxWorker = new PostgresOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), eventRegistry,
            Options.Create(new OutboxOptions { Enabled = true, LeaseSeconds = 30, MaxAttempts = 5 }), clock,
            NullLogger<PostgresOutboxWorker>.Instance);

        var queuedLease = await ObserveClaimAsync(jobGate, () => jobWorker.ExecuteNextAsync(CancellationToken.None), async () =>
        {
            var row = await database.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == legacy.QueuedJob);
            Assert.Equal("running", row.State);
            Assert.Equal(1, row.Attempts);
            Assert.Equal(clock.GetUtcNow().AddSeconds(30), row.LockedUntilUtc);
            return (row.Id, row.LeaseId);
        }, legacy, legacy.QueuedJob, 1, "legacy queued");
        var pendingLease = await ObserveClaimAsync(outboxGate, () => outboxWorker.PublishNextAsync(CancellationToken.None), async () =>
        {
            var row = await database.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == legacy.PendingMessage);
            Assert.Equal(1, row.RetryCount);
            Assert.Equal(clock.GetUtcNow().AddSeconds(30), row.LockedUntilUtc);
            return (row.Id, row.LeaseId);
        }, legacy, legacy.PendingMessage, 1, "legacy pending");

        // A legacy in-flight row has no token, but its pre-upgrade time lease still owns it.
        Assert.False(await jobWorker.ExecuteNextAsync(CancellationToken.None));
        Assert.False(await outboxWorker.PublishNextAsync(CancellationToken.None));
        var running = await database.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == legacy.RunningJob);
        var leased = await database.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == legacy.LeasedMessage);
        Assert.Null(running.LeaseId);
        Assert.Null(leased.LeaseId);
        Assert.Equal(2, running.Attempts);
        Assert.Equal(2, leased.RetryCount);
        Assert.Equal(Baseline.AddMinutes(5), running.LockedUntilUtc);
        Assert.Equal(Baseline.AddMinutes(5), leased.LockedUntilUtc);

        clock.Advance(TimeSpan.FromMinutes(6));
        var recoveredJobLease = await ObserveClaimAsync(jobGate, () => jobWorker.ExecuteNextAsync(CancellationToken.None), async () =>
        {
            var row = await database.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == legacy.RunningJob);
            Assert.Equal("running", row.State);
            Assert.Equal(3, row.Attempts);
            Assert.Equal(clock.GetUtcNow().AddSeconds(30), row.LockedUntilUtc);
            return (row.Id, row.LeaseId);
        }, legacy, legacy.RunningJob, 3, "legacy running");
        var recoveredMessageLease = await ObserveClaimAsync(outboxGate, () => outboxWorker.PublishNextAsync(CancellationToken.None), async () =>
        {
            var row = await database.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(value => value.Id == legacy.LeasedMessage);
            Assert.Equal(3, row.RetryCount);
            Assert.Equal(clock.GetUtcNow().AddSeconds(30), row.LockedUntilUtc);
            return (row.Id, row.LeaseId);
        }, legacy, legacy.LeasedMessage, 3, "legacy leased");
        Assert.NotEqual(queuedLease, recoveredJobLease);
        Assert.NotEqual(pendingLease, recoveredMessageLease);
        Assert.False(await jobWorker.ExecuteNextAsync(CancellationToken.None));
        Assert.False(await outboxWorker.PublishNextAsync(CancellationToken.None));
        jobs = await database.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        messages = await database.OutboxMessages.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(4, jobs.Count);
        Assert.Equal(4, messages.Count);
        Assert.Equal(3, jobs.Count(row => row.State == "succeeded"));
        Assert.Equal(3, messages.Count(row => row.ProcessedAtUtc is not null));
        Assert.Single(jobs, row => row.State == "dead_letter");
        Assert.Single(messages, row => row.DeadLetteredAtUtc is not null);
        Assert.All(jobs, row => Assert.Null(row.LeaseId));
        Assert.All(messages, row => Assert.Null(row.LeaseId));
    }

    private static void AssertLeaseAddition(MigrationOperation operation, string table)
    {
        var addition = Assert.IsType<AddColumnOperation>(operation);
        Assert.Equal("platform", addition.Schema);
        Assert.Equal(table, addition.Table);
        Assert.Equal("LeaseId", addition.Name);
        Assert.Equal("uuid", addition.ColumnType);
        Assert.Equal(typeof(Guid), addition.ClrType);
        Assert.True(addition.IsNullable);
        Assert.Null(addition.DefaultValue);
        Assert.Null(addition.DefaultValueSql);
        Assert.Null(addition.ComputedColumnSql);
    }

    private static async Task<Guid> ObserveClaimAsync(ExecutionGate gate, Func<Task<bool>> execute,
        Func<Task<(Guid Id, Guid? LeaseId)>> read, LegacyRows legacy, Guid expectedId, int attempt, string label)
    {
        var pause = gate.Arm();
        var execution = execute();
        Exception? observationFailure = null;
        Guid? leaseId = null;
        try
        {
            var observed = await pause.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(expectedId, observed.Id);
            Assert.Equal(legacy.TenantId, observed.TenantId);
            Assert.Equal(legacy.CompanyId, observed.CompanyId);
            Assert.Equal(attempt, observed.Attempt);
            Assert.Equal(label, observed.Label);
            Assert.Equal("migration-" + label.Replace(' ', '-'), observed.CorrelationId);
            var claimed = await read();
            Assert.Equal(expectedId, claimed.Id);
            leaseId = Assert.IsType<Guid>(claimed.LeaseId);
            Assert.NotEqual(Guid.Empty, leaseId.GetValueOrDefault());
        }
        catch (Exception exception) { observationFailure = exception; }
        finally { pause.Release.TrySetResult(); }
        var executionFailure = await Record.ExceptionAsync(async () => Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(15))));
        if (observationFailure is not null && executionFailure is not null)
            throw new AggregateException("Claim observation and worker completion both failed.", observationFailure, executionFailure);
        if (observationFailure is not null) ExceptionDispatchInfo.Capture(observationFailure).Throw();
        if (executionFailure is not null) ExceptionDispatchInfo.Capture(executionFailure).Throw();
        return leaseId.GetValueOrDefault();
    }

    private static async Task<LegacyRows> SeedLegacyAsync(string connectionString)
    {
        var legacy = new LegacyRows(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var organization = new NpgsqlCommand("""
            INSERT INTO platform.tenants ("Id", "Name", "Slug", "TimeZoneId", "IsActive", "CreatedAtUtc")
            VALUES (@tenant, 'Legacy queue migration', @slug, 'UTC', TRUE, @created);
            INSERT INTO platform.companies ("Id", "TenantId", "Name", "Code", "BaseCurrencyCode", "TimeZoneId", "IsActive")
            VALUES (@company, @tenant, 'Legacy queue company', 'LEGACY', 'USD', 'UTC', TRUE);
            """, connection))
        {
            organization.Parameters.AddWithValue("tenant", legacy.TenantId);
            organization.Parameters.AddWithValue("company", legacy.CompanyId);
            organization.Parameters.AddWithValue("slug", "legacy-" + legacy.TenantId.ToString("N"));
            organization.Parameters.AddWithValue("created", Baseline.AddDays(-1));
            await organization.ExecuteNonQueryAsync();
        }

        foreach (var state in new[] { "queued", "running", "succeeded", "dead_letter" })
        {
            var id = state == "queued" ? legacy.QueuedJob : state == "running" ? legacy.RunningJob : Guid.NewGuid();
            var label = "legacy " + state;
            await using var job = new NpgsqlCommand("""
                INSERT INTO platform.background_jobs
                    ("Id", "TenantId", "CompanyId", "Name", "PayloadJson", "State", "Attempts", "MaxAttempts",
                     "ExecuteAfterUtc", "LockedUntilUtc", "CreatedAtUtc", "CompletedAtUtc", "CorrelationId", "IdempotencyKey", "LastError")
                VALUES (@id, @tenant, @company, @name, @payload, @state, @attempts, 5,
                        @due, @locked, @created, @completed, @correlation, @idempotency, @error);
                """, connection);
            job.Parameters.AddWithValue("id", id);
            job.Parameters.AddWithValue("tenant", legacy.TenantId);
            job.Parameters.AddWithValue("company", legacy.CompanyId);
            job.Parameters.AddWithValue("name", JobName);
            job.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new LegacyPayload(label)));
            job.Parameters.AddWithValue("state", state);
            job.Parameters.AddWithValue("attempts", state == "queued" ? 0 : state == "running" ? 2 : 1);
            job.Parameters.AddWithValue("due", Baseline.AddMinutes(-10));
            job.Parameters.AddWithValue("locked", NpgsqlDbType.TimestampTz, state == "running" ? Baseline.AddMinutes(5) : DBNull.Value);
            job.Parameters.AddWithValue("created", Baseline.AddHours(-2));
            job.Parameters.AddWithValue("completed", NpgsqlDbType.TimestampTz,
                state is "succeeded" or "dead_letter" ? Baseline.AddMinutes(-5) : DBNull.Value);
            job.Parameters.AddWithValue("correlation", "migration-" + label.Replace(' ', '-'));
            job.Parameters.AddWithValue("idempotency", "legacy-idempotency-" + state);
            job.Parameters.AddWithValue("error", NpgsqlDbType.Text, state == "dead_letter" ? "LegacyJobFailure" : DBNull.Value);
            await job.ExecuteNonQueryAsync();
        }
        foreach (var state in new[] { "pending", "leased", "processed", "dead_letter" })
        {
            var id = state == "pending" ? legacy.PendingMessage : state == "leased" ? legacy.LeasedMessage : Guid.NewGuid();
            var label = "legacy " + state;
            await using var message = new NpgsqlCommand("""
                INSERT INTO platform.outbox_messages
                    ("Id", "TenantId", "CompanyId", "OccurredAtUtc", "EventType", "PayloadJson", "CorrelationId",
                     "ProcessedAtUtc", "DeadLetteredAtUtc", "NextAttemptAtUtc", "LockedUntilUtc", "RetryCount", "LastError")
                VALUES (@id, @tenant, @company, @occurred, @event, @payload, @correlation,
                        @processed, @dead, @due, @locked, @attempts, @error);
                """, connection);
            message.Parameters.AddWithValue("id", id);
            message.Parameters.AddWithValue("tenant", legacy.TenantId);
            message.Parameters.AddWithValue("company", legacy.CompanyId);
            message.Parameters.AddWithValue("occurred", Baseline.AddHours(-2));
            message.Parameters.AddWithValue("event", EventName);
            message.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(new LegacyEvent(id, Baseline.AddHours(-2), label)));
            message.Parameters.AddWithValue("correlation", "migration-" + label.Replace(' ', '-'));
            message.Parameters.AddWithValue("processed", NpgsqlDbType.TimestampTz, state == "processed" ? Baseline.AddMinutes(-5) : DBNull.Value);
            message.Parameters.AddWithValue("dead", NpgsqlDbType.TimestampTz, state == "dead_letter" ? Baseline.AddMinutes(-4) : DBNull.Value);
            message.Parameters.AddWithValue("due", Baseline.AddMinutes(-10));
            message.Parameters.AddWithValue("locked", NpgsqlDbType.TimestampTz, state == "leased" ? Baseline.AddMinutes(5) : DBNull.Value);
            message.Parameters.AddWithValue("attempts", state == "pending" ? 0 : state == "leased" ? 2 : 1);
            message.Parameters.AddWithValue("error", NpgsqlDbType.Text, state == "dead_letter" ? "LegacyOutboxFailure" : DBNull.Value);
            await message.ExecuteNonQueryAsync();
        }
        return legacy;
    }

    private static async Task<QueueSnapshot> SnapshotAsync(string connectionString) => new(
        await ScalarAsync(connectionString, """
            SELECT jsonb_agg(to_jsonb(job_row) - 'LeaseId' ORDER BY job_row."Id")::text
            FROM platform.background_jobs AS job_row
            """),
        await ScalarAsync(connectionString, """
            SELECT jsonb_agg(to_jsonb(message_row) - 'LeaseId' ORDER BY message_row."Id")::text
            FROM platform.outbox_messages AS message_row
            """));

    private static Task<string> ExistingColumnsAsync(string connectionString) => ScalarAsync(connectionString, """
        SELECT jsonb_agg(to_jsonb(column_info) ORDER BY column_info.table_name, column_info.ordinal_position)::text
        FROM information_schema.columns AS column_info
        WHERE column_info.table_schema = 'platform' AND column_info.column_name <> 'LeaseId'
        """);

    private static async Task<string[]> LeaseColumnsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT table_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default, 'NULL')
            FROM information_schema.columns
            WHERE table_schema = 'platform' AND column_name = 'LeaseId'
            ORDER BY table_name
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns.ToArray();
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private sealed record QueueSnapshot(string Jobs, string Messages);
    private sealed record LegacyRows(Guid TenantId, Guid CompanyId, Guid QueuedJob, Guid RunningJob, Guid PendingMessage, Guid LeasedMessage);
    private sealed record LegacyPayload(string Label);
    private sealed record LegacyEvent(Guid EventId, DateTimeOffset OccurredAtUtc, string Label) : IIntegrationEvent;
    private sealed record ObservedExecution(Guid Id, Guid TenantId, Guid CompanyId, int Attempt, string CorrelationId, string Label);

    private sealed class ControlledTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan interval) => _utcNow += interval;
    }

    private sealed class ExecutionPause
    {
        public TaskCompletionSource<ObservedExecution> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ExecutionGate
    {
        private ExecutionPause? _current;
        public ExecutionPause Arm() => _current = new ExecutionPause();
        public Task WaitAsync(ObservedExecution observed, CancellationToken cancellationToken)
        {
            var pause = _current ?? throw new InvalidOperationException("The migration test must arm its execution gate before claiming work.");
            pause.Started.TrySetResult(observed);
            return pause.Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class MigrationJob(ExecutionGate gate) : IBackgroundJobHandler<LegacyPayload>
    {
        public Task ExecuteAsync(LegacyPayload payload, BackgroundJobExecutionContext context, CancellationToken cancellationToken) =>
            gate.WaitAsync(new(context.JobId, context.TenantId.Value, context.CompanyId.Value, context.Attempt, context.CorrelationId, payload.Label), cancellationToken);
    }

    private sealed class MigrationConsumer(ExecutionGate gate) : IIntegrationEventConsumer<LegacyEvent>
    {
        public Task HandleAsync(LegacyEvent integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken) =>
            gate.WaitAsync(new(context.MessageId, context.TenantId.Value, context.CompanyId.Value, context.Attempt, context.CorrelationId, integrationEvent.Label), cancellationToken);
    }
}
