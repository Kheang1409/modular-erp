using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Hcm.Contracts;

namespace Erp.Modules.Hcm;

public sealed record PayrollAdjustmentJournalPlan(PayrollAccountMappingType ComponentMapping,
    IReadOnlyList<PayrollJournalLineRequest> Lines);

/// <summary>Builds only correction journals whose debit/credit treatment is backed by existing HCM mappings.</summary>
public static class PayrollAdjustmentJournalBuilder
{
    public static PayrollAccountMappingType RequiredComponentMapping(PayrollAdjustmentCategory category) => category switch
    {
        PayrollAdjustmentCategory.SalaryCorrection or PayrollAdjustmentCategory.AttendanceCorrection or
            PayrollAdjustmentCategory.RetroactiveCompensationCorrection or PayrollAdjustmentCategory.UnderpaymentCorrection =>
                PayrollAccountMappingType.SalaryExpense,
        PayrollAdjustmentCategory.OvertimeCorrection => PayrollAccountMappingType.OvertimeExpense,
        PayrollAdjustmentCategory.AllowanceCorrection => PayrollAccountMappingType.AllowanceExpense,
        PayrollAdjustmentCategory.DeductionCorrection => PayrollAccountMappingType.EmployeeDeductionLiability,
        PayrollAdjustmentCategory.TaxWithholdingCorrection => PayrollAccountMappingType.TaxLiability,
        PayrollAdjustmentCategory.OverpaymentRecovery => PayrollAccountMappingType.EmployeeReceivable,
        _ => throw new ArgumentException("No configured accounting treatment exists for this adjustment category.", nameof(category))
    };

    public static PayrollAdjustmentJournalPlan Build(PayrollAdjustmentCategory category, decimal signedAmount,
        int currencyPrecision, Guid componentAccountId, Guid netSalaryPayableAccountId)
    {
        if (!Enum.IsDefined(category) || signedAmount == 0 || currencyPrecision is < 0 or > 4 ||
            componentAccountId == Guid.Empty || netSalaryPayableAccountId == Guid.Empty ||
            componentAccountId == netSalaryPayableAccountId)
            throw new ArgumentException("A supported category, nonzero amount, currency precision, and mapped accounts are required.");
        var amount = decimal.Round(signedAmount, currencyPrecision, MidpointRounding.AwayFromZero);
        if (amount == 0 || amount != signedAmount)
            throw new ArgumentException("Adjustment amount must be nonzero and already rounded to the configured currency precision.", nameof(signedAmount));
        var mapping = RequiredComponentMapping(category);
        if (category == PayrollAdjustmentCategory.OverpaymentRecovery)
        {
            if (signedAmount < 0) throw new ArgumentException("Overpayment recovery must be a positive receivable amount.", nameof(signedAmount));
            IReadOnlyList<PayrollJournalLineRequest> recoveryLines =
            [
                new(componentAccountId, "Recognize employee payroll overpayment receivable", signedAmount, 0),
                new(netSalaryPayableAccountId, "Reverse overpaid payroll expense", 0, signedAmount)
            ];
            return new(mapping, recoveryLines);
        }
        var componentIsExpense = mapping is PayrollAccountMappingType.SalaryExpense or
            PayrollAccountMappingType.OvertimeExpense or PayrollAccountMappingType.AllowanceExpense;
        var componentIsDebit = componentIsExpense == (amount > 0);
        var absolute = decimal.Abs(amount);
        IReadOnlyList<PayrollJournalLineRequest> lines =
        [
            new(componentAccountId, $"Payroll adjustment {category}", componentIsDebit ? absolute : 0, componentIsDebit ? 0 : absolute),
            new(netSalaryPayableAccountId, "Payroll net payable adjustment", componentIsDebit ? 0 : absolute, componentIsDebit ? absolute : 0)
        ];
        return new(mapping, lines);
    }

    public static IReadOnlyList<PayrollJournalLineRequest> BuildCalculationDelta(PayrollCalculationResult original,
        PayrollCalculationResult corrected, int currencyPrecision, IReadOnlyDictionary<PayrollAccountMappingType, Guid> accountIds)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(corrected);
        ArgumentNullException.ThrowIfNull(accountIds);
        if (currencyPrecision is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(currencyPrecision));
        var originalDetails = original.Details.GroupBy(x => x.Code, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(value => value.Amount), StringComparer.Ordinal);
        var correctedDetails = corrected.Details.GroupBy(x => x.Code, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(value => value.Amount), StringComparer.Ordinal);
        var entries = new List<PayrollJournalLineRequest>();
        var contributionDelta = 0m;
        var mappedComponentDelta = 0m;
        foreach (var code in originalDetails.Keys.Union(correctedDetails.Keys, StringComparer.Ordinal))
        {
            var delta = Money(correctedDetails.GetValueOrDefault(code) - originalDetails.GetValueOrDefault(code), currencyPrecision);
            if (delta == 0) continue;
            var mapping = code switch
            {
                "attendance-pay" => (PayrollAccountMappingType.SalaryExpense, Expense: true),
                "overtime-pay" => (PayrollAccountMappingType.OvertimeExpense, Expense: true),
                "tax" => (PayrollAccountMappingType.TaxLiability, Expense: false),
                _ when code.StartsWith("allowance:", StringComparison.Ordinal) => (PayrollAccountMappingType.AllowanceExpense, Expense: true),
                _ when code.StartsWith("deduction:", StringComparison.Ordinal) => (PayrollAccountMappingType.EmployeeDeductionLiability, Expense: false),
                _ when code.StartsWith("employer-contribution:", StringComparison.Ordinal) => (PayrollAccountMappingType.EmployerContributionExpense, Expense: true),
                "base-pay" or "overtime-hours" => ((PayrollAccountMappingType?)null, Expense: false),
                _ => throw new ArgumentException($"Calculation detail '{code}' has no configured payroll accounting treatment.", nameof(corrected))
            };
            if (mapping.Item1 is null) continue;
            Add(mapping.Item1.Value, code, delta, mapping.Expense);
            if (mapping.Item1 is PayrollAccountMappingType.SalaryExpense or PayrollAccountMappingType.OvertimeExpense or PayrollAccountMappingType.AllowanceExpense)
                mappedComponentDelta += delta;
            if (code.StartsWith("employer-contribution:", StringComparison.Ordinal)) contributionDelta += delta;
        }
        if (contributionDelta != 0)
            Add(PayrollAccountMappingType.EmployerContributionLiability, "Employer contribution liability", contributionDelta, false);
        var netDelta = Money(corrected.NetPay - original.NetPay, currencyPrecision);
        var expectedNetDelta = Money(mappedComponentDelta - Money(corrected.TaxWithheld - original.TaxWithheld, currencyPrecision) -
            Money(corrected.OtherDeductions - original.OtherDeductions, currencyPrecision), currencyPrecision);
        if (netDelta != expectedNetDelta || contributionDelta != 0)
            throw new ArgumentException("Recalculated payroll totals do not reconcile to supported account mappings; employer contribution changes require the authorized manual correction path.", nameof(corrected));
        if (netDelta != 0) Add(PayrollAccountMappingType.NetSalaryPayable, "Net salary payable", netDelta, false);
        if (entries.Count < 2 || entries.Sum(x => x.Debit) <= 0 ||
            Money(entries.Sum(x => x.Debit), currencyPrecision) != Money(entries.Sum(x => x.Credit), currencyPrecision))
            throw new ArgumentException("The recalculated correction does not produce a balanced journal with the supplied mapping types.");
        return entries;

        void Add(PayrollAccountMappingType type, string description, decimal signed, bool expense)
        {
            if (!accountIds.TryGetValue(type, out var accountId) || accountId == Guid.Empty)
                throw new ArgumentException($"The {type} account mapping is required for this recalculated correction.", nameof(accountIds));
            var debit = expense == (signed > 0);
            var amount = decimal.Abs(signed);
            entries.Add(new(accountId, description, debit ? amount : 0, debit ? 0 : amount));
        }
    }

    private static decimal Money(decimal amount, int precision) => decimal.Round(amount, precision, MidpointRounding.AwayFromZero);
}
