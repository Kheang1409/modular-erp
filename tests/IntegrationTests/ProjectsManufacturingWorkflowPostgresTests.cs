using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Organization;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Manufacturing;
using Erp.Modules.Manufacturing.Contracts;
using Erp.Modules.Products.Contracts;
using Erp.Modules.Projects;
using Erp.Modules.Projects.Contracts;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.IntegrationTests;

public sealed class ProjectsManufacturingWorkflowPostgresTests
{
    [Fact]
    public async Task ProjectsPersistOperationalWorkflowAndRejectInvalidTransitionsAcrossCompanyScopes()
    {
        var postgres = await IntegrationContainers.StartPostgresAsync("projects_workflows");
        await using (postgres)
        {
            var tenant = Guid.NewGuid();
            var company = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<ProjectsDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            await using (var migration = new ProjectsDbContext(options)) await migration.Database.MigrateAsync();
            await using var session = new RelationalDatabaseSession(postgres.GetConnectionString());
            await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNpgsql(session.Connection).Options, new TenantContext(tenant));
            await using var database = new ProjectsDbContext(new DbContextOptionsBuilder<ProjectsDbContext>()
                .UseNpgsql(session.Connection).Options, new TenantContext(tenant), new CompanyContext(company));
            var audit = new RecordingAudit();
            var service = new ProjectsService(database, new TenantContext(tenant), new CompanyContext(company),
                new CompanyUsers(), new FixedClock(), audit, new HttpContextAccessor(), new BusinessTransactionCoordinator(platform));
            var token = CancellationToken.None;

            var project = await service.CreateProjectAsync(new("  ERP rollout  ", "  phased delivery ",
                ProjectStatus.Planned, new(2026, 11, 1), new(2026, 12, 31)), token);
            Assert.Equal("ERP rollout", project.Name);
            Assert.Equal("phased delivery", project.Description);
            Assert.Equal(project.Id, (await service.GetProjectAsync(project.Id, token))!.Id);
            Assert.Single(await service.ListProjectsAsync(token));

            var started = await service.UpdateProjectAsync(project.Id,
                new(project.Name, project.Description, ProjectStatus.Active, project.StartDate, project.EndDate, project.ConcurrencyStamp), token);
            await Assert.ThrowsAsync<ProjectsValidationException>(() => service.UpdateProjectAsync(project.Id,
                new(project.Name, project.Description, ProjectStatus.Planned, project.StartDate, project.EndDate, started.ConcurrencyStamp), token));
            Assert.Equal(ProjectStatus.Active, (await service.GetProjectAsync(project.Id, token))!.Status);

            var milestone = await service.SaveMilestoneAsync(null,
                new(project.Id, "Foundation", null, new(2026, 11, 20), ProjectWorkStatus.NotStarted), token);
            var milestoneProgress = await service.SaveMilestoneAsync(milestone.Id,
                new(project.Id, "Foundation", null, new(2026, 11, 20), ProjectWorkStatus.InProgress, milestone.ConcurrencyStamp), token);
            var completedMilestone = await service.SaveMilestoneAsync(milestone.Id,
                new(project.Id, "Foundation", null, new(2026, 11, 20), ProjectWorkStatus.Completed, milestoneProgress.ConcurrencyStamp), token);
            await Assert.ThrowsAsync<ProjectsValidationException>(() => service.SaveMilestoneAsync(milestone.Id,
                new(project.Id, "Foundation", null, new(2026, 11, 20), ProjectWorkStatus.InProgress, completedMilestone.ConcurrencyStamp), token));
            var task = await service.SaveTaskAsync(null,
                new(project.Id, milestone.Id, "Configure tenant", null, new(2026, 11, 18), ProjectWorkStatus.NotStarted, null), token);
            Assert.Single(await service.ListTasksAsync(project.Id, milestone.Id, token));
            var progressing = await service.SaveTaskAsync(task.Id,
                new(project.Id, milestone.Id, task.Title, task.Description, task.DueDate, ProjectWorkStatus.InProgress, null, task.ConcurrencyStamp), token);
            var completed = await service.SaveTaskAsync(task.Id,
                new(project.Id, milestone.Id, task.Title, task.Description, task.DueDate, ProjectWorkStatus.Completed, null, progressing.ConcurrencyStamp), token);
            await Assert.ThrowsAsync<ProjectsValidationException>(() => service.SaveTaskAsync(task.Id,
                new(project.Id, milestone.Id, task.Title, task.Description, task.DueDate, ProjectWorkStatus.InProgress, null, completed.ConcurrencyStamp), token));

            var entry = await service.SaveTimeEntryAsync(null,
                new(project.Id, task.Id, new(2026, 11, 14), 7.5m, true, "Implementation"), token);
            Assert.Equal(7.5m, Assert.Single(await service.ListTimeEntriesAsync(project.Id, new(2026, 11, 1), new(2026, 11, 30), token)).Hours);
            await Assert.ThrowsAsync<ProjectsValidationException>(() => service.SaveTimeEntryAsync(null,
                new(project.Id, task.Id, new(2026, 11, 14), 24.01m, false, null), token));
            await Assert.ThrowsAsync<ProjectsValidationException>(() => service.UpdateProjectAsync(project.Id,
                new(project.Name, project.Description, ProjectStatus.Planned, project.StartDate, project.EndDate, started.ConcurrencyStamp), token));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SaveTaskAsync(null,
                new(Guid.NewGuid(), null, "Orphan", null, null, ProjectWorkStatus.NotStarted, null), token));
            var onHold = await service.UpdateProjectAsync(project.Id,
                new(project.Name, project.Description, ProjectStatus.OnHold, project.StartDate, project.EndDate, started.ConcurrencyStamp), token);
            await using (var competingSession = new RelationalDatabaseSession(postgres.GetConnectionString()))
            await using (var competingPlatform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNpgsql(competingSession.Connection).Options, new TenantContext(tenant)))
            await using (var competingDatabase = new ProjectsDbContext(new DbContextOptionsBuilder<ProjectsDbContext>()
                .UseNpgsql(competingSession.Connection).Options, new TenantContext(tenant), new CompanyContext(company)))
            {
                var competingService = new ProjectsService(competingDatabase, new TenantContext(tenant), new CompanyContext(company),
                    new CompanyUsers(), new FixedClock(), new RecordingAudit(), new HttpContextAccessor(), new BusinessTransactionCoordinator(competingPlatform));
                await Assert.ThrowsAsync<ProjectsConcurrencyException>(() => competingService.UpdateProjectAsync(project.Id,
                    new("Stale edit", project.Description, ProjectStatus.Active, project.StartDate, project.EndDate, started.ConcurrencyStamp), token));
            }

            await using var otherCompany = new ProjectsDbContext(options, new TenantContext(tenant), new CompanyContext(Guid.NewGuid()));
            Assert.Empty(await otherCompany.Projects.ToListAsync());
            Assert.Empty(await otherCompany.TimeEntries.ToListAsync());
            await using var otherTenant = new ProjectsDbContext(options, new TenantContext(Guid.NewGuid()), new CompanyContext(company));
            Assert.Empty(await otherTenant.Projects.ToListAsync());
            await using var verify = new ProjectsDbContext(options, new TenantContext(tenant), new CompanyContext(company));
            Assert.Equal("ERP rollout", (await verify.Projects.SingleAsync()).Name);
            Assert.Equal(ProjectStatus.OnHold, (await verify.Projects.SingleAsync()).Status);
            Assert.Equal(entry.Id, (await verify.TimeEntries.SingleAsync()).Id);
            Assert.Contains(audit.Entries, x => x.Action == "projects.task.created");
        }
    }

    [Fact]
    public async Task ManufacturingPersistsBomOrderCapacityQualityAndScopedInventoryLifecycle()
    {
        var postgres = await IntegrationContainers.StartPostgresAsync("manufacturing_workflows");
        await using (postgres)
        {
            var tenant = Guid.NewGuid();
            var company = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<ManufacturingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            await using (var migration = new ManufacturingDbContext(options)) await migration.Database.MigrateAsync();
            await using var session = new RelationalDatabaseSession(postgres.GetConnectionString());
            await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNpgsql(session.Connection).Options, new TenantContext(tenant));
            await using var database = new ManufacturingDbContext(new DbContextOptionsBuilder<ManufacturingDbContext>()
                .UseNpgsql(session.Connection).Options, new TenantContext(tenant), new CompanyContext(company));
            var stock = new RecordingProductionStock();
            var products = new TestProductDirectory();
            var service = new ManufacturingService(database, new TenantContext(tenant), new CompanyContext(company),
                new FixedClock(), new HttpContextAccessor(), products, stock, new EmptyStockAvailability(),
                new RecordingAudit(), new BusinessTransactionCoordinator(platform));
            var token = CancellationToken.None;
            var output = products.Add("ASSEMBLY", "Assembly");
            var component = products.Add("BOLT", "Bolt");
            var location = Guid.NewGuid();

            var bom = await service.CreateBillOfMaterialAsync(new("BOM-100", output.Id, "A", 1,
                [new(component.Id, 2, "EA", null, 1.25m)], null), token);
            await Assert.ThrowsAsync<ManufacturingConflictException>(() =>
                service.SetBillOfMaterialStateAsync(bom.Id, BillOfMaterialState.Retired, bom.ConcurrencyStamp, token));
            var activeBom = await service.SetBillOfMaterialStateAsync(bom.Id, BillOfMaterialState.Active, bom.ConcurrencyStamp, token);
            var capacity = await service.SetProductionCapacityAsync(new(16, null), token);
            Assert.Equal(16, capacity.HoursPerDay);
            await Assert.ThrowsAsync<ManufacturingConcurrencyException>(() => service.SetProductionCapacityAsync(new(20, "stale"), token));
            var start = new DateOnly(2026, 11, 16);
            var order = await service.CreateWorkOrderAsync(new("WO-100", activeBom.Id, 10, start, start,
                null, location, 8, "USD"), token);
            Assert.Equal(25m, order.PlannedMaterialCost);
            await Assert.ThrowsAsync<ManufacturingConflictException>(() => service.TransitionWorkOrderAsync(order.Id,
                new(WorkOrderState.InProgress, null, order.ConcurrencyStamp), token));
            await Assert.ThrowsAsync<ManufacturingConflictException>(() => service.CreateInspectionAsync(order.Id,
                new("QI-EARLY", 1, InspectionDecision.Accepted, null, null, null), token));
            var released = await service.TransitionWorkOrderAsync(order.Id,
                new(WorkOrderState.Released, null, order.ConcurrencyStamp), token);
            Assert.Equal(WorkOrderState.Released, released.State);
            Assert.Equal(new ProductionMaterialIssue(component.Id, 20), Assert.Single(stock.Reservations.Single().Requirements));
            var overCapacityOrder = await service.CreateWorkOrderAsync(new("WO-OVER", activeBom.Id, 2, start, start,
                null, location, 9, "USD"), token);
            await Assert.ThrowsAsync<ManufacturingConflictException>(() => service.TransitionWorkOrderAsync(overCapacityOrder.Id,
                new(WorkOrderState.Released, null, overCapacityOrder.ConcurrencyStamp), token));
            Assert.DoesNotContain(stock.Reservations, x => x.WorkOrder == overCapacityOrder.Id);
            var inProgress = await service.TransitionWorkOrderAsync(order.Id,
                new(WorkOrderState.InProgress, "Started", released.ConcurrencyStamp), token);
            var inspection = await service.CreateInspectionAsync(order.Id,
                new("QI-100", 2, InspectionDecision.Accepted, null, "Inspector", null), token);
            Assert.Equal("WO-100", inspection.WorkOrderNumber);
            var completed = await service.TransitionWorkOrderAsync(order.Id,
                new(WorkOrderState.Completed, "Finished", inProgress.ConcurrencyStamp, 9, 7.5m, 22.25m, 10m, 2.5m), token);
            Assert.Equal(9m, completed.CompletedQuantity);
            Assert.Equal(34.75m, completed.TotalProductionCost);
            Assert.Equal(18m, Assert.Single(stock.Completions).Issues.Single().Quantity);
            Assert.Equal(output.Id, stock.Completions.Single().OutputProduct);
            await Assert.ThrowsAsync<ManufacturingConflictException>(() => service.TransitionWorkOrderAsync(order.Id,
                new(WorkOrderState.Cancelled, null, completed.ConcurrencyStamp), token));

            var cancelOrder = await service.CreateWorkOrderAsync(new("WO-101", activeBom.Id, 2, start, start,
                null, location, 2, "USD"), token);
            var cancelReleased = await service.TransitionWorkOrderAsync(cancelOrder.Id,
                new(WorkOrderState.Released, null, cancelOrder.ConcurrencyStamp), token);
            var cancelled = await service.TransitionWorkOrderAsync(cancelOrder.Id,
                new(WorkOrderState.Cancelled, "Customer request", cancelReleased.ConcurrencyStamp), token);
            Assert.Equal(WorkOrderState.Cancelled, cancelled.State);
            Assert.Equal([WorkOrderState.Released, WorkOrderState.Cancelled],
                (await service.GetWorkOrderHistoryAsync(cancelOrder.Id, token)).Select(x => x.ToState));
            Assert.Equal(1, stock.Releases.Count(x => x == cancelOrder.Id));
            await Assert.ThrowsAsync<ManufacturingConflictException>(() => service.TransitionWorkOrderAsync(cancelOrder.Id,
                new(WorkOrderState.Cancelled, null, cancelled.ConcurrencyStamp), token));
            Assert.Equal(1, stock.Releases.Count(x => x == cancelOrder.Id));

            await using var otherCompany = new ManufacturingDbContext(options, new TenantContext(tenant), new CompanyContext(Guid.NewGuid()));
            Assert.Empty(await otherCompany.WorkOrders.ToListAsync());
            await using var otherTenant = new ManufacturingDbContext(options, new TenantContext(Guid.NewGuid()), new CompanyContext(company));
            Assert.Empty(await otherTenant.WorkOrders.ToListAsync());
            await using var verify = new ManufacturingDbContext(options, new TenantContext(tenant), new CompanyContext(company));
            Assert.Equal(WorkOrderState.Completed, (await verify.WorkOrders.SingleAsync(x => x.Id == order.Id)).State);
            Assert.Single(await verify.QualityInspections.ToListAsync());
            Assert.Contains(WorkOrderState.Completed, (await service.GetWorkOrderHistoryAsync(order.Id, token)).Select(x => x.ToState));
        }
    }

    private sealed class TenantContext(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class CompanyContext(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero); }
    private sealed class RecordingAudit : IAuditLogWriter
    {
        public List<AuditWriteEntry> Entries { get; } = [];
        public void Append(AuditWriteEntry entry) => Entries.Add(entry);
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class CompanyUsers : ICompanyUserDirectory
    {
        public Task<CompanyUserReference?> FindAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<CompanyUserReference?>(null);
        public Task<IReadOnlyList<CompanyUserReference>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CompanyUserReference>>([]);
    }
    private sealed class TestProductDirectory : IProductDirectory
    {
        private readonly Dictionary<Guid, ProductReference> _products = [];
        public ProductReference Add(string sku, string name)
        {
            var product = new ProductReference(Guid.NewGuid(), ProductReferenceType.Product, sku, name, 0, "USD", "EA", true, false);
            _products.Add(product.Id, product);
            return product;
        }
        public Task<ProductReference?> FindAsync(Guid productId, CancellationToken cancellationToken) =>
            Task.FromResult(_products.GetValueOrDefault(productId));
        public Task<IReadOnlyDictionary<Guid, ProductReference>> FindAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ProductReference>>(productIds.Where(_products.ContainsKey).ToDictionary(id => id, id => _products[id]));
    }
    private sealed class RecordingProductionStock : IProductionStockWriter
    {
        public List<(Guid WorkOrder, Guid Location, IReadOnlyList<ProductionMaterialIssue> Requirements)> Reservations { get; } = [];
        public List<Guid> Releases { get; } = [];
        public List<(Guid WorkOrder, IReadOnlyList<ProductionMaterialIssue> Issues, Guid OutputProduct, decimal OutputQuantity)> Completions { get; } = [];
        public Task ReserveProductionMaterialsAsync(Guid workOrderId, Guid locationId, IReadOnlyList<ProductionMaterialIssue> materialRequirements, CancellationToken cancellationToken)
        { Reservations.Add((workOrderId, locationId, materialRequirements)); return Task.CompletedTask; }
        public Task ReleaseProductionMaterialsAsync(Guid workOrderId, CancellationToken cancellationToken)
        { Releases.Add(workOrderId); return Task.CompletedTask; }
        public Task CompleteProductionAsync(Guid workOrderId, Guid locationId, DateOnly businessDate, IReadOnlyList<ProductionMaterialIssue> materialIssues, Guid outputProductId, decimal outputQuantity, CancellationToken cancellationToken)
        { Completions.Add((workOrderId, materialIssues, outputProductId, outputQuantity)); return Task.CompletedTask; }
    }
    private sealed class EmptyStockAvailability : IStockAvailability
    {
        public Task<StockBalanceReference> GetAsync(Guid productId, Guid locationId, CancellationToken cancellationToken) =>
            Task.FromResult(new StockBalanceReference(productId, locationId, 0, 0, 0));
        public Task<ProductionStockAvailability> GetForProductionAsync(Guid workOrderId, Guid productId, Guid locationId,
            CancellationToken cancellationToken) => Task.FromResult(new ProductionStockAvailability(productId, locationId, 0, 0, 0, 0));
    }
}
