using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Modules.Crm;

public sealed class CrmDbContext(DbContextOptions<CrmDbContext> options, ITenantContext? tenant = null,
    ICompanyContext? company = null) : DbContext(options), ITransactionParticipant
{
    private Guid TenantId => tenant?.TenantId?.Value ?? Guid.Empty;
    private Guid CompanyId => company?.CompanyId?.Value ?? Guid.Empty;
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<CrmActivity> Activities => Set<CrmActivity>();

    DbConnection ITransactionParticipant.Connection => Database.GetDbConnection();
    bool ITransactionParticipant.HasPendingChanges => ChangeTracker.HasChanges();

    async Task ITransactionParticipant.EnlistAsync(DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null && Database.CurrentTransaction is { } current &&
            !ReferenceEquals(current.GetDbTransaction(), transaction))
            throw new InvalidOperationException("An unrelated CRM transaction is already active.");
        _ = await Database.UseTransactionAsync(transaction, cancellationToken);
    }

    void ITransactionParticipant.DiscardChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("crm");
        modelBuilder.Entity<Lead>(entity =>
        {
            entity.ToTable("leads", table =>
            {
                table.HasCheckConstraint("CK_leads_Revenue", "\"ExpectedRevenue\" >= 0");
                table.HasCheckConstraint("CK_leads_Probability", "\"Probability\" BETWEEN 0 AND 100");
                table.HasCheckConstraint("CK_leads_LostReason", "\"State\" <> 'Lost' OR \"LostReason\" IS NOT NULL");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Email).HasMaxLength(254);
            entity.Property(x => x.Phone).HasMaxLength(50);
            entity.Property(x => x.Source).HasMaxLength(100);
            entity.Property(x => x.ExpectedRevenue).HasPrecision(18, 4);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.LostReason).HasMaxLength(500);
            entity.Property(x => x.Notes).HasMaxLength(4000);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.State, x.IsArchived });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.AssignedUserId });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ContactId });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
        modelBuilder.Entity<PipelineStage>(entity =>
        {
            entity.ToTable("pipeline_stages", table =>
            {
                table.HasCheckConstraint("CK_pipeline_stages_Probability", "\"DefaultProbability\" BETWEEN 0 AND 100");
                table.HasCheckConstraint("CK_pipeline_stages_Terminal", "NOT (\"IsWon\" AND \"IsLost\")");
            });
            entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(100); entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Name }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Sequence }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
        modelBuilder.Entity<Opportunity>(entity =>
        {
            entity.ToTable("opportunities", table =>
            {
                table.HasCheckConstraint("CK_opportunities_Revenue", "\"ExpectedRevenue\" >= 0");
                table.HasCheckConstraint("CK_opportunities_Probability", "\"Probability\" BETWEEN 0 AND 100");
                table.HasCheckConstraint("CK_opportunities_LostReason", "\"State\" <> 'Lost' OR \"LostReason\" IS NOT NULL");
            });
            entity.HasKey(x => x.Id); entity.Property(x => x.Title).HasMaxLength(200);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.ExpectedRevenue).HasPrecision(18, 4); entity.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.LostReason).HasMaxLength(500); entity.Property(x => x.Notes).HasMaxLength(4000);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.State, x.IsArchived });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ContactId });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.SourceLeadId }).IsUnique().HasFilter("\"SourceLeadId\" IS NOT NULL");
            entity.HasOne<PipelineStage>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.StageId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
        modelBuilder.Entity<CrmActivity>(entity =>
        {
            entity.ToTable("activities", table => table.HasCheckConstraint("CK_activities_Parent", "(\"LeadId\" IS NOT NULL)::int + (\"OpportunityId\" IS NOT NULL)::int = 1"));
            entity.HasKey(x => x.Id); entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Subject).HasMaxLength(200); entity.Property(x => x.Details).HasMaxLength(4000);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.LeadId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.OpportunityId, x.CreatedAtUtc });
            entity.HasOne<Lead>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.LeadId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Opportunity>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.OpportunityId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var changes = ChangeTracker.Entries().Where(x => x.Entity is Lead or PipelineStage or Opportunity or CrmActivity)
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (changes.Length > 0)
        {
            var tenantId = tenant?.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
            var companyId = company?.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
            if (changes.Any(x => Scope(x.Entity) != (tenantId, companyId)))
                throw new InvalidOperationException("Cross-tenant or cross-company CRM writes are not permitted.");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    private static (Guid Tenant, Guid Company) Scope(object value) => value switch { Lead x => (x.TenantId, x.CompanyId), PipelineStage x => (x.TenantId, x.CompanyId), Opportunity x => (x.TenantId, x.CompanyId), CrmActivity x => (x.TenantId, x.CompanyId), _ => default };
}

public sealed class CrmDesignTimeFactory : IDesignTimeDbContextFactory<CrmDbContext>
{
    public CrmDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CrmDbContext>()
        .UseNpgsql("Host=localhost;Database=erp_design;Username=erp;Password=design_only").Options);
}
