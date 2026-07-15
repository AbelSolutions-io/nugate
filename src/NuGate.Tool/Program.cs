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
        => throw new NotImplementedException("D3: implement CLI per the frozen contract above.");
}
