# Issue #152 verification

[日本語](path-state-isolation-verification_ja.md)

The implementation follows the accepted Wiki Phase 3, ADR-013, and
[path/state design](path-state-isolation-design.md). Branch: `codex/152-path-state-isolation`.

## Verified on 2026-10-03

| Check | Result |
|---|---|
| Debug / Release solution and VSIX builds | Zero warnings and errors |
| Full Release Core tests | 311 passed, 5 skipped, 0 failed |
| Full Release UI tests | 317 passed, 1 skipped, 0 failed |
| Windows junction smoke | In-root existing/future files accepted; escaping files rejected |
| CLI 0.159.1 stable / experimental surfaces | Passed |
| Schema-cache contract / live initialize | Passed |
| VSIX contract and payload | v18; Worker, Extension, both Contracts copies match Release outputs |
| Embedded raw XAML | Matches source |
| VSIX identity | Existing identity, publisher `kkamegawa`, Preview flag retained |
| Text format / diff | UTF-8 BOM, CRLF, `git diff --check` passed |

The six skips require symlink creation capability unavailable on this host. Actual
junction tests supplement the physical-containment checks; they do not replace the
skipped symlink/loop cases.

Release VSIX SHA-256: `9F35C339BC4A1566553D9BFDBD5AAC28299F8BAD8DD08B068214FB04F867F73A`.

## Pending acceptance

Experimental Instance screenshots of connection switching, cleared selected state,
attachment rejection, and remote skill selection remain unverified. Visual Studio
discovery returned no installed instance. The selected `orca` executable was not
recognized; the computer-use skill requires stopping when that executable cannot run.
Unit tests and package inspection do not establish actual rendered UI acceptance.

## Follow-up verified on 2026-10-04

Branch: `fix/152-path-state-followups`. Scope: owner-initiated account-change reconnect,
whole-turn attachment rejection, mapped thread working directory, Worker rate-limit filter,
and the missing remote-mode and junction tests.

| Check | Result |
|---|---|
| Debug / Release solution builds | Zero warnings and errors |
| Full Release Core tests | 327 passed, 5 skipped, 0 failed |
| Full Release UI tests | 318 passed, 1 skipped, 0 failed |
| Automated junction test | In-root existing/future files accepted; escaping junction rejected |
| Schema-cache contract / live initialize (CLI 0.159.1) | Passed |
| Text format / diff | UTF-8 BOM, CRLF, `git diff --check` passed |

The five skips are the symlink cases that need symlink creation capability. The new
junction test runs without that capability.

## Pending acceptance (follow-up)

Experimental Instance screenshots of sign-in/sign-out reconnect, remote profile switching,
cleared selected state, attachment rejection, and remote skill selection remain unverified.
Visual Studio 2026 Insiders is installed, but this session has no desktop control, and the
sign-in scenarios require the maintainer's own ChatGPT account and remote app-server.
