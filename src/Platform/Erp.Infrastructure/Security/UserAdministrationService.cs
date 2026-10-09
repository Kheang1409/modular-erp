using System.Data;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Security;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Security;

public sealed class UserAdministrationService(
    PlatformDbContext database,
    ITenantContext tenantContext,
    UserManager<ApplicationUser> userManager,
    IRoleAdministrationService roles,
    IAuditLogWriter auditLog,
    IHttpContextAccessor httpContextAccessor) : IUserAdministrationService
{
    public async Task<TenantUserRoleSummary> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = RequireTenant();
        var email = request.Email?.Trim() ?? "";
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email)
            throw new ArgumentException("Enter a valid email address, up to 254 characters.", nameof(request));
        ValidatePasswordInput(request.Password);
        ArgumentNullException.ThrowIfNull(request.CompanyIds);
        ArgumentNullException.ThrowIfNull(request.RoleIds);
        var companyIds = request.CompanyIds.Distinct().OrderBy(id => id).ToArray();
        var roleIds = request.RoleIds.Distinct().OrderBy(id => id).ToArray();
        if (companyIds.Length == 0 || !companyIds.Contains(request.DefaultCompanyId))
            throw new ArgumentException("Select at least one company and a default company from that selection.", nameof(request));

        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            if (await database.Companies.CountAsync(x => x.TenantId == tenantId && x.IsActive && companyIds.Contains(x.Id), cancellationToken) != companyIds.Length)
                throw new ArgumentException("All selected companies must be active and belong to this organization.", nameof(request));
            if (await database.Roles.CountAsync(x => x.TenantId == tenantId && roleIds.Contains(x.Id), cancellationToken) != roleIds.Length)
                throw new ArgumentException("All selected roles must belong to this organization.", nameof(request));
            var normalizedEmail = userManager.NormalizeEmail(email);
            if (await database.Users.AnyAsync(x => x.NormalizedEmail == normalizedEmail || x.NormalizedUserName == normalizedEmail, cancellationToken))
                throw new ArgumentException("A user with that email address already exists.", nameof(request));
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                LockoutEnabled = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            RequireSuccess(await userManager.CreateAsync(user, request.Password));
            cancellationToken.ThrowIfCancellationRequested();
            database.UserCompanyMemberships.AddRange(companyIds.Select(companyId => new UserCompanyMembership
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = user.Id,
                CompanyId = companyId,
                IsDefault = companyId == request.DefaultCompanyId
            }));
            database.UserRoles.AddRange(roleIds.Select(roleId => new IdentityUserRole<Guid> { UserId = user.Id, RoleId = roleId }));
            auditLog.Append(new("user.created", "User", user.Id.ToString(), null, null,
                JsonSerializer.Serialize(new { Email = email, CompanyIds = companyIds, RoleIds = roleIds, request.DefaultCompanyId })));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await roles.ListUsersAsync(cancellationToken)).Single(x => x.UserId == user.Id);
        }
        catch
        {
            database.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<TenantUserRoleSummary?> SetActiveAsync(Guid userId, SetUserActiveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = RequireTenant();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var user = await LoadUserAsync(userId, cancellationToken);
            if (user is null) return null;
            ValidateVersion(user, request.ConcurrencyStamp);
            var wasActive = user.LockoutEnd != DateTimeOffset.MaxValue;
            if (request.IsActive && !await (from membership in database.UserCompanyMemberships
                                            join company in database.Companies on membership.CompanyId equals company.Id
                                            where membership.UserId == userId && membership.TenantId == tenantId && company.IsActive
                                            select membership.Id).AnyAsync(cancellationToken))
                throw new InvalidOperationException("Assign an active company before activating this account.");
            if (!request.IsActive)
            {
                if (Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) && actorId == userId)
                    throw new InvalidOperationException("You cannot deactivate your own account.");
                var isAdministrator = await (from assignment in database.UserRoles
                                             join permission in database.RolePermissions on assignment.RoleId equals permission.RoleId
                                             where assignment.UserId == userId && permission.TenantId == tenantId && permission.Permission == PlatformPermissions.SecurityManage
                                             select assignment.UserId).AnyAsync(cancellationToken);
                var systemRoleIds = await (from assignment in database.UserRoles
                                           join role in database.Roles on assignment.RoleId equals role.Id
                                           where assignment.UserId == userId && role.TenantId == tenantId && role.IsSystem
                                           select role.Id).ToArrayAsync(cancellationToken);
                foreach (var systemRoleId in systemRoleIds)
                {
                    if (!await RoleAdministrationService.HasOtherActiveRoleAssigneeAsync(database, userId, tenantId, systemRoleId, cancellationToken))
                        throw new InvalidOperationException("The final active user assigned to a system role cannot be deactivated.");
                }
                if (isAdministrator &&
                    !await RoleAdministrationService.HasOtherActiveAdministratorAsync(database, tenantId, userId, cancellationToken))
                    throw new InvalidOperationException("The organization must retain an active administrator.");
            }
            if (wasActive == request.IsActive && !(request.IsActive && user.LockoutEnd > DateTimeOffset.UtcNow))
            {
                await transaction.CommitAsync(cancellationToken);
                return (await roles.ListUsersAsync(cancellationToken)).Single(x => x.UserId == userId);
            }
            user.LockoutEnabled = true;
            user.LockoutEnd = request.IsActive ? null : DateTimeOffset.MaxValue;
            user.AccessFailedCount = 0;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            RequireSuccess(await userManager.UpdateAsync(user));
            auditLog.Append(new(request.IsActive ? "user.activated" : "user.deactivated", "User", userId.ToString(), null,
                JsonSerializer.Serialize(new { IsActive = wasActive }), JsonSerializer.Serialize(new { request.IsActive })));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await roles.ListUsersAsync(cancellationToken)).Single(x => x.UserId == userId);
        }
        catch
        {
            database.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<TenantUserRoleSummary?> ResetPasswordAsync(Guid userId, ResetUserPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePasswordInput(request.Password);
        _ = RequireTenant();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var user = await LoadUserAsync(userId, cancellationToken);
            if (user is null) return null;
            ValidateVersion(user, request.ConcurrencyStamp);
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            RequireSuccess(await userManager.ResetPasswordAsync(user, token, request.Password));
            cancellationToken.ThrowIfCancellationRequested();
            auditLog.Append(new("user.password.reset", "User", userId.ToString(), null, null, null));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await roles.ListUsersAsync(cancellationToken)).Single(x => x.UserId == userId);
        }
        catch
        {
            database.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<ApplicationUser?> LoadUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        if (!await database.UserCompanyMemberships.AnyAsync(x => x.TenantId == tenantId && x.UserId == userId, cancellationToken)) return null;
        if (await database.UserCompanyMemberships.IgnoreQueryFilters().AnyAsync(x => x.UserId == userId && x.TenantId != tenantId, cancellationToken))
            throw new InvalidOperationException("This account is shared with another organization. Account status and passwords must be managed centrally.");
        return await database.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
    }

    private Guid RequireTenant() => tenantContext.TenantId?.Value ?? throw new InvalidOperationException("An active organization is required.");

    private static void ValidateVersion(ApplicationUser user, string expected)
    {
        if (string.IsNullOrEmpty(expected) || user.ConcurrencyStamp != expected)
            throw new InvalidOperationException("The user changed after they were loaded. Refresh and try again.");
    }

    private static void ValidatePasswordInput(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024)
            throw new ArgumentException("Enter a password with no more than 1024 characters.", nameof(password));
    }

    private static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new ArgumentException(string.Join(" ", result.Errors.Select(x => x.Description)));
    }
}
