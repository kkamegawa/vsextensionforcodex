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
| App Server operations | `/compact`, `/feedback`, `/fork`, `/goal`, `/mcp`, `/review`, `/shell` | Invoke dedicated typed Worker RPC methods. `/shell` executes only through explicit invocation and confirmation. |
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
The Worker keeps a 60-second snapshot for the current owner in memory. The pinned
account contract cannot prove stable account continuity, so disk-cache reads and
writes are disabled. The cache never authorizes a turn: `turn/start` force reloads the live catalog and requires an
enabled exact `Name + Scope + Path` identity.

## Worker contract

Worker contract version 18 adds captured-owner request and notification scopes.
Version 15 added structured skill invocation, catalog freshness,
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

The file-store format uses composite workspace/owner SHA-256 keys under
`%LOCALAPPDATA%\Kkamegawa.CodexForVisualStudio\skill-catalog\v2` for future
authoritative owner identities. It retains limits of 200 skills, 4 MiB per
workspace, a 24-hour hard expiry, and 64 MiB total, with atomic replacement,
LRU cleanup, and bounded cross-process locking. Current sessions neither read nor
write these snapshots and never reuse v1 snapshots. Default prompts, dependency
values, icon source paths, raw app-server JSON, and Remote UI selection IDs are
never persisted. See [the owner design](path-state-isolation-design.md).

## Known gaps

Skill icons are not rendered. `interface.iconSmall` is reduced to a presence flag
and every row uses a fixed glyph until the Remote UI image and cache containment
spike proves a safe binding. `dependencies.tools` is parsed and bounded but has
no badge or tooltip yet, so both fields currently cross the contract without a
consumer. The brand-color accent has no High Contrast branch, so a High Contrast
theme still shows the app-server color rather than a Visual Studio theme
resource. A skill rejected by live identity validation at `turn/start` is
recorded in diagnostics but is not reported in the chat surface.

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

## Explicit shell command (Issue #155)

The supported form is exactly `/shell [--timeout-ms N] -- <command>`. `/shell` is the ninth built-in;
the inline suggestions can show up to nine built-ins without dropping skills. Selecting its
suggestion only fills the command chip. Execution requires the explicit Execute action in the
confirmation flow. The parser preserves every character after `--` and rejects an empty command,
duplicate or unknown options, negative or malformed values, and timeout values above `int64`.
Omitting `--timeout-ms` uses the server default of one hour; zero is an immediate timeout;
`--timeout-ms` without a value is rejected.

Only a joined, current-owner idle thread can run `/shell`. Confirmation displays the connection and
profile, thread, exact command as inert text, server working directory, timeout, and the CLI 0.159.1
fact that `thread/shellCommand` always runs unsandboxed with full access. Raw command text is not
written to logs. After Execute, the Worker revalidates target, generation, and cwd and evaluates
`IApprovalPolicyEngine` locally; policy denial is final. Full access and prior grants never bypass
the confirmation.

The empty RPC response acknowledges receipt and does not prove completion. The RPC response
deadline is independent of the command timeout. Events retain their actual thread/turn/item IDs and
are not claimed to correlate with the submission. At most one shell submission is pending per
thread: a definitive acknowledgement or error releases it; acknowledgement timeout or disconnect
keeps it locked until generation retirement. A new generation can submit only after thread status
reports idle. Cancel before dispatch sends nothing. There is no request-specific Stop after dispatch,
no interruption inference from timing/text, no replay, and no fallback to `command/exec` or a local
process.
