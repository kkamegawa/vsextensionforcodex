# Windows Sandbox panel in transcript scrolling

[Japanese work record](task-windows-sandbox-panel-scroll_ja.md) · Approved [English design](daily-use-app-server-design.md) / [Japanese design](daily-use-app-server-design_ja.md).

Related issue: [#200](https://github.com/kkamegawa/vsextensionforcodex/issues/200), closed. The approved implementation plan is published on the [English Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/windows-sandbox-panel-scroll) and [Japanese Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/windows-sandbox-panel-scroll_ja).

## Implementation

- Moved the Windows Sandbox panel into a synthetic leading row in the virtualized transcript. The panel now shares the conversation's vertical scrolling surface.
- Kept `Items` as the canonical conversation/history collection and added the `TranscriptItems` UI projection. The synthetic panel row is excluded from history, history byte accounting, and persisted `Items` state.
- Kept the synthetic row stable while mirroring Add, Remove, Replace, and Move notifications. Reset or inconsistent range notifications rebuild only the changed projection segment, preserving shared rows.
- Bound the synthetic row to a typed `DataContract` presentation object instead of reaching the root view model through an ancestor. It forwards sandbox state, commands, welcome state, and property-change notifications from the owning view model.
- Used separate templates for ordinary virtualized messages and the sandbox panel so message rows do not instantiate sandbox controls. The synthetic row is non-focusable and is omitted from non-empty transcripts while the panel is hidden.
- Moved the empty-thread welcome content into that same row so it cannot overlay sandbox controls. Kept recycling virtualization, pixel scrolling, fixed app-server notices, and shell-command confirmation in their existing regions.
- Left sandbox preference persistence, backend selection, setup commands, and next-local-server-start behavior unchanged.

## Verification

| Check | Result |
|---|---|
| Focused transcript and projection checks | 9 passed, 0 skipped, 0 failed |
| Release UI suite | 654 passed, 3 skipped, 0 failed (657 total) |
| Debug solution build | 0 warnings, 0 errors |
| Release solution build | 0 warnings, 0 errors |
| XAML and presentation contract tests | Passed as part of the focused and full UI suites |
| Claude final diff review | No actionable findings |
| Encoding and XAML checks | UTF-8 BOM and CRLF retained; XAML XML parse and `git diff --check` passed |
| Visual Studio Experimental Instance screenshot | Accepted by the user on 2026-11-26. The supplied screenshot shows an active conversation after processing started, with the Windows Sandbox panel no longer in the visible transcript area. Earlier launch attempts failed before content display with `IToolWindowProvider`/ServiceHub errors; the successful screenshot completed visual acceptance. |

The user confirmed the accepted behavior: once processing starts, the sandbox panel disappears from the current viewport.
