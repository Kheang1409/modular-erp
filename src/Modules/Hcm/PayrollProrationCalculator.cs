using Erp.Modules.Hcm.Contracts;

namespace Erp.Modules.Hcm;

public static class PayrollProrationCalculator
{
    public static decimal SegmentFactor(PayrollProrationMethod method, DateOnly periodStart, DateOnly periodEnd,
        DateOnly segmentStart, DateOnly segmentEnd, IReadOnlyList<DateOnly> expectedWorkDates)
    {
        if (periodEnd < periodStart || segmentEnd < segmentStart || segmentStart < periodStart || segmentEnd > periodEnd)
            throw new ArgumentException("Proration period and segment dates must be valid and contained within the period.");
        ArgumentNullException.ThrowIfNull(expectedWorkDates);
        if (expectedWorkDates.Count == 0 || expectedWorkDates.Any(date => date < periodStart || date > periodEnd) ||
            expectedWorkDates.Distinct().Count() != expectedWorkDates.Count)
            throw new ArgumentException("Expected work dates must be nonempty, unique, and within the payroll period.", nameof(expectedWorkDates));

        return method switch
        {
            PayrollProrationMethod.CalendarDays =>
                (decimal)(segmentEnd.DayNumber - segmentStart.DayNumber + 1) /
                (periodEnd.DayNumber - periodStart.DayNumber + 1),
            PayrollProrationMethod.ScheduledWorkingDays =>
                (decimal)expectedWorkDates.Count(date => date >= segmentStart && date <= segmentEnd) /
                expectedWorkDates.Count,
            _ => throw new ArgumentOutOfRangeException(nameof(method), method,
                "Legacy attendance-hour calculation is not a configurable proration method.")
        };
    }

    public static bool IsEmployedOn(DateOnly date, DateOnly hireDate, DateOnly? employmentEndDate,
        PayrollDateBoundaryTreatment hireDateTreatment, PayrollDateBoundaryTreatment terminationDateTreatment)
    {
        if (!Enum.IsDefined(hireDateTreatment) || !Enum.IsDefined(terminationDateTreatment))
            throw new ArgumentOutOfRangeException(nameof(hireDateTreatment), "Employment boundary treatments must be valid.");
        var afterHire = hireDateTreatment == PayrollDateBoundaryTreatment.Inclusive ? date >= hireDate : date > hireDate;
        var beforeEnd = employmentEndDate is null ||
            (terminationDateTreatment == PayrollDateBoundaryTreatment.Inclusive
                ? date <= employmentEndDate.Value
                : date < employmentEndDate.Value);
        return afterHire && beforeEnd;
    }
}
