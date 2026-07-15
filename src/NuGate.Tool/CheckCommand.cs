using NuGate.Core;

namespace NuGate.Tool;

/// <summary>
/// Implements `nugate check`: discover restore outputs, load config, evaluate the policy, report.
/// Every Core interaction is injected (reader, timestamp provider, policy evaluator, config
/// loader) plus an output writer and a clock, so the whole flow is unit-testable with fakes even
/// though Core's real implementations currently throw <see cref="NotImplementedException"/>.
/// </summary>
public sealed class CheckCommand
{
    private readonly IResolvedPackageReader _reader;
    private readonly INuGetTimestampProvider _timestampProvider;
    private readonly PolicyEvaluator _evaluatePolicy;
    private readonly Func<string, NuGateConfig> _loadConfig;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly Func<DateTimeOffset> _clock;

    public CheckCommand(
        IResolvedPackageReader reader,
        INuGetTimestampProvider timestampProvider,
        PolicyEvaluator evaluatePolicy,
        Func<string, NuGateConfig> loadConfig,
        TextWriter stdout,
        TextWriter stderr,
        Func<DateTimeOffset> clock)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _timestampProvider = timestampProvider ?? throw new ArgumentNullException(nameof(timestampProvider));
        _evaluatePolicy = evaluatePolicy ?? throw new ArgumentNullException(nameof(evaluatePolicy));
        _loadConfig = loadConfig ?? throw new ArgumentNullException(nameof(loadConfig));
        _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
        _stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<int> RunAsync(CheckOptions options, CancellationToken cancellationToken)
    {
        var scanRoot = Path.GetFullPath(options.Path);

        IReadOnlyList<DiscoveredFile> discovered;
        try
        {
            discovered = RestoreOutputDiscovery.Discover(scanRoot);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to scan '{scanRoot}': {ex.Message}");
        }

        if (discovered.Count == 0)
        {
            return Fail(
                $"No NuGet restore outputs found under '{scanRoot}' " +
                "(looked for **/obj/project.assets.json and **/packages.lock.json). " +
                "Run 'dotnet restore' first.");
        }

        var configPath = options.ConfigPath ?? Path.Combine(scanRoot, NuGateConfig.DefaultFileName);
        NuGateConfig config;
        try
        {
            config = _loadConfig(configPath);
        }
        catch (Exception ex)
        {
            return Fail($"Invalid NuGate configuration at '{configPath}': {ex.Message}");
        }

        if (options.MinAgeDaysOverride.HasValue)
        {
            config.MinAgeDays = options.MinAgeDaysOverride.Value;
        }

        List<PackageIdentity> packages;
        try
        {
            packages = ReadAndDedupePackages(discovered);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to read resolved packages: {ex.Message}");
        }

        PolicyResult result;
        try
        {
            result = await _evaluatePolicy(config, packages, _timestampProvider, _clock(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimestampLookupException ex)
        {
            return Fail(
                $"nuget.org API was unreachable while evaluating the policy ({ex.Message}). " +
                "Set \"onApiFailure\": \"warn\" in nugate.json to opt out of failing closed.");
        }
        catch (Exception ex)
        {
            return Fail($"Policy evaluation failed: {ex.Message}");
        }

        var diagnostics = options.Json ? _stderr : _stdout;
        OutputFormatting.WriteHuman(diagnostics, result, config, packages.Count);

        if (options.Json)
        {
            var filesScanned = discovered.Select(f => f.RelativePath).ToArray();
            OutputFormatting.WriteJson(_stdout, result, config, filesScanned);
        }

        return result.ShouldFail ? Program.ExitViolations : Program.ExitPass;
    }

    private List<PackageIdentity> ReadAndDedupePackages(IReadOnlyList<DiscoveredFile> discovered)
    {
        var packages = new List<PackageIdentity>();
        var seen = new HashSet<(string Id, string Version)>();

        foreach (var file in discovered)
        {
            foreach (var package in _reader.ReadResolvedPackages(file.FullPath))
            {
                var key = (package.Id.ToLowerInvariant(), package.Version);
                if (seen.Add(key))
                {
                    packages.Add(package);
                }
            }
        }

        packages.Sort((a, b) =>
        {
            var byId = string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            return byId != 0 ? byId : string.CompareOrdinal(a.Version, b.Version);
        });

        return packages;
    }

    private int Fail(string message)
    {
        _stderr.WriteLine(message);
        return Program.ExitOperationalError;
    }
}
