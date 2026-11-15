namespace Erp.Modules.Projects.Contracts;

public enum ProjectStatus { Planned, Active, OnHold, Completed, Cancelled }
public enum ProjectWorkStatus { NotStarted, InProgress, Completed, Blocked }

/// <summary>Fields used to create or update a project.</summary>
public sealed record ProjectRequest(
    string Name,
    string? Description,
    ProjectStatus Status,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? ConcurrencyStamp = null);

/// <summary>A project record in the active company.</summary>
public sealed record ProjectResponse(
    Guid Id,
    string Name,
    string? Description,
    ProjectStatus Status,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string ConcurrencyStamp);

/// <summary>Fields used to create or update a project milestone.</summary>
public sealed record MilestoneRequest(
    Guid ProjectId,
    string Name,
    string? Description,
    DateOnly? DueDate,
    ProjectWorkStatus Status,
    string? ConcurrencyStamp = null);

/// <summary>A milestone belonging to a project.</summary>
public sealed record MilestoneResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    DateOnly? DueDate,
    ProjectWorkStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string ConcurrencyStamp);

/// <summary>Fields used to create or update a project task.</summary>
public sealed record ProjectTaskRequest(
    Guid ProjectId,
    Guid? MilestoneId,
    string Title,
    string? Description,
    DateOnly? DueDate,
    ProjectWorkStatus Status,
    Guid? AssignedUserId,
    string? ConcurrencyStamp = null);

/// <summary>A task belonging to a project, optionally linked to a milestone.</summary>
public sealed record ProjectTaskResponse(
    Guid Id,
    Guid ProjectId,
    Guid? MilestoneId,
    string Title,
    string? Description,
    DateOnly? DueDate,
    ProjectWorkStatus Status,
    Guid? AssignedUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string ConcurrencyStamp);

/// <summary>Hours captured against a project, without rates, taxes, or invoice details.</summary>
public sealed record TimeEntryRequest(
    Guid ProjectId,
    Guid? TaskId,
    DateOnly WorkDate,
    decimal Hours,
    bool IsBillable,
    string? Description,
    string? ConcurrencyStamp = null);

/// <summary>A recorded project time entry, including whether the hours may be billable later.</summary>
public sealed record TimeEntryResponse(
    Guid Id,
    Guid ProjectId,
    Guid? TaskId,
    DateOnly WorkDate,
    decimal Hours,
    bool IsBillable,
    string? Description,
    Guid? UserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string ConcurrencyStamp);

public interface IProjectsService
{
    Task<IReadOnlyList<ProjectResponse>> ListProjectsAsync(CancellationToken cancellationToken);
    Task<ProjectResponse?> GetProjectAsync(Guid id, CancellationToken cancellationToken);
    Task<ProjectResponse> CreateProjectAsync(ProjectRequest request, CancellationToken cancellationToken);
    Task<ProjectResponse> UpdateProjectAsync(Guid id, ProjectRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<MilestoneResponse>> ListMilestonesAsync(Guid projectId, CancellationToken cancellationToken);
    Task<MilestoneResponse> SaveMilestoneAsync(Guid? id, MilestoneRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectTaskResponse>> ListTasksAsync(Guid projectId, Guid? milestoneId, CancellationToken cancellationToken);
    Task<ProjectTaskResponse> SaveTaskAsync(Guid? id, ProjectTaskRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TimeEntryResponse>> ListTimeEntriesAsync(Guid projectId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken);
    Task<TimeEntryResponse> SaveTimeEntryAsync(Guid? id, TimeEntryRequest request, CancellationToken cancellationToken);
}
