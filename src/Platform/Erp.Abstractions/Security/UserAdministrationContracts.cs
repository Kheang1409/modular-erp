namespace Erp.Abstractions.Security;

public interface IUserAdministrationService
{
    Task<TenantUserRoleSummary> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken);
    Task<TenantUserRoleSummary?> SetActiveAsync(Guid userId, SetUserActiveRequest request, CancellationToken cancellationToken);
    Task<TenantUserRoleSummary?> ResetPasswordAsync(Guid userId, ResetUserPasswordRequest request, CancellationToken cancellationToken);
}

public sealed record CreateUserRequest(
    string Email,
    string Password,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid> CompanyIds,
    Guid DefaultCompanyId);

public sealed record SetUserActiveRequest(bool IsActive, string ConcurrencyStamp);

public sealed record ResetUserPasswordRequest(string Password, string ConcurrencyStamp);
