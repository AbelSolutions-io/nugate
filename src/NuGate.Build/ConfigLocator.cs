using System.IO;

namespace NuGate.Build;

/// <summary>
/// Locates the repo-root <c>nugate.json</c> for a build. This is Build-owned discovery logic
/// (walk up from the project directory); the actual parse/validation lives in
/// <see cref="NuGate.Core.NuGateConfig.Load(string?)"/>.
/// </summary>
internal static class ConfigLocator
{
    /// <summary>
    /// Resolve the config file path to hand to <c>NuGateConfig.Load</c>.
    /// <list type="number">
    /// <item>An explicit <paramref name="configFilePath"/> (from <c>$(NuGateConfigFile)</c>) wins verbatim.</item>
    /// <item>Otherwise walk up from <paramref name="startDirectory"/> looking for <c>nugate.json</c>,
    /// stopping at the drive/volume root.</item>
    /// <item>If none is found, return <see langword="null"/> — the caller passes that to
    /// <c>NuGateConfig.Load</c>, which yields spec defaults (7 days, enforce, fail-closed).</item>
    /// </list>
    /// </summary>
    /// <param name="startDirectory">Directory of the project being built (the assets file's project dir).</param>
    /// <param name="configFilePath">Optional explicit config path; blank/whitespace means "discover".</param>
    /// <param name="fileExists">Filesystem probe (injected so discovery is unit-testable without touching disk).</param>
    public static string? Resolve(
        string? startDirectory,
        string? configFilePath,
        Func<string, bool> fileExists)
    {
        if (fileExists is null)
        {
            throw new ArgumentNullException(nameof(fileExists));
        }

        if (!string.IsNullOrWhiteSpace(configFilePath))
        {
            // Explicit path is honored verbatim. If it does not exist, that is surfaced by the
            // loader (an explicit-but-missing config is a configuration error, not a silent default).
            return configFilePath!.Trim();
        }

        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(startDirectory!);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, NuGate.Core.NuGateConfig.DefaultFileName);
            if (fileExists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent; // null once we pass the drive/volume root
        }

        return null; // no nugate.json anywhere up the tree ⇒ defaults
    }
}
