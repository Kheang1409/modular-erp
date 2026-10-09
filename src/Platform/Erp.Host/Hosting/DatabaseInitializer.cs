using System.Security.Claims;
using Erp.Abstractions.Modules;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Erp.Host.Hosting;

public static class DatabaseInitializer
{
    public static async Task InitializeDatabaseAsync(this WebApplication app, IPermissionRegistry permissions, CancellationToken cancellationToken = default)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup")) return;
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await database.Database.OpenConnectionAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(47190520260913)", cancellationToken);
        try
        {
            await database.Database.MigrateAsync(cancellationToken);
            var registry = scope.ServiceProvider.GetRequiredService<IModuleRegistry>();
            var initializers = scope.ServiceProvider.GetServices<IModuleDatabaseInitializer>()
                .ToDictionary(initializer => initializer.ModuleId, StringComparer.OrdinalIgnoreCase);
            foreach (var module in registry.Modules)
            {
                if (!initializers.TryGetValue(module.Manifest.Id, out var initializer)) continue;
                await initializer.MigrateAsync(cancellationToken);
                await RecordMigrationAsync(database, module.Manifest, cancellationToken);
            }
            await BootstrapAdministratorAsync(scope.ServiceProvider, permissions, app.Configuration, app.Logger, cancellationToken);
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(47190520260913)", CancellationToken.None);
            await database.Database.CloseConnectionAsync();
        }
    }

    private static async Task RecordMigrationAsync(
        PlatformDbContext database,
        ModuleManifest manifest,
        CancellationToken cancellationToken)
    {
        var installed = await database.InstalledModules.SingleOrDefaultAsync(
            module => module.ModuleId == manifest.Id, cancellationToken);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest))));
        if (installed is null)
        {
            installed = new InstalledModule
            {
                Id = Guid.NewGuid(),
                ModuleId = manifest.Id,
                Version = manifest.Version,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                Enabled = true,
                Checksum = checksum
            };
            database.InstalledModules.Add(installed);
        }
        else
        {
            installed.Version = manifest.Version;
            installed.InstalledAtUtc = DateTimeOffset.UtcNow;
            installed.Enabled = true;
            installed.Checksum = checksum;
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private static async Task BootstrapAdministratorAsync(
        IServiceProvider services,
        IPermissionRegistry permissions,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var email = configuration["BootstrapAdmin:Email"];
        var password = configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        var createdUser = false;
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Bootstrap administrator creation failed: {string.Join("; ", created.Errors.Select(x => x.Description))}");
            }
            createdUser = true;
        }

        var databaseOptions = services.GetRequiredService<DbContextOptions<PlatformDbContext>>();
        await using var database = new PlatformDbContext(databaseOptions);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var membership = await EnsureOrganizationMembershipAsync(database, user.Id, configuration, cancellationToken);
        await EnsureBaseCurrencyAsync(database, membership, cancellationToken);
        var administratorRole = await EnsureAdministratorRoleAsync(database, membership.TenantId, cancellationToken);
        await SynchronizeAdministratorPermissionsAsync(database, administratorRole, permissions, cancellationToken);
        if (!await database.UserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == administratorRole.Id, cancellationToken))
        {
            database.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = administratorRole.Id });
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await RemoveLegacyPermissionClaimsAsync(users, user);
        if (createdUser) HostLog.BootstrapAdminCreated(logger, email);
    }

    private static async Task<UserCompanyMembership> EnsureOrganizationMembershipAsync(
        PlatformDbContext database,
        Guid userId,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var membership = await database.UserCompanyMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.IsDefault, cancellationToken);
        if (membership is not null) return membership;

        const string tenantSlug = "default";
        var tenant = await database.Tenants.SingleOrDefaultAsync(x => x.Slug == tenantSlug, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = configuration["BootstrapOrganization:TenantName"] ?? "Default Tenant",
                Slug = tenantSlug,
                TimeZoneId = configuration["BootstrapOrganization:TimeZone"] ?? "UTC",
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            database.Tenants.Add(tenant);
        }

        var company = new Company
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = configuration["BootstrapOrganization:CompanyName"] ?? "Default Company",
            Code = "MAIN",
            BaseCurrencyCode = configuration["BootstrapOrganization:Currency"] ?? "USD",
            TimeZoneId = tenant.TimeZoneId,
            IsActive = true
        };
        database.Companies.Add(company);
        database.Branches.Add(new Branch
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CompanyId = company.Id,
            Name = "Main Branch",
            Code = "MAIN",
            TimeZoneId = tenant.TimeZoneId,
            IsActive = true
        });
        membership = new UserCompanyMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserId = userId,
            CompanyId = company.Id,
            IsDefault = true
        };
        database.UserCompanyMemberships.Add(membership);
        return membership;
    }

    private static async Task<ApplicationRole> EnsureAdministratorRoleAsync(
        PlatformDbContext database,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var role = await database.Roles.IgnoreQueryFilters().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.NormalizedName == "ADMINISTRATOR",
            cancellationToken);
        if (role is not null)
        {
            role.IsSystem = true;
            return role;
        }

        role = new ApplicationRole
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Administrator",
            NormalizedName = "ADMINISTRATOR",
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            IsSystem = true
        };
        database.Roles.Add(role);
        return role;
    }

    private static async Task EnsureBaseCurrencyAsync(
        PlatformDbContext database,
        UserCompanyMembership membership,
        CancellationToken cancellationToken)
    {
        var company = await database.Companies.IgnoreQueryFilters().SingleAsync(
            x => x.TenantId == membership.TenantId && x.Id == membership.CompanyId, cancellationToken);
        var code = company.BaseCurrencyCode.Trim().ToUpperInvariant();
        company.BaseCurrencyCode = code;
        if (await database.CompanyCurrencies.IgnoreQueryFilters().AnyAsync(
            x => x.TenantId == membership.TenantId && x.CompanyId == membership.CompanyId && x.Code == code,
            cancellationToken)) return;
        database.CompanyCurrencies.Add(new CompanyCurrency
        {
            Id = Guid.NewGuid(),
            TenantId = membership.TenantId,
            CompanyId = membership.CompanyId,
            Code = code,
            Name = code,
            Symbol = code,
            MinorUnits = 2,
            IsActive = true,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        });
    }

    private static async Task SynchronizeAdministratorPermissionsAsync(
        PlatformDbContext database,
        ApplicationRole role,
        IPermissionRegistry permissions,
        CancellationToken cancellationToken)
    {
        var assigned = await database.RolePermissions.IgnoreQueryFilters()
            .Where(x => x.TenantId == role.TenantId && x.RoleId == role.Id)
            .Select(x => x.Permission)
            .ToListAsync(cancellationToken);
        database.RolePermissions.AddRange(permissions.Permissions
            .Where(x => !assigned.Contains(x.Name, StringComparer.Ordinal))
            .Select(x => new RolePermissionAssignment
            {
                TenantId = role.TenantId,
                RoleId = role.Id,
                Permission = x.Name
            }));
    }

    private static async Task RemoveLegacyPermissionClaimsAsync(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        var legacyClaims = (await users.GetClaimsAsync(user)).Where(x => x.Type == "permission").ToArray();
        if (legacyClaims.Length == 0) return;
        var removed = await users.RemoveClaimsAsync(user, legacyClaims);
        if (!removed.Succeeded)
        {
            throw new InvalidOperationException("Bootstrap administrator legacy permission cleanup failed.");
        }
    }
}
