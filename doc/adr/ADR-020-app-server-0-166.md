# ADR-020: Codex App Server 0.166.0 support, per-version tiers, and staged delivery

[日本語](ADR-020-app-server-0-166_ja.md)

- Date: 2026-10-31
- Status: Accepted
- Tracking: [Issue #240](https://github.com/kkamegawa/vsextensionforcodex/issues/240) and child Issues [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241), [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242), [#243](https://github.com/kkamegawa/vsextensionforcodex/issues/243), [#244](https://github.com/kkamegawa/vsextensionforcodex/issues/244)

## Context

Codex CLI released 0.164, 0.165, and 0.166 within a week, and 0.166 changes several things that affect the extension:

- The `detached` review delivery is removed (openai/codex#57667).
- The default thread unload delay falls to 60 seconds (openai/codex#57242), so unsubscribed conversations receive `thread/closed` sooner.
- Provider rejections arrive as structured JSON-RPC error `data`, and `thread/resume` can be rejected while `thread/read` still works.
- `accountSession/switch` stops loaded conversations, and `account/updated` carries no workspace identity.
- The complete Windows package replaces the single executable as the unit that real verification needs.

The product never reaches `detached` from its UI, because `ExecuteReviewAsync` never sets the delivery. The existing `GetFeatureTier` compares with the target version, so raising the target alone would lower 0.164 and 0.165 connections to the 0.163 tier.

## Decision

- **Supported versions:** the minimum stays 0.161.0, the target becomes 0.166.0, and the regression set is 0.165.0, 0.164.0, 0.163.0, 0.162.1, and 0.161.0, as recorded in the ADR-011 amendment of the same date.
- **Version tiers:** tiers 0.165 and 0.166 are added, and each tier compares with its own introduction version. Experimental features also require the negotiated capability.
- **Delivery:** work is split into stages that can each ship alone: compatibility, stable features, experimental features, and integration. Compatibility never waits for the feature work.
- **Review:** `ReviewDelivery` is replaced by `ReviewPlacement`. A separate-conversation review is a new feature built from `thread/start` plus `inline` `review/start` as one Worker operation, available on every supported version. It does not inherit history, and `/fork` covers that need.
- **Linked forks:** 0.166 is the introduction version, because a child's history excludes parent turns only from 0.166 (openai/codex#57256). `attachedToThreadId` is the only source of children.
- **Worktree scope:** local connections only, matching the upstream `repository_cwd_filter`. The extension has no read-only path for running Git on a remote server.
- **Account fingerprint:** adds the workspace ID from `account/read.workspaceRouting.chatgptAccountId`. External switches are not detected while experimental is disabled.
- **Not adopted:** authentication wizards, `account/gcp/setup`, and rewrite suggestions from `turn/moderationMetadata`, whose metadata is untyped and unstable.
- **Worker contract:** becomes v27.

## Consequences

- Separate-conversation review adds conversations to the normal history list, because the new conversation is an ordinary thread.
- Workspace switching interrupts other clients on a shared server, so the confirmation dialog says so.
- Conversation text writes sanitized content to disk under owner separation, with deletion on close and a 24-hour cleanup.
- Four real environments can complete on Fake coverage alone, when recorded as unverified in the release record: SSO-required workspaces, multiple workspaces with the same email and plan, native USD plans, and Vertex AI or Bedrock only.
- The approved design and plan are in [app-server-0-166-design.md](../app-server-0-166-design.md) and [app-server-0-166-plan.md](../app-server-0-166-plan.md).
