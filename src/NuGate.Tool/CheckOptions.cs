namespace NuGate.Tool;

/// <summary>Parsed options for a `nugate check` invocation.</summary>
public sealed class CheckOptions
{
    /// <summary>Root to scan for restore outputs. Defaults to the current directory.</summary>
    public required string Path { get; init; }

    /// <summary>Explicit --config path. Null means "use nugate.json at <see cref="Path"/>".</summary>
    public string? ConfigPath { get; init; }

    /// <summary>--json: emit the machine-readable schema on stdout instead of human text.</summary>
    public bool Json { get; init; }

    /// <summary>--min-age-days override. Null means "use the config value".</summary>
    public int? MinAgeDaysOverride { get; init; }
}
