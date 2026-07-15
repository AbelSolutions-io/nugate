using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuGate.Core;

/// <summary>Policy failure behavior when the nuget.org API cannot be reached.</summary>
public enum ApiFailureMode
{
    /// <summary>Fail the build (default — fail closed).</summary>
    Fail,

    /// <summary>Warn and continue (documented opt-out for shops that won't tolerate nuget.org outages).</summary>
    Warn,
}

/// <summary>Enforcement mode for the whole gate.</summary>
public enum PolicyMode
{
    /// <summary>Violations fail the build (default).</summary>
    Enforce,

    /// <summary>Violations are reported but do not fail — rollout mode.</summary>
    Warn,
}

/// <summary>One allowlist entry. Entries may carry an expiry so exceptions don't fossilize.</summary>
public sealed class AllowEntry
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Exact version this exception applies to.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Optional ISO date after which this entry no longer applies.</summary>
    public DateTimeOffset? Expires { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// Deserialized `nugate.json` (repo root). Defaults match the spec: 7 days, enforce, fail-closed.
/// </summary>
public sealed class NuGateConfig
{
    public const string DefaultFileName = "nugate.json";

    public int MinAgeDays { get; set; } = 7;

    public PolicyMode Mode { get; set; } = PolicyMode.Enforce;

    public ApiFailureMode OnApiFailure { get; set; } = ApiFailureMode.Fail;

    public IList<AllowEntry> Allow { get; set; } = new List<AllowEntry>();

    /// <summary>Package-id prefixes exempt from the gate (internal/private-feed packages).</summary>
    public IList<string> ExemptPrefixes { get; set; } = new List<string>();

    /// <summary>
    /// Load config from a `nugate.json` file. Unknown properties are an error (typo protection);
    /// a missing file yields spec defaults.
    /// </summary>
    public static NuGateConfig Load(string? path)
    {
        // Missing / null path or missing file => spec defaults (7 / enforce / fail-closed).
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new NuGateConfig();
        }

        string json;
        try
        {
            json = File.ReadAllText(path!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NuGateConfigException($"Could not read config file '{path}': {ex.Message}", ex);
        }

        NuGateConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<NuGateConfig>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // Covers unknown-property errors (typo protection) and malformed JSON.
            throw new NuGateConfigException($"Invalid {DefaultFileName} at '{path}': {ex.Message}", ex);
        }

        if (config is null)
        {
            return new NuGateConfig();
        }

        Validate(config, path!);
        return config;
    }

    internal static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // Unknown JSON properties are an error — protects against silent typos in nugate.json.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new FlexibleDateTimeOffsetConverter());
        return options;
    }

    private static void Validate(NuGateConfig config, string path)
    {
        if (config.MinAgeDays < 0)
        {
            throw new NuGateConfigException(
                $"minAgeDays must be >= 0 (was {config.MinAgeDays}) in '{path}'.");
        }

        config.Allow ??= new List<AllowEntry>();
        config.ExemptPrefixes ??= new List<string>();

        for (var i = 0; i < config.Allow.Count; i++)
        {
            var entry = config.Allow[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Version))
            {
                throw new NuGateConfigException(
                    $"allow[{i}] requires both a non-empty 'id' and 'version' in '{path}'.");
            }
        }
    }
}

/// <summary>Raised when <c>nugate.json</c> cannot be read, parsed, or fails validation.</summary>
public class NuGateConfigException : Exception
{
    public NuGateConfigException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Parses ISO-8601 timestamps and bare calendar dates (<c>yyyy-MM-dd</c>) as UTC, so allowlist
/// <c>expires</c> values may be written either way. Dates with no time component are treated as
/// midnight UTC on that day.
/// </summary>
internal sealed class FlexibleDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("Expected a date string.");
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            return value;
        }

        throw new JsonException($"Could not parse '{text}' as a date.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}
