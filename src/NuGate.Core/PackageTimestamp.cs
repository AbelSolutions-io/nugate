namespace NuGate.Core;

/// <summary>
/// Publication metadata for one package version, sourced from the nuget.org catalog.
/// IMPORTANT: <see cref="Created"/> is the immutable catalog `created` timestamp — never the
/// registration `published` field, which nuget.org resets to 1900-01-01 when a package is
/// unlisted (and compromised versions get unlisted after takedown).
/// </summary>
public sealed class PackageTimestamp
{
    public PackageTimestamp(DateTimeOffset created, bool isListed)
    {
        Created = created;
        IsListed = isListed;
    }

    /// <summary>Immutable catalog creation time (UTC).</summary>
    public DateTimeOffset Created { get; }

    /// <summary>False when the version has been unlisted — itself a flag-worthy signal.</summary>
    public bool IsListed { get; }
}
