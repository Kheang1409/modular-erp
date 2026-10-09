using System.Security.Claims;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Security;

public sealed class ErpUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityOptions> options,
    PlatformDbContext database,
    IHttpContextAccessor? httpContextAccessor = null)
    : UserClaimsPrincipalFactory<ApplicationUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var currentPrincipal = httpContextAccessor?.HttpContext?.User;
        var preferredCompanyId = Guid.TryParse(currentPrincipal?.FindFirstValue(ClaimTypes.NameIdentifier), out var principalUserId)
            && principalUserId == user.Id
            && Guid.TryParse(currentPrincipal.FindFirstValue("company_id"), out var principalCompanyId)
                ? principalCompanyId
                : Guid.Empty;
        var membership = await (
            from candidate in database.UserCompanyMemberships.IgnoreQueryFilters()
            join company in database.Companies.IgnoreQueryFilters()
                on new { candidate.TenantId, Id = candidate.CompanyId } equals new { company.TenantId, company.Id }
            join tenant in database.Tenants on candidate.TenantId equals tenant.Id
            where candidate.UserId == user.Id && company.IsActive && tenant.IsActive
            orderby candidate.CompanyId == preferredCompanyId descending,
                candidate.IsDefault descending, company.Name, company.Id
            select new { candidate.TenantId, candidate.CompanyId })
            .FirstOrDefaultAsync();

        if (membership is not null)
        {
            identity.AddClaim(new Claim("tenant_id", membership.TenantId.ToString()));
            identity.AddClaim(new Claim("company_id", membership.CompanyId.ToString()));
            var permissions = await (
                from userRole in database.UserRoles
                join role in database.Roles.IgnoreQueryFilters() on userRole.RoleId equals role.Id
                join permission in database.RolePermissions.IgnoreQueryFilters() on role.Id equals permission.RoleId
                where userRole.UserId == user.Id && role.TenantId == membership.TenantId && permission.TenantId == membership.TenantId
                select permission.Permission)
                .Distinct()
                .ToListAsync();
            identity.AddClaims(permissions.Select(permission => new Claim("permission", permission)));
        }

        return identity;
    }
}
