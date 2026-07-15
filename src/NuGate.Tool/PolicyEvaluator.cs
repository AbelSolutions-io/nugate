using NuGate.Core;

namespace NuGate.Tool;

/// <summary>
/// Matches the shape of <see cref="PolicyEngine.EvaluateAsync"/>. Program.cs wires the real
/// engine; tests inject a fake that returns a canned <see cref="PolicyResult"/> so exit-code and
/// output-formatting logic is verifiable without touching Core's (currently throwing) stubs.
/// </summary>
public delegate Task<PolicyResult> PolicyEvaluator(
    NuGateConfig config,
    IReadOnlyList<PackageIdentity> packages,
    INuGetTimestampProvider timestamps,
    DateTimeOffset asOfUtc,
    CancellationToken cancellationToken);
