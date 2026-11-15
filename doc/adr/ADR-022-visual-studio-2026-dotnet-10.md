# ADR-022: Support Visual Studio 2026 18.12+ and .NET 10 only

[日本語](ADR-022-visual-studio-2026-dotnet-10_ja.md)

- Date: 2026-11-16
- Status: Accepted
- Tracking: [Issue #268](https://github.com/kkamegawa/vsextensionforcodex/issues/268) ([#273](https://github.com/kkamegawa/vsextensionforcodex/issues/273), [#274](https://github.com/kkamegawa/vsextensionforcodex/issues/274)); implementation [Issue #269](https://github.com/kkamegawa/vsextensionforcodex/issues/269) and [Issue #271](https://github.com/kkamegawa/vsextensionforcodex/issues/271)

## Context

The extension supported Visual Studio 2022 17.14 and later on .NET 8. Visual Studio 2026 runs out-of-process VisualStudio.Extensibility extensions on .NET 10. Supporting both releases keeps every project on .NET 8, keeps `Codex.VisualStudio.Contracts` on netstandard2.0, and prevents the codebase from using .NET 10 and C# 14 features. The net472 project `Codex.VisualStudio.Package` is an empty placeholder that no feature uses.

## Decision

- **Visual Studio:** the only supported release is Visual Studio 2026, version 18.12 or later, on amd64 and arm64. Visual Studio 2022 is not supported.
- **Runtime:** every project targets .NET 10. The Extension and the UI tests target `net10.0-windows10.0.22621.0`; the Worker, Protocol, Contracts, Fake app-server, PoC, and Core tests target `net10.0`. .NET 8 is not supported.
- **Placeholder:** `Codex.VisualStudio.Package` is removed. No in-process component exists.
- **Contracts:** `Codex.VisualStudio.Contracts` moves from netstandard2.0 to `net10.0`.
- **Project format:** projects keep `Microsoft.NET.Sdk` with explicit package references. The Visual Studio SDK packages move to their 18.12 versions. The VSIX installation target and .NET target follow from those packages and the target framework, and are set explicitly only if the generated manifest does not contain `[18.12,)` and `net10.0`.
- **Worker host:** the Worker stays framework-dependent and starts with the `dotnet.exe` that hosts the Extension's runtime. It keeps the default `RollForward` policy, `Minor`, so it runs on the newest .NET 10 patch and never on another major version.
- **Language:** the codebase adopts .NET 10 and C# 14 features where they apply.

## Alternatives

- **Support both Visual Studio 2022 and 2026.** Rejected. It keeps .NET 8 as the common runtime, blocks .NET 10 and C# 14, and doubles the acceptance matrix.
- **Keep the net472 placeholder for a future in-process feature.** Rejected. It adds a .NET Framework build to every build and has no user. A future in-process feature is a new decision.
- **Self-contained Worker.** Rejected. It adds the full runtime to the VSIX, while Visual Studio already provides the matching runtime.
- **`RollForward=Major` for the Worker.** Rejected. Running on an untested major version would hide incompatibilities; the Extension must be retargeted for a new major version anyway.

## Consequences

- Users of Visual Studio 2022 cannot install new releases.
- Contracts are no longer consumable from .NET Framework or netstandard callers.
- CI, scripts, VSIX content checks, and documents use .NET 10 paths and no longer pin packages for .NET 8 compatibility.
- The design is in [design.md](../design.md) and [plan.md](../plan.md); the protocol work on this platform is in [app-server-0-169-design.md](../app-server-0-169-design.md).

## Decision history

- 2026-11-16: Accepted. The maintainer approved the platform change in the implementation plan of 2026-11-15.
