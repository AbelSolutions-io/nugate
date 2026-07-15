using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGate.Core;
using Xunit;

namespace NuGate.Build.Tests;

public class NuGateCheckRunnerTests
{
    private static readonly IReadOnlyList<PackageIdentity> OnePackage =
        new[] { new PackageIdentity("Example.Pkg", "2.0.1") };

    private const string AssetsPath = @"C:\repo\src\App\obj\project.assets.json";

    private static NuGateCheckRunner Build(
        IResolvedPackageReader reader,
        NuGateCheckRunner.EvaluateDelegate evaluate,
        Func<string?, NuGateConfig>? loadConfig = null,
        TimeSpan? budget = null)
        => new NuGateCheckRunner(
            reader,
            new UnusedTimestampProvider(),
            evaluate,
            loadConfig ?? (_ => new NuGateConfig()),
            fileExists: _ => false, // no nugate.json on disk ⇒ discovery returns null ⇒ defaults
            clock: () => new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
            budget: budget ?? TimeSpan.FromSeconds(100));

    private static Task<PolicyResult> Result(
        IReadOnlyList<Violation> violations, IReadOnlyList<string> warnings, PolicyMode mode)
        => Task.FromResult(new PolicyResult(violations, warnings, mode));

    private static Violation TooYoung()
        => new Violation(new PackageIdentity("Example.Pkg", "2.0.1"), ViolationKind.TooYoung,
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero), 2.0);

    [Fact]
    public async Task Clean_result_succeeds_with_no_errors_or_warnings()
    {
        var runner = Build(
            new FakeReader(OnePackage),
            (c, p, t, a, ct) => Result(Array.Empty<Violation>(), Array.Empty<string>(), PolicyMode.Enforce));

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Empty(outcome.Errors);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Enforce_mode_violations_become_errors_and_fail()
    {
        var runner = Build(
            new FakeReader(OnePackage),
            (c, p, t, a, ct) => Result(new[] { TooYoung() }, Array.Empty<string>(), PolicyMode.Enforce));

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.False(outcome.Success);
        var error = Assert.Single(outcome.Errors);
        Assert.Contains("Example.Pkg", error);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Warn_mode_demotes_violations_to_warnings_and_succeeds()
    {
        var runner = Build(
            new FakeReader(OnePackage),
            (c, p, t, a, ct) => Result(new[] { TooYoung() }, Array.Empty<string>(), PolicyMode.Warn));

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.True(outcome.Success);            // mode=warn ⇒ task returns true
        Assert.Empty(outcome.Errors);
        var warning = Assert.Single(outcome.Warnings);
        Assert.Contains("Example.Pkg", warning);
    }

    [Fact]
    public async Task Policy_warnings_are_passed_through()
    {
        var runner = Build(
            new FakeReader(OnePackage),
            (c, p, t, a, ct) => Result(Array.Empty<Violation>(), new[] { "an expired allow entry" }, PolicyMode.Enforce));

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Contains("an expired allow entry", outcome.Warnings);
    }

    [Fact]
    public async Task Config_load_failure_is_a_fail_closed_operational_error()
    {
        var runner = Build(
            new FakeReader(OnePackage),
            (c, p, t, a, ct) => Result(Array.Empty<Violation>(), Array.Empty<string>(), PolicyMode.Enforce),
            loadConfig: _ => throw new InvalidOperationException("unknown property 'minAgeDayz'"));

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.False(outcome.Success);
        var error = Assert.Single(outcome.Errors);
        Assert.Contains("configuration", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reader_failure_is_a_fail_closed_operational_error()
    {
        var runner = Build(
            new FakeReader(new FileNotFoundException("assets gone")),
            (c, p, t, a, ct) => Result(Array.Empty<Violation>(), Array.Empty<string>(), PolicyMode.Enforce));

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.False(outcome.Success);
        var error = Assert.Single(outcome.Errors);
        Assert.Contains("could not read resolved packages", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_graph_short_circuits_to_success()
    {
        var evaluated = false;
        var runner = Build(
            new FakeReader(Array.Empty<PackageIdentity>()),
            (c, p, t, a, ct) => { evaluated = true; return Result(Array.Empty<Violation>(), Array.Empty<string>(), PolicyMode.Enforce); });

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.False(evaluated); // nothing resolved ⇒ engine not invoked
    }

    [Fact]
    public async Task Timeout_under_fail_closed_fails_the_build()
    {
        // Evaluate never completes until cancelled; a tiny budget forces the timeout path.
        var runner = Build(
            new FakeReader(OnePackage),
            async (c, p, t, a, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; },
            loadConfig: _ => new NuGateConfig { OnApiFailure = ApiFailureMode.Fail },
            budget: TimeSpan.FromMilliseconds(50));

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains(outcome.Errors, e => e.Contains("budget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Timeout_under_warn_reports_a_warning_and_succeeds()
    {
        var runner = Build(
            new FakeReader(OnePackage),
            async (c, p, t, a, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; },
            loadConfig: _ => new NuGateConfig { OnApiFailure = ApiFailureMode.Warn },
            budget: TimeSpan.FromMilliseconds(50));

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Empty(outcome.Errors);
        Assert.Contains(outcome.Warnings, w => w.Contains("budget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Escaped_lookup_exception_is_routed_through_onApiFailure()
    {
        var runner = Build(
            new FakeReader(OnePackage),
            (c, p, t, a, ct) => throw new TimestampLookupException("nuget.org 503"),
            loadConfig: _ => new NuGateConfig { OnApiFailure = ApiFailureMode.Fail });

        var outcome = await runner.RunAsync(AssetsPath, null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains(outcome.Errors, e => e.Contains("nuget.org", StringComparison.OrdinalIgnoreCase));
    }
}
