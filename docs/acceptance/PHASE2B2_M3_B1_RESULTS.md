# Phase 2B-2 M3-B1 — Durable Approval Request & Decision

日期：2026-10-16（Asia/Taipei）。基準 `c5a436fb798b9f827a2a915e9f1aa82de33243a9`，main / fetch 後 origin/main 相同；起始 tracked tree 乾淨，既有 `.vscode/` 不讀取、不提交。本輪只實作 B1，沒有 Resume、approval-derived continuation、production human authentication 或 provider dispatch。

## 實作與責任

- 新增 [DurableApprovalApplication](../../src/MAF.Workflows.External/DurableApprovalApplication.cs)，一個獨立、非執行性的 application entrypoint，只有 `RequestAsync` / `DecideAsync`。重用 [A1 assessment](../../src/MAF.Workflows.External/ExternalExecutionApplication.cs)、既有 Core contracts、[TrustedPolicyAuthority](../../src/MAF.Core/Policy/TrustedAuthority.cs) 與 [SqliteApprovalStore](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs) 的 Request / Decide 交易。
- [SyntheticApprovalApplication](../../tests/MAF.ApprovalAcceptance/SyntheticApprovalApplication.cs) 只存在 test executable：以 reference-verified caller/operator context 與固定 clock，透過**真實 TrustedPolicyAuthority adapter**提供 synthetic HumanOperator/FreshHumanPresence。不是靠 subject 字串比對代替認證，也不是 production human proof。
- [Test helper Program](../../tests/MAF.ApprovalAcceptance/Program.cs) 增加兩個 test-only 子命令：application request commit 後等候 driver；fresh process 以相同 exact request 驗 idempotent persisted Pending。
- 新增 [DurableApprovalApplicationTests](../../tests/MAF.Tests/DurableApprovalApplicationTests.cs)。沒有修改既有測試、Host composition、A2c protected runtime、Workflow、provider adapter、Core fingerprint/state machine、任何 store 交易、schema 或 migration。

這是**明確的 approval request API**，不是改寫 A2c `RunAsync`。A2c 的 direct-Allow 路徑完全保留；Run 遇 RequireApproval 仍拒絕執行，呼叫端可另行明確呼叫 RequestAsync。沒有隱含持久化、等待核准後自動繼續、背景消耗或 operational approval CLI。Host external modes 仍 `ProductionTrustUnavailable`。

## Request / decision 語意

Request 先由受控 resolver 驗證 caller provenance、subject/context、kind、有效期；缺 resolver 使用既有 UnavailableTrust。接著驗 bounded durable Submit intent，取得 TaskId ownership、fresh read execution；已存在 execution 一律 recovery-only，不重綁為新 approval。以 trusted inputs 衍生 scope/configuration/connector/authority/revision 與原 fingerprint，A1 assessment 必須恰為 NewSubmission + RequireApproval，才呼叫既有 store RequestAsync。Allow / Deny / ephemeral / inspect / batch 不走此 API 建 Pending。

Store 於 transaction 外取得 authority lease；transaction 內重驗 durable active tuple、current RequireApproval、exact binding、verified caller、control/cancellation、initial-operation uniqueness 與 required audit，原子保存 operation/control/Pending。**Publication 不等於 activation**，沒有 business-triggered activation。ownership 在 Request 返回前釋放，不會在等待人工決定期間持有。

Decide 先驗 verified、fresh HumanOperator + FreshHumanPresence；此處只是 preliminary authentication。Store 在 authority lease / SQLite writer transaction 內**再認證**，對 stored operation、binding digest、required operator scope 做 exact authorization、非 self、有效期、Pending/version CAS、cancellation 與 audit 檢查。回傳 Approved 或 Rejected metadata，沒有建立 execution、消耗 approval、mint continuation 或呼叫 Workflow/provider。

沿用原冪等性：同 ID、同 request binding 回原 row；同 DecisionId/choice/operator/digest 回原 decision，不新增 audit、不產生新權限。不同 choice、stale version、binding mismatch 或互斥決定不能蓋掉 winner。Request 的重放可能回原本已決定的 status，並不保證重建新的 Pending。Expired approval 的**新**決定拒絕；原 store 對已成功 exact decision 的歷史 idempotent response 語意不變，不把它當 fresh authorization。過期 row 可仍標 Pending，直到既有 Expire 操作投影狀態；B1 沒有新增過期排程。

## 零執行與信任邊界

- Application constructor 不接收 provider、HTTP client、Workflow callback 或 admission/launch coordinator；public result 只有 ApprovalRecord。Source review 確認沒有 Consume/Resume/activation/Workflow/provider 呼叫。
- Positive cases 注入的同一 boundary object 同時實作 execution store、approval coordinator、provider callbacks。Execution Create/Update/List、Consume、provider Validate/Create/Submit/Observe/Abort 與額外 Workflow probe 均設 counter + throw，測試明確 assert 全為 0，並核對 execution table count=0。Workflow probe 搭配 source/constructor-shape 檢查，不冒充已接入並跑過一個真正 graph。
- Approve 與 Reject 後，另將該 synthetic RequireApproval policy 交給原 A2c runtime：即使 row 寫 Approved，Run 仍拒絕；fake HTTP raw method/path requests 為空，provider POST=0。沒有用改 policy 為 Allow 讓測試過關。
- Production 沒有 synthetic identity/resolver fallback；缺、偽造、過期／未生效、agent、無 fresh presence、self 或已撤權 operator 均被擋。Application 前置驗證後身份才失效，也由 store 的 transaction-time authority 再驗擋住。
- 注入的 source/resolver/store/coordinator/inputs 仍是可信 composition 前提，使用同一 synthetic SQLite v2 與 explicit execution-store profile。這不是惡意同帳號程式、任意 DI、直接 DB/HTTP、reflection 或 friend assembly 的安全隔離。沒有新增 authenticated metadata listing 或真 operator consent UI。

## 實際 build / test

Windows / .NET 10、Debug。最後 focused discovery **30 entries**；focused **30 executed / 30 passed / 0 failed / 0 skipped**。Full offline **560 executed / 560 passed / 0 failed / 0 skipped**。Solution build **0 warnings / 0 errors**。

最終文件 scope/whitespace diff 已檢查；README、Roadmap 與本報告共 63 個 relative file links 解析成功。

比較：A2c 的 530 全部通過 + 新 B1 30 = 560，focused 已包含於 full。原 155 baseline、M1 98、M2-A persistence 51、authority 67、A1 62、A2b 60、A2c runtime 37 均保留通過；沒有改歷史 assertion。沒有執行 live OpenCode/model、企業 endpoint 或實際 operational approval。未觀察到 endpoint-security alert，未更動企業安全事件記錄；家用測試不是企業 endpoint clearance。

| Cases | 驗證 |
|---|---|
| 2 | RequireApproval→exact Pending；synthetic authorized Approve/Reject→version 1、audit、原 IDs/binding、0 execution/Workflow/provider POST；Approved 不會讓原 runtime 自動執行 |
| 8 | missing/forged/expired/future/self/agent/no-presence/revoked operator；原 Pending 不變 |
| 1 + 1 | expired approval 新 decision 拒絕；missing resolver 無 fallback |
| 1 | existing execution 不能被變成新 Pending，原 snapshot 保留；僅使用既有 synthetic offline migration fixture，沒有新增 migration |
| 1 + 1 | request/decision exact duplicate idempotency、stale CAS/choice/digest conflict；competing Approve/Reject 只有一個 winner |
| 2 + 5 | Allow/Deny 不建 Pending；missing/forged requester、ephemeral/inspect/batch 不落入 approval fallback |
| 4 | preliminary auth 後、decision transaction 前，cancel / explicit activation / newer unactivated candidate / identity expiry 均 fail closed |
| 1 | assessment 後、Request transaction 前 activation 先 commit，舊 request 不能建 Pending |
| 1 + 1 | decision 先 commit 後 activation：保留歷史 Approved，但不能 replay 舊 revision 授權；decision 先寫入持 writer，後續 cancellation 將 Approved 撤銷，仍無執行 |
| 1 | application Pending 在真實 writer process abrupt termination 後，由 fresh process/application/authority 讀回同 row；唯一性、audit、integrity 驗證 |

最後 focused run 的跨程序 evidence：

- Writer PID **12804**，start UTC `2026-10-16T08:18:46.9416076Z`；觀察 `application-pending-committed`、確認 PID/StartTime 未變且尚未退出後，只 `Kill(entireProcessTree:false)` 該 captured helper。
- Fresh process PID **55784**；透過 application idempotent Request 讀回，完整 ApprovalRecord equality、status Pending / version 0，approval rows=1、audit rows=2（activation + request）、execution rows=0、Workflow=0、provider POST=0、`integrity_check=ok`。
- 原始／恢復 correlation：approval `app-approval`、operation `runtime-operation`、task `runtime-task`、run `runtime-run`、execution identity `runtime-execution`、message `msg_runtime`；這些 execution identity 只是 Pending operation metadata，**沒有 execution row 或 session**。
- stdout/stdin driver gate 有 10–30 秒 bounded timeout；只操作每個測試自行建立的 synthetic DB/目錄。Pending snapshot 不含原 prompt；marker assertion 已通過，沒有 credentials 或 provider transcript 落庫。

```powershell
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter FullyQualifiedName~DurableApprovalApplicationTests
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~DurableApprovalApplicationTests --logger 'trx;LogFileName=phase2b2-m3-b1-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m3-b1-offline.trx' --results-directory TestResults/home-validation
```

Raw TRX 留在 ignored `TestResults/home-validation`，本文件保存非敏感 counters/PID evidence。A2c 的真 dispatch-intent crash tests 也在 full offline 通過；本輪 process test 證明的是 Pending persistence，不是 approval Resume 或 launch crash gate。

## M3-B2 未解前提與停止點

1. 必須另行授權 **explicit Resume**。重新提供原 input（不在 DB 保存 prompt），重新驗 fingerprint/scope/config/current policy/operator rights/expiry/cancellation/control；缺 input 或過期 binding 不能重送。已存在 execution 只能 Inspect/recover，不得再 consume/Submit。
2. 既有 ConsumeAsync 是 data-only、沒有 ownership/launch continuation。B2 需設計窄、同 owner 的 atomic Consume+Created→一次性 handoff，不能直接呼叫 RunAsync(existing Created) 期待送 prompt，也不能重建 token。
3. **A2c effect guard 只接受 fresh Allow；RequireApproval + Approved row 仍不足以 dispatch。** B2 必須定義受消耗 approval 的 exact authorization 如何接入每個 effect guard、綁原 operation/operator/control/current revision，不能暫時改 policy 為 Allow、接受 raw Approved metadata 或繞開 guard。
4. 補 approval consumption／handoff／dispatch-intent 前後的真 process crash、競爭及 ambiguous delivery 驗證；沿用 no-resubmit、Unknown、原 IDs 與原 session/message，不新增自動 retry 或 replacement。
5. D12 same-TaskId approval-generation replacement 仍 deferred，existing unique operation/schema 不變；不聲稱 stale approval 能在相同 TaskId 自動換代。Production human proof、Broker、writer/secret isolation、anti-rollback/revocation anchor 仍未部署，B2 synthetic evidence 不代替 M3-C。

**B1 申請／決定增量完成；B2 Resume 尚未實作。沒有新 schema/migration、Registry、Hybrid、permission reply、Abort 或 live call。驗證並提交推送後停止，等待 owner 對 B2 的明確授權。**
