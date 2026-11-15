using Erp.Modules.Hcm.Contracts;

namespace Erp.Modules.Hcm;

public static class AttendanceBreakPolicy
{
    public static decimal CalculatePaidHours(DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc,
        IReadOnlyList<AttendanceBreakInput> breaks)
    {
        if (endedAtUtc <= startedAtUtc) throw new ArgumentException("A worked shift must have positive duration.", nameof(endedAtUtc));
        var ordered = breaks.OrderBy(x => x.StartedAtUtc).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            if (item.EndedAtUtc <= item.StartedAtUtc || item.StartedAtUtc < startedAtUtc || item.EndedAtUtc > endedAtUtc ||
                (index > 0 && ordered[index - 1].EndedAtUtc > item.StartedAtUtc))
                throw new ArgumentException("Breaks must be positive, within the shift, and may not overlap.", nameof(breaks));
        }
        var unpaidHours = ordered.Where(x => !x.Paid).Sum(x => (decimal)(x.EndedAtUtc - x.StartedAtUtc).TotalHours);
        return decimal.Round((decimal)(endedAtUtc - startedAtUtc).TotalHours - unpaidHours, 2, MidpointRounding.AwayFromZero);
    }
}
