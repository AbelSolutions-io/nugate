using System.Collections.Generic;
using System.IO;
using Xunit;

namespace NuGate.Build.Tests;

public class ConfigLocatorTests
{
    [Fact]
    public void Explicit_path_is_honored_verbatim_even_when_missing()
    {
        // An explicit --config that doesn't exist must NOT silently fall back to defaults; it is
        // returned so the loader can surface it as a configuration error.
        var result = ConfigLocator.Resolve(
            startDirectory: @"C:\repo\src\App",
            configFilePath: @"C:\repo\custom-nugate.json",
            fileExists: _ => false);

        Assert.Equal(@"C:\repo\custom-nugate.json", result);
    }

    [Fact]
    public void Explicit_path_is_trimmed()
    {
        var result = ConfigLocator.Resolve(
            startDirectory: null,
            configFilePath: "  C:\\repo\\nugate.json  ",
            fileExists: _ => true);

        Assert.Equal(@"C:\repo\nugate.json", result);
    }

    [Fact]
    public void Walks_up_to_find_nugate_json_at_an_ancestor()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "nugate-root");
        var expected = Path.Combine(repoRoot, "nugate.json");
        var start = Path.Combine(repoRoot, "src", "App", "obj");

        var seen = new List<string>();
        var result = ConfigLocator.Resolve(
            startDirectory: start,
            configFilePath: "",
            fileExists: path =>
            {
                seen.Add(path);
                return string.Equals(path, expected, StringComparison.OrdinalIgnoreCase);
            });

        Assert.Equal(expected, result);
        // Proves it actually walked upward through the intermediate directories.
        Assert.Contains(Path.Combine(repoRoot, "src", "App", "obj", "nugate.json"), seen);
        Assert.Contains(Path.Combine(repoRoot, "src", "App", "nugate.json"), seen);
    }

    [Fact]
    public void Returns_null_when_no_config_exists_up_to_the_root()
    {
        var start = Path.Combine(Path.GetTempPath(), "nugate-none", "a", "b");

        var result = ConfigLocator.Resolve(
            startDirectory: start,
            configFilePath: "",
            fileExists: _ => false);

        Assert.Null(result); // caller passes null ⇒ NuGateConfig.Load yields defaults
    }

    [Fact]
    public void Returns_null_when_start_directory_is_blank_and_no_explicit_path()
    {
        var result = ConfigLocator.Resolve(
            startDirectory: "",
            configFilePath: "",
            fileExists: _ => false);

        Assert.Null(result);
    }
}
