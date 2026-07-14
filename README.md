# NuGate

**A dependency age gate for NuGet.** The build fails if any resolved package version — direct or transitive — was published less than N days ago (default 7), unless explicitly allowlisted.

> **Status: pre-release.** Under construction — nothing is published to nuget.org yet.

## What this will be

| Artifact | Form | Job |
|---|---|---|
| `NuGate.Build` | MSBuild task (NuGet package) | One `PackageReference` in `Directory.Build.props` gates every build in the repo — including on dev machines |
| `NuGate.Tool` | dotnet tool | `nugate check` between restore and build in CI; gates *before* MSBuild targets execute |
| GitHub Action | Composite action | Thin wrapper around `nugate check` |

## What this does / does not do

NuGate **enforces a dependency age policy**. That's all it does, and it does it deliberately.

It does **not** detect malware, scan for vulnerabilities, or prevent attacks. A 30-day-old compromised package passes the gate by design. Cooldowns shrink the window in which freshly poisoned package versions can reach you — they don't close it.

## License

[Apache-2.0](LICENSE) © Abel Solutions

*Not affiliated with Microsoft or the NuGet project.*
