using Erp.Modules.Hcm;
using Erp.Modules.Hcm.Contracts;

namespace Erp.Hcm.UnitTests;

public sealed class PayrollProrationCalculatorTests
{
    [Fact]
    public void CalendarProrationIncludesLeapDayAndCalendarBoundaries()
    {
        var factor = PayrollProrationCalculator.SegmentFactor(PayrollProrationMethod.CalendarDays,
            new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29),
            new DateOnly(2024, 2, 15), new DateOnly(2024, 2, 29),
            [new DateOnly(2024, 2, 1)]);

        Assert.Equal(15m / 29m, factor);
    }

    [Fact]
    public void ScheduledWorkingDayProrationUsesOnlyExpectedDates()
    {
        var factor = PayrollProrationCalculator.SegmentFactor(PayrollProrationMethod.ScheduledWorkingDays,
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31),
            new DateOnly(2026, 5, 16), new DateOnly(2026, 5, 31),
            [new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 11), new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 25)]);

        Assert.Equal(0.5m, factor);
    }

    [Theory]
    [InlineData(PayrollDateBoundaryTreatment.Inclusive, PayrollDateBoundaryTreatment.Inclusive, true, true)]
    [InlineData(PayrollDateBoundaryTreatment.Exclusive, PayrollDateBoundaryTreatment.Inclusive, false, true)]
    [InlineData(PayrollDateBoundaryTreatment.Inclusive, PayrollDateBoundaryTreatment.Exclusive, true, false)]
    public void EmploymentDateBoundariesAreExplicit(PayrollDateBoundaryTreatment hireTreatment,
        PayrollDateBoundaryTreatment terminationTreatment, bool employedOnHireDate, bool employedOnEndDate)
    {
        var hireDate = new DateOnly(2026, 1, 10);
        var endDate = new DateOnly(2026, 1, 20);

        Assert.Equal(employedOnHireDate, PayrollProrationCalculator.IsEmployedOn(hireDate, hireDate,
            endDate, hireTreatment, terminationTreatment));
        Assert.Equal(employedOnEndDate, PayrollProrationCalculator.IsEmployedOn(endDate, hireDate,
            endDate, hireTreatment, terminationTreatment));
    }
}
