namespace NuGate.Core;

/// <summary>Policy failure behavior when the nuget.org API cannot be reached.</summary>
public enum ApiFailureMode
{
    /// <summary>Fail the build (default — fail closed).</summary>
    Fail,

    /// <summary>Warn and continue (documented opt-out for shops that won't tolerate nuget.org outages).</summary>
    Warn,
}

/// <summary>Enforcement mode for the whole gate.</summary>
public enum PolicyMode
{
    /// <summary>Violations fail the build (default).</summary>
    Enforce,

    /// <summary>Violations are reported but do not fail — rollout mode.</summary>
    Warn,
}

/// <summary>One allowlist entry. Entries may carry an expiry so exceptions don't fossilize.</summary>
public sealed class AllowEntry
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Exact version this exception applies to.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Optional ISO date after which this entry no longer applies.</summary>
    public DateTimeOffset? Expires { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// Deserialized `nugate.json` (repo root). Defaults match the spec: 7 days, enforce, fail-closed.
/// </summary>
public sealed class NuGateConfig
{
    public const string DefaultFileName = "nugate.json";

    public int MinAgeDays { get; set; } = 7;

    public PolicyMode Mode { get; set; } = PolicyMode.Enforce;

    public ApiFailureMode OnApiFailure { get; set; } = ApiFailureMode.Fail;

    public IList<AllowEntry> Allow { get; set; } = new List<AllowEntry>();

    /// <summary>Package-id prefixes exempt from the gate (internal/private-feed packages).</summary>
    public IList<string> ExemptPrefixes { get; set; } = new List<string>();

    /// <summary>
    /// Load config from a `nugate.json` file. Unknown properties are an error (typo protection);
    /// a missing file yields spec defaults.
    /// </summary>
    public static NuGateConfig Load(string? path)
        => throw new NotImplementedException("D1: implement config load + validation.");
}
