# NuGate.Build consume-the-package e2e (deferred to integration)

This fixture is a standalone consumer that installs `NuGate.Build` from a repo-local feed and lets
the gate run during its build. It is **excluded from the solution build** (the test csproj excludes
`fixtures/**`) so it doesn't run against the stubbed Core. Run it by hand once `feat/core` is merged
and Core is real.

## Prerequisites

- NuGate.Core implements `IResolvedPackageReader` and `INuGetTimestampProvider` with public
  parameterless constructors (see `src/NuGate.Build/CoreServices.cs`), OR Core exposes a factory and
  `CoreServices` is updated to call it.

## Commands (run from the worktree root)

```powershell
# 1. Pack the task package into the local feed the fixture points at.
dotnet pack src/NuGate.Build/NuGate.Build.csproj -c Release -o artifacts/

# 2. (Optional) re-verify the package shape.
pwsh tests/NuGate.Build.Tests/verify-package.ps1

# 3. Restore + build the consumer. Its NuGet.config adds the local artifacts feed; the empty local
#    package cache flag forces a fresh pull of the just-packed version.
dotnet build tests/NuGate.Build.Tests/fixtures/ConsumerProject/ConsumerApp.csproj -c Release --no-incremental

# If the package version was already cached from a prior run, clear it first:
#   dotnet nuget locals global-packages --clear
```

## Expected behavior

- **Clean policy** (all resolved packages older than `minAgeDays`): build succeeds; no NuGate errors.
- **A too-young dependency**: build fails with one `error` per violation naming the package, version,
  age in days, the policy, and the exact `nugate.json` allow snippet to override it.
- **`"mode": "warn"`** in `nugate.json`: violations appear as `warning`s and the build still succeeds.
- **Gate opt-out**: `dotnet build ... -p:NuGateEnabled=false` skips the gate entirely.
- **Design-time builds** (IDE): the target is skipped (`$(DesignTimeBuild) == 'true'`).

## Negative test (force a violation)

Add a very recently published package to `ConsumerApp.csproj`, e.g.:

```xml
<PackageReference Include="<some.package.published.in.the.last.day>" Version="<newest>" />
```

Restore + build and confirm the gate fails the build with the allowlist hint, then add that snippet
to `nugate.json`'s `allow` array and confirm the build passes.
