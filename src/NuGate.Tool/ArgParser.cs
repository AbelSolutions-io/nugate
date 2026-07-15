using System.Globalization;

namespace NuGate.Tool;

/// <summary>What the parsed command line asked the process to do.</summary>
public enum CliMode
{
    RunCheck,
    ShowHelp,
    ShowVersion,
    Error,
}

/// <summary>Outcome of parsing <c>args</c>.</summary>
public sealed class CliParseResult
{
    public required CliMode Mode { get; init; }

    public CheckOptions? Options { get; init; }

    public string? ErrorMessage { get; init; }

    public static CliParseResult RunCheck(CheckOptions options) =>
        new() { Mode = CliMode.RunCheck, Options = options };

    public static CliParseResult Help() => new() { Mode = CliMode.ShowHelp };

    public static CliParseResult Version() => new() { Mode = CliMode.ShowVersion };

    public static CliParseResult Error(string message) => new() { Mode = CliMode.Error, ErrorMessage = message };
}

/// <summary>
/// Hand-rolled parser for the frozen CLI surface (see docs/CONTRACTS.md). No package
/// dependencies — this is deliberately small enough not to need one.
/// </summary>
public static class ArgParser
{
    public const string Usage =
        "usage: nugate check [path] [--config <file>] [--json] [--min-age-days <N>]\n" +
        "       nugate --help | --version";

    public const string HelpText =
        Usage + "\n\n" +
        "nugate enforces a dependency age policy for resolved NuGet packages.\n\n" +
        "Commands:\n" +
        "  check [path]          Scan restore outputs under [path] (default: current directory)\n\n" +
        "Options:\n" +
        "  --config <file>       Path to nugate.json (default: nugate.json at [path])\n" +
        "  --json                Emit machine-readable JSON on stdout\n" +
        "  --min-age-days <N>    Override minAgeDays from config\n" +
        "  -h, --help            Show this help\n" +
        "  --version             Show version\n\n" +
        "Exit codes: 0 pass, 1 policy violations, 2 operational error.";

    public static CliParseResult Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return CliParseResult.Error("No command given.\n\n" + Usage);
        }

        return args[0] switch
        {
            "-h" or "--help" => CliParseResult.Help(),
            "--version" => CliParseResult.Version(),
            "check" => ParseCheck(args),
            var unknown => CliParseResult.Error($"Unknown command '{unknown}'.\n\n" + Usage),
        };
    }

    private static CliParseResult ParseCheck(string[] args)
    {
        string? path = null;
        string? configPath = null;
        var json = false;
        int? minAgeDays = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--config":
                    if (++i >= args.Length)
                    {
                        return CliParseResult.Error("--config requires a value.\n\n" + Usage);
                    }

                    configPath = args[i];
                    break;

                case "--json":
                    json = true;
                    break;

                case "--min-age-days":
                    if (++i >= args.Length)
                    {
                        return CliParseResult.Error("--min-age-days requires a value.\n\n" + Usage);
                    }

                    if (!int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        || parsed < 0)
                    {
                        return CliParseResult.Error(
                            $"--min-age-days must be a non-negative integer, got '{args[i]}'.\n\n" + Usage);
                    }

                    minAgeDays = parsed;
                    break;

                case "-h":
                case "--help":
                    return CliParseResult.Help();

                default:
                    if (arg.StartsWith('-'))
                    {
                        return CliParseResult.Error($"Unknown flag '{arg}'.\n\n" + Usage);
                    }

                    if (path is not null)
                    {
                        return CliParseResult.Error($"Unexpected extra argument '{arg}'.\n\n" + Usage);
                    }

                    path = arg;
                    break;
            }
        }

        return CliParseResult.RunCheck(new CheckOptions
        {
            Path = path ?? ".",
            ConfigPath = configPath,
            Json = json,
            MinAgeDaysOverride = minAgeDays,
        });
    }
}
