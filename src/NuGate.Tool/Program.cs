using System.Reflection;
using NuGate.Core;

namespace NuGate.Tool;

/// <summary>
/// CLI contract (FROZEN — see docs/CONTRACTS.md):
///
///   nugate check [path] [--config &lt;file&gt;] [--json] [--min-age-days &lt;N&gt;]
///
///   path            root to scan for project.assets.json / packages.lock.json (default: cwd)
///   --config        explicit nugate.json path (default: nugate.json at [path])
///   --json          machine-readable output on stdout (schema in CONTRACTS.md)
///   --min-age-days  override config minAgeDays
///
/// Exit codes: 0 = pass, 1 = policy violations, 2 = operational error
/// (bad config, API failure under fail-closed, no restore outputs found).
/// </summary>
public static class Program
{
    public const int ExitPass = 0;
    public const int ExitViolations = 1;
    public const int ExitOperationalError = 2;

    public static int Main(string[] args)
    {
        var parsed = ArgParser.Parse(args);

        switch (parsed.Mode)
        {
            case CliMode.ShowHelp:
                Console.Out.WriteLine(ArgParser.HelpText);
                return ExitPass;

            case CliMode.ShowVersion:
                Console.Out.WriteLine(GetVersion());
                return ExitPass;

            case CliMode.Error:
                Console.Error.WriteLine(parsed.ErrorMessage);
                return ExitOperationalError;

            case CliMode.RunCheck:
                var command = new CheckCommand(
                    reader: new ResolvedPackageReader(),
                    timestampProvider: new NuGetTimestampProvider(),
                    evaluatePolicy: (config, packages, timestamps, asOfUtc, ct) =>
                        new PolicyEngine().EvaluateAsync(config, packages, timestamps, asOfUtc, ct),
                    loadConfig: NuGateConfig.Load,
                    stdout: Console.Out,
                    stderr: Console.Error,
                    clock: () => DateTimeOffset.UtcNow);
                return command.RunAsync(parsed.Options!, CancellationToken.None).GetAwaiter().GetResult();

            default:
                throw new InvalidOperationException($"Unhandled CLI mode '{parsed.Mode}'.");
        }
    }

    private static string GetVersion()
    {
        var assembly = typeof(Program).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
