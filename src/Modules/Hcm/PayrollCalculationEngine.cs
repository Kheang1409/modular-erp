namespace Erp.Modules.Hcm;

using Erp.Modules.Hcm.Contracts;

/// <summary>Versioned, tenant-configured payroll calculation inputs. Rates are configuration, never legal defaults.</summary>
public sealed record PayrollRuleSet(string JurisdictionCode, string Version, bool IsSupported,
    decimal StandardHoursPerPeriod, decimal OvertimeMultiplier, IReadOnlyList<PayrollTaxBand> TaxBands,
    IReadOnlyList<PayrollContributionRule>? EmployerContributions = null,
    int CurrencyPrecision = 2,
    PayrollRoundingMode RoundingMode = PayrollRoundingMode.MidpointAwayFromZero);

public sealed record PayrollTaxBand(decimal AmountLimit, decimal Rate);
public sealed record PayrollContributionRule(string Code, bool IsPercentage, decimal Value,
    PayrollContributionBasis Basis = PayrollContributionBasis.GrossPay, decimal? MaximumAmount = null);
public sealed record PayrollAdjustment(string Code, decimal Amount);
public sealed record PayrollCalculationInput(decimal BasePayForPeriod, decimal AttendanceHours,
    IReadOnlyList<PayrollAdjustment> Allowances, IReadOnlyList<PayrollAdjustment> Deductions,
    IReadOnlyList<PayrollCalculationSegment>? Segments = null);
public sealed record PayrollCalculationSegment(decimal BasePayForPeriod, decimal StandardHoursForPeriod,
    decimal RegularHours, decimal OvertimeHours, IReadOnlyList<PayrollAdjustment> Allowances,
    IReadOnlyList<PayrollAdjustment> Deductions);
public sealed record PayrollCalculationResult(decimal BasePay, decimal AttendancePay,
    decimal OvertimeHours, decimal OvertimePay, decimal Allowances, decimal GrossPay,
    decimal TaxWithheld, decimal OtherDeductions, decimal NetPay,
    IReadOnlyList<PayrollCalculationDetail> Details, decimal EmployerContributions = 0);
public sealed record PayrollCalculationDetail(string Code, decimal Amount);

public sealed class UnsupportedPayrollJurisdictionException(string jurisdictionCode)
    : Exception($"No payroll rules are configured for jurisdiction '{jurisdictionCode}'.");

/// <summary>Deterministic decimal-only calculation. Tax bands and overtime policy come from an explicit versioned ruleset.</summary>
public static class PayrollCalculationEngine
{
    public static PayrollCalculationResult Calculate(PayrollRuleSet rules, PayrollCalculationInput input)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(input);
        if (!rules.IsSupported) throw new UnsupportedPayrollJurisdictionException(rules.JurisdictionCode);
        if (string.IsNullOrWhiteSpace(rules.JurisdictionCode) || string.IsNullOrWhiteSpace(rules.Version))
            throw new ArgumentException("A jurisdiction and immutable rule version are required.", nameof(rules));
        if (rules.StandardHoursPerPeriod <= 0 || rules.OvertimeMultiplier < 1 || rules.TaxBands is null ||
            rules.TaxBands.Any(x => x.AmountLimit <= 0 || x.Rate is < 0 or > 1) ||
            rules.TaxBands.Zip(rules.TaxBands.Skip(1), (a, b) => a.AmountLimit >= b.AmountLimit).Any(x => x) ||
            rules.CurrencyPrecision is < 0 or > 4 || !Enum.IsDefined(rules.RoundingMode))
            throw new ArgumentException("Payroll rules contain invalid hours, overtime, or progressive tax bands.", nameof(rules));
        if (input.BasePayForPeriod < 0 || input.AttendanceHours < 0 || input.Allowances is null || input.Deductions is null ||
            input.Allowances.Concat(input.Deductions).Any(x => string.IsNullOrWhiteSpace(x.Code) || x.Amount < 0))
            throw new ArgumentException("Payroll inputs require nonnegative pay, hours, and named adjustments.", nameof(input));
        if (input.Segments?.Any(segment => segment.BasePayForPeriod < 0 || segment.StandardHoursForPeriod <= 0 ||
                segment.RegularHours < 0 || segment.OvertimeHours < 0 || segment.Allowances is null || segment.Deductions is null ||
                segment.Allowances.Concat(segment.Deductions).Any(component => string.IsNullOrWhiteSpace(component.Code) || component.Amount < 0)) == true ||
            (input.Segments is not null && input.Segments.Sum(segment => segment.RegularHours + segment.OvertimeHours) != input.AttendanceHours))
            throw new ArgumentException("Payroll segments require valid pay, schedule hours, components, and attendance totals.", nameof(input));
        if (input.Segments is not null && input.Segments.Sum(segment => segment.RegularHours) > input.AttendanceHours)
            throw new ArgumentException("Segment regular hours cannot exceed total attendance hours.", nameof(input));
        if (rules.EmployerContributions?.Any(x => string.IsNullOrWhiteSpace(x.Code) || x.Value < 0 ||
                x.MaximumAmount < 0 || !Enum.IsDefined(x.Basis)) == true)
            throw new ArgumentException("Employer contribution rules require a code, nonnegative value and limit, and supported earnings basis.", nameof(rules));

        var segments = input.Segments;
        var ordinaryHours = segments is null ? decimal.Min(input.AttendanceHours, rules.StandardHoursPerPeriod) : segments.Sum(x => x.RegularHours);
        var overtimeHours = segments is null ? decimal.Max(0, input.AttendanceHours - rules.StandardHoursPerPeriod) : segments.Sum(x => x.OvertimeHours);
        var basePay = segments is null ? Money(input.BasePayForPeriod, rules) : Money(segments.Sum(x => Money(x.BasePayForPeriod, rules)), rules);
        var attendancePay = segments is null
            ? Money(input.BasePayForPeriod / rules.StandardHoursPerPeriod * ordinaryHours, rules)
            : Money(segments.Sum(x => Money(x.BasePayForPeriod / x.StandardHoursForPeriod * x.RegularHours, rules)), rules);
        var overtimePay = segments is null
            ? Money(input.BasePayForPeriod / rules.StandardHoursPerPeriod * overtimeHours * rules.OvertimeMultiplier, rules)
            : Money(segments.Sum(x => Money(x.BasePayForPeriod / x.StandardHoursForPeriod * x.OvertimeHours * rules.OvertimeMultiplier, rules)), rules);
        var allowanceItems = segments is null ? input.Allowances : segments.SelectMany(x => x.Allowances).ToArray();
        var deductionItems = segments is null ? input.Deductions : segments.SelectMany(x => x.Deductions).ToArray();
        var allowanceTotal = Money(allowanceItems.Sum(x => Money(x.Amount, rules)), rules);
        var gross = Money(attendancePay + overtimePay + allowanceTotal, rules);
        var tax = Money(CalculateProgressiveTax(gross, rules.TaxBands), rules);
        var deductions = Money(deductionItems.Sum(x => Money(x.Amount, rules)), rules);
        var contributionRules = rules.EmployerContributions ?? [];
        var contributions = contributionRules.Sum(rule =>
        {
            var basis = rule.Basis == PayrollContributionBasis.BasePay ? basePay : gross;
            var amount = rule.IsPercentage ? basis * rule.Value / 100m : rule.Value;
            return Money(rule.MaximumAmount is { } maximum ? decimal.Min(amount, maximum) : amount, rules);
        });
        if (tax + deductions > gross)
            throw new ArgumentException("Configured tax and deductions exceed calculated gross pay.", nameof(input));

        var details = new List<PayrollCalculationDetail>
        {
            new("base-pay", basePay),
            new("attendance-pay", attendancePay),
            new("overtime-hours", overtimeHours),
            new("overtime-pay", overtimePay)
        };
        details.AddRange(allowanceItems.Select(x => new PayrollCalculationDetail($"allowance:{x.Code}", Money(x.Amount, rules))));
        details.Add(new("tax", tax));
        details.AddRange(deductionItems.Select(x => new PayrollCalculationDetail($"deduction:{x.Code}", Money(x.Amount, rules))));
        foreach (var rule in contributionRules)
        {
            var basis = rule.Basis == PayrollContributionBasis.BasePay ? basePay : gross;
            var amount = rule.IsPercentage ? basis * rule.Value / 100m : rule.Value;
            details.Add(new PayrollCalculationDetail($"employer-contribution:{rule.Code}",
                Money(rule.MaximumAmount is { } maximum ? decimal.Min(amount, maximum) : amount, rules)));
        }
        return new(basePay, attendancePay, overtimeHours, overtimePay,
            allowanceTotal, gross, tax, deductions, Money(gross - tax - deductions, rules), details, Money(contributions, rules));
    }

    private static decimal CalculateProgressiveTax(decimal gross, IReadOnlyList<PayrollTaxBand> bands)
    {
        decimal tax = 0;
        decimal lower = 0;
        foreach (var band in bands)
        {
            var taxable = decimal.Max(0, decimal.Min(gross, band.AmountLimit) - lower);
            tax += taxable * band.Rate;
            lower = band.AmountLimit;
            if (gross <= lower) break;
        }
        if (bands.Count > 0 && gross > bands[^1].AmountLimit)
            tax += (gross - bands[^1].AmountLimit) * bands[^1].Rate;
        return tax;
    }

    private static decimal Money(decimal amount, PayrollRuleSet rules) => decimal.Round(amount,
        rules.CurrencyPrecision, rules.RoundingMode == PayrollRoundingMode.MidpointToEven
            ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);
}
