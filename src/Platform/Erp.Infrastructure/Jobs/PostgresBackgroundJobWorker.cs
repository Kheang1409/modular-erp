using System.Reflection;
using System.Text.Json;
using Erp.Abstractions.Context;
using Erp.Abstractions.Jobs;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Security;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Jobs;

public sealed class PostgresBackgroundJobWorker(
    IServiceScopeFactory scopeFactory,
    IBackgroundJobRegistry registry,
    IOptions<BackgroundJobOptions> options,
    TimeProvider timeProvider,
    ILogger<PostgresBackgroundJobWorker> logger) : BackgroundService
{
    private readonly BackgroundJobOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        var pollInterval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 60));
        using var timer = new PeriodicTimer(pollInterval, timeProvider);
        do
        {
            try
            {
                while (await ExecuteNextAsync(stoppingToken)) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { JobLog.PollFailed(logger, exception); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<bool> ExecuteNextAsync(CancellationToken cancellationToken)
    {
        BackgroundJob? job;
        await using (var claimScope = scopeFactory.CreateAsyncScope())
        {
            var databaseOptions = claimScope.ServiceProvider.GetRequiredService<DbContextOptions<PlatformDbContext>>();
            await using var database = new PlatformDbContext(databaseOptions);
            var now = timeProvider.GetUtcNow();
            var lockedUntil = now.AddSeconds(Math.Clamp(_options.LeaseSeconds, 10, 3600));
            var claimed = await database.BackgroundJobs.FromSqlInterpolated($$"""
                UPDATE platform.background_jobs
                SET "State" = 'running', "Attempts" = "Attempts" + 1, "LockedUntilUtc" = {{lockedUntil}}
                WHERE "Id" = (
                    SELECT "Id" FROM platform.background_jobs
                    WHERE ("State" = 'queued' AND "ExecuteAfterUtc" <= {{now}})
                       OR ("State" = 'running' AND "LockedUntilUtc" < {{now}})
                    ORDER BY "ExecuteAfterUtc", "CreatedAtUtc"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
                RETURNING *
                """).IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
            job = claimed.SingleOrDefault();
        }
        if (job is null) return false;

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = job.Id,
            ["JobName"] = job.Name,
            ["TenantId"] = job.TenantId,
            ["CompanyId"] = job.CompanyId,
            ["CorrelationId"] = job.CorrelationId
        });
        try
        {
            var definition = registry.Jobs.SingleOrDefault(x => string.Equals(x.Name, job.Name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No handler is registered for background job '{job.Name}'.");
            await using var executionScope = scopeFactory.CreateAsyncScope();
            executionScope.ServiceProvider.GetRequiredService<ExecutionOrganizationContext>()
                .Set(new TenantId(job.TenantId), new CompanyId(job.CompanyId));
            var handler = executionScope.ServiceProvider.GetRequiredService(definition.HandlerType);
            var payload = JsonSerializer.Deserialize(job.PayloadJson, definition.PayloadType)
                ?? throw new InvalidDataException("The background job payload is null.");
            var method = definition.HandlerType.GetMethod(
                nameof(IBackgroundJobHandler<object>.ExecuteAsync),
                BindingFlags.Instance | BindingFlags.Public,
                [definition.PayloadType, typeof(BackgroundJobExecutionContext), typeof(CancellationToken)])
                ?? throw new InvalidOperationException($"Handler '{definition.HandlerType.Name}' has no executable job method.");
            var context = new BackgroundJobExecutionContext(
                job.Id, new TenantId(job.TenantId), new CompanyId(job.CompanyId), job.Attempts, job.CorrelationId);
            var task = method.Invoke(handler, [payload, context, cancellationToken]) as Task
                ?? throw new InvalidOperationException("The background job handler did not return a task.");
            await task;
            await UpdateResultAsync(job, succeeded: true, null, cancellationToken);
            JobLog.Completed(logger, job.Name, job.Id, job.Attempts);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            await FailAsync(job, exception.InnerException, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await FailAsync(job, exception, cancellationToken);
        }
        return true;
    }

    private async Task FailAsync(BackgroundJob job, Exception exception, CancellationToken cancellationToken)
    {
        await UpdateResultAsync(job, succeeded: false, exception, cancellationToken);
        JobLog.Failed(logger, job.Name, job.Id, job.Attempts, exception.GetType().Name);
    }

    private async Task UpdateResultAsync(BackgroundJob job, bool succeeded, Exception? exception, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var databaseOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<PlatformDbContext>>();
        await using var database = new PlatformDbContext(databaseOptions);
        var now = timeProvider.GetUtcNow();
        var terminal = succeeded || job.Attempts >= job.MaxAttempts;
        var state = succeeded ? "succeeded" : terminal ? "dead_letter" : "queued";
        var retryAt = succeeded || terminal ? job.ExecuteAfterUtc : now.AddSeconds(Math.Min(300, Math.Pow(2, job.Attempts - 1)));
        var error = exception?.GetType().Name;
        await database.BackgroundJobs.IgnoreQueryFilters().Where(x => x.Id == job.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, state)
            .SetProperty(x => x.ExecuteAfterUtc, retryAt)
            .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null)
            .SetProperty(x => x.CompletedAtUtc, terminal ? now : null)
            .SetProperty(x => x.LastError, error), cancellationToken);
    }
}

internal static partial class JobLog
{
    [LoggerMessage(3100, LogLevel.Error, "Background-job polling failed.")]
    public static partial void PollFailed(ILogger logger, Exception exception);

    [LoggerMessage(3101, LogLevel.Information, "Background job {JobName} ({JobId}) completed on attempt {Attempt}.")]
    public static partial void Completed(ILogger logger, string jobName, Guid jobId, int attempt);

    [LoggerMessage(3102, LogLevel.Error, "Background job {JobName} ({JobId}) failed on attempt {Attempt} with {FailureType}.")]
    public static partial void Failed(ILogger logger, string jobName, Guid jobId, int attempt, string failureType);
}
