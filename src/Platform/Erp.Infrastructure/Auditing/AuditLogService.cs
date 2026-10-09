using System.Security.Claims;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Core;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Auditing;

public sealed class AuditLogService(
    PlatformDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IHttpContextAccessor httpContextAccessor,
    IClock clock) : IAuditLogWriter, IAuditLogReader
{
    public void Append(AuditWriteEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var tenantId = tenantContext.TenantId?.Value
            ?? throw new InvalidOperationException("An active tenant is required to write an audit entry.");
        var httpContext = httpContextAccessor.HttpContext;
        var userId = Guid.TryParse(httpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
            ? parsedUserId
            : (Guid?)null;

        database.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanyId = companyContext.CompanyId?.Value,
            UserId = userId,
            Action = ValidateRequired(entry.Action, nameof(entry.Action), 100),
            EntityType = ValidateRequired(entry.EntityType, nameof(entry.EntityType), 200),
            EntityId = ValidateRequired(entry.EntityId, nameof(entry.EntityId), 200),
            TimestampUtc = clock.UtcNow,
            CorrelationId = httpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N"),
            IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
            MetadataJson = entry.MetadataJson,
            BeforeJson = entry.BeforeJson,
            AfterJson = entry.AfterJson
        });
    }

    public Task FlushAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);

    public async Task<AuditPage> ReadAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        _ = tenantContext.TenantId?.Value
            ?? throw new InvalidOperationException("An active tenant is required to read audit entries.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");

        var query = database.AuditLogs.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.TimestampUtc)
            .ThenByDescending(x => x.Id)
            .Skip(offset)
            .Take(limit)
            .Select(x => new AuditEntry(
                x.Id, x.CompanyId, x.UserId, x.Action, x.EntityType, x.EntityId,
                x.TimestampUtc, x.CorrelationId, x.IpAddress, x.MetadataJson, x.BeforeJson, x.AfterJson))
            .ToListAsync(cancellationToken);
        return new(offset, limit, total, items);
    }

    private static string ValidateRequired(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException($"Value is required and cannot exceed {maximumLength} characters.", parameterName);
        }
        return value;
    }
}
