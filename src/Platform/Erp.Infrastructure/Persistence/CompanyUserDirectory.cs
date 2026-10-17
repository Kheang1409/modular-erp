using Erp.Abstractions.Context;
using Erp.Abstractions.Organization;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence;

internal sealed class CompanyUserDirectory(PlatformDbContext database, ITenantContext tenant, ICompanyContext company)
    : ICompanyUserDirectory
{
    public Task<CompanyUserReference?> FindAsync(Guid userId, CancellationToken cancellationToken) => References(Query()
        .Where(user => user.Id == userId)).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CompanyUserReference>> ListAsync(CancellationToken cancellationToken) =>
        await References(Query().OrderBy(user => user.UserName ?? user.Email ?? user.Id.ToString())).ToListAsync(cancellationToken);

    private static IQueryable<CompanyUserReference> References(IQueryable<ApplicationUser> users) => users
        .Select(user => new CompanyUserReference(user.Id, user.UserName ?? user.Email ?? user.Id.ToString(), user.Email));

    private IQueryable<ApplicationUser> Query()
    {
        var tenantId = tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
        var companyId = company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
        return from membership in database.UserCompanyMemberships.AsNoTracking().IgnoreQueryFilters()
               join user in database.Users.AsNoTracking() on membership.UserId equals user.Id
               where membership.TenantId == tenantId && membership.CompanyId == companyId
               select user;
    }
}
