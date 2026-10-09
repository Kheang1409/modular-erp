using System.Diagnostics.Metrics;
using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Infrastructure.Operations;

public sealed class QueueMetricsCollector : BackgroundService
{
    public const string MeterName = "Erp.Operations";
    private static readonly Meter Meter = new(MeterName);
    private long _outboxPending;
    private long _outboxDeadLetters;
    private long _jobsPending;
    private long _jobsDeadLetters;
    private long _oldestOutboxAgeSeconds;
    private long _oldestJobAgeSeconds;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QueueMetricsCollector> _logger;
    private readonly ObservableGauge<long>[] _gauges;

    public QueueMetricsCollector(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<QueueMetricsCollector> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _gauges =
        [
            Meter.CreateObservableGauge("erp.outbox.pending", () => Volatile.Read(ref _outboxPending)),
            Meter.CreateObservableGauge("erp.outbox.dead_letters", () => Volatile.Read(ref _outboxDeadLetters)),
            Meter.CreateObservableGauge("erp.jobs.pending", () => Volatile.Read(ref _jobsPending)),
            Meter.CreateObservableGauge("erp.jobs.dead_letters", () => Volatile.Read(ref _jobsDeadLetters)),
            Meter.CreateObservableGauge("erp.outbox.oldest_pending_age", () => Volatile.Read(ref _oldestOutboxAgeSeconds), "s"),
            Meter.CreateObservableGauge("erp.jobs.oldest_pending_age", () => Volatile.Read(ref _oldestJobAgeSeconds), "s")
        ];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), _timeProvider);
        do
        {
            try { await CollectAsync(stoppingToken); GC.KeepAlive(_gauges); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { QueueMetricsLog.CollectionFailed(_logger, exception.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task CollectAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var databaseOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<PlatformDbContext>>();
        await using var database = new PlatformDbContext(databaseOptions);
        var now = _timeProvider.GetUtcNow();
        var outboxPending = database.OutboxMessages.IgnoreQueryFilters().Where(item => item.ProcessedAtUtc == null && item.DeadLetteredAtUtc == null);
        var jobsPending = database.BackgroundJobs.IgnoreQueryFilters().Where(item => item.State == "queued" || item.State == "running");
        var oldestOutbox = await outboxPending.Select(item => (DateTimeOffset?)item.OccurredAtUtc).MinAsync(cancellationToken);
        var oldestJob = await jobsPending.Select(item => (DateTimeOffset?)item.CreatedAtUtc).MinAsync(cancellationToken);
        Volatile.Write(ref _outboxPending, await outboxPending.LongCountAsync(cancellationToken));
        Volatile.Write(ref _outboxDeadLetters, await database.OutboxMessages.IgnoreQueryFilters().LongCountAsync(item => item.DeadLetteredAtUtc != null, cancellationToken));
        Volatile.Write(ref _jobsPending, await jobsPending.LongCountAsync(cancellationToken));
        Volatile.Write(ref _jobsDeadLetters, await database.BackgroundJobs.IgnoreQueryFilters().LongCountAsync(item => item.State == "dead_letter", cancellationToken));
        Volatile.Write(ref _oldestOutboxAgeSeconds, oldestOutbox is null ? 0 : Math.Max(0, (long)(now - oldestOutbox.Value).TotalSeconds));
        Volatile.Write(ref _oldestJobAgeSeconds, oldestJob is null ? 0 : Math.Max(0, (long)(now - oldestJob.Value).TotalSeconds));
    }
}

internal static partial class QueueMetricsLog
{
    [LoggerMessage(3400, LogLevel.Warning, "Queue metric collection failed with {FailureType}.")]
    public static partial void CollectionFailed(ILogger logger, string failureType);
}
