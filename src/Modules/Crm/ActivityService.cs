using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Organization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Crm;

public interface IActivityService
{
    Task<IReadOnlyList<ActivityDto>> ListAsync(Guid? leadId, Guid? opportunityId, CancellationToken token);
    Task<ActivityDto> CreateAsync(ActivityRequest request, CancellationToken token);
    Task<ActivityDto> SetCompletedAsync(Guid id, string concurrencyStamp, bool completed, CancellationToken token);
}

public sealed class ActivityService(CrmDbContext database, ITenantContext tenant, ICompanyContext company,
    ICompanyUserDirectory users, IClock clock, IAuditLogWriter audit, IHttpContextAccessor http) : IActivityService
{
    public async Task<IReadOnlyList<ActivityDto>> ListAsync(Guid? leadId, Guid? opportunityId, CancellationToken token)
    { ValidateParent(leadId, opportunityId); return await database.Activities.AsNoTracking().Where(x => x.LeadId == leadId && x.OpportunityId == opportunityId).OrderByDescending(x => x.CreatedAtUtc).Select(x => Dto(x)).ToListAsync(token); }
    public async Task<ActivityDto> CreateAsync(ActivityRequest request, CancellationToken token)
    {
        ValidateParent(request.LeadId, request.OpportunityId); var subject = string.IsNullOrWhiteSpace(request.Subject) ? null : request.Subject.Trim();
        if (subject is null || subject.Length > 200) throw Validation("subject", "Subject is required and cannot exceed 200 characters.");
        if (request.Details?.Length > 4000) throw Validation("details", "Details cannot exceed 4,000 characters.");
        if (request.AssignedUserId is not null && await users.FindAsync(request.AssignedUserId.Value, token) is null) throw Validation("assignedUserId", "Assigned user is not a member of the active company.");
        if (request.LeadId is not null && !await database.Leads.AnyAsync(x => x.Id == request.LeadId, token)) throw Validation("leadId", "Lead was not found.");
        if (request.OpportunityId is not null && !await database.Opportunities.AnyAsync(x => x.Id == request.OpportunityId, token)) throw Validation("opportunityId", "Opportunity was not found.");
        var activity = new CrmActivity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant(),
            CompanyId = Company(),
            LeadId = request.LeadId,
            OpportunityId = request.OpportunityId,
            Type = request.Type,
            Subject = subject,
            Details = string.IsNullOrWhiteSpace(request.Details) ? null : request.Details.Trim(),
            DueAtUtc = request.DueAtUtc,
            AssignedUserId = request.AssignedUserId,
            CreatedAtUtc = clock.UtcNow,
            CreatedBy = User(),
            ConcurrencyStamp = Stamp()
        };
        database.Activities.Add(activity); await database.SaveChangesAsync(token); await WriteAudit("crm.activity.created", activity, token); return Dto(activity);
    }
    public async Task<ActivityDto> SetCompletedAsync(Guid id, string concurrencyStamp, bool completed, CancellationToken token)
    {
        var activity = await database.Activities.SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
        if (!StringComparer.Ordinal.Equals(activity.ConcurrencyStamp, concurrencyStamp)) throw new LeadConcurrencyException();
        activity.CompletedAtUtc = completed ? clock.UtcNow : null; activity.ConcurrencyStamp = Stamp();
        try { await database.SaveChangesAsync(token); } catch (DbUpdateConcurrencyException) { throw new LeadConcurrencyException(); }
        await WriteAudit(completed ? "crm.activity.completed" : "crm.activity.reopened", activity, token); return Dto(activity);
    }
    private async Task WriteAudit(string action, CrmActivity value, CancellationToken token) { audit.Append(new(action, "CrmActivity", value.Id.ToString(), null, null, JsonSerializer.Serialize(Dto(value)))); await audit.FlushAsync(token); }
    internal static void ValidateParent(Guid? leadId, Guid? opportunityId) { if (leadId.HasValue == opportunityId.HasValue) throw Validation("parent", "Exactly one leadId or opportunityId is required."); }
    private Guid Tenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); private Guid Company() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private Guid? User() => Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null; private static string Stamp() => Guid.NewGuid().ToString("N");
    private static ActivityDto Dto(CrmActivity x) => new(x.Id, x.LeadId, x.OpportunityId, x.Type, x.Subject, x.Details, x.DueAtUtc, x.CompletedAtUtc, x.AssignedUserId, x.CreatedAtUtc, x.ConcurrencyStamp);
    private static LeadValidationException Validation(string key, string message) => new(new Dictionary<string, string[]> { [key] = [message] });
}
