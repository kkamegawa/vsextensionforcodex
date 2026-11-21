# Codex App Server 0.171 support design

[日本語](app-server-0-171-design_ja.md)

[ADR-024](adr/ADR-024-codex-cli-0-169-minimum.md) defines the minimum, target and pinned versions. [The 0.169 feature design](app-server-0-169-design.md) defines the Phase 4 behavior shared by the supported versions. The revised [implementation plan](https://github.com/kkamegawa/vsextensionforcodex/wiki/codex-cli-0-170-support) assigns the feature changes to their tracking issues.

## App-server contract

| Contract change | Extension behavior |
| --- | --- |
| Stable, 0.171.0 `modelProvider/default/set` | Use `providerId` on 0.171 and retain the approved `config/value/write` path on 0.169 and 0.170. Make one write per user action. [#302](https://github.com/kkamegawa/vsextensionforcodex/issues/302) |
| Experimental, 0.171.0 `thread/search/stream` | Scope partial results and cancellation to the active request and connection. Replace partials with the authoritative final page. Earlier versions with experimental API use one-shot search. The stable surface uses the title filter. [#304](https://github.com/kkamegawa/vsextensionforcodex/issues/304) |
| 0.170.0 answer display v1 | Hold all assistant-message body deltas until the item completes. Show progress while waiting. Apply the presentation before display, copying or export. Hide explicitly moderated content when its instruction is missing or unsupported. Use authoritative history for interrupted or restored content. [#328](https://github.com/kkamegawa/vsextensionforcodex/issues/328) |
| 0.171.0 MCP registration scope | Route supported operations by thread, server name and registration environment. Suppress ambiguous actions. [#329](https://github.com/kkamegawa/vsextensionforcodex/issues/329) |
| 0.171.0 account update reason and terminal denial | For `workspaceRoutingReady`, confirm the current owner before updating. Preserve normal invalidation for missing, unknown or mismatched identity. Display `bioPolicy` as a terminal denial. [#305](https://github.com/kkamegawa/vsextensionforcodex/issues/305) |
| Enterprise MCP and OTLP | Use the existing CLI sign-in and telemetry configuration surfaces. The 0.171 CLI handles IdPs that omit refresh tokens and local managed OTLP header helpers. Document the compatibility limits and that Enterprise environment acceptance is waived. [#330](https://github.com/kkamegawa/vsextensionforcodex/issues/330) |

Each feature issue owns its Worker contract changes and parser tests. The baseline migration uses the current method set on all three pinned releases. The manifest records structural differences for both generated schema surfaces.

## Verified schema differences

Schema generation and structural comparison were run against the official Windows x64 CLI assets. The minimum-boundary 0.169.0 stable and experimental schemas are structurally equivalent to 0.169.1; 0.169.0 is therefore checked for minimum-version smoke and equivalence, not treated as a separate regression release.

| Comparison | Stable schema files changed | Experimental schema files changed | Worker decisions |
| --- | ---: | ---: | --- |
| 0.169.1 → 0.170.0 | 18 | 23 | Adopt the versioned `AgentMessageDisplay` v1 decision contract on 0.170+; keep the 0.169 streaming behavior. Buffer assistant body text until completion, and fail closed when moderated content has no recognized display instruction. (#328) |
| 0.170.0 → 0.171.0 | 35 | 50 | Use the typed provider-default setter, streamed thread search and cancellation, environment-scoped MCP registration identity, and the new Enterprise MCP/managed OTLP behavior only on the supported version and negotiated capability. (#302, #304, #329, #330) |
| 0.169.1 → 0.171.0 | 35 | 51 | The combined delta is covered by the per-feature version and capability gates above; unrelated schema additions do not cause the Worker to call or interpret unconsumed methods. |

The 0.171 schema introduces the `ModelProviderDefaultSetParams`, `ThreadSearchPartialNotification`, `ThreadSearchCancelParams`/`Response`, and `ThreadRealtimeListParams`/`Response` shapes. MCP operations also carry registration scope through `environmentId`. The Worker passes these fields only through the feature paths that consume them; an absent or ambiguous MCP identity disables the affected operation rather than falling back to a name-only route. `workspaceRoutingReady` remains an owner-checked refresh signal and `bioPolicy` remains a terminal denial. (#305, #329)

## Acceptance

Build and generate schemas with 0.171.0. Validate the manifest, compare stable and experimental regressions, and check the used method tables for each pinned CLI. Confirm real CLI initialization at the minimum and for each pin, and rejection of 0.168.0 before `initialized`. Test the local and remote gates, prerelease and build metadata boundaries, and newer-version notice. [#331](https://github.com/kkamegawa/vsextensionforcodex/issues/331) tracks integrated acceptance, including Visual Studio 2026 Light and Dark theme checks for user interface changes.
