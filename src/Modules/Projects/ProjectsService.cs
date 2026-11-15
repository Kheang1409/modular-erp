using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Organization;
using Erp.Abstractions.Persistence;
using Erp.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Projects;

public sealed class ProjectsService(
    ProjectsDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    ICompanyUserDirectory companyUsers,
    IClock clock,
    IAuditLogWriter audit,
    IHttpContextAccessor httpContextAccessor,
    IBusinessTransactionCoordinator transactions) : IProjectsService
{
    public async Task<IReadOnlyList<ProjectResponse>> ListProjectsAsync(CancellationToken cancellationToken) =>
        await database.Projects.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => ToResponse(x)).ToListAsync(cancellationToken);

    public async Task<ProjectResponse?> GetProjectAsync(Guid id, CancellationToken cancellationToken) =>
        await database.Projects.AsNoTracking().Where(x => x.Id == id)
            .Select(x => ToResponse(x)).SingleOrDefaultAsync(cancellationToken);

    public async Task<ProjectResponse> CreateProjectAsync(ProjectRequest request, CancellationToken cancellationToken)
    {
        ValidateProject(request);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var now = clock.UtcNow;
        var entity = new ProjectRecord
        {
            Id = Guid.NewGuid(),
            TenantId = RequiredTenant(),
            CompanyId = RequiredCompany(),
            Name = request.Name.Trim(),
            Description = Normalize(request.Description),
            Status = request.Status,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = CurrentUserId(),
            UpdatedBy = CurrentUserId(),
            ConcurrencyStamp = NewStamp()
        };
        database.Projects.Add(entity);
        await database.SaveChangesAsync(cancellationToken);
        var response = ToResponse(entity);
        await AuditAsync("projects.project.created", "Project", entity.Id, null, response, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<ProjectResponse> UpdateProjectAsync(Guid id, ProjectRequest request, CancellationToken cancellationToken)
    {
        ValidateProject(request, updating: true);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var entity = await database.Projects.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        EnsureStamp(entity, request.ConcurrencyStamp);
        EnsureProjectTransition(entity.Status, request.Status);
        var before = ToResponse(entity);
        entity.Name = request.Name.Trim(); entity.Description = Normalize(request.Description);
        entity.Status = request.Status; entity.StartDate = request.StartDate; entity.EndDate = request.EndDate;
        Touch(entity);
        await SaveAsync(cancellationToken);
        var response = ToResponse(entity);
        await AuditAsync("projects.project.updated", "Project", id, before, response, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<IReadOnlyList<MilestoneResponse>> ListMilestonesAsync(Guid projectId, CancellationToken cancellationToken) =>
        await database.Milestones.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.DueDate).ThenBy(x => x.Name).Select(x => ToResponse(x)).ToListAsync(cancellationToken);

    public async Task<MilestoneResponse> SaveMilestoneAsync(Guid? id, MilestoneRequest request, CancellationToken cancellationToken)
    {
        ValidateName(request.Name, "name");
        ValidateText(request.Description, 4000, "description");
        ValidateEnum(request.Status, "status");
        await EnsureProjectAsync(request.ProjectId, cancellationToken);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        ProjectMilestone entity;
        MilestoneResponse? before = null;
        if (id is null)
        {
            var now = clock.UtcNow;
            entity = new ProjectMilestone
            {
                Id = Guid.NewGuid(),
                TenantId = RequiredTenant(),
                CompanyId = RequiredCompany(),
                ProjectId = request.ProjectId,
                Name = request.Name.Trim(),
                Description = Normalize(request.Description),
                DueDate = request.DueDate,
                Status = request.Status,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedBy = CurrentUserId(),
                UpdatedBy = CurrentUserId(),
                ConcurrencyStamp = NewStamp()
            };
            database.Milestones.Add(entity);
        }
        else
        {
            entity = await database.Milestones.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException();
            if (entity.ProjectId != request.ProjectId) throw new KeyNotFoundException();
            EnsureStamp(entity, request.ConcurrencyStamp);
            EnsureWorkTransition(entity.Status, request.Status, "status");
            before = ToResponse(entity);
            entity.ProjectId = request.ProjectId; entity.Name = request.Name.Trim();
            entity.Description = Normalize(request.Description); entity.DueDate = request.DueDate;
            entity.Status = request.Status; Touch(entity);
        }
        await SaveAsync(cancellationToken);
        var response = ToResponse(entity);
        await AuditAsync(id is null ? "projects.milestone.created" : "projects.milestone.updated",
            "ProjectMilestone", entity.Id, before, response, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<IReadOnlyList<ProjectTaskResponse>> ListTasksAsync(Guid projectId, Guid? milestoneId, CancellationToken cancellationToken)
    {
        var query = database.Tasks.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (milestoneId is not null) query = query.Where(x => x.MilestoneId == milestoneId);
        return await query.OrderBy(x => x.DueDate).ThenBy(x => x.Title)
            .Select(x => ToResponse(x)).ToListAsync(cancellationToken);
    }

    public async Task<ProjectTaskResponse> SaveTaskAsync(Guid? id, ProjectTaskRequest request, CancellationToken cancellationToken)
    {
        ValidateName(request.Title, "title");
        ValidateText(request.Description, 4000, "description");
        ValidateEnum(request.Status, "status");
        await EnsureProjectAsync(request.ProjectId, cancellationToken);
        await EnsureMilestoneAsync(request.MilestoneId, request.ProjectId, cancellationToken);
        await EnsureAssigneeAsync(request.AssignedUserId, cancellationToken);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        ProjectTask entity;
        ProjectTaskResponse? before = null;
        if (id is null)
        {
            var now = clock.UtcNow;
            entity = new ProjectTask
            {
                Id = Guid.NewGuid(),
                TenantId = RequiredTenant(),
                CompanyId = RequiredCompany(),
                ProjectId = request.ProjectId,
                MilestoneId = request.MilestoneId,
                Title = request.Title.Trim(),
                Description = Normalize(request.Description),
                DueDate = request.DueDate,
                Status = request.Status,
                AssignedUserId = request.AssignedUserId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedBy = CurrentUserId(),
                UpdatedBy = CurrentUserId(),
                ConcurrencyStamp = NewStamp()
            };
            database.Tasks.Add(entity);
        }
        else
        {
            entity = await database.Tasks.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException();
            if (entity.ProjectId != request.ProjectId) throw new KeyNotFoundException();
            EnsureStamp(entity, request.ConcurrencyStamp);
            EnsureWorkTransition(entity.Status, request.Status, "status");
            before = ToResponse(entity);
            entity.ProjectId = request.ProjectId; entity.MilestoneId = request.MilestoneId;
            entity.Title = request.Title.Trim(); entity.Description = Normalize(request.Description);
            entity.DueDate = request.DueDate; entity.Status = request.Status;
            entity.AssignedUserId = request.AssignedUserId; Touch(entity);
        }
        await SaveAsync(cancellationToken);
        var response = ToResponse(entity);
        await AuditAsync(id is null ? "projects.task.created" : "projects.task.updated",
            "ProjectTask", entity.Id, before, response, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<IReadOnlyList<TimeEntryResponse>> ListTimeEntriesAsync(
        Guid projectId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken)
    {
        if (from is not null && through is not null && from > through) throw Validation("dateRange", "The start date must not follow the end date.");
        var query = database.TimeEntries.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (from is not null) query = query.Where(x => x.WorkDate >= from);
        if (through is not null) query = query.Where(x => x.WorkDate <= through);
        return await query.OrderByDescending(x => x.WorkDate).ThenBy(x => x.Id)
            .Select(x => ToResponse(x)).ToListAsync(cancellationToken);
    }

    public async Task<TimeEntryResponse> SaveTimeEntryAsync(Guid? id, TimeEntryRequest request, CancellationToken cancellationToken)
    {
        ValidateTimeEntry(request);
        await EnsureProjectAsync(request.ProjectId, cancellationToken);
        await EnsureTaskAsync(request.TaskId, request.ProjectId, cancellationToken);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        ProjectTimeEntry entity;
        TimeEntryResponse? before = null;
        if (id is null)
        {
            var now = clock.UtcNow;
            entity = new ProjectTimeEntry
            {
                Id = Guid.NewGuid(),
                TenantId = RequiredTenant(),
                CompanyId = RequiredCompany(),
                ProjectId = request.ProjectId,
                TaskId = request.TaskId,
                UserId = CurrentUserId(),
                WorkDate = request.WorkDate,
                Hours = request.Hours,
                IsBillable = request.IsBillable,
                Description = Normalize(request.Description),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedBy = CurrentUserId(),
                UpdatedBy = CurrentUserId(),
                ConcurrencyStamp = NewStamp()
            };
            database.TimeEntries.Add(entity);
        }
        else
        {
            entity = await database.TimeEntries.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException();
            if (entity.ProjectId != request.ProjectId) throw new KeyNotFoundException();
            EnsureStamp(entity, request.ConcurrencyStamp);
            before = ToResponse(entity);
            entity.ProjectId = request.ProjectId; entity.TaskId = request.TaskId;
            entity.WorkDate = request.WorkDate; entity.Hours = request.Hours;
            entity.IsBillable = request.IsBillable; entity.Description = Normalize(request.Description);
            Touch(entity);
        }
        await SaveAsync(cancellationToken);
        var response = ToResponse(entity);
        await AuditAsync(id is null ? "projects.time-entry.created" : "projects.time-entry.updated",
            "ProjectTimeEntry", entity.Id, before, response, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private async Task EnsureProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!await database.Projects.AnyAsync(x => x.Id == projectId, cancellationToken)) throw new KeyNotFoundException();
    }

    private async Task EnsureMilestoneAsync(Guid? milestoneId, Guid projectId, CancellationToken cancellationToken)
    {
        if (milestoneId is not null && !await database.Milestones.AnyAsync(
                x => x.Id == milestoneId && x.ProjectId == projectId, cancellationToken))
            throw Validation("milestoneId", "The milestone must belong to this project.");
    }

    private async Task EnsureAssigneeAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is not null && await companyUsers.FindAsync(userId.Value, cancellationToken) is null)
            throw Validation("assignedUserId", "The assignee must be an active user in this company.");
    }

    private async Task EnsureTaskAsync(Guid? taskId, Guid projectId, CancellationToken cancellationToken)
    {
        if (taskId is not null && !await database.Tasks.AnyAsync(
                x => x.Id == taskId && x.ProjectId == projectId, cancellationToken))
            throw Validation("taskId", "The task must belong to this project.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new ProjectsConcurrencyException(); }
    }

    private void Touch(ProjectScopedEntity entity)
    {
        entity.UpdatedAtUtc = clock.UtcNow;
        entity.UpdatedBy = CurrentUserId();
        entity.ConcurrencyStamp = NewStamp();
    }

    private async Task AuditAsync<T>(
        string action, string type, Guid id, T? before, T after, CancellationToken cancellationToken)
    {
        audit.Append(new(action, type, id.ToString(), null,
            before is null ? null : JsonSerializer.Serialize(before), JsonSerializer.Serialize(after)));
        await audit.FlushAsync(cancellationToken);
    }

    private Guid RequiredTenant() => tenantContext.TenantId?.Value
        ?? throw new InvalidOperationException("An active tenant is required.");
    private Guid RequiredCompany() => companyContext.CompanyId?.Value
        ?? throw new InvalidOperationException("An active company is required.");
    private Guid? CurrentUserId() =>
        Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private static string NewStamp() => Guid.NewGuid().ToString("N");
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureStamp(ProjectScopedEntity entity, string? stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp)) throw Validation("concurrencyStamp", "Concurrency stamp is required.");
        if (!StringComparer.Ordinal.Equals(entity.ConcurrencyStamp, stamp)) throw new ProjectsConcurrencyException();
    }

    private static void ValidateProject(ProjectRequest request, bool updating = false)
    {
        ValidateName(request.Name, "name");
        ValidateText(request.Description, 4000, "description");
        ValidateEnum(request.Status, "status");
        if (request.StartDate is not null && request.EndDate is not null && request.EndDate < request.StartDate)
            throw Validation("endDate", "The end date must not precede the start date.");
        if (updating && string.IsNullOrWhiteSpace(request.ConcurrencyStamp))
            throw Validation("concurrencyStamp", "Concurrency stamp is required.");
    }

    private static void EnsureProjectTransition(ProjectStatus current, ProjectStatus next)
    {
        if (current == next) return;
        var allowed = (current, next) is
            (ProjectStatus.Planned, ProjectStatus.Active or ProjectStatus.Cancelled) or
            (ProjectStatus.Active, ProjectStatus.OnHold or ProjectStatus.Completed or ProjectStatus.Cancelled) or
            (ProjectStatus.OnHold, ProjectStatus.Active or ProjectStatus.Cancelled);
        if (!allowed) throw Validation("status", $"A project cannot move from {current} to {next}.");
    }

    private static void EnsureWorkTransition(ProjectWorkStatus current, ProjectWorkStatus next, string field)
    {
        if (current == next) return;
        var allowed = (current, next) is
            (ProjectWorkStatus.NotStarted, ProjectWorkStatus.InProgress or ProjectWorkStatus.Blocked) or
            (ProjectWorkStatus.InProgress, ProjectWorkStatus.Completed or ProjectWorkStatus.Blocked) or
            (ProjectWorkStatus.Blocked, ProjectWorkStatus.InProgress);
        if (!allowed) throw Validation(field, $"Work cannot move from {current} to {next}.");
    }

    private static void ValidateTimeEntry(TimeEntryRequest request)
    {
        if (request.Hours <= 0 || request.Hours > 24 || decimal.Round(request.Hours, 2) != request.Hours)
            throw Validation("hours", "Hours must be greater than zero and at most 24, to two decimal places.");
        ValidateText(request.Description, 1000, "description");
    }

    private static void ValidateName(string name, string field)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw Validation(field, "A value between 1 and 200 characters is required.");
    }

    private static void ValidateText(string? value, int maxLength, string field)
    {
        if (value?.Length > maxLength) throw Validation(field, $"Must not exceed {maxLength} characters.");
    }

    private static void ValidateEnum<T>(T value, string field) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw Validation(field, "A valid value is required.");
    }

    private static ProjectResponse ToResponse(ProjectRecord x) =>
        new(x.Id, x.Name, x.Description, x.Status, x.StartDate, x.EndDate, x.CreatedAtUtc, x.UpdatedAtUtc, x.ConcurrencyStamp);
    private static MilestoneResponse ToResponse(ProjectMilestone x) =>
        new(x.Id, x.ProjectId, x.Name, x.Description, x.DueDate, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.ConcurrencyStamp);
    private static ProjectTaskResponse ToResponse(ProjectTask x) =>
        new(x.Id, x.ProjectId, x.MilestoneId, x.Title, x.Description, x.DueDate, x.Status, x.AssignedUserId, x.CreatedAtUtc, x.UpdatedAtUtc, x.ConcurrencyStamp);
    private static TimeEntryResponse ToResponse(ProjectTimeEntry x) =>
        new(x.Id, x.ProjectId, x.TaskId, x.WorkDate, x.Hours, x.IsBillable, x.Description, x.UserId, x.CreatedAtUtc, x.UpdatedAtUtc, x.ConcurrencyStamp);

    private static ProjectsValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

public sealed class ProjectsValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("Project data validation failed.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class ProjectsConcurrencyException()
    : Exception("The project record changed since it was loaded.");
