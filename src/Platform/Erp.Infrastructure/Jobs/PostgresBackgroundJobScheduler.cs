using System.Text.Json;
using Erp.Abstractions.Context;
using Erp.Abstractions.Jobs;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Jobs;

public sealed class PostgresBackgroundJobScheduler(
    PlatformDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IBackgroundJobRegistry registry,
    IHttpContextAccessor httpContextAccessor,
    IClock clock) : IBackgroundJobScheduler
{
    public async Task<Guid> EnqueueAsync<TPayload>(
        string jobName,
        TPayload payload,
        DateTimeOffset? executeAfterUtc = null,
        string? idempotencyKey = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
        var companyId = companyContext.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
        var definition = registry.Jobs.SingleOrDefault(x => string.Equals(x.Name, jobName, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Background job '{jobName}' is not registered.", nameof(jobName));
        if (definition.PayloadType != typeof(TPayload))
            throw new ArgumentException($"Background job '{jobName}' requires payload type '{definition.PayloadType.Name}'.", nameof(payload));
        if (maxAttempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Maximum attempts must be between 1 and 20.");
        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (idempotencyKey?.Length > 200) throw new ArgumentException("Idempotency key cannot exceed 200 characters.", nameof(idempotencyKey));
        if (idempotencyKey is not null)
        {
            var existingId = await database.BackgroundJobs
                .Where(x => x.CompanyId == companyId && x.Name == jobName && x.IdempotencyKey == idempotencyKey)
                .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
            if (existingId is not null) return existingId.Value;
        }

        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanyId = companyId,
            Name = jobName,
            PayloadJson = JsonSerializer.Serialize(payload),
            State = "queued",
            Attempts = 0,
            MaxAttempts = maxAttempts,
            ExecuteAfterUtc = executeAfterUtc ?? clock.UtcNow,
            CreatedAtUtc = clock.UtcNow,
            CorrelationId = httpContextAccessor.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N"),
            IdempotencyKey = idempotencyKey
        };
        database.BackgroundJobs.Add(job);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (idempotencyKey is not null)
        {
            database.Entry(job).State = EntityState.Detached;
            var existingId = await database.BackgroundJobs
                .Where(x => x.CompanyId == companyId && x.Name == jobName && x.IdempotencyKey == idempotencyKey)
                .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
            if (existingId is not null) return existingId.Value;
            throw new InvalidOperationException("The background job could not be scheduled.", exception);
        }
        return job.Id;
    }
}
