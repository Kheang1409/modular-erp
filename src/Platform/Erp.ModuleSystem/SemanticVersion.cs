using System.Globalization;
using System.Text.RegularExpressions;

namespace Erp.ModuleSystem;

internal sealed partial record SemanticVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<SemanticVersion>
{
    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        var match = VersionPattern().Match(value ?? string.Empty);
        if (!match.Success)
        {
            version = null;
            return false;
        }

        version = new(
            int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture),
            match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;

        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var comparison = CompareIdentifier(left[index], right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
        if (leftNumeric && rightNumeric) return leftNumber.CompareTo(rightNumber);
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    [GeneratedRegex(
        "^(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

internal static partial class SemanticVersionRange
{
    public static bool Contains(string range, SemanticVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        var expressions = range.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return expressions.Length > 0 && expressions.All(expression => Matches(expression, version));
    }

    private static bool Matches(string expression, SemanticVersion version)
    {
        var match = ComparatorPattern().Match(expression);
        if (!match.Success || !SemanticVersion.TryParse(match.Groups["version"].Value, out var expected))
        {
            throw new InvalidOperationException($"Invalid module version range '{expression}'.");
        }

        var comparison = version.CompareTo(expected);
        return match.Groups["operator"].Value switch
        {
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            "" or "=" => comparison == 0,
            _ => false
        };
    }

    [GeneratedRegex("^(?<operator>>=|<=|>|<|=)?(?<version>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ComparatorPattern();
}
