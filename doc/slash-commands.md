# Slash Command Integration

## Scope

The Visual Studio extension recognizes Codex commands only when `/` is the first
input character. Built-in commands and structured skills share one inline,
virtualized suggestion list. Built-in commands are never sent to the model as
prompt text. A leading `//` escapes command mode and sends one literal leading slash.

The implementation is tracked by GitHub Issue #46 and its four sub-issues.

## Supported commands

| Category | Commands | Behavior |
|---|---|---|
| App Server operations | `/compact`, `/feedback`, `/fork`, `/goal`, `/mcp`, `/review` | Invoke dedicated typed Worker RPC methods. |
| Next-turn settings | `/fast`, `/model`, `/permissions` (`/approve` alias), `/personality`, `/plan`, `/reasoning` | Update typed fields used by the next `turn/start`. Except for picker selections, these settings are consumed by the next started turn. |
| Visual Studio operations | `/ide-context`, `/init`, `/status` | Toggle bounded editor context, safely create `AGENTS.md`, or show local session state. |

The following commands remain hidden because the app-server or the current
single-thread UI cannot preserve their official semantics:
`/cloud`, `/cloud-environment`, `/local`, `/memories`, `/project`,
and `/side`. Direct input produces a local unsupported message.

`/review` supports uncommitted changes, a base branch, a commit, or custom
instructions. `/goal` supports show (alias: get), set, edit, pause, resume,
and clear. Goal objectives contain between 1 and 4,000 characters. `/model`
matches the catalog case-insensitively and applies the canonical model id.

`/permissions` is the canonical approval-mode command; `/approve` is a compatibility
alias. With no argument it shows the desired default, the app-server-reported effective
state, and available stable IDs. Built-ins are `ask`, `auto`, `full`, and `custom`;
runtime profiles use `permission:<id>`. Full access requires confirmation because it
disables the Codex sandbox and normal approval prompts, so operations may run without an
extension approval request. Returning from a turn override to `custom` requires a new
thread; omission or `null` is not treated as a reset.

`/plan` without arguments selects Plan mode for the next turn. With arguments,
it immediately starts a Plan-mode turn using the supplied prompt.

## Routing and queueing

`SlashCommandParser` separates normal prompts, escaped prompts, supported
commands, unsupported commands, and unknown commands. Unknown commands return
up to three candidates within a bounded edit distance and never reach
`turn/start` or `turn/steer`.

While a turn is active, `/status`, `/mcp`, and goal display execute immediately.
Other commands enter a per-thread FIFO queue with a limit of ten; commands
issued before any thread exists use a session-scoped queue. Pending setting
commands with the same identity are replaced in place by their newest value.
Queue draining begins after the active turn completes, covers the completed
turn's thread, the selected thread, and the session queue, continues past a
failed command, and pauses as soon as a queued command starts another turn.

Queues are memory-only. They are canceled on disconnect, Worker restart, or
confirmed thread removal. A built-in slash command is never sent through
`turn/steer`; selected skills are held in one independent chip and sent only as
the structured `turn/start` input item `{ type: "skill", name, path }`. Scope and
raw path are used for Worker validation and are not bound to Remote UI. While a
skill chip is pending, send/steer is disabled until the chip is removed or its
turn starts successfully.

The live app-server `skills/list` response is the skill catalog system of record.
The Worker keeps a 60-second memory snapshot and a versioned, per-workspace
persistent stale-while-revalidate snapshot. A persisted hit can populate
non-selectable `Cached - refreshing` rows while one live refresh runs. It never
authorizes a turn: `turn/start` force reloads the live catalog and requires an
enabled exact `Name + Scope + Path` identity.

## Worker contract

Worker contract version 15 adds structured skill invocation, catalog freshness,
invalidation, and exact live identity validation. Version 9 added the validated connected Codex version to
`WorkerStatus` for Remote UI status presentation. Version 8 added typed DTOs
and RPC methods for compact, review, fork, goals, MCP status, feedback, and
rate limits. `StartTurnRequest` includes
reasoning effort, personality, service tier, collaboration mode, and bounded
IDE context. Model entries include supported reasoning efforts, the default
effort, personality support, and service tiers.

App-server error `-32601` disables only the affected command for the current
session. It does not degrade the entire connection. Non-idempotent command
operations are not retried.

Compaction, review mode, goal changes, and rate limits use dedicated typed
events. Their raw JSON payload is not forwarded to the transcript. When a
compaction completes while no turn is active, the Worker restores the Ready
state so queued commands are not blocked behind a finished compaction.

## IDE context and initialization

IDE context is opt-in state controlled by `/ide-context` and defaults to
enabled. Context capture accepts only paths below the workspace root, at most
ten referenced files, and at most 32 KiB of UTF-8 selection text. The active
document and primary selection are captured from the Remote UI command's
Visual Studio client context.

`/init` targets only the workspace root. It previews the complete English
`AGENTS.md` content and requires confirmation. Creation uses create-new
semantics, so an existing file is never overwritten, including races between
the preview and write.

## Remote UI and safety

The composer uses one inline, virtualized suggestion list rather than a popup.
It shows at most eight built-ins and every distinct skill identity safely accepted
by the Worker, including disabled rows. The Worker accepts at most 200 untrusted
entries; reaching that safety bound produces a passive catalog-truncated row.
There is no separate twenty-row UI cap, so keyboard navigation and UI Automation
can reach the twenty-first through final accepted row.
Selecting a built-in creates its command chip; selecting a skill creates an
independent skill chip, clears only the slash query through `SetComposerText("")`,
and keeps the ordinary composer visible. Fixed arguments use themed option buttons.

Up and Down move selection, Enter or Tab accepts a suggestion, Escape closes
the list, and Ctrl+Enter executes. Enter remains a newline when suggestions
are closed. Bindable types use `DataContract` and `DataMember`; commands
implement the Remote UI `IAsyncCommand` contract.

All app-server text displayed in the UI passes through `SafeMarkdownService`.
Worker diagnostics continue to pass through secret redaction. Raw payload JSON
is not rendered.

Persistent catalog snapshots are stored below
`%LOCALAPPDATA%\Kkamegawa.CodexForVisualStudio\skill-catalog\v1`, keyed by a
workspace SHA-256 fingerprint. They are limited to 200 skills, 4 MiB per
workspace, a 24-hour hard expiry, and 64 MiB total, and use atomic replacement,
LRU cleanup, and a bounded cross-process lock. Cache files are untrusted and revalidated on read.
Default prompts, dependency values, icon source paths, raw app-server JSON, and
Remote UI selection IDs are never persisted. Cache failure falls back to live
discovery without blocking the composer.

## Validation

Core tests cover exact app-server method and parameter mappings, typed
notifications, timeout, cancellation, crashes, method-not-found capability
fallback, and non-retry behavior.

UI tests cover parsing, escaping, multiline arguments, aliases, unknown input,
input limits, candidate filtering, queue order and replacement, thread
separation, disconnect cancellation, command-versus-steer separation,
DataContract and IAsyncCommand requirements, XAML structure, keyboard
bindings, accessibility, and input preservation.

Skill-catalog tests cover 0, 1, 20, 21, 200, and 201 server entries; complete
virtualized navigation; stale-to-fresh replacement; empty, unsupported, failed,
and truncated states; workspace isolation; corrupt, expired, and oversized cache
files; generation races; concurrent instances; LRU cleanup; and mandatory live
force-reload validation before skill invocation.
