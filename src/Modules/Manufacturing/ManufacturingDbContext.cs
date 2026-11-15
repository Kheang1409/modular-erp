using System.Data.Common;
using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Modules.Manufacturing;

public sealed class ManufacturingDbContext(
    DbContextOptions<ManufacturingDbContext> options,
    ITenantContext? tenantContext = null,
    ICompanyContext? companyContext = null) : DbContext(options), ITransactionParticipant
{
    private readonly ITenantContext? _tenantContext = tenantContext;
    private readonly ICompanyContext? _companyContext = companyContext;
    private Guid TenantId => _tenantContext?.TenantId?.Value ?? Guid.Empty;
    private Guid CompanyId => _companyContext?.CompanyId?.Value ?? Guid.Empty;

    public DbSet<BillOfMaterial> BillsOfMaterial => Set<BillOfMaterial>();
    public DbSet<BillOfMaterialLine> BillOfMaterialLines => Set<BillOfMaterialLine>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderStatusHistory> WorkOrderStatusHistory => Set<WorkOrderStatusHistory>();
    public DbSet<QualityInspection> QualityInspections => Set<QualityInspection>();
    public DbSet<ProductionCapacityPolicy> ProductionCapacityPolicies => Set<ProductionCapacityPolicy>();

    DbConnection ITransactionParticipant.Connection => Database.GetDbConnection();
    bool ITransactionParticipant.HasPendingChanges => ChangeTracker.HasChanges();

    async Task ITransactionParticipant.EnlistAsync(DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null && Database.CurrentTransaction is { } current &&
            !ReferenceEquals(current.GetDbTransaction(), transaction))
            throw new InvalidOperationException("An unrelated Manufacturing transaction is already active.");
        _ = await Database.UseTransactionAsync(transaction, cancellationToken);
    }

    void ITransactionParticipant.DiscardChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("manufacturing");
        modelBuilder.Entity<BillOfMaterial>(entity =>
        {
            entity.ToTable("bills_of_material", table =>
            {
                table.HasCheckConstraint("CK_bills_of_material_output_quantity", "\"OutputQuantity\">0");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Number).HasMaxLength(50);
            entity.Property(x => x.ProductSku).HasMaxLength(100);
            entity.Property(x => x.ProductName).HasMaxLength(200);
            entity.Property(x => x.Revision).HasMaxLength(30);
            entity.Property(x => x.OutputQuantity).HasPrecision(18, 4);
            entity.Property(x => x.OutputUnitCode).HasMaxLength(20);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Notes).HasMaxLength(2000);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Number }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
        modelBuilder.Entity<BillOfMaterialLine>(entity =>
        {
            entity.ToTable("bill_of_material_lines", table =>
                table.HasCheckConstraint("CK_bill_of_material_lines_quantity", "\"Quantity\">0"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.ComponentSku).HasMaxLength(100);
            entity.Property(x => x.ComponentName).HasMaxLength(200);
            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.StandardUnitCost).HasPrecision(18, 4);
            entity.Property(x => x.UnitCode).HasMaxLength(20);
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BillOfMaterialId });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<BillOfMaterial>().WithMany(x => x.Components)
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BillOfMaterialId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WorkOrder>(entity =>
        {
            entity.ToTable("work_orders", table =>
            {
                table.HasCheckConstraint("CK_work_orders_planned_quantity", "\"PlannedQuantity\">0");
                table.HasCheckConstraint("CK_work_orders_completed_quantity", "\"CompletedQuantity\" IS NULL OR \"CompletedQuantity\" > 0");
                table.HasCheckConstraint("CK_work_orders_planned_labor_hours", "\"PlannedLaborHours\" > 0");
                table.HasCheckConstraint("CK_work_orders_planned_material_cost", "\"PlannedMaterialCost\" >= 0");
                table.HasCheckConstraint("CK_work_orders_actual_labor_hours", "\"ActualLaborHours\" IS NULL OR \"ActualLaborHours\" >= 0");
                table.HasCheckConstraint("CK_work_orders_costs_nonnegative", "(\"ActualMaterialCost\" IS NULL OR \"ActualMaterialCost\" >= 0) AND (\"ActualLaborCost\" IS NULL OR \"ActualLaborCost\" >= 0) AND (\"ActualOverheadCost\" IS NULL OR \"ActualOverheadCost\" >= 0) AND (\"TotalProductionCost\" IS NULL OR \"TotalProductionCost\" >= 0)");
                table.HasCheckConstraint("CK_work_orders_planned_dates", "\"PlannedEndDate\" IS NULL OR \"PlannedStartDate\" IS NULL OR \"PlannedEndDate\">=\"PlannedStartDate\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Number).HasMaxLength(50);
            entity.Property(x => x.BillOfMaterialNumber).HasMaxLength(50);
            entity.Property(x => x.BillOfMaterialRevision).HasMaxLength(30);
            entity.Property(x => x.ProductSku).HasMaxLength(100);
            entity.Property(x => x.ProductName).HasMaxLength(200);
            entity.Property(x => x.PlannedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.PlannedLaborHours).HasPrecision(12, 2);
            entity.Property(x => x.PlannedMaterialCost).HasPrecision(18, 2);
            entity.Property(x => x.CostCurrencyCode).HasMaxLength(3).IsFixedLength();
            entity.Property(x => x.CompletedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ActualLaborHours).HasPrecision(12, 2);
            entity.Property(x => x.ActualMaterialCost).HasPrecision(18, 2);
            entity.Property(x => x.ActualLaborCost).HasPrecision(18, 2);
            entity.Property(x => x.ActualOverheadCost).HasPrecision(18, 2);
            entity.Property(x => x.TotalProductionCost).HasPrecision(18, 2);
            entity.Property(x => x.UnitCode).HasMaxLength(20);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Notes).HasMaxLength(2000);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Number }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.State, x.CreatedAtUtc });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<BillOfMaterial>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BillOfMaterialId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<WorkOrderStatusHistory>(entity =>
        {
            entity.ToTable("work_order_status_history");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FromState).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ToState).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Notes).HasMaxLength(2000);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.WorkOrderId, x.ChangedAtUtc });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<WorkOrder>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.WorkOrderId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<QualityInspection>(entity =>
        {
            entity.ToTable("quality_inspections", table =>
                table.HasCheckConstraint("CK_quality_inspections_sample_quantity", "\"SampleQuantity\">0"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Number).HasMaxLength(50);
            entity.Property(x => x.SampleQuantity).HasPrecision(18, 4);
            entity.Property(x => x.Decision).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Inspector).HasMaxLength(200);
            entity.Property(x => x.Notes).HasMaxLength(2000);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Number }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.WorkOrderId, x.InspectedAtUtc });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<WorkOrder>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.WorkOrderId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProductionCapacityPolicy>(entity =>
        {
            entity.ToTable("production_capacity_policies", table =>
                table.HasCheckConstraint("CK_manufacturing_capacity_hours", "\"HoursPerDay\" > 0 AND \"HoursPerDay\" <= 10000"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.HoursPerDay).HasPrecision(12, 2);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var changed = ChangeTracker.Entries()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (changed.Length > 0)
        {
            var tenant = _tenantContext?.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
            var company = _companyContext?.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
            if (changed.Any(x => Scope(x.Entity) != (tenant, company)))
                throw new InvalidOperationException("Cross-tenant or cross-company manufacturing writes are not permitted.");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private static (Guid Tenant, Guid Company) Scope(object entity) => entity switch
    {
        BillOfMaterial x => (x.TenantId, x.CompanyId),
        BillOfMaterialLine x => (x.TenantId, x.CompanyId),
        WorkOrder x => (x.TenantId, x.CompanyId),
        WorkOrderStatusHistory x => (x.TenantId, x.CompanyId),
        QualityInspection x => (x.TenantId, x.CompanyId),
        ProductionCapacityPolicy x => (x.TenantId, x.CompanyId),
        _ => default
    };
}

public sealed class ManufacturingDesignTimeFactory : IDesignTimeDbContextFactory<ManufacturingDbContext>
{
    public ManufacturingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ManufacturingDbContext>()
            .UseNpgsql("Host=localhost;Database=erp_design;Username=erp;Password=design")
            .Options);
}
