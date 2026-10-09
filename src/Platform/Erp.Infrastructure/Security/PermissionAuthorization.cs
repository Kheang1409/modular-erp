using System.Security.Claims;
using Erp.Abstractions.Context;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Erp.Infrastructure.Security;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim("permission", requirement.Permission)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public TenantId? TenantId => Parse(accessor.HttpContext?.User.FindFirstValue("tenant_id"));
    private static TenantId? Parse(string? value) => Guid.TryParse(value, out var id) ? new(id) : null;
}

public sealed class HttpCompanyContext(IHttpContextAccessor accessor) : ICompanyContext
{
    public CompanyId? CompanyId => Parse(accessor.HttpContext?.User.FindFirstValue("company_id"));
    private static CompanyId? Parse(string? value) => Guid.TryParse(value, out var id) ? new(id) : null;
}

public sealed class ExecutionOrganizationContext(IHttpContextAccessor accessor) : ITenantContext, ICompanyContext
{
    private TenantId? _tenantId;
    private CompanyId? _companyId;

    public TenantId? TenantId => _tenantId ?? ParseTenant(accessor.HttpContext?.User.FindFirstValue("tenant_id"));
    public CompanyId? CompanyId => _companyId ?? ParseCompany(accessor.HttpContext?.User.FindFirstValue("company_id"));

    public void Set(TenantId tenantId, CompanyId companyId)
    {
        _tenantId = tenantId;
        _companyId = companyId;
    }

    private static TenantId? ParseTenant(string? value) => Guid.TryParse(value, out var id) ? new(id) : null;
    private static CompanyId? ParseCompany(string? value) => Guid.TryParse(value, out var id) ? new(id) : null;
}
