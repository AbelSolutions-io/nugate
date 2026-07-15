using System.Text.Json;
using NuGate.Core;
using NuGate.Tool.Tests.Fakes;
using Xunit;

namespace NuGate.Tool.Tests;

public class CheckCommandTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    private sealed record Harness(CheckCommand Command, FakeResolvedPackageReader Reader, StringWriter Stdout, StringWriter Stderr);

    private static Harness CreateHarness(
        Func<string, IReadOnlyList<PackageIdentity>>? read = null,
        Func<string, NuGateConfig>? loadConfig = null,
        PolicyEvaluator? evaluate = null,
        Func<DateTimeOffset>? clock = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var reader = new FakeResolvedPackageReader(read ?? (_ => Array.Empty<PackageIdentity>()));
        var command = new CheckCommand(
            reader,
            new NoOpTimestampProvider(),
            evaluate ?? ((_, _, _, _, _) => Task.FromResult(Passing())),
            loadConfig ?? (_ => new NuGateConfig()),
            stdout,
            stderr,
            clock ?? (() => FixedNow));
        return new Harness(command, reader, stdout, stderr);
    }

    private static PolicyResult Passing(PolicyMode mode = PolicyMode.Enforce) =>
        new(Array.Empty<Violation>(), Array.Empty<string>(), mode);

    private static CheckOptions Options(
        string path, string? configPath = null, bool json = false, int? minAgeDaysOverride = null) =>
        new() { Path = path, ConfigPath = configPath, Json = json, MinAgeDaysOverride = minAgeDaysOverride };

    [Fact]
    public async Task No_restore_outputs_is_an_operational_error()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness();

        var exitCode = await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(Program.ExitOperationalError, exitCode);
        Assert.Contains("No NuGet restore outputs found", harness.Stderr.ToString());
        Assert.Contains("dotnet restore", harness.Stderr.ToString());
    }

    [Fact]
    public async Task Bad_config_is_an_operational_error()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        var harness = CreateHarness(loadConfig: _ => throw new InvalidOperationException("unknown property 'oops'"));

        var exitCode = await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(Program.ExitOperationalError, exitCode);
        Assert.Contains("Invalid NuGate configuration", harness.Stderr.ToString());
        Assert.Contains("unknown property 'oops'", harness.Stderr.ToString());
    }

    [Fact]
    public async Task Explicit_config_that_does_not_exist_is_an_operational_error()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        // loadConfig would return defaults for a missing file — the command must not get that far.
        var harness = CreateHarness(loadConfig: _ => new NuGateConfig());
        var missing = Path.Combine(temp.Path, "moved-nugate.json");

        var exitCode = await harness.Command.RunAsync(
            Options(temp.Path, configPath: missing), CancellationToken.None);

        Assert.Equal(Program.ExitOperationalError, exitCode);
        Assert.Contains(missing, harness.Stderr.ToString());
        Assert.Contains("does not exist", harness.Stderr.ToString());
    }

    [Fact]
    public async Task Reader_failure_is_an_operational_error()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        var harness = CreateHarness(read: _ => throw new InvalidDataException("malformed assets file"));

        var exitCode = await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(Program.ExitOperationalError, exitCode);
        Assert.Contains("Failed to read resolved packages", harness.Stderr.ToString());
    }

    [Fact]
    public async Task Passing_result_exits_zero_and_reports_ok()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        var harness = CreateHarness(read: _ => new[] { new PackageIdentity("Example.Pkg", "1.0.0") });

        var exitCode = await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(Program.ExitPass, exitCode);
        Assert.Contains("OK: 1 package(s) checked", harness.Stdout.ToString());
    }

    [Fact]
    public async Task Violations_under_enforce_mode_exit_one_and_list_violations_with_allow_hint()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        var package = new PackageIdentity("Example.Pkg", "2.0.1");
        var violation = new Violation(
            package, ViolationKind.TooYoung, new DateTimeOffset(2026, 7, 12, 9, 14, 0, TimeSpan.Zero), 2.9);
        var result = new PolicyResult(new[] { violation }, Array.Empty<string>(), PolicyMode.Enforce);
        var harness = CreateHarness(
            read: _ => new[] { package },
            evaluate: (_, _, _, _, _) => Task.FromResult(result));

        var exitCode = await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(Program.ExitViolations, exitCode);
        var stdout = harness.Stdout.ToString();
        Assert.Contains("VIOLATION Example.Pkg 2.0.1", stdout);
        Assert.Contains("tooYoung", stdout);
        Assert.Contains("allow: { \"id\": \"Example.Pkg\", \"version\": \"2.0.1\"", stdout);
        Assert.Contains("Result: FAIL (mode=enforce)", stdout);
    }

    [Fact]
    public async Task Violations_under_warn_mode_still_exit_zero_but_report_violations()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        var package = new PackageIdentity("Example.Pkg", "2.0.1");
        var violation = new Violation(package, ViolationKind.TooYoung, FixedNow, 1.0);
        var result = new PolicyResult(new[] { violation }, Array.Empty<string>(), PolicyMode.Warn);
        var harness = CreateHarness(
            read: _ => new[] { package },
            evaluate: (_, _, _, _, _) => Task.FromResult(result));

        var exitCode = await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(Program.ExitPass, exitCode);
        var stdout = harness.Stdout.ToString();
        Assert.Contains("VIOLATION Example.Pkg 2.0.1", stdout);
        Assert.Contains("mode=warn", stdout);
        Assert.DoesNotContain("Result: FAIL", stdout);
    }

    [Fact]
    public async Task LookupFailed_violation_mentions_unreachable_api_and_the_warn_opt_out()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        var package = new PackageIdentity("Example.Pkg", "2.0.1");
        var violation = new Violation(package, ViolationKind.LookupFailed, null, null);
        var result = new PolicyResult(new[] { violation }, Array.Empty<string>(), PolicyMode.Enforce);
        var harness = CreateHarness(
            read: _ => new[] { package },
            evaluate: (_, _, _, _, _) => Task.FromResult(result));

        var exitCode = await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(Program.ExitViolations, exitCode);
        var stdout = harness.Stdout.ToString();
        Assert.Contains("unreachable", stdout);
        Assert.Contains("onApiFailure", stdout);
        Assert.Contains("warn", stdout);
    }

    [Fact]
    public async Task TimestampLookupException_from_evaluator_is_an_operational_error()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        var harness = CreateHarness(
            read: _ => new[] { new PackageIdentity("Example.Pkg", "1.0.0") },
            evaluate: (_, _, _, _, _) => throw new TimestampLookupException("connection reset"));

        var exitCode = await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(Program.ExitOperationalError, exitCode);
        var stderr = harness.Stderr.ToString();
        Assert.Contains("unreachable", stderr);
        Assert.Contains("onApiFailure", stderr);
        Assert.Contains("warn", stderr);
    }

    [Fact]
    public async Task Min_age_days_override_takes_precedence_over_config()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        NuGateConfig? capturedConfig = null;
        var harness = CreateHarness(
            read: _ => new[] { new PackageIdentity("Example.Pkg", "1.0.0") },
            loadConfig: _ => new NuGateConfig { MinAgeDays = 7 },
            evaluate: (config, _, _, _, _) =>
            {
                capturedConfig = config;
                return Task.FromResult(Passing());
            });

        var exitCode = await harness.Command.RunAsync(
            Options(temp.Path, minAgeDaysOverride: 3), CancellationToken.None);

        Assert.Equal(Program.ExitPass, exitCode);
        Assert.NotNull(capturedConfig);
        Assert.Equal(3, capturedConfig!.MinAgeDays);
    }

    [Fact]
    public async Task Config_value_is_used_when_no_override_given()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        NuGateConfig? capturedConfig = null;
        var harness = CreateHarness(
            read: _ => new[] { new PackageIdentity("Example.Pkg", "1.0.0") },
            loadConfig: _ => new NuGateConfig { MinAgeDays = 21 },
            evaluate: (config, _, _, _, _) =>
            {
                capturedConfig = config;
                return Task.FromResult(Passing());
            });

        await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(21, capturedConfig!.MinAgeDays);
    }

    [Fact]
    public async Task Packages_are_deduped_across_multiple_discovered_files()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src", "App", "obj", "project.assets.json");
        temp.CreateFile("src", "Lib", "obj", "project.assets.json");

        var shared = new PackageIdentity("Shared.Pkg", "1.0.0");
        var appMarker = Path.Combine("App", "obj", "project.assets.json");
        IReadOnlyList<PackageIdentity>? capturedPackages = null;
        var harness = CreateHarness(
            read: path => path.EndsWith(appMarker, StringComparison.Ordinal)
                ? new[] { shared, new PackageIdentity("App.Only", "2.0.0") }
                : new[] { shared, new PackageIdentity("Lib.Only", "3.0.0") },
            evaluate: (_, packages, _, _, _) =>
            {
                capturedPackages = packages;
                return Task.FromResult(Passing());
            });

        await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.NotNull(capturedPackages);
        Assert.Equal(3, capturedPackages!.Count);
        Assert.Single(capturedPackages, p => p.Id == "Shared.Pkg");
    }

    [Fact]
    public async Task Json_mode_writes_only_json_to_stdout_and_human_text_to_stderr()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        var harness = CreateHarness(read: _ => new[] { new PackageIdentity("Example.Pkg", "1.0.0") });

        var exitCode = await harness.Command.RunAsync(Options(temp.Path, json: true), CancellationToken.None);

        Assert.Equal(Program.ExitPass, exitCode);
        var stdout = harness.Stdout.ToString().Trim();
        using var doc = JsonDocument.Parse(stdout); // throws if stdout has any non-JSON content
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
        Assert.NotEmpty(harness.Stderr.ToString());
        Assert.Contains("OK:", harness.Stderr.ToString());
    }

    [Fact]
    public async Task Json_schema_matches_contract_for_violations()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src", "App", "obj", "project.assets.json");
        var package = new PackageIdentity("Example.Pkg", "2.0.1");
        var created = new DateTimeOffset(2026, 7, 12, 9, 14, 0, TimeSpan.Zero);
        var violation = new Violation(package, ViolationKind.TooYoung, created, 2.9);
        var result = new PolicyResult(new[] { violation }, new[] { "some warning" }, PolicyMode.Enforce);
        var harness = CreateHarness(
            read: _ => new[] { package },
            loadConfig: _ => new NuGateConfig { MinAgeDays = 7 },
            evaluate: (_, _, _, _, _) => Task.FromResult(result));

        await harness.Command.RunAsync(Options(temp.Path, json: true), CancellationToken.None);

        using var doc = JsonDocument.Parse(harness.Stdout.ToString());
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal("enforce", root.GetProperty("mode").GetString());
        Assert.Equal(7, root.GetProperty("minAgeDays").GetInt32());

        var violations = root.GetProperty("violations");
        Assert.Equal(1, violations.GetArrayLength());
        var jsonViolation = violations[0];
        Assert.Equal("Example.Pkg", jsonViolation.GetProperty("id").GetString());
        Assert.Equal("2.0.1", jsonViolation.GetProperty("version").GetString());
        Assert.Equal("tooYoung", jsonViolation.GetProperty("kind").GetString());
        Assert.Equal("2026-07-12T09:14:00Z", jsonViolation.GetProperty("created").GetString());
        Assert.Equal(2.9, jsonViolation.GetProperty("ageDays").GetDouble(), 3);

        var allowlistHint = jsonViolation.GetProperty("allowlistHint");
        Assert.Equal("Example.Pkg", allowlistHint.GetProperty("id").GetString());
        Assert.Equal("2.0.1", allowlistHint.GetProperty("version").GetString());
        Assert.Equal("<yyyy-MM-dd>", allowlistHint.GetProperty("expires").GetString());
        Assert.Equal("<why>", allowlistHint.GetProperty("reason").GetString());

        var warnings = root.GetProperty("warnings");
        Assert.Equal(1, warnings.GetArrayLength());
        Assert.Equal("some warning", warnings[0].GetString());

        var filesScanned = root.GetProperty("filesScanned");
        Assert.Equal(1, filesScanned.GetArrayLength());
        Assert.Equal("src/App/obj/project.assets.json", filesScanned[0].GetString());
    }

    [Fact]
    public async Task Warn_mode_json_passed_is_true_even_with_violations()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        var package = new PackageIdentity("Example.Pkg", "2.0.1");
        var violation = new Violation(package, ViolationKind.TooYoung, FixedNow, 1.0);
        var result = new PolicyResult(new[] { violation }, Array.Empty<string>(), PolicyMode.Warn);
        var harness = CreateHarness(
            read: _ => new[] { package },
            evaluate: (_, _, _, _, _) => Task.FromResult(result));

        var exitCode = await harness.Command.RunAsync(Options(temp.Path, json: true), CancellationToken.None);

        Assert.Equal(Program.ExitPass, exitCode);
        using var doc = JsonDocument.Parse(harness.Stdout.ToString());
        Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("violations").GetArrayLength());
    }

    [Fact]
    public async Task Evaluator_is_invoked_with_the_injected_clock_value()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("obj", "project.assets.json");
        DateTimeOffset? capturedAsOf = null;
        var harness = CreateHarness(
            read: _ => new[] { new PackageIdentity("Example.Pkg", "1.0.0") },
            evaluate: (_, _, _, asOfUtc, _) =>
            {
                capturedAsOf = asOfUtc;
                return Task.FromResult(Passing());
            });

        await harness.Command.RunAsync(Options(temp.Path), CancellationToken.None);

        Assert.Equal(FixedNow, capturedAsOf);
    }
}
