# Slash Command Integration

## Scope

The Visual Studio extension recognizes Codex commands only when `/` is the first
input character. Commands are resolved by an allowlisted catalog and are never
sent to the model as prompt text. A leading `//` escapes command mode and sends
one literal leading slash.

The implementation is tracked by GitHub Issue #46 and its four sub-issues.

## Supported commands

| Category | Commands | Behavior |
|---|---|---|
| App Server operations | `/compact`, `/feedback`, `/fork`, `/goal`, `/mcp`, `/review` | Invoke dedicated typed Worker RPC methods. |
| Next-turn settings | `/fast`, `/model`, `/personality`, `/plan`, `/reasoning` | Update typed fields used by the next `turn/start`. |
| Visual Studio operations | `/ide-context`, `/init`, `/status` | Toggle bounded editor context, safely create `AGENTS.md`, or show local session state. |

The following commands remain hidden because the app-server or the current
single-thread UI cannot preserve their official semantics:
`/approve`, `/cloud`, `/cloud-environment`, `/local`, `/memories`, `/project`,
and `/side`. Direct input produces a local unsupported message.

`/review` supports uncommitted changes, a base branch, a commit, or custom
instructions. `/goal` supports show, set, edit, pause, resume, and clear.
Goal objectives contain between 1 and 4,000 characters.

`/plan` without arguments selects Plan mode for the next turn. With arguments,
it immediately starts a Plan-mode turn using the supplied prompt.

## Routing and queueing

`SlashCommandParser` separates normal prompts, escaped prompts, supported
commands, unsupported commands, and unknown commands. Unknown commands return
up to three candidates and never reach `turn/start` or `turn/steer`.

While a turn is active, `/status`, `/mcp`, and goal display execute immediately.
Other commands enter a per-thread FIFO queue with a limit of ten. Pending
setting commands with the same identity are replaced in place by their newest
value. Queue draining begins after the active turn completes and pauses as soon
as a queued command starts another turn.

Queues are memory-only. They are canceled on disconnect, Worker restart, or
confirmed thread removal. A slash command is never sent through `turn/steer`;
the existing steering behavior remains unchanged for normal prompts.

## Worker contract

Worker contract version 8 adds typed DTOs and RPC methods for compact, review,
fork, goals, MCP status, feedback, and rate limits. `StartTurnRequest` includes
reasoning effort, personality, service tier, collaboration mode, and bounded
IDE context. Model entries include supported reasoning efforts, the default
effort, personality support, and service tiers.

App-server error `-32601` disables only the affected command for the current
session. It does not degrade the entire connection. Non-idempotent command
operations are not retried.

Compaction, review mode, goal changes, and rate limits use dedicated typed
events. Their raw JSON payload is not forwarded to the transcript.

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

The composer uses an inline, height-bounded suggestion list rather than a
popup. Selecting a command creates a chip and leaves free-form arguments in a
separate text box, avoiding asynchronous `ComposerText` echo and caret reset.
Fixed arguments use themed option buttons.

Up and Down move selection, Enter or Tab accepts a suggestion, Escape closes
the list, and Ctrl+Enter executes. Enter remains a newline when suggestions
are closed. Bindable types use `DataContract` and `DataMember`; commands
implement the Remote UI `IAsyncCommand` contract.

All app-server text displayed in the UI passes through `SafeMarkdownService`.
Worker diagnostics continue to pass through secret redaction. Raw payload JSON
is not rendered.

## Validation

Core tests cover exact app-server method and parameter mappings, typed
notifications, timeout, cancellation, crashes, method-not-found capability
fallback, and non-retry behavior.

UI tests cover parsing, escaping, multiline arguments, aliases, unknown input,
input limits, candidate filtering, queue order and replacement, thread
separation, disconnect cancellation, command-versus-steer separation,
DataContract and IAsyncCommand requirements, XAML structure, keyboard
bindings, accessibility, and input preservation.
