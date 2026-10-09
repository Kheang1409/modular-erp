namespace Erp.SharedKernel;

public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.NewGuid());
}

public readonly record struct CompanyId(Guid Value);

public readonly record struct UserId(Guid Value);
