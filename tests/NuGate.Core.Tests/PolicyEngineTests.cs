using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGate.Core;
using Xunit;

namespace NuGate.Core.Tests;

public class PolicyEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    private static PackageIdentity Pkg(string id, string version = "1.0.0") => new(id, version);

    private static Task<PolicyResult> Evaluate(
        NuGateConfig config,
        IReadOnlyList<PackageIdentity> packages,
        FakeTimestampProvider provider)
        => new PolicyEngine().EvaluateAsync(config, packages, provider, Now, CancellationToken.None);

    [Fact]
    public async Task Too_young_package_is_a_violation()
    {
        var provider = new FakeTimestampProvider();
        provider.Add("Young.Pkg", "2.0.0", Now.AddDays(-2)); // 2 days old, min is 7

        var result = await Evaluate(new NuGateConfig(), new[] { Pkg("Young.Pkg", "2.0.0") }, provider);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(ViolationKind.TooYoung, violation.Kind);
        Assert.True(result.ShouldFail);
    }

    [Fact]
    public async Task Old_enough_package_passes()
    {
        var provider = new FakeTimestampProvider();
        provider.Add("Old.Pkg", "1.0.0", Now.AddDays(-30));

        var result = await Evaluate(new NuGateConfig(), new[] { Pkg("Old.Pkg") }, provider);

        Assert.Empty(result.Violations);
        Assert.False(result.ShouldFail);
    }

    [Fact]
    public async Task Exactly_min_age_days_old_is_not_a_violation()
    {
        var provider = new FakeTimestampProvider();
        // Created exactly minAgeDays (7) days before asOfUtc => age == 7.0 => passes.
        provider.Add("Boundary.Pkg", "1.0.0", Now.AddDays(-7));

        var result = await Evaluate(new NuGateConfig(), new[] { Pkg("Boundary.Pkg") }, provider);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task Just_under_min_age_is_a_violation()
    {
        var provider = new FakeTimestampProvider();
        provider.Add("Edge.Pkg", "1.0.0", Now.AddDays(-7).AddSeconds(1)); // a hair under 7 days

        var result = await Evaluate(new NuGateConfig(), new[] { Pkg("Edge.Pkg") }, provider);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(ViolationKind.TooYoung, violation.Kind);
    }

    [Fact]
    public async Task Exempt_prefix_skips_the_package_entirely()
    {
        var provider = new FakeTimestampProvider(); // no data => would fail lookup if consulted
        var config = new NuGateConfig { ExemptPrefixes = { "MyCompany." } };

        var result = await Evaluate(config, new[] { Pkg("MyCompany.Internal", "0.1.0") }, provider);

        Assert.Empty(result.Violations);
        Assert.Empty(result.Warnings);
        Assert.False(provider.WasQueried("MyCompany.Internal", "0.1.0"));
    }

    [Fact]
    public async Task Exempt_prefix_is_case_insensitive()
    {
        var provider = new FakeTimestampProvider();
        var config = new NuGateConfig { ExemptPrefixes = { "mycompany." } };

        var result = await Evaluate(config, new[] { Pkg("MyCompany.Widget", "0.1.0") }, provider);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task Active_allowlist_entry_passes_without_lookup()
    {
        var provider = new FakeTimestampProvider();
        var config = new NuGateConfig
        {
            Allow = { new AllowEntry { Id = "Hot.Fix", Version = "3.1.4", Expires = Now.AddDays(10) } },
        };

        var result = await Evaluate(config, new[] { Pkg("Hot.Fix", "3.1.4") }, provider);

        Assert.Empty(result.Violations);
        Assert.False(provider.WasQueried("Hot.Fix", "3.1.4"));
    }

    [Fact]
    public async Task Allowlist_match_is_case_insensitive_on_id()
    {
        var provider = new FakeTimestampProvider();
        var config = new NuGateConfig
        {
            Allow = { new AllowEntry { Id = "hot.fix", Version = "3.1.4" } }, // no expiry => open-ended
        };

        var result = await Evaluate(config, new[] { Pkg("Hot.Fix", "3.1.4") }, provider);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task Expired_allowlist_entry_warns_and_still_evaluates()
    {
        var provider = new FakeTimestampProvider();
        provider.Add("Hot.Fix", "3.1.4", Now.AddDays(-1)); // too young once the allow expires

        var config = new NuGateConfig
        {
            Allow = { new AllowEntry { Id = "Hot.Fix", Version = "3.1.4", Expires = Now.AddDays(-5) } },
        };

        var result = await Evaluate(config, new[] { Pkg("Hot.Fix", "3.1.4") }, provider);

        Assert.Single(result.Warnings);
        Assert.Contains("expired", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
        // Expired => not a pass => the too-young package is still flagged.
        var violation = Assert.Single(result.Violations);
        Assert.Equal(ViolationKind.TooYoung, violation.Kind);
    }

    [Fact]
    public async Task Unlisted_version_is_a_violation_regardless_of_age()
    {
        var provider = new FakeTimestampProvider();
        provider.Add("Yanked.Pkg", "9.9.9", Now.AddDays(-400), listed: false); // ancient but unlisted

        var result = await Evaluate(new NuGateConfig(), new[] { Pkg("Yanked.Pkg", "9.9.9") }, provider);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(ViolationKind.Unlisted, violation.Kind);
        Assert.True(result.ShouldFail);
    }

    [Fact]
    public async Task Lookup_failure_fails_closed_by_default()
    {
        var provider = new FakeTimestampProvider(); // throws for unknown packages
        provider.Fail("Broken.Pkg", "1.0.0");

        var config = new NuGateConfig(); // onApiFailure = Fail

        var result = await Evaluate(config, new[] { Pkg("Broken.Pkg") }, provider);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(ViolationKind.LookupFailed, violation.Kind);
        Assert.True(result.ShouldFail);
    }

    [Fact]
    public async Task Lookup_failure_with_warn_mode_only_warns()
    {
        var provider = new FakeTimestampProvider();
        provider.Fail("Broken.Pkg", "1.0.0");

        var config = new NuGateConfig { OnApiFailure = ApiFailureMode.Warn };

        var result = await Evaluate(config, new[] { Pkg("Broken.Pkg") }, provider);

        Assert.Empty(result.Violations);
        Assert.Single(result.Warnings);
        Assert.False(result.ShouldFail);
    }

    [Fact]
    public async Task Null_timestamp_fails_closed_like_a_failure()
    {
        var provider = new FakeTimestampProvider(); // returns null for unknown-to-nuget packages
        var config = new NuGateConfig(); // fail-closed

        var result = await Evaluate(config, new[] { Pkg("Private.Pkg", "1.2.3") }, provider);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(ViolationKind.LookupFailed, violation.Kind);
    }

    [Fact]
    public async Task Null_timestamp_with_warn_only_warns()
    {
        var provider = new FakeTimestampProvider();
        var config = new NuGateConfig { OnApiFailure = ApiFailureMode.Warn };

        var result = await Evaluate(config, new[] { Pkg("Private.Pkg", "1.2.3") }, provider);

        Assert.Empty(result.Violations);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Warn_mode_reports_violations_but_does_not_fail()
    {
        var provider = new FakeTimestampProvider();
        provider.Add("Young.Pkg", "2.0.0", Now.AddDays(-1));

        var config = new NuGateConfig { Mode = PolicyMode.Warn };

        var result = await Evaluate(config, new[] { Pkg("Young.Pkg", "2.0.0") }, provider);

        Assert.Single(result.Violations); // still fully reported
        Assert.False(result.ShouldFail);  // but the build does not fail
    }

    [Fact]
    public async Task Duplicate_packages_are_only_evaluated_once()
    {
        var provider = new FakeTimestampProvider();
        provider.Add("Dup.Pkg", "1.0.0", Now.AddDays(-1));

        var packages = new[] { Pkg("Dup.Pkg"), Pkg("Dup.Pkg"), Pkg("DUP.pkg") };

        var result = await Evaluate(new NuGateConfig(), packages, provider);

        Assert.Single(result.Violations);
    }

    [Fact]
    public async Task Mixed_graph_reports_each_class_of_violation()
    {
        var provider = new FakeTimestampProvider();
        provider.Add("Old.Ok", "1.0.0", Now.AddDays(-100));
        provider.Add("Young.Bad", "2.0.0", Now.AddDays(-1));
        provider.Add("Yanked.Bad", "3.0.0", Now.AddDays(-500), listed: false);
        provider.Fail("Broken.Bad", "4.0.0");

        var packages = new[]
        {
            Pkg("Old.Ok"),
            Pkg("Young.Bad", "2.0.0"),
            Pkg("Yanked.Bad", "3.0.0"),
            Pkg("Broken.Bad", "4.0.0"),
        };

        var result = await Evaluate(new NuGateConfig(), packages, provider);

        Assert.Equal(3, result.Violations.Count);
        Assert.Contains(result.Violations, v => v.Package.Id == "Young.Bad" && v.Kind == ViolationKind.TooYoung);
        Assert.Contains(result.Violations, v => v.Package.Id == "Yanked.Bad" && v.Kind == ViolationKind.Unlisted);
        Assert.Contains(result.Violations, v => v.Package.Id == "Broken.Bad" && v.Kind == ViolationKind.LookupFailed);
        Assert.True(result.ShouldFail);
    }

    [Fact]
    public async Task Empty_graph_passes()
    {
        var result = await Evaluate(new NuGateConfig(), Array.Empty<PackageIdentity>(), new FakeTimestampProvider());

        Assert.Empty(result.Violations);
        Assert.False(result.ShouldFail);
    }
}
