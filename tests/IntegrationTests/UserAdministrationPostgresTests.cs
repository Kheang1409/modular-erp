using System.Security.Claims;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Security;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Security;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests;

public sealed class UserAdministrationPostgresTests
{
    [Fact]
    public async Task UsersHaveAtomicTenantScopedAccessAndProtectedAccountLifecycle()
    {
        var postgres = await IntegrationContainers.StartPostgresAsync("user_admin_tests");
        await using (postgres)
        {
            var tenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var firstCompany = Guid.NewGuid();
            var secondCompany = Guid.NewGuid();
            var foreignCompany = Guid.NewGuid();
            var systemRole = Guid.NewGuid();
            var readerRole = Guid.NewGuid();
            var foreignRole = Guid.NewGuid();
            var administratorId = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            await using (var seed = new PlatformDbContext(options))
            {
                await seed.Database.MigrateAsync();
                seed.Tenants.AddRange(TenantFor(tenantId, "organization"), TenantFor(foreignTenantId, "other"));
                seed.Companies.AddRange(CompanyFor(firstCompany, tenantId, "MAIN"), CompanyFor(secondCompany, tenantId, "WEST"), CompanyFor(foreignCompany, foreignTenantId, "FOREIGN"));
                seed.Roles.AddRange(RoleFor(systemRole, tenantId, "Administrator", true), RoleFor(readerRole, tenantId, "Reader"), RoleFor(foreignRole, foreignTenantId, "Other reader"));
                seed.RolePermissions.AddRange(new RolePermissionAssignment { TenantId = tenantId, RoleId = systemRole, Permission = PlatformPermissions.SecurityManage },
                    new RolePermissionAssignment { TenantId = tenantId, RoleId = readerRole, Permission = PlatformPermissions.OrganizationRead });
                seed.Users.Add(new ApplicationUser
                {
                    Id = administratorId,
                    UserName = "admin@example.test",
                    Email = "admin@example.test",
                    NormalizedEmail = "ADMIN@EXAMPLE.TEST",
                    NormalizedUserName = "ADMIN@EXAMPLE.TEST",
                    SecurityStamp = Stamp(),
                    ConcurrencyStamp = Stamp(),
                    LockoutEnabled = true
                });
                seed.UserRoles.Add(new IdentityUserRole<Guid> { UserId = administratorId, RoleId = systemRole });
                seed.UserCompanyMemberships.Add(new UserCompanyMembership { Id = Guid.NewGuid(), TenantId = tenantId, UserId = administratorId, CompanyId = firstCompany, IsDefault = true });
                await seed.SaveChangesAsync();
            }

            var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal(administratorId, firstCompany) } };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection();
            services.AddSingleton<ITenantContext>(new TenantContext(tenantId));
            services.AddSingleton<ICompanyContext>(new CompanyContext(firstCompany));
            services.AddSingleton<IClock, Clock>();
            services.AddSingleton<IHttpContextAccessor>(http);
            services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
            services.AddDbContext<PlatformDbContext>(configuration => configuration.UseNpgsql(postgres.GetConnectionString()));
            services.AddIdentityCore<ApplicationUser>(identity =>
            {
                identity.Password.RequiredLength = 12;
                identity.Password.RequireDigit = identity.Password.RequireLowercase = identity.Password.RequireUppercase = identity.Password.RequireNonAlphanumeric = true;
            }).AddRoles<ApplicationRole>().AddEntityFrameworkStores<PlatformDbContext>().AddDefaultTokenProviders();
            services.AddScoped<IAuditLogWriter, AuditLogService>();
            services.AddScoped<IRoleAdministrationService, RoleAdministrationService>();
            services.AddScoped<IUserAdministrationService, UserAdministrationService>();
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserAdministrationService>();
            var roles = scope.ServiceProvider.GetRequiredService<IRoleAdministrationService>();
            var database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var identity = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            await Assert.ThrowsAsync<ArgumentException>(() => users.CreateAsync(new("weak@example.test", "short", [readerRole], [firstCompany], firstCompany), CancellationToken.None));
            Assert.False(await database.Users.AnyAsync(x => x.Email == "weak@example.test"));
            await Assert.ThrowsAsync<ArgumentException>(() => users.CreateAsync(new("foreign@example.test", "Valid@Password1409", [foreignRole], [firstCompany], firstCompany), CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => users.CreateAsync(new("foreign@example.test", "Valid@Password1409", [readerRole], [foreignCompany], foreignCompany), CancellationToken.None));
            Assert.Single(await database.Users.ToListAsync());

            var worker = await users.CreateAsync(new("worker@example.test", "Valid@Password1409", [readerRole], [firstCompany, secondCompany], firstCompany), CancellationToken.None);
            Assert.True(worker.IsActive);
            Assert.Equal(firstCompany, worker.DefaultCompanyId);
            Assert.Equal(2, worker.CompanyIds.Count);
            Assert.True(await identity.CheckPasswordAsync((await identity.FindByIdAsync(worker.UserId.ToString()))!, "Valid@Password1409"));

            await Assert.ThrowsAsync<ArgumentException>(() => roles.SetUserAccessAsync(worker.UserId,
                new([foreignRole], worker.RoleIds, [secondCompany], worker.CompanyIds, secondCompany, worker.DefaultCompanyId), CancellationToken.None));
            var unchanged = (await roles.ListUsersAsync(CancellationToken.None)).Single(x => x.UserId == worker.UserId);
            Assert.Equal(worker.RoleIds, unchanged.RoleIds);
            Assert.Equal(worker.CompanyIds, unchanged.CompanyIds);
            Assert.Equal(worker.ConcurrencyStamp, unchanged.ConcurrencyStamp);
            await Assert.ThrowsAsync<InvalidOperationException>(() => roles.SetUserAccessAsync(worker.UserId,
                new([], worker.RoleIds, [secondCompany], worker.CompanyIds, secondCompany, secondCompany), CancellationToken.None));
            worker = (await roles.SetUserAccessAsync(worker.UserId,
                new([], worker.RoleIds, [secondCompany], worker.CompanyIds, secondCompany, worker.DefaultCompanyId), CancellationToken.None))!;
            Assert.Empty(worker.RoleIds);
            Assert.Equal(secondCompany, Assert.Single(worker.CompanyIds));
            var originalStamp = worker.ConcurrencyStamp;
            worker = (await roles.SetUserAccessAsync(worker.UserId,
                new(worker.RoleIds, worker.RoleIds, worker.CompanyIds, worker.CompanyIds, worker.DefaultCompanyId, worker.DefaultCompanyId), CancellationToken.None))!;
            Assert.Equal(originalStamp, worker.ConcurrencyStamp);

            worker = (await users.SetActiveAsync(worker.UserId, new(false, worker.ConcurrencyStamp), CancellationToken.None))!;
            Assert.False(worker.IsActive);
            Assert.True(await identity.IsLockedOutAsync((await identity.FindByIdAsync(worker.UserId.ToString()))!));
            Assert.Single(worker.CompanyIds);
            var company = await database.Companies.SingleAsync(x => x.Id == secondCompany);
            company.IsActive = false;
            await database.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => users.SetActiveAsync(worker.UserId, new(true, worker.ConcurrencyStamp), CancellationToken.None));
            company = await database.Companies.SingleAsync(x => x.Id == secondCompany);
            company.IsActive = true;
            await database.SaveChangesAsync();
            worker = (await users.SetActiveAsync(worker.UserId, new(true, worker.ConcurrencyStamp), CancellationToken.None))!;
            Assert.True(worker.IsActive);
            Assert.False(worker.IsLockedOut);
            var beforeResetStamp = (await identity.FindByIdAsync(worker.UserId.ToString()))!.SecurityStamp;
            worker = (await users.ResetPasswordAsync(worker.UserId, new("Different@Password1409", worker.ConcurrencyStamp), CancellationToken.None))!;
            var resetIdentity = (await identity.FindByIdAsync(worker.UserId.ToString()))!;
            Assert.NotEqual(beforeResetStamp, resetIdentity.SecurityStamp);
            Assert.False(await identity.CheckPasswordAsync(resetIdentity, "Valid@Password1409"));
            Assert.True(await identity.CheckPasswordAsync(resetIdentity, "Different@Password1409"));

            var administrator = (await roles.ListUsersAsync(CancellationToken.None)).Single(x => x.UserId == administratorId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => users.SetActiveAsync(administratorId, new(false, administrator.ConcurrencyStamp), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => roles.SetUserAccessAsync(administratorId,
                new([], administrator.RoleIds, administrator.CompanyIds, administrator.CompanyIds, firstCompany, firstCompany), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => roles.SetUserAccessAsync(administratorId,
                new(administrator.RoleIds, administrator.RoleIds, [secondCompany], administrator.CompanyIds, secondCompany, firstCompany), CancellationToken.None));

            var customAdmin = await roles.CreateAsync(new("Custom administrator", [PlatformPermissions.SecurityManage]), CancellationToken.None);
            worker = (await roles.SetUserAccessAsync(worker.UserId,
                new([customAdmin.Id], worker.RoleIds, worker.CompanyIds, worker.CompanyIds, secondCompany, secondCompany), CancellationToken.None))!;
            http.HttpContext!.User = Principal(worker.UserId, secondCompany);
            var lastSystemError = await Assert.ThrowsAsync<InvalidOperationException>(() => users.SetActiveAsync(administratorId, new(false, administrator.ConcurrencyStamp), CancellationToken.None));
            Assert.Contains("system role", lastSystemError.Message, StringComparison.Ordinal);
            var actorRoleError = await Assert.ThrowsAsync<InvalidOperationException>(() => roles.SetPermissionsAsync(customAdmin.Id, new([], customAdmin.ConcurrencyStamp), CancellationToken.None));
            Assert.Contains("your own", actorRoleError.Message, StringComparison.Ordinal);

            var administratorIdentity = await database.Users.SingleAsync(x => x.Id == administratorId);
            administratorIdentity.LockoutEnd = DateTimeOffset.MaxValue;
            await database.SaveChangesAsync();
            http.HttpContext.User = Principal(Guid.NewGuid(), firstCompany);
            var lastAdminError = await Assert.ThrowsAsync<InvalidOperationException>(() => roles.SetPermissionsAsync(customAdmin.Id, new([], customAdmin.ConcurrencyStamp), CancellationToken.None));
            Assert.Contains("active administrator", lastAdminError.Message, StringComparison.Ordinal);
            administratorIdentity = await database.Users.SingleAsync(x => x.Id == administratorId);
            administratorIdentity.LockoutEnd = null;
            await database.SaveChangesAsync();
            http.HttpContext.User = Principal(administratorId, firstCompany);
            beforeResetStamp = (await database.Users.SingleAsync(x => x.Id == worker.UserId)).SecurityStamp;
            await roles.SetPermissionsAsync(customAdmin.Id, new([PlatformPermissions.OrganizationRead], customAdmin.ConcurrencyStamp), CancellationToken.None);
            Assert.NotEqual(beforeResetStamp, (await database.Users.SingleAsync(x => x.Id == worker.UserId)).SecurityStamp);

            var audits = await database.AuditLogs.ToListAsync();
            Assert.Contains(audits, x => x.Action == "user.created" && x.EntityId == worker.UserId.ToString());
            Assert.Contains(audits, x => x.Action == "user.access.changed");
            Assert.Contains(audits, x => x.Action == "user.password.reset");
            Assert.DoesNotContain(audits, x => (x.BeforeJson ?? "").Contains("Password1409", StringComparison.Ordinal) || (x.AfterJson ?? "").Contains("Password1409", StringComparison.Ordinal));
        }
    }

    private static ClaimsPrincipal Principal(Guid userId, Guid companyId) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim("company_id", companyId.ToString())], "test"));
    private static Tenant TenantFor(Guid id, string slug) => new() { Id = id, Name = slug, Slug = slug, IsActive = true, TimeZoneId = "UTC", CreatedAtUtc = DateTimeOffset.UtcNow };
    private static Company CompanyFor(Guid id, Guid tenant, string code) => new() { Id = id, TenantId = tenant, Code = code, Name = code, BaseCurrencyCode = "USD", TimeZoneId = "UTC", IsActive = true };
    private static ApplicationRole RoleFor(Guid id, Guid tenant, string name, bool system = false) => new() { Id = id, TenantId = tenant, Name = name, NormalizedName = name.ToUpperInvariant(), ConcurrencyStamp = Stamp(), IsSystem = system };
    private static string Stamp() => Guid.NewGuid().ToString("N");
    private sealed class TenantContext(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class CompanyContext(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class PermissionRegistry : IPermissionRegistry
    {
        public IReadOnlyCollection<PermissionDefinition> Permissions => [new(PlatformPermissions.SecurityManage, "Security", "platform"), new(PlatformPermissions.OrganizationRead, "Organization", "platform")];
        public void Add(PermissionDefinition permission) => throw new NotSupportedException();
    }
}
