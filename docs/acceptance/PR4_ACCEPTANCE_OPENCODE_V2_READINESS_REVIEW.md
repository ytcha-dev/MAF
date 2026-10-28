# PR #4 Acceptance & OpenCode V2 Readiness Review

> 2026-10-27 R1 implementation follow-up: owner/backend allocation now precedes OS Start;
> post-start setup failure returns the original owner via a sanitized typed exception.
> Exit observation precedes stream setup; partial cleanup remains owned and repeated
> disposal retains failure evidence. Five additional Fake Facts are authored, all NotRun.
> Current intended count: 42 = 36 fake/metadata + 1 platform metadata + 5 opt-in OS.
> See [updated ownership contract](../architecture/OWNED_HARNESS_PROCESS_LIFECYCLE.md).
> R1/B3 below are historical findings against b2d7f13; authored fix is complete, execution
> acceptance remains pending. Build/tests/process/socket remain NotRun under the hold.
> Product priority is now Personal Alpha offline delivery; V2 research/implementation is
> deferred and does not block that work. The original compatibility matrix is retained.

日期：2026-10-27。狀態：**供 Architect 審核；Conditional Go 僅限離線審查與下一個契約研究增量，並非 PR 驗收通過或執行授權。V2 operational integration：No-Go。**

## 1. 審查基準與方法

本次以 connected GitHub GET 核對遠端狀態，再讀取本機對應 Git objects、原始碼、測試與文件：

| 項目 | 核對結果 |
| --- | --- |
| Repository | `ytcha-dev/MAF`，origin 為同一 repository |
| 遠端目前 main | `684403313ac387f9e051154e5b6bbe28bd1dc2b7` |
| PR #4 | Open、未合併；base 為上述 main |
| 審查的 PR head / 本機 HEAD | `b2d7f131f4df630bbe14accb34692dcc5b9870f1` |
| PR implementation commits | `8c36b25692f8293d062f437da8bb6e59c8ecc7fb`、`b2d7f131f4df630bbe14accb34692dcc5b9870f1` |
| 差異 | 7 files、923 insertions；無既有產品程式碼刪改 |
| Working tree | 原有未追蹤 `.opencode/` 保留、不讀取其設定內容、不加入 commit |

來源：[PR #4](https://github.com/ytcha-dev/MAF/pull/4)、[main snapshot](https://github.com/ytcha-dev/MAF/tree/684403313ac387f9e051154e5b6bbe28bd1dc2b7)。本報告後續文件 commit 不屬於上述已審查 implementation head；若產品程式碼改動，必須重新評估。

已讀取：ROADMAP、ARCHITECTURE_GUARDRAILS、OWNED_HARNESS_PROCESS_LIFECYCLE、PERSONAL_LAB_RUNTIME_LIFECYCLE、Runtime Lifecycle acceptance evidence，以及 OwnedHarnessProcess 的全部程式碼、Fake/OS tests 和 acceptance runner 新增分支。另追讀 PersonalLabOfflineRuntime、OpenCodeClient、LabOpenCodeBoundary、LabOpenCodeExecutionPlan 與既有 Core contracts，核對實際使用點。

目前 ROADMAP、ownership contract 與 PR 本文仍明示公司 execution hold。沒有目前裝置解除 hold 的證據；歷史其他環境成功執行不構成 clearance。本次僅執行 source/Git inspection、文件編輯及官方資料查詢；沒有 Build、test runner、專案 executable、child process fixture、本機 Socket、OpenCode server 或模型請求。

## 2. PR #4 Code Review

### 2.1 Findings 與嚴重度

**沒有靜態確認的 P0/P1 授權繞過或必然正常流程失敗；這不是無缺陷證明。以下分開列出防禦性缺口、測試缺口與已知限制，沒有把未執行的推論寫成已重現產品缺陷。**

| ID / 類型 | 位置（審查 head） | 發現、影響及處置 |
| --- | --- | --- |
| R1 / P2 防禦性缺口，未重現 | `src/MAF.Host/OwnedHarnessProcess.cs:203-214` | constructor 的 catch 僅涵蓋 `process.Start()`；成功建立 child 後，取得 redirected streams 及初始化 observation 的步驟位於 catch 外。若 post-start setup 同步拋例外，Start 不會回傳 owner，亦無保留該 child 的明確交接路徑。正常 .NET redirected-stream getter 預期可用；目前沒有證據顯示這會在本環境發生。Architect 應明確決定此 failure-atomicity 風險是否可接受；若要求修補，先設計成功 start 後無法完成 setup 時仍可保留 owner 的小範圍處理，不能只 Dispose handle 後假裝 child 已停止，也不能增加 tree kill。現有 fake seam 位於成功建構之後，不能覆蓋此窗口。 |
| R2 / P3 測試證據缺口 | `tests/MAF.Tests/OwnedHarnessProcessOsTests.cs:55-84` | authored OS cases 有大量輸出及不同 owner 隔離，但沒有「原 child 已退出、descendant 仍持有 pipe」的案例，亦不能憑 fake Kill 次數證明 Windows original-handle 行為。文件承認不管理 descendants，這不是新增 descendant 管理要求；未來獲准的 OS 驗證應針對 exit/pipe independence 補證，或將此情境維持 Unknown。 |
| R3 / P3 診斷限制 | `src/MAF.Host/OwnedHarnessProcess.cs:113-120,222-228` | CloseInput 失敗保留 boolean；其他 Stop backend 例外折疊為 Unresolved，output IO 失敗結束 drain。可維持保密與保守結果，但不能僅由 Unresolved 判定是權限、平台或產品缺陷，也不能由 ResourceCleanup 成功推論未發生輸出讀取錯誤。後續如需診斷，僅考慮非敏感分類，不記錄 environment/output 或自動 retry。 |

R1 是建構失敗路徑的設計審核項，不是已證實的漏失 process 事件。本次未修改產品程式碼或新增測試。最主要 acceptance blocker 仍是所有新程式碼完全沒有當前執行證據。

### 2.2 程序所有權與生命週期逐項判定

| 面向 | 靜態結果 | 驗證限制 |
| --- | --- | --- |
| Start / ownership | absolute executable、existing working directory、ArgumentList、UseShellExecute=false；無 PATH fallback、attach、PID/name lookup。啟動前檢查 cancellation，檢查後仍可能完成同步啟動並回傳 owner，契約有揭露 | 不證明 binary integrity、設定隔離或 executable approval；R1 post-start setup 例外窗口待決 |
| Environment | 清空 inherited environment，只加入 caller 指定內容 | 不是環境變數 allowlist；caller 仍須審核 credentials、loader hooks、HOME/config 與 downstream tools |
| EOF | 預設不關 stdin；opt-in 僅 Close，不寫 command；失敗保留 InputCloseFailed，繼續 grace observation | 不能由 generic EOF fixture 推論真實 OpenCode 支援相同 protocol |
| Exit / output | 原 Process 的 HasExited 每 20ms observation；stdout/stderr 各 4096-byte buffer 丟棄，不依賴輸出 EOF 判定原 child exit | 無實際 Windows/Linux 證據；descendant 不是 owner 保證範圍 |
| Stop | 先 publish completion task，再進 backend callback；所有 Stop 共用一次；grace 後僅一次 permitted termination | Stop cached outcome 不隨晚到 exit 改寫；應另讀 Observe/WaitForExitAsync |
| Windows termination | retained Process、Kill(false)，沒有 process tree kill / reacquire PID | 原 handle 語意與實際行為尚未由本次 OS test 驗證 |
| Unix termination | 主動拒絕強制 termination，非合作 child 留在 Unresolved | 有意的平台限制；不是 Windows/Linux feature parity |
| Dispose | lock 下拒絕 stop 未完成或 exit 未成功觀察；exit 後僅 release 一次，持續保留 sync Dispose failure | 不會 implicit kill；無法解決 faulted Exit observation 的 owner 不可丟棄，且本增量沒有 repair API |
| Async cleanup | DisposeAsync 先 join Stop、再 Dispose、bounded wait ResourceCleanup；timeout 不改寫成功；重複 Dispose 不重做 release | sync Dispose return 僅代表 release 已嘗試；完整釋放需 ResourceCleanup 成功 |
| Timeout | grace、terminationWait、startup/exit wait 都要求正值、最多 1 分鐘 | 同步 Close/Kill/Dispose 與 scheduling 不受硬即時 deadline 保證；不能宣稱 total stop 精確上限 |
| Races | Stop/Dispose 以同一 lock 管理；fake 涵蓋 EOF failure/exit、kill failure/exit、concurrent Stop、cleanup late fault | 無執行、無 exhaustive scheduler / OS race 證據 |
| Unresolved | ownership 留存、Dispose 拒絕、無 retry / name search / reattach / replacement child | 必須有 caller 保留 owner 與人工處置責任；無 operational integration |

### 2.3 安全邊界

base → head 的產品新增只有 internal OwnedHarnessProcess；`rg` 的所有使用點僅在此元件及測試。Host Program、DI、PersonalLab runtime composition、Production trust、policy/admission/storage/receipt/recovery 實作沒有在此 diff 修改。acceptance runner 的新 `owned-process-fixture` 分支位於 database/Workflow dispatch 前，僅提供 exit、EOF、有限輸出與五秒 linger；這是 test-only executable mode，不是公開 Host 執行入口。

靜態未發現新增 Network Allow、synthetic authority、Production fallback、permission reply、模型呼叫、重新提交、remote Abort 或 ReadOnly 放寬。已知 shell 名稱/副檔名拒絕只是輸入檢查，不是所有 interpreter 的安全 sandbox；不能用重新命名 executable 當安全證明。未來 caller 必須先取得真實操作授權。所有結論限定本次 diff 與核對的 call sites；未執行 regression。

## 3. 與現有 Runtime Lifecycle 的責任關係

| 責任 | 現有 owner | PR #4 owner / 未來衝突風險 |
| --- | --- | --- |
| task identity、single-flight journey、native work ledger | PersonalLabOfflineRuntime / Workflow | 不移入 process primitive；process start 不建立另一 execution identity |
| local cancellation / bounded task drain | runtime RequestCancellationAsync / WaitForCompletionAsync | process Stop 只處理 OS child；不可取代 task drain，也不可把 local cancel 等同 remote Abort |
| HTTP boundary / transport | runtime 擁有 HttpClient/boundary/transport | process primitive 不擁有 transport；未來不得兩邊 Dispose 同一 handler |
| native clients / journal | caller | 必須活到原 task drain、原 outcome 記錄完成；process exit 不授予 journal terminal evidence |
| OS child / pipes | main 沒有此 owner；PR #4 新 primitive | 未接入 runtime，因此目前沒有雙重管理；未來只允許一個 explicit owner，禁止另一 cleanup path kill 同一 child |
| cancellation / durable Abort / reconciliation | 既有 protected operation/evidence boundaries | Stop/EOF/Kill 不可繞過 current authorization，不能當 Cancelled 或 Abort delivery evidence |
| downstream configuration | LabOpenCodeExecutionPlan 描述 binding | 不是 factory、custody 證明或啟動授權；V2 需要重新審核版本與有效設定語意 |

目前沒有實際重複呼叫；「Stop、wait、Dispose」名稱相似但作用層不同。未來 composition 必須明確排序：封鎖新工作 → request local cancellation → bounded drain → 記錄原 outcome → 必要時依另行授權處理 remote cancellation → process stop/exit → pipes cleanup → 釋放剩餘 caller-owned 資源。若 drain 超時，不可先釋放正在使用的 transport/native clients/journal；若 child Unresolved，不可宣稱 runtime 全部已關閉。這是後續設計條件，並非本次實作。

## 4. Test Evidence / NotRun Inventory

| 驗證 | 本次結果 | 說明 |
| --- | --- | --- |
| Git main/head identity、diff、使用點追蹤 | 已完成靜態檢查 | 以遠端 GET + 本機 Git objects 比對 |
| `git diff --check 6844033 b2d7f13` | 通過，無 whitespace 訊息 | 僅格式證據 |
| .NET Build / restore | **NotRun** | corporate execution hold；未啟動 MSBuild/testhost |
| OwnedHarnessProcessTests | **NotRun：31 intended cases** | 靜態計數 23 Facts + 3 Theories / 8 rows；含 follow-up 10 Facts，不能再加成 41 |
| 上述純 Fake lifecycle 子集合 | **NotRun** | 計數中另含 existing-file/directory metadata cases；整個 class 不是嚴格零檔案存取 |
| KnownPlatformShellIsRejected | **NotRun：1 metadata case** | 不 launch shell，但也不能在 hold 下啟動 test runner |
| OwnedProcessOS | **NotRun：5 cases** | default opt-in gate 不代表授權；未設定啟用變數、未 launch fixture |
| Windows / Linux 實際原程序與 pipes | **NotRun** | 無 OS acceptance；Linux 未執行不應寫成平台通過 |
| RuntimeLifecycleTests、LabOpenCodeExecutionPlanTests、LabOpenCodeBoundaryTests、protected/source/Reviewer guard regressions | **NotRun** | 現有源碼核對不是 regression pass |
| full offline suite | **NotRun** | 沒有產生本次 TRX、pass count 或 failure classification |
| 真實 OpenCode / preflight / SSE / model / live ReadOnly | **NotRun，且未授權** | 未安裝、啟動、連線、提交或執行模型 |

合計新 authored coverage 為 **37 intended cases = 31 fake/metadata + 1 platform metadata + 5 OS**，不是 37 passed。PR #3 的 `1624 passed / 0 failed / 3 skipped` 僅為既有文件記載的歷史 baseline；本次未重現、未獨立查驗其原始 TRX，也不能轉算成 PR #4 的結果。

未來順序（須先解除相關 hold）：先 build，再逐名選取純 Fake lifecycle tests，之後 metadata tests；另行取得 OS process 操作授權後，才執行 5 個 OS cases。不要直接選整個 acceptance test project，因為其他既有 cases 可能有 filesystem/process 副作用。後續按已審核 scope 跑 runtime/configuration/guard regressions，再考慮 full offline。Windows/Linux 要分別記錄 command、commit、SDK、環境授權、TRX 與 skipped inventory。

若未來失敗：有最小可重現且違反契約才分類產品缺陷；assertion/fixture/race 假設錯誤列測試缺陷；OS 不支援、missing approved executable 或 execution block 列環境限制。EDR flag/terminate 一律停止並保存事件，不 retry、改名、換路徑或替換執行機制。本次沒有執行失敗可供產品/測試歸因。

## 5. OpenCode V1 / V2 Compatibility Matrix

### 5.1 版本與證據範圍

V1 的 MAF 既有契約基準是 `docs/plans/PHASE2A_API_DISCOVERY.md` 記錄的 **1.18.35**；這是歷史實測文件，不是本次 live evidence。另查官方 V1 server 文件 [S1]。

V2 同時使用官方 migration/API/client/plugin/CLI 文件 [S2-S7]，並將原始碼固定於官方 `anomalyco/opencode` 的 **`19d87a72e6da026cc3f0cc620808d336d9fb0a04`**（2026-10-27T02:47:26Z）[S8]。該 snapshot 的 `@opencode/client`、`@opencode/sdk`、`@opencode/cli` package.json 為 **2.1.4**；這只證明 source manifest，不證明已發布 artifact、安裝版本或所有 2.0.x 的 shape。官方 API 頁本身仍標示 Experimental；不得把 dev snapshot 泛稱為永遠穩定的 V2 契約。此次未選定 approved V2 release artifact。

標記：**Confirmed** = 官方文件或固定原始碼有依據；**Breaking** = 相對 MAF V1 wire/operational 假設有具體不相容；**Unknown** = 目標版本或執行證據不足。Confirmed 不表示 MAF 已相容。下列 S8 子檔案皆以同一 SHA 固定。

| 面向 | V1 / 現有 MAF 假設 | V2 官方依據 | 判定與 MAF 影響 |
| --- | --- | --- | --- |
| Server launch / ownership | V1 `serve` 可獨立啟動；PR #4 只擁有原 child | V2 CLI 預設發現/啟動使用者共享 server；提供 standalone；serve handler 區分 default/service/stdio [S5,S8] | **Breaking operational assumption**：啟動 CLI 不保證擁有 server；禁止 attach/stop shared service。專用 binary/foreground 策略與 Windows artifact **Unknown** |
| EOF / readiness | PR #4 opt-in EOF，所有 stdout 丟棄；survival 不是 readiness | V2 `serve --stdio` 以 stdin EOF 結束 ownership lease，stdout 第一行提供 JSON URL；官方 standalone 使用 `--stdio --port 0` [S8] | **Confirmed source protocol；現成整合不足**：丟棄 stdout 不能取得動態 port。未來只可設計 bounded/redacted handshake 或另經審核的 endpoint 策略；不得直接視為 ready |
| Discovery / auth | `/global/health`、`/doc`；V1 basic username 可設定 | V2 `/api/info`；Basic 保留、username 固定 opencode，`OPENCODE_PASSWORD`，舊 password env 相容 [S2,S3] | **Breaking** discovery 與 custom username；effective auth/ready response 在目標 release **Unknown** |
| Session Create / Get | `POST /session?directory=...`，bare session；`permission`、agent、metadata | `POST /api/session`，`location`、`permissions`、agent、model；回傳 `{data: ...}`，Get 亦包 data [S8 session group] | **Breaking** route、scope、payload、response；原回應 receipt 必須重新證明，不能以後續 GET 補造原 Create evidence |
| Prompt / identity | `/session/{id}/prompt_async`；messageID、agent、parts，204；Lab 注入 model | `/api/session/{id}/prompt`；id、text 等輸入，回傳 data inbox user；支援 resume / delivery [S8 session group] | **Breaking**：不能只改 URL；one-attempt、request correlation、late response/ambiguous submit 與原 receipt 保證皆需 fixtures；不得利用新 API 自動 replay |
| Status / messages | `/session/status`；messages bare array，before pagination、assistant.parentID correlation | `/api/session/active`；message API 為 data + opaque cursor；typed messages；Assistant schema 沒有 V1 parentID，content/model 改形 [S8 message/session-message] | **Breaking** parser 與 terminal provenance；active absence/idle 都不能當 Completed。如何將原 submitted input 精確綁定 terminal outcome **Unknown，硬 gate** |
| Event / SSE | V1 `/event`、`/global/event`；MAF 現在實際採 GET polling，沒有 SSE consumer | `/api/event` 明示 volatile，斷線會漏、slow consumer 可失敗；另有 experimental durable session log（after sequence / follow）[S8 event/session] | **Breaking** event contract；不應為相容研究新增第二套 observer。若未來使用 SSE，必須處理 gap/bound/budget；不能將 event 或 idle 當 terminal proof |
| Permission API | GET `/permission`、`/question`；session rule 欄位 permission/pattern/action；MAF 不回覆 | `/api/permission/request`、session permission routes；reply payload decision；forms 取代部分 user-input surface [S8 permission/session] | **Breaking** route/shape；保持只觀察 WaitingForApproval，不得自動 once/always 或 form reply；question 等價行為 **Unknown** |
| ReadOnly / rules | deny-all 後 read/glob/grep 允許、外部目錄及 secrets 拒絕 | V2 ordered action/resource/effect；shell/subagent 等名稱改變；最後 matching rule 生效、預設 external_directory allow [S6,S8 permission schema] | **Breaking security semantics**：不可只靠 supported config normalization；必須對 effective session+agent policy、unknown tools、outside workspace、secret paths 做完整負面驗證；不接受默認值 |
| Agent configuration | `agent`/permission/prompt；GET `/agent` bare array | native config agents/permissions/system；GET `/api/agent` 包 location/data；V1 supported config 有 normalization [S2,S3,S6] | **Confirmed config migration intention；Breaking wire**。檔案可讀不等於 enforced ReadOnly；有效設定/custody 仍 **Unknown** |
| Model / recipient | Lab model object 嚴格要求 providerID/modelID；觀察同樣欄位 | snapshot `Model.Ref` 為 providerID/**id**/optional variant；session create/model switch 設定 model；config variant 可加入 model reference [S8 model/session] | **Breaking** strict model binding；LabOpenCodeBoundary 不能原樣使用。fallback、auxiliary title/summary/compaction recipients 與 digest/effective config **Unknown** |
| Cancellation / Abort | POST `/session/{id}/abort` boolean；current auth + original identity，ack 不等於 Cancelled | POST `/api/session/{id}/interrupt` 回 interrupted boolean；resume=true 可續跑 pending steering/control [S8 session] | **Breaking** delivery/response semantics；不能將 process Stop、local cancel 或 interrupt ack 當 terminal；不啟用 resume；durable cancellation evidence 是否可映射 **Unknown** |
| SDK / plugins | V1 `@opencode-ai/sdk`；MAF 本身是 .NET direct HTTP、不是 TS plugin | V2 network client `@opencode/client`；`@opencode/sdk` 是不同整合 surface；plugin default definition id/setup(ctx)，舊實作不載入 [S4,S7,S8 manifests] | **Breaking** SDK/plugin contracts；MAF 無須為使用 HTTP 引入 Node SDK、plugin host 或新 project。插件/動態載入仍屬 downstream risk |
| Recovery / stored scope | Provider OpenCode + origin/root/agent/rules hash，v4 receipt/delivery assumptions | V2 session/message/model shapes 與 scope 改變；CLI/config locations 可能共用 [S2,S8] | **Unknown** cross-version durable recovery；禁止拿 V1 handle 在 V2 reattach/補送/轉換 evidence。需先決定 version-bound scope，不能順便更改 production schema |

### 5.2 官方來源索引

- S1：[V1 Server](https://opencode.ai/docs/server/) — 啟動、health、session、abort、events 的官方 V1 說明。
- S2：[V2 Migrate from V1](https://dev.opencode.ai/v2/docs/migrate-v1/) — 明列 Server/Client/Plugin breaking；supported config normalization 與 auth migration。
- S3：[V2 API reference](https://dev.opencode.ai/v2/docs/api) — 當日 generated API，標為 Experimental；不是 MAF acceptance evidence。
- S4：[V2 JavaScript client](https://dev.opencode.ai/v2/docs/build/client/) — network client `@opencode/client`。
- S5：[V2 CLI](https://dev.opencode.ai/v2/docs/cli) — shared background server、standalone/server selection。
- S6：[V2 Permissions](https://dev.opencode.ai/v2/docs/permissions/) — rule order/defaults、external directory 與 agent policy。
- S7：[V2 Plugin migration](https://dev.opencode.ai/v2/docs/build/plugins/migrate-v1) — setup/context registration 與舊 plugin 不相容。
- S8：[固定官方 source snapshot](https://github.com/anomalyco/opencode/tree/19d87a72e6da026cc3f0cc620808d336d9fb0a04)，實際讀取的檔案：
  - [session protocol](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/protocol/src/groups/session.ts)、[message protocol](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/protocol/src/groups/message.ts)、[event protocol](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/protocol/src/groups/event.ts)、[permission protocol](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/protocol/src/groups/permission.ts)。
  - [Model.Ref](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/schema/src/model.ts)、[permission schema](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/schema/src/permission.ts)、[message schema](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/schema/src/session-message.ts)。
  - [serve handler](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/cli/src/commands/handlers/serve.ts)、[server process](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/cli/src/server-process.ts)、[standalone ownership](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/cli/src/services/standalone.ts)。
  - [client manifest](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/client/package.json)、[SDK manifest](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/sdk/package.json)、[CLI manifest](https://github.com/anomalyco/opencode/blob/19d87a72e6da026cc3f0cc620808d336d9fb0a04/packages/cli/package.json)。

未使用第三方指南作為技術結論依據；沒有執行官方範例、installer 或 upstream tests。

## 6. Next Increment Proposal（未授權、未實作）

**下一步建議：版本固定的離線 protocol/evidence design gate，暫不啟動 V2。**

1. Architect 先決定 PR #4 的 R1 風險處置與接受條件；保留目前 NotRun，不把文件審查當完成驗收。
2. 選定具體 V2 release/source/artifact 組合，離線固定必要 schema 的來源及 digest。不得用本機 `opencode` 名稱推論版本，也不安裝/替換目前 executable。
3. 在現有 OpenCode adapter 範圍設計明確 V1/V2 protocol selection；保留 IExternalHarnessClient / IResumableExternalHarnessClient、OpenCodeExecutor 與既有 policy/effect boundaries。先考慮既有 options 中的明確版本設定與少量 private parsing/serialization helpers；不新增 interface、factory、provider registry、generic plugin framework 或 project。
4. 版本選擇必須在 Create/Prompt 前固定並驗證 schema；unknown/mismatch fail closed。不能根據 POST 失敗再切換版本重送，也不能跨版本沿用 durable scope/handle。若既有 evidence 契約無法表達 V2 correlation，停止於 schema/provenance decision，不降低 guard。
5. 在既有 tests 專案規劃純 in-memory HTTP fixtures：V1 行為不變、V2 incompatible discovery、session/location/policy echo、model id/variant、prompt original-response correlation、message pagination/terminal mapping、late/ambiguous response、permission pending、interrupt ack 非 terminal、unknown version 與 cross-version recovery 拒絕。此階段可先撰寫契約案例，但執行仍受 hold 約束。
6. Process readiness 與 protocol compatibility 分開驗收。未來專用 server 若需要 stdout handshake，先審查固定 buffer、敏感資料處理與超時 owner 留存；不能將 PR #4 丟棄輸出的行為誤認為已支援。不能用 shared service、shell wrapper 或 executable 改名解決 ownership 問題。

此 proposal 不授權開始 V2 Adapter implementation。未來獨立授權時，先完成 Fake 契約與 reviewer gate，再依 clearance 做 build/tests；OS lifecycle、Socket preflight、effective configuration verification、真實 ReadOnly / 模型請求都各自另設 scope。ProductionProtected、Network denial、原 receipt、Unknown/no-retry 與 public entrypoint 狀態維持原 gate。

## 7. Risk & Blocker List / Architect Decision

| ID | 風險 / blocker | 關閉條件 |
| --- | --- | --- |
| B1 | corporate execution hold；無本次 build/test evidence | 裝置適用的 IT/security clearance + owner scope；不得自行重試 |
| B2 | PR #4 新增 37 cases 皆 NotRun；Windows/Linux 與 failure paths 未驗證 | 按 inventory 取得真實 evidence；OS tests 必須另授權 |
| B3 | post-start setup exception owner 交接缺口（R1） | Architect 接受明確限制，或另行小範圍修補與驗證；未重現不寫成 confirmed incident |
| B4 | 尚無核准 V2 artifact；API/source snapshot 不是 release compatibility guarantee | 固定 target、來源、digest 與精確 schema；不泛稱 V2 已相容 |
| B5 | V2 shared-service / stdout readiness / approved Windows executable 策略未定 | 專用 original-child ownership 與 handshake 設計審核；不觸碰共享程序 |
| B6 | ReadOnly、有效 agent/model/recipient 與設定合併語意未證實 | 等價 deny-by-default、secrets/outside root、override/fallback/auxiliary outbound 負面證據 |
| B7 | V2 input→terminal correlation、receipt、interrupt 與 durable recovery 未映射 | 原 response evidence 與 current authorization 不弱化；不能證明就維持 Unknown/Blocked |
| B8 | descendants、Unix forced termination、faulted observation repair 仍有限制 | 保留 owner、明示 unresolved 操作責任；不以擴大 kill/registry/supervisor 範圍掩蓋 |

**Go**：完成本次靜態審查與官方來源研究，將文件交付 Architect。

**Conditional Go**：Architect 可考慮接受 PR #4 作為「internal、未接入、未驗證的 primitive」之設計方向；須明確處置 R1 並保留 B1/B2 gate。這不是建議現在 merge，也不是宣稱程式已驗收。下一個可審核增量是 version-pinned offline contract design。

**No-Go**：目前不具備無條件 PR acceptance、V2 operational readiness、production activation 或真實 OpenCode execution 條件。本次不 merge PR、不改 Production Trust、不公開執行入口、不發模型請求、不實作 V2 Adapter。最終接受與後續授權由 Architect / Owner 決定。
