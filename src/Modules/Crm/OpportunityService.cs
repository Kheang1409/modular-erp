using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Organization;
using Erp.Abstractions.Persistence;
using Erp.Modules.Contacts.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Crm;

public interface IOpportunityService
{
    Task<IReadOnlyList<PipelineStageDto>> GetStagesAsync(bool includeInactive, CancellationToken token);
    Task<PipelineStageDto> SaveStageAsync(Guid? id, PipelineStageRequest request, CancellationToken token);
    Task<OpportunityPage> SearchAsync(OpportunityQuery query, CancellationToken token);
    Task<OpportunityDto?> GetAsync(Guid id, CancellationToken token);
    Task<OpportunityDto> CreateAsync(OpportunityRequest request, CancellationToken token);
    Task<OpportunityDto> UpdateAsync(Guid id, OpportunityRequest request, CancellationToken token);
    Task<OpportunityDto> MoveAsync(Guid id, OpportunityStageRequest request, CancellationToken token);
    Task<OpportunityDto> AssignAsync(Guid id, OpportunityAssignmentRequest request, CancellationToken token);
    Task<OpportunityDto> SetArchivedAsync(Guid id, string concurrencyStamp, bool archived, CancellationToken token);
}

public sealed class OpportunityService(CrmDbContext database, ITenantContext tenant, ICompanyContext company,
    IContactDirectory contacts, ICompanyUserDirectory users, IClock clock, IAuditLogWriter audit, IHttpContextAccessor http,
    IBusinessTransactionCoordinator transactions) : IOpportunityService
{
    public async Task<IReadOnlyList<PipelineStageDto>> GetStagesAsync(bool includeInactive, CancellationToken token) =>
        await database.PipelineStages.AsNoTracking().Where(x => includeInactive || x.IsActive).OrderBy(x => x.Sequence)
            .Select(x => StageDto(x)).ToListAsync(token);

    public async Task<PipelineStageDto> SaveStageAsync(Guid? id, PipelineStageRequest request, CancellationToken token)
    {
        var value = ValidateStage(request, id.HasValue);
        await using var transaction = await transactions.BeginAsync(database, token);
        PipelineStage stage;
        if (id is null) { stage = new() { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), Name = value.Name!, ConcurrencyStamp = Stamp() }; database.PipelineStages.Add(stage); }
        else { stage = await database.PipelineStages.SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException(); Check(stage.ConcurrencyStamp, value.ConcurrencyStamp!); }
        stage.Name = value.Name!; stage.Sequence = value.Sequence; stage.DefaultProbability = value.DefaultProbability;
        stage.IsWon = value.IsWon; stage.IsLost = value.IsLost; stage.IsActive = value.IsActive; stage.ConcurrencyStamp = Stamp();
        await Save(token); await Audit(id is null ? "crm.stage.created" : "crm.stage.updated", "PipelineStage", stage.Id, stage, token);
        var result = StageDto(stage);
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<OpportunityPage> SearchAsync(OpportunityQuery query, CancellationToken token)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > 100) throw Validation("page", "Invalid page or page size.");
        var source = database.Opportunities.AsNoTracking(); if (!query.IncludeArchived) source = source.Where(x => !x.IsArchived);
        if (query.State is not null) source = source.Where(x => x.State == query.State); if (query.StageId is not null) source = source.Where(x => x.StageId == query.StageId);
        if (query.AssignedUserId is not null) source = source.Where(x => x.AssignedUserId == query.AssignedUserId);
        if (!string.IsNullOrWhiteSpace(query.Search)) { var search = query.Search.Trim(); source = source.Where(x => EF.Functions.ILike(x.Title, $"%{search}%")); }
        var total = await source.CountAsync(token); var items = await source.OrderByDescending(x => x.UpdatedAtUtc).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(token);
        return new(query.Page, query.PageSize, total, await Map(items, token));
    }
    public async Task<OpportunityDto?> GetAsync(Guid id, CancellationToken token)
    { var item = await database.Opportunities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token); return item is null ? null : (await Map([item], token))[0]; }

    public async Task<OpportunityDto> CreateAsync(OpportunityRequest request, CancellationToken token)
    {
        var value = ValidateOpportunity(request, false);
        await using var transaction = await transactions.BeginAsync(database, token);
        var stage = await ActiveStage(value.StageId, token); EnsureOpen(stage);
        var contact = await Contact(value.ContactId, token); var now = clock.UtcNow;
        var item = new Opportunity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant(),
            CompanyId = Company(),
            Title = value.Title!,
            ContactId = value.ContactId,
            StageId = stage.Id,
            ExpectedRevenue = value.ExpectedRevenue,
            Probability = stage.DefaultProbability,
            AssignedUserId = null,
            PlannedCloseDate = value.PlannedCloseDate,
            Notes = value.Notes,
            State = OpportunityState.Open,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = User(),
            UpdatedBy = User(),
            ConcurrencyStamp = Stamp()
        };
        database.Opportunities.Add(item); await Save(token); await Audit("crm.opportunity.created", "Opportunity", item.Id, item, token);
        var result = Dto(item, stage, contact?.DisplayName);
        await transaction.CommitAsync(token);
        return result;
    }
    public async Task<OpportunityDto> UpdateAsync(Guid id, OpportunityRequest request, CancellationToken token)
    {
        var value = ValidateOpportunity(request, true);
        await using var transaction = await transactions.BeginAsync(database, token);
        var item = await Find(id, token); Check(item.ConcurrencyStamp, value.ConcurrencyStamp!);
        if (item.State != OpportunityState.Open) throw Validation("state", "Won or lost opportunities cannot be edited.");
        var stage = await ActiveStage(value.StageId, token); EnsureOpen(stage); var contact = await Contact(value.ContactId, token);
        item.Title = value.Title!; item.ContactId = value.ContactId; item.StageId = stage.Id; item.ExpectedRevenue = value.ExpectedRevenue;
        item.Probability = stage.DefaultProbability; item.PlannedCloseDate = value.PlannedCloseDate; item.Notes = value.Notes; Touch(item);
        await Save(token); await Audit("crm.opportunity.updated", "Opportunity", item.Id, item, token);
        var result = Dto(item, stage, contact?.DisplayName);
        await transaction.CommitAsync(token);
        return result;
    }
    public async Task<OpportunityDto> MoveAsync(Guid id, OpportunityStageRequest request, CancellationToken token)
    {
        await using var transaction = await transactions.BeginAsync(database, token);
        var item = await Find(id, token); Check(item.ConcurrencyStamp, request.ConcurrencyStamp); var stage = await ActiveStage(request.StageId, token);
        if (item.State == OpportunityState.Won) throw Validation("state", "Won opportunities are terminal.");
        if (item.State == OpportunityState.Lost && (stage.IsWon || stage.IsLost)) throw Validation("state", "Lost opportunities must reopen through an open stage.");
        if (stage.IsLost && string.IsNullOrWhiteSpace(request.LostReason)) throw Validation("lostReason", "A lost reason is required.");
        item.StageId = stage.Id; item.State = stage.IsWon ? OpportunityState.Won : stage.IsLost ? OpportunityState.Lost : OpportunityState.Open;
        item.Probability = stage.IsWon ? 100 : stage.IsLost ? 0 : stage.DefaultProbability; item.LostReason = stage.IsLost ? request.LostReason!.Trim() : null; Touch(item);
        await Save(token); await Audit($"crm.opportunity.{item.State.ToString().ToLowerInvariant()}", "Opportunity", item.Id, item, token);
        var result = Dto(item, stage, null);
        await transaction.CommitAsync(token);
        return result;
    }
    public async Task<OpportunityDto> AssignAsync(Guid id, OpportunityAssignmentRequest request, CancellationToken token)
    {
        await using var transaction = await transactions.BeginAsync(database, token);
        await Assignee(request.AssignedUserId, token);
        var item = await Find(id, token); Check(item.ConcurrencyStamp, request.ConcurrencyStamp);
        item.AssignedUserId = request.AssignedUserId; Touch(item);
        await Save(token); await Audit("crm.opportunity.assigned", "Opportunity", item.Id, item, token);
        var stage = await ActiveStage(item.StageId, token);
        var result = Dto(item, stage, null);
        await transaction.CommitAsync(token);
        return result;
    }
    public async Task<OpportunityDto> SetArchivedAsync(Guid id, string concurrencyStamp, bool archived, CancellationToken token)
    {
        await using var transaction = await transactions.BeginAsync(database, token);
        var item = await Find(id, token); Check(item.ConcurrencyStamp, concurrencyStamp);
        item.IsArchived = archived; Touch(item);
        await Save(token); await Audit(archived ? "crm.opportunity.archived" : "crm.opportunity.reactivated", "Opportunity", item.Id, item, token);
        var stage = await ActiveStage(item.StageId, token);
        var result = Dto(item, stage, null);
        await transaction.CommitAsync(token);
        return result;
    }

    internal static PipelineStageRequest ValidateStage(PipelineStageRequest value, bool stampRequired)
    { var name = string.IsNullOrWhiteSpace(value.Name) ? null : value.Name.Trim(); var errors = new Dictionary<string, string[]>(); if (name is null || name.Length > 100) errors["name"] = ["Name is required and cannot exceed 100 characters."]; if (value.Sequence < 0) errors["sequence"] = ["Sequence cannot be negative."]; if (value.DefaultProbability is < 0 or > 100) errors["defaultProbability"] = ["Probability must be between 0 and 100."]; if (value.IsWon && value.IsLost) errors["terminal"] = ["A stage cannot be both won and lost."]; if (stampRequired && string.IsNullOrWhiteSpace(value.ConcurrencyStamp)) errors["concurrencyStamp"] = ["Concurrency stamp is required."]; if (errors.Count > 0) throw new LeadValidationException(errors); return value with { Name = name }; }
    private static OpportunityRequest ValidateOpportunity(OpportunityRequest value, bool stampRequired)
    { var title = string.IsNullOrWhiteSpace(value.Title) ? null : value.Title.Trim(); var errors = new Dictionary<string, string[]>(); if (title is null || title.Length > 200) errors["title"] = ["Title is required."]; if (value.ExpectedRevenue < 0) errors["expectedRevenue"] = ["Expected revenue cannot be negative."]; if (stampRequired && string.IsNullOrWhiteSpace(value.ConcurrencyStamp)) errors["concurrencyStamp"] = ["Concurrency stamp is required."]; if (errors.Count > 0) throw new LeadValidationException(errors); return value with { Title = title, Notes = string.IsNullOrWhiteSpace(value.Notes) ? null : value.Notes.Trim() }; }
    private async Task<IReadOnlyList<OpportunityDto>> Map(IReadOnlyList<Opportunity> items, CancellationToken token) { var stages = await database.PipelineStages.AsNoTracking().Where(x => items.Select(i => i.StageId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, token); var contactIds = items.Where(x => x.ContactId.HasValue).Select(x => x.ContactId!.Value).Distinct().ToArray(); var refs = await contacts.FindAsync(contactIds, token); return items.Select(x => Dto(x, stages[x.StageId], refs.GetValueOrDefault(x.ContactId ?? Guid.Empty)?.DisplayName)).ToList(); }
    private async Task<ContactReference?> Contact(Guid? id, CancellationToken token) { if (id is null) return null; var value = await contacts.FindAsync(id.Value, token) ?? throw Validation("contactId", "Contact was not found in the active company."); if (value.IsArchived) throw Validation("contactId", "Archived contacts cannot be assigned."); return value; }
    private async Task Assignee(Guid? id, CancellationToken token) { if (id is not null && await users.FindAsync(id.Value, token) is null) throw Validation("assignedUserId", "Assigned user is not a member of the active company."); }
    private async Task<PipelineStage> ActiveStage(Guid id, CancellationToken token) =>
        await database.PipelineStages.SingleOrDefaultAsync(x => x.Id == id && x.IsActive, token) ?? throw Validation("stageId", "An active stage is required.");
    private async Task<Opportunity> Find(Guid id, CancellationToken token) =>
        await database.Opportunities.SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
    private static void EnsureOpen(PipelineStage stage) { if (stage.IsWon || stage.IsLost) throw Validation("stageId", "An open stage is required."); }
    private void Touch(Opportunity item) { item.UpdatedAtUtc = clock.UtcNow; item.UpdatedBy = User(); item.ConcurrencyStamp = Stamp(); }
    private async Task Save(CancellationToken token) { try { await database.SaveChangesAsync(token); } catch (DbUpdateConcurrencyException) { throw new LeadConcurrencyException(); } catch (DbUpdateException) { throw Validation("conflict", "The stage name or sequence already exists."); } }
    private async Task Audit(string action, string type, Guid id, object after, CancellationToken token) { audit.Append(new(action, type, id.ToString(), null, null, JsonSerializer.Serialize(after))); await audit.FlushAsync(token); }
    private Guid Tenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); private Guid Company() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private Guid? User() => Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null; private static string Stamp() => Guid.NewGuid().ToString("N"); private static void Check(string actual, string expected) { if (!StringComparer.Ordinal.Equals(actual, expected)) throw new LeadConcurrencyException(); }
    private static PipelineStageDto StageDto(PipelineStage x) => new(x.Id, x.Name, x.Sequence, x.DefaultProbability, x.IsWon, x.IsLost, x.IsActive, x.ConcurrencyStamp);
    private static OpportunityDto Dto(Opportunity x, PipelineStage stage, string? contact) => new(x.Id, x.SourceLeadId, x.ContactId, contact, x.StageId, stage.Name, x.Title, x.ExpectedRevenue, x.Probability, x.AssignedUserId, x.PlannedCloseDate, x.State, x.LostReason, x.Notes, x.IsArchived, x.CreatedAtUtc, x.UpdatedAtUtc, x.ConcurrencyStamp);
    private static LeadValidationException Validation(string key, string message) => new(new Dictionary<string, string[]> { [key] = [message] });
}
