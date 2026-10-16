# Phase 2B-2 M3-A1：Provider-free application admission 結果

日期：2026-10-16（Asia/Taipei）。開發基準：`400f47f16071e11f77b2fea9d5bf843846ef78ab`；fetch 後 `origin/main` 相同。Tracked 工作樹開始時乾淨；既有未追蹤 `.vscode/` 保留、不提交。Owner 接受 [M3-0 review](PHASE2B2_M3_0_REVIEW.md) 為規劃基準，**只授權 M3-A1**。

本輪完成非執行性的 admission assessment 與 deterministic tests。沒有把 policy 接入 Host、既有 public execution APIs 或 provider adapter；沒有修復 M3-0 所列 runtime bypass。M3-A2/B/C/M4 及完整 Phase 2B-2 未完成、未因此獲得實作授權。

## 1. 實作檔案與依賴契約

- [ExternalExecutionApplication.cs](../../src/MAF.Workflows.External/ExternalExecutionApplication.cs)：一個 concrete application component，只有 `Assess`，沒有 launch/resume/dispatch API。
- [ExternalAdmissionTests.cs](../../tests/MAF.Tests/ExternalAdmissionTests.cs)：明確 synthetic agent identity、固定 clock、immutable policy/configuration/execution snapshots，以及 armed execution callback probes。沒有模擬真人核准或 production test fallback。
- 本結果文件。沒有改現有 source/test assertions、project dependencies、套件、Host routing/DI、stores、migration、Workflow、OpenCode client、fingerprint 或 durable transitions。

`ExternalAdmissionRequest` 是不可信 intent，包含 mode、TaskId、timeout、可選原 ExternalTaskRequest；不接收 subject、authority、epoch/version、connector/capability 或 AuthorizationScope。Admission DTO 的 top-level unknown JSON members 使用既有 .NET attribute 拒絕；tests 確認額外 authorityId/epoch/scope/capability/callerSubject 不能成為信任來源。此 schema 拒絕為 JsonException，不是執行授權；本輪未建立 Host JSON ingress 或例外輸出路徑。

`ExternalAdmissionInputs` 是由**可信 application composition** 提供的 immutable snapshot，不得從 ingress JSON、repo 設定或模型宣稱建構。它包含預期 authority/revision、provider、已驗 canonical resource scope、configuration/scope revisions、最大 budget、submit/inspect descriptors、預配 metadata IDs、immutable execution snapshots。元件以 trusted root 建 resource scope，request timeout 受 trusted maximum 與既有 bounds 限制，其他 scope/config 欄位不接受 caller override。Scope.ResourceId 必須已是 absolute normalized root；元件只做純 path normalization/比較，**沒有 filesystem/reparse 驗證或真 DB 查詢**。

Caller context 交由注入的既有 `ITrustedIdentityResolver` 驗證來源；同字串或僅實作 ITrustedCallerContext 不算認證。元件核對 resolver 回傳的 subject/context/kind/有效期，再凍結已驗 metadata 給 evaluator。缺 resolver 使用既有 `UnavailableTrust`，不是測試 fallback。Resolver/source 的真實可信來源仍是 injecting composition 的責任，這個 in-process class 不是 OS security boundary。

Policy 規則完全重用 [DeterministicPolicyEvaluator](../../src/MAF.Core/Policy/DeterministicPolicyEvaluator.cs)，operation digest 重用 [OperationBinding](../../src/MAF.Core/Policy/OperationBinding.cs)，request fingerprint 重用原 [DurableExecution.Fingerprint](../../src/MAF.Core/DurableExecution.cs)。沒有重建 policy matching engine。Policy source 每次 assessment 由 evaluator 讀一次，評估使用單一 clock instant；預期 authority/epoch/version 取自 trusted inputs，不自動採用來源的任意新 revision。非 Deny 的 canonical digest 還須與 application descriptor 一致，避免 policy descriptor 與預期 adapter/config binding 不符仍被當成同一操作。

## 2. 分類與結果

| Intent / snapshot | 分類與行為 |
|---|---|
| Submit，TaskId 不存在 | NewSubmission → SubmitNewReadOnlyTask，預配 run/execution/message metadata，沒有 session |
| EphemeralSubmit，TaskId 不存在 | EphemeralSubmission → 同一 submit policy；RequireApproval 仍只回 assessment，不退回任何 execution fallback |
| Submit/EphemeralSubmit，TaskId 已存在 | 原 input fingerprint 必須一致；改為 ExistingInspection，使用原 run/execution/session/message IDs 與獨立 InspectExistingExecution policy |
| Inspect，TaskId 已存在 | ExistingInspection；不要求重供 prompt，綁原 fingerprint/correlation；不存在則 InvalidRequest |
| BatchInspect | BatchInspection，固定 Deny / NotSupported；缺 global list authorization contract，不枚舉或披露 execution snapshots |
| Cancel、unknown mode、write/unsupported external policy | Deny；沒有實作取消、write/shell、provider permission reply |

`ExternalAdmissionResult` 回 typed classification、既有 `PolicyDecision`，以及非 Deny 時的 derived `PolicyRequest` metadata。Allow / Deny / RequireApproval 都是**非執行性評估結果**，沒有 delegate、owner、continuation、session factory 或 dispatch ticket。RequireApproval 不寫 Pending；Allow 不授予呼叫 legacy provider APIs 的權限。

穩定 reason codes 重用既有 enum，例如 Allowed、ExplicitDeny、ApprovalRequired、InvalidCaller、UnknownOperation/Capability、NotSupported、ScopeMismatch、BindingMismatch、InvalidRequest、PolicyUnavailable/InvalidPolicy、AuthorityMismatch、PolicyEpochMismatch、PolicyVersionMismatch、EvaluationError。Dependency/clock/malformed snapshot 例外不回傳任意文字，也不產生 Allow。Deny 不附回原 execution correlation；非 Deny 的期限不超過 policy decision deadline 或已驗 identity deadline。

## 3. 實際 validation

環境：Windows / .NET 10。以下最終命令皆 exit 0；未觀察到 endpoint-security alert。沒有實際 provider POST、live model/OpenCode session 或正式 DB 作業。Full offline 仍執行原先的 synthetic SQLite/process/fake-provider tests；它們不是新 M3 runtime enforcement 證據。

| 檢查 | 真實結果 |
|---|---|
| Solution build（Debug、no-restore） | 9 projects；**0 warnings / 0 errors** |
| Focused discovery | **62 entries**；inline cases 已展開 |
| M3-A1 focused | **62 executed / 62 passed / 0 failed / 0 skipped** |
| Full offline，排除 OpenCodeLiveTests | **409 executed / 409 passed / 0 failed / 0 skipped** |
| 與 M2-B 347 baseline 比較 | **原347全部通過 + 新62 = 409**；focused 已含於 full，不另相加 |
| 原 M1 | PolicyTests 67 + OperationBindingTests 31 = 98 passed |
| 原 M2-A / M2-B | ApprovalPersistenceTests 51 + TrustedAuthorityTests 43 = 94 passed |
| 2B-1 等既有 offline | 原155全部通過；未修改其 assertions 或 runtime 行為 |

命令：

```powershell
dotnet build MAF.slnx --no-restore --configuration Debug
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~MAF.Tests.ExternalAdmissionTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~MAF.Tests.ExternalAdmissionTests' --logger 'trx;LogFileName=phase2b2-m3-a1-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m3-a1-offline.trx' --results-directory TestResults/home-validation
```

TRX counters 與按 class 分組已核對：62 + 67 + 31 + 51 + 43 + 155 = 409。Raw TRX 留在 ignored TestResults，不提交個人路徑。沒有重跑 2B-1 controlled Host/live acceptance runner，也沒有 M3 OS-process admission/dispatch 驗收。

### Callback 零呼叫的證明

Test boundary 同時實作 read-only policy/identity interfaces 與 IExternalHarnessClient、IExecutionStore、IPolicyActivationCoordinator。Application **只收到前兩種介面**；同一 test object 上的 Validate/Create/Submit/Observe/Abort、owner acquisition、DB read/list/insert/update、activation callbacks 全部設為 counter + throw traps，另有 Workflow/approval persistence traps。每次 assessment helper 的 finally 都 assert 所有 counters = 0，即使 component catch 例外也不能讓誤呼叫藏成綠燈。

Constructor/result shape tests 另驗沒有 provider/store/Workflow/delegate 依賴或可執行結果；code review 確認新元件沒有這些呼叫或檔案寫入。三種 outcome、new/existing/ephemeral、各種否決都經相同零效果檢查。Workflow/approval traps 不是聲稱接入了真 Workflow/store，而是與無 execution dependency 的結構檢查一起驗證「assessment 不執行」。

Focused cases 包含：exact-scope Allow、ExplicitDeny、RequireApproval；缺/同字串偽造/過期/未生效/錯 subject/context/unknown kind 身分；缺失/過期/無效/故障 policy；authority/revision/capability/connector/scope/config/adapter/provider mismatch；原 input/IDs/session 重綁拒絕；existing Inspect 與 Submit 的獨立規則；budget/readonly/invalid request；batch fail closed；ingress trust claims schema 拒絕；Deny 不披露原 IDs；identity/policy deadline 收斂；duplicate metadata ambiguity；determinism。

## 4. 已驗不變量與限制

- 三種 decision 不造成 provider、DB、Workflow、approval persistence 或 activation 活動。沒有 authority Acquire/Pin/Activate call；**既有 SqliteApprovalStore.PinAuthority 的 implicit activation 完全未改**，D02 的 explicit-only runtime 實作留 M3-A2。
- New/existing 分類只依可信的 immutable metadata snapshot，沒有 TaskId ownership、reservation 或 CAS。Snapshot可能過期，預配IDs的唯一性/保留亦由未來 owner/admission 邊界負責；A2必須在owner下重讀。不能把這個結果當為 Created 或 permission。
- Batch 僅能分類、尚不能授權list；預設拒絕是明確限制，沒有暗增 broad metadata 權限。Deny 雖不回原IDs，分類仍是 assessment metadata；A2在實際讀取前還需資料授權與scope過濾。
- Trusted inputs/source/resolver由可信程式注入，不代表已部署可信 production source、real human proof、writer isolation 或 anti-rollback anchor。Test identity 是 agent fixture，沒有 operational human approval。
- Host routing/DI、ExternalWorkflow、DurableExecutionService、OpenCodeExecutor/client、stores、schema/migration、fingerprint、recovery、provider permissions **都未修改**。原 public/raw entrypoints 的 policy bypass、recovery token→Abort 路徑仍存在；P10/R03/G1 等 runtime gates **未通過**，不以62個provider-free tests取代。
- D12 same-TaskId approval-generation replacement繼續 deferred，沒有改schema或聲稱支援。Production Broker/activation/humanapproval仍disabled；七項guardrails、獨立PCA/Reviewer與官方Workflow不變。公司安全事件及歷史報告未修改。

## 5. M3-A2 prerequisites 與 blocker

本輪 scope 沒有阻止交付的 blocker。下列是 **A2/C 的未解實作／部署前提**，不是本輪可順帶完成的工作：

1. 實作 owner 已採納的 D02 explicit-only activation：普通 pin/admission verify-active，不advance；current candidate/active digest不一致、source unavailable、revocation/rollback、audit與multi-instance排序皆有明確fail-closed行為。現有fixture可改明確activation setup，但不能弱化既有assertions。
2. 實作 D11 的完整 protected entrypoint與wire guard：缺context default-deny、無unsafe fallback，明確處理legacy Host/API相容性；包含direct/raw/ephemeral/batch/recovery/cancellation，不能只包CLI。沒有此證據就不能聲稱修完 bypass。
3. 建立窄 atomic Allow admission / dispatch-intent coordinator、TaskId owner驗證、consume/newCreated後一次性 internal continuation，以及explicit v2 execution read/update。仍不得automatic migration或重送existing execution；這些都是A2或後續範圍，A1沒有任何hook執行它們。
4. 實際canonicalroot/reparse/config provenance、scope budget與資料讀取授權、批次list契約、fresh trusted source/identity的取得與revalidation需在接入前確立。Snapshot分類不能代替owner下檢查。
5. 真human proof、Broker transport/secret/writer isolation、durable consent evidence與external anti-rollback仍屬Blocked production trust。M3-B/C/M4需各自授權；D12不擴張。

**結論：M3-A1 provider-free assessment 已實作並通過62 focused／409 offline與solution build；不代表M3-A2 runtime或完整Phase2B-2驗收。驗證並提交推送後停止，等待owner對M3-A2的明確授權。**
