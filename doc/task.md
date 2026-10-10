# task.md — 実装タスク（フェーズ別チェックリスト）

`plan.md` のフェーズ分割に対応する詳細タスク。各タスクは独立してレビュー可能な小さなスライスを意図する。

## 2026-10-08: Daily-use App Server features (Issue #155)

Tracking: [Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155), under [Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149). Approved [design](daily-use-app-server-design.md) and [plan](daily-use-app-server-plan.md); [Japanese work record](task-issue155_ja.md).

- [x] Read the approved Wiki and reconcile ADR-016, design section 17, plan section 13, Phase 6, and slash-command specifications.
- [x] P0: Worker contract v21, pinned 0.159.1 methods/schema classifications, 0.155.1 regression fixtures, typed contracts and saved-payload registry.
- [x] P1: bounded plan delta/final reconciliation, notices, effective-model modality admission, and additive catalog/status fields.
- [x] P2: typed live/history results, bounded PNG/JPEG previews, and owner-scoped mapped file actions.
- [x] P3: explicit saved attachment add/remove, known payload actions, provenance validation, and uncertain outcomes without mutation replay.
- [x] P4: exact /shell parsing, explicit target/command confirmation, local policy, acknowledgement state, and per-thread pending lock.
- [x] P5: local Windows sandbox readiness/setup, confirmed mode/solution root, completion ordering, and one attempt per connection generation.
- [x] P6 automated portion: integration review, Debug/Release builds, full Core/UI suites, schema checks, package hashes, and actual off-screen WPF preview rendering.
- [ ] P6 visual portion: Experimental Instance screenshots and Light/Dark/High Contrast, narrow-width, keyboard/focus, and accessibility acceptance.

Final automated results: Core Debug/Release each 399 passed, 0 failed, 5 skipped (404 total); UI Debug/Release each 370 passed, 0 failed, 1 skipped (371 total). Both full solution builds report 0 warnings and 0 errors. Debug/Release VSIX DLL hashes match their build outputs, raw embedded XAML matches source, and both packages carry contract v21. See [implementation evidence](implementation.md#issue-155-daily-use-app-server-features).

The WPF preview fragment was parsed and rendered with XamlReader/RenderTargetBitmap; its output image was inspected. This is separate from Experimental Instance acceptance. Visual Studio 2026 Enterprise 18.10.3 is installed, but native CUA APIs are disabled and no Experimental screenshots were obtained. Issue #155 stays open for this evidence; Issue #156 remains the release gate.

## 2026-10-04: Issue #153 recovery design reconciliation ([Issue #153](https://github.com/kkamegawa/vsextensionforcodex/issues/153))

Approved plan: [English design](connection-history-recovery-design.md) / [Japanese design](connection-history-recovery-design_ja.md). Publication: [English Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/connection-history-recovery) / [Japanese Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/connection-history-recovery_ja).

- [x] Compare implementation baseline `517d991`, Issue #153, the pinned 0.159.1 contract, and completed dependencies #150/#151/#152; record the accepted scope without changing runtime behavior.
- [x] Define transient-only, single-flight recovery with five attempts; quarantine the previous owner's draft until explicit review and restoration; separate read-only history from explicit resume.
- [x] Define bounded history/notification reconciliation, complete attachment pagination and identity-only notifications, and uncertain operations with zero automatic replay. Keep rich attachment operations in #155.
- [x] Prepare the English/Japanese detailed design, existing design/ADR amendments, Issue body, and bilingual Wiki/Home changes under the user-approved plan.
- [x] Publish and read back the corrected Issue and bilingual Wiki (Wiki commit `ca79534`). Verify the Issue body matches, all 10 published documents match committed bytes, all 34 public Home Wiki links resolve, and routes/dates, relative document links, UTF-8 BOM/CRLF, and `git diff --check` pass.
- [x] Implement Worker contract v19, bounded transient recovery, isolated draft review/restore/discard, explicit history selection and Join, paged history/attachment metadata, generation-aware notification merging, and unknown-outcome tracking with zero automatic replay.
- [x] Verify Release Core tests (343 passed / 5 skipped) and UI tests (335 passed / 1 skipped); Debug and Release solution builds (zero warnings/errors); the 0.159.1 and 0.155.1 stable schema-cache, method-surface, and schema-difference contracts; and Release VSIX DLL hashes, embedded XAML, identity, publisher, and Preview flag.
- [x] Address [PR #164](https://github.com/kkamegawa/vsextensionforcodex/pull/164) review findings: retire a dead Worker transport before an explicit Connect, skip per-turn history items from another turn, keep a cancelled local connect attempt as cancellation so the coordinator retries it, merge attachment membership by type and identity key, coalesce attachment refreshes into one single-flight scan, and correct the ADR-014 status and design text. Release build has zero warnings; Core 350/350 and UI 338/338 pass.
- [ ] Verify recovery, history, draft, and attachment presentation in Experimental Instance screenshots. Visual evidence remains pending; Visual Studio 2026 Enterprise 18.10.3 is installed in the current environment.

## 2026-10-04: Issue #152 reconciliation and follow-up ([Issue #152](https://github.com/kkamegawa/vsextensionforcodex/issues/152))

Plan: [English Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/path-state-isolation) / [Japanese Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/path-state-isolation_ja). Evidence: [verification record](path-state-isolation-verification.md).

- [x] Compare Issue #152, Wiki Phase 3, the path/state design, ADR-013, and the code. Check the items PR #162 delivered, delegate changed-file links/artifacts/open-reveal to Issue #155 and stored attachments/history/drafts to Issue #153, and amend the Wiki, Issue, and Preview condition.
- [x] Record the account-change lifecycle in the design and ADR-013: the owner's own logout or matching sign-in completion connects a new owner automatically; an unsolicited account change stays Degraded.
- [x] Implement the automatic new-owner connection, whole-turn rejection of missing/unreadable/protected attachments, mapped thread working directory, and the Worker rate-limit emission filter.
- [x] Add remote-mode integration tests (working directory, permission profile, `localImage`, IDE context, unmappable approval path, thread working directory), an automated junction test, and account-change lifecycle tests (Worker and UI).
- [x] Fix the Experimental Instance degradation: CLI 0.159.1 sends `account/updated` after startup without an account change. Account notifications now reread the account and retire the owner only when the Worker-only account fingerprint changes; a notification processed during the owner's own logout counts as that logout (Codex review finding).
- [x] Verify Debug/Release builds (zero warnings), Release Core 331 passed / 5 skipped and UI 318 passed / 1 skipped, CLI 0.159.1 schema-cache and live initialize, and `git diff --check`.
- [x] Experimental Instance acceptance by the maintainer: startup reaches Ready signed in, and Sign out reaches a new Ready session with Sign in available (screenshots). Remote profile switching, attachment rejection, and remote skill selection rely on the automated tests.

## 2026-10-03: Path mapping and owner state isolation ([Issue #152](https://github.com/kkamegawa/vsextensionforcodex/issues/152))

Evidence: [English verification record](path-state-isolation-verification.md) / [Japanese verification record](path-state-isolation-verification_ja.md).

- [x] Re-review the accepted Wiki Phase 3 and ADR-013 against the merged Issue #151 implementation; record the final owner/cache contract in the paired path/state design.
- [x] Add separate local/server path domains, component-based Windows/POSIX mapping, safe Windows aliases, and physical local link containment; use them for cwd, IDE context, attachments, and inbound approval paths.
- [x] Keep remote skill paths as bounded exact server identifiers and reject unmappable explicit attachments before `turn/start`.
- [x] Add volatile Worker/attempt owner partitions, context-owned grants and streaming buffers, cache invalidation, contract v18 owner-scoped mutations and notifications, and serialized owner validation/send.
- [x] Reset selected UI state at an owner boundary and reject stale model, skill, usage, history, input, authentication, send, and suggestion completions.
- [x] Disable disk-cache reads and writes when authoritative account continuity cannot be established; retain the bounded v2 composite owner/workspace file format for future admitted owners.
- [x] Verify Debug and Release solution builds (zero warnings/errors), full Release Core (311 passed / 5 symlink-capability skips) and UI (317 passed / 1 symlink-capability skip), pinned 0.159.1 stable/experimental contract surfaces, schema-cache contract, live initialize smoke, and `git diff --check`.
- [x] Verify actual Windows junctions: accept existing and future files through an in-root junction; reject both through an escaping junction. Inspect the Release VSIX: contract v18, both Contracts copies and Worker/Extension DLL hashes match their Release outputs, raw embedded XAML matches source, and publisher/identity/Preview remain correct.
- [ ] Observe Experimental Instance connection switches, attachment rejection, and remote skill selection in screenshots. Visual Studio discovery returned no installed instance; `orca` was not recognized, so the computer-use path cannot run.

## 2026-09-13: Codex App Server update and remote connection (approved plan)

- [x] Phase 1 ([Issue #150](https://github.com/kkamegawa/vsextensionforcodex/issues/150)): target the CLI 0.155.1 contract, compare 0.154.0 and 0.155.1 stable/experimental schemas, update schema cache metadata, exact request routing, capability probes, and connection-generation state. Verified with four official schema generations, cache/contract checks, 72 focused tests, 130 full Core tests, a zero-warning Release solution build, and a live 0.155.1 initialize/thread/turn round trip.
- [ ] Phase 2: add explicitly enabled secure WebSocket transport for already running remote servers, authentication, health distinction, and bounded reconnect.
- [ ] Phase 3: add component-wise local/server root mapping and connection/account/authentication-principal/workspace state partitioning, including invalidation of the previous owner's session, WebSocket state, model catalog, caches, and late events.
- [ ] Phase 4: retain in-memory drafts, reconnect and page history plus stored attachments, merge notifications, and prevent uncertain message/approval/attachment mutation replay.
- [ ] Phase 5: raise the Worker contract for questions, permissions, native user verification, MCP forms/authentication and OAuth revocation recovery with safe secret/proof handling.
- [ ] Phase 6: add plan/status/artifact and stored attachment rendering, model catalog capabilities, explicit `thread/shellCommand`, and local sandbox setup status.
- [ ] Phase 7: run 0.154.0-to-0.155.1 contract-diff, race, auth-owner switch, transport, path, stored attachment, verification/MCP reauthentication, UI accessibility, build, VSIX, and Experimental Instance validation.
- [x] Tracking: parent Issue and seven linked child Issues; English/Japanese Wiki plan and Home indexes. The detailed Issue #150 plan was pushed to the bilingual Wiki on 2026-09-22.

## 2026-09-23: PR #157 review fixes ([PR #157](https://github.com/kkamegawa/vsextensionforcodex/pull/157), [Issue #150](https://github.com/kkamegawa/vsextensionforcodex/issues/150), [Issue #151](https://github.com/kkamegawa/vsextensionforcodex/issues/151), [Issue #152](https://github.com/kkamegawa/vsextensionforcodex/issues/152))

- [x] Read the `turn/completed` turn id from `params.turn.id` and track the thread whose `turn/start` is in flight, so completion-before-response and new-thread `turn/started` are handled on the real wire shape.
- [x] Accept integer and string request ids in `serverRequest/resolved`.
- [x] Share `JsonRpcServerRequestDispatcher` between the stdio and WebSocket transports, deliver WebSocket notifications in order, tolerate isolated malformed frames, and fail pending requests as connection-closed. Added WebSocket transport tests over an in-memory socket.
- [x] Report remote connection loss as `Degraded` in the Worker without treating an intentional restart as a loss.
- [x] Reject an explicit attachment outside the remote local root before the turn starts, and show the reason in the transcript while keeping the attachment.
- [x] Persist remote profiles only through validated Save, stop rewriting settings on load, and remove the unused DI registration.
- [x] Build and release CI use the pinned 0.155.1 executable. The latest release runs a non-blocking app-server smoke test, and the latest-release API lookup is authenticated. README setup pins 0.155.1.
- [x] Schema-cache version rejection is tested with stub executables. The schema validation target is incremental.
- [x] Recorded the Worker contract v16 decision (ADR-011 amendment) and the transport corrections (ADR-012 amendment). Updated the Phase 5 contract statement in the plan and restored the `plan.md` section order.
- [ ] Health diagnostics and other remaining Phase 2/3 items stay in Issues #151 and #152.
- Validation: Release solution build with 0 warnings. Core 147/147 (three runs), UI 288/288 (one skipped), `scripts/test-schema-cache.ps1`, and `scripts/smoke-app-server.ps1` passed locally with Codex 0.155.1.

## 2026-09-30: PR #157 second review fixes ([PR #157](https://github.com/kkamegawa/vsextensionforcodex/pull/157), [Issue #151](https://github.com/kkamegawa/vsextensionforcodex/issues/151))

- [x] Confirm WebSocket notifications are delivered in wire order by one consumer (existing test).
- [x] Require both `localRoot` and `serverRoot` for a remote connection in the Worker.
- [x] Publish a remote connection loss only for the still-current connection, under the transition gate.
- [x] Add the connection-target flyout: remote profile list, editor, Save, Connect with this profile, and Use local app-server (design.md section 12, ADR-012 amendment).
- [x] Tests: root validation, close during connect, command state after loss, profile apply flow, flyout exclusivity, XAML binding reachability.
- Validation: Release build with 0 warnings. Core 153/153 (three runs), UI 295/295 (one skipped).

## 2026-09-30: Contract update to Codex CLI 0.159.1 and Preview release scope ([Issue #150](https://github.com/kkamegawa/vsextensionforcodex/issues/150), [Issue #156](https://github.com/kkamegawa/vsextensionforcodex/issues/156), [PR #157](https://github.com/kkamegawa/vsextensionforcodex/pull/157))

- [x] Pin 0.159.1 as the target and 0.155.1 as the regression baseline in `app-server-contract.json`; read pinned versions from the manifest in the schema scripts.
- [x] Generate 0.155.1/0.159.1 stable and experimental schemas and record the measured differences; confirm every used method exists.
- [x] Add contract tests for the 0.159.1 plan type and model catalog fields; update the test user agent.
- [x] Update README (setup, limitations, FAQ for missing models), ADR-011, and the plan documents to 0.159.1.
- [x] Label the remote connection as Preview in the flyout, README, design.md, and ADR-012.
- [x] README setup installs the release package (x64 or Arm64) instead of the standalone executable. A standalone `codex.exe` lacks `codex-code-mode-host.exe` and the `codex-resources` helpers, so every tool call failed with "failed to spawn code-mode host" in the Experimental Instance. Added FAQ entries for that error and for models missing from the picker.
- [x] Update the CI and release workflows to 0.155.1/0.159.1 (edited by the maintainer; the `.github` directory is outside the agent's write permission).
- [x] Experimental Instance with 0.159.1: `Ready · Codex 0.159.1`, and a turn completed.
- [x] Experimental Instance with the package install: `gpt-6.1-sol` is selectable and commands run.
- [x] Command output header: the label was an inline `Expander.Header` element that inherited the Expander foreground, so it turned white on the light hover background. It is now realized by `HeaderTemplate`; verified in the Experimental Instance.
- [x] Interrupt diagnostics: a Stop press whose turn ended with `status: interrupted` looked like a normal completion, and the log had no record of when Stop was pressed. The Extension and Worker now log the click, the `turn/interrupt` acknowledgement, and the final status with elapsed time (design.md section 13, README FAQ).
- [x] Experimental Instance: pressing Stop during a turn writes the three interrupt log lines.
- [x] Interrupt button: `RaiseCommandStates` had lost the `InterruptCommand` and `AccountCommand` raises when the connection-target commands were added, so the button stayed disabled during a turn. Restored and covered by a test; verified in the Experimental Instance.
- Validation: Release build with 0 warnings. Core 155/155, UI 295/295 (one skipped). Schema cache, compare, surface, and smoke checks passed with 0.159.1.

## 2026-07-21: Merge main into PR #31 (issue #25) and resolve conflicts

- [x] Resolve `ChoicePromptParser.cs` confirmation-regex conflict by adopting main's line-anchored `\A...to\b` pattern (issue #45), compatible with this branch's question-line scoping fix (issue #33).
- [x] Resolve `ChatToolWindowContent.xaml` action-row conflict by dropping this branch's superseded `Grid.Row="1"` DockPanel (would have collided with the slash-command `ItemsControl` already at `Grid.Row="1"`) and keeping main's `Grid.Row="3"` accessibility live region; the richer `Grid.Row="5"` action row already covers this branch's Attach/Mode/Model/Experimental API controls.
- [x] Resolve `ViewModelTests.cs` and `doc/implementation.md` conflicts (pure additive union with main).
- [x] Validate: solution builds with 0 warnings/0 errors; 95 Core tests and 268 UI tests pass.
- Ref: PR #31, issue #25.

## 2026-08-11: Complete skill catalog and persistent cache (Issue #140, ADR-010)

- [x] Add ADR-010 and synchronize the repository design, plan, task, and English/Japanese slash-command specifications. ADR-010 supersedes only ADR-009's twenty-skill presentation cap and volatile-cache-only assumption.
- [x] Remove the UI `.Take(20)` path and render every distinct Worker-accepted skill identity, including disabled rows, while retaining the Worker safety cap of 200 and a distinct `IsTruncated` state.
- [x] Add a Worker-owned, versioned, per-workspace persistent stale-while-revalidate snapshot alongside the existing 60-second memory cache. Return cached rows as stale and non-selectable, single-flight the live refresh, and publish only the newest generation.
- [x] Implement bounded cache storage under `%LOCALAPPDATA%\Kkamegawa.CodexForVisualStudio\skill-catalog\v1`: at most 200 skills, 4 MiB per workspace, a 24-hour hard expiry, 64 MiB total, atomic replacement, LRU cleanup, bounded cross-process locking, and fail-open-to-live handling for corrupt or unavailable cache files.
- [x] Exclude `defaultPrompt`, dependency values, icon source paths, raw app-server JSON, and Remote UI selection IDs from persistence. Revalidate all loaded fields and keep raw paths outside Remote UI data members.
- [x] Preserve live `skills/list` as the catalog system of record. A `turn/start` must bypass memory and disk snapshots, force reload, require an enabled exact `Name + Scope + Path`, and retain the pending chip on refresh or validation failure.
- [x] Add Core/UI/file-store coverage for 0/1/20/21/200/201 entries, full keyboard/UI Automation reachability, stale-to-fresh replacement, empty/unsupported/failed/truncated states, workspace isolation, corrupt/expired/oversize files, generation races, concurrent instances, cleanup, and force-reload invocation safety.
- [ ] Update Issue #140 and both Wiki languages with the approved ADR-010 amendment. After implementation and validation, update `doc/implementation.md`, rerun Debug/Release and full tests, and replace all VSIX/DLL/embedded-XAML/deployed-artifact evidence.

## 2026-08-11: Unified slash menu and structured skill invocation (Issue #140)

This section records the original ADR-009 implementation. ADR-010 supersedes its skill presentation
cap and cache durability decisions without rewriting the completed history below.

- [x] Update Issue #140 and the English/Japanese Wiki plan with the reviewed v15 contract, busy-state, candidate limits, identity validation, metadata boundaries, and icon spike gate.
- [x] Add ADR-009. ADR-008 remains authoritative for capability probing, flattened catalogs, complete identity, and missing-data tolerance; only explicitly admitted metadata fields are superseded.
- [x] Raise the Worker contract to v15 with `SkillInvocationInfo`, metadata DTOs, skills invalidation observer, 60-second `TimeProvider` cache, generation guard, and exact enabled-skill revalidation before `turn/start`.
- [x] Add one unified virtualized slash list with built-in rows, skill rows, scope labels, loading/unsupported/empty/truncated states, stable ranking, and non-selectable state rows.
- [x] Add one independent pending skill chip. It replaces the previous chip, clears only the slash query, permits text-free Ready turns, blocks pending send/steer while Busy, and clears only after successful matching `turn/start`.
- [x] Bound and sanitize brand color, default prompt preview, and dependency badges. Keep default prompt insertion explicit and non-sending; keep icon data behind the fixed-glyph spike gate.
- [ ] Add final Remote UI screenshot and Experimental Instance hash verification after the icon spike is accepted.
- [x] Complete Core/UI regression coverage for cache TTL/generation, metadata fallback, ranking/collisions, chip lifecycle, and structured input serialization.

## 2026-07-21: Stabilize intermittent CI build failures (issue #110)

- [x] Replace the fixed `Task.Delay(250)` in `StreamingBufferTests.cs` (3 tests) with a poll-until-condition-or-timeout wait, removing the race against the `StreamingBuffer`'s 75ms flush timer.
- [x] Validate: solution builds with 0 warnings/0 errors (Release); `StreamingBufferTests` pass 8/8 consecutive local runs; 95 Core tests and 268 UI tests pass.
- [ ] Add inline PowerShell retry (max 3 attempts) around the `Test core` / `Test UI` steps in `.github/workflows/ci.yml` — deferred: `Edit(.github/workflows/**)` is denied by this environment's permission settings, so the change is provided as a diff for manual application instead of being committed by the agent.
- Ref: issue #110.

## 2026-07-20: PR #89 review and merge validation

- [x] Confirm that the PR branch already contains the current `main` commit without conflicts.
- [x] Re-base plain text inputs on the Visual Studio TextBox style so slash-command arguments keep a themed foreground/background pair.
- [x] Preserve hidden default model metadata when only the top-level default identifier is reported.
- [x] Make the reasoning override test independent from persisted user settings.
- [x] Validate the integrated Release outputs with 95 Core tests and 244 UI tests passing.

## 2026-07-20: Reasoning and service-tier pickers (#85, #86, #93-#98)

- [x] Upgrade the Extension/Worker contract to version 13 with effort and service-tier presence flags.
- [x] Preserve hidden default model capabilities separately from the visible catalog.
- [x] Track effective turn settings across thread lifecycle and settings updates.
- [x] Add sanitized, model-aware persistent Reasoning and Speed pickers to Remote UI.
- [x] Make `/reasoning` and `/fast` thread-scoped, canonical, success-consumed, and sticky-restoring.
- [x] Update the Fake app-server and add Core/UI regression coverage.

---

## Phase 0: リポジトリ準備・PoC

### 0.1 公開リポジトリ初期化
- [x] `.gitignore`（Visual Studio / .NET 用）を追加
- [x] `LICENSE`（MIT 等）を追加
- [x] `README.md`（概要・前提条件・ビルド手順の雛形）を追加
- [x] `.editorconfig`（UTF-8 BOM、CRLF、C# 規約）を追加
- [x] `.github/copilot-instructions.md`（publisher 名一貫性、英語ソース等）を追加

### 0.2 apm セットアップ
- [x] apm CLI をインストール（macOS では `uv tool install apm-cli`）
- [x] `apm marketplace add github/awesome-copilot`
- [x] `apm.yml` を作成し `tfsugjp/skills/.github/agents/visual-studio-extension.agent.md` を宣言
- [x] GitHub Copilot / Claude / Codex 向け target を設定し、必要最小限の agent だけを展開
- [x] `apm install` 実行、`apm.lock.yaml` を生成・コミット
- [x] `apm-policy.yml` を追加（トランジティブ MCP / Unicode ガバナンス）
- [x] `apm audit --ci --policy apm-policy.yml` で lockfile・allowlist・drift を検証

### 0.3 codex app-server 疎通 PoC
- [x] ローカルに `codex` CLI が存在することを確認（`codex --version`）
- [x] `codex app-server generate-json-schema --out ./schemas` でスキーマ取得
- [x] 最小 C# コンソールで `codex app-server` を spawn → `initialize`/`initialized`/`thread/start`/`turn/start` 往復を確認
- [x] VisualStudio.Extensibility の **.NET 8** 実機対応範囲を検証し、機能ギャップを記録

**完了条件**: 公開リポジトリが初期化され、apm でエージェントが導入でき、C# から app-server と最小往復ができる。

---

## Phase 1: プロトコル基盤（AppServerClient）

### 1.1 プロセス管理
- [x] `CodexProcessHost`: `codex app-server`（stdio）を起動/終了、再起動、終了コード監視
- [x] codex 実行パス解決（PATH / 設定で上書き可能）
- [x] stderr をログへ転送
- [x] `ProcessStartInfo` は `UseShellExecute=false`、引数配列、固定 working directory、最小環境変数で構成
- [x] stderr / exit code / crash reason を `SecretRedactor` 経由で VS ActivityLog に記録（OutputChannel 経由）
- [x] app-server 終了時に pending RPC と active turn を fail fast し、UI をブロックしない

### 1.2 JSON-RPC レイヤ
- [x] `JsonRpcMessage` 型（request/response/notification、`jsonrpc` ヘッダはワイヤ上省略）
- [x] stdin へ改行区切り JSON 書き込み（JSONL）
- [x] stdout 行単位読み取り → `id` 対応の `result`/`error` を `TaskCompletionSource` に解決
- [x] `id` なし通知を購読者へディスパッチ（`IObservable` / event）
- [x] WebSocket 過負荷エラー（`-32001`）等のリトライ方針（将来 WS 用に抽象化）
- [x] stdout reader / JSON parser / response resolver / notification dispatcher を `Channel<T>` で分離
- [x] request timeout、cancellation、orphan `TaskCompletionSource` cleanup を実装
- [x] 1 行あたりの最大 JSON サイズと malformed JSON 時の復旧方針を定義
- [x] WebSocket は既定無効。使用時は loopback + capability token / signed bearer token を必須化

### 1.3 ライフサイクル
- [x] `InitializeAsync`（`clientInfo.name` = `codex_visual_studio`、`optOutNotificationMethods` 対応）
- [x] `experimentalApi` opt-in をオプション化
- [x] `ThreadStartAsync` / `ThreadResumeAsync` / `ThreadForkAsync`
- [x] `TurnStartAsync`（text/image/localImage 入力、model/effort/sandbox オーバーライド）
- [x] 通知 → ドメインイベント変換（`turn/*`, `item/*`）

### 1.4 型生成
- [x] `generate-json-schema` 出力から C# DTO を生成 or 手書き（バージョン整合チェック）
- [x] `SchemaVersionGuard` で実行中 app-server とクライアント DTO の互換性を起動時に検証（`InitializeAsync` で `serverInfo.version` を確認）
- [x] 未知の method / notification / enum 値はクラッシュせずログ記録し、可能なら degraded mode で継続

### 1.5 信頼境界・安全性
- [x] `ApprovalPolicyEngine` を実装し、command/file/network/oauth/MCP 要求を統一判定
- [x] risk category（`read-only` / `workspace-write` / `workspace-outside` / `network` / `destructive` / `credential/oauth`）を定義
- [x] `PathAccessPolicy` で full path、symlink、relative path、case-insensitive 比較を正規化
- [x] workspace 外書き込み、破壊的コマンド、資格情報らしき文字列を検出
- [x] 承認決定を session/thread/turn 単位でスコープ管理し、`acceptForSession` の有効範囲を監査可能にする
- [x] `SecretRedactor` で token、connection string、private key、OAuth credential を表示/保存前にマスク
- [x] `AuditLogService` で承認要求・決定・拒否・policy block を VS ActivityLog に記録（OutputChannel 経由で `WorkerBridge` / `ChatViewModel` に実装）

### 1.6 ストリーミング性能
- [x] `StreamingBuffer` を実装し、delta を 50-100ms 程度でバッチ化
- [x] command output / diff / reasoning summary にメモリ上限と truncation 表示を導入
- [x] 長い command output は折りたたみ、リングバッファ、または一時ログファイル退避に切り替える
- [x] notification burst 時に UI thread へ直接連続 dispatch しないことをテストで確認

**完了条件**: `AppServerClient` で thread/turn を開始し、ストリーミング通知をイベントとして受け取れる。app-server 終了・大量 delta・危険操作要求でも Visual Studio が固まらず、承認ポリシーが一元的に適用される。

---

## Phase 2: チャット UI（MVP）

### 2.1 拡張プロジェクト雛形

> **アーキテクチャ変更（Session 2 実施済み）**: 当初の「in-proc Package + out-of-proc Worker」構成から
> `Microsoft.VisualStudio.Extensibility` SDK による完全 OOP 構成に移行した（`design.md` 参照）。

- [x] OOP Extension プロジェクト（`Codex.VisualStudio.Extension`、net8.0-windows10.0.22621.0）を作成
  - `Microsoft.VisualStudio.Extensibility` SDK を使用
  - `CodexExtension : Extension`、`ShowCodexWindowCommand : Command`、`CodexToolWindow : ToolWindow`
  - `RemoteUserControl`（`ChatToolWindowContent` + `ChatToolWindowContent.xaml`）による Remote UI
- [x] in-proc Package（`Codex.VisualStudio.Package`、net472）を将来の差分ビュー用プレースホルダとして維持
- [x] ビルド設定（Central Package Management、arm64 マニフェスト、experimental instance デプロイガード）を整備
- [x] 拡張メタデータ（拡張名・publisher・VSIX ID、`ExtensionConfiguration.Metadata`）を設定
- [ ] DI コンテナで `AppServerClient` / `CodexSessionService` を登録

### 2.2 ツールウィンドウ
- [x] チャットツールウィンドウ（Remote UI / WPF）を実装
  - XAML は `EmbeddedResource`（`<Page>` でなく生 XML）として埋め込み — `EnvironmentColors` 等 VS 固有型をランタイムで解決するため
- [x] テーマ対応（`EnvironmentColors` / `VsResourceKeys`、色ハードコード禁止）
- [x] MVVM 構成（ViewModel / async コマンド / CancellationToken 対応）

### 2.3 ストリーミング表示
- [x] `item/agentMessage/delta` のバッチ追記（`StreamingBuffer` 経由）
- [x] `item/reasoning/summaryTextDelta` の折りたたみ表示（長文上限あり）
- [x] `commandExecution` 実行ログ（`item/commandExecution/outputDelta`、仮想化/折りたたみ/上限あり）
- [x] `fileChange` 差分プレビュー（`turn/diff/updated`、巨大 diff は折りたたみ）
- [x] `turn/plan/updated` の計画ステップ表示（状態更新でレイアウトが跳ねない）
- [x] `turn/completed` / `error` のステータス表示
- [x] 順序付きリスト（`1.` 番号保持）・ネストリスト（深さ別インデント、2 段でキャップ）のブロック描画 (#25)
- [x] トランスクリプトの `VirtualizingPanel.ScrollUnit=Pixel`（可変高アイテムのスクロール跳ね防止） (#25)

### 2.4 承認ハンドリング
- [x] `item/commandExecution/requestApproval` → `ApprovalPolicyEngine` 判定付き承認 UI（accept/acceptForSession/decline/cancel）
- [x] `item/fileChange/requestApproval` → path 正規化と workspace 境界判定付き承認 UI
- [x] `networkApprovalContext` 用のネットワーク承認 UI（host、port、protocol、session scope を表示）
- [x] risk category、承認スコープ、有効期限、policy block 理由を UI に表示
- [x] 承認対象の command/file/network 内容を `SecretRedactor` 経由で表示
- [x] `serverRequest/resolved` の整合処理
- [x] 承認/選択の解決後にカードを消し、結果のみの 1 行（"Accepted — <対象>" / "Selected — <選択肢>"）をトランスクリプトへ表示（Copilot Chat 準拠） (#25)

### 2.5 操作
- [x] 送信 / 中断（`turn/interrupt`）/ 追記（`turn/steer`）ボタン
- [x] コンポーザーの Ctrl+Enter で送信（`SendCommand`、Enter 単独は改行を維持）(#5)
- [x] 会話履歴一覧（`thread/list`）と再開（`thread/resume`）
- [x] app-server 未起動/クラッシュ/非互換時の degraded UI と再起動導線
- [x] `account/read` によるログイン状態表示と `account/login/start` による ChatGPT ブラウザ認証導線
- [x] UI thread ブロック、過剰メモリ使用、長大出力表示の回帰テスト

**完了条件**: GitHub Copilot 風チャット UI で Codex と対話でき、承認・中断・差分表示が機能する。

---

## Phase 3: スラッシュコマンド / スキル

### 3.1 スラッシュコマンドルーター
- [x] 入力先頭 `/` を検出してコマンドへルーティング（GitHub Issue #46）
- [x] `/review`（`review/start`: uncommittedChanges / baseBranch / commit / custom）
- [x] `/compact`（`thread/compact/start`、専用compaction event表示）
- [x] `/goal`（`thread/goal/set` / `get` / `clear`、専用goal event）
- [x] Codex IDEコマンドの許可リスト、非対応コマンド非表示、`//`エスケープ
- [x] コマンド補完 UI（入力時サジェスト、コマンドチップ、固定引数）
- [x] 実行中のスレッド別FIFOキュー、設定置換、切断・再起動・スレッド消失時取消
- [x] Worker契約v8と型付きcompact/review/fork/goal/MCP/feedback/rate-limit RPC
- [x] `/ide-context`、`/init`、`/status`のVisual Studio内処理
- [x] レビュー指摘対応: 次ターン設定の消費、キュードレイン網羅（失敗後継続・選択スレッド・セッションキュー）、compaction完了時のReady復帰、`/fork`後の履歴復元、`/model`大文字小文字非区別、`/goal show`エイリアス、候補の編集距離閾値（GitHub Issue #51、sub-issues #52-#56）
- [x] Remove the stale Experimental Instance registration, centralize extension identity diagnostics, and add slash-command display and packaging regression coverage (GitHub Issue #51, sub-issue #58)
  - The former identity has zero remaining Experimental Instance metadata or deployment hits; the current identity remains registered.
  - Slash-command normal, hover, and selection states use paired Visual Studio theme resources without reduced text opacity, preserving contrast across themes.
  - Worker diagnostics cancellation, process teardown, and output shutdown are serialized and awaited to prevent exceptions when a debugging session ends.
  - Debug solution build completed with zero warnings and zero errors; Core tests passed 70/70 and UI tests passed 171/171 with `--no-build`.
  - The VSIX manifest, packaged assembly, embedded Remote UI XAML, and SDK-managed Experimental deployment were inspected; packaged, build, and deployed assembly hashes matched.

### 3.2 スキル
- [x] `skills/list`（`cwds` スコープ、`forceReload`）でスキル一覧取得（60秒memory cache + invalidation）
- [x] 統合Slashメニューの独立チップ + `skill` 入力アイテムでスキル明示呼び出し
- [ ] `skills/config/write` で有効/無効切替
- [x] `skills/changed` 通知で一覧を再取得（invalidation）
- [ ] ADR-010に従い、全件仮想化表示とWorker永続stale-while-revalidate cacheを実装

### 3.3 apm との連携（スキル/プラグイン導入）
- [ ] awesome-copilot から必要スキル/プラグイン/エージェントを `apm install` で導入する手順をドキュメント化
- [ ] 導入済み資産が codex の `skills/list` / `plugin/list` に反映されることを確認
- [ ] `apm.lock.yaml` 固定、marketplace allowlist、未知 plugin/MCP の既定無効化をドキュメント化
- [ ] `apm audit` で Unicode spoofing、transitive MCP、未固定参照を検出する運用を定義

**完了条件**: 主要スラッシュコマンドが UI から実行でき、スキル呼び出しと apm 管理が機能する。

---

## Phase 4: 拡張機能・統合

### 4.1 モデル / 努力度
- [x] `model/list`（`includeHidden`）でモデルピッカー UI（`ChatViewModel.PopulateModelsAsync`、起動時 1 回ロード）
- [x] Load the startup model catalog before Remote UI account synchronization can block initialization (#39)
- [x] Add bounded model discovery diagnostics across the extension, Worker RPC, and app-server request boundaries (#39)
- [ ] モデル一覧の明示的な再取得（refresh）コマンド
- [x] スラッシュコマンドのreasoning effort、personality、service tier選択にモデル能力を反映（GitHub Issue #46）

### 4.2 インライン補完（任意）
- [ ] エディタ内ゴーストテキスト補完プロバイダ（in-proc が必要なら .NET Framework 4.7.2 フォールバック）
- [ ] 補完要求の debounce、キャンセル、active document 変更時の stale response 破棄
- [ ] 送信する editor context のサイズ上限と秘密情報 redaction

### 4.3 MCP / アプリ（コネクタ）
- [x] `/mcp`から`mcpServerStatus/list`でMCPサーバー状態表示（GitHub Issue #46）
- [ ] `mcpServer/oauth/login`（OAuth、`mcpServer/oauthLogin/completed`）
- [ ] `app/list` でアプリ一覧、`$<app-slug>` mention 入力（キャッシュ + invalidation）
- [ ] OAuth は PKCE / MSAL public client（client secret を拡張に埋め込まない）
- [ ] MCP tool call と OAuth login は `ApprovalPolicyEngine` と managed policy の対象にする
- [ ] token は OS/VS の安全な資格情報ストアを使い、ログ・設定ファイルに保存しない

### 4.4 設定 UI（新 Unified Settings）
- [ ] codex 実行パス、既定モデル、サンドボックス/承認ポリシー、ローカライズ等
- [ ] `config/read` / `config/value/write` / `config/batchWrite` で codex 設定連携
- [ ] managed policy を読み込み、ユーザー設定より強い制約として自動承認・non-loopback transport・未承認 marketplace・MCP/OAuth を制御
- [ ] 設定変更時に app-server restart が必要な項目と即時反映項目を明示

### 4.5 承認モードピッカー（GitHub Issue #75）

ChatGPT デスクトップと同等の承認方法選択 UI。レビュー済みの wire マッピングと設計詳細は #75 を参照。

- [x] Sub-issue A (#76): 組み込みモード（Ask for approval / Approve on my behalf / Full access / Custom (config.toml)）を、表示名と安定 ID を分離した Remote UI DTO で追加する。Agent モード時のみ有効にし、設定ストアを注入可能にして永続化する。`turn/start` には手動承認=`on-request` + `user` + `workspaceWrite`、代理承認=`on-request` + `auto_review` + `workspaceWrite`、Full access=`never` + `user` + `dangerFullAccess` を送る
- [x] Sub-issue B (#77): 手書き TOML 解析は行わず、対応する app-server の `permissionProfile/list`（`cwd`、ページング）で `[permissions.<id>]` を取得し、実験 API と runtime capability が利用できる場合だけ turn の `permissions` override で選択する。未対応時はプロファイル項目を表示せず組み込みモードを継続する
- [x] Sub-issue C (#78): `/permissions` を正式名、`/approve` を互換エイリアスとして実装し、`/status`、候補表示、`doc/slash-commands*.md`・`doc/design.md`・`doc/implementation.md` を更新する
- [x] Full access は Codex の sandbox と承認プロンプトを無効化し、Worker のポリシーは app-server が承認要求を送った場合だけ評価されることを、確認 UI・ToolTip・Automation HelpText・ドキュメントで正確に警告する
- [x] 保存する「希望する既定値」と thread start/resume/fork response および `thread/settings/updated` から得る「実効状態」を分離し、`/status` で両者の差を表示する。Full access は再起動後に無確認で復元しない
- [x] `turn/start` override は後続 turn に残るため、Ask / Auto / Full / profile から Custom に切り替える場合は新規 thread の作成を確認し、null/省略を reset として扱わない
- [x] profile カタログの非同期ロード中は保存済み選択を保持し、取得成功後に限って欠落 profile を Custom へフォールバックする。RPC 一時失敗で設定を上書きしない
- [x] XAML バインド対象の option collection / selected ID / enablement に `[DataMember]` を付け、Remote UI シリアライズ、アクセシビリティ、`/status` の実効値、Fake/実 app-server の wire 値を回帰テストする

**完了条件**: モデル選択・承認モードピッカー・MCP/アプリ・設定 UI が動作し、必要に応じインライン補完を提供できる。

---

## Phase 5: パッケージング / 品質

### 5.1 ローカライズ
- [ ] 英語ソース + 日本語リソース（英語フォールバック）

### 5.2 パッケージング検証
- [ ] VSIX 内容物の検証（出力ディレクトリではなく VSIX 実体）
- [ ] 依存 DLL が各コンポーネント横に配置されることを確認
- [ ] Experimental Instance での読み込み検証（`ActivityLog.xml` 活用）
- [ ] 決定論的パッケージング（その場限りの登録ハック排除）

### 5.3 CI / 再現性
- [ ] ビルド/テスト CI（GitHub Actions）
- [ ] `microsoft/apm-action` で apm 資産の再現性を CI に組み込み
- [ ] `apm audit` をコンテンツセキュリティチェックとして実行
- [ ] lockfile drift、未知 marketplace、未固定 plugin/skill 参照を CI で失敗させる
- [ ] unit test: JSON-RPC timeout/cancel/crash、policy 判定、path 正規化、secret redaction
- [ ] UI/perf test: 大量 delta、巨大 command output、巨大 diff で UI が固まらないことを検証

### 5.4 ドキュメント
- [ ] README に前提（ローカル codex CLI / apm）・セットアップ・OAuth アプリ登録手順
- [ ] アーキテクチャ図・プロトコルマッピング・トラブルシューティング
- [ ] セキュリティモデル（承認カテゴリ、managed policy、ログ redaction、transport 制約）を記載
- [ ] 性能モデル（streaming buffer、出力上限、キャッシュ/invalidation、既知の制限）を記載

**完了条件**: VSIX が検証済みで配布可能、CI と apm 統合で再現性が担保され、ドキュメントが整備される。

---

## 横断タスク（全フェーズ）

- [ ] 英語ソースコード・コメント、UTF-8 BOM・CRLF を維持
- [ ] async-first / CancellationToken 対応
- [ ] 認証・設定・拡張アクションのサービス抽象化
- [ ] 破壊的操作は必ず承認フローを経由
- [ ] `codex app-server` のバージョン差をスキーマ生成で検証
- [ ] すべての外部入力（app-server 通知、command output、diff、apm metadata、MCP/app metadata）を untrusted として扱う
- [ ] UI に表示する動的文字列は markdown/HTML/ANSI escape の扱いを明確にし、意図しないリンク・装飾・制御文字を無害化
- [ ] long-running operation は CancellationToken、timeout、progress/error reporting を持つ
- [ ] telemetry/logging は opt-in 方針、redaction、保存期間、管理者ポリシーを明確にする

---

## Work log

### 2026-07-20: Implemented bounded collapsible command output (issue #80)

Implemented issue #80 and sub-issues #81, #82, and #83. Sanitized command deltas now accumulate
in a non-serialized extension buffer capped at 2 MiB of characters. Output remains inline through
three logical lines and 4,096 characters, then starts collapsed with only that bounded preview
published to Remote UI. Hidden streaming deltas no longer republish the accumulated full text.

The transcript uses a standard WPF Expander with TwoWay state, native keyboard/UI Automation
behavior, Visual Studio dynamic theme resources, non-wrapping monospace text, and horizontal
scrolling. CRLF split across deltas is counted once, truncated output avoids unverified total-line
claims, and no third-party control or package was added. ADR-003 records the projection boundary.

The expanded header now retains its normal themed surface instead of remaining in the pressed
state. Hover and pressed foregrounds can override the inherited normal foreground, so every state
keeps a matching Visual Studio foreground/background pair. Non-truncated items also publish an
empty truncation notice across Remote UI.

- Validation: Release solution build completed with zero warnings and zero errors.
- Tests: Core tests passed 95/95 with `--no-build`.
- Tests: UI tests passed 267 with one symlink test skipped when the Windows test process lacked
  symlink privilege.

### 2026-07-20: Implemented empty SLNX-only scaffolding (issue #88)

Implemented issue #88 and sub-issues #102, #103, and #104. The empty-workspace prompt now
offers a root-level empty solution that contains no implicit project or source layout. The SLNX
file uses the sanitized workspace name, exact empty-solution XML, UTF-8 BOM, and CRLF, and the
existing non-overwrite and file-based app behaviors remain intact. ADR-006 records the decision.

- Validation: Release solution build completed with zero warnings and zero errors.
- Tests: eight focused scaffold tests are included; Core tests passed 95/95 and UI tests passed
  251 with one symlink test skipped when the Windows test process lacked symlink privilege.
- Compatibility: generated SLNX files passed XML parsing and `dotnet sln ... list` validation.

### 2026-07-20: Implemented usage presentation and freshness (issue #87)

Implemented issue #87 and sub-issues #99, #100, and #101. The Worker now preserves missing usage
percentages, while the extension presents clamped remaining limits, known window labels, Unix reset
times, and sanitized credits in both the popup and `/status`. Signed-in connection generations fetch
once; a 60-second popup TTL, monotonic push versions, and lifecycle invalidation prevent stale reads.
Transient refresh failures preserve the last-good snapshot and remain retryable. The themed Usage
popup is mutually exclusive with History, binds Escape at both host and popup levels, and opens only
compile-time approved destinations through an exact allowlist. ADR-005 records the freshness and
Remote UI focus contracts.

- Validation: project-scoped Release builds completed with zero warnings and zero errors. Core and UI
  tests passed with `--no-build`, covering parser, presentation, freshness, read/push races,
  lifecycle invalidation, links, and embedded XAML structure.

### 2026-07-20: Implemented the approval mode picker (issue #75)

Implemented issue #75 and sub-issues #76, #77, and #78. The Agent composer now exposes
stable built-in approval modes and capability-gated permission profiles, while Chat keeps
the exact read-only tuple. Contract version 12 carries the approval reviewer, mutually
exclusive permission-profile selection, and app-server-reported effective thread state.
Full access and Custom transitions use explicit confirmation, saved profile selections
survive asynchronous discovery failures, and `/permissions` plus `/approve` share the same
safe selection path.

- Validation: Release UI build completed with zero warnings and zero errors.
- Tests: Core tests passed 89/89 and UI tests passed 206/206 with `--no-build`.
- Packaging: the VSIX contains the Worker and both matching Contracts assemblies; packaged
  binaries match their Release outputs and the approval picker XAML remains a raw embedded
  `DataTemplate` resource.

### 2026-07-19: Addressed attachment and presentation review feedback (PR #74)

Disposed completed file-suggestion refresh cancellation sources without racing newer
refreshes, made temporary workspace cleanup reliable when tests fail, and restored exact
cardinality checks for slash-command key bindings so duplicate bindings are detected.

### 2026-07-19: Improved chat author label contrast (issue #73)

Set the transcript author label foreground directly to the Visual Studio tool-window text
theme resource and restored full opacity. This keeps the `You` and `Codex` labels paired
with the existing tool-window card background across light, dark, and High Contrast themes,
including live theme changes in the Remote UI host.

### 2026-07-19: Implemented file attachment support (issue #67)

Implemented the approved file attachment plan with SDK-backed multi-file selection,
removable attachment chips, workspace file suggestions triggered by `#`, and typed
`mention`/`localImage` turn inputs. Explicit selections are validated at both process
boundaries, capped and de-duplicated, while steering remains text-only and preserves
attachments for the next turn. ADR-001 records the Remote UI constraints and trust-boundary
decisions.

### 2026-07-18: Verified and closed slash command review findings (issue #51)

All fixes for issue #51 and its sub-issues (#52, #53, #54, #55, #56, #58) had already been
implemented on the `fix/51-slash-command-review-findings` branch and merged to `main` via
PR #60 (squash commit 2b005c6), but the issues remained open. Verified each fix against
current `main` and closed every issue with an evidence comment.

- Verification: Release build with 0 warnings (`TreatWarningsAsErrors=true`); full test
  suite passed without rebuilding (Core.Tests 70/70, Ui.Tests 171/171), including the
  regression tests named in each sub-issue.
- Closed: #52 (next-turn settings consumption), #53 (queue drain gaps), #54 (Ready state
  after compaction), #55 (/fork history load), #56 (model matching / goal alias /
  suggestion threshold), #58 (stale Experimental deployment), and parent #51.

### 2026-07-18: Displayed the connected Codex version (issue #61)

Implemented issue #61 and sub-issues #62, #63, and #64. The Worker now reads a
bounded, validated version from the app-server initialize user agent, carries it
through contract version 9, and clears stale values outside connected states.
The Remote UI header displays the sanitized value as `Ready · Codex <version>`
and preserves it for busy and approval states with narrow-width truncation and
one accessible live-region announcement.

- Validation: Release build completed with 0 warnings and 0 errors.
- Tests: Core.Tests 75/75 and Ui.Tests 179/179 passed from the Release build.
- Documentation: implementation notes, worker contract notes, the security
  policy, and the approved Wiki plan were updated.

### 2026-07-20: Release readiness — docs, VSIX identity, and CI (issue #105)

Prepared the first Marketplace-bound release. Rewrote `README.md` for end users
(requirements, setup, limitations, FAQ, release flow), added `README_ja.md`, moved the
VSIX identity to `relaycodexforvs.KazushiKamegawa.<GUID>`, bundled the English
license and the extension icon into the VSIX, and made the VSIX version follow the git
tag through the generated assembly version.

- Issues: #105 (parent), #106 (README), #107 (VSIX identity and bundled assets),
  #108 (CI and release workflows).
- Documented limitations: Codex CLI older than the verified 0.145.0 is unsupported,
  multiple Codex installations can launch an older build (`CODEX_PATH` pins it), and npm
  installs are known to misbehave so winget is recommended.
- Validation: Release build with 0 warnings; Core.Tests 95/95 and Ui.Tests 268/268 passed.
  A build with `-p:Version=1.2.3.4` produced a VSIX whose `Identity Version` was `1.2.3.4`.
- Decision record: `doc/adr.md` ADR-007.

### 2026-07-20: Fixed a dangling-symlink write bypass found by CI (PR #109)

The GitHub-hosted Windows CI runner has symlink-creation privilege that local
development machines typically lack, so `CreateEmptySolution_DoesNotFollowDanglingSolutionSymlink`
had always been skipped locally and never actually exercised. On CI it failed for real:
`ProjectScaffolder.WriteFileIfMissing` opened the target path with
`FileMode.CreateNew` without first checking for an existing leaf entry, and Windows
transparently follows a dangling symbolic link for that open mode, so scaffolding could
write a new file at the link's target instead of leaving the existing link alone.
Added an upfront `PathEntryExists` check before the open.

- Validation: Release build 0 warnings; Ui.Tests 268/268 passed locally (the symlink
  test itself still reports Inconclusive/skipped locally, lacking the OS privilege).

### 2026-08-10: Restored selected-surface foreground contrast (issue #137)

Paired the active slash-command chip background with the Visual Studio selected glyph
foreground and generalized the selected-chip icon style for both attachment removal and
slash-command clearing. Fixed slash-command option labels and thread-history text now inherit
their owning selectable control's state foreground without an implicit `TextBlock` foreground
overriding it. Structural regression coverage verifies the exact selected, hover, and pressed
theme-resource pairs and both inheritance paths.

- Validation: focused UI test compilation was blocked before source compilation because the
  sandbox denied access to the local Windows SDK discovery directory.
- Formatting: modified XAML, C#, and Markdown files retain UTF-8 BOM and CRLF line endings.

### 2026-08-12: Refresh usage after conversation turn completion

Added the approved turn-completion usage refresh. The Extension now forces the existing
`worker/account/rateLimits` read after every `TurnCompleted` event, after the transcript projection
has finished. `TurnCompleted` covers turns the app-server reports as interrupted (it still arrives
as `turn/completed`); a transport-level failure instead reports `Degraded`, under which no forced
read is attempted (see the ADR-005 amendment). Existing connection-generation, TTL, push-version,
cancellation, and last-good-snapshot behavior remain unchanged; no Worker, RPC contract, XAML, or
package changes are required.

- Tests: added UI regression coverage for TTL-bypassing refresh, unavailable-account no-op behavior,
  and retry after a failed post-turn read.
- Validation: CI (`ci.yml`, commit `f04cfd0`) built the solution in Release with zero warnings and
  ran the full `Codex.VisualStudio.Core.Tests` and `Codex.VisualStudio.Ui.Tests` suites, not only the
  focused usage/turn subset reported at design time. Result: `build` check SUCCESS. The Visual Studio
  Experimental Instance check (real turn completion, header/popup/updated-time sync) remains to be
  run interactively and is tracked as its own sub-issue rather than closed by this entry.
- Tracking: no parent/sub-issue set was created before this branch was pushed, so the branch name
  omits an issue number (`codex/feature-refresh-usage-after-turn` instead of
  `codex/feature-<parent-issue>-refresh-usage-after-turn` per the original plan). A parent issue and
  sub-issues were opened retroactively and linked from PR #142; the branch itself was not renamed to
  avoid disrupting the open PR.

### 2026-08-12: Also refresh usage after context compaction

Code review of PR #142 found that `/compact` consumes model calls but does not always raise
`TurnCompleted` — the app-server may report completion only through `context/compacted`
(`WorkerRpcService.PublishContextCompactedAsync` already special-cases this for `Ready` recovery).
`ChatViewModel.OnContextCompactedAsync` now also forces a `worker/account/rateLimits` read when
`IsCompleted` is true, using the same post-projection ordering and `force: true` gate as the
turn-completion path. In-progress compaction events remain a no-op.

- Tests: added `ChatViewModel_ContextCompacted_ForcesUsageRefreshWithinTtl` and
  `ChatViewModel_ContextCompacted_InProgressDoesNotRefreshUsage`.
- Validation: `dotnet build CodexForVisualStudio.slnx -c Release` — 0 warnings, 0 errors. Full suite
  run locally: `Codex.VisualStudio.Core.Tests` 113/113, `Codex.VisualStudio.Ui.Tests` 285/285.
  Visual Studio Experimental Instance check still pending (tracked in the sub-issue above).

### 2026-09-22: Use the latest Codex release locally in CI ([Issue #150](https://github.com/kkamegawa/vsextensionforcodex/issues/150))

Replaced the remaining inline release download logic with `scripts/install-codex.ps1`.
Schema generation continues to use the manifest-pinned 0.154.0 and 0.155.1 Windows x64
assets and their SHA-256 values. The build and release jobs additionally download the
latest stable Windows x64 asset into the runner-local temporary directory, verify the
published digest, run `--version`, and pass the resulting path through `CODEX_PATH`.
This keeps the protocol contract reproducible while ensuring every executable CI path
uses a locally downloaded standalone Codex binary rather than winget.

### 2026-09-28: Fix version selection, Worker startup, and account refresh in the ARM64 Experimental Instance ([Issue #158](https://github.com/kkamegawa/vsextensionforcodex/issues/158))

- [x] Launch the packaged Worker DLL through the `dotnet.exe` beside the Extension's active
  .NET runtime, with the existing Worker apphost as a fallback when that host is absent.
- [x] Add a regression test for selecting the runtime host and preserving a Worker DLL path
  containing spaces as one process argument.
- [x] Dispatch JSONL notifications and server requests through a bounded ordered pump while
  resolving responses immediately; add a regression test for a notification awaiting `account/read`.
- [x] Verify UI tests (289 passed, one skipped), a zero-warning Release extension build,
  and the Worker DLL, EXE, and runtime configuration in the VSIX.
- [x] Verify Core tests (148 passed) and UI tests (289 passed, one skipped) after the JSONL fix.
- [x] Verify the WinGet Codex 0.157.1 executable initializes its app-server and returns an
  existing signed-in account from `account/read`.
- [x] Start the Release Worker DLL with the Visual Studio-bundled `dotnet.exe` and WinGet Codex
  0.157.1, then confirm `worker/connect` returns Ready and `worker/account/status` returns SignedIn.
- [x] Diagnose the reported F5 failure: the Experimental Instance loaded the globally installed
  0.2.0.0 VSIX instead of the newly deployed 0.1.0.0 VSIX with the same extension ID. The runtime
  log showed the old Worker launch path and the same hostfxr load failure.
- [x] Raise the default development version to 0.2.1 and verify the Debug VSIX manifest and
  extension assembly are 0.2.1.0. The Debug build has zero warnings and errors; the release
  workflow's explicit `-p:Version` continues to override the default.
- [x] Record the version precedence decision in `doc/adr/ADR-017-development-vsix-version.md`
  and verify the Debug UI suite (289 passed, one skipped).
- [x] Confirm F5 in the Visual Studio Experimental Instance loads the 0.2.1.0 VSIX, launches the
  Worker with `dotnet.exe`, connects to Codex 0.157.1, and reaches Ready and SignedIn. The user
  confirmed the UI result, and the 2026-09-28 07:51 diagnostic log confirms each runtime state.

### 2026-09-28: Close the JSONL connection when a pump faults ([Issue #159](https://github.com/kkamegawa/vsextensionforcodex/issues/159))

- [x] Confirm the notification/response deadlock is already fixed by #158 and covered by
  `NotificationHandler_CanAwaitRequestResponse`.
- [x] Run every `JsonLineRpcConnection` pump through a guard that closes the connection on an
  unexpected exception, and fail pending requests before canceling the lifetime so callers see
  `JsonRpcConnectionClosedException`.
- [x] Report notification handler timeouts as error events, and report an `account/read` timeout
  as Unavailable instead of leaving the account at Checking.
- [x] Add Core tests for a write pump failure, a timed-out notification handler followed by a
  later notification, and an account notification whose `account/read` times out.
- [x] Verify a zero-warning Release build of the solution, UI tests (286 passed, one skipped), and
  Core tests (133 passed). The two `CodexProcessHostTests` failures also occur on the unmodified base.

### 2026-10-01: Revise the secure remote connection design (Issue #151)

- [x] Reconcile the design review with existing transport, profile, retry, and diagnostic implementations.
- [x] Define shared endpoint validation, bounded token I/O and secret leases, profile freshness, independent root health diagnostics, startup deadlines, idle peer detection, and the exact retry allowlist in paired English/Japanese design documents.
- [x] Synchronize design.md section 12 and the paired Phase 2 plan; retain ADR-012 history and add a proposed amendment and Japanese translation.
- [x] Prepare paired Wiki plan pages and matching Home index changes for user review.
- [x] Publish the paired Wiki plans and Home index after user authorization; update Issue #151 with the design clarifications and remaining acceptance evidence.
- [x] Confirm the revised design and ADR amendment before any later implementation (design confirmed before the 2026-10-02 implementation; ADR-012 amendment accepted in [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161)).
- [x] Implement, build, test, inspect the VSIX, and verify the Experimental Instance display against the design acceptance criteria (see the 2026-10-02 implementation record and [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161)).

Design: [Secure Remote App Server Connection](secure-remote-connection-design.md) / [日本語](secure-remote-connection-design_ja.md).
The user restricted this follow-up to Issue/Wiki publication. Wiki commits `2705f9b` (publication) and `5b29eb8` (canonical page/language links) are pushed; the latter matches remote master. Issue #151 body is updated and verified; its title and open state are preserved. Both published plan pages and both Home pages render in the browser, with canonical index and language links verified. Implementation and build/UI verification remain pending.

### 2026-10-02: Implement the secure remote connection (Issue #151)

- [x] Contracts: shared endpoint, bearer-token, and token-path policies; profile fingerprint; target snapshot; typed `-32051` rejection; contract v17 with `worker/reconnect` and `worker/connection/diagnose`.
- [x] Protocol: shared-policy WebSocket admission, allowlist-owned `SendReadOnlyRequestAsync`, caller-owned invoker, 30-second keep-alive, and inbound activity sequence.
- [x] Worker: networking factory with proxy mapping and loopback pinning, bounded token reader, secret leases and redacted diagnostics, staged 45-second startup, Restart/Reconnect separation, health diagnosis, and idle watchdog.
- [x] Extension: shared validation at Save, operation gate, reconnect freshness checks, Restart local / Reconnect remote labels, and the health check rows in the connection-target flyout.
- [x] Tests: policy, token, lease, proxy/DNS, TLS/authentication/redirect, health, retry, startup, watchdog, reconnect, and view-model coverage. Release build with zero warnings; Core 274 passed (2 pre-existing failures reproduced on the base commit, 1 skipped); UI 306 passed (1 skipped); contract-surface and schema-cache checks; VSIX inspection.
- [x] Review fix: exact `localhost` tries every verified loopback address in order, so a listener on only `::1` (or only `127.0.0.1`) is reachable for the WebSocket handshake and health diagnosis.
- [x] Pinned 0.159.1 `test-schema-cache.ps1` (schema cache contract tests passed) and `smoke-app-server.ps1` (initialize succeeded), run on 2026-10-02 against the hash-verified release asset from `install-codex.ps1`.
- [x] Experimental Instance display: the maintainer confirmed the local connection-target flyout and the widened usage popup with screenshots on 2026-10-03 and accepted the remaining items (remote actions, failures, health states, themes, narrow width, keyboard focus) without recorded screenshots.
- [x] PR #161 fifth review fix: the idle watchdog uses its own activity generation, advanced after each timestamp update and captured before the expiry check, as the probe-episode baseline, so a message racing with that check restarts the idle window instead of being absorbed. Release build zero warnings; Core 281 passed (same 2 order-dependent failures, 1 skipped); watchdog tests stable over repeated runs. Tracking: [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161).
- [x] PR #161 sixth review fix: a `WebSocketException` raised while the parent token (caller or overall startup deadline) is canceled is preserved as cancellation; only a stage-local cancellation becomes `Timeout`. Release build zero warnings; Core 282 passed (same 2 order-dependent failures, 1 skipped). Tracking: [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161).
- [x] PR #161 seventh review fixes: Apply, Save, and Remove capture the row they were invoked for before waiting on the profile operation gate and act on that row (Apply persists that profile's selection under the gate before connecting); editing the checked profile's metadata clears the health result and discards an in-flight completion. Release build zero warnings; UI 308 passed (1 skipped). Tracking: [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161).
- [x] PR #161 eighth review fix: a selection change raises the Save and Remove `CanExecute` notifications immediately instead of only after the queued selection persistence, so Remote UI buttons stay current while a connect holds the profile operation gate. UI 309 passed (1 skipped). Tracking: [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161).
- [x] PR #161 ninth review fixes: an ungated connect takes the profile operation gate before the connect guard and releases it only after clearing the guard, so a queued profile action always dispatches its own connect; a failed connect to a saved remote profile keeps that remote target (generation 0), shows Reconnect remote app-server, and Restart reruns the saved-profile connect instead of a local restart. UI 312 passed (1 skipped). Tracking: [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161).
- [x] Confirm the ADR-012 amendment status: accepted by the maintainer on 2026-10-02 in [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161); both language versions record it as Accepted.
- [x] PR #161 review fixes: the connection-target label captures the applied profile under the profile operation gate; the idle watchdog measures silence from the last inbound message (activity signal on `IInboundActivitySource`); the health-check label shows the observation time; the Check health test asserts `CanExecute` notifications; design references name `ReadOnlyRetryPolicy`/`ReadOnlyRequestAllowlist`. Release build zero warnings; Core 275 passed (same 2 pre-existing failures, 1 skipped); UI 306 passed (1 skipped). Tracking: [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161).
- [x] PR #161 second review fixes: the idle watchdog keeps one activity baseline per probe episode and the Worker revalidates it under the transition gate before retiring the socket; positive trusted-TLS handshake (`127.0.0.1` and pinned `localhost`) and hostname-mismatch tests via a custom-root chain policy without machine trust changes. Release build zero warnings; Core 281 passed (1 skipped, with `CODEX_PATH` set to the pinned 0.159.1 CLI); UI 306 passed (1 skipped). Tracking: [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161).
- [x] PR #161 third review fixes: the bearer-token buffer is cleared on every exit of the read (open, final-path, read, cancellation, parse); remote close handlers are admitted under `trackedCallbacksGate` before logging and suppressed once `DisposeAsync` snapshots its callbacks, so no exception is logged after the token lease is released. Release build zero warnings; Core 279 passed (the 2 known failures are order-dependent and pass in isolation, 1 skipped); UI 306 passed (1 skipped). Tracking: [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161).
- [x] PR #161 fourth review fixes: the read-only retry's close handler tolerates invocation after its cancellation source is disposed; the token reader no longer pre-checks `FileInfo.Exists`, so an existing but unreadable file is `TokenFileUnreadable` and only a missing file or directory is `TokenFileMissing`. Release build zero warnings; Core 281 passed (same 2 order-dependent failures, 1 skipped); UI 306 passed (1 skipped). Tracking: [PR #161](https://github.com/kkamegawa/vsextensionforcodex/pull/161).

Implementation record: [implementation.md](implementation.md#secure-remote-app-server-connection-issue-151-2026-10-02). Tracking: [Issue #151](https://github.com/kkamegawa/vsextensionforcodex/issues/151).
## 2026-10-05: Questions, permissions, and MCP interaction (Issue #154)

- [x] Review the v19 contract, fixed CLI 0.159.1 protocol, existing implementation, and published bilingual plan; correct the contract baseline to v20 (the next available version).
- [x] Approve and publish the bilingual interaction design, ADR-015 revision, Issue #154 scope, and child issue hierarchy (#165–#168).
- [x] Add typed Worker/Extension contracts and RPCs for questions, scoped permissions, command choices, MCP elicitation, unsupported interactions, and authentication state/actions.
- [x] Add a shared owner/generation/request-scoped pending registry with validation, timeout, external-resolution, retirement, and at-most-once completion handling.
- [x] Add independent pending interaction cards and explicit response flows for questions, permissions, approvals, and supported MCP forms.
- [x] Add local Gateway OAuth status/read gating and explicit login/cancel/browser actions; restrict remote profiles to status and guidance.
- [x] Reject secret-marked input and unsupported native user-verification requests before sensitive payloads reach the UI; keep success-path capability undeclared.
- [x] Complete the Fake App Server and fixed CLI 0.159.1 contract checks, CLI contract surface verification, stable schema comparison from 0.155.1 to 0.159.1, and schema-cache tests.
- [x] Record final Core/UI test totals: Core Debug 369 passed, 0 failed, 5 skipped (374 total); UI Debug 342 passed, 0 failed, 1 skipped (343 total).
- [x] Verify full solution Debug and Release builds with 0 warnings and 0 errors; emit the Release VSIX.
- [x] Inspect the Release assembly raw XAML hash against the source XAML and record the VSIX SHA-256: `BA83A86AFA1988B8FA8A2C387415435F5F727054E0379C3E241ED252AF83AB85`.
- [ ] Complete Experimental Instance screenshots for Light/Dark/High Contrast, narrow width, keyboard, read-aloud/accessibility, multiple cards, and authentication states. Visual acceptance remains pending; Visual Studio 2026 Enterprise 18.10.3 is installed in the current environment.

### Validation evidence

- Initial Core run during implementation: 336 passed, 18 failed, 5 skipped; this interim result is superseded by final validation.
- Core Debug: 369 passed, 0 failed, 5 skipped (374 total).
- UI Debug: 342 passed, 0 failed, 1 skipped (343 total).
- Full solution Debug and Release builds: 0 warnings, 0 errors in each; Release VSIX emitted.
- CLI contract surface verification: passed; stable schema comparison 0.155.1→0.159.1: passed; schema-cache tests: passed.
- Release assembly raw XAML SHA-256 matches source XAML. Release VSIX SHA-256: `BA83A86AFA1988B8FA8A2C387415435F5F727054E0379C3E241ED252AF83AB85`.
- Visual/accessibility acceptance: pending; no Experimental Instance screenshots are recorded. Visual Studio 2026 Enterprise 18.10.3 is installed in the current environment.

Tracking: [Issue #154](https://github.com/kkamegawa/vsextensionforcodex/issues/154) and children [#165](https://github.com/kkamegawa/vsextensionforcodex/issues/165), [#166](https://github.com/kkamegawa/vsextensionforcodex/issues/166), [#167](https://github.com/kkamegawa/vsextensionforcodex/issues/167), and [#168](https://github.com/kkamegawa/vsextensionforcodex/issues/168).

### 2026-10-10: Collapse authentication status details ([Issue #173](https://github.com/kkamegawa/vsextensionforcodex/issues/173))

- [x] Record the requested final presentation in the paired interaction design: retain the compact Gateway summary/recovery actions and Check status; expand MCP details explicitly; add a persistent MCP details toggle beside Check status. The toggle remains visible in both states and preserves keyboard focus when collapsing.
- [x] Add serialized expansion state and a persistent MCP details toggle. Preserve authentication data and pending actions; keep collapsed details closed after notifications or in-flight responses; reset expansion on owner retirement. The stable toggle remains visible and retains keyboard focus while its checked state reflects expansion.
- [x] Add four regression tests for hide/recheck, retained actions, asynchronous races, owner reset/stale responses, and embedded XAML bindings. Allow the existing image-preview test's root detection to recognize a Git worktree's `.git` file.
- [x] Validate the Release solution build (0 warnings, 0 errors), focused tests (4 passed), full UI tests (378 passed, 1 skipped), and full Core tests (402 passed, 5 skipped). Skipped cases require filesystem-link support unavailable in this environment.
- [x] Verify packaged Extension DLL matches the Release output and embedded raw XAML matches source. The renders under `artifacts/hide-auth-status/` cover the earlier Hide status iteration only, not the final toggle.
- [x] Address the PR #175 review: label the toggle MCP details and bind its checked state two-way to the expansion state (removing the unbound Hide command and the toggle command); share `RoundButtonStyle` (now `ButtonBase`) with the toggle so disabled and hover states stay consistent; show a compact hint while collapsed details hide pending MCP sign-in, authorization, or dismiss actions; remove the one-off `.gitattributes` rule; strengthen the regression tests (five tests, including pending-action tracking).
- [x] Re-validate after the review: Release solution build (0 warnings, 0 errors), full UI tests (380 passed, 0 skipped), and full Core tests (407 passed on rerun; the first run reported one failure that did not reproduce and was not identified). Inspect off-screen WPF renders of the final authentication XAML at 700/380 pixels in Light, Dark, and High Contrast palettes for collapsed-with-pending, expanded, and collapsed-clear states. Render evidence is under `artifacts/issue173-mcp-details/`; the isolated harness supplies theme brushes, the inherited foreground, and presentation data, so it does not establish Experimental Instance integration or live theme switching.

The initial full UI run exposed the pre-existing `.git` directory assumption in the image-preview test. Accepting both a `.git` directory and a worktree `.git` file resolves that test-harness issue; the final UI suite above passes in the dedicated worktree. This bug fix updates the existing design and does not add a feature-plan Wiki page.

The separate goal cancellation defect is tracked in [Issue #174](https://github.com/kkamegawa/vsextensionforcodex/issues/174); its implementation is outside this PR.

Japanese record: [日本語](task-issue173_ja.md).

### 2026-10-11: Goal Stop ([Issue #174](https://github.com/kkamegawa/vsextensionforcodex/issues/174))

- [x] Record the approved design and plan in [English](goal-stop-design.md) / [Japanese](goal-stop-design_ja.md) and [English](goal-stop-plan.md) / [Japanese](goal-stop-plan_ja.md), with summaries in `design.md` and `plan.md`. After the integration review, add the composer stop-state table and the Worker rules for failed reads, timeouts, unsupported pause, overlapping stops, and starting turns.
- [x] Advance the Worker contract to v22 with `worker/thread/goal/stop` (`StopThreadGoalRequest` / `StopThreadGoalResult`, independent pause and interrupt outcomes).
- [x] Worker: read the goal, send a status-only pause, then interrupt the latest same-thread turn. Revalidate owner, generation, and thread around each call. Timeouts are unknown outcomes and are never retried. An unsupported pause still attempts the interrupt. Overlapping stops are serialized. A turn start without a server turn ID is reported as unknown.
- [x] Extension: Goal state from command results, notifications, and reads, guarded by projection revisions. The composer stop phases (None / Stopping / AwaitingTurn / Unknown / Retry) all have exits and run under one lock. There is a single primary Stop action with matching glyph, tooltip, and automation name/help text. Ctrl+Enter uses a send-only `SendKeyCommand`. Slash commands stay available while a goal is Active and are blocked only while a stop is unresolved. Queued thread commands are cancelled and fenced. The goal is refreshed only after a reconnect, with no replay.
- [x] Fake App Server `Issue174GoalStop` scenario and integration tests; Worker ordering, failure, and race tests; ViewModel tests for stuck states, notifications, stale results, unsupported servers, keyboard, and slash visibility.
- [x] Parallel sub-agent reviews of Worker concurrency and ViewModel/WPF behavior. Findings fixed and covered by tests: timeouts escaping the interrupt, unsupported-pause handling, false duplicate failures, unresolvable Check Stop Status, permanent Stopping, missing completion notifications, sticky Unsupported/Retry states, stale operations after thread switches, over-blocked slash commands, duplicate send buttons, stale help text, and lingering stop messages.
- [x] Validate Debug and Release solution builds (0 warnings, 0 errors each); Core 415 passed / 5 skipped (420); UI 399 passed / 1 skipped (400); stable and experimental schema comparisons 0.155.1→0.159.1, contract-surface verification for both 0.159.1 surfaces, and schema-cache validation all passed.
- [x] Release VSIX integrity: packaged Extension and Contracts DLLs (root and `Worker/`) match the build output; embedded XAML matches source (`3B6475D56A21E8E68AAF64B84A94AF9426AF4C55A3780E1D05E855CA4AF84805`); `ContractVersions.Current` is 22. VSIX SHA-256: `8F2FB7C6013815448D8C327D8F8EC508DF3DB097EA0A1DB5A18EA3BFDEB617C8`.
- [ ] Experimental Instance acceptance (Light, Dark, High Contrast, narrow width, keyboard/focus, UI Automation, and screenshots of Active Goal, stopping, stopped, and reconnected states). Not performed: desktop automation was unavailable in this session. These criteria remain incomplete.

Japanese record: [日本語](task-issue174_ja.md).
