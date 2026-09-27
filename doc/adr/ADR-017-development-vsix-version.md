# ADR-017: Development VSIX version follows the latest release

- Date: 2026-09-28
- Status: Accepted
- Task: GitHub Issue #158

## Context

The repository defaulted to VSIX version `0.1.0.0` after release `v0.2.0`. Visual Studio 18
Insiders already had version `0.2.0.0` installed globally with the same extension ID. F5 deployed
the corrected `0.1.0.0` build to the Experimental Instance, but the instance loaded the installed
`0.2.0.0` build. Its Worker apphost still failed before account status could be read.

## Decision

- Set the normal development `VersionPrefix` to `0.2.1`, so the generated Debug VSIX version
  `0.2.1.0` is newer than the installed `0.2.0.0` release.
- Keep release packaging governed by ADR-007: the release workflow passes the tag-derived
  `-p:Version` explicitly, which overrides the development default.
- Advance the development default after each release so F5 uses the current source when a
  published version of the same extension is installed.

## Consequences

F5 deployments can be distinguished from the installed release by their manifest and assembly
versions. A successful build alone does not establish which version the Experimental Instance
loaded; runtime diagnostics must confirm the version when debugging deployment failures.
