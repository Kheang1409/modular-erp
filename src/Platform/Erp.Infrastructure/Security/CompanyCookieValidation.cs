using System.Security.Claims;
using Erp.Abstractions.Context;
using Erp.Abstractions.Security;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Security;

public static class CompanyCookieValidation
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.HttpContext.RequestServices;
        if (context.Principal is null || !await services.GetRequiredService<IAuthenticatedSessionValidator>()
                .IsValidAsync(context.Principal, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }
        // Authentication middleware has not assigned HttpContext.User yet. Make the validated
        // cookie available while Identity regenerates claims, preserving this session's company.
        var previousPrincipal = context.HttpContext.User;
        context.HttpContext.User = context.Principal;
        try { await SecurityStampValidator.ValidatePrincipalAsync(context); }
        finally { context.HttpContext.User = previousPrincipal; }
    }
}

public sealed class AuthenticatedSessionValidator(
    DbContextOptions<PlatformDbContext> databaseOptions,
    IOptions<IdentityOptions> identityOptions,
    IClock clock) : IAuthenticatedSessionValidator
{
    public async Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true) return false;
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !Guid.TryParse(principal.FindFirstValue("tenant_id"), out var tenantId)
            || !Guid.TryParse(principal.FindFirstValue("company_id"), out var companyId))
        {
            return false;
        }

        // Each HTTP request checks the cookie's company, rather than the user's persisted default.
        // A separate context also keeps cookie validation independent of component database work.
        await using var database = new PlatformDbContext(databaseOptions);
        var membership = await (
            from access in database.UserCompanyMemberships.IgnoreQueryFilters().AsNoTracking()
            join company in database.Companies.IgnoreQueryFilters() on access.CompanyId equals company.Id
            join tenant in database.Tenants on access.TenantId equals tenant.Id
            join user in database.Users on access.UserId equals user.Id
            where access.UserId == userId && access.TenantId == tenantId && access.CompanyId == companyId
                && company.TenantId == tenantId && company.IsActive && tenant.IsActive
            select new { user.SecurityStamp, user.LockoutEnabled, user.LockoutEnd })
            .SingleOrDefaultAsync(cancellationToken);
        return membership is not null
            && !(membership.LockoutEnabled && membership.LockoutEnd > clock.UtcNow)
            && string.Equals(membership.SecurityStamp, principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType), StringComparison.Ordinal);
    }
}
