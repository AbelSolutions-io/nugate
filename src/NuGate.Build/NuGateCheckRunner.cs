using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NuGate.Core;

namespace NuGate.Build;

/// <summary>Structured result of one gate run, decoupled from MSBuild's <c>Log</c>.</summary>
/// <remarks>
/// The task pumps <see cref="Errors"/> into <c>Log.LogError</c> and <see cref="Warnings"/> into
/// <c>Log.LogWarning</c>. Keeping this a plain object lets every branch of the orchestrator
/// (config discovery, message formatting, error routing, mode=warn demotion, API-failure routing)
/// be unit-tested with fakes — no MSBuild task host required.
/// </remarks>
internal sealed class RunOutcome
{
    public RunOutcome(bool success, IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        Success = success;
        Errors = errors;
        Warnings = warnings;
    }

    /// <summary>Return value of <c>Task.Execute()</c> — false fails the build.</summary>
    public bool Success { get; }

    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// The gate orchestrator. This is the single seam where NuGate.Core is invoked; everything else in
/// the task package is a pure function of its inputs so it can be tested without a real Core, a real
/// nuget.org, or a real MSBuild host.
/// </summary>
/// <remarks>
/// <para>
/// Core is reached through injected seams rather than concrete references:
/// <list type="bullet">
/// <item><see cref="IResolvedPackageReader"/> and <see cref="INuGetTimestampProvider"/> are Core
/// interfaces — faked directly in tests.</item>
/// <item>Config load (<c>NuGateConfig.Load</c>) and policy evaluation (<c>PolicyEngine.EvaluateAsync</c>,
/// a sealed Core class) are injected as delegates so tests can return canned results.</item>
/// </list>
/// The production wiring in <see cref="NuGateCheckTask"/> binds these to the real Core.
/// </para>
/// </remarks>
internal sealed class NuGateCheckRunner
{
    /// <summary>Signature of <c>PolicyEngine.EvaluateAsync</c>, injected so the sealed engine is isolated.</summary>
    public delegate Task<PolicyResult> EvaluateDelegate(
        NuGateConfig config,
        IReadOnlyList<PackageIdentity> packages,
        INuGetTimestampProvider timestamps,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);

    private readonly IResolvedPackageReader _reader;
    private readonly INuGetTimestampProvider _timestamps;
    private readonly EvaluateDelegate _evaluate;
    private readonly Func<string?, NuGateConfig> _loadConfig;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _budget;

    public NuGateCheckRunner(
        IResolvedPackageReader reader,
        INuGetTimestampProvider timestamps,
        EvaluateDelegate evaluate,
        Func<string?, NuGateConfig> loadConfig,
        Func<string, bool> fileExists,
        Func<DateTimeOffset> clock,
        TimeSpan budget)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _timestamps = timestamps ?? throw new ArgumentNullException(nameof(timestamps));
        _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
        _loadConfig = loadConfig ?? throw new ArgumentNullException(nameof(loadConfig));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _budget = budget;
    }

    /// <param name="assetsFilePath">Path to project.assets.json (or packages.lock.json).</param>
    /// <param name="configFilePath">Explicit nugate.json path, or blank to discover by walking up.</param>
    public async Task<RunOutcome> RunAsync(
        string assetsFilePath,
        string? configFilePath,
        CancellationToken cancellationToken)
    {
        // --- 1. Load config (discovery here, parse in Core). Bad config is fail-closed: a build must
        //        not silently fall back to defaults when the author's config is malformed. -----------
        NuGateConfig config;
        try
        {
            var startDirectory = GetProjectDirectory(assetsFilePath);
            var resolvedConfigPath = ConfigLocator.Resolve(startDirectory, configFilePath, _fileExists);
            config = _loadConfig(resolvedConfigPath);
        }
        catch (Exception ex)
        {
            return Fail($"NuGate: could not load configuration ({Path.GetFileName(NuGateConfig.DefaultFileName)}): {ex.Message}");
        }

        // --- 2. Read the resolved graph from restore outputs. Missing/corrupt outputs are operational
        //        errors (fail closed) — the target only runs when the assets file exists, so this is
        //        genuinely unexpected. --------------------------------------------------------------
        IReadOnlyList<PackageIdentity> packages;
        try
        {
            packages = _reader.ReadResolvedPackages(assetsFilePath);
        }
        catch (Exception ex)
        {
            return Fail($"NuGate: could not read resolved packages from '{assetsFilePath}': {ex.Message}");
        }

        if (packages.Count == 0)
        {
            // Nothing resolved to check — nothing to enforce against.
            return new RunOutcome(true, Array.Empty<string>(), Array.Empty<string>());
        }

        // --- 3. Evaluate under a soft time budget. A hung nuget.org lookup must never hang a build
        //        forever; on timeout we honor onApiFailure exactly as a lookup failure would. --------
        PolicyResult result;
        using (var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (_budget > TimeSpan.Zero)
            {
                budgetCts.CancelAfter(_budget);
            }

            try
            {
                result = await _evaluate(config, packages, _timestamps, _clock(), budgetCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested
                                                     && !cancellationToken.IsCancellationRequested)
            {
                // Budget elapsed (not host cancellation): treat as an API failure and route per policy.
                return RouteApiFailure(
                    config,
                    $"NuGate: dependency timestamp lookups exceeded the {_budget.TotalSeconds:0} second budget");
            }
            catch (TimestampLookupException ex)
            {
                // Defensive: PolicyEngine is expected to internalize this into LookupFailed violations,
                // but if one ever escapes, route it through onApiFailure rather than crashing the build.
                return RouteApiFailure(config, $"NuGate: nuget.org metadata lookup failed: {ex.Message}");
            }
        }

        return MapResult(result, config.MinAgeDays);
    }

    /// <summary>Project directory = parent of an <c>obj</c> folder, else the assets file's directory.</summary>
    private static string? GetProjectDirectory(string assetsFilePath)
    {
        if (string.IsNullOrWhiteSpace(assetsFilePath))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(assetsFilePath);
        if (string.IsNullOrEmpty(dir))
        {
            return dir;
        }

        // project.assets.json lives in <project>/obj; hop up so discovery starts at the project root.
        if (string.Equals(new DirectoryInfo(dir).Name, "obj", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(dir);
            if (parent is not null)
            {
                return parent.FullName;
            }
        }

        return dir;
    }

    private static RunOutcome MapResult(PolicyResult result, int minAgeDays)
    {
        var errors = new List<string>();
        var warnings = new List<string>(result.Warnings);

        // mode=warn demotes violations to warnings; PolicyResult.ShouldFail already encodes the mode.
        var demoteToWarning = result.Mode == PolicyMode.Warn;

        foreach (var violation in result.Violations)
        {
            var message = ViolationFormatter.Format(violation, minAgeDays);
            if (demoteToWarning)
            {
                warnings.Add(message);
            }
            else
            {
                errors.Add(message);
            }
        }

        return new RunOutcome(!result.ShouldFail, errors, warnings);
    }

    private static RunOutcome RouteApiFailure(NuGateConfig config, string message)
        => config.OnApiFailure == ApiFailureMode.Warn
            ? new RunOutcome(true, Array.Empty<string>(), new[] { message + " (onApiFailure=warn)." })
            : Fail(message + " (onApiFailure=fail, fail-closed).");

    private static RunOutcome Fail(string message)
        => new RunOutcome(false, new[] { message }, Array.Empty<string>());
}
