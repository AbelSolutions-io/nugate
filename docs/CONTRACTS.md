# NuGate v0 — Frozen Contracts

This file is the coordination contract between parallel work streams. **Do not change any
public shape defined here without updating this file and flagging the change in your
handoff summary** — other branches are building against it.

Spec of record: `C:\marvin\content\nugate-spec.md` (v1.2). Where this file and the spec
disagree, this file wins for interface shapes; the spec wins for product behavior.

## Ownership map

| Branch | Owns | Must not touch |
|---|---|---|
| `feat/core` | `src/NuGate.Core/**` (implement stubs, add internals), `tests/NuGate.Core.Tests/**` | Tool/Build/action/workflows |
| `feat/build-task` | `src/NuGate.Build/**`, `tests/NuGate.Build.Tests/**` (create if useful) | Core public API, Tool, action |
| `feat/tool` | `src/NuGate.Tool/**`, `tests/NuGate.Tool.Tests/**` (create if useful) | Core public API, Build, action |
| `feat/ci` | `action/**`, `.github/workflows/**` | All src/ |

Core's **public API surface may not change** on `feat/build-task` / `feat/tool` — code against
the stubs. If a stub is unbuildable as-specified, note it in your summary; integration resolves it.

## Core public API (stubs in `src/NuGate.Core/`)

- `PackageIdentity(Id, Version)` — resolved package.
- `PackageTimestamp(Created, IsListed)` — `Created` is the **catalog `created`** timestamp.
  Never use registration `published` (nuget.org resets it to 1900-01-01 on unlist).
- `NuGateConfig.Load(path)` — missing file ⇒ spec defaults (7 / enforce / fail-closed);
  unknown JSON properties ⇒ error (typo protection). camelCase JSON, enums as lowercase strings.
- `IResolvedPackageReader.ReadResolvedPackages(path)` — accepts `project.assets.json` **and**
  `packages.lock.json`; returns direct + transitive resolved packages.
- `INuGetTimestampProvider.GetTimestampAsync(pkg, ct)` — null = unknown to nuget.org;
  throws `TimestampLookupException` on API failure (caller applies `onApiFailure`).
  Implementation must cache to disk (`%LOCALAPPDATA%/nugate/cache` or equivalent) — timestamps
  are immutable, cache forever. Batch lookups where the API allows.
- `PolicyEngine.EvaluateAsync(config, packages, timestamps, asOfUtc, ct)` → `PolicyResult`.

### Policy semantics (D1 implements, everyone relies on)

1. Package exempt if its id starts with any `exemptPrefixes` entry (ordinal, case-insensitive).
2. Package allowed if an `allow` entry matches id+version (case-insensitive id) AND
   (`expires` is null OR `asOfUtc` ≤ expires). Expired entries generate a warning, not a pass.
3. Age = `asOfUtc − Created`. Violation (`TooYoung`) if age < `minAgeDays` days.
4. Unlisted version ⇒ `Unlisted` violation regardless of age.
5. `TimestampLookupException` or null timestamp: `onApiFailure=fail` ⇒ `LookupFailed`
   violation (fail closed); `warn` ⇒ warning only.
6. `mode=warn` ⇒ `ShouldFail` is false but violations still fully reported.

## Tool CLI (frozen, implemented on `feat/tool`)

```
nugate check [path] [--config <file>] [--json] [--min-age-days <N>]
```

Exit codes: `0` pass · `1` policy violations · `2` operational error (bad config, fail-closed
API failure, no restore outputs found). Also respond to `nugate --version` / `--help`.

`--json` stdout schema:

```json
{
  "version": 1,
  "passed": false,
  "mode": "enforce",
  "minAgeDays": 7,
  "violations": [
    {
      "id": "Example.Pkg", "version": "2.0.1",
      "kind": "tooYoung",            // tooYoung | unlisted | lookupFailed
      "created": "2026-07-12T09:14:00Z",
      "ageDays": 2.9,
      "allowlistHint": { "id": "Example.Pkg", "version": "2.0.1", "expires": "<yyyy-MM-dd>", "reason": "<why>" }
    }
  ],
  "warnings": ["..."],
  "filesScanned": ["src/App/obj/project.assets.json"]
}
```

## MSBuild task (frozen shape, implemented on `feat/build-task`)

- Task `NuGate.Build.NuGateCheckTask` with `AssetsFilePath` (required), `ConfigFilePath`.
- Hook: after `ResolvePackageAssets`, before `CoreCompile`, respects `$(NuGateEnabled)`.
- Package shape: `IncludeBuildOutput=false`, assemblies under `tasks/netstandard2.0/`,
  props/targets under `build/`. NuGate.Core + System.Text.Json must be packed alongside the
  task assembly. `DevelopmentDependency=true` (consumers get PrivateAssets=all by default).
- Error output: one `Log.LogError` per violation, message includes package, version, age,
  policy, and the exact allowlist JSON hint.

## GitHub Action + CI (implemented on `feat/ci`)

- `action/action.yml` composite: install NuGate.Tool (`dotnet tool install`), run
  `nugate check`, violations as `::error` annotations, propagate exit code.
- `.github/workflows/ci.yml`: build + test on PR/push to main (windows + ubuntu matrix).
- `.github/workflows/release.yml`: tag-triggered pack + push to nuget.org via
  **trusted publishing (OIDC)** — no long-lived API-key secrets anywhere.

## Integration notes (post-merge, 2026-07-15)

- **Catalog-leaf hop (verified against the live API):** the registration leaf's `catalogEntry`
  carries `published` + `listed` but NOT `created`. The immutable `created` lives in the catalog
  leaf referenced by `catalogEntry["@id"]`; `NuGetTimestampProvider` does that extra fetch (skipped
  if `created` is ever inlined). The earlier prose above saying the leaf "carries catalogEntry with
  created" is corrected by this note.
- **Fail-closed exit-code semantics:** `PolicyEngine` converts lookup failures into `LookupFailed`
  *violations* (policy semantics rule 5), so under `onApiFailure=fail` the Tool exits **1** via the
  normal `ShouldFail` path. Exit **2** is reserved for operational errors outside policy evaluation
  (bad config, no restore outputs, or an exception escaping the evaluator — a defensive path that
  should not occur in normal operation).

## House rules (all branches)

- Copy discipline in every user-facing string and package description: NuGate "enforces a
  dependency age policy" — never "protects", "prevents", "secures", "detects malware".
- No new package dependencies in Core without a note in your handoff (Build packs them all).
- No personal names anywhere: author/committer/copyright = "Abel Solutions".
- Commit to your own branch only; don't merge or rebase.
