namespace NuGate.Tool.Tests;

/// <summary>A scratch directory, deleted on dispose. Used to exercise real-filesystem discovery.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nugate-tool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string CreateFile(params string[] relativeSegments)
    {
        var fullPath = System.IO.Path.Combine(new[] { Path }.Concat(relativeSegments).ToArray());
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, "{}");
        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; leaked temp dirs don't fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
