namespace NuGate.Core;

/// <summary>
/// Reads the full resolved dependency graph (direct + transitive) from restore outputs:
/// `obj/project.assets.json` and/or `packages.lock.json`.
/// Floating versions are covered by definition — these files contain resolved versions only.
/// </summary>
public interface IResolvedPackageReader
{
    /// <param name="path">Path to a project.assets.json or packages.lock.json file.</param>
    IReadOnlyList<PackageIdentity> ReadResolvedPackages(string path);
}
