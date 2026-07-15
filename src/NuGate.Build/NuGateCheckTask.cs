using Microsoft.Build.Framework;

namespace NuGate.Build;

/// <summary>
/// MSBuild task: reads the project's resolved graph from project.assets.json and fails the build
/// on policy violations. Wired in via build/NuGate.Build.targets after package assets resolve —
/// which protects dev machines, where patient zero sat.
/// </summary>
public sealed class NuGateCheckTask : Microsoft.Build.Utilities.Task
{
    /// <summary>Path to this project's obj/project.assets.json (from $(ProjectAssetsFile)).</summary>
    [Required]
    public string AssetsFilePath { get; set; } = string.Empty;

    /// <summary>Repo-root nugate.json; empty means defaults (7 days, enforce, fail-closed).</summary>
    public string ConfigFilePath { get; set; } = string.Empty;

    public override bool Execute()
        => throw new System.NotImplementedException("D2: implement — parse assets, evaluate PolicyEngine, log violations via Log.LogError with allowlist hints.");
}
