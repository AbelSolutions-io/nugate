using Xunit;

namespace NuGate.Tool.Tests;

public class RestoreOutputDiscoveryTests
{
    [Fact]
    public void Finds_project_assets_json_under_nested_obj_directories()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src", "App", "obj", "project.assets.json");

        var found = RestoreOutputDiscovery.Discover(temp.Path);

        var file = Assert.Single(found);
        Assert.Equal("src/App/obj/project.assets.json", file.RelativePath);
    }

    [Fact]
    public void Finds_packages_lock_json_at_project_root()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src", "App", "packages.lock.json");

        var found = RestoreOutputDiscovery.Discover(temp.Path);

        var file = Assert.Single(found);
        Assert.Equal("src/App/packages.lock.json", file.RelativePath);
    }

    [Fact]
    public void Skips_node_modules_git_and_bin_directories()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("node_modules", "somepkg", "obj", "project.assets.json");
        temp.CreateFile(".git", "obj", "project.assets.json");
        temp.CreateFile("src", "App", "bin", "obj", "project.assets.json");
        temp.CreateFile("src", "App", "obj", "project.assets.json"); // the one that should survive

        var found = RestoreOutputDiscovery.Discover(temp.Path);

        var file = Assert.Single(found);
        Assert.Equal("src/App/obj/project.assets.json", file.RelativePath);
    }

    [Fact]
    public void Ignores_unrelated_files()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src", "App", "App.csproj");
        temp.CreateFile("README.md");

        var found = RestoreOutputDiscovery.Discover(temp.Path);

        Assert.Empty(found);
    }

    [Fact]
    public void Returns_results_sorted_deterministically()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src", "Zeta", "obj", "project.assets.json");
        temp.CreateFile("src", "Alpha", "obj", "project.assets.json");

        var found = RestoreOutputDiscovery.Discover(temp.Path);

        Assert.Equal(
            new[] { "src/Alpha/obj/project.assets.json", "src/Zeta/obj/project.assets.json" },
            found.Select(f => f.RelativePath));
    }

    [Fact]
    public void Finds_multiple_projects()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src", "App", "obj", "project.assets.json");
        temp.CreateFile("src", "Lib", "obj", "project.assets.json");
        temp.CreateFile("tests", "App.Tests", "packages.lock.json");

        var found = RestoreOutputDiscovery.Discover(temp.Path);

        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void Throws_when_root_does_not_exist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nugate-does-not-exist-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => RestoreOutputDiscovery.Discover(missing));
    }
}
