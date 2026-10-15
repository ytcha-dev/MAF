# ADR-004：本機 durable execution 與明確 recovery

日期：2026-10-15。狀態：**實作與測試原始碼已撰寫，未 build／未執行驗證；決策待驗證及 owner 接受**。安全事件後的靜態開發不代表 Phase 2B-1 已驗收。

## 範圍與依據

保留 [ADR-003](ADR-003_OPENCODE_WORKFLOW_EXECUTOR.md) 的官方 Microsoft Agent Framework Workflow 1.24.0、OpenCodeExecutor、direct API、唯讀權限及獨立 PCA／Reviewer。僅實作 Roadmap Phase 2B-1，不加入 approval UI、Registry、Hybrid、A2A、其他 adapter 或寫入工具。Roadmap 的 proposed 狀態不等於實作授權；本次授權來自 owner 的明確 Phase 2B-1 指令，執行限制由後續安全事件指令收緊。

## 分層與資料

- Core 提供 `IExecutionStore`、`IExecutionOwnership`、durable snapshot、central transition rules、`IResumableExternalHarnessClient`，不依賴 SQLite 或 SDK。
- `MAF.Persistence.Sqlite` 實作短交易、TaskId primary key、完整舊 snapshot + version compare-and-swap、OS exclusive file ownership。
- `DurableExecutionService` 與其 persistence decorator 在 Workflow 邊界保存狀態；Host 透過 DI 建構服務。官方 WorkflowBuilder／InProcessExecution 繼續驅動既有 executor，不新增 agent loop。
- OpenCodeClient 只負責 HTTP、correlation、scope 和唯讀 policy；沒有 DB 操作。新增 prepare identity 與 GET-only reattach，保留原有 bounded observation、pending permission 優先及 parentID 對應。

保存 TaskId、SHA256 request fingerprint（normalized root、原 task text、timeout ticks、readonly policy）、endpoint／root／agent／policy scope digest、run/execution/session/message IDs、submission phase、state、version、UTC timestamps、固定診斷碼及 abort-requested。**不保存 prompt、回應 body、transcript、憑證或任意 exception text**。Fingerprint 是一致性檢查，不是機密加密或存取控制；短 prompt 的 digest 仍可能被猜測。

TaskId、root 與外部 IDs 是必要 metadata，不應放入機密。DB 由目前標準使用者的既有 filesystem 權限保護；沒有自行改 ACL／加密／政策。CLI／provider summary 仍沿用既有 best-effort redaction，不能宣稱任意來源內容都無機密。

## 狀態與提交順序

`Created → SessionCreating → ReadyToSubmit → SubmissionIntended → Running → Completed/Partial/Failed/Cancelled`。

遺失提交確認：`SubmissionIntended → SubmissionUncertain`。`WaitingForApproval`、`Running`、`Unknown` 可依原 session 新證據恢復；Unknown 不是 terminal。Terminal evidence 不覆寫；若 provider 後續證據矛盾，該次觀察回報 Unknown，保留原 durable 終態。Completed 是外部執行結束，不代表結果已經 Reviewer 或人類接受。

SubmissionPhase 單調前進：`None → SessionCreationIntended → SessionKnown → PromptIntended → Acknowledged`。狀態、階段、immutable identity、版本及 completion timestamp 一起驗證；不能先跳 Running 再補 intent。

1. 先保存穩定 workflow/execution/message identity 與 Created。
2. 保存 session creation intent，才 POST session。
3. 收到 session 後保存 session ID，才進入 prompt submission。
4. 保存 prompt intent，才 POST prompt_async **一次**。
5. 收到確認才保存 acknowledged；模糊失敗只查同一 session/message，絕不重送。

SQLite 與遠端 HTTP 不存在共同 transaction；**沒有 exactly-once delivery 保證**。若 session POST 接受後 response／本機 commit 遺失，session ID 可能不可得；保留 execution/message IDs，Unknown，需 operator 調查，不能建立替代 session。即使 crash 發生在 intent 保存後但實際 POST 前，也不自動送出，選擇安全而非自動繼續。

## Restart / timeout

`--recover <TaskId>` 僅使用已保存 identity；驗證相同 scope/root、health/schema/agent/session policy 後 GET status、permission/question、correlated messages。無 submitted evidence 或 session identity 時 Unknown；無網路、404、policy mismatch 或模糊回應不當成遠端 task failed，不替換 ID。已確認 terminal 記錄仍可明確 recovery 重新讀 provider body；若 provider 不可用，該次 response 為 Unknown，原 terminal 記錄保持不變。

`--recover-pending` 依 TaskId 排序，最多 100 筆非 terminal，每筆 observe once（含現有 bounded transient GET retry），stdout 為單一 JSON array 或 text/verbose；超過容量需明確逐筆 recovery，不默默漏掉。每筆 root/scope 不符會拒絕，避免跨 provider／workspace 啟動工作。沒有 background scheduler 或自動 startup POST。

Durable 模式等候／poll 預算到期回 Unknown，不 abort，供稍後 recovery；Ctrl+C 仍透過獨立 5 秒 abort budget，只有遠端取消證據才 Cancelled。舊的未配置 DB Phase 2A 路徑保持 timeout/abort 行為。重新連接 discovery 與 Workflow observation 各有 timeout 上限；大量筆數的 batch 非固定整體 SLA。

本機只保存結果 metadata，重新取得完整摘要依賴 OpenCode 保留該 message。若遠端刪除 session、頁面上限外無法定位 message 或模型沒有 final response，不能重建原輸出。模型品質、連線逾時、Host 停止、遠端真正失敗以不同可觀測證據區分；不能僅憑 idle 判 Completed。

## SQLite / ownership

Schema 1 使用 `PRAGMA user_version`、`executions(task_id PK, version, terminal, snapshot)`。新 DB 在短 transaction 建表及寫 version；已知 v1 重用，未知版本 fail closed，不做破壞性 migration。每次連線 Pooling=false、busy timeout 5 秒、synchronous FULL；單筆 compare-and-swap 是 atomic SQL statement。沒有跨 network 的 SQLite transaction。

沿用 [SQLite smoke](../acceptance/SQLITE_SMOKE_RESULTS.md) 的 Microsoft.Data.Sqlite 10.0.0 與 explicit SQLitePCLRaw.bundle_e_sqlite3 3.0.5，避免先前預設 2.1.11 的 NU1903。**新 project 尚未 restore，不能宣稱目前 graph 已通過 vulnerability audit。** 未改 NuGet sources 或無關 dependencies。

Host 要求 `--execution-db`／`MAF_EXECUTION_DB` 明確 absolute local file，拒絕 Temp、目標 source root、Git/SVN ancestor、network drive 及已知 reparse point。不得把正式資料放入測試 Temp。測試 store 可用隔離 Temp DB，但全部暫停。本設計不改既有造成事件的 fixture 路徑或名稱。

TaskId 對應 DB 旁 `.locks/<SHA256(TaskId)>.lock`，FileShare.None，空內容，owner 跨服務／Host instance 互斥，process 結束由 OS 釋放 handle；檔案殘留不等於鎖仍被持有。Owner identity 在 store 更新時檢查，版本防 stale write。每個服務最多兩個 executor 同時運作。僅限同一台機器、同一個 DB 路徑；複製 DB、換另一 DB 路徑或 remote storage 不提供去重。鎖不是惡意同帳號程序的安全隔離，也不宣稱已實測程序終止的鎖釋放。

## 驗證與待辦

Fake provider 與 temporary SQLite 測試已撰寫，包括新 service/store、fault injection、post response loss、並行 TaskId、錯誤 transition、provider outage、waiting/cancellation、敏感內容排除、future schema rejection；不能將 exception injection 當成真的 process crash。Separate-process crash、live restart、source/index/status unchanged，以及真實 provider unavailable/recovery **未實作驗收 runner、未執行**，須取得安全許可後補足。

實際證據／限制見 [results](../acceptance/PHASE2B1_RESULTS.md)、[security event](../acceptance/PHASE2B1_SECURITY_EVENT.md)。所有新增程式的編譯、API 相容性、native dependency loading、concurrency 及 durable guarantees 仍待執行驗證。
