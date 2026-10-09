using Erp.SharedKernel;

namespace Erp.Abstractions.Context;

public interface IOrganizationContextReader
{
    Task<OrganizationContext?> GetAsync(UserId userId, CancellationToken cancellationToken);
}

public interface IOrganizationAdministrationService
{
    Task<IReadOnlyList<CompanySummary>> ListCompaniesAsync(Guid userId, CancellationToken cancellationToken, bool includeAllCompanies = false);
    Task<CompanySummary> CreateCompanyAsync(Guid userId, CreateCompanyRequest request, CancellationToken cancellationToken);
    Task<CompanySummary?> UpdateCompanyAsync(Guid companyId, UpdateCompanyRequest request, CancellationToken cancellationToken);
    Task<bool> SwitchCompanyAsync(Guid userId, Guid companyId, CancellationToken cancellationToken);
    Task<OrganizationSummary> UpdateOrganizationAsync(UpdateOrganizationRequest request, CancellationToken cancellationToken);
}

public sealed record OrganizationContext(
    TenantId TenantId,
    string TenantName,
    CompanyId CompanyId,
    string CompanyName,
    string CompanyCode,
    string BaseCurrencyCode,
    string TimeZoneId,
    string TenantTimeZoneId = "UTC",
    string TenantVersion = "");

public sealed record OrganizationSummary(Guid Id, string Name, string TimeZoneId, string Version);
public sealed record UpdateOrganizationRequest(string? Name, string? TimeZoneId, string? ExpectedVersion);

public sealed record CompanySummary(
    Guid Id,
    string Name,
    string Code,
    string BaseCurrencyCode,
    string TimeZoneId,
    bool IsActive,
    bool IsMember,
    bool IsCurrent,
    int MemberCount,
    string Version);

public sealed record CreateCompanyRequest(string? Name, string? Code, string? BaseCurrencyCode, string? TimeZoneId);
public sealed record UpdateCompanyRequest(string? Name, string? Code, string? TimeZoneId, bool IsActive, string? ExpectedVersion = null);

public static class PlatformPermissions
{
    public const string OrganizationRead = "platform.organization.read";
    public const string OrganizationManage = "platform.organization.manage";
    public const string SecurityManage = "platform.security.manage";
    public const string AuditRead = "platform.audit.read";
    public const string CurrencyManage = "platform.currency.manage";
    public const string AttachmentRead = "platform.attachment.read";
    public const string AttachmentManage = "platform.attachment.manage";
    public const string ModuleManage = "platform.module.manage";
    public const string OperationsManage = "platform.operations.manage";
}
