# ADR-011: App Server contract and request routing

- Date: 2026-09-13
- Status: Accepted
- Tracking: Codex App Server update parent Issue

## Decision

- Validate the target client contract against CLI 0.155.1. Generate standard and experimental schemas for both 0.154.0 and 0.155.1, keep 0.154.0 as the regression source, and contract-test their structural differences. Cache the target schemas separately with version and generation-option metadata.
- Determine feature support from declared capabilities, known read-only contracts, and `-32601`; never probe a mutating operation.
- Route server requests by exact method name and request shape. Unknown requests receive a protocol-appropriate rejection and cannot enter a generic approval path.
- Track connection generation, thread, turn, and pending server request identity so late responses cannot revive completed state. Disconnect completes or cancels pending requests according to their ownership.

## Consequences

Contract drift from the previous stable baseline becomes visible in tests and stale connection events are safely ignored. Generated schema output remains uncommitted.
