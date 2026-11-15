using Erp.Modules.Projects.Contracts;

namespace Erp.Modules.Projects;

public abstract class ProjectScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed class ProjectRecord : ProjectScopedEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}

public sealed class ProjectMilestone : ProjectScopedEntity
{
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateOnly? DueDate { get; set; }
    public ProjectWorkStatus Status { get; set; }
}

public sealed class ProjectTask : ProjectScopedEntity
{
    public Guid ProjectId { get; set; }
    public Guid? MilestoneId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateOnly? DueDate { get; set; }
    public ProjectWorkStatus Status { get; set; }
    public Guid? AssignedUserId { get; set; }
}

public sealed class ProjectTimeEntry : ProjectScopedEntity
{
    public Guid ProjectId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? UserId { get; set; }
    public DateOnly WorkDate { get; set; }
    public decimal Hours { get; set; }
    public bool IsBillable { get; set; }
    public string? Description { get; set; }
}
