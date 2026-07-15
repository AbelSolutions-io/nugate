using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using NuGate.Core;

namespace NuGate.Build;

/// <summary>
/// MSBuild task: reads the project's resolved graph from project.assets.json and fails the build
/// when a resolved package version is younger than the configured minimum age. Enforces a
/// dependency age policy at build time. Wired in via build/NuGate.Build.targets after package
/// assets resolve and before compilation.
/// </summary>
/// <remarks>
/// <para><b>Async on an MSBuild task.</b> <see cref="Execute"/> is synchronous and the orchestrator
/// is async (Core's <c>PolicyEngine.EvaluateAsync</c> and the timestamp provider). To avoid the
/// classic sync-over-async deadlock on hosts with a single-threaded context, the async work is
/// pushed onto the thread pool via <c>Task.Run(...).GetAwaiter().GetResult()</c> — the task body
/// never captures or blocks the host's context.</para>
///
/// <para><b>Assembly-load isolation.</b> The task and its dependency closure
/// (NuGate.Core + System.Text.Json and its netstandard2.0 closure) are packed under
/// <c>tasks/netstandard2.0/</c> and loaded by MSBuild's default task host — the .NET Framework host
/// inside Visual Studio and the .NET host under <c>dotnet build</c>. v0 deliberately does NOT set up
/// AppDomain / AssemblyLoadContext isolation:
/// <list type="bullet">
/// <item>The closure is kept minimal (netstandard2.0, System.Text.Json 8.x) so the risk of clashing
/// with another task's copy of System.Text.Json in the same host is low.</item>
/// <item>Full isolation (a dedicated AppDomain on Framework, a custom AssemblyLoadContext on Core)
/// is a meaningful amount of plumbing and is overkill for a single self-contained gate task. It is
/// noted as a future hardening step, not a v0 requirement.</item>
/// </list>
/// If a consumer ever reports a System.Text.Json load conflict, the mitigation is either a
/// <c>&lt;TaskFactory&gt;</c>-hosted AppDomain or binding-redirect-free ALC isolation — captured here
/// so the decision is discoverable.</para>
/// </remarks>
public sealed class NuGateCheckTask : Microsoft.Build.Utilities.Task
{
    /// <summary>Overall soft time budget for timestamp resolution before a run is treated as a lookup failure.</summary>
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(100);

    /// <summary>Path to this project's obj/project.assets.json (from <c>$(ProjectAssetsFile)</c>).</summary>
    [Required]
    public string AssetsFilePath { get; set; } = string.Empty;

    /// <summary>Repo-root nugate.json; empty means "walk up to discover", missing means defaults.</summary>
    public string ConfigFilePath { get; set; } = string.Empty;

    public override bool Execute()
    {
        if (string.IsNullOrWhiteSpace(AssetsFilePath))
        {
            Log.LogError("NuGate: AssetsFilePath was not supplied; the gate cannot run.");
            return false;
        }

        // Obtain the real Core implementations. On the stub branch these are absent, which is
        // reported as a fail-closed operational error (see CoreServices remarks).
        if (!CoreServices.TryCreateReader(out var reader, out var readerError))
        {
            Log.LogError($"NuGate: {readerError}.");
            return false;
        }

        if (!CoreServices.TryCreateTimestampProvider(out var timestamps, out var providerError))
        {
            Log.LogError($"NuGate: {providerError}.");
            return false;
        }

        var runner = new NuGateCheckRunner(
            reader,
            timestamps,
            evaluate: (config, packages, ts, asOf, ct) =>
                new PolicyEngine().EvaluateAsync(config, packages, ts, asOf, ct),
            loadConfig: NuGateConfig.Load,
            fileExists: System.IO.File.Exists,
            clock: () => DateTimeOffset.UtcNow,
            budget: TimeBudget);

        RunOutcome outcome;
        try
        {
            // Push async work onto the pool and block there — no host context is captured, so this
            // cannot deadlock the single-threaded .NET Framework MSBuild host.
            outcome = Task.Run(() => runner.RunAsync(AssetsFilePath, ConfigFilePath, CancellationToken.None))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            // Unexpected failure ⇒ fail closed. A gate that cannot decide must not wave the build through.
            Log.LogError($"NuGate: the dependency age policy check failed to run: {ex.Message}");
            return false;
        }

        foreach (var warning in outcome.Warnings)
        {
            Log.LogWarning(warning);
        }

        foreach (var error in outcome.Errors)
        {
            Log.LogError(error);
        }

        return outcome.Success;
    }
}
