using Erp.Modules.Manufacturing.Contracts;

namespace Erp.Modules.Manufacturing;

public sealed class BillOfMaterial
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Number { get; set; }
    public Guid ProductId { get; set; }
    public required string ProductSku { get; set; }
    public required string ProductName { get; set; }
    public required string Revision { get; set; }
    public decimal OutputQuantity { get; set; }
    public required string OutputUnitCode { get; set; }
    public BillOfMaterialState State { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
    public List<BillOfMaterialLine> Components { get; set; } = [];
}

public sealed class BillOfMaterialLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BillOfMaterialId { get; set; }
    public Guid ComponentProductId { get; set; }
    public required string ComponentSku { get; set; }
    public required string ComponentName { get; set; }
    public decimal Quantity { get; set; }
    public decimal StandardUnitCost { get; set; }
    public required string UnitCode { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class WorkOrder
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Number { get; set; }
    public Guid BillOfMaterialId { get; set; }
    public required string BillOfMaterialNumber { get; set; }
    public required string BillOfMaterialRevision { get; set; }
    public Guid ProductId { get; set; }
    public required string ProductSku { get; set; }
    public required string ProductName { get; set; }
    public decimal PlannedQuantity { get; set; }
    public decimal PlannedLaborHours { get; set; }
    public decimal PlannedMaterialCost { get; set; }
    public required string CostCurrencyCode { get; set; }
    public Guid? InventoryLocationId { get; set; }
    public decimal? CompletedQuantity { get; set; }
    public decimal? ActualLaborHours { get; set; }
    public decimal? ActualMaterialCost { get; set; }
    public decimal? ActualLaborCost { get; set; }
    public decimal? ActualOverheadCost { get; set; }
    public decimal? TotalProductionCost { get; set; }
    public required string UnitCode { get; set; }
    public WorkOrderState State { get; set; }
    public DateOnly? PlannedStartDate { get; set; }
    public DateOnly? PlannedEndDate { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed class ProductionCapacityPolicy
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public decimal HoursPerDay { get; set; }
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");
}

public sealed class WorkOrderStatusHistory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid WorkOrderId { get; set; }
    public WorkOrderState FromState { get; set; }
    public WorkOrderState ToState { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
    public Guid? ChangedBy { get; set; }
    public string? Notes { get; set; }
}

public sealed class QualityInspection
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid WorkOrderId { get; set; }
    public required string Number { get; set; }
    public decimal SampleQuantity { get; set; }
    public InspectionDecision Decision { get; set; }
    public DateTimeOffset InspectedAtUtc { get; set; }
    public string? Inspector { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
}
