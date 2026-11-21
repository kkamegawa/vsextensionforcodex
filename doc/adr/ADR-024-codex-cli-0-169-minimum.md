# ADR-024: Codex CLI minimum 0.169.0 and target 0.171.0

[日本語](ADR-024-codex-cli-0-169-minimum_ja.md)

- Date: 2026-11-20
- Status: Accepted
- Tracking: [#326](https://github.com/kkamegawa/vsextensionforcodex/issues/326), [#327](https://github.com/kkamegawa/vsextensionforcodex/issues/327), [#331](https://github.com/kkamegawa/vsextensionforcodex/issues/331)
- Supersedes: [ADR-023](ADR-023-codex-cli-0-167-minimum.md) for current version policy

## Decision

| Role | CLI version |
| --- | --- |
| Accepted minimum for local and remote app-server | 0.169.0 |
| Pinned regression contracts | 0.169.1 and 0.170.0 |
| Pinned target and recommended version | 0.171.0 |

The version gate runs after `initialize` and before `initialized`. The extension refuses an unrecognized version or a version below 0.169.0. Semantic Versioning precedence determines the boundary, including prerelease ordering and ignoring build metadata. A version newer than 0.171.0 may connect and receives the existing newer-version notice.

Pin 0.169.1 for the 0.169 series and compare both generated surfaces with 0.169.0 to verify equivalence. Use 0.169.0 for a separate minimum-version smoke check. Use 0.168.0 as a rejection fixture.

Promote features already available at the minimum to the baseline. Preserve capability gates, optional field handling and applicable protocol fallbacks for the supported versions. Add a newer feature gate when its approved implementation uses a newer app-server contract.

## Consequences

Use the official 0.171.0 complete platform package for new installations. Existing 0.167 and 0.168 users must update the CLI before connecting. The manifest supplies release pins, the stable and experimental method tables, and regression schema differences. Build-time schema validation uses the stable 0.171.0 cache.

[Codex CLI 0.171.0](https://github.com/openai/codex/releases/tag/rust-v0.171.0) · [0.169.0 to 0.169.1](https://github.com/openai/codex/compare/rust-v0.169.0...rust-v0.169.1)
