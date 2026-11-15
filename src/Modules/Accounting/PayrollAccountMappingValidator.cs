using Erp.Modules.Accounting.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting;

public sealed class PayrollAccountMappingValidator(AccountingDbContext db) : IPayrollAccountMappingValidator
{
    public Task<bool> IsEligibleAsync(Guid accountId, string mappingType, CancellationToken cancellationToken)
    {
        var type = mappingType switch
        {
            "SalaryExpense" or "OvertimeExpense" or "AllowanceExpense" or "EmployerContributionExpense" => AccountType.Expense,
            "EmployeeDeductionLiability" or "TaxLiability" or "NetSalaryPayable" or "EmployerContributionLiability" => AccountType.Liability,
            "EmployeeReceivable" => AccountType.Asset,
            _ => (AccountType?)null
        };
        return type is null ? Task.FromResult(false) : db.Accounts.AsNoTracking()
            .AnyAsync(x => x.Id == accountId && x.IsActive && x.AllowDirectPosting && x.Type == type, cancellationToken);
    }
}
