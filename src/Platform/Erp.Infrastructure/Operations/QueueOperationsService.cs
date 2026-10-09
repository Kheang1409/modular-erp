using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Operations;
using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Operations;

public sealed class QueueOperationsService(
    PlatformDbContext database,
    ICompanyContext companyContext,
    IClock clock,
    IAuditLogWriter audit) : IQueueOperationsService
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
        var item = await database.OutboxMessages.SingleOrDefaultAsync(
            value => value.Id == id && value.CompanyId == companyId && value.DeadLetteredAtUtc != null, cancellationToken);
        if (item is null) return false;
        item.DeadLetteredAtUtc = null;
        item.ProcessedAtUtc = null;
        item.NextAttemptAtUtc = clock.UtcNow;
        item.LockedUntilUtc = null;
        item.RetryCount = 0;
        item.LastError = null;
        audit.Append(new("platform.outbox.replayed", "OutboxMessage", item.Id.ToString(), null, null, null));
        await audit.FlushAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReplayJobAsync(Guid id, CancellationToken cancellationToken)
    {
        var companyId = RequireCompany();
        var item = await database.BackgroundJobs.SingleOrDefaultAsync(
            value => value.Id == id && value.CompanyId == companyId && value.State == "dead_letter", cancellationToken);
        if (item is null) return false;
        item.State = "queued";
        item.Attempts = 0;
        item.ExecuteAfterUtc = clock.UtcNow;
        item.LockedUntilUtc = null;
        item.CompletedAtUtc = null;
        item.LastError = null;
        audit.Append(new("platform.background_job.replayed", "BackgroundJob", item.Id.ToString(), null, null, null));
        await audit.FlushAsync(cancellationToken);
        return true;
    }

    private Guid RequireCompany() => companyContext.CompanyId?.Value
        ?? throw new InvalidOperationException("An active company is required.");
}
