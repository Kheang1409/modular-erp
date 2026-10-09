namespace Erp.Abstractions.Security;

public interface IRoleAdministrationService
{
    Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken);
    Task<RoleSummary> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken);
    Task<RoleSummary?> SetPermissionsAsync(Guid roleId, SetRolePermissionsRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantUserRoleSummary>> ListUsersAsync(CancellationToken cancellationToken);
    Task<TenantUserRoleSummary?> SetUserRolesAsync(Guid userId, SetUserRolesRequest request, CancellationToken cancellationToken);
    Task<TenantUserRoleSummary?> SetUserCompaniesAsync(Guid userId, SetUserCompaniesRequest request, CancellationToken cancellationToken);
    Task<TenantUserRoleSummary?> SetUserAccessAsync(Guid userId, SetUserAccessRequest request, CancellationToken cancellationToken);
}

public sealed record RoleSummary(
    Guid Id,
    string Name,
    bool IsSystem,
    string ConcurrencyStamp,
    IReadOnlyList<string> Permissions);

public sealed record CreateRoleRequest(string Name, IReadOnlyList<string> Permissions);

public sealed record SetRolePermissionsRequest(IReadOnlyList<string> Permissions, string ConcurrencyStamp);

public sealed record TenantUserRoleSummary(Guid UserId, string Email, IReadOnlyList<Guid> RoleIds)
{
    public IReadOnlyList<Guid> CompanyIds { get; init; } = [];
    public Guid? DefaultCompanyId { get; init; }
    public bool IsActive { get; init; } = true;
    public bool IsLockedOut { get; init; }
    public string ConcurrencyStamp { get; init; } = "";
}

public sealed record SetUserRolesRequest(IReadOnlyList<Guid> RoleIds, IReadOnlyList<Guid> ExpectedRoleIds);
public sealed record SetUserCompaniesRequest(IReadOnlyList<Guid> CompanyIds, IReadOnlyList<Guid> ExpectedCompanyIds, Guid? DefaultCompanyId);

public sealed record SetUserAccessRequest(
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid> ExpectedRoleIds,
    IReadOnlyList<Guid> CompanyIds,
    IReadOnlyList<Guid> ExpectedCompanyIds,
    Guid? DefaultCompanyId,
    Guid? ExpectedDefaultCompanyId);
