---
name: remote-ui-regression-checks
description: 'Checks and fixes for regressions that pass unit tests but break the running Visual Studio Remote UI tool window in this repository. Use when a button stays disabled (or enabled) although its view-model condition changed, when text in a templated control is unreadable only on hover/pressed/selected, when a flyout or ViewModel command is added or edited, when a build passes locally but fails on CI with MSB3371 or a missing obj path, and after any scripted (sed/python) edit of C# or XAML. Also covers how to reproduce these without the IDE.'
---

# Remote UI Regression Checks

Each item below is a defect that shipped past a green local build and full unit-test run, and was
found only in the Experimental Instance or on CI. Apply the matching check whenever you touch that
area, and add the listed test so the next regression is caught without the IDE.

## 1. Commands must raise CanExecute; Remote UI never polls it

**Symptom.** A button is shown (its visibility binding updated) but stays disabled, or the reverse.
Example: the Interrupt button was visible during a turn but could not be pressed.

**Cause.** Remote UI serializes `AsyncCommand.CanExecute` and refreshes it only when the command
raises `PropertyChanged("CanExecute")` through `RaiseCanExecuteChanged()`. Reading `CanExecute` in a
test returns the live value, so a test that only asserts `CanExecute` passes even when the
notification is missing. The Interrupt and Account raises had been dropped from
`ChatViewModel.RaiseCommandStates()` by a scripted edit.

**Check.**
- Every command whose predicate reads `Status`, account, selection, or flags appears in
  `RaiseCommandStates()` (or the equivalent change handler). Compare the method with the last known
  good commit: `git show <good>:src/Codex.VisualStudio.Extension/ChatViewModel.cs | grep -A20 "void RaiseCommandStates"`.
- Test the **notification**, not just the value: subscribe to `command.PropertyChanged`, publish the
  state change through the fake bridge, and assert the event count. See
  `ChatViewModel_InterruptCommand_FollowsActiveTurnInEveryTurnState` in `ViewModelTests.cs`.

## 2. Inline content inherits from its logical parent, not from the template

**Symptom.** Text inside a templated control is unreadable only in some interaction states (white
label on the light hover background), while a glyph in the same template is fine.

**Cause.** An element written inline (`<Expander.Header><TextBlock .../></Expander.Header>`) has the
control itself as its logical parent and inherits `Foreground` from it. Trigger setters on the
template's inner `ToggleButton` never reach it; a `Path` inside the template (bound with
`TemplateBinding Foreground`) does follow the state, which hides the problem.

**Fix.** Pass plain data (`Header="{Binding Label}"`) and render it with a `HeaderTemplate` /
`ContentTemplate`, so the element is created under the template's `ContentPresenter` and inherits the
state foreground. Pin it with an XAML test that asserts no inline header element exists and the
template's `TextBlock` has no explicit `Foreground` (see `TranscriptPresentationTests`).

**Review rule.** When a template changes `Foreground` in a trigger, search its usages for inline
`*.Header>` or content elements.

## 3. Bind nested Remote UI data by path; avoid RelativeSource across a Popup

Popup content lives in a separate visual tree. Bind flyout editors by full path from the root data
context (`RemoteProfiles.SelectedProfile.Endpoint`) instead of switching `DataContext` and reaching
back with `RelativeSource AncestorType=Popup`. Every bound path must resolve segment by segment
through `[DataMember]` properties of `[DataContract]` types; the XAML binding tests check the first
segment for all bindings and full paths for the remote profile editor. Add new flyout view-model
types to `RemoteUiContextTypes`.

## 4. MSBuild targets that write under obj must create the directory

**Symptom.** Local builds pass; CI fails with `MSB3371 ... cannot be created. Could not find a part
of the path ...\obj\Release\net8.0\...`.

**Cause.** A target running `BeforeTargets="BeforeBuild"` wrote a stamp into
`$(IntermediateOutputPath)` before the SDK created it on a clean checkout. Local `obj` already
existed.

**Fix and reproduction.** Add `<MakeDir Directories="$(IntermediateOutputPath)" />` before
`Touch`/`WriteLinesToFile`. Reproduce a clean checkout without deleting anything:

```powershell
dotnet build src/Codex.AppServer.Protocol -c Release "-p:IntermediateOutputPath=obj/CleanRepro/" "-p:OutputPath=bin/CleanRepro/"
```

## 5. Scripted edits: prove what was removed

The Interrupt regression came from a scripted multi-line replacement whose "old" and "new" strings
were edited separately to disambiguate a duplicate match, silently dropping two lines.

- Prefer the Edit tool for exact multi-line replacements; use scripts only for mechanical,
  single-token changes.
- After any scripted edit, review removed lines only:
  `git diff <file> | grep '^-' | grep -v '^---'`. Every removed line must be intended.
- Assert uniqueness in the script (`assert text.count(old) == 1`) and never rewrite the pattern
  and the replacement independently.

## 6. Line endings and encoding

The repository requires UTF-8 with BOM and CRLF in the working tree, but some files are LF in both
`HEAD` and the working tree (for example `ChatViewModel.cs`, the scripts, `app-server-contract.json`).
Keep each file's existing style to avoid whole-file diffs:

- Compare `git show HEAD:<file> | tr -cd '\r' | wc -c` with the working file before committing.
- Heredoc appends (`cat >> file`) add LF lines to CRLF files; normalize mixed files to the file's
  dominant ending. The Edit tool can convert a whole LF file to CRLF; convert it back if `HEAD` was LF.

## 7. Verify in the Experimental Instance, with evidence

Unit and XAML tests cannot show theme colors, focus, or Remote UI refresh. For any UI-affecting
change, ask the user for screenshots of the exact states changed (hover, pressed, focused, disabled;
Light and Dark) and judge pass/fail from them. For behavior that is hard to see (a Stop press), add
a diagnostics log line with timings and check the log instead (see design.md section 13).
