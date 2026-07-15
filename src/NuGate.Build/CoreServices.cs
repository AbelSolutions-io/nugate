using NuGate.Core;

namespace NuGate.Build;

/// <summary>
/// Creates the concrete NuGate.Core implementations of <see cref="IResolvedPackageReader"/> and
/// <see cref="INuGetTimestampProvider"/> for the MSBuild task's composition root. Construction
/// failure is reported (never thrown) so the task can fail closed with a clear message.
/// </summary>
internal static class CoreServices
{
    public static bool TryCreateReader(out IResolvedPackageReader reader, out string? error)
    {
        try
        {
            reader = new ResolvedPackageReader();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            reader = null!;
            error = $"could not construct the NuGate.Core package reader: {ex.Message}";
            return false;
        }
    }

    public static bool TryCreateTimestampProvider(out INuGetTimestampProvider provider, out string? error)
    {
        try
        {
            // Defaults: shared HttpClient, %LOCALAPPDATA%/nugate/cache, nuget.org registration base.
            provider = new NuGetTimestampProvider();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            provider = null!;
            error = $"could not construct the NuGate.Core timestamp provider: {ex.Message}";
            return false;
        }
    }
}
