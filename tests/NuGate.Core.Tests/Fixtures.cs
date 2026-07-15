using System;
using System.IO;

namespace NuGate.Core.Tests;

/// <summary>Resolves paths to the canned fixture files copied next to the test assembly.</summary>
internal static class Fixtures
{
    public static string Path(string name)
        => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}
