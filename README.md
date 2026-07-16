# NuGate

**A dependency age gate for NuGet.** The build fails if any resolved package version — direct or transitive — was published less than N days ago (default 7), unless explicitly allowlisted.

> **v0.1.2 is live on nuget.org:** [`NuGate.Build`](https://www.nuget.org/packages/NuGate.Build) · [`NuGate.Tool`](https://www.nuget.org/packages/NuGate.Tool)

## The pieces

| Artifact | Form | Job |
|---|---|---|
| `NuGate.Build` | MSBuild task (NuGet package) | One `PackageReference` in `Directory.Build.props` gates every build in the repo — including on dev machines |
| `NuGate.Tool` | dotnet tool | `nugate check` between restore and build in CI; gates *before* MSBuild targets execute |
| GitHub Action | Composite action | Thin wrapper around `nugate check` |

## Install

**Gate every build in a repo** (dev machines included) — add to `Directory.Build.props`:

```xml
<ItemGroup>
  <PackageReference Include="NuGate.Build" Version="0.1.2" PrivateAssets="all" />
</ItemGroup>
```

**Gate CI before anything builds** — run between restore and build:

```bash
dotnet tool install -g NuGate.Tool
nugate check          # non-zero exit on violations; --json for machine-readable output
```

**Or as a GitHub Action** (needs a dotnet SDK on the runner, e.g. `actions/setup-dotnet`):

```yaml
- uses: AbelSolutions-io/nugate@v0.1.2
```

## Configure — `nugate.json` at the repo root

```jsonc
{
  "minAgeDays": 7,
  "mode": "enforce",            // "warn" for rollout
  "onApiFailure": "fail",       // fail closed by default; "warn" opts out
  "allow": [
    { "id": "SomePackage", "version": "3.1.4", "expires": "2026-08-01", "reason": "hotfix" }
  ],
  "exemptPrefixes": ["MyCompany."]
}
```

No config file means the defaults above (7 days, enforce, fail closed). Allowlist entries can carry an expiry so exceptions don't fossilize; `exemptPrefixes` covers internal packages that aren't on nuget.org.

## What this does / does not do

NuGate **enforces a dependency age policy**. That's all it does, and it does it deliberately.

It does **not** detect malware, scan for vulnerabilities, or prevent attacks. A 30-day-old compromised package passes the gate by design. Cooldowns shrink the window in which freshly poisoned package versions can reach you — they don't close it.

Ages come from the immutable nuget.org catalog `created` timestamp — not the registration `published` field, which nuget.org resets when a version is unlisted. Unlisted versions are flagged regardless of age. Timestamps are cached locally; listed status is revalidated after 24 hours.

## License

[Apache-2.0](LICENSE) © Abel Solutions

*Not affiliated with Microsoft or the NuGet project.*
