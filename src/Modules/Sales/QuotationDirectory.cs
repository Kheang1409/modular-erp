using Erp.Modules.Sales.Contracts;
using Microsoft.EntityFrameworkCore;
namespace Erp.Modules.Sales;

internal sealed class QuotationDirectory(SalesDbContext database) : IQuotationDirectory { public async Task<QuotationReference?> FindAsync(Guid id, CancellationToken token) => await database.Quotations.AsNoTracking().Where(x => x.Id == id).Select(x => new QuotationReference(x.Id, x.Number, x.CustomerId, x.CurrencyCode, x.Total, x.State.ToString())).SingleOrDefaultAsync(token); }
