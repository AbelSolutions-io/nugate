using System.Linq;
using NuGate.Core;
using Xunit;

namespace NuGate.Core.Tests;

public class ResolvedPackageReaderTests
{
    private readonly ResolvedPackageReader _reader = new();

    [Fact]
    public void Reads_assets_targets_packages_only()
    {
        var packages = _reader.ReadResolvedPackages(Fixtures.Path("project.assets.json"));

        // MyApp.Contracts is type=project and must be excluded.
        Assert.DoesNotContain(packages, p => p.Id == "MyApp.Contracts");

        Assert.Contains(packages, p => p.Id == "Newtonsoft.Json" && p.Version == "13.0.3");
        Assert.Contains(packages, p => p.Id == "Serilog" && p.Version == "3.1.1");
        // Transitive resolved package present in the graph.
        Assert.Contains(packages, p => p.Id == "Serilog.Sinks.Console" && p.Version == "5.0.1");
        Assert.Contains(packages, p => p.Id == "System.Text.Json" && p.Version == "8.0.5");
    }

    [Fact]
    public void Assets_dedupes_same_package_across_frameworks()
    {
        var packages = _reader.ReadResolvedPackages(Fixtures.Path("project.assets.json"));

        // Newtonsoft.Json 13.0.3 appears in both net8.0 and net9.0 targets => one entry.
        Assert.Single(packages, p => p.Id == "Newtonsoft.Json" && p.Version == "13.0.3");
        Assert.Equal(4, packages.Count);
    }

    [Fact]
    public void Reads_lock_dependencies_including_transitive_skipping_project()
    {
        var packages = _reader.ReadResolvedPackages(Fixtures.Path("packages.lock.json"));

        Assert.DoesNotContain(packages, p => p.Id == "MyApp.Contracts");

        Assert.Contains(packages, p => p.Id == "Newtonsoft.Json" && p.Version == "13.0.3"); // Direct
        Assert.Contains(packages, p => p.Id == "Serilog.Sinks.Console" && p.Version == "5.0.1"); // Transitive
        Assert.Contains(packages, p => p.Id == "System.Text.Json" && p.Version == "8.0.5"); // CentralTransitive (kept)
    }

    [Fact]
    public void Lock_dedupes_across_frameworks()
    {
        var packages = _reader.ReadResolvedPackages(Fixtures.Path("packages.lock.json"));

        Assert.Single(packages, p => p.Id == "Newtonsoft.Json" && p.Version == "13.0.3");
        // Newtonsoft, Serilog, Serilog.Sinks.Console, System.Text.Json
        Assert.Equal(4, packages.Count);
    }

    [Fact]
    public void Both_formats_produce_the_same_resolved_set()
    {
        var fromAssets = _reader.ReadResolvedPackages(Fixtures.Path("project.assets.json"))
            .Select(p => p.Id + "/" + p.Version)
            .OrderBy(x => x)
            .ToArray();

        var fromLock = _reader.ReadResolvedPackages(Fixtures.Path("packages.lock.json"))
            .Select(p => p.Id + "/" + p.Version)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(fromAssets, fromLock);
    }
}
