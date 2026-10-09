using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Modules.Crm;

internal static class CrmEndpoints
{
    public static void MapCrmEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/crm/leads").WithTags("CRM Leads");
        group.MapGet("/", Search).RequirePermission(CrmPermissions.Read);
        group.MapGet("/{id:guid}", Get).RequirePermission(CrmPermissions.Read);
        group.MapPost("/", Create).RequirePermission(CrmPermissions.Manage);
        group.MapPut("/{id:guid}", Update).RequirePermission(CrmPermissions.Manage);
        group.MapPost("/{id:guid}/qualify", (Guid id, LeadTransitionRequest request, ILeadService service, CancellationToken token) => Execute(() => service.TransitionAsync(id, LeadState.Qualified, request, token))).RequirePermission(CrmPermissions.Manage);
        group.MapPost("/{id:guid}/lose", (Guid id, LeadTransitionRequest request, ILeadService service, CancellationToken token) => Execute(() => service.TransitionAsync(id, LeadState.Lost, request, token))).RequirePermission(CrmPermissions.Manage);
        group.MapPost("/{id:guid}/reopen", (Guid id, LeadTransitionRequest request, ILeadService service, CancellationToken token) => Execute(() => service.TransitionAsync(id, LeadState.New, request, token))).RequirePermission(CrmPermissions.Manage);
        group.MapPost("/{id:guid}/assign", (Guid id, LeadAssignmentRequest request, ILeadService service, CancellationToken token) => Execute(() => service.AssignAsync(id, request, token))).RequirePermission(CrmPermissions.Assign);
        group.MapPost("/{id:guid}/convert", (Guid id, ConvertLeadRequest request, ILeadService service, CancellationToken token) => Execute(() => service.ConvertAsync(id, request, token))).RequirePermission(CrmPermissions.Manage);
        group.MapPost("/{id:guid}/archive", (Guid id, LeadTransitionRequest request, ILeadService service, CancellationToken token) => Execute(() => service.SetArchivedAsync(id, request.ConcurrencyStamp, true, token))).RequirePermission(CrmPermissions.Archive);
        group.MapPost("/{id:guid}/reactivate", (Guid id, LeadTransitionRequest request, ILeadService service, CancellationToken token) => Execute(() => service.SetArchivedAsync(id, request.ConcurrencyStamp, false, token))).RequirePermission(CrmPermissions.Archive);

        var stages = endpoints.MapGroup("/api/v1/crm/stages").WithTags("CRM Pipeline");
        stages.MapGet("/", (bool includeInactive, IOpportunityService service, CancellationToken token) => service.GetStagesAsync(includeInactive, token)).RequirePermission(CrmPermissions.Read);
        stages.MapPost("/", (PipelineStageRequest request, IOpportunityService service, CancellationToken token) => Execute(() => service.SaveStageAsync(null, request, token), true)).RequirePermission(CrmPermissions.Configure);
        stages.MapPut("/{id:guid}", (Guid id, PipelineStageRequest request, IOpportunityService service, CancellationToken token) => Execute(() => service.SaveStageAsync(id, request, token))).RequirePermission(CrmPermissions.Configure);

        var opportunities = endpoints.MapGroup("/api/v1/crm/opportunities").WithTags("CRM Opportunities");
        opportunities.MapGet("/", (string? search, OpportunityState? state, Guid? stageId, bool includeArchived, Guid? assignedUserId, int page, int pageSize, IOpportunityService service, CancellationToken token) => Execute(() => service.SearchAsync(new(search, state, stageId, includeArchived, assignedUserId, page < 1 ? 1 : page, pageSize < 1 ? 25 : pageSize), token))).RequirePermission(CrmPermissions.Read);
        opportunities.MapGet("/{id:guid}", async (Guid id, IOpportunityService service, CancellationToken token) => await service.GetAsync(id, token) is { } item ? Results.Ok(item) : Results.NotFound()).RequirePermission(CrmPermissions.Read);
        opportunities.MapPost("/", (OpportunityRequest request, IOpportunityService service, CancellationToken token) => Execute(() => service.CreateAsync(request, token), true)).RequirePermission(CrmPermissions.Manage);
        opportunities.MapPut("/{id:guid}", (Guid id, OpportunityRequest request, IOpportunityService service, CancellationToken token) => Execute(() => service.UpdateAsync(id, request, token))).RequirePermission(CrmPermissions.Manage);
        opportunities.MapPost("/{id:guid}/stage", (Guid id, OpportunityStageRequest request, IOpportunityService service, CancellationToken token) => Execute(() => service.MoveAsync(id, request, token))).RequirePermission(CrmPermissions.Manage);
        opportunities.MapPost("/{id:guid}/assign", (Guid id, OpportunityAssignmentRequest request, IOpportunityService service, CancellationToken token) => Execute(() => service.AssignAsync(id, request, token))).RequirePermission(CrmPermissions.Assign);
        opportunities.MapPost("/{id:guid}/archive", (Guid id, LeadTransitionRequest request, IOpportunityService service, CancellationToken token) => Execute(() => service.SetArchivedAsync(id, request.ConcurrencyStamp, true, token))).RequirePermission(CrmPermissions.Archive);
        opportunities.MapPost("/{id:guid}/reactivate", (Guid id, LeadTransitionRequest request, IOpportunityService service, CancellationToken token) => Execute(() => service.SetArchivedAsync(id, request.ConcurrencyStamp, false, token))).RequirePermission(CrmPermissions.Archive);

        var activities = endpoints.MapGroup("/api/v1/crm/activities").WithTags("CRM Activities");
        activities.MapGet("/", (Guid? leadId, Guid? opportunityId, IActivityService service, CancellationToken token) => Execute(() => service.ListAsync(leadId, opportunityId, token))).RequirePermission(CrmPermissions.Read);
        activities.MapPost("/", (ActivityRequest request, IActivityService service, CancellationToken token) => Execute(() => service.CreateAsync(request, token), true)).RequirePermission(CrmPermissions.Manage);
        activities.MapPost("/{id:guid}/complete", (Guid id, LeadTransitionRequest request, IActivityService service, CancellationToken token) => Execute(() => service.SetCompletedAsync(id, request.ConcurrencyStamp, true, token))).RequirePermission(CrmPermissions.Manage);
        activities.MapPost("/{id:guid}/reopen", (Guid id, LeadTransitionRequest request, IActivityService service, CancellationToken token) => Execute(() => service.SetCompletedAsync(id, request.ConcurrencyStamp, false, token))).RequirePermission(CrmPermissions.Manage);
    }
    private static Task<IResult> Search(ILeadService service, string? search, LeadState? state, bool includeArchived = false, Guid? assignedUserId = null, int page = 1, int pageSize = 25, CancellationToken token = default) => Execute(() => service.SearchAsync(new(search, state, includeArchived, assignedUserId, page, pageSize), token));
    private static async Task<IResult> Get(Guid id, ILeadService service, CancellationToken token) => await service.GetAsync(id, token) is { } lead ? Results.Ok(lead) : Results.NotFound();
    private static Task<IResult> Create(LeadRequest request, ILeadService service, CancellationToken token) => Execute(() => service.CreateAsync(request, token), true);
    private static Task<IResult> Update(Guid id, LeadRequest request, ILeadService service, CancellationToken token) => Execute(() => service.UpdateAsync(id, request, token));
    private static async Task<IResult> Execute<T>(Func<Task<T>> action, bool created = false)
    { try { var value = await action(); return created && value is LeadDto lead ? Results.Created($"/api/v1/crm/leads/{lead.Id}", value) : Results.Ok(value); } catch (LeadValidationException ex) { return Results.ValidationProblem(ex.Errors); } catch (LeadConcurrencyException ex) { return Results.Conflict(new { error = ex.Message }); } catch (KeyNotFoundException) { return Results.NotFound(); } }
}
