# Phase 2B-2 M3-A2c — Controlled runtime enforcement

日期：2026-10-16（Asia/Taipei）。基準 `a5ce12cc0f92c331d9c25d9461bb0b7ddc1e5de6`，main 與 fetch 後 origin/main 相同；起始 tracked tree 乾淨，既有 `.vscode/` 不讀取、不提交。重讀 [M3-0 review](PHASE2B2_M3_0_REVIEW.md)、[A1](PHASE2B2_M3_A1_RESULTS.md)、[A2a](PHASE2B2_M3_A2A_RESULTS.md)、[A2b](PHASE2B2_M3_A2B_RESULTS.md)，以 source 為準。

**本輪受控 synthetic composition 已接上既有官方 Workflow 與 mandatory provider-effect guard，37 focused／530 offline 通過。Production trust、Broker、human approval/resume、Abort delivery 未啟用。這不是全 Host／惡意 same-principal isolation 的驗收。**

## 1. Entrypoint inventory 與實際 coverage

| Source / entrypoint | 現在行為與 coverage |
|---|---|
| [Host Program](../../src/MAF.Host/Program.cs) → [ExternalConsole.RunAsync](../../src/MAF.Host/ExternalConsole.cs) | output/mode arguments 仍先驗；沒有 production trust，明確 `ProductionTrustUnavailable`、exit 1，不讀舊 provider env、不建 client/store/DI、不披露 records。Help 明列停用及 native exclusion |
| [ExternalWorkflow.RunAsync](../../src/MAF.Workflows.External/ExternalWorkflow.cs) | 公開 constructor 預設 unavailable access；Run 在任何 graph/provider 活動前拒絕。沒有 ephemeral fallback |
| [DurableExecutionService](../../src/MAF.Workflows.External/DurableExecutionService.cs) Run/Recover/List | 公開 constructor 同樣 default-deny；在 store read/create/list 前拒絕。只供受控 composition 的 internal LaunchOwned/InspectOwned 接回原 Execute/RecoverOwned/PersistingClient |
| [OpenCodeExecutor.HandleAsync](../../src/MAF.Workflows.External/OpenCodeExecutor.cs) | 公開 construction 不能繞 entry guard。受控 executor 使用原官方 Executor；protected cancellation/timeout 只停止觀測，**不呼叫 Abort，也不再做取消後的額外 Observe** |
| [OpenCodeClient](../../src/MAF.Workflows.External/OpenCode/OpenCodeClient.cs) public Validate/Create/Submit/Observe/Reattach/Abort | 每個方法先進入 effect context，唯一 SendAsync 再做 wire guard。Public constructor 缺 context → `ProtectedRuntimeRequired`，0 HTTP；PrepareExecution/Scope/GetRequestCount 僅本地 metadata，非 grant |
| [ProtectedExternalRuntime.RunAsync](../../src/MAF.Workflows.External/ProtectedExternalRuntime.cs) Submit new | verified identity + trusted root/config → TaskId owner → fresh DB read → A1 exact assessment → A2b atomic Allow → consume same-owner continuation → 原 DurableExecutionService 官方 graph；Deny/RequireApproval 不建 execution、不 HTTP、不自動 request/approve |
| Protected Run existing / Inspect | owner 下 fresh read，獨立 Inspect assessment；Submit intent 若已有 execution 也只 recovery。Metadata effect 在返回任何已存 correlation 前驗 policy/control/owner；已知 session Reattach → 原 recovery-only Workflow，沒有 Create/Submit |
| Protected ephemeral / batch / list | `OperationNotSupported`／`MetadataListNotSupported`，不列 records、不換 legacy path。沒有 global list policy 契約，不先列後 filter |
| Cancel / Abort | Protected Cancel intent 不支援；Abort 在 effect context 與 persistence effect check 皆拒絕。Submit、Inspect 不授 Cancel；本輪不新增 cancellation intent CLI 或 M4 delivery |
| Native [ComprehensionAgentFactory.Create](../../src/MAF.Agents.Comprehension/ComprehensionAgentFactory.cs)、[ReviewService.ReviewAsync](../../src/MAF.Agents.Review/ReviewService.cs) | Program 的 ask/interactive/review 直接走各自官方 agent/IChatClient；沒有經 external policy，仍是未受保護的獨立 legacy capability，未修改或執行 live model |
| Public execution/approval stores、activation/maintenance primitives | 仍是 trusted library/storage APIs，不是 authenticated ingress。直接 store metadata read 不提供此 runtime 的 disclosure protection；可改 DB／注入不可信 DI 的程式不在此 composition 安全保證內 |

M3-0 B1–B5/B9 的 external dispatch/list/raw/implicit-Abort 入口在上述 composition 內關閉；B10 native routes、B7 writer isolation 等不因此通過。沒有「全 process 都受 policy 保護」的宣稱。PCA/Reviewer 不會被當成可由 protected runtime 選擇的 harness。

## 2. Required protected composition 與檔案

Production Host 不註冊 synthetic trust。受控 application 明確注入 immutable `ExternalAdmissionInputs`、policy source、verified identity resolver、同一 SQLite v2 的 execution/approval/admission/effect coordinator、clock、trusted HTTP transport 與 OpenCodeOptions。Inputs 的預配 operation/run/execution/message IDs 由可信 composition 供給，須按新 operation 分配；不是 untrusted request 的 authority/scope 宣稱。此 primitive 不提供 multi-tenant identity/ID allocator。

- [Core RuntimeEnforcement.cs](../../src/MAF.Core/Policy/RuntimeEnforcement.cs)：窄 `IRuntimeEnforcementCoordinator.VerifyEffectAsync`、provider-neutral effect kinds；不暴露 SQLite transaction，不新建 orchestration framework。
- [SqliteApprovalStore.Runtime.cs](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Runtime.cs)：同一短 writer transaction 內驗 active tuple/canonical binding/verified caller/current control/ownership/full execution snapshot，必要時 CAS dispatch intent + required bounded audit。既有 Activate/approval/admission 邏輯未改。
- [ProtectedExternalRuntime](../../src/MAF.Workflows.External/ProtectedExternalRuntime.cs)：one owned run 的 access、operation-specific assessment、intent-store decorator；原 service 在寫 SessionCreationIntended/PromptIntended 時交給固定 coordinator。A1 在 ownership 下讀回 fresh record 後作最終分類，避免信任 advisory snapshots。全 protected call 使用一個 bounded cancellation budget，涵蓋 admission、reattach 與 observation。
- [RuntimeAccess.cs](../../src/MAF.Workflows.External/RuntimeAccess.cs)：internal per-effect composition protocol；public construction 唯一預設是 unavailable，不提供 `unsafe` flag。`AssemblyInfo.cs` 只對既有兩個 test assemblies 開 internal fixture injection；它不是 OS boundary。
- OpenCodeClient 的 AsyncLocal effect scope 連結每個 API 到 wire gate，包含 nested Reattach→Validate 與 pagination/retries；每次 HTTP 都重驗。Create/Submit 的 exact fingerprint/run/handle 不可改，recovery access 永遠拒絕兩者；method/path whitelist 無 shell、write、permission/question reply。HTTP request 使用已驗 options 的 absolute URI，重用同-origin HttpClient 不再重設已啟動的 BaseAddress。
- DurableExecutionService 只加 access、owned launch/inspect seam；保留原 WorkflowBuilder、InProcessExecution、Executor/PersistingClient、state transitions、terminal immutability、fingerprint、correlation、recovery-only 算法。沒有第二個 agent/tool loop 或 Workflow checkpoint engine。
- [SyntheticRuntime.cs](../../tests/MAF.ApprovalAcceptance/SyntheticRuntime.cs)、[helper Program](../../tests/MAF.ApprovalAcceptance/Program.cs)：test-owned agent trust、fake HTTP、process protocol；[RuntimeEnforcementTests](../../tests/MAF.Tests/RuntimeEnforcementTests.cs) 是新 focused suite。

`ExplicitV2` 才能給 coordinator 真 ownership；legacy v1 profile 仍拒絕 v2，protected composition 誤傳 legacy profile 不 fallback。本輪不動 schema/DDL/user_version/migration；[ApprovalSchema.cs](../../src/MAF.Persistence.Sqlite/ApprovalSchema.cs) 只加 internal fault checkpoints `DispatchIntentWritten/Committed`。沿用 Requested/null approval audit code，dispatch row version 對應 execution intent version，並保存 active epoch/version；沒有假 Approved row 或 persisted permit。

MAF guard 負責 admission 與 provider API effects；OpenCode 的 agent/session permission system 仍負責 harness 內的工具權限。MAF Allow 不能授予或證明內部 write/shell 權限，既有 deny-by-default read-only rules 與 provider permission checks 保持原樣。

## 3. Operation-specific authorization 與 ordering

| Effect | 所需 operation 與 durable evidence |
|---|---|
| Discovery Validate | 新工作用 Submit；recovery 用 Inspect。只准 health/doc/agent GET，不授泛用 provider 存取 |
| Create session | SubmitNewReadOnlyTask；Created→SessionCreating/SessionCreationIntended 與 auth/control/audit 同交易 commit；wire gate 要求 exact phase、無 session、same owner |
| Submit prompt | SubmitNewReadOnlyTask；ReadyToSubmit→SubmissionIntended/PromptIntended 同交易 commit；wire 再驗原 request fingerprint 與已存 session/message；GET session 與 prompt POST 均檢查 |
| Observe / Reattach / returned stored metadata | **InspectExistingExecution** 的獨立 descriptor/rule，綁原 task/run/execution/session/message/fingerprint/scope/current revision；每個 GET 重驗。Submit-only 可 Submit，但不能靠它 Observe；Inspect-only 不能 Create/Submit |
| Abort | 一律 NotSupported，0 Abort POST；取消 token、HTTP timeout、inspection timeout 不構成 RequestCancelExecution 授權 |

鎖順序保留 TaskId ownership → authority acquisition/lease（transaction 外）→ schema gate / SQLite IMMEDIATE transaction。交易內 callback 依 protected-authority 契約只做 local verification，沒有 network I/O。Revalidate 重用既有 evaluator/binding；檢查 supplied decision 有效期、authority/epoch/version/digest、精確 scope/config revision 與 caller authorization。Explicit-only activation 不變：candidate 未 active、rollback、digest conflict、source failure 均 fail closed，business/effects 不啟用 policy。

Intent 的 execution CAS + audit + active/control checks 全部 commit 或 rollback。Audit 滿額／注入 fault 不留下部分 intent；Created admission 可能已存在，必須仍 recovery-only，不能把 failed launch 當作新任務再送。

**SQLite commit 不與 HTTP atomic。** Intent commit 後每次 wire 做最後 preflight，再離開所有 DB transactions 送 HTTP。Policy/control 先於 intent commit → 拒絕；intent commit 後、wire preflight 前更新 → 此次 HTTP 被擋；最後 preflight 之後更新 → request 可能已送。測試明確展示 session POST 已進 fake transport 後 activation 仍可成功，不能撤回該 POST，但下一個 prompt intent 被擋。沒有 exactly-once、零 race window 或遠端 abort 承諾。

## 4. Crash 與 ambiguous delivery

| Window | Restart truth |
|---|---|
| intent transaction 內、commit 前 kill | intent/audit rollback；先前 Created 或 SessionKnown 原樣保留 |
| SessionCreationIntended commit 後、wire 前 kill | 無 session，Unknown/MissingSubmissionEvidence；不重建 continuation、不 Create replacement |
| SessionKnown、PromptIntended 尚未 commit | 可 Reattach 原 session，仍無提交證據，回 Unknown；不補送 prompt |
| PromptIntended commit 後、wire 前 kill | 只用原 session/message Inspect。可能未送也可能已送，原 intent 不授權 retry；fake evidence 不足時 Running/Unknown，0 prompt POST |
| Create response lost | POST 可能被接受但 session identity 未知 → Unknown，restart 不建另一 session |
| Prompt accepted / response lost | 保留原 message/session，SubmissionUncertain→獨立 Inspect reconciliation；本輪 fake 可觀察 Completed，不重送 |

Official Workflow 仍由原 builder 執行；graph restart 不能重建 owner-bound continuation。沒有加入 SDK checkpointstore/resume；execution ledger 仍是效果對帳依據，Workflow run ID 不是授權證明。

最後 focused run 的真 OS termination evidence（UTC；stdin/stdout gate、20 秒 driver wait、30 秒 child gate，只 kill captured PID，驗 StartTime）：

| Checkpoint / ordinal | Killed PID / startUTC | Restart PID | Commit 後保留 phase | Create / prompt / abort POST |
|---|---|---|---|---|
| DispatchIntentWritten / 1 | 45484 / `2026-10-16T07:58:21.4704251Z` | 11700 | None | 0 / 0 / 0 |
| DispatchIntentCommitted / 1 | 44116 / `2026-10-16T07:58:20.2956262Z` | 57072 | SessionCreationIntended | 0 / 0 / 0 |
| DispatchIntentWritten / 2 | 41188 / `2026-10-16T07:58:22.1213165Z` | 50320 | SessionKnown | 1 / 0 / 0 |
| DispatchIntentCommitted / 2 | 53292 / `2026-10-16T07:58:20.8455096Z` | 44944 | PromptIntended | 1 / 0 / 0 |

四例皆唯一 execution=1、`PRAGMA integrity_check=ok`、restart POST delta=0。Original/recovered IDs 為 `runtime-task / runtime-run / runtime-execution / msg_runtime`；前兩例 session=null，後兩例 session=`ses_runtime`。Fake provider state 與 **method + path** ledger 是 test-owned sidecars，不存 prompt/transcript/credentials；不是 runtime DB schema 或 production provider service。

正常 Allow 的 raw transport 帳目：GET health/doc/agent 各 1，POST `/session` 1，POST `/session/ses_runtime/prompt_async` 1，GET session 共 2，GET permission/question/status/message 各 1；其他 POST=0。不以包含 GET 的 adapter Requests 總數冒充 POST 計數。

## 5. Actual build/test results

Windows / .NET 10、Debug。Solution restore 成功（更新 helper 的既有 transitive Workflow assets，無套件版本變更）；solution build **0 warnings / 0 errors**。Focused discovery **37 entries**；最後 focused **37 executed / 37 passed / 0 failed / 0 skipped**。Full offline **530 executed / 530 passed / 0 failed / 0 skipped**。

最終 diff 已核對 scope；README、Roadmap 與本報告共 76 個 relative file links 解析成功，`git diff --check` 無錯誤。

比較：A2b 493 全部保留 + 新 37 = 530。歷史 2B-1 等 155、M1 98、M2-A persistence 51、M2-B/A2a authority 67、A1 62、A2b 60 全通過。Focused 已含在 full，沒有重複相加。TRX 在 ignored `TestResults/home-validation`。

| Focused group | Cases / assertion |
|---|---|
| Host modes | 3：external-task/recover/recover-pending 都不讀舊配置、不建 DB |
| 正常 Allow／Deny／RequireApproval | 3：official graph/原 IDs/一 execution/兩合法 POST；後兩種 execution=0、HTTP=0 |
| Operation separation | 2：Submit 不授 Inspect，Inspect-only 不授 Submit |
| 不支援／信任／profile | 6：forged identity、ephemeral、batch、cancel、config revision mismatch、legacy profile，無 fallback/HTTP |
| Public bypass/list | 1 case 逐一測 raw Validate/Create/Submit/Observe/Reattach/Abort、Workflow、durable Run/Recover/List、executor、protected List，全部拒絕 |
| Lost response | 2：Create lost 與 Submit lost；restart 沒有 replacement POST |
| Inspect cancellation/timeout | 2：Unknown、AbortRequested=false、Abort POST=0 |
| Activation/control race | 4：在 discovery 與 intent commit 後 barrier，更新可阻止下一 intent/wire |
| Protected raw misuse | 6：Abort、changed prompt/run、wrong handle、recovery Submit、disposed owner，無新增 HTTP |
| Intent rollback | 2：audit full／precommit fault，intent/audit rollback，POST=0 |
| Expiration | 1：discovery 後 policy 過期，不 advance intent |
| 最後 preflight window | 1：已送 session POST 無法撤回，下一 prompt 被擋 |
| 真 process crash | 4：上述四個 windows、原 IDs、integrity、unique row、0 restart POST |

歷史 regression fixture 的唯一改動是 constructor 注入 [HistoricalRuntimeAccess](../../tests/MAF.Tests/HistoricalRuntimeAccess.cs)（Tests assembly 內實作）。原成功、readonly、fingerprint、CAS、no-resubmit、Abort 舊行為 assertions 不變。它透過 internal constructors 驗歷史演算法，**不是 production fallback，也不是新 protected positive-path 證據**；新的 protected positives 只用真實 evaluator/TrustedPolicyAuthority/SQLite coordinator + fake HTTP。沒有修改或啟用 OpenCodeLiveTests；舊 live/Host acceptance driver 現在會因 default-deny 被擋，未重新認證 live 功能。

早期測試揭露兩個修正：共用 HttpClient 已開始後不可重設 BaseAddress；child helper 缺先前 no-restore 下未更新的 Workflow transitive runtime assets。修正 source／restore 後重跑，沒有放寬 process crash 或成功結果 assertions。沒有觀察到 endpoint-security alert，未執行企業 endpoint/live OpenCode/model，未更動公司安全事件；本輪不構成企業環境 clearance。

```powershell
dotnet restore MAF.slnx
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter FullyQualifiedName~RuntimeEnforcementTests
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~RuntimeEnforcementTests --logger 'trx;LogFileName=phase2b2-m3-a2c-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m3-a2c-offline.trx' --results-directory TestResults/home-validation
```

## 6. Compatibility、remaining bypasses、M3-B prerequisites

- D11 deliberate breaking change：舊 public constructors 保留 source shape，但執行不再具有隱含權限。Host external modes 明確停用，沒有環境變數／CLI unsafe opt-out。既有 v1 DB 不轉成 v2；schema2 只能經 explicit profile 存取。README/help/roadmap 已同步，歷史驗收文件不重寫。
- Supported protected composition 的 public external/raw/list entrypoints 已測 fail closed；**這不限制惡意同帳號程式直接發 HTTP、讀寫 DB／憑證、反射、載入冒名 friend assembly、替換 DI／HTTP handler 或改 binary**。信任 inputs、identity/source、store/coordinator、HTTP transport 都由可信 composition 提供；transport 必須維持正確 origin、無自動 redirect/retry POST 的部署邊界。本輪只有 fake transport，沒有聲稱完成 production transport/writer isolation。
- Native PCA/Reviewer、trusted storage reads/maintenance/activation primitives 仍可由 library caller 到達，未接 authenticated ingress 或全系統 policy。P10/R03 只在本文件列明的 external composition 有證據；R04/真人／writer／rollback isolation 仍 Blocked/Not Demonstrated。
- Production source/authentication/Broker、人類 fresh presence/exact approval、external anti-rollback/revocation anchor 均未部署；沒有 synthetic identity 加入 Host。Policy authority 仍 explicit-only，approval row 永遠不自動執行。
- M3-B 需另批 synthetic Pending/Decide/explicit Resume：重供原 input，重新驗 operator、policy/scope/control/ownership/expiry，atomic Consume+Created 後同 owner 一次性 handoff；existing 只能 Inspect。不能把既有 data-only Consume 回傳值或 Created metadata 當成 dispatch context。D12 generation replacement 仍 deferred，不擴 schema。
- M4 才定義獨立 RequestCancelExecution、durable delivery/ack/reconciliation；本輪只在 fake fixture 更新 control 驗 ordering，不發 provider Abort。Batch/list 與 ephemeral 沒有安全契約時繼續 Deny/NotSupported。

**M3-A2c controlled runtime increment 驗證完成；production activation 未完成。七項 guardrails、read-only harness permissions、2B-1 fingerprint／state machine／recovery invariants 保留。提交推送後停止，不自動開始 M3-B/M3-C/M4。**
