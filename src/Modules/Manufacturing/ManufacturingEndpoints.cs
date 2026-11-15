using Erp.Abstractions.Modules;
using Erp.Modules.Manufacturing.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Modules.Manufacturing;

internal static class ManufacturingEndpoints
{
    public static void MapManufacturingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/manufacturing").WithTags("Manufacturing");

        group.MapGet("/capacity", GetProductionCapacity)
            .WithName("GetManufacturingCapacity").WithSummary("Get daily production capacity")
            .RequirePermission(ManufacturingPermissions.Read);
        group.MapPut("/capacity", SetProductionCapacity)
            .WithName("SetManufacturingCapacity").WithSummary("Set daily production capacity")
            .RequirePermission(ManufacturingPermissions.WorkOrderManage);

        group.MapGet("/bills-of-material", SearchBillsOfMaterial)
            .WithName("SearchManufacturingBillsOfMaterial").WithSummary("Search bills of materials")
            .RequirePermission(ManufacturingPermissions.Read);
        group.MapGet("/bills-of-material/{id:guid}", GetBillOfMaterial)
            .WithName("GetManufacturingBillOfMaterial").WithSummary("Get a bill of materials")
            .RequirePermission(ManufacturingPermissions.Read);
        group.MapPost("/bills-of-material", CreateBillOfMaterial)
            .WithName("CreateManufacturingBillOfMaterial").WithSummary("Create a draft bill of materials")
            .RequirePermission(ManufacturingPermissions.BillOfMaterialManage);
        group.MapPut("/bills-of-material/{id:guid}", UpdateBillOfMaterial)
            .WithName("UpdateManufacturingBillOfMaterial").WithSummary("Update a draft bill of materials")
            .RequirePermission(ManufacturingPermissions.BillOfMaterialManage);
        group.MapPost("/bills-of-material/{id:guid}/activate", ActivateBillOfMaterial)
            .WithName("ActivateManufacturingBillOfMaterial").WithSummary("Activate a bill of materials")
            .RequirePermission(ManufacturingPermissions.BillOfMaterialManage);
        group.MapPost("/bills-of-material/{id:guid}/retire", RetireBillOfMaterial)
            .WithName("RetireManufacturingBillOfMaterial").WithSummary("Retire a bill of materials")
            .RequirePermission(ManufacturingPermissions.BillOfMaterialManage);

        group.MapGet("/work-orders", SearchWorkOrders)
            .WithName("SearchManufacturingWorkOrders").WithSummary("Search production work orders")
            .RequirePermission(ManufacturingPermissions.Read);
        group.MapGet("/work-orders/{id:guid}", GetWorkOrder)
            .WithName("GetManufacturingWorkOrder").WithSummary("Get a production work order")
            .RequirePermission(ManufacturingPermissions.Read);
        group.MapGet("/work-orders/{id:guid}/materials", GetWorkOrderMaterialAvailability)
            .WithName("GetManufacturingWorkOrderMaterialAvailability")
            .WithSummary("Check work-order component availability and shortages")
            .RequirePermission(ManufacturingPermissions.Read);
        group.MapPost("/work-orders", CreateWorkOrder)
            .WithName("CreateManufacturingWorkOrder").WithSummary("Create a work order from an active bill of materials")
            .RequirePermission(ManufacturingPermissions.WorkOrderManage);
        group.MapPost("/work-orders/{id:guid}/transition", TransitionWorkOrder)
            .WithName("TransitionManufacturingWorkOrder").WithSummary("Advance or cancel a work order")
            .RequirePermission(ManufacturingPermissions.WorkOrderTransition);
        group.MapGet("/work-orders/{id:guid}/status-history", GetWorkOrderHistory)
            .WithName("GetManufacturingWorkOrderHistory").WithSummary("Read production status history")
            .RequirePermission(ManufacturingPermissions.Read);
        group.MapGet("/work-orders/{id:guid}/inspections", GetInspections)
            .WithName("GetManufacturingQualityInspections").WithSummary("List quality inspections for a work order")
            .RequirePermission(ManufacturingPermissions.Read);
        group.MapPost("/work-orders/{id:guid}/inspections", CreateInspection)
            .WithName("CreateManufacturingQualityInspection").WithSummary("Record a quality inspection")
            .RequirePermission(ManufacturingPermissions.QualityManage);
    }

    private static Task<IResult> SearchBillsOfMaterial(
        [FromServices] IManufacturingService service, string? search, BillOfMaterialState? state, CancellationToken cancellationToken) =>
        Execute(() => service.SearchBillOfMaterialsAsync(search, state, cancellationToken));

    private static Task<IResult> GetProductionCapacity([FromServices] IManufacturingService service,
        CancellationToken token) => Execute(() => service.GetProductionCapacityAsync(token));

    private static Task<IResult> SetProductionCapacity(SetProductionCapacityRequest request,
        [FromServices] IManufacturingService service, CancellationToken token) =>
        Execute(() => service.SetProductionCapacityAsync(request, token));

    private static async Task<IResult> GetBillOfMaterial(
        Guid id, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        await service.GetBillOfMaterialAsync(id, cancellationToken) is { } item ? Results.Ok(item) : Results.NotFound();

    private static Task<IResult> CreateBillOfMaterial(
        BillOfMaterialRequest request, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.CreateBillOfMaterialAsync(request, cancellationToken), $"/api/v1/manufacturing/bills-of-material");

    private static Task<IResult> UpdateBillOfMaterial(
        Guid id, BillOfMaterialRequest request, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.UpdateBillOfMaterialAsync(id, request, cancellationToken));

    private static Task<IResult> ActivateBillOfMaterial(
        Guid id, SetBillOfMaterialStateRequest request, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.SetBillOfMaterialStateAsync(id, BillOfMaterialState.Active, request.ConcurrencyStamp, cancellationToken));

    private static Task<IResult> RetireBillOfMaterial(
        Guid id, SetBillOfMaterialStateRequest request, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.SetBillOfMaterialStateAsync(id, BillOfMaterialState.Retired, request.ConcurrencyStamp, cancellationToken));

    private static Task<IResult> SearchWorkOrders(
        [FromServices] IManufacturingService service, WorkOrderState? state, CancellationToken cancellationToken) =>
        Execute(() => service.SearchWorkOrdersAsync(state, cancellationToken));

    private static async Task<IResult> GetWorkOrder(
        Guid id, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        await service.GetWorkOrderAsync(id, cancellationToken) is { } item ? Results.Ok(item) : Results.NotFound();

    private static Task<IResult> GetWorkOrderMaterialAvailability(
        Guid id, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.GetWorkOrderMaterialAvailabilityAsync(id, cancellationToken));

    private static Task<IResult> CreateWorkOrder(
        CreateWorkOrderRequest request, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.CreateWorkOrderAsync(request, cancellationToken), "/api/v1/manufacturing/work-orders");

    private static Task<IResult> TransitionWorkOrder(
        Guid id, ChangeWorkOrderStateRequest request, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.TransitionWorkOrderAsync(id, request, cancellationToken));

    private static Task<IResult> GetWorkOrderHistory(
        Guid id, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.GetWorkOrderHistoryAsync(id, cancellationToken));

    private static Task<IResult> GetInspections(
        Guid id, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.GetInspectionsAsync(id, cancellationToken));

    private static Task<IResult> CreateInspection(
        Guid id, CreateQualityInspectionRequest request, [FromServices] IManufacturingService service, CancellationToken cancellationToken) =>
        Execute(() => service.CreateInspectionAsync(id, request, cancellationToken),
            $"/api/v1/manufacturing/work-orders/{id}/inspections");

    private static async Task<IResult> Execute<T>(Func<Task<T>> operation, string? location = null)
    {
        try
        {
            var value = await operation();
            return location is null ? Results.Ok(value) : Results.Created(location, value);
        }
        catch (ManufacturingValidationException exception) { return Results.ValidationProblem(exception.Errors); }
        catch (ManufacturingConcurrencyException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (ManufacturingConflictException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }
}
