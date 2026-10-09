namespace Erp.Modules.Crm;

public enum LeadState { New, Qualified, Converted, Lost }
public enum OpportunityState { Open, Won, Lost }
public enum ActivityType { Task, Call, Meeting, Note }

public sealed class Lead
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Title { get; set; }
    public Guid? ContactId { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Source { get; set; }
    public decimal ExpectedRevenue { get; set; }
    public int Probability { get; set; }
    public Guid? AssignedUserId { get; set; }
    public DateOnly? PlannedCloseDate { get; set; }
    public LeadState State { get; set; }
    public string? LostReason { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed class PipelineStage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public int Sequence { get; set; }
    public int DefaultProbability { get; set; }
    public bool IsWon { get; set; }
    public bool IsLost { get; set; }
    public bool IsActive { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed class Opportunity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? SourceLeadId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid StageId { get; set; }
    public required string Title { get; set; }
    public decimal ExpectedRevenue { get; set; }
    public int Probability { get; set; }
    public Guid? AssignedUserId { get; set; }
    public DateOnly? PlannedCloseDate { get; set; }
    public OpportunityState State { get; set; }
    public string? LostReason { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed class CrmActivity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? OpportunityId { get; set; }
    public ActivityType Type { get; set; }
    public required string Subject { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public Guid? AssignedUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed record LeadRequest(string? Title, Guid? ContactId, string? Email, string? Phone, string? Source,
    decimal ExpectedRevenue, int Probability, Guid? AssignedUserId, DateOnly? PlannedCloseDate,
    string? Notes, string? ConcurrencyStamp = null);
public sealed record LeadDto(Guid Id, string Title, Guid? ContactId, string? ContactName, string? Email, string? Phone,
    string? Source, decimal ExpectedRevenue, int Probability, Guid? AssignedUserId, DateOnly? PlannedCloseDate,
    LeadState State, string? LostReason, string? Notes, bool IsArchived, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, string ConcurrencyStamp);
public sealed record LeadQuery(string? Search = null, LeadState? State = null, bool IncludeArchived = false,
    Guid? AssignedUserId = null, int Page = 1, int PageSize = 25);
public sealed record LeadPage(int Page, int PageSize, int Total, IReadOnlyList<LeadDto> Items);
public sealed record LeadTransitionRequest(string ConcurrencyStamp, string? Reason = null);
public sealed record LeadAssignmentRequest(string ConcurrencyStamp, Guid? AssignedUserId);
public sealed record ConvertLeadRequest(string ConcurrencyStamp, Guid StageId, string? Title = null);
public sealed record PipelineStageRequest(string? Name, int Sequence, int DefaultProbability, bool IsWon, bool IsLost,
    bool IsActive = true, string? ConcurrencyStamp = null);
public sealed record PipelineStageDto(Guid Id, string Name, int Sequence, int DefaultProbability, bool IsWon,
    bool IsLost, bool IsActive, string ConcurrencyStamp);
public sealed record OpportunityRequest(string? Title, Guid? ContactId, Guid StageId, decimal ExpectedRevenue,
    Guid? AssignedUserId, DateOnly? PlannedCloseDate, string? Notes,
    string? ConcurrencyStamp = null);
public sealed record OpportunityDto(Guid Id, Guid? SourceLeadId, Guid? ContactId, string? ContactName, Guid StageId,
    string StageName, string Title, decimal ExpectedRevenue, int Probability, Guid? AssignedUserId,
    DateOnly? PlannedCloseDate, OpportunityState State, string? LostReason, string? Notes, bool IsArchived,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string ConcurrencyStamp);
public sealed record OpportunityQuery(string? Search = null, OpportunityState? State = null, Guid? StageId = null,
    bool IncludeArchived = false, Guid? AssignedUserId = null, int Page = 1, int PageSize = 25);
public sealed record OpportunityPage(int Page, int PageSize, int Total, IReadOnlyList<OpportunityDto> Items);
public sealed record OpportunityStageRequest(string ConcurrencyStamp, Guid StageId, string? LostReason = null);
public sealed record OpportunityAssignmentRequest(string ConcurrencyStamp, Guid? AssignedUserId);
public sealed record ActivityRequest(Guid? LeadId, Guid? OpportunityId, ActivityType Type, string? Subject,
    string? Details, DateTimeOffset? DueAtUtc, Guid? AssignedUserId);
public sealed record ActivityDto(Guid Id, Guid? LeadId, Guid? OpportunityId, ActivityType Type, string Subject,
    string? Details, DateTimeOffset? DueAtUtc, DateTimeOffset? CompletedAtUtc, Guid? AssignedUserId,
    DateTimeOffset CreatedAtUtc, string ConcurrencyStamp);

public sealed class LeadValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Lead validation failed.")
{ public IReadOnlyDictionary<string, string[]> Errors { get; } = errors; }
public sealed class LeadConcurrencyException : Exception
{ public LeadConcurrencyException() : base("The lead changed since it was loaded.") { } }
