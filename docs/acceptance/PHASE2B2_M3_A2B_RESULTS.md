# Phase 2B-2 M3-A2b — Atomic Admission results

日期：2026-10-16（Asia/Taipei）。基準 `4a1a46ed24f59dd69af395fb9ce9dd6f536ffe06`，main / origin/main 同步；起始 tracked tree 乾淨，既有未追蹤 `.vscode/` 不納入提交。本輪只交付 provider-free admission foundation，沒有 M3-A2c runtime enforcement。

## 實作與檔案

| 檔案 | 實作 |
|---|---|
| [Core/AtomicAdmission.cs](../../src/MAF.Core/Policy/AtomicAdmission.cs) | 固定 `IExecutionAdmissionCoordinator.AdmitAllowedAsync` 與 sealed `AdmissionContinuation`；不新增通用交易框架，不暴露 SQLite transaction |
| [Core/AssemblyInfo.cs](../../src/MAF.Core/AssemblyInfo.cs) | 只授予 persistence assembly internal continuation constructor；Host、application、測試不能直接建立 continuation |
| [AtomicAdmissionApplication.cs](../../src/MAF.Workflows.External/AtomicAdmissionApplication.cs) | 重用 M3-A1 assessment／trusted identity resolver／trusted application inputs，建立精確 Created metadata，再呼叫 coordinator；未註冊 production composition |
| [SqliteApprovalStore.Admission.cs](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Admission.cs) | direct Allow 固定原子交易；重用 evaluator、canonical binding、authority lease、active tuple checks、control、audit 與既有表 |
| [SqliteApprovalStore.cs](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs) | 只改為 partial 並實作新介面；既有 Activate / Request / Decide / Consume / Revoke / Cancellation 內容不變 |
| [SqliteExecutionStore.cs](../../src/MAF.Persistence.Sqlite/SqliteExecutionStore.cs) | 明確 LegacyV1 / ExplicitV2 profile；真實 ownership 的 admission use lease，阻止交易／continuation consumption 途中釋放 OS lock |
| [ApprovalSchema.cs](../../src/MAF.Persistence.Sqlite/ApprovalSchema.cs) | 只附加 internal `AdmissionCommitted` fault checkpoint；DDL、schema version、migration 均未改 |
| [SyntheticAdmission.cs](../../tests/MAF.ApprovalAcceptance/SyntheticAdmission.cs)、[Program.cs](../../tests/MAF.ApprovalAcceptance/Program.cs)、[helper project](../../tests/MAF.ApprovalAcceptance/MAF.ApprovalAcceptance.csproj) | 沿用最小 test executable，加入 admission/crash 子命令與 test-only reference-verified agent fixture；新增對 application project 的測試相依 |
| [AtomicAdmissionTests.cs](../../tests/MAF.Tests/AtomicAdmissionTests.cs) | 60 個 deterministic focused cases，包括受控 OS process termination；只使用每個測試自建的 synthetic SQLite DB |

閱讀基準：[M3-0 review](PHASE2B2_M3_0_REVIEW.md)、[M3-A1 results](PHASE2B2_M3_A1_RESULTS.md)、[M3-A2a results](PHASE2B2_M3_A2A_RESULTS.md)。實際 source contracts 為實作依據。

## 交易與 ownership invariant

1. 先驗證 ownership 必須是 SQLite store 內部實體、ExplicitV2、同 canonical database path、同 TaskId、尚未 disposed。自製 `IExecutionOwnership` 或 legacy profile 不被接受。持有 use lease 到 commit 與 continuation 建立完成；同一 owner 的並發 admission 也會序列化。Dispose 必須等待 use lease。
2. 在 SQLite transaction **外**取得 protected authority lease／verified candidate。交易內的 snapshot、身份與 exact operation authorization callbacks 依既有 protected authority 契約必須是 local，不允許 network I/O。未新增 trusted production source、身份 fallback 或 activation interface。
3. 一筆 `BEGIN IMMEDIATE` writer transaction，沿用 schema shared gate、Pooling=false、5 秒 busy timeout、synchronous FULL。先重查 durable active authority/epoch/version/digest；候選不是 active 時拒絕，絕不隱式啟用。
4. 已存在 execution（包含 Created、Unknown、terminal）一律 `ExistingExecutionRecoveryOnly`，不回傳新的 continuation。不可用舊的 advisory application snapshot 決定重新提交。
5. 使用既有 evaluator `Revalidate` 驗精確 operation binding、caller、scope、configuration revision、fingerprint、epoch/version、Allow/Allowed 與有效期，再由 protected authority `CanControl` 重新驗身份來源及精確權限。要求 SubmitNewReadOnlyTask、generation=1、無 session；重算**原樣未改**的 2B-1 fingerprint，驗 request root/timeout/read-only 與原始 Created/run/execution/message/provider identity。
6. 重查 task control：缺 row 時 expected version 必須 0；既有 row 必須 execution identity/version 完全相同且無 cancellation。operation ID、initial TaskId operation uniqueness 與 execution primary key 同交易保護。已有 pending initial approval 也不能被 direct Allow 取代（D12 仍 deferred）。
7. 原子寫入必要 control、authorization operation、Created execution、bounded audit，再驗有效期／identity／binding 與 active policy。任何 audit capacity 或 transaction failure 都 rollback；不留下部分 control/operation/execution。
8. 交易成功 commit 後才建立 continuation；commit 後至 mint 前當機也只留下 Created。Direct Allow **不建立任何 approval row**，不走 Approved/Consume 生命週期。

沿用 schema v2 的 `ControlAuditCode.Requested`：本操作 `approval_id=NULL`，與 pending approval 的非空 approval ID 區分；audit 記錄 actor/task/version/active epoch/version，operation 表記 operation/execution identity。沒有擴充 audit enum 或 schema。這是准入紀錄，不能當作 persisted dispatch grant 或完整可重建 authorization token；未保存 prompt、credentials、provider transcript。

## Continuation 與 schema profile

- Continuation 無 public constructor、factory 或 restore API，沒有 persisted representation；System.Text.Json 的 write/read 都明確拒絕，沒有 Serializable attribute。一般受信任程式碼不能從 execution metadata 重建它；這不是抵抗同程序反射／任意記憶體修改的 sandbox。
- 消耗要求原本 ownership **物件實體**仍存活，且仍綁定該 DB/TaskId；並發消耗只有一次成功。錯誤 owner、disposed owner、釋放後重取鎖都不能 replay。資料庫不保存 continuation。
- `ConsumeAsync` 僅回傳已 commit 的不可變 metadata handoff，**不是 provider dispatch ticket**。commit 後 policy/control 可改變；本輪不授權任何 provider side effect。A2c 必須在 effect boundary 再驗證，不能把 token 的存在視為當前權限。
- 原 constructor 保持 LegacyV1：只能原有 v0 初始化／v1，仍拒絕 v2。ExplicitV2 使用 ReadWrite，只接受已存在 v2，拒絕缺檔、v0/v1/newer；不 CREATE／migrate／改 user_version。沿用既有 path/reparse-point 檢查。明確 v2 `CreateAsync` 拒絕 `AtomicAdmissionRequired`，只供 read/list/update；Update 原樣使用完整 prior snapshot + CAS + state transition validation，包含 terminal immutability。
- 此 profile 是受控 library opt-in，不是 production activation 或 runtime authorization 修補。既有 approval consumption 仍是獨立 data-only API；本輪沒有替它新增 continuation 或修改既有語意。

## 實際驗證

Windows / .NET 10，Debug。最後一次 focused discovery **60**；focused **60 executed / 60 passed / 0 failed / 0 skipped**。完整 offline **493 executed / 493 passed / 0 failed / 0 skipped**。Solution build **0 warnings / 0 errors**。

歷史 A2a 433 全部保留通過 + 新 60 = 493；M2-B 的 347 基準到本輪另含 A1 62、A2a 24 與 A2b 60。沒有將 focused 重複相加，沒有修改任何歷史測試 assertion。

| 驗證 | 證據／測試 |
|---|---|
| Direct Allow 與 one-use | `ApplicationCommitsExactCreatedWithNoApprovalAndSingleUseOwnerBoundContinuation`；精確 Created/None/v0、approval count=0、audit current tuple、不可 JSON serialize/restore |
| 同 owner 並發 | `SameOwnerConcurrentAdmissionAndContinuationConsumptionHaveSingleWinner`：一筆 Created、一個 continuation、一次 consumption |
| 身份與 binding | 18 個 `InvalidBindingsFailWithoutWrites` cases；application missing／同字串但 forged identity；真實 TrustedPolicyAuthority adapter 未安裝 identity/authorization 時仍 `CallerNotAuthorized` |
| Policy fail closed | unactivated / newer candidate / expired / same-revision digest mismatch / unavailable；沒有 implicit activation |
| Activation race | activation 先 commit → stale admission `AuthorityRollback`；admission 持 writer 先 commit → activation 在其後提升 active，原 Created identity 不變 |
| Control race | test-owned SQL control CAS 在 admission 等 authority 時先 commit → `ControlConflict`、無 Created；admission 持 writer 時 control writer 在其後 commit → 一筆 Created + sticky cancellation version=1。這是持久 control ordering 驗證，未新增 production cancellation API／abort |
| Ownership | fake/disposed/wrong-task/wrong-db/legacy 全拒絕；交易中 Dispose 等待，後續 token 失效；換 owner 不能 replay |
| Rollback / audit | ExecutionInserted、BeforeCommit 注入失敗全部 rollback；AdmissionCommitted 注入失敗留下 Created、沒有 token；audit capacity=1（activation 已用滿）阻止 admission |
| Existing execution | restart 一律 recovery-only；既有 DurableExecutionService 對無 session Created 恢復為 Unknown/MissingSubmissionEvidence，原 IDs 不變、WorkflowEvents 為空、Prepare/Validate/Create/Submit/Observe/Abort/Reattach traps 全部 0 |
| Approval separation | Deny/RequireApproval 不准入；既有 Pending 不能被新版 Allow 偷換 generation，不會 consume／approve |
| Profile/state | v0/v1/v3/v99 原樣拒絕、不改 schema；缺 DB 不建檔；legacy 拒 v2；v2 direct Create 拒絕，update stale CAS／identity mutation／terminal mutation 均拒絕 |

實際子程序 evidence（最後 focused run；時間為 UTC，PID 只指該次 test-owned dotnet process）：

| 場景 | PID / startUTC | 結果 |
|---|---|---|
| 同 TaskId 競爭 | owner **45312** / `2026-10-16T07:33:16.2111267Z`；contender **51860**；restart **9168** | owner-acquired → competitor owner-busy → admitted-committed → kill owner → restart owner-acquired/recovery-only；control/operation/execution 各 1，approval 0，integrity=ok |
| commit 前 kill | **54052** / `2026-10-16T07:33:16.6775020Z`，ExecutionInserted | 尚未 commit 的 control/operation/Created/audit 全回滾；釋鎖後重新准入成功，沒有 pre-crash continuation |
| commit 後、mint 前 kill | **7216** / `2026-10-16T07:33:16.8944243Z`，AdmissionCommitted | Created/None/v0 保留；重取 owner 後再次 admission 拒絕 recovery-only，沒有 continuation 重建，integrity=ok |

原始／恢復 IDs 一致：`task-admit / run-admit / execution-admit / message-admit`，session=null。每次 kill 都先確認已觀察到協定 checkpoint、captured StartTime 未改且 process 未退出，再 `Kill(entireProcessTree:false)`；不搜尋或終止其他 Host。使用 stdin/stdout gate 與 5–30 秒 timeout。Windows exit notification 後若 OS lock handle 尚在 teardown，最多 5 秒 bounded polling，不能以 fake owner 繞過；最早測試暴露此時序問題後已修正 fixture，同時保留真實 OS ownership 要求。

所有新 admission/crash cases provider POST=0：沒有 HTTP/provider client 或 Workflow launch 依賴可供這些路徑呼叫；這是 provider-free foundation evidence，不是 live request accounting，也不是既有 runtime policy bypass 已修復的證據。完整 offline regression 使用既有 synthetic providers，不發出 live model/OpenCode 請求。本輪未觀察到 endpoint-security alert，未改企業安全事件記錄；家用測試不是企業 endpoint clearance。

可重現命令（TRX 在 ignored `TestResults/home-validation`，本文件保存非敏感 evidence）：

```powershell
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter FullyQualifiedName~AtomicAdmissionTests
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~AtomicAdmissionTests --logger 'trx;LogFileName=phase2b2-m3-a2b-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m3-a2b-offline.trx' --results-directory TestResults/home-validation
```

## 限制與 A2c 前提

- Host routing/DI、Workflow、OpenCode adapter、durable state machine、fingerprint、existing snapshots 全未修改；production Broker、activation control、人類 approval、write/shell、permission reply、provider abort 持續未啟用。新信任 fixture 只在 test projects。
- 本輪只原子准入新的 durable read-only Allow。Ephemeral、batch、cancel 不可用此 API 建立 execution；既有 TaskId 只能走既有 recovery 語意，不能補發 prompt。D12 same-TaskId approval replacement 未實作。
- DB / filesystem writer isolation、trusted source 與防 rollback 外部 anchor 仍是未解 production 前提；同帳號 Agent 不會因此成為人類 operator。ExplicitV2 profile 不會替公開 runtime API 增加 policy enforcement。
- A2c 需另行授權：將 protected runtime APIs、ephemeral/durable/recovery/list entrypoints 與每個 provider side-effect boundary 納入 default-deny；在 ownership 下消耗 handoff 並重新驗當前 policy/control、精確 operation 與 dispatch-intent checkpoint；決定失敗／當機後的 recovery-only 行為，不得重建 continuation／自動 submit。不得把 approval consumption 的資料結果或 persisted Created 當成 dispatch permit。
- 沒有新增架構 blocker 或 schema 需求；M3-A2b isolated foundation 通過本輪驗證。**不代表 A2c、M3-B Approval/Resume 或 M3-C production trust 已完成。提交後停止，不自動進下一階段。**
