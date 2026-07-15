using System.Threading;
using System.Threading.Tasks;

namespace NuGate.Core;

/// <summary>
/// Resolves catalog `created` timestamps for package versions from nuget.org,
/// backed by a local cache (timestamps are immutable — cache forever).
/// </summary>
public interface INuGetTimestampProvider
{
    /// <returns>Timestamp metadata, or null when the package/version is unknown to nuget.org
    /// (e.g. private-feed packages that escaped an exemptPrefixes entry).</returns>
    /// <exception cref="TimestampLookupException">The API could not be queried — the caller applies
    /// the configured <see cref="ApiFailureMode"/>.</exception>
    Task<PackageTimestamp?> GetTimestampAsync(PackageIdentity package, CancellationToken cancellationToken);
}

/// <summary>Raised when nuget.org metadata cannot be retrieved (network/API failure, not a 404).</summary>
public class TimestampLookupException : Exception
{
    public TimestampLookupException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
