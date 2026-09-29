---
name: codex-cli-contract-update
description: 'Move this extension''s pinned Codex CLI app-server contract to a new release (for example 0.155.1 to 0.159.1), and diagnose Codex CLI version problems in the running extension. Use when the user asks to "update the app-server contract", "target Codex X.Y.Z", "0.xxx.x前提にして", or "最新のapp server contractを取り入れて"; when a model that works in ChatGPT (for example gpt-6.1) is missing from the model picker; when the status bar shows an older Codex version than expected; or when every tool call fails with "failed to spawn code-mode host". Covers the manifest, schema cache, scripts, CI, docs, issues, and wiki.'
---

# Codex CLI Contract Update

The extension verifies one pinned **target** Codex CLI release and keeps one older **regression**
release for structural schema comparison. Everything version-specific flows from
`app-server-contract.json`. This skill records the procedure and the traps found while moving the
target from 0.155.1 to 0.159.1.

## Diagnose first: is it really a contract problem?

| Symptom | Cause | Check |
|---|---|---|
| A model that works in ChatGPT is missing from the picker | Codex delivers the model catalog **per CLI version**. The picker shows exactly what `model/list` returns. | Run [scripts/probe-model-list.py](scripts/probe-model-list.py) against the executable. Compare two versions. |
| Status bar shows an older version than the one you installed | `CODEX_PATH` still points elsewhere (often a winget package). The environment is read only at Visual Studio startup. | `echo $CODEX_PATH`, `& $env:CODEX_PATH --version`, then restart every Visual Studio instance. |
| Turns answer, but every file write or command fails with `failed to spawn code-mode host ... (os error 2)` | `CODEX_PATH` points to a **standalone** `codex-*.exe`. Codex needs its helpers next to it. | Install `codex-package-<arch>-pc-windows-msvc.tar.gz` instead (see below). |
| winget does not offer the pinned release | winget lags GitHub releases. | `winget show OpenAI.Codex`. Use the GitHub release package. |

The Worker log is `%TEMP%\Kkamegawa.CodexForVisualStudio\diagnostics.log`. Search it for
`codex-error`, `spawn`, and `Ignoring unsupported app-server notification` before changing code.

### Installing a release correctly (x64 or Arm64)

Use the **package**, never the standalone executable, for an interactive install. The package lays
out `bin\codex.exe`, `bin\codex-code-mode-host.exe`, `codex-resources\` (command runner, Windows
sandbox setup), and `codex-path\rg.exe`. Extract it into `%LOCALAPPDATA%\OpenAI\Codex` so
`CODEX_PATH` is `...\OpenAI\Codex\bin\codex.exe`. The README setup script selects the asset by
`RuntimeInformation.OSArchitecture` and prints the SHA-256 to compare with the release page.

CI and schema generation may keep using the standalone x64 executable: generating schemas and
completing `initialize` do not start tools.

## Procedure

Work in this order. Stop after step 2 and show the user the classified differences before changing
Worker code (design-first rule).

### 1. Pin the release in the manifest

- Get the asset digest: `gh release view rust-vX.Y.Z -R openai/codex --json assets` and read `digest`
  for `codex-x86_64-pc-windows-msvc.exe`.
- In `app-server-contract.json`, set `targetVersion`, set `regressionVersion` to the previous
  target, and keep exactly those two entries under `releases` with `tag`, `asset`, and `sha256`.
- Scripts read versions from the manifest. `generate-schemas.ps1` and `validate-schema-cache.ps1`
  default to `targetVersion` and accept only pinned versions; `install-codex.ps1` still has a
  `ValidateSet` that must list both versions plus `latest`.

### 2. Generate schemas and classify the differences

```powershell
pwsh -NoProfile -File scripts/install-codex.ps1 -Version <ver> -OutputDirectory <scratch>\cli-<ver>
pwsh -NoProfile -File scripts/generate-schemas.ps1 -Version <ver> -Surface stable -CodexPath <exe>
pwsh -NoProfile -File scripts/generate-schemas.ps1 -Version <ver> -Surface experimental -CodexPath <exe>
pwsh -NoProfile -File scripts/compare-schemas.ps1 -BaselineDirectory schemas/<old>/stable -TargetDirectory schemas/<new>/stable -Surface stable -ReportPath <scratch>\diff-stable.txt
pwsh -NoProfile -File scripts/verify-contract-surface.ps1 -SchemaDirectory schemas/<new>/stable
```

Generate both surfaces for both versions (the regression version's experimental cache may be
missing). Then:

- Write the `added` / `removed` / `changed` lists from the reports into `knownDifferences` for both
  surfaces. `compare-schemas.ps1` fails on any unlisted or missing difference.
- File-level `changed` says little. Run [scripts/schema-diff.py](scripts/schema-diff.py) on the
  changed files that belong to `usedMethods` to get property-level changes (added/removed
  properties, `required`, enum values, nullability).
- Classify each change for used methods: **breaking** (removed/renamed property the Worker reads,
  new required property the Worker sends, removed enum value the Worker matches) or **additive**
  (new optional field, new enum value, new method). 0.155.1 to 0.159.1 was entirely additive for used
  methods; only the unused `thread/rollback` was removed.
- New methods and fields belong to the phase issues (#151 to #155) as "X.Y.Z contract additions",
  not to this change.

### 3. Switch build, tests, and CI

- `src/Codex.AppServer.Protocol/Codex.AppServer.Protocol.csproj`: schema directory and both
  `-Version` arguments.
- `scripts/test-schema-cache.ps1`: target version, the cache path literal, the stub that must be
  rejected as "different stable version" (use the regression version), and the prerelease stub.
- Contract tests: bump the `userAgent` fixture and add a test for each new enum value or field the
  Worker parses.
- `.github/workflows/ci.yml` and `release.yml`: the matrix, the compare **baseline and target**
  paths, the surface checks, and the pinned install version. The agent cannot write to `.github`
  (permission-denied); give the user the exact lines, then re-read the files with `git grep` before
  committing. The compare baseline lines are the easiest to get wrong.

### 4. Documents, issues, wiki

- README / README_ja: requirements, setup (package install and SHA-256), limitations, FAQ, build.
  A two-step replacement keeps sentences correct: old target to a placeholder, old regression to old
  target, placeholder to new target.
- ADR-011 amendment with the reason; `doc/plan.md`, `doc/app-server-update-plan(_ja).md`,
  `doc/implementation.md`, `doc/task.md`.
- Issues #149, #150, #156 (versions) and the phase issues (additions): back up each body, edit with
  `gh issue edit --body-file`, and comment the reason.
- Wiki plan pages through the `github-wiki-plan` skill. GitHub wikis are flat: a page stored at
  `plan/<date>/<slug>.md` is served at `wiki/<slug>`.

## Verification

1. `test-schema-cache.ps1`, `compare-schemas.ps1`, and `verify-contract-surface.ps1` for both surfaces.
2. Release build with zero warnings; Core tests three times and UI tests with `CODEX_PATH` unset
   (`env -u CODEX_PATH dotnet test ...`). Two `CodexProcessHost` tests fail when `CODEX_PATH` is set.
3. `scripts/smoke-app-server.ps1 -CodexPath <new exe>`.
4. `probe-model-list.py` against the new executable shows the expected models.
5. Experimental Instance with the **package** install: status shows the new version, the expected
   model is selectable, and a turn that writes a file and runs a command succeeds.
6. `git grep` for the old versions: only regression-baseline references and history remain.
