using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Modules.Products;

public sealed class ProductsDbContext(
    DbContextOptions<ProductsDbContext> options,
    ITenantContext? tenantContext = null,
    ICompanyContext? companyContext = null) : DbContext(options), ITransactionParticipant
{
    private readonly ITenantContext? _tenantContext = tenantContext;
    private readonly ICompanyContext? _companyContext = companyContext;
    private Guid TenantId => _tenantContext?.TenantId?.Value ?? Guid.Empty;
    private Guid CompanyId => _companyContext?.CompanyId?.Value ?? Guid.Empty;

    public DbSet<Product> Products => Set<Product>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();

    DbConnection ITransactionParticipant.Connection => Database.GetDbConnection();
    bool ITransactionParticipant.HasPendingChanges => ChangeTracker.HasChanges();

    async Task ITransactionParticipant.EnlistAsync(DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null && Database.CurrentTransaction is { } current &&
            !ReferenceEquals(current.GetDbTransaction(), transaction))
            throw new InvalidOperationException("An unrelated Products transaction is already active.");
        _ = await Database.UseTransactionAsync(transaction, cancellationToken);
    }

    void ITransactionParticipant.DiscardChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("products");
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("products", table =>
            {
                table.HasCheckConstraint("CK_products_Prices", "\"SalePrice\" >= 0 AND \"Cost\" >= 0");
                table.HasCheckConstraint("CK_products_ServiceInventory", "\"Type\" <> 'Service' OR NOT \"TracksInventory\"");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Sku).HasMaxLength(100);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.Barcode).HasMaxLength(100);
            entity.Property(x => x.SalePrice).HasPrecision(18, 4);
            entity.Property(x => x.Cost).HasPrecision(18, 4);
            entity.Property(x => x.CurrencyCode).HasMaxLength(3);
            entity.Property(x => x.SalesUnitCode).HasMaxLength(20);
            entity.Property(x => x.PurchaseUnitCode).HasMaxLength(20);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Sku }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Barcode }).IsUnique()
                .HasFilter("\"Barcode\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Name });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.SalesUnitCode).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.PurchaseUnitCode).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<UnitOfMeasure>(entity =>
        {
            entity.ToTable("units_of_measure", table => table.HasCheckConstraint(
                "CK_units_of_measure_Precision", "\"DecimalPrecision\" BETWEEN 0 AND 6"));
            entity.HasKey(x => x.Code);
            entity.Property(x => x.Code).HasMaxLength(20);
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.Category).HasMaxLength(50);
            entity.HasData(
                new UnitOfMeasure { Code = "EA", Name = "Each", Category = "Count", DecimalPrecision = 0, IsActive = true },
                new UnitOfMeasure { Code = "HUR", Name = "Hour", Category = "Time", DecimalPrecision = 2, IsActive = true },
                new UnitOfMeasure { Code = "KGM", Name = "Kilogram", Category = "Mass", DecimalPrecision = 3, IsActive = true },
                new UnitOfMeasure { Code = "MTR", Name = "Metre", Category = "Length", DecimalPrecision = 3, IsActive = true });
        });
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var changes = ChangeTracker.Entries<Product>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (changes.Length > 0)
        {
            var tenant = _tenantContext?.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
            var company = _companyContext?.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
            if (changes.Any(x => x.Entity.TenantId != tenant || x.Entity.CompanyId != company))
                throw new InvalidOperationException("Cross-tenant or cross-company product writes are not permitted.");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
