using System.Globalization;
using NuGate.Core;

namespace NuGate.Build;

/// <summary>
/// Turns a <see cref="Violation"/> into a single-line, actionable build message. Kept free of any
/// MSBuild types so the exact wording is unit-testable without a task host.
/// </summary>
/// <remarks>
/// Copy discipline (frozen house rule): NuGate "enforces a dependency age policy". These strings
/// never say protects / prevents / secures / detects.
/// </remarks>
internal static class ViolationFormatter
{
    /// <summary>
    /// Format one violation. Message carries: package id + version, the age in days, the policy
    /// (minAgeDays), and the exact nugate.json allowlist snippet to override it
    /// (<see cref="Violation.AllowlistHint"/>).
    /// </summary>
    public static string Format(Violation violation, int minAgeDays)
    {
        if (violation is null)
        {
            throw new ArgumentNullException(nameof(violation));
        }

        var id = violation.Package.Id;
        var version = violation.Package.Version;
        var hint = violation.AllowlistHint;

        switch (violation.Kind)
        {
            case ViolationKind.TooYoung:
            {
                var age = FormatAge(violation.AgeDays);
                return $"NuGate: {id} {version} is {age} days old; the dependency age policy requires "
                     + $"at least {minAgeDays} days. To allow this exact version, add to nugate.json \"allow\": "
                     + $"{hint}";
            }

            case ViolationKind.Unlisted:
            {
                return $"NuGate: {id} {version} is unlisted on nuget.org; the dependency age policy treats "
                     + $"an unlisted version as a violation regardless of age. To allow this exact version, "
                     + $"add to nugate.json \"allow\": {hint}";
            }

            case ViolationKind.LookupFailed:
            {
                return $"NuGate: could not resolve the publication timestamp for {id} {version} from "
                     + $"nuget.org; the dependency age policy is fail-closed (onApiFailure=fail). To allow "
                     + $"this exact version, add to nugate.json \"allow\": {hint}";
            }

            default:
            {
                // Defensive: any future ViolationKind still produces an actionable, override-able line.
                var age = FormatAge(violation.AgeDays);
                return $"NuGate: {id} {version} violates the dependency age policy (age {age} days, "
                     + $"minimum {minAgeDays}). To allow this exact version, add to nugate.json \"allow\": {hint}";
            }
        }
    }

    private static string FormatAge(double? ageDays)
        => ageDays.HasValue
            ? ageDays.Value.ToString("0.#", CultureInfo.InvariantCulture)
            : "an unknown number of";
}
