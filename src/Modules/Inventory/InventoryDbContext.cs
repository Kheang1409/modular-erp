using Erp.Abstractions.Context;
#pragma warning disable CA1725
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Erp.Modules.Inventory;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options, ITenantContext? tenant = null, ICompanyContext? company = null) : DbContext(options)
{
    private Guid TenantId => tenant?.TenantId?.Value ?? Guid.Empty;
    private Guid CompanyId => company?.CompanyId?.Value ?? Guid.Empty;
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<StockLocation> Locations => Set<StockLocation>();
    public DbSet<StockMove> StockMoves => Set<StockMove>();
    public DbSet<StockReservation> Reservations => Set<StockReservation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("inventory");
        b.Entity<Warehouse>(e => { e.ToTable("warehouses"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id }); e.Property(x => x.Code).HasMaxLength(50); e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken(); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique(); e.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId); });
        b.Entity<StockLocation>(e => { e.ToTable("locations"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id }); e.Property(x => x.Code).HasMaxLength(50); e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.Type).HasConversion<string>().HasMaxLength(24); e.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken(); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique(); e.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId); e.HasOne<Warehouse>().WithMany(x => x.Locations).HasForeignKey(x => new { x.TenantId, x.CompanyId, x.WarehouseId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<StockMove>(e => { e.ToTable("stock_moves", t => t.HasCheckConstraint("CK_stock_moves_quantity", "\"Quantity\">0")); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id }); e.Property(x => x.Number).HasMaxLength(50); e.Property(x => x.Type).HasConversion<string>().HasMaxLength(24); e.Property(x => x.State).HasConversion<string>().HasMaxLength(24); e.Property(x => x.ProductSku).HasMaxLength(100); e.Property(x => x.UnitCode).HasMaxLength(20); e.Property(x => x.Quantity).HasPrecision(18, 4); e.Property(x => x.ReferenceDocumentType).HasMaxLength(50); e.Property(x => x.Notes).HasMaxLength(2000); e.Property(x => x.CompletionKey).HasMaxLength(100); e.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken(); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.Number }).IsUnique(); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.CompletionKey }).IsUnique().HasFilter("\"CompletionKey\" IS NOT NULL"); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.ProductId, x.State }); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.ReversesMoveId }).IsUnique().HasFilter("\"ReversesMoveId\" IS NOT NULL"); e.HasOne<StockMove>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.ReversesMoveId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict); e.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId); });
        b.Entity<StockReservation>(e => { e.ToTable("reservations", t => { t.HasCheckConstraint("CK_reservations_quantity", "\"Quantity\">0"); t.HasCheckConstraint("CK_reservations_consumed_quantity", "\"ConsumedQuantity\">=0 AND \"ConsumedQuantity\"<=\"Quantity\""); }); e.HasKey(x => x.Id); e.Property(x => x.Quantity).HasPrecision(18, 4); e.Property(x => x.ConsumedQuantity).HasPrecision(18, 4); e.Property(x => x.State).HasConversion<string>().HasMaxLength(24); e.Property(x => x.IdempotencyKey).HasMaxLength(100); e.Property(x => x.ReleaseKey).HasMaxLength(100); e.Property(x => x.ReferenceDocumentType).HasMaxLength(50); e.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken(); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.IdempotencyKey }).IsUnique(); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.ReleaseKey }).IsUnique().HasFilter("\"ReleaseKey\" IS NOT NULL"); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.ProductId, x.LocationId, x.State }); e.HasIndex(x => new { x.TenantId, x.CompanyId, x.ReferenceDocumentId, x.State }); e.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId); });
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken token = default)
    {
        var changed = ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (changed.Length > 0)
        {
            var t = tenant?.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
            var c = company?.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
            if (changed.Any(x => Scope(x.Entity) is var s && s != (t, c))) throw new InvalidOperationException("Cross-tenant or cross-company inventory writes are not permitted.");
            if (changed.Any(x => x.Entity is StockMove && x.State != EntityState.Added && x.OriginalValues.GetValue<StockMoveState>(nameof(StockMove.State)) == StockMoveState.Completed)) throw new InvalidOperationException("Completed stock moves are immutable; create a compensating move.");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, token);
    }
    private static (Guid, Guid) Scope(object x) => x switch { Warehouse v => (v.TenantId, v.CompanyId), StockLocation v => (v.TenantId, v.CompanyId), StockMove v => (v.TenantId, v.CompanyId), StockReservation v => (v.TenantId, v.CompanyId), _ => default };
}
public sealed class InventoryDesignTimeFactory : IDesignTimeDbContextFactory<InventoryDbContext> { public InventoryDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql("Host=localhost;Database=erp_design;Username=erp;Password=design_only").Options); }
