using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Operations;
using Erp.Abstractions.Persistence;
using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Operations;

public sealed class QueueOperationsService(
    PlatformDbContext database,
    ICompanyContext companyContext,
    IClock clock,
    IAuditLogWriter audit,
    IBusinessTransactionCoordinator transactions) : IQueueOperationsService
{
    public async Task<QueueOperationsSnapshot> GetAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var companyId = RequireCompany();
        var outbox = await database.OutboxMessages.AsNoTracking()
            .Where(item => item.CompanyId == companyId && item.DeadLetteredAtUtc != null)
            .OrderByDescending(item => item.DeadLetteredAtUtc)
            .Take(limit)
            .Select(item => new DeadLetterOutboxItem(item.Id, item.EventType, item.RetryCount, item.OccurredAtUtc, item.CorrelationId))
            .ToListAsync(cancellationToken);
        var jobs = await database.BackgroundJobs.AsNoTracking()
            .Where(item => item.CompanyId == companyId && item.State == "dead_letter")
            .OrderByDescending(item => item.CompletedAtUtc)
            .Take(limit)
            .Select(item => new DeadLetterJobItem(item.Id, item.Name, item.Attempts, item.MaxAttempts, item.CreatedAtUtc, item.CorrelationId))
            .ToListAsync(cancellationToken);
        return new(clock.UtcNow, outbox, jobs);
    }

    public async Task<bool> ReplayOutboxAsync(Guid id, CancellationToken cancellationToken)
    {
        var companyId = RequireCompany();
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var now = clock.UtcNow;
        var affected = await database.OutboxMessages
            .Where(value => value.Id == id && value.CompanyId == companyId && value.DeadLetteredAtUtc != null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(value => value.DeadLetteredAtUtc, (DateTimeOffset?)null)
                .SetProperty(value => value.ProcessedAtUtc, (DateTimeOffset?)null)
                .SetProperty(value => value.NextAttemptAtUtc, now)
                .SetProperty(value => value.LeaseId, (Guid?)null)
                .SetProperty(value => value.LockedUntilUtc, (DateTimeOffset?)null)
                .SetProperty(value => value.RetryCount, 0)
                .SetProperty(value => value.LastError, (string?)null), cancellationToken);
        if (affected == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        audit.Append(new("platform.outbox.replayed", "OutboxMessage", id.ToString(), null, null, null));
        await audit.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReplayJobAsync(Guid id, CancellationToken cancellationToken)
    {
        var companyId = RequireCompany();
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var now = clock.UtcNow;
        var affected = await database.BackgroundJobs
            .Where(value => value.Id == id && value.CompanyId == companyId && value.State == "dead_letter")
            .ExecuteUpdateAsync(update => update
                .SetProperty(value => value.State, "queued")
                .SetProperty(value => value.Attempts, 0)
                .SetProperty(value => value.ExecuteAfterUtc, now)
                .SetProperty(value => value.LeaseId, (Guid?)null)
                .SetProperty(value => value.LockedUntilUtc, (DateTimeOffset?)null)
                .SetProperty(value => value.CompletedAtUtc, (DateTimeOffset?)null)
                .SetProperty(value => value.LastError, (string?)null), cancellationToken);
        if (affected == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        audit.Append(new("platform.background_job.replayed", "BackgroundJob", id.ToString(), null, null, null));
        await audit.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private Guid RequireCompany() => companyContext.CompanyId?.Value
        ?? throw new InvalidOperationException("An active company is required.");
}
