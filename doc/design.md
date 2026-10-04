# design.md — アーキテクチャ設計決定記録

Session 1・2 での実装・修正作業から得た設計決定と教訓をまとめる。
次のセッション（Codex 等）へ引き継ぐための参照資料。

---

## 1. プロジェクト構成

| プロジェクト | TFM | 役割 |
|---|---|---|
| `Codex.VisualStudio.Extension` | net8.0-windows10.0.22621.0 | OOP 拡張本体（コマンド・ツールウィンドウ・ビジネスロジック） |
| `Codex.VisualStudio.Package` | net472 | in-proc プレースホルダ（将来の差分ビュー等 VSSDK 依存機能用） |
| `Codex.VisualStudio.Worker` | net8.0 | 将来の app-server 仲介役候補（現在は Extension 内 WorkerBridge が直接 spawn） |
| `Codex.VisualStudio.Contracts` | netstandard2.0 | Extension↔Worker 間 RPC 契約 |
| `Codex.AppServer.Protocol` | net8.0 | Codex app-server JSON-RPC 型定義 |
| `Codex.AppServer.Fake` | net8.0 | テスト用フェイク app-server |

---

## 2. OOP 拡張の設計決定

### 2.1 Microsoft.VisualStudio.Extensibility SDK を採用した理由

- Visual Studio 2022 の推奨拡張モデル。クラッシュ時に VS 本体を道連れにしない。
- `RemoteUserControl` / Remote UI でツールウィンドウを VS の WPF プロセスにレンダリングできる。
- `Command`、`ToolWindow` 等を `[VisualStudioContribution]` 属性で宣言的に登録できる。

### 2.2 ExtensionConfiguration.Metadata は必須

OOP モード（`RequiresInProcessHosting = false`）では `Metadata` が `null` だと
CEE0028（コンパイル時評価エラー）で失敗する。最低限の設定：

```csharp
public override ExtensionConfiguration ExtensionConfiguration => new()
{
    Metadata = new(
        id: "<publisher>.<name>.<guid>",      // 実際の値は CodexExtension.cs の ExtensionIdentity.Id を参照
        version: ExtensionAssemblyVersion,   // 基底クラスのプロパティ
        publisherName: "<publisher>",         // 実際の値は ExtensionIdentity.PublisherName を参照
        displayName: "Codex for Visual Studio",
        description: "AI coding assistant powered by OpenAI Codex."),
};
```

`RequiresInProcessHosting = true`（in-proc hosted 拡張）にした場合は逆に `Metadata = null` でなければならない。

### 2.3 アセンブリ名と SDK 基底クラス名の衝突

`RootNamespace = "Codex.VisualStudio.Extension"` のとき、
`Extension`（SDK の基底クラス `Microsoft.VisualStudio.Extensibility.Extension`）が
アセンブリと同じ名前空間で解決されず CS0118 になる。

**対処**: エイリアスを使う。

```csharp
using VSX = Microsoft.VisualStudio.Extensibility;

[VSX.VisualStudioContribution]
internal sealed class CodexExtension : VSX.Extension { ... }
```

### 2.4 CA1416（プラットフォーム互換性）の抑制方法

`Microsoft.VisualStudio.Extensibility` SDK の型は「Windows 8.0 以降」とマークされている。
OOP プロセスが `net8.0-windows10.0.22621.0` をターゲットにしているのに
`TreatWarningsAsErrors=true` のため CA1416 がエラーになる。

**対処**: Extension エントリポイントファイルにアセンブリ属性を一度だけ宣言する。

```csharp
// CodexExtension.cs
[assembly: SupportedOSPlatform("windows10.0.22621")]
```

これでプロジェクト全体が Windows 10 以降限定と宣言され、CA1416 が解消する。

### 2.5 コマンド表示名のローカライズ

SDK は `CommandConfiguration` のコンストラクタに生文字列リテラルを渡すと
CEE0027 でエラーにする。

**対処**: `%キー%` 形式 + `string-resources.json` を使う。

```csharp
// Commands/ShowCodexWindowCommand.cs
public override CommandConfiguration CommandConfiguration
    => new("%ShowCodexWindowCommand.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };
```

```json
// string-resources.json（プロジェクトルートに配置）
{
  "ShowCodexWindowCommand.DisplayName": "Codex"
}
```

### 2.6 XAML は EmbeddedResource として埋め込む

`UseWPF=true` を設定すると SDK は XAML ファイルを自動的に `<Page>`（BAML コンパイル）として扱う。
ただし `EnvironmentColors`（`Microsoft.VisualStudio.Shell.15.0` 由来）等 VS 固有型は
Extension プロジェクトで参照できないため、BAML コンパイル時に MC3050 が出る。

**対処**: BAML コンパイル対象から除外して生 XML として埋め込む。

```xml
<Page Remove="ToolWindows\ChatToolWindowContent.xaml" />
<EmbeddedResource Include="ToolWindows\ChatToolWindowContent.xaml">
  <LogicalName>Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml</LogicalName>
</EmbeddedResource>
```

`RemoteUserControl` の SDK がランタイムに VS の WPF プロセスで XAML をロードするため、
`EnvironmentColors` 等は正常に解決される。

**XAML のルート要素は `DataTemplate`**（`x:Class` なし、コードビハインドなし）。
`DataContext` は SDK が `RemoteUserControl` のコンストラクタ引数から自動バインドする。

### 2.7 sealed を付ける

`Extension`・`Command`・`ToolWindow` サブクラスは、外部から継承されないなら `sealed` にする。
`TreatWarningsAsErrors=true` のもとで CA1852 がエラーになるため。

---

## 3. ビルド設定の決定

### 3.1 experimental instance への自動デプロイ

```xml
<!-- Codex.VisualStudio.Extension.csproj -->
<DeployExtension Condition="!('$(BuildingInsideVisualStudio)' == 'true'
                              and '$(Configuration)' == 'Debug')">false</DeployExtension>
<VSSDKTargetPlatformRegRootSuffix>Exp</VSSDKTargetPlatformRegRootSuffix>
<StartArguments>/RootSuffix Exp /log "$(VisualStudioActivityLogPath)"</StartArguments>
```

- VS 内 Debug ビルド → `DeployExtension = true`（VSSDK 既定）→ Exp インスタンスへ自動配置。
- コマンドライン / CI / Release ビルド → `false` → 配置なし。
- `Directory.Build.props` に `DeployToExperimentalInstance` を書いてはいけない（全プロジェクトに漏れる）。

### 3.2 Central Package Management

`Directory.Packages.props` でバージョンを一元管理。各 `.csproj` では `Version=` を省略する。

```xml
<PackageVersion Include="Microsoft.VisualStudio.Extensibility" Version="17.14.2098" />
<PackageVersion Include="Microsoft.VisualStudio.Extensibility.Sdk" Version="17.14.40608" />
<PackageVersion Include="Microsoft.VisualStudio.Extensibility.Build" Version="17.14.40608" />
```

- `Sdk` と `Build` は `<PrivateAssets>all</PrivateAssets>` を付ける（ビルド専用ツール）。
- テストプロジェクトが Extension を ProjectReference で参照すると NU1603 が出る（安全な警告）→ `<NoWarn>` で抑制。
- `Microsoft.Extensions.DependencyInjection.Abstractions` のバージョン競合（MSB3277）は
  `<MSBuildWarningsAsMessages>` で抑制できる。

### 3.3 VSIX マニフェスト（source.extension.vsixmanifest）

- `InstallationTarget` に amd64・arm64 の両 `<ProductArchitecture>` を追加する。
- バージョン上限は `[17.9,)` に開放する（将来の VS をブロックしない）。
- Preview 段階は `<Preview>true</Preview>` を追加する。

### 3.4 Development and release version precedence

`Directory.Build.props` sets the default `VersionPrefix` to `0.2.1`, the next development
version after the published `v0.2.0` release. This keeps F5 deployments in the Experimental
Instance newer than the installed release with the same extension identity. Release builds
continue to pass an explicit `-p:Version` derived from the release tag; that command-line
property takes precedence over the development default and determines the packaged VSIX version.

---

## 4. ワーカーの埋め込み

Extension プロジェクトの MSBuild ターゲットで Worker を VSIX に含める。

```xml
<Target Name="BuildCodexWorker" BeforeTargets="GetVsixSourceItems">
  <MSBuild Projects="../Codex.VisualStudio.Worker/Codex.VisualStudio.Worker.csproj"
           Targets="Build" Properties="Configuration=$(Configuration)" />
  <ItemGroup>
    <VSIXSourceItem Include="../Codex.VisualStudio.Worker/bin/$(Configuration)/net8.0/**/*.*"
                    Exclude=".../**/*.pdb">
      <VSIXSubPath>Worker/%(RecursiveDir)</VSIXSubPath>
    </VSIXSourceItem>
  </ItemGroup>
</Target>
```

`WorkerBridge` starts the packaged `Worker/Codex.VisualStudio.Worker.dll` through the
`dotnet.exe` beside the Extension's active .NET runtime. If that host is unavailable,
it starts the packaged Worker apphost. The Extension and Worker communicate through a
named pipe and StreamJsonRpc. This avoids apphost runtime discovery in the Visual Studio
debug environment while retaining support for runtime layouts without a `dotnet.exe` host.

For local JSONL and remote WebSocket transports, the receive path resolves client responses
immediately. A bounded single-consumer queue delivers notifications in wire order and starts
server requests after earlier notifications complete. Notification handlers may then await a
new app-server request, such as `account/read` after `account/updated`, without blocking its
response on the receive path.
If any read, parse, notification, or write pump stops with an unexpected exception, the
connection closes with that exception: outstanding requests fail with a connection-closed error
and `Closed` triggers the normal reconnect path. A notification handler failure, including a
request timeout, is caught by the notification pump and reported as an error event, so later
notifications are still delivered. An `account/read` timeout reports the account as Unavailable
instead of leaving it at Checking.

---

## 5. テストプロジェクト構成

| プロジェクト | TFM | 備考 |
|---|---|---|
| `Codex.VisualStudio.Core.Tests` | net8.0 | プロトコル・ロジックのユニットテスト |
| `Codex.VisualStudio.Ui.Tests` | net8.0-windows10.0.22621.0 | ViewModel のユニットテスト |

UI テストは Extension への ProjectReference を持つため `UseWPF=true` が必要。
NU1603・MSB3277 を `<NoWarn>` / `<MSBuildWarningsAsMessages>` で抑制する。

---

## 6. 既知の問題・今後の検討事項

| 項目 | 状態 | 優先度 |
|---|---|---|
| `CommandPlacement.KnownPlacements.ToolsMenu` を使用中 → View メニューへ移動したい場合は `CommandGroupConfiguration` + `GroupPlacement.VsctParent(...)` で実装 | 暫定 | 低 |
| `WorkerBridge` が Extension 内にある → 将来 Worker プロセスに移動して責務を分離 | 暫定 | 中 |
| `ChatViewModel.OnUiAsync` の `Application.Current?.Dispatcher` → OOP プロセスでは null になるため直接実行（意図的） | 正常動作 | — |
| `Codex.VisualStudio.Package` は空プレースホルダ → 差分ビュー等が必要になったときに実装 | 予定 | 低 |
| DI コンテナへの `AppServerClient` / `CodexSessionService` 登録 → Phase 1 未完了 | 未着手 | 高 |

## 7. Approval mode picker

The Agent composer exposes typed approval options whose display text is separate from the
stable IDs `ask`, `auto`, `full`, `custom`, and `permission:<id>`. Remote UI synchronizes the
selection with `SelectedValuePath=Id`. Chat mode keeps its fixed read-only behavior and disables
the picker while exposing the reason through the adjacent mode control's accessibility help.

A saved permission profile is represented by a bounded loading placeholder until the capability-
gated catalog completes. Only a complete successful catalog may fall back to Custom when a profile
is missing; transient failures and truncated responses preserve the saved ID. Full access must be
confirmed because it disables the Codex sandbox and normal approval prompts. Moving from any turn
override to Custom starts a new thread because omitted overrides do not reset an existing thread.

## 8. Bounded command-output projection

Command output crosses two independently bounded stages. The Worker batches deltas and caps its
visible stream at 2 MiB, while the extension keeps a separate 2 MiB-character sanitized buffer that
is never serialized as a Remote UI member. `ChatItemViewModel.Text` is only the current projection:
the complete short output, a three-logical-line/4,096-character collapsed preview, or the buffered
full output after explicit expansion.

The projection counts CRLF as one break across delta boundaries and preserves empty logical lines.
After the preview boundary is reached, hidden deltas update only small summary properties unless the
preview itself changes. Truncated output uses non-exact buffered-output wording because the overflow
file can contain additional lines that the extension does not scan.

The Remote UI uses a standard WPF `Expander` with a TwoWay expanded-state binding, Visual Studio
dynamic theme resources, UI Automation name/help text, non-wrapping monospace text, and horizontal
scrolling. No custom or third-party control is required.

## 9. Reasoning effort and service-tier pickers (contract version 13)

The composer exposes model-aware Reasoning and Speed pickers after the model selector. Their first entry is `Default`, which omits the turn override and inherits Codex configuration. A concrete selection persists only its canonical catalog ID. When a selected model does not support that ID, the UI temporarily displays `Default` without overwriting the saved preference.

Hidden default models remain absent from the normal model catalog but are represented by `ListModelsResult.DefaultModelInfo`. This preserves reasoning and service-tier capabilities when the default model ID is injected into the picker. Every app-server name and description passes through `SafeMarkdownService` before it reaches Remote UI, while server ordering, canonical casing, and case-insensitive deduplication are retained.

Contract version 13 adds explicit presence flags for effort and service tier. The Worker can send an omitted property to inherit configuration, an explicit null to clear a sticky thread override, or a canonical value. Effective settings are tracked after start, resume, fork, turn start, and thread-settings updates.

`/reasoning` and `/fast` are thread-scoped one-turn overrides consumed only after `turn/start` succeeds. The following turn explicitly restores the persistent selection or captured effective value, including null. Normal and direct Plan turns share the same resolvers.

## 10. Usage presentation and freshness

The signed-in header exposes one Usage flyout backed by `UsagePresentation`. It converts the
app-server's used percentage into a clamped remaining percentage, recognizes the five-hour and
weekly windows, formats reset values as Unix seconds, and sanitizes bounded credit text before it
crosses Remote UI. Missing usage percentages and ambiguous multi-limit maps are not presented as
zero usage.

Usage freshness is scoped to a connection generation and a monotonic push version. The first
signed-in Ready state fetches once, and a Busy-to-Ready transition by itself does not fetch. Each
`TurnCompleted` event and each completed `context/compacted` event are explicit usage-consumption
boundaries: both force a rate-limit read even inside the 60-second TTL so the header and flyout
reflect the completed turn or compaction. Opening the flyout still refreshes only after the TTL.
Disconnect, sign-out, and disposal invalidate the snapshot; failed forced reads keep the last
successful snapshot eligible for a later retry. A turn that ends via a transport-level failure
reports `Degraded` rather than `TurnCompleted`, so no forced read is attempted there — the last
successful snapshot stays visible until reconnection restores `IsUsageAvailable`.
The Usage and History flyouts are mutually exclusive; the Usage popup cycles Tab focus after focus
enters its content, closes with Escape from either the host or popup, uses Visual Studio dynamic
theme resources, and exposes automation names and help text. Raw Remote UI cannot run VS-side
`Popup.Opened` code to transfer keyboard focus; guaranteed opening focus would require an in-process
WPF host.

## 11. Empty solution scaffolding

When the resolved workspace contains no solution or project, the default scaffold choice creates
only `ROOT/<Name>.slnx`. The generated solution is an empty SLNX document with UTF-8 BOM and CRLF
line endings. It deliberately omits `src`, project files, and source files so Codex can shape the
workspace without inheriting an arbitrary application template.

The operation remains non-destructive: an existing solution is never overwritten, and the separate
file-based app choice continues to create only a root-level `Program.cs` without a solution or
project. The generated empty document must remain parseable as XML and accepted by the pinned
`.NET` SDK's `dotnet sln` command.
## Unified slash menu and skill boundary (Issue #140)

The Extension uses one non-popup, virtualized ListBox for the eight built-in candidates and every
distinct skill identity safely accepted by the Worker. ADR-008's 200-skill input bound remains the
security limit, but there is no separate UI cap: empty and filtered queries can render all accepted
enabled and disabled rows. `IsTruncated` produces a passive Worker-truncation row and never a claim
that the catalog is complete. Skill selection is not `SlashCommands.ActiveCommand`: it creates one
`PendingSkill` chip while the normal composer remains visible. Accepting a live row resolves an
opaque selection key against the current `(Name, Scope, Path)` snapshot and clears only the slash
query.

Worker contract v18 preserves the force-reload and exact identity validation before
`turn/start`; only `{ type: "skill", name, path }` is serialized to App Server. Scope and raw
path never enter Remote UI-bound data. Remote skill paths remain server-owned identifiers and
are not probed on the Visual Studio host. Busy and approval-waiting states permit chip changes,
but pending skills disable send/steer until removal or successful start.

The live `skills/list` response is the catalog system of record. The Worker owns a 60-second
memory snapshot for the captured owner partition. `skills/changed` invalidates it, and sticky
`-32601` remains distinct from an empty catalog. Owner replacement clears every catalog and
rejects late refresh results. The v2 persistent store keys by workspace and owner partition,
with 200 skills, 4 MiB per partition, 24-hour hard expiry, 64 MiB total, LRU cleanup, atomic
replacement, and bounded cross-process locking. Default prompts, dependency values, icons,
raw JSON, and Remote UI selection IDs are excluded. The pinned account contract cannot prove
stable owner identity, so current local and remote sessions bypass disk reads and writes and
never reuse v1 workspace-only snapshots. A stale catalog cannot authorize a turn.

Metadata is untrusted display data: brand colors accept only normalized `#RRGGBB` and are applied
as a narrow accent that must fall back to Visual Studio theme resources under High Contrast,
default prompts are redacted/bounded and require an explicit empty-composer button, and
dependencies are plain-text badges with no execution or installation behavior. Metadata that has
no Remote UI surface is not carried across the contract, so `dependencies.tools` and the
`iconSmall` presence flag belong in the contract only once their surface exists. The icon spike is
gated; until a Remote UI image/cache containment proof exists, the presentation uses a fixed glyph
and exposes no raw icon path.

## 12. Connection target, profiles, and diagnosis

The detailed contract is defined in [Secure Remote App Server Connection](secure-remote-connection-design.md)
and its [Japanese translation](secure-remote-connection-design_ja.md). The Extension talks to its local
Worker; local stdio is the default and a saved enabled remote profile explicitly opts into WebSocket.

The toolbar reports the Worker-confirmed target and generation. `Target` identifies the intended target
while Disconnected/Connecting/Degraded; only Ready/Busy/WaitingForApproval is labeled `Connected`.
A local process ID is shown only for an owned child process. Remote state never claims a server PID.
The connection flyout retains Usage/History mutual exclusion, Escape, trapped Tab navigation, Visual
Studio dynamic theme resources, automation names, and a polite status live region.

The flyout follows the user's tasks:

1. Target and independent diagnosis: the selected saved enabled profile has `Check health` and separate
   `/healthz` and `/readyz` results. Labels identify the checked profile, observation time, and authority-root
   scope. A profile with a routing path shows `Route not verified`; a root response does not establish that
   the routed App Server, authentication, RPC, or any feature is available. The active target's actual RPC
   state is shown independently; an inactive checked target is `Not connected`.
2. Saved profile list with Add/Remove, Preview guidance, and explicit unsaved/disabled markers.
3. Selected profile editor: Name, Endpoint, Token file path, Local root, Server root, Enabled, then Save.
4. Validation/apply status, `Connect with this profile`, and `Use local app-server`.

Save validates only metadata through the endpoint-only `RemoteEndpointPolicy` in Contracts: endpoint
scheme/host/URI policy, both roots, unique name, and a nonempty syntactically valid local absolute token-file
path for an enabled profile. Save never reads the token or tests existence/readability. The Worker checks
existence, readability, strict UTF-8, length/size, and bearer format immediately before every handshake.
The UI carries only the file path. Unsaved/disabled profiles cannot apply or issue diagnostic requests;
this enforces explicit opt-in and prevents requests to unvalidated editor input. Health itself requires no
credential-file read or authentication header.
The Worker independently refuses a remote connection missing either root, and existing attachment
checks reject local paths outside the configured local root before `turn/start`.

Save, rename, disable, and delete change eligibility for future connections; they do not change the active
socket or silently fall back to local. The active target remains visible independently of edited/selected
configuration. An explicit reconnect reloads saved settings by the applied profile name and requires the
same canonical endpoint, token-file path, roots, enabled state, and metadata fingerprint as the applied
snapshot. Missing, disabled, changed, or unsaved matching profiles are refused with guidance to apply a
saved profile or choose local. Token-file content rotation is reflected by rereading the matching path.
All same-instance profile/settings mutations (including selection persistence, Save, rename, enable/disable,
delete, and explicit local switch) and reconnect validation/dispatch are serialized; the Worker additionally
checks the expected generation and immutable metadata under its own transition gate.

`Connect with this profile` explicitly applies saved metadata even while Ready, but is disabled during a
transition, active turn, or pending approval. Selecting a row does not apply it. `Use local app-server`
explicitly selects and connects the local process. Diagnostic progress cannot update Worker readiness,
start a remote socket, or replay a mutation. Selection, saved metadata changes, and generation retirement
cancel/clear diagnostic snapshots so late checks cannot overwrite another target.

On a remote close or two silent liveness timeouts, the Worker retires only that current generation and
publishes Degraded. The degraded remote action is `Reconnect remote app-server` through `worker/reconnect`;
`worker/restart` is rejected for remote targets with a typed operation-rejection error before any stop.
The local action is `Restart local app-server`. Connect/reconnect/close are serialized and pending
requests finish on retirement; stale close notifications cannot overwrite a newer Ready state.

Remote mode remains Preview until Issue #153 is complete and upstream WebSocket support is no longer
experimental. Issue #152 supplies account/principal state isolation as specified in section 14. Health diagnosis and the bounded, allowlisted overload retry
contract are available independently of that limitation. Automatic reconnect/history recovery remains
tracked by Issue #153; a failed liveness check does not resend user input or reconnect automatically.

## 13. Interrupt diagnostics

Stopping a turn sends `turn/interrupt`; the app-server may still finish in-flight output before it
sends `turn/completed`. The transcript does not distinguish an interrupted completion, so the
diagnostics log records the stop timeline instead: the Extension writes the click
(`Interrupt requested by user`), and the Worker writes the request, its acknowledgement with the
elapsed time, and, for the interrupted turn only, the final `turn.status` with the time from the
request to the completion. Pending stop timestamps are keyed by connection generation, thread, and
turn, and are cleared on reinitialization so a lost completion cannot accumulate state. The lines
contain only server-assigned thread/turn identifiers and timings.

## 14. Path mapping and connection state isolation

[Path mapping and connection state isolation](path-state-isolation-design.md) and its
[Japanese translation](path-state-isolation-design_ja.md) define the accepted Issue #152
boundary. Local/server path domains use a single component mapper and physical local-root
validation. Every cache, grant, selected conversation, draft, and asynchronous result belongs
to a captured owner partition and connection generation. The pinned account contract cannot
prove a stable account for every provider, so those owners use volatile partitions instead
of sharing workspace-only persisted skill state. Account/principal replacement retires the
previous remote socket before new-owner state is activated. A logout or sign-in started by the
owner then connects a new owner automatically; an unsolicited account change ends in Degraded.

## 15. Connection and history recovery

The [Issue #153 design](connection-history-recovery-design.md) and its
[Japanese translation](connection-history-recovery-design_ja.md) define connection
and history recovery. Worker contract v19 adds bounded history and attachment
metadata reads. An Extension-owned coordinator recovers transient failures with
five bounded attempts. It preserves the active owner boundary in section 14:
only an isolated, in-memory old-owner draft may survive retirement, and explicit
connection/conversation review is required before copying it into the composer.
Restoration and sending are separate actions.

History uses bounded read-only pages and generation-stamped notification
merging. Joining a conversation remains an explicit resume action. Attachment
notifications identify membership changes; bounded list reads supply metadata.
Operations with uncertain delivery are never replayed automatically.
