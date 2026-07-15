using System.Globalization;
using System.Text.Json;
using NuGate.Core;

namespace NuGate.Tool;

/// <summary>
/// Renders a <see cref="PolicyResult"/> as human-readable text or as the frozen --json schema
/// (see docs/CONTRACTS.md). Pure formatting — no I/O beyond writing to the given writer.
/// </summary>
internal static class OutputFormatting
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void WriteHuman(TextWriter writer, PolicyResult result, NuGateConfig config, int packageCount)
    {
        // Use result.Mode (what the engine actually evaluated under), not config.Mode, so display
        // stays correct even if a caller's config and evaluation result ever diverge (e.g. tests).
        var modeText = FormatMode(result.Mode);
        var apiFailureText = FormatApiFailureMode(config.OnApiFailure);

        if (result.Violations.Count == 0)
        {
            writer.WriteLine(
                $"OK: {packageCount} package(s) checked, no policy violations. " +
                $"Policy: minAgeDays={config.MinAgeDays}, mode={modeText}, onApiFailure={apiFailureText}.");
        }
        else
        {
            foreach (var violation in result.Violations)
            {
                writer.WriteLine(FormatViolationLine(violation));
                writer.WriteLine($"    allow: {violation.AllowlistHint}");
            }

            writer.WriteLine();
            writer.WriteLine(
                $"{result.Violations.Count} violation(s) found across {packageCount} package(s) checked. " +
                $"Policy: minAgeDays={config.MinAgeDays}, mode={modeText}.");
            writer.WriteLine(
                result.Mode == PolicyMode.Enforce
                    ? "Result: FAIL (mode=enforce)."
                    : "Result: reported only, not failing the build (mode=warn).");
        }

        if (result.Warnings.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Warnings:");
            foreach (var warning in result.Warnings)
            {
                writer.WriteLine($"  WARNING: {warning}");
            }
        }
    }

    public static void WriteJson(
        TextWriter writer,
        PolicyResult result,
        NuGateConfig config,
        IReadOnlyList<string> filesScanned)
    {
        var payload = new JsonOutputPayload
        {
            Version = 1,
            Passed = !result.ShouldFail,
            Mode = FormatMode(result.Mode),
            MinAgeDays = config.MinAgeDays,
            Violations = result.Violations.Select(ToJsonViolation).ToList(),
            Warnings = result.Warnings.ToList(),
            FilesScanned = filesScanned.ToList(),
        };

        writer.Write(JsonSerializer.Serialize(payload, JsonOptions));
        writer.Write('\n');
    }

    private static JsonViolationPayload ToJsonViolation(Violation violation) => new()
    {
        Id = violation.Package.Id,
        Version = violation.Package.Version,
        Kind = FormatKind(violation.Kind),
        Created = FormatTimestamp(violation.Created),
        AgeDays = violation.AgeDays.HasValue ? Math.Round(violation.AgeDays.Value, 2) : null,
        AllowlistHint = new JsonAllowlistHintPayload
        {
            Id = violation.Package.Id,
            Version = violation.Package.Version,
        },
    };

    private static string FormatViolationLine(Violation violation) => violation.Kind switch
    {
        ViolationKind.TooYoung =>
            $"VIOLATION {violation.Package.Id} {violation.Package.Version} — tooYoung " +
            $"(age {FormatAgeDays(violation.AgeDays)}, created {FormatTimestamp(violation.Created)})",
        ViolationKind.Unlisted =>
            $"VIOLATION {violation.Package.Id} {violation.Package.Version} — unlisted" +
            (violation.Created is null ? string.Empty : $" (created {FormatTimestamp(violation.Created)})"),
        ViolationKind.LookupFailed =>
            $"VIOLATION {violation.Package.Id} {violation.Package.Version} — lookupFailed: " +
            "the nuget.org API was unreachable; set \"onApiFailure\": \"warn\" in nugate.json to opt out of " +
            "failing closed.",
        _ =>
            $"VIOLATION {violation.Package.Id} {violation.Package.Version} — {violation.Kind}",
    };

    private static string FormatKind(ViolationKind kind) => kind switch
    {
        ViolationKind.TooYoung => "tooYoung",
        ViolationKind.Unlisted => "unlisted",
        ViolationKind.LookupFailed => "lookupFailed",
        _ => kind.ToString(),
    };

    private static string FormatMode(PolicyMode mode) => mode == PolicyMode.Enforce ? "enforce" : "warn";

    private static string FormatApiFailureMode(ApiFailureMode mode) =>
        mode == ApiFailureMode.Fail ? "fail" : "warn";

    private static string FormatAgeDays(double? ageDays) => ageDays.HasValue
        ? ageDays.Value.ToString("0.0", CultureInfo.InvariantCulture) + "d"
        : "unknown";

    private static string? FormatTimestamp(DateTimeOffset? timestamp) => timestamp?.UtcDateTime
        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private sealed class JsonOutputPayload
    {
        public int Version { get; set; }

        public bool Passed { get; set; }

        public string Mode { get; set; } = string.Empty;

        public int MinAgeDays { get; set; }

        public List<JsonViolationPayload> Violations { get; set; } = new();

        public List<string> Warnings { get; set; } = new();

        public List<string> FilesScanned { get; set; } = new();
    }

    private sealed class JsonViolationPayload
    {
        public string Id { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string? Created { get; set; }

        public double? AgeDays { get; set; }

        public JsonAllowlistHintPayload AllowlistHint { get; set; } = new();
    }

    private sealed class JsonAllowlistHintPayload
    {
        public string Id { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Expires { get; set; } = "<yyyy-MM-dd>";

        public string Reason { get; set; } = "<why>";
    }
}
