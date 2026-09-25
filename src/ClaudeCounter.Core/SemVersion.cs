using System.Globalization;

namespace ClaudeCounter.Core;

/// <summary>
/// Just enough semantic versioning to answer "is the release newer than me?".
/// Handles a leading "v", the three numeric parts, and prerelease ordering
/// (1.0.0-rc.1 sorts before 1.0.0). Build metadata is ignored, as the spec
/// requires. Not worth a NuGet dependency.
/// </summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string? Prerelease)
    : IComparable<SemVersion>
{
    public bool IsPrerelease => Prerelease is not null;

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion(0, 0, 0, null);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        // Build metadata never affects precedence.
        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];

        string? prerelease = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            if (prerelease.Length == 0)
                return false;
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3)
            return false;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return false;
            numbers[i] = n;
        }

        version = new SemVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
            return 1;

        var byNumber = Major.CompareTo(other.Major);
        if (byNumber != 0) return byNumber;
        byNumber = Minor.CompareTo(other.Minor);
        if (byNumber != 0) return byNumber;
        byNumber = Patch.CompareTo(other.Patch);
        if (byNumber != 0) return byNumber;

        // A release outranks any prerelease of the same numbers.
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            // Fewer identifiers sorts lower when everything before is equal.
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;

            var aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);

            int result;
            if (aNumeric && bNumeric)
                result = an.CompareTo(bn);
            else if (aNumeric)
                result = -1;              // numeric identifiers sort below alphanumeric
            else if (bNumeric)
                result = 1;
            else
                result = string.CompareOrdinal(a[i], b[i]);

            if (result != 0) return result;
        }

        return 0;
    }

    public override string ToString() =>
        Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
