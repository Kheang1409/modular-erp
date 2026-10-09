using Erp.Abstractions.Context;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Contacts;

public sealed class ContactsDbContext(
    DbContextOptions<ContactsDbContext> options,
    ITenantContext? tenantContext = null,
    ICompanyContext? companyContext = null) : DbContext(options)
{
    private readonly ITenantContext? _tenantContext = tenantContext;
    private readonly ICompanyContext? _companyContext = companyContext;
    private Guid TenantId => _tenantContext?.TenantId?.Value ?? Guid.Empty;
    private Guid CompanyId => _companyContext?.CompanyId?.Value ?? Guid.Empty;
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ContactEmailAddress> ContactEmails => Set<ContactEmailAddress>();
    public DbSet<ContactPhoneNumber> ContactPhones => Set<ContactPhoneNumber>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("contacts");
        modelBuilder.Entity<Contact>(entity =>
        {
            entity.ToTable("contacts", table => table.HasCheckConstraint(
                "CK_contacts_Role", "\"IsCustomer\" OR \"IsSupplier\""));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.FirstName).HasMaxLength(100);
            entity.Property(x => x.LastName).HasMaxLength(100);
            entity.Property(x => x.OrganizationName).HasMaxLength(200);
            entity.Property(x => x.LegalName).HasMaxLength(200);
            entity.Property(x => x.Website).HasMaxLength(500);
            entity.Property(x => x.Email).HasMaxLength(254);
            entity.Property(x => x.Phone).HasMaxLength(50);
            entity.Property(x => x.TaxNumber).HasMaxLength(100);
            entity.Property(x => x.Notes).HasMaxLength(4000);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.DisplayName });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Email });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.IsCustomer, x.IsSupplier });
            entity.OwnsOne(x => x.BillingAddress, address => ConfigureAddress(address, "Billing"));
            entity.OwnsOne(x => x.ShippingAddress, address => ConfigureAddress(address, "Shipping"));
            entity.Navigation(x => x.BillingAddress).IsRequired();
            entity.Navigation(x => x.ShippingAddress).IsRequired();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasMany(x => x.EmailAddresses).WithOne()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.ContactId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.PhoneNumbers).WithOne()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.ContactId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ContactEmailAddress>(entity =>
        {
            entity.ToTable("contact_emails"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Address).HasMaxLength(254);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ContactId, x.Address }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
        modelBuilder.Entity<ContactPhoneNumber>(entity =>
        {
            entity.ToTable("contact_phones"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Number).HasMaxLength(50);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ContactId, x.Number }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
    }

    private static void ConfigureAddress(
        Microsoft.EntityFrameworkCore.Metadata.Builders.OwnedNavigationBuilder<Contact, ContactAddress> address,
        string prefix)
    {
        address.Property(x => x.Line1).HasColumnName($"{prefix}Line1").HasMaxLength(200);
        address.Property(x => x.Line2).HasColumnName($"{prefix}Line2").HasMaxLength(200);
        address.Property(x => x.City).HasColumnName($"{prefix}City").HasMaxLength(100);
        address.Property(x => x.StateOrProvince).HasColumnName($"{prefix}StateOrProvince").HasMaxLength(100);
        address.Property(x => x.PostalCode).HasColumnName($"{prefix}PostalCode").HasMaxLength(30);
        address.Property(x => x.CountryCode).HasColumnName($"{prefix}CountryCode").HasMaxLength(2);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateScope();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateScope()
    {
        var changes = ChangeTracker.Entries()
            .Where(x => x.Entity is Contact or ContactEmailAddress or ContactPhoneNumber)
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (changes.Length == 0) return;
        var tenant = _tenantContext?.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
        var company = _companyContext?.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
        if (changes.Any(x => Scope(x.Entity) != (tenant, company)))
            throw new InvalidOperationException("Cross-tenant or cross-company contact writes are not permitted.");
    }

    private static (Guid Tenant, Guid Company) Scope(object entity) => entity switch
    {
        Contact value => (value.TenantId, value.CompanyId),
        ContactEmailAddress value => (value.TenantId, value.CompanyId),
        ContactPhoneNumber value => (value.TenantId, value.CompanyId),
        _ => default
    };
}
