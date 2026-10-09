using System.Text.Json;
using System.Security.Claims;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Auditing;

public sealed class AuthenticationAuditService(
    DbContextOptions<PlatformDbContext> databaseOptions,
    IHttpContextAccessor httpContextAccessor,
    IClock clock) : IAuthenticationAuditService
{
    public Task<bool> RecordSignInAsync(
        Guid userId,
        bool succeeded,
        bool lockedOut,
        CancellationToken cancellationToken) =>
        RecordAsync(
            userId,
            succeeded ? "authentication.sign_in.succeeded" : "authentication.sign_in.failed",
            succeeded ? null : JsonSerializer.Serialize(new { Reason = lockedOut ? "locked_out" : "invalid_credentials" }),
            cancellationToken);

    public Task<bool> RecordLogoutAsync(Guid userId, CancellationToken cancellationToken) =>
        RecordAsync(userId, "authentication.logout", null, cancellationToken);

    private async Task<bool> RecordAsync(
        Guid userId,
        string action,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        await using var database = new PlatformDbContext(databaseOptions);
        var httpContext = httpContextAccessor.HttpContext;
        var principal = httpContext?.User;
        var preferredCompanyId = Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var principalUserId)
            && principalUserId == userId
            && Guid.TryParse(principal.FindFirstValue("company_id"), out var principalCompanyId)
                ? principalCompanyId : Guid.Empty;
        var membership = await (
            from candidate in database.UserCompanyMemberships.IgnoreQueryFilters()
            join company in database.Companies.IgnoreQueryFilters()
                on new { candidate.TenantId, Id = candidate.CompanyId } equals new { company.TenantId, company.Id }
            join tenant in database.Tenants on candidate.TenantId equals tenant.Id
            where candidate.UserId == userId && company.IsActive && tenant.IsActive
            orderby candidate.CompanyId == preferredCompanyId descending,
                candidate.IsDefault descending, company.Name, company.Id
            select new { candidate.TenantId, candidate.CompanyId }).FirstOrDefaultAsync(cancellationToken);
        if (membership is null) return false;

        database.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = membership.TenantId,
            CompanyId = membership.CompanyId,
            UserId = userId,
            Action = action,
            EntityType = "User",
            EntityId = userId.ToString(),
            TimestampUtc = clock.UtcNow,
            CorrelationId = httpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N"),
            IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
            MetadataJson = metadataJson
        });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }
}
