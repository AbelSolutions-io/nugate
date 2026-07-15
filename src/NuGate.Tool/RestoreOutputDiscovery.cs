namespace NuGate.Tool;

/// <summary>One restore output file found under the scan root.</summary>
public sealed record DiscoveredFile(string FullPath, string RelativePath);

/// <summary>
/// Recursively finds `project.assets.json` / `packages.lock.json` restore outputs under a root,
/// skipping common noise directories.
/// </summary>
public static class RestoreOutputDiscovery
{
    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        ".git",
        "bin",
    };

    private static readonly HashSet<string> TargetFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "project.assets.json",
        "packages.lock.json",
    };

    /// <exception cref="DirectoryNotFoundException"><paramref name="rootPath"/> does not exist.</exception>
    public static IReadOnlyList<DiscoveredFile> Discover(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Path '{root}' does not exist.");
        }

        var results = new List<DiscoveredFile>();
        Walk(root, root, results);
        results.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return results;
    }

    private static void Walk(string root, string currentDir, List<DiscoveredFile> results)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(currentDir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!TargetFileNames.Contains(name))
            {
                continue;
            }

            var full = Path.GetFullPath(file);
            var relative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            results.Add(new DiscoveredFile(full, relative));
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(currentDir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var dir in dirs)
        {
            var dirName = Path.GetFileName(dir);
            if (SkipDirectoryNames.Contains(dirName))
            {
                continue;
            }

            Walk(root, dir, results);
        }
    }
}
