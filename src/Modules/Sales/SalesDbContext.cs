using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Modules.Sales;

public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options, ITenantContext? tenant = null, ICompanyContext? company = null) : DbContext(options), ITransactionParticipant
{
    private readonly ITenantContext? _tenant = tenant;
    private readonly ICompanyContext? _company = company;
    private Guid TenantId => _tenant?.TenantId?.Value ?? Guid.Empty;
    private Guid CompanyId => _company?.CompanyId?.Value ?? Guid.Empty;
    public DbSet<Quotation> Quotations => Set<Quotation>();
    public DbSet<QuotationLine> QuotationLines => Set<QuotationLine>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();

    DbConnection ITransactionParticipant.Connection => Database.GetDbConnection();
    bool ITransactionParticipant.HasPendingChanges => ChangeTracker.HasChanges();

    async Task ITransactionParticipant.EnlistAsync(DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null && Database.CurrentTransaction is { } current &&
            !ReferenceEquals(current.GetDbTransaction(), transaction))
            throw new InvalidOperationException("An unrelated Sales transaction is already active.");
        _ = await Database.UseTransactionAsync(transaction, cancellationToken);
    }

    void ITransactionParticipant.DiscardChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("sales");
        modelBuilder.Entity<Quotation>(entity =>
        {
            entity.ToTable("quotations", table => table.HasCheckConstraint("CK_quotations_totals", "\"Subtotal\" >= 0 AND \"DiscountTotal\" >= 0 AND \"TaxTotal\" >= 0 AND \"Total\" >= 0 AND \"ExchangeRate\" > 0"));
            entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id }); entity.Property(x => x.Number).HasMaxLength(50); entity.Property(x => x.CustomerName).HasMaxLength(200);
            entity.Property(x => x.CurrencyCode).HasMaxLength(3); entity.Property(x => x.BaseCurrencyCode).HasMaxLength(3); entity.Property(x => x.ExchangeRate).HasPrecision(18, 8);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(20); entity.Property(x => x.Subtotal).HasPrecision(18, 4); entity.Property(x => x.DiscountTotal).HasPrecision(18, 4); entity.Property(x => x.TaxTotal).HasPrecision(18, 4); entity.Property(x => x.Total).HasPrecision(18, 4);
            entity.Property(x => x.Notes).HasMaxLength(4000); entity.Property(x => x.Terms).HasMaxLength(4000); entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Number }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.CustomerId }); entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.State, x.UpdatedAtUtc });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasMany(x => x.Lines).WithOne().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.QuotationId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<QuotationLine>(entity =>
        {
            entity.ToTable("quotation_lines", table => table.HasCheckConstraint("CK_quotation_lines_values", "\"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND \"DiscountPercent\" BETWEEN 0 AND 100 AND \"TaxPercent\" BETWEEN 0 AND 100 AND \"Total\" >= 0"));
            entity.HasKey(x => x.Id); entity.Property(x => x.ProductSku).HasMaxLength(100); entity.Property(x => x.Description).HasMaxLength(500); entity.Property(x => x.UnitCode).HasMaxLength(20);
            foreach (var property in new[] { nameof(QuotationLine.Quantity), nameof(QuotationLine.UnitPrice), nameof(QuotationLine.DiscountPercent), nameof(QuotationLine.TaxPercent), nameof(QuotationLine.Subtotal), nameof(QuotationLine.DiscountAmount), nameof(QuotationLine.TaxAmount), nameof(QuotationLine.Total) }) entity.Property(property).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.QuotationId, x.Sequence }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
        modelBuilder.Entity<SalesOrder>(entity =>
        {
            entity.ToTable("sales_orders", table => table.HasCheckConstraint("CK_sales_orders_totals", "\"Subtotal\" >= 0 AND \"DiscountTotal\" >= 0 AND \"TaxTotal\" >= 0 AND \"Total\" >= 0 AND \"ExchangeRate\" > 0"));
            entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id }); entity.Property(x => x.Number).HasMaxLength(50); entity.Property(x => x.CustomerName).HasMaxLength(200); entity.Property(x => x.CustomerTaxNumber).HasMaxLength(100);
            foreach (var property in new[] { nameof(SalesOrder.BillingLine1), nameof(SalesOrder.BillingLine2), nameof(SalesOrder.BillingCity), nameof(SalesOrder.BillingStateOrProvince), nameof(SalesOrder.BillingPostalCode), nameof(SalesOrder.ShippingLine1), nameof(SalesOrder.ShippingLine2), nameof(SalesOrder.ShippingCity), nameof(SalesOrder.ShippingStateOrProvince), nameof(SalesOrder.ShippingPostalCode) }) entity.Property(property).HasMaxLength(200);
            entity.Property(x => x.BillingCountryCode).HasMaxLength(2); entity.Property(x => x.ShippingCountryCode).HasMaxLength(2); entity.Property(x => x.CurrencyCode).HasMaxLength(3); entity.Property(x => x.BaseCurrencyCode).HasMaxLength(3); entity.Property(x => x.ExchangeRate).HasPrecision(18, 8); entity.Property(x => x.State).HasConversion<string>().HasMaxLength(24);
            foreach (var property in new[] { nameof(SalesOrder.Subtotal), nameof(SalesOrder.DiscountTotal), nameof(SalesOrder.TaxTotal), nameof(SalesOrder.Total) }) entity.Property(property).HasPrecision(18, 4);
            entity.Property(x => x.Notes).HasMaxLength(4000); entity.Property(x => x.Terms).HasMaxLength(4000); entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Number }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.SourceQuotationId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.State, x.UpdatedAtUtc }); entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasMany(x => x.Lines).WithOne().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.SalesOrderId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SalesOrderLine>(entity =>
        {
            entity.ToTable("sales_order_lines", table => table.HasCheckConstraint("CK_sales_order_lines_values", "\"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND \"DiscountPercent\" BETWEEN 0 AND 100 AND \"TaxPercent\" BETWEEN 0 AND 100 AND \"Total\" >= 0")); entity.HasKey(x => x.Id); entity.Property(x => x.ProductSku).HasMaxLength(100); entity.Property(x => x.Description).HasMaxLength(500); entity.Property(x => x.UnitCode).HasMaxLength(20);
            foreach (var property in new[] { nameof(SalesOrderLine.Quantity), nameof(SalesOrderLine.UnitPrice), nameof(SalesOrderLine.DiscountPercent), nameof(SalesOrderLine.TaxPercent), nameof(SalesOrderLine.Subtotal), nameof(SalesOrderLine.DiscountAmount), nameof(SalesOrderLine.TaxAmount), nameof(SalesOrderLine.Total) }) entity.Property(property).HasPrecision(18, 4); entity.HasIndex(x => new { x.SalesOrderId, x.Sequence }).IsUnique(); entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var scoped = ChangeTracker.Entries().Where(x => x.Entity is Quotation or QuotationLine or SalesOrder or SalesOrderLine && x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).Select(x => x.Entity).ToArray();
        if (scoped.Length > 0)
        {
            var tenant = _tenant?.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); var company = _company?.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
            if (scoped.Any(x => x switch { Quotation q => q.TenantId != tenant || q.CompanyId != company, QuotationLine l => l.TenantId != tenant || l.CompanyId != company, SalesOrder o => o.TenantId != tenant || o.CompanyId != company, SalesOrderLine l => l.TenantId != tenant || l.CompanyId != company, _ => false })) throw new InvalidOperationException("Cross-tenant or cross-company sales writes are not permitted.");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}

public sealed class SalesDesignTimeFactory : IDesignTimeDbContextFactory<SalesDbContext>
{
    public SalesDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SalesDbContext>()
        .UseNpgsql("Host=localhost;Database=erp_design;Username=erp;Password=design_only").Options);
}
