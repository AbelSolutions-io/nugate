using NuGate.Core;

namespace NuGate.Tool.Tests.Fakes;

/// <summary>Maps a restore-output path to canned packages, without touching Core's real reader.</summary>
internal sealed class FakeResolvedPackageReader : IResolvedPackageReader
{
    private readonly Func<string, IReadOnlyList<PackageIdentity>> _read;
    public readonly List<string> ReadPaths = new();

    public FakeResolvedPackageReader(Func<string, IReadOnlyList<PackageIdentity>> read)
    {
        _read = read;
    }

    public static FakeResolvedPackageReader Empty() => new(_ => Array.Empty<PackageIdentity>());

    public IReadOnlyList<PackageIdentity> ReadResolvedPackages(string path)
    {
        ReadPaths.Add(path);
        return _read(path);
    }
}
