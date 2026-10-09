using Erp.Abstractions.Context;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Security;
using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.Data;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Erp.Infrastructure.Security;

public sealed class RoleAdministrationService(
    PlatformDbContext database,
    ITenantContext tenantContext,
    IPermissionRegistry permissionRegistry,
    IAuditLogWriter auditLog,
    IHttpContextAccessor? httpContextAccessor = null) : IRoleAdministrationService
{
    public async Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var roles = await database.Roles
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, Name = x.Name!, x.IsSystem, ConcurrencyStamp = x.ConcurrencyStamp! })
            .ToListAsync(cancellationToken);
        var permissions = await database.RolePermissions
            .Where(x => x.TenantId == tenantId)
            .GroupBy(x => x.RoleId)
            .ToDictionaryAsync(x => x.Key, x => x.Select(permission => permission.Permission).OrderBy(name => name).ToArray(), cancellationToken);

        return roles.Select(role => new RoleSummary(
            role.Id, role.Name, role.IsSystem, role.ConcurrencyStamp,
            permissions.GetValueOrDefault(role.Id) ?? [])).ToArray();
    }

    public async Task<RoleSummary> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = RequireTenant();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 100) throw new ArgumentException("Role name must contain between 2 and 100 characters.", nameof(request));
        var normalizedName = name.ToUpperInvariant();
        if (await database.Roles.AnyAsync(x => x.TenantId == tenantId && x.NormalizedName == normalizedName, cancellationToken))
        {
            throw new InvalidOperationException($"Role '{name}' already exists in this tenant.");
        }

        var permissions = ValidatePermissions(request.Permissions);
        var role = new ApplicationRole
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            NormalizedName = normalizedName,
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            IsSystem = false
        };
        database.Roles.Add(role);
        database.RolePermissions.AddRange(permissions.Select(permission => new RolePermissionAssignment
        {
            TenantId = tenantId,
            RoleId = role.Id,
            Permission = permission
        }));
        auditLog.Append(new(
            "role.created", "Role", role.Id.ToString(), null, null,
            JsonSerializer.Serialize(new { role.Name, Permissions = permissions })));
        await database.SaveChangesAsync(cancellationToken);
        return new(role.Id, name, false, role.ConcurrencyStamp!, permissions);
    }

    public async Task<RoleSummary?> SetPermissionsAsync(
        Guid roleId,
        SetRolePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = RequireTenant();
        var permissions = ValidatePermissions(request.Permissions);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var role = await database.Roles.SingleOrDefaultAsync(x => x.Id == roleId && x.TenantId == tenantId, cancellationToken);
            if (role is null) return null;
            if (!string.Equals(role.ConcurrencyStamp, request.ConcurrencyStamp, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The role changed after it was loaded. Refresh and try again.");
            }

            if (role.IsSystem && !permissions.Contains(PlatformPermissions.SecurityManage, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("The system administrator role must retain platform.security.manage.");
            }

            var existing = await database.RolePermissions.Where(x => x.TenantId == tenantId && x.RoleId == role.Id).ToListAsync(cancellationToken);
            if (existing.Select(x => x.Permission).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(permissions))
            {
                await transaction.CommitAsync(cancellationToken);
                return new(role.Id, role.Name!, role.IsSystem, role.ConcurrencyStamp!, permissions);
            }
            var affectedIds = await database.UserRoles.Where(x => x.RoleId == roleId).Select(x => x.UserId).ToArrayAsync(cancellationToken);
            if (existing.Any(x => x.Permission == PlatformPermissions.SecurityManage) && !permissions.Contains(PlatformPermissions.SecurityManage))
            {
                var actor = httpContextAccessor?.HttpContext?.User;
                if (Guid.TryParse(actor?.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) && affectedIds.Contains(actorId) &&
                    !await (from assignment in database.UserRoles
                            join permission in database.RolePermissions on assignment.RoleId equals permission.RoleId
                            where assignment.UserId == actorId && assignment.RoleId != roleId && permission.TenantId == tenantId && permission.Permission == PlatformPermissions.SecurityManage
                            select assignment.UserId).AnyAsync(cancellationToken))
                    throw new InvalidOperationException("You cannot remove your own access to security administration.");
                if (!await (from assignment in database.UserRoles
                            join permission in database.RolePermissions on assignment.RoleId equals permission.RoleId
                            join user in database.Users on assignment.UserId equals user.Id
                            join membership in database.UserCompanyMemberships on user.Id equals membership.UserId
                            join company in database.Companies on membership.CompanyId equals company.Id
                            where assignment.RoleId != roleId && permission.TenantId == tenantId && permission.Permission == PlatformPermissions.SecurityManage &&
                                membership.TenantId == tenantId && company.IsActive && (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= DateTimeOffset.UtcNow)
                            select user.Id).AnyAsync(cancellationToken))
                    throw new InvalidOperationException("The organization must retain an active administrator.");
            }
            database.RolePermissions.RemoveRange(existing);
            database.RolePermissions.AddRange(permissions.Select(permission => new RolePermissionAssignment
            {
                TenantId = tenantId,
                RoleId = role.Id,
                Permission = permission
            }));
            auditLog.Append(new(
                "role.permissions.changed", "Role", role.Id.ToString(), null,
                JsonSerializer.Serialize(new { Permissions = existing.Select(x => x.Permission).OrderBy(x => x).ToArray() }),
                JsonSerializer.Serialize(new { Permissions = permissions })));
            role.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            var affectedUsers = await database.Users.Where(x => affectedIds.Contains(x.Id)).ToListAsync(cancellationToken);
            foreach (var user in affectedUsers)
            {
                user.SecurityStamp = Guid.NewGuid().ToString("N");
                user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            }
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(role.Id, role.Name!, role.IsSystem, role.ConcurrencyStamp, permissions);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            database.ChangeTracker.Clear();
            throw new InvalidOperationException("The role changed after it was loaded. Refresh and try again.", exception);
        }
        catch
        {
            database.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<IReadOnlyList<TenantUserRoleSummary>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var users = await database.UserCompanyMemberships
            .Where(x => x.TenantId == tenantId)
            .Join(database.Users, membership => membership.UserId, user => user.Id,
                (membership, user) => new { user.Id, Email = user.Email ?? user.UserName ?? user.Id.ToString(), user.LockoutEnabled, user.LockoutEnd, user.ConcurrencyStamp })
            .Distinct()
            .OrderBy(x => x.Email)
            .ToListAsync(cancellationToken);
        var roleAssignments = await database.UserRoles
            .Join(database.Roles, assignment => assignment.RoleId, role => role.Id,
                (assignment, role) => new { assignment.UserId, assignment.RoleId, role.TenantId })
            .Where(x => x.TenantId == tenantId)
            .GroupBy(x => x.UserId)
            .ToDictionaryAsync(x => x.Key, x => x.Select(assignment => assignment.RoleId).OrderBy(id => id).ToArray(), cancellationToken);
        var companyAssignments = await database.UserCompanyMemberships
            .Where(x => x.TenantId == tenantId)
            .GroupBy(x => x.UserId)
            .ToDictionaryAsync(x => x.Key, x => new
            {
                CompanyIds = x.Select(membership => membership.CompanyId).OrderBy(id => id).ToArray(),
                DefaultCompanyId = x.Where(membership => membership.IsDefault).Select(membership => (Guid?)membership.CompanyId).FirstOrDefault()
            }, cancellationToken);

        return users.Select(user =>
        {
            var companies = companyAssignments.GetValueOrDefault(user.Id);
            return new TenantUserRoleSummary(user.Id, user.Email, roleAssignments.GetValueOrDefault(user.Id) ?? [])
            {
                CompanyIds = companies?.CompanyIds ?? [],
                DefaultCompanyId = companies?.DefaultCompanyId,
                IsActive = user.LockoutEnd != DateTimeOffset.MaxValue,
                IsLockedOut = user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow,
                ConcurrencyStamp = user.ConcurrencyStamp ?? ""
            };
        }).ToArray();
    }

    public async Task<TenantUserRoleSummary?> SetUserRolesAsync(
        Guid userId,
        SetUserRolesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var memberships = await database.UserCompanyMemberships
            .Where(x => x.TenantId == RequireTenant() && x.UserId == userId).ToListAsync(cancellationToken);
        if (memberships.Count == 0) return null;
        var companies = memberships.Select(x => x.CompanyId).ToArray();
        var defaultCompany = memberships.FirstOrDefault(x => x.IsDefault)?.CompanyId ?? companies[0];
        return await SetUserAccessAsync(userId, new(request.RoleIds, request.ExpectedRoleIds, companies, companies,
            defaultCompany, memberships.FirstOrDefault(x => x.IsDefault)?.CompanyId), cancellationToken);
    }

    public async Task<TenantUserRoleSummary?> SetUserCompaniesAsync(Guid userId, SetUserCompaniesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var memberships = await database.UserCompanyMemberships
            .Where(x => x.TenantId == RequireTenant() && x.UserId == userId).ToListAsync(cancellationToken);
        if (memberships.Count == 0) return null;
        var roles = await CurrentRoleIdsAsync(userId, RequireTenant(), cancellationToken);
        return await SetUserAccessAsync(userId, new(roles, roles, request.CompanyIds, request.ExpectedCompanyIds,
            request.DefaultCompanyId, memberships.FirstOrDefault(x => x.IsDefault)?.CompanyId), cancellationToken);
    }

    public async Task<TenantUserRoleSummary?> SetUserAccessAsync(Guid userId, SetUserAccessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RoleIds);
        ArgumentNullException.ThrowIfNull(request.ExpectedRoleIds);
        ArgumentNullException.ThrowIfNull(request.CompanyIds);
        ArgumentNullException.ThrowIfNull(request.ExpectedCompanyIds);
        var tenantId = RequireTenant();
        var requestedRoles = request.RoleIds.Distinct().OrderBy(id => id).ToArray();
        var requestedCompanies = request.CompanyIds.Distinct().OrderBy(id => id).ToArray();
        if (requestedCompanies.Length == 0) throw new ArgumentException("A user must belong to at least one company.", nameof(request));
        if (request.DefaultCompanyId is not { } defaultCompanyId || !requestedCompanies.Contains(defaultCompanyId))
            throw new ArgumentException("The default company must be one of the selected companies.", nameof(request));

        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var user = await database.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
            var memberships = await database.UserCompanyMemberships
                .Where(x => x.TenantId == tenantId && x.UserId == userId).OrderBy(x => x.CompanyId).ToListAsync(cancellationToken);
            if (user is null || memberships.Count == 0) return null;
            var existingRoles = await database.UserRoles
                .Join(database.Roles, assignment => assignment.RoleId, role => role.Id,
                    (assignment, role) => new { Assignment = assignment, role.TenantId, role.IsSystem })
                .Where(x => x.Assignment.UserId == userId && x.TenantId == tenantId).ToListAsync(cancellationToken);
            var existingRoleIds = existingRoles.Select(x => x.Assignment.RoleId).OrderBy(id => id).ToArray();
            var existingCompanyIds = memberships.Select(x => x.CompanyId).ToArray();
            var previousDefault = memberships.FirstOrDefault(x => x.IsDefault)?.CompanyId;
            if (!existingRoleIds.SequenceEqual(request.ExpectedRoleIds.Distinct().OrderBy(id => id)) ||
                !existingCompanyIds.SequenceEqual(request.ExpectedCompanyIds.Distinct().OrderBy(id => id)) ||
                previousDefault != request.ExpectedDefaultCompanyId)
                throw new InvalidOperationException("The user's access changed after it was loaded. Refresh and try again.");

            if (await database.Roles.CountAsync(x => x.TenantId == tenantId && requestedRoles.Contains(x.Id), cancellationToken) != requestedRoles.Length)
                throw new ArgumentException("One or more roles are unavailable in the active organization.", nameof(request));
            if (await database.Companies.CountAsync(x => x.TenantId == tenantId && requestedCompanies.Contains(x.Id), cancellationToken) != requestedCompanies.Length ||
                !await database.Companies.AnyAsync(x => x.TenantId == tenantId && x.Id == defaultCompanyId && x.IsActive, cancellationToken))
                throw new ArgumentException("The default company must be active, and all selected companies must belong to this organization.", nameof(request));

            var actor = httpContextAccessor?.HttpContext?.User;
            if (Guid.TryParse(actor?.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) && actorId == userId)
            {
                if (Guid.TryParse(actor.FindFirstValue("company_id"), out var currentCompanyId) && !requestedCompanies.Contains(currentCompanyId))
                    throw new InvalidOperationException("You cannot remove your access to the company currently open.");
                if (!await database.RolePermissions.AnyAsync(x => x.TenantId == tenantId && requestedRoles.Contains(x.RoleId) &&
                    x.Permission == PlatformPermissions.SecurityManage, cancellationToken))
                    throw new InvalidOperationException("You cannot remove your own access to security administration.");
            }

            foreach (var removedSystemRole in existingRoles.Where(x => x.IsSystem && !requestedRoles.Contains(x.Assignment.RoleId)))
            {
                var roleId = removedSystemRole.Assignment.RoleId;
                if (!await HasOtherActiveRoleAssigneeAsync(database, userId, tenantId, roleId, cancellationToken))
                    throw new InvalidOperationException("The final active user assigned to a system role cannot be removed.");
            }
            if (!await database.RolePermissions.AnyAsync(x => x.TenantId == tenantId && requestedRoles.Contains(x.RoleId) &&
                    x.Permission == PlatformPermissions.SecurityManage, cancellationToken) &&
                await database.RolePermissions.AnyAsync(x => x.TenantId == tenantId && existingRoleIds.Contains(x.RoleId) &&
                    x.Permission == PlatformPermissions.SecurityManage, cancellationToken) &&
                !await HasOtherActiveAdministratorAsync(database, tenantId, userId, cancellationToken))
                throw new InvalidOperationException("The organization must retain an active administrator.");

            if (existingRoleIds.SequenceEqual(requestedRoles) && existingCompanyIds.SequenceEqual(requestedCompanies) && previousDefault == defaultCompanyId)
            {
                await transaction.CommitAsync(cancellationToken);
                return await UserSummaryAsync(user, tenantId, requestedRoles, cancellationToken);
            }
            database.UserRoles.RemoveRange(existingRoles.Where(x => !requestedRoles.Contains(x.Assignment.RoleId)).Select(x => x.Assignment));
            database.UserRoles.AddRange(requestedRoles.Where(id => !existingRoleIds.Contains(id))
                .Select(id => new IdentityUserRole<Guid> { UserId = userId, RoleId = id }));
            database.UserCompanyMemberships.RemoveRange(memberships.Where(x => !requestedCompanies.Contains(x.CompanyId)));
            database.UserCompanyMemberships.AddRange(requestedCompanies.Where(id => !existingCompanyIds.Contains(id))
                .Select(id => new UserCompanyMembership { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, CompanyId = id, IsDefault = id == defaultCompanyId }));
            foreach (var membership in memberships.Where(x => requestedCompanies.Contains(x.CompanyId))) membership.IsDefault = membership.CompanyId == defaultCompanyId;
            auditLog.Append(new("user.access.changed", "User", userId.ToString(), null,
                JsonSerializer.Serialize(new { RoleIds = existingRoleIds, CompanyIds = existingCompanyIds, DefaultCompanyId = previousDefault }),
                JsonSerializer.Serialize(new { RoleIds = requestedRoles, CompanyIds = requestedCompanies, DefaultCompanyId = defaultCompanyId })));
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await UserSummaryAsync(user, tenantId, requestedRoles, cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            database.ChangeTracker.Clear();
            throw new InvalidOperationException("The user's access changed after it was loaded. Refresh and try again.", exception);
        }
        catch
        {
            database.ChangeTracker.Clear();
            throw;
        }
    }

    internal static async Task<bool> HasOtherActiveRoleAssigneeAsync(PlatformDbContext database, Guid userId, Guid tenantId, Guid roleId, CancellationToken cancellationToken) =>
        await (from assignment in database.UserRoles
               join user in database.Users on assignment.UserId equals user.Id
               join membership in database.UserCompanyMemberships on user.Id equals membership.UserId
               join company in database.Companies on membership.CompanyId equals company.Id
               where assignment.RoleId == roleId && user.Id != userId && membership.TenantId == tenantId && company.IsActive &&
                   (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= DateTimeOffset.UtcNow)
               select user.Id).AnyAsync(cancellationToken);

    internal static async Task<bool> HasOtherActiveAdministratorAsync(PlatformDbContext database, Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await (from assignment in database.UserRoles
               join permission in database.RolePermissions on assignment.RoleId equals permission.RoleId
               join user in database.Users on assignment.UserId equals user.Id
               join membership in database.UserCompanyMemberships on user.Id equals membership.UserId
               join company in database.Companies on membership.CompanyId equals company.Id
               where permission.TenantId == tenantId && permission.Permission == PlatformPermissions.SecurityManage && user.Id != userId &&
                   membership.TenantId == tenantId && company.IsActive && (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= DateTimeOffset.UtcNow)
               select user.Id).AnyAsync(cancellationToken);

    private async Task<TenantUserRoleSummary> UserSummaryAsync(ApplicationUser user, Guid tenantId, IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken)
    {
        var memberships = await database.UserCompanyMemberships.Where(x => x.TenantId == tenantId && x.UserId == user.Id).ToListAsync(cancellationToken);
        return new(user.Id, user.Email ?? user.UserName ?? user.Id.ToString(), roleIds)
        {
            CompanyIds = memberships.Select(x => x.CompanyId).OrderBy(id => id).ToArray(),
            DefaultCompanyId = memberships.Where(x => x.IsDefault).Select(x => (Guid?)x.CompanyId).FirstOrDefault(),
            IsActive = user.LockoutEnd != DateTimeOffset.MaxValue,
            IsLockedOut = user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow,
            ConcurrencyStamp = user.ConcurrencyStamp ?? ""
        };
    }

    private async Task<Guid[]> CurrentRoleIdsAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) => await database.UserRoles
        .Join(database.Roles, assignment => assignment.RoleId, role => role.Id, (assignment, role) => new { assignment.UserId, assignment.RoleId, role.TenantId })
        .Where(x => x.UserId == userId && x.TenantId == tenantId).Select(x => x.RoleId).OrderBy(id => id).ToArrayAsync(cancellationToken);

    private Guid RequireTenant() => tenantContext.TenantId?.Value
        ?? throw new InvalidOperationException("An active tenant is required.");

    private string[] ValidatePermissions(IEnumerable<string> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var permissions = requested.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var known = permissionRegistry.Permissions.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = permissions.FirstOrDefault(permission => !known.Contains(permission));
        if (unknown is not null) throw new ArgumentException($"Unknown permission '{unknown}'.", nameof(requested));
        return permissions;
    }
}
