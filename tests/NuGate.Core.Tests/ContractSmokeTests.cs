using NuGate.Core;
using Xunit;

namespace NuGate.Core.Tests;

/// <summary>Placeholder proving the test pipeline runs; D1 replaces with real coverage.</summary>
public class ContractSmokeTests
{
    [Fact]
    public void Defaults_match_spec()
    {
        var config = new NuGateConfig();

        Assert.Equal(7, config.MinAgeDays);
        Assert.Equal(PolicyMode.Enforce, config.Mode);
        Assert.Equal(ApiFailureMode.Fail, config.OnApiFailure); // fail-closed by default
    }
}
