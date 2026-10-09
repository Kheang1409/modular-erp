using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Operations;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Infrastructure.Operations;

public sealed class DataMaintenanceService(
    PlatformDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IClock clock,
    IAuditLogWriter audit,
    IDataProtectionProvider protection) : IDataMaintenanceService
{
    private readonly IDataProtector _protector = protection.CreateProtector("Erp.Platform.DataMaintenance.Preview.v1");

    public async Task<DataMaintenancePreview> PreviewAsync(int retentionDays, CancellationToken cancellationToken)
    {
        if (retentionDays is < 30 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Keep between 30 and 3,650 days of completed work.");

        var (tenantId, companyId) = RequireScope();
        var company = await RequireCompanyAsync(tenantId, companyId, cancellationToken);
        var now = clock.UtcNow;
        var cutoff = now.AddDays(-retentionDays);
        var jobs = await Jobs(tenantId, companyId, cutoff).Select(item => item.Id).ToListAsync(cancellationToken);
        var outbox = await Outbox(tenantId, companyId, cutoff).Select(item => item.Id).ToListAsync(cancellationToken);
        var snapshot = new PreviewSnapshot(tenantId, companyId, company.Code, retentionDays, cutoff,
            now.AddMinutes(30), jobs.Count, outbox.Count, Digest(jobs), Digest(outbox));

        return new(companyId, company.Name, company.Code, retentionDays, cutoff, snapshot.ExpiresAtUtc,
            jobs.Count, outbox.Count, Confirmation(company.Code), _protector.Protect(JsonSerializer.Serialize(snapshot)));
    }

    public async Task<DataMaintenanceResult> ExecuteAsync(ExecuteDataMaintenanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (tenantId, companyId) = RequireScope();
        var snapshot = ReadSnapshot(request.PreviewToken);
        if (snapshot.TenantId != tenantId || snapshot.CompanyId != companyId)
            throw new InvalidOperationException("The company changed. Generate a new preview for the current company.");
        if (snapshot.ExpiresAtUtc <= clock.UtcNow || snapshot.RetentionDays is < 30 or > 3650
            || snapshot.CutoffUtc > clock.UtcNow.AddDays(-30))
            throw new InvalidOperationException("This preview has expired. Generate a new preview before cleaning completed work.");
        if (!string.Equals(request.Confirmation, Confirmation(snapshot.CompanyCode), StringComparison.Ordinal))
            throw new ArgumentException($"Type {Confirmation(snapshot.CompanyCode)} exactly to confirm this cleanup.", nameof(request));

        var existingAudits = database.ChangeTracker.Entries<AuditLog>().Select(entry => entry.Entity).ToHashSet();
        var committed = false;
        try
        {
            // The snapshot and deletes share a serializable transaction. Any changed selection requires a new preview.
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var company = await RequireCompanyAsync(tenantId, companyId, cancellationToken);
            if (!string.Equals(company.Code, snapshot.CompanyCode, StringComparison.Ordinal))
                throw new InvalidOperationException("The company details changed. Generate a new preview.");
            var jobs = Jobs(tenantId, companyId, snapshot.CutoffUtc);
            var outbox = Outbox(tenantId, companyId, snapshot.CutoffUtc);
            var jobIds = await jobs.Select(item => item.Id).ToListAsync(cancellationToken);
            var outboxIds = await outbox.Select(item => item.Id).ToListAsync(cancellationToken);
            if (jobIds.Count != snapshot.JobCount || outboxIds.Count != snapshot.OutboxCount
                || Digest(jobIds) != snapshot.JobDigest || Digest(outboxIds) != snapshot.OutboxDigest)
                throw new InvalidOperationException("Completed work changed since the preview. Generate a new preview to review the current counts.");

            var deletedJobs = await jobs.ExecuteDeleteAsync(cancellationToken);
            var deletedOutbox = await outbox.ExecuteDeleteAsync(cancellationToken);
            if (deletedJobs != jobIds.Count || deletedOutbox != outboxIds.Count)
                throw new InvalidOperationException("Completed work changed during cleanup. No changes were saved; generate a new preview.");

            var result = new DataMaintenanceResult(companyId, snapshot.CutoffUtc, deletedJobs, deletedOutbox);
            audit.Append(new("platform.data_maintenance.completed", "Company", companyId.ToString(),
                JsonSerializer.Serialize(new { snapshot.RetentionDays, snapshot.CutoffUtc, company.Code }),
                JsonSerializer.Serialize(new { SuccessfulJobCount = jobIds.Count, ProcessedOutboxCount = outboxIds.Count }),
                JsonSerializer.Serialize(result)));
            await audit.FlushAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return result;
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            throw new InvalidOperationException("Completed work changed during cleanup. No changes were saved. Generate a new preview and try again.", exception);
        }
        finally
        {
            if (!committed)
            {
                // A failed save must not leave a cleanup audit queued for the component's next action.
                foreach (var entry in database.ChangeTracker.Entries<AuditLog>()
                    .Where(entry => !existingAudits.Contains(entry.Entity)
                        && entry.Entity.Action == "platform.data_maintenance.completed"
                        && entry.Entity.EntityId == companyId.ToString()).ToArray())
                    entry.State = EntityState.Detached;
            }
        }
    }

    private IQueryable<BackgroundJob> Jobs(Guid tenantId, Guid companyId, DateTimeOffset cutoff) =>
        database.BackgroundJobs.AsNoTracking().Where(item => item.TenantId == tenantId && item.CompanyId == companyId
            && item.State == "succeeded" && item.CompletedAtUtc != null && item.CompletedAtUtc < cutoff
            && item.IdempotencyKey == null && item.LockedUntilUtc == null && item.LastError == null);

    private IQueryable<OutboxMessage> Outbox(Guid tenantId, Guid companyId, DateTimeOffset cutoff) =>
        database.OutboxMessages.AsNoTracking().Where(item => item.TenantId == tenantId && item.CompanyId == companyId
            && item.ProcessedAtUtc != null && item.ProcessedAtUtc < cutoff && item.DeadLetteredAtUtc == null
            && item.LockedUntilUtc == null && item.LastError == null);

    private (Guid TenantId, Guid CompanyId) RequireScope() =>
        (tenantContext.TenantId?.Value ?? throw new InvalidOperationException("An active organization is required."),
         companyContext.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required."));

    private async Task<Company> RequireCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await database.Companies.AsNoTracking().SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.Id == companyId && item.IsActive, cancellationToken)
        ?? throw new InvalidOperationException("The current company is unavailable. Select an active company.");

    private PreviewSnapshot ReadSnapshot(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16384)
            throw new ArgumentException("A valid cleanup preview is required.", nameof(token));
        try
        {
            return JsonSerializer.Deserialize<PreviewSnapshot>(_protector.Unprotect(token))
                ?? throw new ArgumentException("A valid cleanup preview is required.", nameof(token));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw new ArgumentException("This preview is invalid. Generate a new preview.", nameof(token));
        }
    }

    private static string Confirmation(string companyCode) => $"CLEAN {companyCode}";

    private static bool IsConcurrencyConflict(Exception exception) => exception is
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }
        or DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } };

    private static string Digest(IEnumerable<Guid> ids) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', ids.Order().Select(id => id.ToString("N"))))));

    private sealed record PreviewSnapshot(Guid TenantId, Guid CompanyId, string CompanyCode, int RetentionDays,
        DateTimeOffset CutoffUtc, DateTimeOffset ExpiresAtUtc, int JobCount, int OutboxCount, string JobDigest, string OutboxDigest);
}
