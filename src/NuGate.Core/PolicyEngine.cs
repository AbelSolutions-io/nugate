using System.Collections.Concurrent;
using System.Linq;
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
    /// <summary>Bounded concurrency for timestamp lookups (nuget.org is one small API).</summary>
    private const int LookupParallelism = 6;

    /// <param name="asOfUtc">Evaluation clock — injected for testability; callers pass DateTimeOffset.UtcNow.</param>
    public async Task<PolicyResult> EvaluateAsync(
        NuGateConfig config,
        IReadOnlyList<PackageIdentity> packages,
        INuGetTimestampProvider timestamps,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (timestamps is null)
        {
            throw new ArgumentNullException(nameof(timestamps));
        }

        var violations = new List<Violation>();
        var warnings = new List<string>();

        // Defensive dedupe by id (case-insensitive) + version, preserving first-seen order.
        var distinct = new List<PackageIdentity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages ?? Array.Empty<PackageIdentity>())
        {
            if (package is null)
            {
                continue;
            }

            var key = package.Id.ToLowerInvariant() + "/" + package.Version.ToLowerInvariant();
            if (seen.Add(key))
            {
                distinct.Add(package);
            }
        }

        // Classify each package. exemptPrefixes and active allowlist entries pass without a lookup.
        // Expired allowlist entries produce a warning but still get evaluated on their merits.
        var plan = new List<PlanItem>();
        foreach (var package in distinct)
        {
            if (IsExempt(config, package))
            {
                continue;
            }

            var allow = MatchAllow(config, package);
            if (allow != null)
            {
                var isActive = allow.Expires is null || asOfUtc <= allow.Expires.Value;
                if (isActive)
                {
                    continue; // allowlisted and unexpired => pass, no lookup
                }

                var expiryText = allow.Expires!.Value.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                plan.Add(new PlanItem(
                    package,
                    $"Allowlist entry for {package.Id} {package.Version} expired on {expiryText}; it no longer grants a pass."));
            }
            else
            {
                plan.Add(new PlanItem(package, expiredAllowWarning: null));
            }
        }

        // Fan out the timestamp lookups with bounded parallelism.
        var outcomes = new ConcurrentDictionary<PackageIdentity, Outcome>();
        using (var gate = new SemaphoreSlim(LookupParallelism))
        {
            var lookups = plan.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var timestamp = await timestamps.GetTimestampAsync(item.Package, cancellationToken).ConfigureAwait(false);
                    outcomes[item.Package] = timestamp is null
                        ? Outcome.NotFound()
                        : Outcome.Found(timestamp);
                }
                catch (TimestampLookupException ex)
                {
                    outcomes[item.Package] = Outcome.Failed(ex.Message);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(lookups).ConfigureAwait(false);
        }

        // Deterministic second pass: build violations/warnings in stable package order.
        foreach (var item in plan)
        {
            if (item.ExpiredAllowWarning != null)
            {
                warnings.Add(item.ExpiredAllowWarning);
            }

            var outcome = outcomes[item.Package];
            switch (outcome.Kind)
            {
                case OutcomeKind.Found:
                    var timestamp = outcome.Timestamp!;
                    var ageDays = (asOfUtc - timestamp.Created).TotalDays;

                    if (!timestamp.IsListed)
                    {
                        // Unlisted is flag-worthy regardless of age.
                        violations.Add(new Violation(item.Package, ViolationKind.Unlisted, timestamp.Created, ageDays));
                    }
                    else if (ageDays < config.MinAgeDays)
                    {
                        // age >= minAgeDays passes; exactly minAgeDays days old is NOT a violation.
                        violations.Add(new Violation(item.Package, ViolationKind.TooYoung, timestamp.Created, ageDays));
                    }

                    break;

                case OutcomeKind.NotFound:
                case OutcomeKind.Failed:
                default:
                    if (config.OnApiFailure == ApiFailureMode.Fail)
                    {
                        // Fail closed: an unverifiable package is a violation.
                        violations.Add(new Violation(item.Package, ViolationKind.LookupFailed, created: null, ageDays: null));
                    }
                    else
                    {
                        warnings.Add(outcome.Kind == OutcomeKind.Failed
                            ? $"Could not verify {item.Package.Id} {item.Package.Version}: {outcome.Error} (onApiFailure=warn)."
                            : $"{item.Package.Id} {item.Package.Version} is unknown to nuget.org; age could not be verified (onApiFailure=warn).");
                    }

                    break;
            }
        }

        return new PolicyResult(violations, warnings, config.Mode);
    }

    private static bool IsExempt(NuGateConfig config, PackageIdentity package)
    {
        if (config.ExemptPrefixes is null)
        {
            return false;
        }

        foreach (var prefix in config.ExemptPrefixes)
        {
            if (!string.IsNullOrEmpty(prefix)
                && package.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static AllowEntry? MatchAllow(NuGateConfig config, PackageIdentity package)
    {
        if (config.Allow is null)
        {
            return null;
        }

        foreach (var entry in config.Allow)
        {
            if (entry != null
                && string.Equals(entry.Id, package.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Version, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private sealed class PlanItem
    {
        public PlanItem(PackageIdentity package, string? expiredAllowWarning)
        {
            Package = package;
            ExpiredAllowWarning = expiredAllowWarning;
        }

        public PackageIdentity Package { get; }

        public string? ExpiredAllowWarning { get; }
    }

    private enum OutcomeKind
    {
        Found,
        NotFound,
        Failed,
    }

    private readonly struct Outcome
    {
        private Outcome(OutcomeKind kind, PackageTimestamp? timestamp, string? error)
        {
            Kind = kind;
            Timestamp = timestamp;
            Error = error;
        }

        public OutcomeKind Kind { get; }

        public PackageTimestamp? Timestamp { get; }

        public string? Error { get; }

        public static Outcome Found(PackageTimestamp timestamp) => new(OutcomeKind.Found, timestamp, null);

        public static Outcome NotFound() => new(OutcomeKind.NotFound, null, null);

        public static Outcome Failed(string error) => new(OutcomeKind.Failed, null, error);
    }
}
