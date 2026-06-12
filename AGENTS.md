# AGENTS.md

## Project

- Publisher: `kazushikamegawa` / VSIX ID: `Kkamegawa.CodexForVisualStudio`
- Source code, comments, and identifiers in **English**
- Encoding: **UTF-8 with BOM**, line endings: **CRLF**
- `TreatWarningsAsErrors=true` — every warning is a blocker

## When working on the Visual Studio extension

Use the **`visual-studio-extension`** sub-agent defined in
`.Codex/agents/visual-studio-extension.md`.

It contains project-specific rules and a complete checklist of known build errors
(CEE0028, CS0118, CA1416, double-dispose, XAML embedding, etc.).
Consult it before writing or modifying any code under `src/` or `tests/`.

## When working on WPF / XAML / MVVM code

Use the **`wpf`** sub-agent defined in `.Codex/agents/wpf.md` — but **only when the
project is confirmed to be WPF**, since MAUI, WinUI 3, and UWP also use XAML.
WPF evidence: `<UseWPF>true</UseWPF>` in the `.csproj`, WPF assembly references
(`PresentationFramework` etc.), or Visual Studio Remote UI `DataTemplate` XAML
(this repository's tool windows qualify). Do not use it for MAUI (`UseMaui`),
WinUI 3 (`UseWinUI` / `Microsoft.WindowsAppSDK`), or UWP projects.

It covers data binding, MVVM (CommunityToolkit.Mvvm), Dispatcher/threading,
virtualization, freezables, resources, and accessibility — all grounded in
Microsoft Learn guidance. For anything VSIX/SDK-specific (theming, packaging,
SDK build errors), defer to the `visual-studio-extension` agent above.

## NuGet Packages

Before adding or updating any NuGet package:
1. Run the **`nuget-validate`** skill to check for known vulnerabilities and deprecated status.
2. Only add packages that pass validation (no vulnerabilities, not deprecated).
3. Pin the version in `Directory.Packages.props` (Central Package Management is enabled).

## Security (always apply)

- Treat all `codex app-server` output as untrusted input.
- Pass dynamic strings through `SafeMarkdownService` before display.
- Pass log text through `ISecretRedactor` before writing.
- Route all destructive operations through `IApprovalPolicyEngine`.

## Docs

`doc/design.md` · `doc/plan.md` · `doc/task.md` · `doc/implementation.md`
