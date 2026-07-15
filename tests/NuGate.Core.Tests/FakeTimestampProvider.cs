using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGate.Core;

namespace NuGate.Core.Tests;

/// <summary>
/// In-memory <see cref="INuGetTimestampProvider"/> for policy tests. Added entries return a
/// timestamp; entries marked to fail throw <see cref="TimestampLookupException"/>; anything else
/// returns null (unknown to nuget.org). Records which packages were queried so tests can assert
/// that exempt/allowlisted packages never trigger a lookup.
/// </summary>
internal sealed class FakeTimestampProvider : INuGetTimestampProvider
{
    private readonly Dictionary<string, PackageTimestamp> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queried = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string id, string version, DateTimeOffset created, bool listed = true)
        => _known[Key(id, version)] = new PackageTimestamp(created, listed);

    public void Fail(string id, string version)
        => _failures.Add(Key(id, version));

    public bool WasQueried(string id, string version)
        => _queried.ContainsKey(Key(id, version));

    public Task<PackageTimestamp?> GetTimestampAsync(PackageIdentity package, CancellationToken cancellationToken)
    {
        var key = Key(package.Id, package.Version);
        _queried[key] = 1;

        if (_failures.Contains(key))
        {
            throw new TimestampLookupException($"Simulated lookup failure for {package}.");
        }

        return Task.FromResult(_known.TryGetValue(key, out var timestamp) ? timestamp : null);
    }

    private static string Key(string id, string version) => id + "/" + version;
}
