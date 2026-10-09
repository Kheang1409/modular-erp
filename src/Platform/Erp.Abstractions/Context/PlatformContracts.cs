using Erp.SharedKernel;

namespace Erp.Abstractions.Context;

public interface ITenantContext
{
    TenantId? TenantId { get; }
}

public interface ICompanyContext
{
    CompanyId? CompanyId { get; }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
