using System.Globalization;
using System.Linq;

namespace NuGate.Core;

/// <summary>
/// Minimal NuGet/SemVer version handling: normalization (for cache keys and leaf matching) and
/// ordering (for choosing which registration page to fetch). Not a full SemVer implementation —
/// just enough to match a resolved version against nuget.org registration data.
/// </summary>
internal static class NuGetVersionUtil
{
    /// <summary>
    /// Normalize a version the way nuget.org does for equality: drop build metadata, lowercase the
    /// prerelease label, trim a trailing zero fourth component, and pad the release to three parts
    /// (so <c>1.2</c>, <c>1.2.0</c>, and <c>1.2.0.0</c> all compare equal).
    /// </summary>
    public static string Normalize(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var value = version.Trim();

        // Strip build metadata (+...).
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value.Substring(0, plus);
        }

        string release;
        string? prerelease = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            release = value.Substring(0, dash);
            prerelease = value.Substring(dash + 1);
        }
        else
        {
            release = value;
        }

        var numbers = release
            .Split('.')
            .Select(part => long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0L)
            .ToList();

        while (numbers.Count > 3 && numbers[numbers.Count - 1] == 0)
        {
            numbers.RemoveAt(numbers.Count - 1);
        }

        while (numbers.Count < 3)
        {
            numbers.Add(0);
        }

        var normalized = string.Join(".", numbers);
        if (prerelease != null)
        {
            normalized += "-" + prerelease.ToLowerInvariant();
        }

        return normalized;
    }

    /// <summary>Compare two versions by SemVer precedence. Inputs may be raw or already normalized.</summary>
    public static int Compare(string a, string b)
    {
        var (releaseA, preA) = Split(Normalize(a));
        var (releaseB, preB) = Split(Normalize(b));

        var max = Math.Max(releaseA.Count, releaseB.Count);
        for (var i = 0; i < max; i++)
        {
            var x = i < releaseA.Count ? releaseA[i] : 0L;
            var y = i < releaseB.Count ? releaseB[i] : 0L;
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }

        var aHasPre = preA.Length > 0;
        var bHasPre = preB.Length > 0;
        if (!aHasPre && !bHasPre)
        {
            return 0;
        }

        // A release version outranks a prerelease of the same numbers.
        if (!aHasPre)
        {
            return 1;
        }

        if (!bHasPre)
        {
            return -1;
        }

        return ComparePrerelease(preA, preB);
    }

    private static (List<long> Release, string Prerelease) Split(string normalized)
    {
        var dash = normalized.IndexOf('-');
        var release = dash >= 0 ? normalized.Substring(0, dash) : normalized;
        var prerelease = dash >= 0 ? normalized.Substring(dash + 1) : string.Empty;

        var numbers = release
            .Split('.')
            .Select(part => long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0L)
            .ToList();

        return (numbers, prerelease);
    }

    private static int ComparePrerelease(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        var max = Math.Max(left.Length, right.Length);

        for (var i = 0; i < max; i++)
        {
            if (i >= left.Length)
            {
                return -1; // fewer identifiers => lower precedence
            }

            if (i >= right.Length)
            {
                return 1;
            }

            var x = left[i];
            var y = right[i];
            var xIsNumber = long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var xValue);
            var yIsNumber = long.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var yValue);

            if (xIsNumber && yIsNumber)
            {
                if (xValue != yValue)
                {
                    return xValue < yValue ? -1 : 1;
                }
            }
            else if (xIsNumber)
            {
                return -1; // numeric identifiers rank below alphanumeric
            }
            else if (yIsNumber)
            {
                return 1;
            }
            else
            {
                var ordinal = string.CompareOrdinal(x, y);
                if (ordinal != 0)
                {
                    return ordinal < 0 ? -1 : 1;
                }
            }
        }

        return 0;
    }
}
