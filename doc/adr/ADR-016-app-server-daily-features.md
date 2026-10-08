# ADR-016: Daily-use App Server features

- Date: 2026-09-13; amended 2026-10-08
- Status: Accepted
- Tracking: [Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155), under [Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149)

## Decision

- Keep `turn/plan/updated` as the structured step snapshot and treat experimental `item/plan/delta` as optional provisional text; the completed item is authoritative.
- Treat the live model catalog as the source of truth, including advertised reasoning levels, input modalities, and access metadata. Validate effective-model inputs before `turn/start` without silently changing the model.
- Add explicit `/shell [--timeout-ms N] -- <command>` using `thread/shellCommand`, with exact command preservation, local approval evaluation, independent RPC and execution deadlines, and one pending submission per thread. CLI 0.159.1 executes this method unsandboxed with full access.
- Render typed bounded results and mapped local file actions. Validate paths at action time and restrict image previews to bounded PNG/JPEG data.
- Treat saved attachments as metadata, never automatic model input. Add/remove are explicit operations over the existing bounded recovery store; client-owned payloads use `relaycodex.file.v1`, while unknown records remain visible and read-only.
- Offer Windows sandbox setup only for owned local stdio connections, with explicit mode and truthful indeterminate outcome states.
- Keep daemon/worktree lifecycle, voice/Realtime, dynamic tools, ExternalMessage, plugin import, and attestation outside this plan.

## Consequences

Daily-use features are bounded and policy-controlled. The approved Issue #155 design and implementation plan are recorded in [daily-use-app-server-design.md](../daily-use-app-server-design.md) and [daily-use-app-server-plan.md](../daily-use-app-server-plan.md). CLI, SDK, runtime, and package versions remain unchanged; the Worker contract takes the next available version at merge time. Phase 6 is tracked by Issue #155 and remains subject to the integrated validation and visual evidence required by Issue #156.
