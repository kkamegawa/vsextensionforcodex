# Codex App Server 0.163.0 Support Design

Tracking: Issue #217 and child Issues #218–#223. Approved on 2026-10-23.

## Purpose

Update the Visual Studio extension to the Codex App Server 0.163.0 contract. Add durable thread read-state controls, preserve conflict details safely across both transports, correct MXC diagnosis, and allow users to disable MXC for local App Server startup.

The extension remains compatible with remote servers at 0.162.1 and 0.159.1. Experimental read-state behavior is available only when the connection negotiated the experimental API. The default ExperimentalApiEnabled=false remains unchanged.

## Contract and error handling

app-server-contract.json uses schema version 2, target version 0.163.0, and regression versions 0.162.1 and 0.159.1. It pins release metadata and asset hashes for all three versions, lists the complete stable and experimental method sets used by each version, and records target differences separately for each regression version.

Schema generation, comparison, cache validation, contract-surface checks, and release verification operate on each version and each surface. Versioned schemas are generated from the official release executables. Unversioned root schemas are not assigned a release version. Contract-manifest versioning is independent of schema-cache and verification-evidence format versions.

Stable connections do not require read-state fields or methods. Generated schemas verify that experimental fields remain experimental.

JsonRpcRemoteException.ErrorData stores an optional cloned JSON value, separate from Exception.Data. The value is retained only up to 16 KiB; oversized values are discarded. Both stdio and WebSocket responses preserve it. Raw error data is not forwarded to logs, UI, or the Worker contract.

A read-state conflict is recognized only when the remote error code is -32600 and error.data.reason is readStateConflict. Generic invalid requests remain ordinary errors. Embedded conflict state passes the same validation as a regular state; absent or invalid state is refreshed with thread/read.

## Read-state model and interaction

Worker contract v25 carries owner-scoped update requests, typed read-state results, and generation-stamped change notifications. The internal state categories are:

| State | Meaning and presentation |
| --- | --- |
| Read | A valid state with firstUnread=null; show no unread marker. |
| Unread at thread start | A valid state whose first unread position is threadStart; show the unread marker. |
| Unread at turn | A valid state whose first unread position identifies a turn; show the unread marker. |
| Not applicable | The list supports read states, but has no entry for this thread; show no marker. |
| Unavailable | The connection cannot use the feature, or the complete state map/value is missing or null; show no marker. |
| Invalid | The value cannot be validated; disable that row's action and explain the invalid state when selected. |

A ThreadRowViewModel implements ObservableObject and is projected from the Worker DTO. The row list mirrors the loaded list order position by position; a selected conversation that is no longer on a loaded page keeps its row state but is not shown as an extra row. Row updates are matched by owner and thread ID. List replacement preserves selection by thread ID. Selecting a conversation keeps its last known read state until a newer server response replaces it. A read-state update refreshes only the matching row and does not reload its history.

The history row displays the unread marker. The selected conversation provides explicit Mark read and Mark unread actions without joining the conversation. Stable connections hide the feature and show one explanation in the history region. Experimental connections keep actions disabled until a valid state and revision are available.

The user must act on the revision actually observed. Updates for the same conversation are serialized; the UI changes only after a successful response. Conflicts apply the validated latest state and ask the user to retry. Requests are never automatically resent after conflict, timeout, or disconnect. A timeout or disconnect is reported as an unconfirmed outcome; the client reads the current receipt once instead of resending. Revision values are opaque and are never ordered. A notification whose receipt equals the displayed one, including the notification for the client's own committed update, is not a concurrent change. If a response and a different notification race, refresh the state and disable actions during reconciliation. The read-state controls are republished after every in-flight change, so they reappear as soon as an update finishes.

Owner, connection generation, request generation, and selected-thread generation are checked before results are applied. This prevents stale results during A→B→A selection or owner changes. Starting, resuming, or forking a conversation reads its state with thread/read. Opening the history list and an explicit refresh use thread/list. No continuous subscription or polling is added.

A visible conversation is never marked read automatically. A new completed result may make that conversation unread; the marker remains until the user explicitly changes it.

## History-list loading

Initial load, refresh, and load-more share owner and request-generation checks. Results must match the current owner. Duplicate requests are suppressed, and empty completion, end-of-list, cancellation, and failure remain distinct outcomes.

A refresh replaces the list only after successful completion. Failure preserves the existing rows, selection, and cursor. Additional pages deduplicate by thread ID. Missing conversations are cancelled only after a complete list succeeds for the same owner; partial results never imply deletion.

## MXC selection and diagnosis

allowedWindowsSandboxImplementations describes legacy sandbox backends and is not evidence that MXC is allowed or denied. The readiness panel must not infer MXC policy from that list.

The local sandbox selector adds Do not use MXC, which passes -c windows.allow_mxc=false to the local CLI. It never changes a remote server. The existing initialize response user agent supplies the CLI version; no second --version process is started. Before sending initialized, a local opt-out requires a known version of at least 0.163.0. Unknown or older versions terminate local startup while leaving settings accessible.

config/read exposes windows.allow_mxc as an optional diagnostic value. A missing value is unknown. The extension does not infer windows.require_mxc, because the App Server configuration API does not expose it. Explicit sandbox=mxc conflicts and managed-policy failures are described using the CLI's sanitized error. Startup failure does not claim which policy rejected MXC, and readiness does not claim which backend an individual command used.

## Validation boundary

The Fake App Server covers conflict payloads, update notifications, read-state variants, the 0.162.1 regression surface, and existing MCP serverRequest/resolved behavior. Client receipt of the core-side MCP question-discard event is not an acceptance condition; when unavailable in a real CLI, the existing five-minute timeout and disconnect recovery are recorded.

Validation also covers steer/turn-completion races and cold-resume permission restoration. Core, UI, and Fake tests plus Debug and Release builds must pass with zero warnings. The Visual Studio Experimental Instance must be inspected at each theme, a narrow width, keyboard-only navigation, and screen-reader presentation, with screenshots used for acceptance.