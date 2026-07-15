using System.Threading;
using System.Threading.Tasks;

namespace NuGate.Core;

/// <summary>Why a package version violated the policy.</summary>
public enum ViolationKind
{
    /// <summary>Younger than minAgeDays.</summary>
    TooYoung,

    /// <summary>Version is unlisted on nuget.org — flag-worthy regardless of age.</summary>
    Unlisted,

    /// <summary>Timestamp lookup failed and onApiFailure=fail (fail closed).</summary>
    LookupFailed,
}

/// <summary>One policy violation, with everything needed for an actionable error message.</summary>
public sealed class Violation
{
    public Violation(PackageIdentity package, ViolationKind kind, DateTimeOffset? created, double? ageDays)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        Kind = kind;
        Created = created;
        AgeDays = ageDays;
    }

    public PackageIdentity Package { get; }

    public ViolationKind Kind { get; }

    public DateTimeOffset? Created { get; }

    public double? AgeDays { get; }

    /// <summary>Exact nugate.json allowlist snippet that would override this violation.</summary>
    public string AllowlistHint =>
        $"{{ \"id\": \"{Package.Id}\", \"version\": \"{Package.Version}\", \"expires\": \"<yyyy-MM-dd>\", \"reason\": \"<why>\" }}";
}

/// <summary>Aggregate outcome of one policy evaluation.</summary>
public sealed class PolicyResult
{
    public PolicyResult(IReadOnlyList<Violation> violations, IReadOnlyList<string> warnings, PolicyMode mode)
    {
        Violations = violations ?? throw new ArgumentNullException(nameof(violations));
        Warnings = warnings ?? throw new ArgumentNullException(nameof(warnings));
        Mode = mode;
    }

    public IReadOnlyList<Violation> Violations { get; }

    /// <summary>Non-fatal notices (e.g. lookup failures under onApiFailure=warn, expired allow entries).</summary>
    public IReadOnlyList<string> Warnings { get; }

    public PolicyMode Mode { get; }

    /// <summary>True when the build should fail: violations exist AND mode is Enforce.</summary>
    public bool ShouldFail => Mode == PolicyMode.Enforce && Violations.Count > 0;
}

/// <summary>
/// The gate. Pure policy: no I/O of its own — packages and timestamps come in via the
/// abstractions so every rule is unit-testable with a fixed clock.
/// </summary>
public sealed class PolicyEngine
{
    /// <param name="asOfUtc">Evaluation clock — injected for testability; callers pass DateTimeOffset.UtcNow.</param>
    public Task<PolicyResult> EvaluateAsync(
        NuGateConfig config,
        IReadOnlyList<PackageIdentity> packages,
        INuGetTimestampProvider timestamps,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
        => throw new NotImplementedException("D1: implement policy evaluation.");
}
