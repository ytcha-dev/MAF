# Phase 2B-2 M1：Policy contracts 與 deterministic evaluator 結果

日期：2026-10-16（Asia/Taipei）。開發基準：`009a96e`。Owner明確核准M1 implementation與focused/offline tests；未授權M2。本報告只接受M1的獨立契約／決策器結果，不宣稱完整2B-2、human authentication或runtime policy enforcement已完成。

## 實作範圍與決策

- [PolicyContracts](../../src/MAF.Core/Policy/PolicyContracts.cs)：provider-neutral immutable records、PolicyEpoch/PolicyVersion正整數value types、三個支援的operation kinds、capability與scope、trusted caller context interface、IPolicyEvaluator、snapshot/source contracts。Snapshot內集合使用ImmutableArray；沒有production identity resolver或test identity fallback。
- [DeterministicPolicyEvaluator](../../src/MAF.Core/Policy/DeterministicPolicyEvaluator.cs)：明確注入policy source與TimeProvider；exact caller/connector/capability/operation/scope rule matching。三種結果Allow/Deny/RequireApproval；Deny優先，其次RequireApproval，無符合規則為Deny。Enabled/healthy本身不授權。
- Unknown操作/capability、無效／缺policy、缺trusted context、scope／authority／epoch／version不符均fail closed。Provider permission resolution、write、shell、scope elevation固定NotSupported／Deny，明確Allow規則也不能開啟。
- `Revalidate`重新評估目前policy，然後檢查舊decision的binding、authority/epoch/version與有效期間；新Deny優先，舊decision不能成為bearer grant。RequireApproval沒有批准消耗實作，不能轉成Allow。有效期為`min(now + DecisionLifetime, snapshot.NotAfter)`，revalidation不得延長原NotAfter；正好expiry即失效、時鐘早於原EvaluatedAt也拒絕。DecisionLifetime需大於0且至多1小時，非持久化approval期限。
- Source/context/clock例外只回固定EvaluationError，不回exception text；null prior decision為固定InvalidRequest。沒有HTTP、filesystem讀寫、approval dispatch、cancel side effect或服務註冊。

`ImmutablePolicySource`是明確配置的單一不可變snapshot容器，不是persistent authority。M1以同一snapshot的正值epoch/version做一致性比較，**不實作跨重啟的epoch rollback防護或policy更新協定**；後續仍需受保護的單一authority與monotonic revision啟用流程。不能從caller輸入或repo中的可改設定建構受信任authority。

## Canonical binding v1

[OperationBinding](../../src/MAF.Core/Policy/OperationBinding.cs)固定欄位順序：

1. Domain `MAF.OperationBinding`、EncodingVersion=1。
2. Authority、PolicyEpoch、PolicyVersion、caller、Operation/Task/WorkflowRun/Execution/Message IDs、request fingerprint。
3. Connector/capability/operation、resource/provider/session-policy scope、configuration/scope revisions、access、budget ticks。
4. Intent generation、optional provider session identity、adapter version、enforcement boundary、enabled/healthy與排序後supported-operation集合。

數值為signed64-bit big-endian；字串為strict UTF-8，前綴unsigned32-bit **byte length**，null marker為FFFFFFFF；不接受空optional session、malformed UTF-16、未知enum與過長欄位。Hashes轉uppercase；其他identifier為ordinal exact文字，不做culture／Unicode normalization或filesystem canonicalization。Supported-operation集合按enum name ordinal排序。輸出為uppercase SHA256，沒有raw JSON equality。

ResourceId被視為已canonical的opaque identifier；M1不碰真path、不驗reparse point。將來application/adapter必須先驗真實workspace並提供穩定scope，不能把字串相同當作OS隔離保證。這個digest不是簽章、credential、token或人類批准證據。

測試含獨立按規格建立的v1固定向量：

```text
A44044AEF87674BA9046587F4E99EE20CFDF6D1AECB7A58EBDD925C9667FCFB3
```

向量對應`OperationBindingTests`的固定synthetic request/descriptor；將來改encoding須升version與新增向量，不無聲改v1。另驗根物件與scope JSON屬性重排、supported operations重排不改digest；22種binding欄位變更改digest；UTF-8非ASCII/emoji、255/256/1024字元byte長度、相同拼接文字的不同field boundaries、unknownenum／invalidUnicode／oversize拒絕、policy version/epoch變更與expiry revalidation。原[Phase2B-1 fingerprint](../../src/MAF.Core/DurableExecution.cs)檔案與算法未修改。

## 實際執行結果

環境沿用家用Windows、.NET SDK10.0.401。下列命令均實際執行，最終exit0；未呼叫LLM、未啟動OpenCode、未執行process/live acceptance runner。

| 檢查 | 真實結果 |
|---|---|
| `dotnet build MAF.slnx --no-restore --configuration Debug` | 8 projects成功；0 warnings / 0 errors |
| M1 `--list-tests` | **47個discovery entries**：PolicyTests 37、OperationBindingTests 10；其中三個MemberData theory containers在run時展開 |
| M1 focused run | **98 executed / 98 passed / 0 failed / 0 skipped**；TRX Completed |
| PolicyTests runtime cases | 67 passed |
| OperationBindingTests runtime cases | 31 passed |
| Full offline regression | **253 executed / 253 passed / 0 failed / 0 skipped**；TRX Completed；OpenCodeLiveTests由filter排除 |
| 既有offline基線 | 上列253中的原有**155 cases全通過**，未修改其tests/assertions |
| 既有durable/recovery/launcher focused組 | 同一full run中29 DurableExecution + 3 State + 2 OpenCodeRecovery + 5 DurableConsoleOutput + 4 Launcher = **43 passed**，已包含於原155 |

Discovery與execution數不同是實際xUnit行為：`InvalidOrMismatchedRequestsFailClosed`、`InvalidPolicyDoesNotAuthorize`、`EveryRelevantBindingDimensionChangesDigest`三個MemberData containers在runtime分別展開18、14、22列，所以47 - 3 + 18 + 14 + 22 = 98；沒有把source靜態計數冒充discovery。Focused98已包含在253中，不相加。

命令：

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~MAF.Tests.PolicyTests|FullyQualifiedName~MAF.Tests.OperationBindingTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~MAF.Tests.PolicyTests|FullyQualifiedName~MAF.Tests.OperationBindingTests' --logger 'trx;LogFileName=phase2b2-m1.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m1-offline.trx' --results-directory TestResults/home-validation
```

Ignored本機evidence：`TestResults/home-validation/phase2b2-m1-discovery.txt`、`phase2b2-m1.trx`、`phase2b2-m1-offline.trx`。TRX按actualtestresult分組核對67+31+155=253；沒有提交含本機路徑的rawTRX。未觀察到endpoint-security alert。

## 安全界線與M2前提

- Evaluator**未接入**Host DI、live submission、recovery或OpenCode adapter；既有流程未因M1新增保護，也沒有新增bypass flag。新的contract不代表全入口policy enforcement gate通過；ADR-005 P10/C、approval、migration、durable cancellation與liveinterop仍未驗證。
- `ITrustedCallerContext`只是一個由未來可信邊界供給的契約；tests中的SyntheticCaller/FakeClock僅在test assembly。任何自行實作該interface的caller並不因此被認證。OS username/SID不能區分同principal的人與agent，M1不解決human presence或self-approval。
- 沒有SQLitev2、IApprovalStore、approval CLI、production identity resolver、durable cancellation、provider permission bridge、Registry或Hybrid orchestration。既有execution snapshot、schema v1、終態、取消與readonly checks均未修改；公司安全事件與歷史結果原檔未修改。
- 本輪只重跑offline regression，不重跑2B-1六checkpoint／七controlled-provider／三live情境；那些保留原歷史證據，不能視為本輪新執行。
- M2仍需owner批准：protected policy authority/monotonic epoch更新與rollback防護、operator trust配置、明確approval deadline/retention、同DB窄UoW與schema v2的offline/exclusive/atomic migration、v1snapshot byte preservation與備份策略。Runtime enforcement與人工批准須等後續里程碑具備完整application/adapter boundary並另獲授權。

**結論：M1 focused與既有offline regression通過；完整Phase2B-2未驗收。提交後停止，不進入M2。**
