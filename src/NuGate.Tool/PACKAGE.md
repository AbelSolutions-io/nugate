# NuGate.Tool

**A dependency age gate for NuGet, as a CLI.** `nugate check` fails (non-zero exit) if any resolved package version — direct or transitive — was published less than N days ago (default 7), unless explicitly allowlisted.

```bash
dotnet tool install -g NuGate.Tool

# in CI, between restore and build:
dotnet restore
nugate check          # --json for machine-readable output
dotnet build --no-restore
```

Running before the build matters: malicious packages can carry MSBuild targets that execute *during* the build, and the CLI gates before any of them run. For dev machines, consider [`NuGate.Build`](https://www.nuget.org/packages/NuGate.Build) — the same gate as an MSBuild task installed with one `PackageReference`.

## Configure — `nugate.json` at the repo root

```json
{
  "minAgeDays": 7,
  "mode": "enforce",
  "onApiFailure": "fail",
  "allow": [
    { "id": "SomePackage", "version": "3.1.4", "expires": "2026-08-01", "reason": "hotfix" }
  ],
  "exemptPrefixes": ["MyCompany."]
}
```

No config file means the defaults (7 days, enforce, fail closed). Allowlist entries can carry an expiry so exceptions don't fossilize; `exemptPrefixes` covers internal packages that aren't on nuget.org.

## What this does / does not do

NuGate **enforces a dependency age policy**. That's all it does, and it does it deliberately.

It does **not** detect malware, scan for vulnerabilities, or prevent attacks. A 30-day-old compromised package passes the gate by design. Cooldowns shrink the window in which freshly poisoned package versions can reach you — they don't close it.

Ages come from the immutable nuget.org catalog `created` timestamp — not the registration `published` field, which nuget.org resets when a version is unlisted. Unlisted versions are flagged regardless of age.

---

Docs & guide: [nugate.dev](https://nugate.dev) · Source: [GitHub](https://github.com/AbelSolutions-io/nugate) · Apache-2.0 © Abel Solutions · *Not affiliated with Microsoft or the NuGet project.*
