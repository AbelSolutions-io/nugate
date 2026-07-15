using NuGate.Core;
using Xunit;

namespace NuGate.Build.Tests;

public class CoreServicesTests
{
    [Fact]
    public void TryCreateReader_returns_a_live_instance()
    {
        var ok = CoreServices.TryCreateReader(out var reader, out var error);

        Assert.True(ok, error);
        Assert.IsAssignableFrom<IResolvedPackageReader>(reader);
    }

    [Fact]
    public void TryCreateTimestampProvider_returns_a_live_instance()
    {
        var ok = CoreServices.TryCreateTimestampProvider(out var provider, out var error);

        Assert.True(ok, error);
        Assert.IsAssignableFrom<INuGetTimestampProvider>(provider);
    }
}
