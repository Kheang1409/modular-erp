using Erp.Modules.Accounting.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting;

public sealed class PayrollJournalResolver(AccountingDbContext db) : IPayrollJournalResolver
{
    public Task<Guid?> FindActivePayrollJournalAsync(CancellationToken cancellationToken) =>
        db.Journals.AsNoTracking().Where(x => x.Code == "PAYROLL" && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
}
