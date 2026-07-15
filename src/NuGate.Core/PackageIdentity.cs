namespace NuGate.Core;

/// <summary>A resolved package (exact version, post-restore) from the dependency graph.</summary>
public sealed class PackageIdentity
{
    public PackageIdentity(string id, string version)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Version = version ?? throw new ArgumentNullException(nameof(version));
    }

    public string Id { get; }

    /// <summary>The resolved version string exactly as it appears in assets/lock files.</summary>
    public string Version { get; }

    public override string ToString() => $"{Id} {Version}";
}
