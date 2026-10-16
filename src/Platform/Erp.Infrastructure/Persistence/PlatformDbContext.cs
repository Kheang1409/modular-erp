using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Infrastructure.Persistence;

public sealed class PlatformDbContext(
    DbContextOptions<PlatformDbContext> options,
    ITenantContext? tenantContext = null)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), ITransactionParticipant
{
    private readonly ITenantContext? _tenantContext = tenantContext;

    DbConnection ITransactionParticipant.Connection => Database.GetDbConnection();
    bool ITransactionParticipant.HasPendingChanges => ChangeTracker.HasChanges();

    async Task ITransactionParticipant.EnlistAsync(DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null && Database.CurrentTransaction is { } current &&
            !ReferenceEquals(current.GetDbTransaction(), transaction))
            throw new InvalidOperationException("An unmanaged platform transaction is already active.");
        await Database.UseTransactionAsync(transaction, cancellationToken);
    }

    void ITransactionParticipant.DiscardChanges() => ChangeTracker.Clear();

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<UserCompanyMembership> UserCompanyMemberships => Set<UserCompanyMembership>();
    public DbSet<RolePermissionAssignment> RolePermissions => Set<RolePermissionAssignment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();
    public DbSet<CompanyCurrency> CompanyCurrencies => Set<CompanyCurrency>();
    public DbSet<CompanyExchangeRate> CompanyExchangeRates => Set<CompanyExchangeRate>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InstalledModule> InstalledModules => Set<InstalledModule>();

    private Guid CurrentTenantId => _tenantContext?.TenantId?.Value ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("platform");

        builder.Entity<ApplicationUser>(entity => entity.ToTable("users"));
        builder.Entity<ApplicationRole>(entity =>
        {
            entity.ToTable("roles");
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasIndex(x => x.NormalizedName).IsUnique(false).HasDatabaseName("RoleNameIndex");
            entity.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>>(entity => entity.ToTable("user_roles"));
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>(entity => entity.ToTable("user_claims"));
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>(entity => entity.ToTable("user_logins"));
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>>(entity => entity.ToTable("role_claims"));
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>(entity => entity.ToTable("user_tokens"));
        builder.Entity<RolePermissionAssignment>(entity =>
        {
            entity.ToTable("role_permissions");
            entity.HasKey(x => new { x.TenantId, x.RoleId, x.Permission });
            entity.Property(x => x.Permission).HasMaxLength(200);
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<ApplicationRole>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.RoleId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(100);
            entity.Property(x => x.EntityType).HasMaxLength(200);
            entity.Property(x => x.EntityId).HasMaxLength(200);
            entity.Property(x => x.CorrelationId).HasMaxLength(100);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");
            entity.Property(x => x.BeforeJson).HasColumnType("jsonb");
            entity.Property(x => x.AfterJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.TenantId, x.TimestampUtc });
            entity.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Company>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<DocumentSequence>(entity =>
        {
            entity.ToTable("document_sequences", table =>
            {
                table.HasCheckConstraint("CK_document_sequences_Padding", "\"Padding\" BETWEEN 1 AND 18");
                table.HasCheckConstraint("CK_document_sequences_NextValue", "\"NextValue\" > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(100);
            entity.Property(x => x.PrefixTemplate).HasMaxLength(100);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Key }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Company>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<CompanyCurrency>(entity =>
        {
            entity.ToTable("company_currencies", table =>
                table.HasCheckConstraint("CK_company_currencies_MinorUnits", "\"MinorUnits\" BETWEEN 0 AND 6"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(3);
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.Symbol).HasMaxLength(10);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Company>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CompanyExchangeRate>(entity =>
        {
            entity.ToTable("company_exchange_rates", table =>
                table.HasCheckConstraint("CK_company_exchange_rates_Rate", "\"Rate\" > 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FromCurrencyCode).HasMaxLength(3);
            entity.Property(x => x.ToCurrencyCode).HasMaxLength(3);
            entity.Property(x => x.Rate).HasPrecision(28, 12);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.FromCurrencyCode, x.ToCurrencyCode, x.EffectiveDate }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Company>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Attachment>(entity =>
        {
            entity.ToTable("attachments", table =>
                table.HasCheckConstraint("CK_attachments_Size", "\"Size\" >= 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileName).HasMaxLength(255);
            entity.Property(x => x.ContentType).HasMaxLength(200);
            entity.Property(x => x.StorageKey).HasMaxLength(500);
            entity.Property(x => x.EntityType).HasMaxLength(200);
            entity.Property(x => x.EntityId).HasMaxLength(200);
            entity.HasIndex(x => x.StorageKey).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EntityType, x.EntityId });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Company>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UploadedBy).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<BackgroundJob>(entity =>
        {
            entity.ToTable("background_jobs", table =>
            {
                table.HasCheckConstraint("CK_background_jobs_Attempts", "\"Attempts\" >= 0 AND \"MaxAttempts\" BETWEEN 1 AND 20");
                table.HasCheckConstraint("CK_background_jobs_State", "\"State\" IN ('queued', 'running', 'succeeded', 'dead_letter')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(150);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.State).HasMaxLength(20);
            entity.Property(x => x.CorrelationId).HasMaxLength(100);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.Property(x => x.LastError).HasMaxLength(4000);
            entity.HasIndex(x => new { x.State, x.ExecuteAfterUtc });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Name, x.IdempotencyKey }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Company>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages", table =>
                table.HasCheckConstraint("CK_outbox_messages_RetryCount", "\"RetryCount\" >= 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(200);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.CorrelationId).HasMaxLength(100);
            entity.Property(x => x.LastError).HasMaxLength(4000);
            entity.HasIndex(x => new { x.ProcessedAtUtc, x.DeadLetteredAtUtc, x.NextAttemptAtUtc });
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Company>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Slug).HasMaxLength(100);
            entity.Property(x => x.TimeZoneId).HasMaxLength(100);
            entity.HasIndex(x => x.Slug).IsUnique();
        });
        builder.Entity<Company>(entity =>
        {
            entity.ToTable("companies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Code).HasMaxLength(30);
            entity.Property(x => x.BaseCurrencyCode).HasMaxLength(3);
            entity.Property(x => x.TimeZoneId).HasMaxLength(100);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<Branch>(entity =>
        {
            entity.ToTable("branches");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Code).HasMaxLength(30);
            entity.Property(x => x.TimeZoneId).HasMaxLength(100);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<Company>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<UserCompanyMembership>(entity =>
        {
            entity.ToTable("user_company_memberships");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.CompanyId }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Company>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<InstalledModule>(entity =>
        {
            entity.ToTable("installed_modules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ModuleId).HasMaxLength(150);
            entity.Property(x => x.Version).HasMaxLength(50);
            entity.Property(x => x.Checksum).HasMaxLength(128);
            entity.HasIndex(x => x.ModuleId).IsUnique();
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateTenantWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateTenantWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateTenantWrites()
    {
        if (_tenantContext is null) return;
        var changes = ChangeTracker.Entries<ITenantOwned>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();
        if (changes.Length == 0) return;

        if (changes.Any(entry => entry.Entity is AuditLog && entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit entries are append-only and cannot be changed or deleted.");
        }

        var tenantId = _tenantContext.TenantId?.Value
            ?? throw new InvalidOperationException("A tenant context is required to modify tenant-owned data.");

        var invalid = changes.FirstOrDefault(entry => entry.Entity.TenantId != tenantId);
        if (invalid is not null)
        {
            throw new InvalidOperationException("Cross-tenant writes are not permitted.");
        }
    }
}
