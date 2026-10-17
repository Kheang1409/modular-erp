using Erp.Abstractions.Modules;
using Erp.Host.Hosting;
using Erp.Infrastructure;
using Erp.Infrastructure.Persistence;
using Erp.ModuleSystem;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.IntegrationTests;

public sealed class BootstrapPostgresTests
{
    private const string AdministratorEmail = "bootstrap@example.test";
    private const string AdministratorPassword = "Bootstrap@TestPassword1409";
    private const string ChangedBootstrapPassword = "Changed@TestPassword1409";

    [Fact]
    public async Task FreshStartupCreatesAdministratorOrganizationBaseCurrencyAndPermissions()
    {
        await using var postgres = await IntegrationContainers.StartPostgresAsync("bootstrap_fresh_tests");
        await using var app = CreateApplication(postgres.GetConnectionString(), currency: " usd ");

        // Exercise the public startup path: the company does not exist in PostgreSQL when bootstrap begins.
        await app.InitializeDatabaseAsync(app.Services.GetRequiredService<IPermissionRegistry>());

        await using var database = CreateDatabase(postgres.GetConnectionString());
        Assert.NotEmpty(await database.Database.GetAppliedMigrationsAsync());
        var user = Assert.Single(await database.Users.ToListAsync());
        Assert.Equal(AdministratorEmail, user.Email);
        Assert.Equal(AdministratorEmail.ToUpperInvariant(), user.NormalizedEmail);
        Assert.True(user.EmailConfirmed);
        Assert.NotEqual(default, user.CreatedAtUtc);
        await AssertAdministratorPasswordAsync(app, AdministratorPassword);

        var tenant = Assert.Single(await database.Tenants.ToListAsync());
        Assert.Equal("default", tenant.Slug);
        Assert.Equal("Default Tenant", tenant.Name);
        Assert.Equal("UTC", tenant.TimeZoneId);
        Assert.True(tenant.IsActive);
        var company = Assert.Single(await database.Companies.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(tenant.Id, company.TenantId);
        Assert.Equal("Default Company", company.Name);
        Assert.Equal("MAIN", company.Code);
        Assert.Equal("USD", company.BaseCurrencyCode);
        Assert.Equal(tenant.TimeZoneId, company.TimeZoneId);
        Assert.True(company.IsActive);
        var branch = Assert.Single(await database.Branches.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(tenant.Id, branch.TenantId);
        Assert.Equal(company.Id, branch.CompanyId);
        Assert.Equal("Main Branch", branch.Name);
        Assert.Equal("MAIN", branch.Code);
        Assert.Equal(tenant.TimeZoneId, branch.TimeZoneId);
        Assert.True(branch.IsActive);
        var membership = Assert.Single(await database.UserCompanyMemberships.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(tenant.Id, membership.TenantId);
        Assert.Equal(company.Id, membership.CompanyId);
        Assert.Equal(user.Id, membership.UserId);
        Assert.True(membership.IsDefault);
        var currency = Assert.Single(await database.CompanyCurrencies.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(tenant.Id, currency.TenantId);
        Assert.Equal(company.Id, currency.CompanyId);
        Assert.Equal("USD", currency.Code);
        Assert.Equal("USD", currency.Name);
        Assert.Equal("USD", currency.Symbol);
        Assert.Equal(2, currency.MinorUnits);
        Assert.True(currency.IsActive);
        Assert.False(string.IsNullOrWhiteSpace(currency.ConcurrencyStamp));
        await AssertAdministratorRoleAsync(database, app, tenant.Id, user.Id);
        Assert.Empty(await database.InstalledModules.ToListAsync());
    }

    [Fact]
    public async Task RestartPreservesIdentityOrganizationAndCustomizedCurrencyWithoutDuplicates()
    {
        await using var postgres = await IntegrationContainers.StartPostgresAsync("bootstrap_restart_tests");
        await using (var first = CreateApplication(postgres.GetConnectionString()))
        {
            await first.InitializeDatabaseAsync(first.Services.GetRequiredService<IPermissionRegistry>());
        }

        await using var database = CreateDatabase(postgres.GetConnectionString());
        var user = await database.Users.SingleAsync();
        var tenant = await database.Tenants.SingleAsync();
        var company = await database.Companies.IgnoreQueryFilters().SingleAsync();
        company.Name = "Established Operating Company";
        company.Code = "OPERATING";
        company.TimeZoneId = "America/Chicago";
        company.IsActive = false;
        var currency = await database.CompanyCurrencies.IgnoreQueryFilters().SingleAsync();
        currency.Name = "Custom accounting dollar";
        currency.Symbol = "$US";
        currency.MinorUnits = 4;
        currency.IsActive = false;
        currency.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await database.SaveChangesAsync();
        var branch = await database.Branches.IgnoreQueryFilters().SingleAsync();
        var membership = await database.UserCompanyMemberships.IgnoreQueryFilters().SingleAsync();
        var role = await database.Roles.IgnoreQueryFilters().SingleAsync();

        await using var restarted = CreateApplication(postgres.GetConnectionString(), ChangedBootstrapPassword, "EUR");
        restarted.Configuration["BootstrapOrganization:TenantName"] = "Replacement Tenant";
        restarted.Configuration["BootstrapOrganization:CompanyName"] = "Replacement Company";
        restarted.Configuration["BootstrapOrganization:TimeZone"] = "Europe/London";
        await restarted.InitializeDatabaseAsync(restarted.Services.GetRequiredService<IPermissionRegistry>());
        // A further pass catches additions that would otherwise appear only after the first restart.
        await restarted.InitializeDatabaseAsync(restarted.Services.GetRequiredService<IPermissionRegistry>());

        database.ChangeTracker.Clear();
        Assert.Equivalent(user, Assert.Single(await database.Users.ToListAsync()), strict: true);
        Assert.Equivalent(tenant, Assert.Single(await database.Tenants.ToListAsync()), strict: true);
        Assert.Equivalent(company, Assert.Single(await database.Companies.IgnoreQueryFilters().ToListAsync()), strict: true);
        Assert.Equivalent(branch, Assert.Single(await database.Branches.IgnoreQueryFilters().ToListAsync()), strict: true);
        Assert.Equivalent(membership, Assert.Single(await database.UserCompanyMemberships.IgnoreQueryFilters().ToListAsync()), strict: true);
        Assert.Equivalent(currency, Assert.Single(await database.CompanyCurrencies.IgnoreQueryFilters().ToListAsync()), strict: true);
        Assert.Equivalent(role, Assert.Single(await database.Roles.IgnoreQueryFilters().ToListAsync()), strict: true);
        await AssertAdministratorPasswordAsync(restarted, AdministratorPassword, ChangedBootstrapPassword);
        await AssertAdministratorRoleAsync(database, restarted, tenant.Id, user.Id);
    }

    [Fact]
    public async Task ExistingBusinessMembershipBackfillsCurrencyWithoutReplacingOrganization()
    {
        await using var postgres = await IntegrationContainers.StartPostgresAsync("bootstrap_existing_tests");
        await using var app = CreateApplication(postgres.GetConnectionString());
        await using var database = CreateDatabase(postgres.GetConnectionString());
        await database.Database.MigrateAsync();
        ApplicationUser user;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = AdministratorEmail,
                Email = AdministratorEmail,
                EmailConfirmed = true,
                CreatedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
            };
            var result = await users.CreateAsync(user, AdministratorPassword);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
        }
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Existing Business Tenant",
            Slug = "existing-business",
            TimeZoneId = "Asia/Tokyo",
            IsActive = true,
            CreatedAtUtc = user.CreatedAtUtc
        };
        var company = new Company
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Existing Business Company",
            Code = "BUSINESS",
            BaseCurrencyCode = "JPY",
            TimeZoneId = tenant.TimeZoneId,
            IsActive = true
        };
        var membership = new UserCompanyMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserId = user.Id,
            CompanyId = company.Id,
            IsDefault = true
        };
        database.Tenants.Add(tenant);
        database.Companies.Add(company);
        database.UserCompanyMemberships.Add(membership);
        await database.SaveChangesAsync();

        await app.InitializeDatabaseAsync(app.Services.GetRequiredService<IPermissionRegistry>());

        database.ChangeTracker.Clear();
        Assert.Equivalent(user, Assert.Single(await database.Users.ToListAsync()), strict: true);
        Assert.Equivalent(tenant, Assert.Single(await database.Tenants.ToListAsync()), strict: true);
        Assert.Equivalent(company, Assert.Single(await database.Companies.IgnoreQueryFilters().ToListAsync()), strict: true);
        Assert.Equivalent(membership, Assert.Single(await database.UserCompanyMemberships.IgnoreQueryFilters().ToListAsync()), strict: true);
        Assert.Empty(await database.Branches.IgnoreQueryFilters().ToListAsync());
        var currency = Assert.Single(await database.CompanyCurrencies.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(tenant.Id, currency.TenantId);
        Assert.Equal(company.Id, currency.CompanyId);
        Assert.Equal("JPY", currency.Code);
        Assert.Equal("JPY", currency.Name);
        Assert.Equal("JPY", currency.Symbol);
        Assert.Equal(2, currency.MinorUnits);
        Assert.True(currency.IsActive);
        await AssertAdministratorPasswordAsync(app, AdministratorPassword);
        await AssertAdministratorRoleAsync(database, app, tenant.Id, user.Id);
    }

    private static WebApplication CreateApplication(
        string connectionString,
        string password = AdministratorPassword,
        string currency = "USD")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "BootstrapIntegrationTests",
            ContentRootPath = AppContext.BaseDirectory,
            Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["BootstrapAdmin:Email"] = AdministratorEmail,
            ["BootstrapAdmin:Password"] = password,
            ["BootstrapOrganization:Currency"] = currency
        });
        builder.Logging.ClearProviders();
        var permissions = new PermissionRegistry();
        permissions.Add(new PermissionDefinition("platform.security.manage", "Manage security", "platform"));
        permissions.Add(new PermissionDefinition("platform.organization.read", "Read organization", "platform"));
        builder.Services.AddSingleton<IPermissionRegistry>(permissions);
        builder.Services.AddSingleton<IModuleRegistry>(new ModuleRegistry());
        builder.Services.AddErpInfrastructure(builder.Configuration, permissions);
        return builder.Build();
    }

    private static PlatformDbContext CreateDatabase(string connectionString) => new(
        new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);

    private static async Task AssertAdministratorPasswordAsync(WebApplication app, string password, string? rejectedPassword = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(AdministratorEmail);
        Assert.NotNull(user);
        Assert.True(await users.CheckPasswordAsync(user, password));
        if (rejectedPassword is not null) Assert.False(await users.CheckPasswordAsync(user, rejectedPassword));
    }

    private static async Task AssertAdministratorRoleAsync(PlatformDbContext database, WebApplication app, Guid tenantId, Guid userId)
    {
        var role = Assert.Single(await database.Roles.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(tenantId, role.TenantId);
        Assert.Equal("Administrator", role.Name);
        Assert.Equal("ADMINISTRATOR", role.NormalizedName);
        Assert.True(role.IsSystem);
        var assignment = Assert.Single(await database.UserRoles.ToListAsync());
        Assert.Equal(userId, assignment.UserId);
        Assert.Equal(role.Id, assignment.RoleId);
        var permissions = await database.RolePermissions.IgnoreQueryFilters().ToListAsync();
        Assert.All(permissions, permission =>
        {
            Assert.Equal(tenantId, permission.TenantId);
            Assert.Equal(role.Id, permission.RoleId);
        });
        Assert.Equal(app.Services.GetRequiredService<IPermissionRegistry>().Permissions.Select(permission => permission.Name).Order(),
            permissions.Select(permission => permission.Permission).Order());
    }
}
