# OpenCode 1.19.2 Version Pin & Fake Preflight

日期：2026-10-27。Baseline：`0be3fda`；分支 `feat/v1-pin-preflight`。
狀態：**V1 pin / bounded fake preflight 已實作並驗證；不是 Live Admission，沒有 operational launch/socket path。**

實作以兩個獨立 commits 交付：`ae5f466`（version pin / release projection）、`c0905f8`（bounded fake preflight）。本輪 timing instrumentation 與審查文件另行提交；沒有改變驗證後的產品程式碼。

## 1. 官方比較與 Compatibility Matrix

目標為使用者已安裝的 **1.19.2**，不要求降版。installed version 為 Owner 提供，本次沒有執行本機 OpenCode binary 驗證。

- [官方 1.19.2 release](https://github.com/anomalyco/opencode/releases/tag/v1.19.2)，source 固定 `7901b7d325c34852caa84fc2c325f3a08dd7a473`。
- 歷史 1.18.35 source 固定 `53d1eabb61e21162157817bf677da0a4ad3332e3`。
- [官方比較](https://github.com/anomalyco/opencode/compare/v1.18.35...v1.19.2) 回傳 diverged，右側 37 commits；不將它當作單純線性升級歷史。另對兩個 exact source endpoints 的 Git blobs 與 OpenAPI 做直接比對。
- 兩份 `packages/sdk/openapi.json` 的 **162 個 paths 完全相同**；components schema 差異只有 Config 的 autoupdate description。OpenAPI `info.version` 都為 `1.0.0`，**不能拿它當 executable/server version**；pin 使用 `/global/health.version`。
- 1.19.2 full OpenAPI SHA-256：`3e27efb6e2bcfee9030df64c7be486c32f250866d6a84b88970dd83d5d9788f2`。connector 對大型檔案不支援，因此以核准的公開 HTTPS raw download 靜態解析；沒有下載或執行 binary。

Confirmed 表示官方 source/spec 或 fake 有可核對依據，不表示 installed binary/live 行為已驗證。Breaking 明確限定變動面；Unknown 不補成相容或成功。

| 面向 | exact 1.19.2 與 MAF 假設 | 判定 |
| --- | --- | --- |
| Server / serve / health | `cli/cmd/serve.ts`、`server/server.ts` blob 與 1.18.35 相同；相同 headless serve 與 health discovery，MAF 增加 exact health version check | **Confirmed source compatibility**；本機 binary / port binding / runtime config **Unknown** |
| Session Create/Get | `/session` POST、`/session/{sessionID}` GET 與 bare session unchanged；permission ref 為 PermissionRuleset array，directory/identity echo 由 MAF 驗證 | **Confirmed**，不必改 payload。Create 的可選 model 為 `{id, providerID, variant?}`；MAF 現在不送 Create model，不把它誤當 Prompt shape |
| Prompt | `/session/{sessionID}/prompt_async`，messageID/agent/parts；可選 model 為 `{providerID,modelID}`，204 accepted | **Confirmed**；Lab 的 model injection 位置正確。accepted 不是 terminal，也不授予 POST retry |
| Status / messages | status map、`before`/limit pagination、assistant parentID/sessionID/finish/time unchanged；session/message/status blobs 同一份 | **Confirmed source compatibility**；MAF 維持 idle/缺結果非 Completed、原 request correlation，不加第二套 observer |
| Permission / question | permission/pattern/action，allow/ask/deny；permission routes、evaluation、agent blobs 相同 | **Confirmed**；returned policy 仍是 server-declared evidence，不是 effective config custody 或 sandbox proof；沒有自動 permission reply |
| Agent / model | agent list 為 array；Create 與 Prompt model schema 不同但兩版各自相同；PCA/Reviewer/Harness recipients 仍分別綁定 | **Confirmed wire shape**；custom endpoint/fallback/auxiliary inference 的真實目的地 **Unknown** |
| Abort | `/session/{sessionID}/abort` 回 boolean；session handler/prompt implementation blob 相同 | **Confirmed source compatibility**；MAF 不將 ack 當 Cancelled。Preflight 不呼叫 Abort |
| Event / SSE | event group/handler 及原 session event schemas 相同；`/event` text/event-stream | **Confirmed source compatibility**；MAF 此路徑仍 GET polling，未驗證真實 SSE ordering/reconnect，不能當 durable terminal evidence |
| Session sharing | 1.19.1 起分享實作停止，share route 保留但失敗；unshare 只清本地連結，Create 不再 autoshare | **Breaking behavior**，MAF 不使用此路由；不需 adapter 改動，也不能因此推論所有 auxiliary outbound 已消失 |
| PTY | connect preflight 的 origin/ticket 檢查順序修正 | **Changed outside MAF surface**；不新增 PTY/OS 操作 |
| Upgrade / configuration | 1.19.2 更新檢查限制在 1.x；升到 2.x 是獨立選擇；Config 更新文字改為通知/明確更新 | **Confirmed change**；MAF 不執行 install/upgrade/downgrade/negotiation；V2 完全不在本增量 |

上述相同 blob 的 source 位於固定 commit 下 `packages/opencode/src/`：`session/session.ts`、`session/prompt.ts`、`session/message-v2.ts`、`session/status.ts`、`permission/index.ts`、`permission/evaluate.ts`、`agent/agent.ts`、`server/routes/instance/httpapi/groups/{session,permission,event}.ts`、`handlers/session.ts`。
可核對 [release session contract](https://github.com/anomalyco/opencode/blob/7901b7d325c34852caa84fc2c325f3a08dd7a473/packages/opencode/src/server/routes/instance/httpapi/groups/session.ts)、[handler](https://github.com/anomalyco/opencode/blob/7901b7d325c34852caa84fc2c325f3a08dd7a473/packages/opencode/src/server/routes/instance/httpapi/handlers/session.ts)、[完整 SDK OpenAPI](https://github.com/anomalyco/opencode/blob/7901b7d325c34852caa84fc2c325f3a08dd7a473/packages/sdk/openapi.json)。

## 2. Version pin 實作

既有 OpenCodeOptions 最後新增 optional `ExpectedVersion`。null 保留歷史 schema-based behavior 和完全相同的舊 Scope hash；沒有改寫 `fixture`、1.18.35、1.19.0 等歷史資料。明確 pin 只接受本次已 source-reviewed 的 `1.18.35` 與 `1.19.2`；preflight 固定要求 **1.19.2**。

`OpenCodeClient.ValidateAsync` 在每次 discovery 開始清除 pin validation；health version 不精確相符就 `OpenCodeVersionMismatch`，只允許 health GET，沒有 Session Create/Prompt。只有 schema 和 agent checks 也成功後才完成 validation。pinned Create/Submit 即使被直接呼叫也要求 validation，否則 `OpenCodeVersionNotValidated`。unsupported expected pin 在 transport 前拒絕。沒有 fallback 或自動改版本。

Pinned Scope 納入 ExpectedVersion，不能把 pinned handle 靜默換成另一版或 legacy scope。null Scope 的原序列化/hash 保持原樣；這不是 migration。版本驗證是當次 observed health，不能防止之後 server 被置換；真正 process/endpoint/config custody 仍須另行建立，pin 不是 admission。

測試中的 matching 1.19.2 fake 可經既有 Workflow 執行一次 fake Create/Prompt；那是 adapter regression，**不是 preflight 的行為或真實請求**。preflight 所有 cases 均為零 POST。

## 3. Bounded preflight 的確切交付與限制

Host 新增單一 internal `OwnedOpenCodePreflight`，只有 `ForTests`，不連 Program/CLI/DI，不增加 interface/factory/registry/project。復用 OwnedHarnessProcess、OpenCodeClient、現有 IRuntimeAccess 和同一 agent/schema guard。

1. expected selection 明列 absolute existing executable/cwd、64 位小寫 SHA-256 描述與 explicit loopback IP/port；復用 CreateStartInfo metadata/shell rejection，**不執行它**。hash 是 fake supplied observed/expected identity 比對，沒有宣稱讀取/驗證真實 binary 或取得 corporate approval。
2. fake port-available callback 必須在 fake start 前通過；occupied 就不 start、不 GET。回傳的 original owner、endpoint owner reference、origin、executable、digest 必須相符。相同健康版本不能取代另一 owner/port/binary。
3. fake launch 將原 child 與 transport 交給同一 preflight owner；R1 exception 直接保留 exception.Owner。setup/start failure、early exit、discovery 中 exit 與 unsafe/missing agent/schema 都保留明確失敗，不 implicit kill 或 detach。
4. private discovery access 只允許 Validate；HTTP guard 限制依序至多一次 GET health/doc/agent、同一 origin、child 仍 running、無 redirect replacement。沒有 Create、Prompt、Abort 或 model API。不是把 live transport 塞入 ForTests 的授權。
5. timeout 為正值、最多一分鐘；caller 取消/timeout 僅結束等待，ValidationCompletion 持續追蹤 late/uncooperative start。它完成前拒絕 Stop/Dispose，不能遺失晚到的 owner。
6. Stop 交由既有 single-flight owner；Unresolved 不 Dispose owner 或 transport。晚到 exit 仍可完成 cleanup，絕不 retry kill。Dispose/transport failure 在重複呼叫仍可見；同步 OS API 的硬即時限制沿用原契約。

`PreparedOffline` 只在 discovery 完成、child running、未 stop/release/expire 時為 true；Evidence 固定為 `ControlledOfflineFixture / NotLiveAdmission / NotEndpointCustodyProof`。**Fake owner reference pairing 不是實際 socket custody 的證明**。occupied-port race、process identity、binary hash/approval、socket/port ownership、auth、effective config merge 的真實檢查仍 NotRun/NotImplemented；沒有健康 server takeover、PID/name lookup、shell fallback、全樹 kill 或任意 real HttpClientHandler default。

## 4. 實際驗證

沿用 Owner 本 session 已核准 build/offline tests 的 scope，本輪僅 fake/in-memory transport（加上既有 metadata / regression test-owned fixtures）；SDK `10.0.103`、Windows `10.0.26300` win-x64、VSTest `18.0.1`，無 `.ps1`。本機已安裝 OpenCode 未執行、未更新。

| Selection | Passed | Failed | Skipped | 證據界線 |
| --- | ---: | ---: | ---: | --- |
| Solution build | — | 0 errors | — | 0 warnings；成功 build，最後 test command 亦重建最新 tests |
| OpenCodeVersionPinTests | 17 | 0 | 0 | 含精確 matching、7 mismatch/unknown、5 unsupported pins、schema failure、scope isolation、official schema projection |
| OwnedOpenCodePreflightTests | 18 | 0 | 0 | occupied port、foreign owner/origin/hash/path、R1、early exit、permissions、timeout/late start、Unresolved、cleanup、cancellation；全 fake |
| scoped existing regression | 254 | 0 | 0 | 30s，11 classes，包含歷史 client/recovery、Host/demo、Lab guards、ownership 與 runtime lifecycle |
| one focused ProtectedHandoff timing run | 9 | 0 | 0 | 17s；不是抹除上一里程碑 3 個失敗，詳見 feasibility/reliability report |

共 298 個本輪不同 cases；不是 full-suite 或 live acceptance。5 個 OwnedProcessOS、其他 OS/crash suites、live OpenCode/SSE、binary/socket/config custody、模型請求全部 **NotRun / 未授權**。沒有新的 build/test failure；沒有為通過而放寬 deadline、assertion 或 guard。

Command：`dotnet build MAF.slnx --no-restore --disable-build-servers -m:1 -v minimal`。New tests 使用下列 command（會先 build 最新 tests）：

```text
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~OpenCodeVersionPinTests|FullyQualifiedName~OwnedOpenCodePreflightTests' --logger 'trx;LogFileName=v1-preflight-new.trx' --results-directory tests/MAF.Tests/TestResults/v1-preflight -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

其餘兩輪使用 `--no-build --no-restore` 與相同單一 thread 設定。每輪 test process 的 `MAF_LIVE_OPENCODE=0`、`MAF_OWNED_PROCESS_OS_TESTS=0`。Regression timeout=240000、logger=v1-regression.trx，filter：

```text
FullyQualifiedName~MAF.Tests.OpenCodeClientTests|FullyQualifiedName~MAF.Tests.OpenCodeRecoveryTests|FullyQualifiedName~MAF.Tests.OwnedHarnessProcessTests|FullyQualifiedName~MAF.Tests.LocalSoloDemoTests|FullyQualifiedName~MAF.Tests.LocalSoloHostTests|FullyQualifiedName~MAF.Tests.LocalSoloReadinessTests|FullyQualifiedName~MAF.Tests.LocalSoloWorkflowTests|FullyQualifiedName~MAF.Tests.LabOpenCodeBoundaryTests|FullyQualifiedName~MAF.Tests.LabOpenCodeExecutionPlanTests|FullyQualifiedName~MAF.Tests.BoundNativeRecipientTests|FullyQualifiedName~MAF.Tests.RuntimeLifecycleTests
```

Timing timeout=180000、logger=protected-timing.trx；只選先前失敗的三個方法與其 variants。TRX 均放 ignored `tests/MAF.Tests/TestResults/v1-preflight/`；計數直接讀取 Counters/named results，不 commit 原始 logs：

| TRX | SHA-256 |
| --- | --- |
| v1-preflight-new.trx | `AC39AC9202A03BD59AB8E1F9474656414FA62DBCB41A321C48C7F4D3A827A3DE` |
| v1-regression.trx | `995BA3E1BF55FA885B067F3F319F8DF7E0F9FE8BAEBE033D079ABAEEC7D9DC66` |
| protected-timing.trx | `FBCDED8F8A5C442B037BEDCF873C8CB68D682E9E40B4C8E09409A2C0687D32AB` |

完整官方 JSON 只作 ignored research artifact；tracked `Fixtures/opencode-1.19.2-discovery.json` 是 9 個 MAF discovery routes 與 permission schemas 的 exact partial projection，附 source commit/full-source hash，嵌入 test assembly；不是完整可解析所有 refs 的 OpenAPI，也不代表 live server export。既有簡化 fake fixtures 仍保留原 baseline。

## 5. 下一個產品增量

相容性與 fake preflight gate 可交付，但 [Live Evidence feasibility](../plans/LIVE_EVIDENCE_FEASIBILITY_V1.md) 判定現有 offline journal 不能原樣表示 genuine live custody。下一步只需針對單一 PersonalLab operation 審核最小 evidence-profile/custody binding extension，再在另行授權的 dedicated process/socket scope 驗證 operational preflight；不做 V2、Registry、ProductionProtected 或第二個 Orchestrator。
