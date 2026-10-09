namespace Erp.Modules.Crm.Contracts;

public enum CrmLeadState { New, Qualified, Converted, Lost }

public sealed record CrmLeadReference(Guid Id, string Title, Guid? ContactId, CrmLeadState State, bool IsArchived);

public interface ICrmLeadDirectory
{
    Task<CrmLeadReference?> FindAsync(Guid leadId, CancellationToken cancellationToken);
}
