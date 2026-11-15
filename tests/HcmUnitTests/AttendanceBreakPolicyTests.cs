using Erp.Modules.Hcm;
using Erp.Modules.Hcm.Contracts;

namespace Erp.Hcm.UnitTests;

public sealed class AttendanceBreakPolicyTests
{
    [Fact]
    public void PaidBreaksRemainPayableAndUnpaidBreaksAreDeducted()
    {
        var shiftStart = new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
        var shiftEnd = shiftStart.AddHours(9);
        var breaks = new[]
        {
            new AttendanceBreakInput(shiftStart.AddHours(2), shiftStart.AddHours(2.5), Paid: true),
            new AttendanceBreakInput(shiftStart.AddHours(5), shiftStart.AddHours(6), Paid: false)
        };

        Assert.Equal(8m, AttendanceBreakPolicy.CalculatePaidHours(shiftStart, shiftEnd, breaks));
    }

    [Fact]
    public void OverlappingBreaksAreRejected()
    {
        var start = new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => AttendanceBreakPolicy.CalculatePaidHours(start, start.AddHours(8),
        [
            new(start.AddHours(1), start.AddHours(2), false),
            new(start.AddHours(1.5), start.AddHours(2.5), false)
        ]));
    }
}
