using System.Text.Json.Serialization;

namespace Erp.Modules.Manufacturing.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BillOfMaterialState { Draft, Active, Retired }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkOrderState { Draft, Released, InProgress, Completed, Cancelled }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InspectionDecision { Accepted, Rejected, Conditional }

/// <summary>A component and quantity required for one output unit.</summary>
public sealed record BillOfMaterialLineRequest(Guid ComponentProductId, decimal Quantity, string? UnitCode, string? Notes,
    decimal StandardUnitCost = 0);

/// <summary>Fields used to create or revise a bill of materials.</summary>
public sealed record BillOfMaterialRequest(
    string? Number, Guid ProductId, string? Revision, decimal OutputQuantity,
    IReadOnlyList<BillOfMaterialLineRequest>? Components, string? Notes, string? ConcurrencyStamp = null);

/// <summary>Expected concurrency stamp for a bill-of-material state change.</summary>
public sealed record SetBillOfMaterialStateRequest(string? ConcurrencyStamp);

/// <summary>A component listed on a bill of materials.</summary>
public sealed record BillOfMaterialLineResponse(
    Guid ComponentProductId, string ComponentSku, string ComponentName, decimal Quantity, string UnitCode, string? Notes,
    decimal StandardUnitCost = 0);

/// <summary>A tenant-scoped bill of materials and its component requirements.</summary>
public sealed record BillOfMaterialResponse(
    Guid Id, string Number, Guid ProductId, string ProductSku, string ProductName, string Revision,
    decimal OutputQuantity, string OutputUnitCode, BillOfMaterialState State, string? Notes,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, Guid? CreatedBy, Guid? UpdatedBy,
    string ConcurrencyStamp, IReadOnlyList<BillOfMaterialLineResponse> Components);

/// <summary>Fields used to create a work order from an active bill of materials.</summary>
public sealed record CreateWorkOrderRequest(
    string? Number, Guid BillOfMaterialId, decimal PlannedQuantity, DateOnly? PlannedStartDate,
    DateOnly? PlannedEndDate, string? Notes, Guid? InventoryLocationId = null,
    decimal PlannedLaborHours = 8, string? CostCurrencyCode = null);

/// <summary>A production work order with its current production state.</summary>
public sealed record WorkOrderResponse(
    Guid Id, string Number, Guid BillOfMaterialId, string BillOfMaterialNumber, string BillOfMaterialRevision,
    Guid ProductId, string ProductSku, string ProductName, decimal PlannedQuantity, string UnitCode,
    WorkOrderState State, DateOnly? PlannedStartDate, DateOnly? PlannedEndDate, string? Notes,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, Guid? CreatedBy, Guid? UpdatedBy,
    string ConcurrencyStamp, Guid? InventoryLocationId = null, decimal? CompletedQuantity = null,
    decimal PlannedLaborHours = 8, decimal? ActualLaborHours = null, decimal? TotalProductionCost = null,
    string? CostCurrencyCode = null, decimal PlannedMaterialCost = 0);

/// <summary>Planned component demand and current inventory coverage for a work order.</summary>
public sealed record WorkOrderMaterialAvailabilityResponse(
    Guid ProductId, string ProductSku, string ProductName, decimal RequiredQuantity,
    decimal OnHand, decimal Reserved, decimal ReservedForWorkOrder, decimal Available, decimal Shortage);

/// <summary>Requested work-order state and transition audit information.</summary>
public sealed record ChangeWorkOrderStateRequest(WorkOrderState State, string? Notes, string? ConcurrencyStamp,
    decimal? ActualOutputQuantity = null, decimal? ActualLaborHours = null,
    decimal? ActualMaterialCost = null, decimal? ActualLaborCost = null, decimal? ActualOverheadCost = null);

public sealed record ProductionCapacityDto(decimal HoursPerDay, string ConcurrencyStamp);
public sealed record SetProductionCapacityRequest(decimal HoursPerDay, string? ConcurrencyStamp);

/// <summary>A recorded production-state transition.</summary>
public sealed record WorkOrderStatusChangeResponse(
    Guid Id, WorkOrderState FromState, WorkOrderState ToState, DateTimeOffset ChangedAtUtc,
    Guid? ChangedBy, string? Notes);

/// <summary>Fields used to record a quality inspection for a work order.</summary>
public sealed record CreateQualityInspectionRequest(
    string? Number, decimal SampleQuantity, InspectionDecision Decision, DateTimeOffset? InspectedAtUtc,
    string? Inspector, string? Notes);

/// <summary>A quality inspection record linked to a work order.</summary>
public sealed record QualityInspectionResponse(
    Guid Id, string Number, Guid WorkOrderId, string WorkOrderNumber, decimal SampleQuantity,
    InspectionDecision Decision, DateTimeOffset InspectedAtUtc, string? Inspector, string? Notes,
    DateTimeOffset CreatedAtUtc, Guid? CreatedBy);

/// <summary>Public service contract for manufacturing records.</summary>
public interface IManufacturingService
{
    Task<ProductionCapacityDto> GetProductionCapacityAsync(CancellationToken cancellationToken);
    Task<ProductionCapacityDto> SetProductionCapacityAsync(SetProductionCapacityRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<BillOfMaterialResponse>> SearchBillOfMaterialsAsync(string? search, BillOfMaterialState? state, CancellationToken cancellationToken);
    Task<BillOfMaterialResponse?> GetBillOfMaterialAsync(Guid id, CancellationToken cancellationToken);
    Task<BillOfMaterialResponse> CreateBillOfMaterialAsync(BillOfMaterialRequest request, CancellationToken cancellationToken);
    Task<BillOfMaterialResponse> UpdateBillOfMaterialAsync(Guid id, BillOfMaterialRequest request, CancellationToken cancellationToken);
    Task<BillOfMaterialResponse> SetBillOfMaterialStateAsync(Guid id, BillOfMaterialState state, string? concurrencyStamp, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkOrderResponse>> SearchWorkOrdersAsync(WorkOrderState? state, CancellationToken cancellationToken);
    Task<WorkOrderResponse?> GetWorkOrderAsync(Guid id, CancellationToken cancellationToken);
    Task<WorkOrderResponse> CreateWorkOrderAsync(CreateWorkOrderRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkOrderMaterialAvailabilityResponse>> GetWorkOrderMaterialAvailabilityAsync(
        Guid workOrderId, CancellationToken cancellationToken);
    Task<WorkOrderResponse> TransitionWorkOrderAsync(Guid id, ChangeWorkOrderStateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkOrderStatusChangeResponse>> GetWorkOrderHistoryAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<QualityInspectionResponse>> GetInspectionsAsync(Guid workOrderId, CancellationToken cancellationToken);
    Task<QualityInspectionResponse> CreateInspectionAsync(Guid workOrderId, CreateQualityInspectionRequest request, CancellationToken cancellationToken);
}

/// <summary>Validation failures for manufacturing input.</summary>
public sealed class ManufacturingValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception(string.Join(" ", errors.Values.SelectMany(x => x)))
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>A conflicting manufacturing state or duplicate identifier.</summary>
public sealed class ManufacturingConflictException(string message) : Exception(message);

/// <summary>The record changed after it was loaded.</summary>
public sealed class ManufacturingConcurrencyException() : Exception("The manufacturing record changed since it was loaded.");
