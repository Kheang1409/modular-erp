using Erp.Abstractions.Modules;
using Erp.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Modules.Projects;

internal static class ProjectsEndpoints
{
    public static void MapProjectsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var projects = endpoints.MapGroup("/api/v1/projects").WithTags("Projects");
        projects.MapGet("/", async ([FromServices] IProjectsService service, CancellationToken token) =>
                Results.Ok(await service.ListProjectsAsync(token)))
            .WithName("ListProjects").WithSummary("List projects in the active company.")
            .RequirePermission(ProjectsPermissions.Read);
        projects.MapGet("/{id:guid}", GetProject).WithName("GetProject")
            .WithSummary("Get a project by ID.")
            .RequirePermission(ProjectsPermissions.Read);
        projects.MapPost("/", (ProjectRequest request, [FromServices] IProjectsService service, CancellationToken token) =>
                Execute(() => service.CreateProjectAsync(request, token), "/api/v1/projects/", item => item.Id))
            .WithName("CreateProject").WithSummary("Create a project.")
            .RequirePermission(ProjectsPermissions.Manage);
        projects.MapPut("/{id:guid}", (Guid id, ProjectRequest request, [FromServices] IProjectsService service, CancellationToken token) =>
                Execute(() => service.UpdateProjectAsync(id, request, token)))
            .WithName("UpdateProject").WithSummary("Update a project using its current concurrency stamp.")
            .RequirePermission(ProjectsPermissions.Manage);

        var milestones = endpoints.MapGroup("/api/v1/projects/{projectId:guid}/milestones").WithTags("Project Milestones");
        milestones.MapGet("/", (Guid projectId, [FromServices] IProjectsService service, CancellationToken token) =>
                service.ListMilestonesAsync(projectId, token))
            .WithName("ListProjectMilestones").WithSummary("List a project's milestones.")
            .RequirePermission(ProjectsPermissions.Read);
        milestones.MapPost("/", (Guid projectId, MilestoneRequest request, [FromServices] IProjectsService service, CancellationToken token) =>
                Execute(() => service.SaveMilestoneAsync(null, request with { ProjectId = projectId }, token),
                    $"/api/v1/projects/{projectId}/milestones/", item => item.Id))
            .WithName("CreateProjectMilestone").WithSummary("Create a milestone for a project.")
            .RequirePermission(ProjectsPermissions.Manage);
        milestones.MapPut("/{id:guid}", (Guid projectId, Guid id, MilestoneRequest request, [FromServices] IProjectsService service, CancellationToken token) =>
                Execute(() => service.SaveMilestoneAsync(id, request with { ProjectId = projectId }, token)))
            .WithName("UpdateProjectMilestone").WithSummary("Update a project milestone.")
            .RequirePermission(ProjectsPermissions.Manage);

        var tasks = endpoints.MapGroup("/api/v1/projects/{projectId:guid}/tasks").WithTags("Project Tasks");
        tasks.MapGet("/", (Guid projectId, Guid? milestoneId, [FromServices] IProjectsService service, CancellationToken token) =>
                service.ListTasksAsync(projectId, milestoneId, token))
            .WithName("ListProjectTasks").WithSummary("List a project's tasks, optionally filtered by milestone.")
            .RequirePermission(ProjectsPermissions.Read);
        tasks.MapPost("/", (Guid projectId, ProjectTaskRequest request, [FromServices] IProjectsService service, CancellationToken token) =>
                Execute(() => service.SaveTaskAsync(null, request with { ProjectId = projectId }, token),
                    $"/api/v1/projects/{projectId}/tasks/", item => item.Id))
            .WithName("CreateProjectTask").WithSummary("Create a task for a project.")
            .RequirePermission(ProjectsPermissions.Manage);
        tasks.MapPut("/{id:guid}", (Guid projectId, Guid id, ProjectTaskRequest request, [FromServices] IProjectsService service, CancellationToken token) =>
                Execute(() => service.SaveTaskAsync(id, request with { ProjectId = projectId }, token)))
            .WithName("UpdateProjectTask").WithSummary("Update a project task.")
            .RequirePermission(ProjectsPermissions.Manage);

        var time = endpoints.MapGroup("/api/v1/projects/{projectId:guid}/time-entries").WithTags("Project Time Entries");
        time.MapGet("/", (Guid projectId, DateOnly? from, DateOnly? to, [FromServices] IProjectsService service, CancellationToken token) =>
                service.ListTimeEntriesAsync(projectId, from, to, token))
            .WithName("ListProjectTimeEntries").WithSummary("List time entries, optionally bounded by work dates.")
            .RequirePermission(ProjectsPermissions.Read);
        time.MapPost("/", (Guid projectId, TimeEntryRequest request, [FromServices] IProjectsService service, CancellationToken token) =>
                Execute(() => service.SaveTimeEntryAsync(null, request with { ProjectId = projectId }, token),
                    $"/api/v1/projects/{projectId}/time-entries/", item => item.Id))
            .WithName("CreateProjectTimeEntry").WithSummary("Record project hours and billable status for later integration.")
            .RequirePermission(ProjectsPermissions.Manage);
        time.MapPut("/{id:guid}", (Guid projectId, Guid id, TimeEntryRequest request, [FromServices] IProjectsService service, CancellationToken token) =>
                Execute(() => service.SaveTimeEntryAsync(id, request with { ProjectId = projectId }, token)))
            .WithName("UpdateProjectTimeEntry").WithSummary("Update a project time entry.")
            .RequirePermission(ProjectsPermissions.Manage);
    }

    private static async Task<IResult> GetProject(Guid id, [FromServices] IProjectsService service, CancellationToken token) =>
        await service.GetProjectAsync(id, token) is { } project ? Results.Ok(project) : Results.NotFound();

    private static async Task<IResult> Execute<T>(
        Func<Task<T>> operation, string? location = null, Func<T, Guid>? getId = null)
    {
        try
        {
            var value = await operation();
            return location is null ? Results.Ok(value) : Results.Created($"{location}{getId!(value)}", value);
        }
        catch (ProjectsValidationException exception) { return Results.ValidationProblem(exception.Errors); }
        catch (ProjectsConcurrencyException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

}
