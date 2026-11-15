# ADR-023: Codex CLI minimum 0.167.0 and target 0.169.0

[日本語](ADR-023-codex-cli-0-167-minimum_ja.md)

- Date: 2026-11-16
- Status: Accepted
- Tracking: [Issue #268](https://github.com/kkamegawa/vsextensionforcodex/issues/268) ([#274](https://github.com/kkamegawa/vsextensionforcodex/issues/274), [#276](https://github.com/kkamegawa/vsextensionforcodex/issues/276)); implementation [Issue #270](https://github.com/kkamegawa/vsextensionforcodex/issues/270) and [Issue #272](https://github.com/kkamegawa/vsextensionforcodex/issues/272)
- Related: [ADR-011](ADR-011-app-server-contract.md), [ADR-020](ADR-020-app-server-0-166.md), [ADR-022](ADR-022-visual-studio-2026-dotnet-10.md)

## Context

Codex CLI released 0.167.0, 0.167.1, 0.167.2, 0.168.0, and 0.169.0 between 2026-11-06 and 2026-11-13. On 2026-11-16, 0.169.0 was the latest stable release; 0.171.0 existed only as alpha builds. The extension supported 0.161.0 through 0.166.0 with five version tiers and fallbacks for each. Pinning every patch release grows the regression matrix without adding protocol coverage.

The schemas were generated from the published Windows executables, verified against their release digests, for 0.166.0, 0.167.0, 0.167.1, 0.167.2, 0.168.0, and 0.169.0 on the stable and experimental surfaces.

- 0.167.0, 0.167.1, and 0.167.2 produce identical schemas on both surfaces; only the generator metadata differs.
- From 0.166.0 to 0.169.0, no method is removed. The additions and shape changes are listed in [app-server-0-169-design.md](../app-server-0-169-design.md).

## Decision

- **Minimum:** 0.167.0 for local and remote servers. Every 0.167.x release may connect. Anything below 0.167.0 is rejected after `initialize` and before `initialized`.
- **Target:** 0.169.0.
- **Pinned releases:** only the latest patch release of each minor series is pinned and verified: 0.167.2 and 0.168.0 as regression versions, and 0.169.0 as the target. 0.167.0 is not pinned. When a newer patch of a pinned minor series is released, it replaces the pinned patch.
- **Manifest validation:** `minimumVersion` must be in the same minor series as the oldest pinned release and must not be above it. It is no longer required to be a regression version or a pinned release.
- **Feature tiers:** `Unsupported`, `Baseline` (0.167), `Tier168`, and `Tier169`. The 0.163 to 0.166 gates and their fallbacks are removed. Each tier compares with its own introduction version.
- **Accepted versus verified:** connection is accepted for every 0.167.x release by policy. Runtime behavior is verified only on the pinned 0.167.2. For 0.167.0 and 0.167.1, the evidence is limited to the protocol surface: their schemas are identical to 0.167.2. A later 0.167.x release that is not pinned has no schema evidence until it is generated and compared.

## Alternatives

- **Keep the minimum at 0.161.0.** Rejected. It keeps five tiers and their fallbacks, and the platform change in ADR-022 already makes every user install a new release.
- **Pin 0.167.0 as the minimum.** Rejected. It adds a regression version whose schema equals 0.167.2 and that has known runtime fixes in later patches.
- **Set the minimum to 0.167.2.** Rejected. 0.167.0 and 0.167.1 expose the same schema as 0.167.2, so rejecting them adds no protocol safety.

## Consequences

- Users of 0.166.0 and earlier must update the CLI before connecting.
- Behavior differences inside 0.167.x that do not change the schema, such as runtime fixes, are covered only by the 0.167.2 verification.
- The contract manifest, scripts, Fake app-server, tests, and setup documents use the new versions.

## Decision history

- 2026-11-16: Accepted. The maintainer approved the version policy in the implementation plan of 2026-11-15. The comparison of the 0.167.0, 0.167.1, and 0.167.2 schemas was recorded on the same date.
