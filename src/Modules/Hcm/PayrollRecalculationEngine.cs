using Erp.Modules.Hcm.Contracts;

namespace Erp.Modules.Hcm;

public sealed record RecalculatedPayrollAdjustment(decimal Amount, PayrollCalculationResult CorrectedCalculation,
    IReadOnlyList<PayrollCompensationSegmentSnapshot> CorrectedSegments);

/// <summary>Replays an original employee snapshot under its frozen rules and applies one typed source correction.</summary>
public static class PayrollRecalculationEngine
{
    public static RecalculatedPayrollAdjustment Recalculate(PayrollRunCalculationSnapshot snapshot,
        Guid employeeId, PayrollAdjustmentCategory category, DateOnly effectiveDate,
        PayrollRecalculationCorrectionInput correction)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(correction);
        var employee = snapshot.Employees.SingleOrDefault(x => x.EmployeeId == employeeId)
            ?? throw new ArgumentException("Employee is absent from the original payroll snapshot.", nameof(employeeId));
        if (!snapshot.PayrollRules.IsSupported)
            throw new ArgumentException($"Historical jurisdiction '{snapshot.PayrollRules.JurisdictionCode}' is not configured for safe recalculation.", nameof(snapshot));
        if (!snapshot.PayrollRules.IsSupported)
            throw new ArgumentException($"Historical jurisdiction '{snapshot.PayrollRules.JurisdictionCode}' is not configured for safe recalculation.", nameof(snapshot));
        var segments = employee.Segments?.ToArray();
        if (segments is null || segments.Length == 0)
            throw new ArgumentException("The original payroll snapshot lacks effective-dated segment inputs needed for safe recalculation.", nameof(snapshot));
        var matchingSegments = segments.Select((segment, index) => (segment, index))
            .Where(x => effectiveDate >= x.segment.StartDate && effectiveDate <= x.segment.EndDate).ToArray();
        if (matchingSegments.Length != 1)
            throw new ArgumentException("The correction effective date must be covered by exactly one compensation segment in the original snapshot.", nameof(effectiveDate));
        var targetIndex = matchingSegments[0].index;
        ValidateShape(category, correction);

        var target = segments[targetIndex];
        var compensation = target.Compensation;
        var regularHours = target.RegularHours;
        var overtimeHours = target.OvertimeHours;
        var allowances = compensation.Allowances.ToArray();
        var deductions = compensation.Deductions.ToArray();
        switch (category)
        {
            case PayrollAdjustmentCategory.SalaryCorrection:
            case PayrollAdjustmentCategory.RetroactiveCompensationCorrection:
                compensation = compensation with { BasePayForPeriod = Nonnegative(correction.CorrectedBasePayForPeriod!.Value, "correctedBasePayForPeriod") };
                break;
            case PayrollAdjustmentCategory.OvertimeCorrection:
                overtimeHours = Nonnegative(correction.CorrectedOvertimeHours!.Value, "correctedOvertimeHours");
                break;
            case PayrollAdjustmentCategory.AttendanceCorrection:
                regularHours = Nonnegative(correction.CorrectedRegularHours!.Value, "correctedRegularHours");
                overtimeHours = Nonnegative(correction.CorrectedOvertimeHours!.Value, "correctedOvertimeHours");
                if (regularHours + overtimeHours > target.StandardHoursForPeriod)
                    throw new ArgumentException("Corrected attendance exceeds the frozen scheduled hours; use an explicitly supported overtime correction for excess hours.", nameof(correction));
                break;
            case PayrollAdjustmentCategory.AllowanceCorrection:
                allowances = ReplaceComponent(allowances, correction.ComponentCode!, correction.CorrectedComponentAmount!.Value);
                compensation = compensation with { Allowances = allowances };
                break;
            case PayrollAdjustmentCategory.DeductionCorrection:
                deductions = ReplaceComponent(deductions, correction.ComponentCode!, correction.CorrectedComponentAmount!.Value);
                compensation = compensation with { Deductions = deductions };
                break;
            default:
                throw new ArgumentException("This category requires the authorized manual correction path.", nameof(category));
        }

        if (category == PayrollAdjustmentCategory.OvertimeCorrection && regularHours + overtimeHours != target.RegularHours + target.OvertimeHours)
            throw new ArgumentException("An overtime correction must reclassify the same frozen total worked hours. Use attendance correction when total hours change.", nameof(correction));
        if (category == PayrollAdjustmentCategory.AttendanceCorrection &&
            ((regularHours > target.RegularHours && overtimeHours < target.OvertimeHours) ||
             (regularHours < target.RegularHours && overtimeHours > target.OvertimeHours)))
            throw new ArgumentException("This attendance correction crosses the historical regular/overtime boundary and cannot be recalculated safely from stored inputs.", nameof(correction));

        segments[targetIndex] = target with
        {
            Compensation = compensation,
            RegularHours = regularHours,
            OvertimeHours = overtimeHours,
            Allowances = allowances.Select(x => x with { Amount = x.Amount * target.ProrationFactor }).ToArray(),
            Deductions = deductions.Select(x => x with { Amount = x.Amount * target.ProrationFactor }).ToArray()
        };
        var rulesDto = snapshot.PayrollRules;
        var rules = new PayrollRuleSet(rulesDto.JurisdictionCode, rulesDto.Version, rulesDto.IsSupported,
            rulesDto.StandardHoursPerPeriod, rulesDto.OvertimeMultiplier,
            rulesDto.TaxBands.Select(x => new PayrollTaxBand(x.AmountLimit, x.Rate)).ToArray(),
            (rulesDto.EmployerContributions ?? []).Select(x => new PayrollContributionRule(x.Code, x.IsPercentage,
                x.Value, x.Basis, x.MaximumAmount)).ToArray(), rulesDto.CurrencyPrecision, rulesDto.RoundingMode);
        var inputs = segments.Select(segment => new PayrollCalculationSegment(
            segment.Compensation.BasePayForPeriod * segment.ProrationFactor,
            segment.StandardHoursForPeriod, segment.RegularHours, segment.OvertimeHours,
            segment.Allowances.Select(x => new PayrollAdjustment(x.Code, x.Amount)).ToArray(),
            segment.Deductions.Select(x => new PayrollAdjustment(x.Code, x.Amount)).ToArray())).ToArray();
        var attendanceHours = inputs.Sum(x => x.RegularHours + x.OvertimeHours);
        var corrected = PayrollCalculationEngine.Calculate(rules, new PayrollCalculationInput(0,
            attendanceHours, [], [], inputs));
        var original = employee.Calculation;
        var amount = category switch
        {
            PayrollAdjustmentCategory.SalaryCorrection or PayrollAdjustmentCategory.RetroactiveCompensationCorrection =>
                corrected.AttendancePay - original.AttendancePay,
            PayrollAdjustmentCategory.OvertimeCorrection => corrected.OvertimePay - original.OvertimePay,
            PayrollAdjustmentCategory.AllowanceCorrection => corrected.Allowances - original.Allowances,
            PayrollAdjustmentCategory.DeductionCorrection => corrected.OtherDeductions - original.OtherDeductions,
            PayrollAdjustmentCategory.AttendanceCorrection => corrected.AttendancePay - original.AttendancePay,
            _ => throw new ArgumentException("This category requires the authorized manual correction path.", nameof(category))
        };
        amount = decimal.Round(amount, snapshot.PayrollRules.CurrencyPrecision,
            snapshot.PayrollRules.RoundingMode == PayrollRoundingMode.MidpointToEven ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);
        if (amount == 0)
            throw new ArgumentException("Corrected source inputs produce no monetary change at the configured currency precision.", nameof(correction));
        return new(amount, corrected, segments);
    }

    private static void ValidateShape(PayrollAdjustmentCategory category, PayrollRecalculationCorrectionInput correction)
    {
        var hasBase = correction.CorrectedBasePayForPeriod.HasValue;
        var hasRegular = correction.CorrectedRegularHours.HasValue;
        var hasOvertime = correction.CorrectedOvertimeHours.HasValue;
        var hasComponent = !string.IsNullOrWhiteSpace(correction.ComponentCode) && correction.CorrectedComponentAmount.HasValue;
        var expected = category switch
        {
            PayrollAdjustmentCategory.SalaryCorrection or PayrollAdjustmentCategory.RetroactiveCompensationCorrection =>
                (Base: true, Regular: false, Overtime: false, Component: false),
            PayrollAdjustmentCategory.OvertimeCorrection => (Base: false, Regular: false, Overtime: true, Component: false),
            PayrollAdjustmentCategory.AttendanceCorrection => (Base: false, Regular: true, Overtime: true, Component: false),
            PayrollAdjustmentCategory.AllowanceCorrection or PayrollAdjustmentCategory.DeductionCorrection =>
                (Base: false, Regular: false, Overtime: false, Component: true),
            _ => throw new ArgumentException("This category requires the authorized manual correction path.", nameof(category))
        };
        if (hasBase != expected.Base || hasRegular != expected.Regular || hasOvertime != expected.Overtime || hasComponent != expected.Component ||
            (correction.CorrectedComponentAmount.HasValue && !hasComponent) ||
            (correction.ComponentCode is not null && !hasComponent))
            throw new ArgumentException("Correction inputs must match the selected category exactly.", nameof(correction));
    }

    private static PayrollComponentInput[] ReplaceComponent(PayrollComponentInput[] components, string code, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(code) || amount < 0) throw new ArgumentException("A component code and nonnegative corrected amount are required.");
        var matches = components.Select((value, index) => (value, index))
            .Where(x => StringComparer.Ordinal.Equals(x.value.Code, code)).ToArray();
        if (matches.Length != 1) throw new ArgumentException("The component code must occur exactly once in the original compensation snapshot.");
        components[matches[0].index] = matches[0].value with { Amount = amount };
        return components;
    }

    private static decimal Nonnegative(decimal value, string parameter)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameter, "Corrected source values cannot be negative.");
        return value;
    }
}
