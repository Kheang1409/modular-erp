using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Modules.Projects;

public sealed class ProjectsDbContext(
    DbContextOptions<ProjectsDbContext> options,
    ITenantContext? tenantContext = null,
    ICompanyContext? companyContext = null) : DbContext(options), ITransactionParticipant
{
    private readonly ITenantContext? _tenantContext = tenantContext;
    private readonly ICompanyContext? _companyContext = companyContext;
    private Guid TenantId => _tenantContext?.TenantId?.Value ?? Guid.Empty;
    private Guid CompanyId => _companyContext?.CompanyId?.Value ?? Guid.Empty;

    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<ProjectMilestone> Milestones => Set<ProjectMilestone>();
    public DbSet<ProjectTask> Tasks => Set<ProjectTask>();
    public DbSet<ProjectTimeEntry> TimeEntries => Set<ProjectTimeEntry>();

    DbConnection ITransactionParticipant.Connection => Database.GetDbConnection();
    bool ITransactionParticipant.HasPendingChanges => ChangeTracker.HasChanges();

    async Task ITransactionParticipant.EnlistAsync(DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null && Database.CurrentTransaction is { } current &&
            !ReferenceEquals(current.GetDbTransaction(), transaction))
            throw new InvalidOperationException("An unrelated Projects transaction is already active.");
        _ = await Database.UseTransactionAsync(transaction, cancellationToken);
    }

    void ITransactionParticipant.DiscardChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("projects");
        modelBuilder.Entity<ProjectRecord>(entity =>
        {
            ConfigureBase(entity, "projects");
            entity.ToTable("projects", table =>
                table.HasCheckConstraint("CK_projects_DateRange", "\"EndDate\" IS NULL OR \"StartDate\" IS NULL OR \"EndDate\" >= \"StartDate\""));
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Name });
        });
        modelBuilder.Entity<ProjectMilestone>(entity =>
        {
            ConfigureBase(entity, "project_milestones");
            entity.ToTable("project_milestones");
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ProjectId });
            entity.HasOne<ProjectRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.ProjectId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ProjectTask>(entity =>
        {
            ConfigureBase(entity, "project_tasks");
            entity.ToTable("project_tasks");
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ProjectId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.MilestoneId });
            entity.HasOne<ProjectRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.ProjectId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProjectMilestone>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.MilestoneId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProjectTimeEntry>(entity =>
        {
            ConfigureBase(entity, "project_time_entries");
            entity.ToTable("project_time_entries", table =>
                table.HasCheckConstraint("CK_project_time_entries_Hours", "\"Hours\" > 0 AND \"Hours\" <= 24"));
            entity.Property(x => x.Hours).HasPrecision(5, 2);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ProjectId, x.WorkDate });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.TaskId });
            entity.HasOne<ProjectRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.ProjectId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProjectTask>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.TaskId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private void ConfigureBase<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity, string table)
        where TEntity : ProjectScopedEntity
    {
        entity.ToTable(table);
        entity.HasKey(x => x.Id);
        entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
        entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
        entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateScope();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateScope();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void ValidateScope()
    {
        var changes = ChangeTracker.Entries<ProjectScopedEntity>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (changes.Length == 0) return;
        var tenant = _tenantContext?.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
        var company = _companyContext?.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
        if (changes.Any(x => x.Entity.TenantId != tenant || x.Entity.CompanyId != company))
            throw new InvalidOperationException("Cross-tenant or cross-company project writes are not permitted.");
    }
}
