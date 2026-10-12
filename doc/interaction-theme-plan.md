# Interaction Theme Colors and Persistent Plan Mode

[日本語](interaction-theme-plan_ja.md)

## Summary

Restore readable theme colors for pending interactions, expose a persistent Plan mode checkbox in the composer, show proposed plans as Codex does, and answer pending questions one at a time. Approved on 2026-10-12.

## Tracking

| Type | Issue |
|---|---|
| Parent | [Issue #192](https://github.com/kkamegawa/vsextensionforcodex/issues/192) |
| Theme colors | [Issue #193](https://github.com/kkamegawa/vsextensionforcodex/issues/193) |
| Persistent Plan mode | [Issue #194](https://github.com/kkamegawa/vsextensionforcodex/issues/194) |
| Proposed plans and Plan availability | [Issue #195](https://github.com/kkamegawa/vsextensionforcodex/issues/195) |
| One question at a time | [Issue #196](https://github.com/kkamegawa/vsextensionforcodex/issues/196) |

## Background

Pending question cards can show dark text on a dark Visual Studio background. Their nested templates and choice controls cannot rely on the root foreground alone. Plan mode is currently consumed after one successful turn and has no explicit composer control for disabling it.

## Design

- Pair pending-interaction text and backgrounds with Visual Studio dynamic theme resources. Cover question, permission, command-approval and MCP form text, and RadioButton/CheckBox content, glyphs and interaction states. Preserve existing selected-surface styles, keyboard focus and accessibility.
- Add a two-way Plan mode checkbox beside the composer settings in the existing wrapping row. `/plan` and `/plan <prompt>` enable the same state. The state survives successful, failed, completed and interrupted turns until explicitly unchecked.
- `turn/start.collaborationMode` is an experimental app-server field; the server rejects it unless the connection negotiated `experimentalApi` (verified with codex 0.161.0). Plan mode is therefore available only when the current connection negotiated Experimental API; the Worker reports that capability in the connection target snapshot. The saved setting applies on the next connect, so availability and the sent mode follow the connection, not the setting. Otherwise the checkbox is disabled with an explanatory tooltip (which says when a reconnect is still pending), `/plan` fails with guidance to enable Experimental API and reconnect, and turns omit `collaborationMode`. The Worker enforces the same rule: it omits `default` and rejects `plan` on a non-experimental connection.
- Snapshot the selected mode when preparing a turn. On a connection that negotiated Experimental API, disabling sends collaboration mode `default` explicitly on the next turn; omitting the field cannot be relied upon to clear server state. Changes do not alter an already running turn.
- A collaboration mode carries a required model. When it must be sent and no model is selected, the turn is not started: the composer keeps the text and a status message asks the user to select a model. Without a collaboration mode, an empty selection still defers to the app-server default model.
- Keep mode state with its conversation and owner, including same-owner draft/recovery paths. Do not leak it into another conversation or account/endpoint owner. Preserve existing owner-generation guards. Every reconnect attempt receives a new owner partition, so a transient recovery sets the Plan selections aside when the connection is lost and restores them only when the recovery reconnects to the validated same target; any other outcome discards them. Plan mode alone is a setting, not draft content, so it never creates a quarantined draft. Argumentless `/plan` without a conversation enables the threadless selection, which moves to the conversation created from it. Plan state changes run on the UI context and only for the current owner.
- Render the proposed plan text from `plan` items (streamed `item/plan/delta` and the authoritative completed text) as Markdown blocks under a "Proposed plan" heading, like agent messages. Structured `turn/plan/updated` steps keep their step list.
- After a turn that was started in Plan mode completes with a proposed plan in the selected conversation, show an "Implement this plan?" card as Codex does. "Yes, implement this plan" disables Plan mode for that conversation and starts a default-mode turn with the prompt "Implement the plan.". "No, stay in Plan mode" dismisses the card. The card expires when the conversation, owner or turn changes.
- Pending questions are answered one at a time. A multi-question request shows "Question n of N" with Back and Next; Next requires an answer to the current question when the request requires answers, and Submit on the last question sends all answers in a single response as the protocol requires. Cancel stays available on every step. Pending cards scroll by pixel, so a card taller than the visible area remains reachable to its actions.
- Keep package versions, approval policies, secret redaction and untrusted-input handling unchanged. Update English documentation and matching Japanese translations.

## Implementation phases

1. Record the final design, then implement theme pairings and themed choice controls under #193.
2. Implement persistent Plan mode, composer binding, slash/status consistency and recovery under #194.
3. Add regression coverage, build and inspect the packaged raw XAML, and record visual evidence and any unavailable checks.
4. Implement proposed-plan rendering, the implementation offer and Plan availability under #195, and one-question-at-a-time cards under #196, with the same regression and rendering evidence.

## Verification

Cover consecutive Plan turns, explicit disabling, failed starts, changes during start, conversation/owner switching and recovery, non-experimental connections, a missing model, proposed-plan rendering, the implementation offer, and multi-question stepping including a card taller than the pending surface. Exercise actual WPF rendering and inspect Dark, Light/Blue, High Contrast, live theme switching, keyboard focus and narrow-width wrapping. Run the relevant UI/Core suites and warning-free solution builds. Verify the packaged raw XAML matches the source. Missing Experimental Instance evidence remains incomplete and must not be reported as passed.
