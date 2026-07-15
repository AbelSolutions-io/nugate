using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGate.Core;

namespace NuGate.Build.Tests;

/// <summary>In-memory <see cref="IResolvedPackageReader"/> — returns a canned graph or throws.</summary>
internal sealed class FakeReader : IResolvedPackageReader
{
    private readonly IReadOnlyList<PackageIdentity>? _packages;
    private readonly Exception? _throw;

    public FakeReader(IReadOnlyList<PackageIdentity> packages) => _packages = packages;

    public FakeReader(Exception toThrow) => _throw = toThrow;

    public string? LastPath { get; private set; }

    public IReadOnlyList<PackageIdentity> ReadResolvedPackages(string path)
    {
        LastPath = path;
        if (_throw is not null)
        {
            throw _throw;
        }

        return _packages!;
    }
}

/// <summary>A timestamp provider that is never actually consulted (the evaluate delegate is faked).</summary>
internal sealed class UnusedTimestampProvider : INuGetTimestampProvider
{
    public Task<PackageTimestamp?> GetTimestampAsync(PackageIdentity package, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The fake evaluate delegate should not call the provider.");
}
