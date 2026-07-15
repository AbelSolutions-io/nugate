using System.IO;
using System.Text.Json;

namespace NuGate.Core;

/// <summary>
/// Reads the resolved dependency graph from a restore output file. Accepts both
/// <c>project.assets.json</c> (the <c>targets</c> section — resolved package id/version per
/// framework, <c>type=package</c> only) and <c>packages.lock.json</c> (the <c>dependencies</c>
/// section, including transitives, skipping project references). Packages are deduplicated by
/// id (case-insensitive) + version, so multi-target files collapse to one entry per version.
/// </summary>
public sealed class ResolvedPackageReader : IResolvedPackageReader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public IReadOnlyList<PackageIdentity> ReadResolvedPackages(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A restore-output path is required.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Restore output not found: {path}", path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path), DocumentOptions);
        var root = document.RootElement;

        var results = new List<PackageIdentity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var hasTargets = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("targets", out _);
        var hasDependencies = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("dependencies", out _);

        if (hasTargets)
        {
            ReadAssetsTargets(root, results, seen);
        }
        else if (hasDependencies)
        {
            ReadLockDependencies(root, results, seen);
        }
        else
        {
            // Fall back to the file name when the shape is ambiguous.
            var name = Path.GetFileName(path);
            if (name.EndsWith("project.assets.json", StringComparison.OrdinalIgnoreCase))
            {
                ReadAssetsTargets(root, results, seen);
            }
            else if (name.EndsWith("packages.lock.json", StringComparison.OrdinalIgnoreCase))
            {
                ReadLockDependencies(root, results, seen);
            }
            else
            {
                throw new FormatException(
                    $"Unrecognized restore file (no 'targets' or 'dependencies' section): {path}");
            }
        }

        return results;
    }

    // project.assets.json: targets -> { framework -> { "Id/Version" -> { type, ... } } }
    private static void ReadAssetsTargets(JsonElement root, List<PackageIdentity> results, HashSet<string> seen)
    {
        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var framework in targets.EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var entry in framework.Value.EnumerateObject())
            {
                // Only resolved NuGet packages; skip project references and any other library type.
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || !entry.Value.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String
                    || !string.Equals(typeElement.GetString(), "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = entry.Name; // "Id/Version"
                var slash = key.LastIndexOf('/');
                if (slash <= 0 || slash == key.Length - 1)
                {
                    continue;
                }

                Add(results, seen, key.Substring(0, slash), key.Substring(slash + 1));
            }
        }
    }

    // packages.lock.json: dependencies -> { framework -> { Id -> { type, resolved, ... } } }
    private static void ReadLockDependencies(JsonElement root, List<PackageIdentity> results, HashSet<string> seen)
    {
        if (!root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var framework in dependencies.EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var dependency in framework.Value.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Project references have no nuget.org identity — skip them.
                if (dependency.Value.TryGetProperty("type", out var typeElement)
                    && typeElement.ValueKind == JsonValueKind.String
                    && string.Equals(typeElement.GetString(), "Project", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!dependency.Value.TryGetProperty("resolved", out var resolvedElement)
                    || resolvedElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var version = resolvedElement.GetString();
                if (string.IsNullOrEmpty(version))
                {
                    continue;
                }

                Add(results, seen, dependency.Name, version!);
            }
        }
    }

    private static void Add(List<PackageIdentity> results, HashSet<string> seen, string id, string version)
    {
        var key = id.ToLowerInvariant() + "/" + version.ToLowerInvariant();
        if (seen.Add(key))
        {
            results.Add(new PackageIdentity(id, version));
        }
    }
}
