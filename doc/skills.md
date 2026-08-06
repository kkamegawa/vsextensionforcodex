# Codex Custom Skills Integration

## Scope

The Visual Studio extension surfaces the Codex skills catalog for the current workspace: a
read-only `/skills` transcript listing, inline `$<name>` mention suggestions in the composer, a
skills panel with per-skill scope and load-error detail, and an enable/disable toggle. The
implementation is tracked by GitHub Issue #119 and its eight sub-issues (#120-#127).

`$` is reserved for skill mentions only in this release. It is not shared with any other
mention convention (see ADR-011 in [doc/adr.md](adr.md)).

## Protocol methods

| Method | Purpose |
|---|---|
| `skills/list` | Discover skills for the session's working directory. Always called with `cwds: []`, which the app-server resolves to the single session working directory. |
| `skills/config/write` | Enable or disable one skill, selected by name or by path (mutually exclusive). |
| `skills/changed` | Server-pushed notification (empty `params: {}`) that the catalog changed on disk; the Worker treats it as a cache-invalidation signal, not a conversation event. |

`skills/list` responses are flattened from `SkillsListResponse.data` (an array of per-cwd
entries, each carrying its own `skills[]` and `errors[]`) into `ListSkillsResult { Skills, Errors,
IsTruncated }`. This is lossless for the `cwds: []` call shape, which always returns exactly one
entry; `SkillInfo.Cwd` and `SkillLoadError.Cwd` are still carried per item so a future multi-root
change would not require another contract shape change. See ADR-008 for the full rationale,
including why `interface.brandColor`, icon paths, `interface.defaultPrompt`, and
`dependencies.tools[]` are intentionally excluded from the contract.

Neither method is gated behind the `experimentalApi` option. Support is detected purely through
the app-server's `-32601` (method not found) response, matching the rest of this extension's
capability-probing convention (ADR-008).

## Worker caching and invalidation

`CodexSessionService` caches the last successful `skills/list` result and serves repeat calls
from that cache unless `forceReload` is set. The cache is cleared on:

- `InitializeAsync` (a new app-server process may expose an entirely different skill set)
- A `skills/changed` notification
- A successful `skills/config/write` call

See ADR-009 for why the cache lives in the Worker rather than the extension process.

## Composer mentions and turn input

Typing `$` followed by characters opens an inline suggestion list (mirroring the existing `#`
file-mention overlay) backed by the cached catalog; `$$` escapes a literal `$`. Accepting a
suggestion or typing a full `$<name>` and sending the turn resolves the token against the cached
catalog and, on a match, adds a `{ type: "skill", name, path }` turn input item separate from file
attachments (ADR-010). An unresolved token is left as plain text rather than causing an error.

Resolution and suggestion both refuse to run against a truncated catalog (the worker's
`MaxSkills`/`MaxSkillErrors` display cap was hit): a wrong scope-collision resolution or a
suggestion for an entry outside the visible window would have send-time consequences. Turn input
items are capped at five per turn and deduplicated by path. Name collisions across scopes resolve
deterministically: enabled skills win over disabled ones, then `repo` > `user` > `system` >
`admin`, then ordinal path comparison.

Skill paths bypass the path-access policy that governs file attachments and mentions: they come
from the app-server's own `skills/list` response, not from user-selected files, and `scope: user`
paths are directories (which fail a `File.Exists` check) while `scope: system`/`admin` paths
routinely live outside the workspace. See ADR-010 for the full trust-boundary argument.

## Skills panel

A toolbar toggle opens a skills panel (mutually exclusive with the history and usage flyouts)
listing every skill's display name, scope, short description, and enabled state, plus any
per-skill load errors reported alongside the catalog. The panel is deliberately more lenient than
the permission-profile picker: a truncated catalog is still rendered (with a note), because
picking from an incomplete list is a security decision for permission profiles but the skills
panel is purely informational (ADR-012). Disabled skills stay visible with a "(disabled)" suffix
rather than being hidden.

Skill entries and load errors are merged into their Remote UI collections in place (`Insert`/`Move`,
never `Clear`+`Add`) so the panel does not momentarily invalidate list state on every refresh.

## Enable and disable

Each skill row has a toggle button, disabled outright for `scope: system`/`admin` skills (managed
by organization policy). Clicking it calls `skills/config/write` with no confirmation dialog —
unlike `/feedback`, this is a local, instantly reversible per-item setting with no external upload
(ADR-013). The UI never applies an optimistic update: the row's enabled state is set only from the
server's `effectiveEnabled` response, which can differ from what was requested if an
organization policy overrides it.

## Validation

Core tests cover catalog flattening, truncation, secret redaction of skill and error text, cache
hits/invalidation, capability-probe fallback, `skills/config/write` selector validation
(name XOR path), and `effectiveEnabled` passthrough.

UI tests cover the `/skills` transcript listing, mention parsing and resolution (including the
truncated-catalog guard and turn-input cap/dedupe), composer suggestion overlay behavior (open,
navigate, accept, close on `skills/changed`), panel mutual exclusion with history/usage, in-place
merge, and the toggle's reconciliation to `effectiveEnabled`.
