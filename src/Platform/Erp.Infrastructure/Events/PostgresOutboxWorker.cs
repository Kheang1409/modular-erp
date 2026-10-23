using System.Reflection;
using System.Text.Json;
using Erp.Abstractions.Events;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Security;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Events;

public sealed class PostgresOutboxWorker(
    IServiceScopeFactory scopeFactory,
    IIntegrationEventRegistry registry,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<PostgresOutboxWorker> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 60)), timeProvider);
        do
        {
            try { while (await PublishNextAsync(stoppingToken)) { } }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { OutboxLog.PollFailed(logger, exception); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<bool> PublishNextAsync(CancellationToken cancellationToken)
    {
        OutboxMessage? message;
        await using (var claimScope = scopeFactory.CreateAsyncScope())
        {
            var databaseOptions = claimScope.ServiceProvider.GetRequiredService<DbContextOptions<PlatformDbContext>>();
            await using var database = new PlatformDbContext(databaseOptions);
            var now = timeProvider.GetUtcNow();
            var lockedUntil = now.AddSeconds(Math.Clamp(_options.LeaseSeconds, 10, 3600));
            var leaseId = Guid.NewGuid();
            var claimed = await database.OutboxMessages.FromSqlInterpolated($$"""
                UPDATE platform.outbox_messages
                SET "RetryCount" = "RetryCount" + 1, "LockedUntilUtc" = {{lockedUntil}}, "LeaseId" = {{leaseId}}
                WHERE "Id" = (
                    SELECT "Id" FROM platform.outbox_messages
                    WHERE "ProcessedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NULL
                      AND "NextAttemptAtUtc" <= {{now}}
                      AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" < {{now}})
                    ORDER BY "OccurredAtUtc", "Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
                RETURNING *
                """).IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
            message = claimed.SingleOrDefault();
        }
        if (message is null) return false;

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["OutboxMessageId"] = message.Id,
            ["EventType"] = message.EventType,
            ["TenantId"] = message.TenantId,
            ["CompanyId"] = message.CompanyId,
            ["CorrelationId"] = message.CorrelationId
        });
        try
        {
            var definition = registry.Events.SingleOrDefault(x => string.Equals(x.EventType, message.EventType, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No integration event is registered for '{message.EventType}'.");
            await using var executionScope = scopeFactory.CreateAsyncScope();
            executionScope.ServiceProvider.GetRequiredService<ExecutionOrganizationContext>()
                .Set(new TenantId(message.TenantId), new CompanyId(message.CompanyId));
            var payload = JsonSerializer.Deserialize(message.PayloadJson, definition.PayloadType)
                ?? throw new InvalidDataException("The outbox payload is null.");
            var consumerType = typeof(IIntegrationEventConsumer<>).MakeGenericType(definition.PayloadType);
            var consumers = executionScope.ServiceProvider.GetServices(consumerType).ToArray();
            var context = new IntegrationEventContext(
                message.Id, new TenantId(message.TenantId), new CompanyId(message.CompanyId),
                message.RetryCount, message.CorrelationId);
            var method = consumerType.GetMethod(nameof(IIntegrationEventConsumer<IIntegrationEvent>.HandleAsync))!;
            foreach (var consumer in consumers)
            {
                var task = method.Invoke(consumer, [payload, context, cancellationToken]) as Task
                    ?? throw new InvalidOperationException("The integration event consumer did not return a task.");
                await task;
            }
            if (await UpdateResultAsync(message, true, null, cancellationToken))
                OutboxLog.Published(logger, message.EventType, message.Id, consumers.Length);
            else
                OutboxLog.LeaseLost(logger, message.EventType, message.Id, message.RetryCount);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            await FailAsync(message, exception.InnerException, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await FailAsync(message, exception, cancellationToken);
        }
        return true;
    }

    private async Task FailAsync(OutboxMessage message, Exception exception, CancellationToken cancellationToken)
    {
        if (await UpdateResultAsync(message, false, exception, cancellationToken))
            OutboxLog.Failed(logger, message.EventType, message.Id, message.RetryCount, exception.GetType().Name);
        else
            OutboxLog.LeaseLost(logger, message.EventType, message.Id, message.RetryCount);
    }

    private async Task<bool> UpdateResultAsync(OutboxMessage message, bool succeeded, Exception? exception, CancellationToken cancellationToken)
    {
        if (message.LeaseId is null) return false;
        await using var scope = scopeFactory.CreateAsyncScope();
        var databaseOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<PlatformDbContext>>();
        await using var database = new PlatformDbContext(databaseOptions);
        var now = timeProvider.GetUtcNow();
        var deadLetter = !succeeded && message.RetryCount >= Math.Clamp(_options.MaxAttempts, 1, 100);
        var error = exception?.GetType().Name;
        // Expiry permits another claim; only a replacement/replay invalidates ownership.
        // Attempt counters and timestamps can repeat, so neither identifies the claim.
        var affected = await database.OutboxMessages.IgnoreQueryFilters()
            .Where(x => x.Id == message.Id && x.TenantId == message.TenantId && x.CompanyId == message.CompanyId
                && x.LeaseId == message.LeaseId && x.ProcessedAtUtc == null && x.DeadLetteredAtUtc == null)
            .ExecuteUpdateAsync(update => update
            .SetProperty(x => x.ProcessedAtUtc, succeeded ? now : null)
            .SetProperty(x => x.DeadLetteredAtUtc, deadLetter ? now : null)
            .SetProperty(x => x.NextAttemptAtUtc, succeeded || deadLetter ? message.NextAttemptAtUtc : now.AddSeconds(Math.Min(300, Math.Pow(2, message.RetryCount - 1))))
            .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null)
            .SetProperty(x => x.LeaseId, (Guid?)null)
            .SetProperty(x => x.LastError, error), cancellationToken);
        return affected == 1;
    }
}

internal static partial class OutboxLog
{
    [LoggerMessage(3300, LogLevel.Error, "Transactional outbox polling failed.")]
    public static partial void PollFailed(ILogger logger, Exception exception);

    [LoggerMessage(3301, LogLevel.Information, "Integration event {EventType} ({MessageId}) published to {ConsumerCount} consumers.")]
    public static partial void Published(ILogger logger, string eventType, Guid messageId, int consumerCount);

    [LoggerMessage(3302, LogLevel.Error, "Integration event {EventType} ({MessageId}) failed on attempt {Attempt} with {FailureType}.")]
    public static partial void Failed(ILogger logger, string eventType, Guid messageId, int attempt, string failureType);

    [LoggerMessage(3303, LogLevel.Information, "Integration event {EventType} ({MessageId}) result ignored because attempt {Attempt} no longer owns its lease.")]
    public static partial void LeaseLost(ILogger logger, string eventType, Guid messageId, int attempt);
}
