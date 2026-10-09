using Erp.Modules.Crm.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Crm;

internal sealed class CrmLeadDirectory(CrmDbContext database) : ICrmLeadDirectory
{
    public Task<CrmLeadReference?> FindAsync(Guid leadId, CancellationToken token) => database.Leads.AsNoTracking().Where(x => x.Id == leadId)
        .Select(x => new CrmLeadReference(x.Id, x.Title, x.ContactId, (CrmLeadState)x.State, x.IsArchived)).SingleOrDefaultAsync(token);
}
