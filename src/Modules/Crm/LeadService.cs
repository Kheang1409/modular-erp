using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Organization;
using Erp.Modules.Contacts.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Crm;

public interface ILeadService
{
    Task<LeadPage> SearchAsync(LeadQuery query, CancellationToken cancellationToken);
    Task<LeadDto?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<LeadDto> CreateAsync(LeadRequest request, CancellationToken cancellationToken);
    Task<LeadDto> UpdateAsync(Guid id, LeadRequest request, CancellationToken cancellationToken);
    Task<LeadDto> TransitionAsync(Guid id, LeadState state, LeadTransitionRequest request, CancellationToken cancellationToken);
    Task<LeadDto> AssignAsync(Guid id, LeadAssignmentRequest request, CancellationToken cancellationToken);
    Task<OpportunityDto> ConvertAsync(Guid id, ConvertLeadRequest request, CancellationToken cancellationToken);
    Task<LeadDto> SetArchivedAsync(Guid id, string concurrencyStamp, bool archived, CancellationToken cancellationToken);
}

public sealed class LeadService(CrmDbContext database, ITenantContext tenant, ICompanyContext company,
    IContactDirectory contacts, ICompanyUserDirectory users, IClock clock, IAuditLogWriter audit, IHttpContextAccessor http) : ILeadService
{
    public async Task<LeadPage> SearchAsync(LeadQuery query, CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        var source = database.Leads.AsNoTracking();
        if (!query.IncludeArchived) source = source.Where(x => !x.IsArchived);
        if (query.State is not null) source = source.Where(x => x.State == query.State);
        if (query.AssignedUserId is not null) source = source.Where(x => x.AssignedUserId == query.AssignedUserId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(x => EF.Functions.ILike(x.Title, $"%{search}%") ||
                (x.Email != null && EF.Functions.ILike(x.Email, $"%{search}%")) ||
                (x.Source != null && EF.Functions.ILike(x.Source, $"%{search}%")));
        }
        var total = await source.CountAsync(cancellationToken);
        var leads = await source.OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
        var contactIds = leads.Where(x => x.ContactId.HasValue).Select(x => x.ContactId!.Value).Distinct().ToArray();
        var references = await contacts.FindAsync(contactIds, cancellationToken);
        return new(query.Page, query.PageSize, total, leads.Select(x => ToDto(x, references.GetValueOrDefault(x.ContactId ?? Guid.Empty)?.DisplayName)).ToList());
    }

    public async Task<LeadDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var lead = await database.Leads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (lead is null) return null;
        var contact = lead.ContactId is { } contactId ? await contacts.FindAsync(contactId, cancellationToken) : null;
        return ToDto(lead, contact?.DisplayName);
    }

    public async Task<LeadDto> CreateAsync(LeadRequest request, CancellationToken cancellationToken)
    {
        var values = Validate(request, requireStamp: false);
        var contact = await ValidateContactAsync(values.ContactId, cancellationToken);
        var now = clock.UtcNow;
        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            TenantId = RequiredTenant(),
            CompanyId = RequiredCompany(),
            Title = values.Title!,
            ContactId = values.ContactId,
            Email = values.Email,
            Phone = values.Phone,
            Source = values.Source,
            ExpectedRevenue = values.ExpectedRevenue,
            Probability = values.Probability,
            AssignedUserId = null,
            PlannedCloseDate = values.PlannedCloseDate,
            Notes = values.Notes,
            State = LeadState.New,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = CurrentUser(),
            UpdatedBy = CurrentUser(),
            ConcurrencyStamp = NewStamp()
        };
        database.Leads.Add(lead); await SaveAsync(cancellationToken); await AuditAsync("crm.lead.created", lead, null, cancellationToken);
        return ToDto(lead, contact?.DisplayName);
    }

    public async Task<LeadDto> UpdateAsync(Guid id, LeadRequest request, CancellationToken cancellationToken)
    {
        var values = Validate(request, requireStamp: true);
        var contact = await ValidateContactAsync(values.ContactId, cancellationToken);
        var lead = await FindAsync(id, cancellationToken); EnsureStamp(lead, values.ConcurrencyStamp!);
        if (lead.State is LeadState.Converted) throw Validation("state", "Converted leads cannot be edited.");
        var before = JsonSerializer.Serialize(ToDto(lead, null));
        lead.Title = values.Title!; lead.ContactId = values.ContactId; lead.Email = values.Email; lead.Phone = values.Phone;
        lead.Source = values.Source; lead.ExpectedRevenue = values.ExpectedRevenue; lead.Probability = values.Probability;
        lead.PlannedCloseDate = values.PlannedCloseDate; lead.Notes = values.Notes;
        Touch(lead); await SaveAsync(cancellationToken); await AuditAsync("crm.lead.updated", lead, before, cancellationToken);
        return ToDto(lead, contact?.DisplayName);
    }

    public async Task<LeadDto> TransitionAsync(Guid id, LeadState state, LeadTransitionRequest request, CancellationToken cancellationToken)
    {
        if (state is not (LeadState.Qualified or LeadState.Lost or LeadState.New)) throw Validation("state", "Unsupported lead transition.");
        var lead = await FindAsync(id, cancellationToken); EnsureStamp(lead, request.ConcurrencyStamp);
        if (lead.State == LeadState.Converted) throw Validation("state", "Converted leads cannot transition.");
        if (state == LeadState.Lost && string.IsNullOrWhiteSpace(request.Reason)) throw Validation("reason", "A lost reason is required.");
        var before = JsonSerializer.Serialize(ToDto(lead, null)); lead.State = state;
        lead.LostReason = state == LeadState.Lost ? request.Reason!.Trim() : null; Touch(lead);
        await SaveAsync(cancellationToken); await AuditAsync($"crm.lead.{state.ToString().ToLowerInvariant()}", lead, before, cancellationToken);
        return ToDto(lead, null);
    }

    public async Task<LeadDto> SetArchivedAsync(Guid id, string concurrencyStamp, bool archived, CancellationToken cancellationToken)
    {
        var lead = await FindAsync(id, cancellationToken); EnsureStamp(lead, concurrencyStamp); var before = JsonSerializer.Serialize(ToDto(lead, null));
        lead.IsArchived = archived; Touch(lead); await SaveAsync(cancellationToken);
        await AuditAsync(archived ? "crm.lead.archived" : "crm.lead.reactivated", lead, before, cancellationToken);
        return ToDto(lead, null);
    }

    public async Task<LeadDto> AssignAsync(Guid id, LeadAssignmentRequest request, CancellationToken cancellationToken)
    {
        await ValidateAssigneeAsync(request.AssignedUserId, cancellationToken);
        var lead = await FindAsync(id, cancellationToken); EnsureStamp(lead, request.ConcurrencyStamp);
        if (lead.State == LeadState.Converted) throw Validation("state", "Converted leads cannot be reassigned.");
        var before = JsonSerializer.Serialize(ToDto(lead, null)); lead.AssignedUserId = request.AssignedUserId; Touch(lead);
        await SaveAsync(cancellationToken); await AuditAsync("crm.lead.assigned", lead, before, cancellationToken); return ToDto(lead, null);
    }

    public async Task<OpportunityDto> ConvertAsync(Guid id, ConvertLeadRequest request, CancellationToken cancellationToken)
    {
        var lead = await FindAsync(id, cancellationToken); EnsureStamp(lead, request.ConcurrencyStamp);
        if (lead.State != LeadState.Qualified) throw Validation("state", "Only qualified leads can be converted.");
        var stage = await database.PipelineStages.SingleOrDefaultAsync(x => x.Id == request.StageId && x.IsActive, cancellationToken)
            ?? throw Validation("stageId", "An active pipeline stage is required.");
        if (stage.IsWon || stage.IsLost) throw Validation("stageId", "Conversion requires an open pipeline stage.");
        var contact = await ValidateContactAsync(lead.ContactId, cancellationToken); var now = clock.UtcNow;
        var opportunity = new Opportunity
        {
            Id = Guid.NewGuid(),
            TenantId = RequiredTenant(),
            CompanyId = RequiredCompany(),
            SourceLeadId = lead.Id,
            ContactId = lead.ContactId,
            StageId = stage.Id,
            Title = string.IsNullOrWhiteSpace(request.Title) ? lead.Title : request.Title.Trim(),
            ExpectedRevenue = lead.ExpectedRevenue,
            Probability = lead.Probability,
            AssignedUserId = lead.AssignedUserId,
            PlannedCloseDate = lead.PlannedCloseDate,
            State = OpportunityState.Open,
            Notes = lead.Notes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = CurrentUser(),
            UpdatedBy = CurrentUser(),
            ConcurrencyStamp = NewStamp()
        };
        lead.State = LeadState.Converted; Touch(lead); database.Opportunities.Add(opportunity);
        await SaveAsync(cancellationToken); await AuditAsync("crm.lead.converted", lead, null, cancellationToken);
        audit.Append(new("crm.opportunity.created", "Opportunity", opportunity.Id.ToString(), null, null, JsonSerializer.Serialize(opportunity)));
        await audit.FlushAsync(cancellationToken);
        return new(opportunity.Id, opportunity.SourceLeadId, opportunity.ContactId, contact?.DisplayName, stage.Id, stage.Name,
            opportunity.Title, opportunity.ExpectedRevenue, opportunity.Probability, opportunity.AssignedUserId,
            opportunity.PlannedCloseDate, opportunity.State, null, opportunity.Notes, false, now, now, opportunity.ConcurrencyStamp);
    }

    internal static LeadRequest Validate(LeadRequest input, bool requireStamp)
    {
        string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var value = input with { Title = Clean(input.Title), Email = Clean(input.Email)?.ToLowerInvariant(), Phone = Clean(input.Phone), Source = Clean(input.Source), Notes = Clean(input.Notes) };
        var errors = new Dictionary<string, string[]>();
        if (value.Title is null || value.Title.Length > 200) errors["title"] = ["Title is required and cannot exceed 200 characters."];
        if (value.Email is not null && (!value.Email.Contains('@') || value.Email.Length > 254)) errors["email"] = ["Email is invalid."];
        if (value.ExpectedRevenue < 0 || DecimalPlaces(value.ExpectedRevenue) > 4) errors["expectedRevenue"] = ["Expected revenue must be non-negative with at most four decimal places."];
        if (value.Probability is < 0 or > 100) errors["probability"] = ["Probability must be between 0 and 100."];
        if (value.Notes?.Length > 4000) errors["notes"] = ["Notes cannot exceed 4,000 characters."];
        if (requireStamp && string.IsNullOrWhiteSpace(value.ConcurrencyStamp)) errors["concurrencyStamp"] = ["Concurrency stamp is required."];
        if (errors.Count > 0) throw new LeadValidationException(errors); return value;
    }

    private async Task<ContactReference?> ValidateContactAsync(Guid? id, CancellationToken token)
    { if (id is null) return null; var contact = await contacts.FindAsync(id.Value, token) ?? throw Validation("contactId", "Contact was not found in the active company."); if (contact.IsArchived) throw Validation("contactId", "Archived contacts cannot be assigned to new CRM work."); return contact; }
    private async Task ValidateAssigneeAsync(Guid? id, CancellationToken token) { if (id is not null && await users.FindAsync(id.Value, token) is null) throw Validation("assignedUserId", "Assigned user is not a member of the active company."); }
    private Task<Lead> FindAsync(Guid id, CancellationToken token) => database.Leads.SingleOrDefaultAsync(x => x.Id == id, token).ContinueWith(x => x.Result ?? throw new KeyNotFoundException("Lead was not found."), token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private void Touch(Lead lead) { lead.UpdatedAtUtc = clock.UtcNow; lead.UpdatedBy = CurrentUser(); lead.ConcurrencyStamp = NewStamp(); }
    private static void EnsureStamp(Lead lead, string stamp) { if (!StringComparer.Ordinal.Equals(lead.ConcurrencyStamp, stamp)) throw new LeadConcurrencyException(); }
    private async Task SaveAsync(CancellationToken token) { try { await database.SaveChangesAsync(token); } catch (DbUpdateConcurrencyException) { throw new LeadConcurrencyException(); } }
    private async Task AuditAsync(string action, Lead lead, string? before, CancellationToken token) { audit.Append(new(action, "Lead", lead.Id.ToString(), null, before, JsonSerializer.Serialize(ToDto(lead, null)))); await audit.FlushAsync(token); }
    private Guid RequiredTenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
    private Guid RequiredCompany() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private Guid? CurrentUser() => Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private static string NewStamp() => Guid.NewGuid().ToString("N");
    private static int DecimalPlaces(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0x7F;
    private static LeadDto ToDto(Lead x, string? contactName) => new(x.Id, x.Title, x.ContactId, contactName, x.Email, x.Phone, x.Source, x.ExpectedRevenue, x.Probability, x.AssignedUserId, x.PlannedCloseDate, x.State, x.LostReason, x.Notes, x.IsArchived, x.CreatedAtUtc, x.UpdatedAtUtc, x.ConcurrencyStamp);
    private static LeadValidationException Validation(string key, string message) => new(new Dictionary<string, string[]> { [key] = [message] });
    private static void ValidateQuery(LeadQuery query) { if (query.Page < 1) throw Validation("page", "Page must be at least 1."); if (query.PageSize is < 1 or > 100) throw Validation("pageSize", "Page size must be between 1 and 100."); if (query.Search?.Length > 200) throw Validation("search", "Search cannot exceed 200 characters."); }
}
