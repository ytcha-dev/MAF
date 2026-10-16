# Phase 2B-2 M2-B：Trusted Authority Foundation 結果

日期：2026-10-16（Asia/Taipei）。開發基準 `96ebffd`（已同步遠端 architecture guardrails）。Owner 本輪只授權 M2-B；本報告接受的是合約、序列化與 synthetic persistence 驗證，**不是 production authentication、M3 或完整 Phase 2B-2 驗收**。

## 三個問題的答案

| 問題 | 已建立的邊界 | 目前部署答案 |
|---|---|---|
| 誰代表可信 Policy Authority？ | Trusted composition 固定 SourceId/AuthorityId；`ITrustedPolicyAcquisition` 從受保護來源取得 immutable snapshot 與 digest；adapter 驗證 identity、內容、期限及 monotonic revision；SQLite coordinator 是 durable activation owner | **尚未安裝可信 production source，因此正式 activation disabled。** SourceId/digest 相符只是內容一致性，不能認證來源；repo/CLI/request 不得供給 authority |
| 誰是真正人工核准者？ | `ITrustedIdentityResolver` 驗證 transport-owned context；`IApprovalAuthorization` 對 exact operation/resource/binding/operator scope 授權；human kind、fresh presence、期限、非 self-approval 均必要 | **本機同帳號的人與 Agent 尚無可靠區分機制，正式 approval disabled。** SID、`--actor`、同字串 context、讀取權限或 Approved row 都不足；預設 `UnavailableTrust` 全拒絕 |
| Broker 替換範圍？ | 換 policy acquisition、identity resolver、exact authorization 與 maintenance access adapter；沿用 approval coordinator/store、canonical binding、execution snapshots 與官方 Workflow | 本輪未建 Broker。未來需驗證 authenticated transport、human-presence proof、restart/revocation 與 durable evidence；不能承諾完全零資料擴充，但不需重設 Approval Store / Durable Execution / Workflow 核心 |

## 實作與信任契約

- [TrustedAuthority](../../src/MAF.Core/Policy/TrustedAuthority.cs)：小型 provider-neutral acquisition/authentication/authorization/binding-verification contracts，以及未註冊的 `TrustedPolicyAuthority` adapter。沒有 OS token reader、repo policy loader、背景服務、SSO、credentials 或 production synthetic fallback。
- `IProtectedPolicyAuthority.AcquireAsync` 回傳 async lease。相同 authority instance 的取得與所有 approval/control mutations 串行；lease 穿越 policy pin、business transaction 與 commit。Lease 釋放後 current snapshot 清除；失敗 refresh 不回退 cached policy。取得與等待 lease 各有 5 秒上限，支援取消；例外不提供 Allow。
- `IPolicyActivationCoordinator.ActivatePolicyAsync()` **不接受 caller snapshot/epoch**。可信來源提供的是 candidate；**同 DB `policy_authority` + audit 的 commit 才是 activation linearization point**。Request/decide/consume 等也先 pin 最新取得的 candidate，之後重新檢查 digest。單純修改來源、取得 lease 或更新記憶體不等於完成 durable activation。
- 同 DB 不同 authority instance 由 SQLite writer transaction + persistent high-water + transaction 前後 digest check 排序。Activation 先 commit，舊 admission 拒絕；admission writer 已先成立時，activation 只能其後 commit。新 Deny 不撤回已 commit admission，也沒有 provider dispatch 實作。
- 同 revision 必須同 digest；升級必須 epoch/version **兩者都嚴格增加**，單升其一、回退、不同 authority 或同版不同內容拒絕。高水位獨立 commit，business failure 不回退它。重建 adapter 後仍由 DB 拒絕舊 revision。
- `PolicySnapshotBinding` 搬出 M2-A 原 digest encoding 供 acquisition 與 storage 共用；domain `MAF.PolicySnapshot.v1`、BinaryWriter UTF-8 length prefixes、ordinal sorting 均保留。歷史 fixture 固定向量：`AE11A91E80130F71BAD8B602BC5110D674D0D27D9D58F6231C6B3D7E2B9E758E`。原 OperationBinding v1、2B-1 fingerprint、DurableExecution state machine 均未改。
- Resolver 必須驗證 context 的真實來源，不能只比較 SubjectId/AuthenticationContextId。Adapter 要求 authenticated identity 與 context 一致且有效；human/presence 標記只能來自可信 resolver，不能由輸入 JSON 決定。Synthetic test transport 以 context object identity 拒絕完全相同文字的偽造 context，**不代表真實 human authentication 已驗證**。
- Policy 對歷史 operation subject 的重新評估只是規則計算，不是重新認證人類。Consume 另驗實際 caller、current policy、記錄的 operator 仍有 exact rights、scope/control/cancel/CAS/execution mapping。Approved 資料不會自動啟動任何工作。

## 真實 build 與測試

.NET 10 / Windows；所有命令 exit 0，未觀察到 endpoint security alert。沒有真模型/OpenCode session/provider POST，沒有遷移既有個人或公司資料庫。

| 檢查 | 最終結果 |
|---|---|
| `dotnet build MAF.slnx --no-restore --configuration Debug` | 9 projects；0 warnings / 0 errors |
| M2-B `--list-tests` | **43 discovery entries**，inline cases 已展開 |
| M2-B + M2-A focused | **94 executed / 94 passed / 0 failed / 0 skipped** = 43 + 51 |
| Full offline（排除 OpenCodeLiveTests） | **347 executed / 347 passed / 0 failed / 0 skipped** |
| 原有 regression | 304 全通過 = 155 既有 + 98 M1 + 51 M2-A，已包含於347，不能另相加 |
| M1 canonical operation binding | 31 passed；PolicyTests 67 passed |
| 2B-1 durable/recovery/output/launcher 組 | 43 passed，已包含於原155；未修改原 assertions |

本輪新增 tests 覆蓋：未知 source/authority/digest、缺失/故障/無效/過期 policy、epoch/version rollback/in-place conflict、canonical policy reorder、clock rollback、缺身分/相同文字偽造/錯 scope/self/agent/no-presence/expired/unavailable/no rights、operator revoke 後禁止 consume、兩種 activation/admission 順序、不同 adapter instance 的 SQLite 排序、restart 高水位、failed business transaction、audit full rollback、不洩漏 lease，以及精確 resource maintenance 授權與前提拒絕。

最終 offline TRX 重新執行的 M2-A 真 process 證據（UTC）：

| 測試 | 實際證據 |
|---|---|
| Pending writer kill / fresh reader | Writer PID **45628**, start `2026-10-16T05:49:26.3875573Z`；reader PID **35672**；`approval-one / operation-one / task-one / execution-one` 原 IDs，Pending/version0，integrity=ok |
| Migration kill after schema version write | PID **47196**, start `2026-10-16T05:49:27.1095176Z`；restart schema1，snapshot bytes unchanged，integrity=ok |
| Migration kill after tables created | PID **55188**, start `2026-10-16T05:49:27.2682620Z`；restart schema1，snapshot bytes unchanged，integrity=ok |
| Decision race | 本次 winner Approve；version1、decisionRows1、executionRows0 |
| Consume race | winners1、executionRows1、consumptionAuditRows1 |
| Cancel/admission race | admitted=true 後 cancel/controlVersion1；executionRows1；provider dispatch not implemented |

M2-B 的 authority restart 測試為**新 adapter instance + 同 durable DB**，不是新的 OS-process/broker restart 驗證；上表 OS-process 證據屬保留並重跑的 M2-A runner。2B-1 controlled Host/live acceptance runner 本輪未重跑；不拿歷史 live 證據冒充 M2-B runtime enforcement。

命令：

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~MAF.Tests.TrustedAuthorityTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~MAF.Tests.TrustedAuthorityTests|FullyQualifiedName~MAF.Tests.ApprovalPersistenceTests' --logger 'trx;LogFileName=phase2b2-m2b-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m2b-offline.trx' --results-directory TestResults/home-validation
```

Raw TRX 留在 ignored TestResults，未提交本機路徑或 arbitrary outputs。本報告只保存 sanitized counts/PIDs/IDs。

## Schema、maintenance 與 audit

Schema 仍 **v2**，表、欄位、既有 snapshot bytes 與 enum values 不變；沒有 automatic startup migration。正常 Host/IExecutionStore 仍 v1-only。新增 `AuthorizedApprovalMaintenance` 窄入口要求 trusted authorizer 對 exact canonical DB path 授權；缺失/拒絕/來源故障時，在建立 DB 前失敗。沒有 production authorizer/CLI。既有 `ApprovalSchema` 是 trusted storage primitive，仍可由受信任測試/維護程式直接呼叫；新 wrapper **不是**對惡意 same-account 程式的安全沙箱。

所有 Host 停止且不得重啟、consistent backup/rollback 已驗證仍是可信維運責任；三個 OfflineMaintenance bool 是 attestations，不能證明身分、備份存在或程序皆已停止。Code 提供 schema/exclusive file/SQLite transaction 與 bytes/integrity checks，不能證明 dormant 舊 binary 不會重啟。完整流程與 archive proposal 見 [maintenance procedure](../setup/SQLITE_APPROVAL_MAINTENANCE.md)。

Audit 上限10000、讀取最多100仍 fail closed；本輪以縮小 capacity fixture 驗證 activation audit 滿額時高水位回滾且原事件完整。**Archive/export/prune disabled，沒有刪除 API**。提案須先定 trusted maintenance owner、完整一致備份、連續 sequence range 與 authority revision checkpoint、受保護 archive manifest/digest、獨立驗證與原子 archive receipt，再另行批准刪除；現有 schema 沒有 receipt/anchor，不實作半套 prune。

## 未解前提與 M3 checklist

- [ ] Owner 選定真實 protected policy source 與 immutable deployment identity；證明來源認證與 writer isolation。此輪 **Not Demonstrated**，不是 SourceId/digest 測試可替代。
- [ ] 持久權威 revision 的外部 rollback anchor、備份/災難回復協定；DB high-water 不抵禦惡意 DB replacement/完整備份回退。多 DB/distributed activation、global authority cutover 尚未提供。
- [ ] 選定可區分 human 與同帳號 Agent 的機制，驗證 exact operation consent、freshness/replay/revocation/expiry；目前 **Blocked（缺 production trust mechanism）**，功能保持關閉。
- [ ] 若 Broker 為長期方案，先批准其 authenticated transport、可信 writer 隔離與 durable decision proof/reference 的保管方式。現有 row 只保存 subject/decision metadata，沒有 signature 或 authentication-method proof；未聲稱防惡意 DB owner。
- [ ] 定義 Broker/source publication 與各 DB activation commit 的成功回應、timeout/crash/restart 語義；不能把遠端發布時間當本 DB 生效時間。本輪只有 local coordinator contract，沒有 remote activation protocol。
- [ ] 維護 operator、停機控制、verified backup/restore、audit retention 達成部署前提；不可將測試 authorizer 註冊正式 Host。
- [ ] Owner 另批 M3 後，盤點所有 durable/ephemeral/direct/recovery/adapter 邊界，接入現在 policy/control/CAS 與 exact identity；approval command 只記錄，resume 另行 revalidate；既有 execution 絕不 resubmit。
- [ ] 統一 enforcement 完整前不開 runtime approval/dispatch；現有 2B-1 路徑沒有因本輪自動取得 MAF policy enforcement。

七項 guardrails 保留：沒有 harness loop、Workflow 重設、provider protocol 進 Core、HTTP-in-transaction、Registry、Hybrid、write/shell/permission reply 或取消 dispatch。主要未解風險是 identity/provenance/deployment isolation，已以 production disabled 隔離；公司安全事件與既有結果原檔未改。

**M2-B foundation 已實作並通過 focused/offline tests；production trust、M3 與完整 Phase 2B-2 尚未接受。提交推送後停止，等待 owner 下一步決策。**
