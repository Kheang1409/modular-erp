namespace Erp.Abstractions.Organization;

public sealed record CompanyUserReference(Guid Id, string DisplayName, string? Email);

public interface ICompanyUserDirectory
{
    Task<CompanyUserReference?> FindAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CompanyUserReference>> ListAsync(CancellationToken cancellationToken);
}
