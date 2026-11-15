using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Modules.Manufacturing.Contracts;
using Erp.Modules.Products.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Abstractions.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Manufacturing;

public sealed class ManufacturingService(
    ManufacturingDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IClock clock,
    IHttpContextAccessor httpContextAccessor,
    IProductDirectory productDirectory,
    IProductionStockWriter productionStock,
    IStockAvailability stockAvailability,
    IAuditLogWriter audit,
    IBusinessTransactionCoordinator transactions) : IManufacturingService
{
    public async Task<ProductionCapacityDto> GetProductionCapacityAsync(CancellationToken cancellationToken)
    {
        var policy = await database.ProductionCapacityPolicies.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return policy is null ? new(8m, string.Empty) : new(policy.HoursPerDay, policy.ConcurrencyStamp);
    }

    public async Task<ProductionCapacityDto> SetProductionCapacityAsync(SetProductionCapacityRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HoursPerDay <= 0 || request.HoursPerDay > 10000)
            throw new ManufacturingValidationException(new Dictionary<string, string[]>
            { ["hoursPerDay"] = ["Daily production capacity must be greater than zero and no more than 10,000 hours."] });
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var (tenant, company) = RequiredScope();
        await LockProductionScheduleAsync(tenant, company, cancellationToken);
        var policy = await database.ProductionCapacityPolicies.SingleOrDefaultAsync(cancellationToken);
        if (policy is null)
        {
            if (!string.IsNullOrEmpty(request.ConcurrencyStamp)) throw new ManufacturingConcurrencyException();
            policy = new ProductionCapacityPolicy { Id = Guid.NewGuid(), TenantId = tenant, CompanyId = company,
                HoursPerDay = request.HoursPerDay, ConcurrencyStamp = NewStamp() };
            database.ProductionCapacityPolicies.Add(policy);
        }
        else
        {
            if (policy.ConcurrencyStamp != request.ConcurrencyStamp) throw new ManufacturingConcurrencyException();
            policy.HoursPerDay = request.HoursPerDay;
            policy.ConcurrencyStamp = NewStamp();
        }
        await SaveAsync(cancellationToken);
        var result = new ProductionCapacityDto(policy.HoursPerDay, policy.ConcurrencyStamp);
        await AuditAsync("manufacturing.capacity.updated", nameof(ProductionCapacityPolicy), policy.Id, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<BillOfMaterialResponse>> SearchBillOfMaterialsAsync(
        string? search, BillOfMaterialState? state, CancellationToken cancellationToken)
    {
        var query = database.BillsOfMaterial.AsNoTracking().Include(x => x.Components).AsQueryable();
        if (state.HasValue) query = query.Where(x => x.State == state);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim();
            query = query.Where(x => x.Number.Contains(value) || x.ProductSku.Contains(value) || x.ProductName.Contains(value));
        }
        var items = await query.OrderBy(x => x.Number).Take(200).ToListAsync(cancellationToken);
        return items.Select(ToResponse).ToArray();
    }

    public async Task<BillOfMaterialResponse?> GetBillOfMaterialAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await database.BillsOfMaterial.AsNoTracking().Include(x => x.Components)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return item is null ? null : ToResponse(item);
    }

    public async Task<BillOfMaterialResponse> CreateBillOfMaterialAsync(
        BillOfMaterialRequest request, CancellationToken cancellationToken)
    {
        var products = await ValidateBillOfMaterialAsync(request, cancellationToken);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var (tenant, company) = RequiredScope();
        var now = clock.UtcNow;
        var actor = CurrentUserId();
        var item = new BillOfMaterial
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            CompanyId = company,
            Number = NormalizeNumber(request.Number, "BOM"),
            ProductId = request.ProductId,
            ProductSku = products[request.ProductId].Sku,
            ProductName = products[request.ProductId].Name,
            Revision = NormalizeRequired(request.Revision, "revision", 30),
            OutputQuantity = request.OutputQuantity,
            OutputUnitCode = products[request.ProductId].SalesUnitCode,
            State = BillOfMaterialState.Draft,
            Notes = NormalizeOptional(request.Notes, 2000),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = actor,
            UpdatedBy = actor,
            ConcurrencyStamp = NewStamp()
        };
        item.Components = request.Components!.Select(line => new BillOfMaterialLine
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            CompanyId = company,
            BillOfMaterialId = item.Id,
            ComponentProductId = line.ComponentProductId,
            ComponentSku = products[line.ComponentProductId].Sku,
            ComponentName = products[line.ComponentProductId].Name,
            Quantity = line.Quantity,
            StandardUnitCost = line.StandardUnitCost,
            UnitCode = products[line.ComponentProductId].SalesUnitCode,
            Notes = NormalizeOptional(line.Notes, 1000),
            CreatedAtUtc = now,
            CreatedBy = actor
        }).ToList();
        if (await database.BillsOfMaterial.AnyAsync(x => x.Number == item.Number, cancellationToken))
            throw new ManufacturingConflictException("A bill of materials with that number already exists.");
        database.BillsOfMaterial.Add(item);
        await SaveAsync(cancellationToken);
        var result = ToResponse(item);
        await AuditAsync("manufacturing.bill-of-material.created", nameof(BillOfMaterial), item.Id, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<BillOfMaterialResponse> UpdateBillOfMaterialAsync(
        Guid id, BillOfMaterialRequest request, CancellationToken cancellationToken)
    {
        var products = await ValidateBillOfMaterialAsync(request, cancellationToken);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var item = await database.BillsOfMaterial.Include(x => x.Components)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        CheckStamp(item.ConcurrencyStamp, request.ConcurrencyStamp);
        if (item.State != BillOfMaterialState.Draft)
            throw new ManufacturingConflictException("Only a draft bill of materials can be edited.");
        var before = ToResponse(item);
        var number = NormalizeNumber(request.Number, "BOM");
        if (await database.BillsOfMaterial.AnyAsync(x => x.Id != id && x.Number == number, cancellationToken))
            throw new ManufacturingConflictException("A bill of materials with that number already exists.");
        var (tenant, company) = RequiredScope();
        var now = clock.UtcNow;
        var actor = CurrentUserId();
        database.BillOfMaterialLines.RemoveRange(item.Components);
        item.Number = number;
        item.ProductId = request.ProductId;
        item.ProductSku = products[request.ProductId].Sku;
        item.ProductName = products[request.ProductId].Name;
        item.Revision = NormalizeRequired(request.Revision, "revision", 30);
        item.OutputQuantity = request.OutputQuantity;
        item.OutputUnitCode = products[request.ProductId].SalesUnitCode;
        item.Notes = NormalizeOptional(request.Notes, 2000);
        item.UpdatedAtUtc = now;
        item.UpdatedBy = actor;
        item.ConcurrencyStamp = NewStamp();
        item.Components = request.Components!.Select(line => new BillOfMaterialLine
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            CompanyId = company,
            BillOfMaterialId = item.Id,
            ComponentProductId = line.ComponentProductId,
            ComponentSku = products[line.ComponentProductId].Sku,
            ComponentName = products[line.ComponentProductId].Name,
            Quantity = line.Quantity,
            StandardUnitCost = line.StandardUnitCost,
            UnitCode = products[line.ComponentProductId].SalesUnitCode,
            Notes = NormalizeOptional(line.Notes, 1000),
            CreatedAtUtc = now,
            CreatedBy = actor
        }).ToList();
        await SaveAsync(cancellationToken);
        var result = ToResponse(item);
        await AuditAsync("manufacturing.bill-of-material.updated", nameof(BillOfMaterial), item.Id, before, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<BillOfMaterialResponse> SetBillOfMaterialStateAsync(
        Guid id, BillOfMaterialState state, string? concurrencyStamp, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var item = await database.BillsOfMaterial.Include(x => x.Components)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new KeyNotFoundException();
        CheckStamp(item.ConcurrencyStamp, concurrencyStamp);
        var before = ToResponse(item);
        if (!((item.State == BillOfMaterialState.Draft && state == BillOfMaterialState.Active) ||
              (item.State == BillOfMaterialState.Active && state == BillOfMaterialState.Retired)))
            throw new ManufacturingConflictException("Bills of materials may only move from Draft to Active or Active to Retired.");
        item.State = state;
        item.UpdatedAtUtc = clock.UtcNow;
        item.UpdatedBy = CurrentUserId();
        item.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        var result = ToResponse(item);
        var action = state == BillOfMaterialState.Active
            ? "manufacturing.bill-of-material.activated"
            : "manufacturing.bill-of-material.retired";
        await AuditAsync(action, nameof(BillOfMaterial), item.Id, before, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<WorkOrderResponse>> SearchWorkOrdersAsync(
        WorkOrderState? state, CancellationToken cancellationToken)
    {
        var query = database.WorkOrders.AsNoTracking().AsQueryable();
        if (state.HasValue) query = query.Where(x => x.State == state);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).Take(200)
            .ToListAsync(cancellationToken);
        return items.Select(ToResponse).ToArray();
    }

    public async Task<WorkOrderResponse?> GetWorkOrderAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await database.WorkOrders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return item is null ? null : ToResponse(item);
    }

    public async Task<IReadOnlyList<WorkOrderMaterialAvailabilityResponse>> GetWorkOrderMaterialAvailabilityAsync(
        Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await database.WorkOrders.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == workOrderId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (workOrder.State is WorkOrderState.Completed or WorkOrderState.Cancelled) return [];
        if (workOrder.InventoryLocationId is not Guid locationId)
            throw new ManufacturingConflictException("Choose an inventory location to check component availability.");

        var bill = await database.BillsOfMaterial.AsNoTracking().Include(item => item.Components)
            .SingleOrDefaultAsync(item => item.Id == workOrder.BillOfMaterialId, cancellationToken)
            ?? throw new ManufacturingConflictException("The work order bill of materials is unavailable.");
        var requirements = MaterialRequirements(bill, workOrder.PlannedQuantity).ToDictionary(item => item.ProductId);
        var components = bill.Components.GroupBy(item => item.ComponentProductId)
            .ToDictionary(group => group.Key, group => new
            {
                Sku = group.First().ComponentSku,
                Name = group.First().ComponentName
            });
        var result = new List<WorkOrderMaterialAvailabilityResponse>(requirements.Count);
        foreach (var requirement in requirements.Values)
        {
            var balance = await stockAvailability.GetForProductionAsync(workOrderId, requirement.ProductId,
                locationId, cancellationToken);
            var shortage = Math.Max(0m, requirement.Quantity - balance.ReservedForWorkOrder - balance.Available);
            result.Add(new(requirement.ProductId, components[requirement.ProductId].Sku,
                components[requirement.ProductId].Name, requirement.Quantity, balance.OnHand, balance.Reserved,
                balance.ReservedForWorkOrder, balance.Available, shortage));
        }
        return result.OrderBy(item => item.ProductSku).ToArray();
    }

    public async Task<WorkOrderResponse> CreateWorkOrderAsync(
        CreateWorkOrderRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.BillOfMaterialId == Guid.Empty) errors["billOfMaterialId"] = ["A bill of materials is required."];
        if (request.PlannedQuantity <= 0) errors["plannedQuantity"] = ["Planned quantity must be greater than zero."];
        if (request.PlannedLaborHours <= 0) errors["plannedLaborHours"] = ["Planned labor hours must be greater than zero."];
        var costCurrency = request.CostCurrencyCode?.Trim().ToUpperInvariant();
        if (costCurrency is null || costCurrency.Length != 3 || costCurrency.Any(x => x is < 'A' or > 'Z'))
            errors["costCurrencyCode"] = ["Enter a three-letter ISO currency code for production costs."];
        if (request.InventoryLocationId is null || request.InventoryLocationId == Guid.Empty)
            errors["inventoryLocationId"] = ["Choose an active internal inventory location for material issue and finished output."];
        if (request.PlannedStartDate.HasValue && request.PlannedEndDate.HasValue &&
            request.PlannedEndDate < request.PlannedStartDate)
            errors["plannedEndDate"] = ["Planned end date cannot precede the start date."];
        if (errors.Count != 0) throw new ManufacturingValidationException(errors);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var bom = await database.BillsOfMaterial.AsNoTracking().Include(x => x.Components)
            .SingleOrDefaultAsync(x => x.Id == request.BillOfMaterialId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (bom.State != BillOfMaterialState.Active)
            throw new ManufacturingConflictException("Work orders can only be created from an active bill of materials.");
        var (tenant, company) = RequiredScope();
        var number = NormalizeNumber(request.Number, "WO");
        if (await database.WorkOrders.AnyAsync(x => x.Number == number, cancellationToken))
            throw new ManufacturingConflictException("A work order with that number already exists.");
        var now = clock.UtcNow;
        var actor = CurrentUserId();
        var item = new WorkOrder
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            CompanyId = company,
            Number = number,
            BillOfMaterialId = bom.Id,
            BillOfMaterialNumber = bom.Number,
            BillOfMaterialRevision = bom.Revision,
            ProductId = bom.ProductId,
            ProductSku = bom.ProductSku,
            ProductName = bom.ProductName,
            PlannedQuantity = request.PlannedQuantity,
            PlannedLaborHours = request.PlannedLaborHours,
            PlannedMaterialCost = decimal.Round(bom.Components.Sum(x => x.Quantity / bom.OutputQuantity *
                request.PlannedQuantity * x.StandardUnitCost), 2, MidpointRounding.AwayFromZero),
            CostCurrencyCode = costCurrency!,
            InventoryLocationId = request.InventoryLocationId,
            UnitCode = bom.OutputUnitCode,
            State = WorkOrderState.Draft,
            PlannedStartDate = request.PlannedStartDate,
            PlannedEndDate = request.PlannedEndDate,
            Notes = NormalizeOptional(request.Notes, 2000),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = actor,
            UpdatedBy = actor,
            ConcurrencyStamp = NewStamp()
        };
        database.WorkOrders.Add(item);
        await SaveAsync(cancellationToken);
        var result = ToResponse(item);
        await AuditAsync("manufacturing.work-order.created", nameof(WorkOrder), item.Id, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkOrderResponse> TransitionWorkOrderAsync(
        Guid id, ChangeWorkOrderStateRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var item = await database.WorkOrders.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        CheckStamp(item.ConcurrencyStamp, request.ConcurrencyStamp);
        var before = ToResponse(item);
        var state = request.State;
        var valid = (item.State, state) is
            (WorkOrderState.Draft, WorkOrderState.Released or WorkOrderState.Cancelled) or
            (WorkOrderState.Released, WorkOrderState.InProgress or WorkOrderState.Cancelled) or
            (WorkOrderState.InProgress, WorkOrderState.Completed or WorkOrderState.Cancelled);
        if (!valid) throw new ManufacturingConflictException($"Work order cannot move from {item.State} to {state}.");
        if (state == WorkOrderState.Released)
        {
            if (item.InventoryLocationId is not Guid reserveLocationId)
                throw new ManufacturingConflictException("Choose an inventory location before releasing this work order.");
            await ValidateProductionCapacityAsync(item, cancellationToken);
            var releaseBill = await database.BillsOfMaterial.AsNoTracking().Include(x => x.Components)
                .SingleOrDefaultAsync(x => x.Id == item.BillOfMaterialId, cancellationToken)
                ?? throw new ManufacturingConflictException("The work order bill of materials is unavailable.");
            try
            {
                await productionStock.ReserveProductionMaterialsAsync(item.Id, reserveLocationId,
                    MaterialRequirements(releaseBill, item.PlannedQuantity), cancellationToken);
            }
            catch (ProductionStockConflictException exception)
            {
                throw new ManufacturingConflictException(exception.Message);
            }
        }
        if (state == WorkOrderState.Cancelled)
        {
            try { await productionStock.ReleaseProductionMaterialsAsync(item.Id, cancellationToken); }
            catch (ProductionStockConflictException exception)
            { throw new ManufacturingConflictException(exception.Message); }
        }
        if (state == WorkOrderState.Completed)
        {
            var actualQuantity = request.ActualOutputQuantity ?? item.PlannedQuantity;
            if (actualQuantity <= 0) throw new ManufacturingValidationException(
                new Dictionary<string, string[]> { ["actualOutputQuantity"] = ["Actual output quantity must be greater than zero."] });
            var actualLaborHours = request.ActualLaborHours ?? item.PlannedLaborHours;
            var actualMaterialCost = request.ActualMaterialCost ?? item.PlannedMaterialCost;
            var actualLaborCost = request.ActualLaborCost ?? 0m;
            var actualOverheadCost = request.ActualOverheadCost ?? 0m;
            if (actualLaborHours < 0 || actualMaterialCost < 0 || actualLaborCost < 0 || actualOverheadCost < 0)
                throw new ManufacturingValidationException(new Dictionary<string, string[]>
                { ["costs"] = ["Actual hours and production costs cannot be negative."] });
            if (item.InventoryLocationId is not Guid locationId)
                throw new ManufacturingConflictException("This work order has no inventory location. Cancel it and create a new work order with a production location.");
            var bill = await database.BillsOfMaterial.AsNoTracking().Include(x => x.Components)
                .SingleOrDefaultAsync(x => x.Id == item.BillOfMaterialId, cancellationToken)
                ?? throw new ManufacturingConflictException("The work order bill of materials is unavailable.");
            var materialIssues = MaterialRequirements(bill, actualQuantity);
            try
            {
                await productionStock.CompleteProductionAsync(item.Id, locationId,
                    DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), materialIssues, item.ProductId, actualQuantity,
                    cancellationToken);
            }
            catch (ProductionStockConflictException exception)
            {
                throw new ManufacturingConflictException(exception.Message);
            }
            item.CompletedQuantity = actualQuantity;
            item.ActualLaborHours = actualLaborHours;
            item.ActualMaterialCost = decimal.Round(actualMaterialCost, 2, MidpointRounding.AwayFromZero);
            item.ActualLaborCost = decimal.Round(actualLaborCost, 2, MidpointRounding.AwayFromZero);
            item.ActualOverheadCost = decimal.Round(actualOverheadCost, 2, MidpointRounding.AwayFromZero);
            item.TotalProductionCost = decimal.Round(actualMaterialCost + actualLaborCost + actualOverheadCost,
                2, MidpointRounding.AwayFromZero);
        }
        var now = clock.UtcNow;
        var actor = CurrentUserId();
        database.WorkOrderStatusHistory.Add(new WorkOrderStatusHistory
        {
            Id = Guid.NewGuid(),
            TenantId = item.TenantId,
            CompanyId = item.CompanyId,
            WorkOrderId = item.Id,
            FromState = item.State,
            ToState = state,
            ChangedAtUtc = now,
            ChangedBy = actor,
            Notes = NormalizeOptional(request.Notes, 2000)
        });
        item.State = state;
        item.UpdatedAtUtc = now;
        item.UpdatedBy = actor;
        item.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        var result = ToResponse(item);
        await AuditAsync("manufacturing.work-order.state-changed", nameof(WorkOrder), item.Id, before, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<WorkOrderStatusChangeResponse>> GetWorkOrderHistoryAsync(
        Guid id, CancellationToken cancellationToken)
    {
        if (!await database.WorkOrders.AnyAsync(x => x.Id == id, cancellationToken))
            throw new KeyNotFoundException();
        return await database.WorkOrderStatusHistory.AsNoTracking()
            .Where(x => x.WorkOrderId == id)
            .OrderBy(x => x.ChangedAtUtc)
            .Select(x => new WorkOrderStatusChangeResponse(
                x.Id, x.FromState, x.ToState, x.ChangedAtUtc, x.ChangedBy, x.Notes))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QualityInspectionResponse>> GetInspectionsAsync(
        Guid workOrderId, CancellationToken cancellationToken)
    {
        if (!await database.WorkOrders.AnyAsync(x => x.Id == workOrderId, cancellationToken))
            throw new KeyNotFoundException();
        return await (from inspection in database.QualityInspections.AsNoTracking()
                      join order in database.WorkOrders.AsNoTracking() on inspection.WorkOrderId equals order.Id
                      where inspection.WorkOrderId == workOrderId
                      orderby inspection.InspectedAtUtc descending
                      select new QualityInspectionResponse(
                          inspection.Id, inspection.Number, inspection.WorkOrderId, order.Number,
                          inspection.SampleQuantity, inspection.Decision, inspection.InspectedAtUtc,
                          inspection.Inspector, inspection.Notes, inspection.CreatedAtUtc, inspection.CreatedBy))
            .ToListAsync(cancellationToken);
    }

    public async Task<QualityInspectionResponse> CreateInspectionAsync(
        Guid workOrderId, CreateQualityInspectionRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var workOrder = await database.WorkOrders.SingleOrDefaultAsync(x => x.Id == workOrderId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (workOrder.State is not (WorkOrderState.InProgress or WorkOrderState.Completed))
            throw new ManufacturingConflictException("Inspections can only be recorded for work orders in progress or completed.");
        if (request.SampleQuantity <= 0)
            throw new ManufacturingValidationException(new Dictionary<string, string[]>
            { ["sampleQuantity"] = ["Sample quantity must be greater than zero."] });
        if (!Enum.IsDefined(request.Decision))
            throw new ManufacturingValidationException(new Dictionary<string, string[]>
            { ["decision"] = ["A valid inspection decision is required."] });
        var number = NormalizeNumber(request.Number, "QI");
        if (await database.QualityInspections.AnyAsync(x => x.Number == number, cancellationToken))
            throw new ManufacturingConflictException("An inspection with that number already exists.");
        var now = clock.UtcNow;
        var item = new QualityInspection
        {
            Id = Guid.NewGuid(),
            TenantId = workOrder.TenantId,
            CompanyId = workOrder.CompanyId,
            WorkOrderId = workOrder.Id,
            Number = number,
            SampleQuantity = request.SampleQuantity,
            Decision = request.Decision,
            InspectedAtUtc = request.InspectedAtUtc?.ToUniversalTime() ?? now,
            Inspector = NormalizeOptional(request.Inspector, 200),
            Notes = NormalizeOptional(request.Notes, 2000),
            CreatedAtUtc = now,
            CreatedBy = CurrentUserId()
        };
        database.QualityInspections.Add(item);
        await SaveAsync(cancellationToken);
        var result = ToResponse(item, workOrder.Number);
        await AuditAsync("manufacturing.quality-inspection.created", nameof(QualityInspection), item.Id, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<IReadOnlyDictionary<Guid, ProductReference>> ValidateBillOfMaterialAsync(
        BillOfMaterialRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.ProductId == Guid.Empty) errors["productId"] = ["A finished product is required."];
        if (request.OutputQuantity <= 0) errors["outputQuantity"] = ["Output quantity must be greater than zero."];
        if (string.IsNullOrWhiteSpace(request.Revision) || request.Revision.Trim().Length > 30)
            errors["revision"] = ["Revision is required and must be at most 30 characters."];
        if (request.Components is null || request.Components.Count == 0)
            errors["components"] = ["At least one component is required."];
        else
        {
            if (request.Components.Count > 500) errors["components"] = ["A bill of materials cannot exceed 500 components."];
            if (request.Components.Any(x => x.ComponentProductId == Guid.Empty || x.Quantity <= 0 || x.StandardUnitCost < 0 ||
                                            string.IsNullOrWhiteSpace(x.UnitCode) || x.UnitCode.Trim().Length > 20))
                errors["components"] = ["Each component needs a product, positive quantity, and unit code of at most 20 characters."];
            if (request.Components.Any(x => x.ComponentProductId == request.ProductId))
                errors["components"] = ["A finished product cannot be its own component."];
        }
        if (errors.Count != 0) throw new ManufacturingValidationException(errors);
        var ids = request.Components!.Select(x => x.ComponentProductId).Append(request.ProductId).Distinct().ToArray();
        var products = await productDirectory.FindAsync(ids, cancellationToken);
        if (ids.Any(id => !products.TryGetValue(id, out var product) || product.IsArchived || !product.TracksInventory))
            throw new ManufacturingValidationException(new Dictionary<string, string[]>
            { ["products"] = ["All bill-of-material products must exist, be active, and track inventory in this company."] });
        if (request.Components!.Any(line =>
                !string.Equals(line.UnitCode?.Trim(), products[line.ComponentProductId].SalesUnitCode, StringComparison.OrdinalIgnoreCase)))
            throw new ManufacturingValidationException(new Dictionary<string, string[]>
            { ["components"] = ["Each component unit code must match that product's unit of measure."] });
        return products;
    }

    private async Task ValidateProductionCapacityAsync(WorkOrder candidate, CancellationToken cancellationToken)
    {
        if (candidate.PlannedStartDate is not DateOnly start || candidate.PlannedEndDate is not DateOnly end)
            throw new ManufacturingConflictException("Set planned start and end dates before releasing a work order into the capacity schedule.");
        var (tenant, company) = RequiredScope();
        await LockProductionScheduleAsync(tenant, company, cancellationToken);
        var capacity = await database.ProductionCapacityPolicies.AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        var dailyLimit = capacity?.HoursPerDay ?? 8m;
        var active = await database.WorkOrders.AsNoTracking().Where(x => x.Id != candidate.Id &&
            (x.State == WorkOrderState.Released || x.State == WorkOrderState.InProgress)).ToListAsync(cancellationToken);
        if (active.Any(x => x.PlannedStartDate is null || x.PlannedEndDate is null))
            throw new ManufacturingConflictException("An active work order has no schedule dates; resolve its schedule before releasing more production.");
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var used = active.Where(x => x.PlannedStartDate <= day && x.PlannedEndDate >= day)
                .Sum(x => x.PlannedLaborHours / (x.PlannedEndDate!.Value.DayNumber - x.PlannedStartDate!.Value.DayNumber + 1));
            var candidateDailyHours = candidate.PlannedLaborHours / (end.DayNumber - start.DayNumber + 1);
            if (used + candidateDailyHours > dailyLimit)
                throw new ManufacturingConflictException($"Production capacity would exceed {dailyLimit:0.##} labor hours on {day:yyyy-MM-dd}.");
        }
    }

    private async Task LockProductionScheduleAsync(Guid tenant, Guid company, CancellationToken cancellationToken)
    {
        var key = $"manufacturing-capacity:{tenant:N}:{company:N}";
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    private (Guid Tenant, Guid Company) RequiredScope() =>
        (tenantContext.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."),
         companyContext.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required."));

    private Guid? CurrentUserId() => Guid.TryParse(
        httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static string NormalizeNumber(string? value, string prefix) =>
        string.IsNullOrWhiteSpace(value) ? $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)] :
        NormalizeRequired(value, "number", 50);

    private static string NormalizeRequired(string? value, string key, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength)
            throw new ManufacturingValidationException(new Dictionary<string, string[]>
            { [key] = [$"{key} is required and must be at most {maxLength} characters."] });
        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maxLength)
            throw new ManufacturingValidationException(new Dictionary<string, string[]>
            { ["value"] = [$"Value cannot exceed {maxLength} characters."] });
        return normalized;
    }

    private static void CheckStamp(string current, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || current != expected)
            throw new ManufacturingConcurrencyException();
    }

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new ManufacturingConcurrencyException(); }
    }

    private async Task AuditAsync(
        string action, string entityType, Guid id, object? before, object after, CancellationToken cancellationToken)
    {
        audit.Append(new(action, entityType, id.ToString(), null,
            before is null ? null : JsonSerializer.Serialize(before), JsonSerializer.Serialize(after)));
        await audit.FlushAsync(cancellationToken);
    }

    private static ProductionMaterialIssue[] MaterialRequirements(BillOfMaterial bill, decimal outputQuantity) =>
        bill.Components.GroupBy(x => x.ComponentProductId)
            .Select(group => new ProductionMaterialIssue(group.Key,
                decimal.Round(group.Sum(x => x.Quantity) / bill.OutputQuantity * outputQuantity,
                    4, MidpointRounding.AwayFromZero)))
            .ToArray();

    private static BillOfMaterialResponse ToResponse(BillOfMaterial item) => new(
        item.Id, item.Number, item.ProductId, item.ProductSku, item.ProductName, item.Revision,
        item.OutputQuantity, item.OutputUnitCode, item.State, item.Notes, item.CreatedAtUtc,
        item.UpdatedAtUtc, item.CreatedBy, item.UpdatedBy, item.ConcurrencyStamp,
        item.Components.OrderBy(x => x.ComponentSku).Select(x => new BillOfMaterialLineResponse(
            x.ComponentProductId, x.ComponentSku, x.ComponentName, x.Quantity, x.UnitCode, x.Notes,
            x.StandardUnitCost)).ToArray());

    private static WorkOrderResponse ToResponse(WorkOrder item) => new(
        item.Id, item.Number, item.BillOfMaterialId, item.BillOfMaterialNumber, item.BillOfMaterialRevision,
        item.ProductId, item.ProductSku, item.ProductName, item.PlannedQuantity, item.UnitCode, item.State,
        item.PlannedStartDate, item.PlannedEndDate, item.Notes, item.CreatedAtUtc, item.UpdatedAtUtc,
        item.CreatedBy, item.UpdatedBy, item.ConcurrencyStamp, item.InventoryLocationId, item.CompletedQuantity,
        item.PlannedLaborHours, item.ActualLaborHours, item.TotalProductionCost, item.CostCurrencyCode,
        item.PlannedMaterialCost);

    private static QualityInspectionResponse ToResponse(QualityInspection item, string workOrderNumber) => new(
        item.Id, item.Number, item.WorkOrderId, workOrderNumber, item.SampleQuantity, item.Decision,
        item.InspectedAtUtc, item.Inspector, item.Notes, item.CreatedAtUtc, item.CreatedBy);
}
