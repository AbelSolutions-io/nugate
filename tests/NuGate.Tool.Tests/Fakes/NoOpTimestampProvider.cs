using NuGate.Core;

namespace NuGate.Tool.Tests.Fakes;

/// <summary>
/// Passed through to a faked <see cref="PolicyEvaluator"/> that never calls it — asserts the
/// tool's own logic never talks to nuget.org directly (that's Core's job).
/// </summary>
internal sealed class NoOpTimestampProvider : INuGetTimestampProvider
{
    public Task<PackageTimestamp?> GetTimestampAsync(PackageIdentity package, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Should not be called when the PolicyEvaluator is faked.");
}
