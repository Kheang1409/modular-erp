using Erp.Abstractions.Context;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence;

public sealed class OrganizationContextReader(
    PlatformDbContext database,
    ITenantContext? tenantContext = null,
    ICompanyContext? companyContext = null) : IOrganizationContextReader
{
    public async Task<OrganizationContext?> GetAsync(UserId userId, CancellationToken cancellationToken)
    {
        var currentTenantId = tenantContext?.TenantId?.Value;
        var currentCompanyId = companyContext?.CompanyId?.Value;
        var query =
            from membership in database.UserCompanyMemberships.IgnoreQueryFilters()
            join company in database.Companies.IgnoreQueryFilters()
                on new { membership.TenantId, Id = membership.CompanyId } equals new { company.TenantId, company.Id }
            join tenant in database.Tenants on membership.TenantId equals tenant.Id
            where membership.UserId == userId.Value && tenant.IsActive && company.IsActive
                && (!currentTenantId.HasValue || membership.TenantId == currentTenantId.Value)
                && (!currentCompanyId.HasValue || membership.CompanyId == currentCompanyId.Value)
            orderby membership.IsDefault descending, company.Name, company.Id
            select new { Tenant = tenant, Company = company };

        var result = await query.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return result is null ? null : new OrganizationContext(
            new TenantId(result.Tenant.Id), result.Tenant.Name, new CompanyId(result.Company.Id), result.Company.Name,
            result.Company.Code, result.Company.BaseCurrencyCode, result.Company.TimeZoneId,
            result.Tenant.TimeZoneId, OrganizationAdministrationService.OrganizationVersion(result.Tenant));
    }
}
