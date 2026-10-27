# Personal Alpha — First Useful Workflow

> Validation follow-up：Owner 確認本輪 scoped execution clearance 後，於同一 baseline
> `a780063` 完成 solution build（0 warnings/errors）、16/16 新 cases 與四種實際 CLI
> walkthrough。既有 regression 為 722 passed / 3 failed；一次未改碼 diagnostic 9/9
> 不抹除原失敗。OS/Live 仍 NotRun。下文 NotRun 為 authored baseline 的歷史狀態；
> 當前證據與限制見 [validation results](PERSONAL_ALPHA_VALIDATION_V1_PILOT_RESULTS.md)，
> live readiness 見 [V1 pilot plan](../plans/PERSONAL_ALPHA_V1_FIRST_LIVE_PILOT.md)。

日期：2026-10-27。Product Delivery First：R1 authored closure 完成後，主要開發優先序轉向單一本機任務的可用流程。V2 保留既有相容性研究，不實作、不阻塞 Alpha。

**本增量：Offline Demo 已實作，Build / Tests / Process / Socket 全部 NotRun。** 公司 execution hold 未解除，以下操作說明只供未來已核准環境使用，並非本次執行紀錄或 clearance。未聲稱 Live Success 或產品價值已獲使用者驗證。

## 1. 功能盤點與重用決策

| 使用者需求 | 原有能力 / 位置 | 本增量交付 / 邊界 |
| --- | --- | --- |
| 單一 Workspace + ReadOnly 任務 | LocalSoloApplication.Preview；absolute local workspace、reparse/remote/control-character 拒絕 | 直接重用；demo 只檢查目錄 metadata，不讀取其內容、不寫入 workspace |
| 預覽 Harness、模型、資料流 | `--local config / preview / readiness`；candidate metadata，未授權 | 重用 preview；另明示實際參與者是 scripted PCA / OfflineDemo / scripted Reviewer，沒有模型；candidate OpenCode 不是實際啟動的 Harness |
| 明確確認 | 原 `submit` 無條件 Blocked；sample 僅展示 | 新 `--local demo` 要求輸入 `run-demo`，其他輸入或 EOF 安全取消、不建立 task。確認僅限 offline demo，不能轉為 live consent |
| 自動 PCA→Harness→Reviewer | internal ControlledHandoffWorkflow + official Workflow engine + typed DTO 已有；原測試使用受控 composition | 直接重用該 Workflow、ITaskBriefing / IControlledDelegation。新增固定 in-memory participants；不複製 OpenCode coding loop、不建立新 Orchestrator |
| 不搬運 Agent 結果 | 原 LocalSoloOutcomeMapper / Presentation 可呈現已產出的 WorkflowOutcome | 完成 scenario 的 TaskEvidence 自動交給 scripted Reviewer；同一次 journey 自動顯示 review / HumanGate，使用者不用貼結果 |
| 查詢狀態 / 結果 | sample 不是真實 task；mapper 可區分 status、review、evidence、limitations | 同一 CLI session 的 `status` / `result` 查詢同一個已完成的 offline outcome，不重新執行、不分配第二 task |
| 透明度 | 既有結果包含 stages / Harness / task IDs | 執行中逐階段顯示 PCA、OfflineDemo Harness、Reviewer、HumanGate；結束後 Current worker=none。這是本機 stage，非遠端即時事件 |
| Failed / Unknown / Completed / WaitingForApproval | 既有 ExternalExecutionStatus 與 HumanGate | 四個明確 scripted scenarios；只有 Completed 進 Reviewer。Unknown 不重送；Waiting 不回 permission / form、不自動核准 |
| Persistence / recovery | 既有 SQLite、Lab journal、protected durable evidence/recovery | 保留，不複製、不為 demo 建立 DB 或 journal。新 demo task **session-only**；關閉後不可查詢/恢復，尚非持久化任務管理 |
| OpenCode Adapter / PCA / Reviewer models | 既有 V1 direct HTTP、native PCA / Reviewer、recipient guards | 本增量不構造真實 adapter/client。未來重用既有 components，但仍需各自 live activation / recipient / trust 審核 |
| Live submission | LocalSoloApplication.Submit / ExternalConsole fail closed | 保持 ProductionTrustUnavailable；demo 的 `submit-live` 亦呼叫同一 unconditional denial。無 operational factory、無新 production route |

程式變更範圍：LocalSoloConsole 的嚴格參數解析與 async journey dispatch、Program 在既有 `--local` 分支 await（仍早於 native/DI）、一個小型 LocalSoloDemo，以及既有測試專案中的 LocalSoloDemoTests。無新增 interface、factory、registry、套件或 project。

## 2. 操作流程（未執行）

在已核准環境，以既有建置產物執行；此處不提供自動 restore/build，也不從公司 hold 中啟動它：

```text
dotnet <approved-build>/MAF.Host.dll --local demo --workspace <absolute-existing-local-directory> --objective "Explain this read-only task"
```

一個 CLI invocation 輸入 Workspace 與任務 → 同頁顯示預覽 → 輸入 `run-demo` → 建立一個 session-only task → 既有 Workflow 自動傳遞 briefing、scripted Harness result、Reviewer assessment → 顯示結果。安全確認是必要一步；不要求重複輸入任務或貼上結果。

接著可輸入：

```text
status
result
submit-live
quit
```

`submit-live` 只會顯示 Blocked，不接真實 provider。EOF 也會結束 session。沒有 approve、retry、resume 命令。重複 status/result 只呈現同一快照，不重新 dispatch。

正常 CLI session 的 exit 0 表示操作流程完成，不表示 task 為 Completed 或被接受；應讀取明確 status/review。若已建立 demo task 但 Workflow/mapper 意外失敗，保留 task/run identity、顯示 Unknown，不虛構 execution ID、不重送；session 結束回傳 exit 1。輸出裝置或輸入本身的 IO 故障仍可能中斷 session，這不是 durable recovery。

為驗證狀態 UX，可另選 `--scenario completed|failed|unknown|waiting`（預設 completed）。這些是使用者明確選擇的測試劇本，**不是對 workspace 任務的實際推論結果**。Completed 只代表 scripted Harness stage 完成；Reviewer 固定指出 InsufficientEvidence，HumanGate 為 NeedsHumanReview，沒有使用者 acceptance、source verification 或模型成功。

Evidence 區域保留 Unavailable / NotEstablished；不會把 DTO 參考或 demo-generated identity 偽造成 VerifiedSourceEvidence。任務目標與可辨識敏感資料沿用既有 bounded/redacted presentation；不接收原始外部 transcript、不讀取憑證。

## 3. 產品驗收標準（與技術驗證並列）

| 指標 | 第一版可觀察的驗收標準 | 本次狀態 / 未來量測 |
| --- | --- | --- |
| 建立任務 | 一個 invocation 指定 Workspace/任務，單次明確確認後建立一個 task；無重填或改貼內容 | 已實作與撰寫案例，**NotRun**；確認不等於 live authorization |
| 執行透明度 | 每個 stage 看得出是 PCA、哪個 Harness、Reviewer；scripted/live 標記清楚，模型缺失顯示 none/Unavailable | 已實作，**NotRun**；即時遠端狀態未接通 |
| 審查流程 | Harness result 自動以同一 task/execution identity 送 Reviewer，顯示 assessment / limitations；手動結果搬運 **0 次** | 已實作 offline typed handoff，**NotRun**；真實模型 Reviewer 尚未驗證 |
| 狀態可靠性 | Completed、Failed、Unknown、WaitingForApproval 不互相轉換；status/result 不產生新 dispatch；Unknown 不自動 retry | 四 scenarios + authored cases，**NotRun** |
| 人工介入 | WaitingForApproval 安全停止、不核准；未知命令無副作用；未確認/EOF 可安全退出 | 已實作，**NotRun**；真實權限 API 尚未測試 |
| 實際價值（主要 gate） | 同一個真實 ReadOnly 任務，對照單獨使用 OpenCode 並人工交接 PCA/Reviewer 的基線，MAF 必須減少搬運與協調；不得以測試數或 scripted demo 代替 | **Unproven**。待授權 personal pilot；以下記錄須由實際使用者完成 |

首次 pilot 使用同一明確 workspace/task、預先定義相同品質要求，在「原有人工操作」與「MAF」各跑一次；若模型等不一致，記錄差異，不能直接歸因 MAF。記錄：

- 為提供任務上下文、傳遞 Harness 輸出、要求 Reviewer、拼接最終結果，人工複製貼上次數與應用切換次數。
- 人工主動協調時間（排除模型等待），以及結果是否仍需重貼/人工修補才能審查。
- 是否保留必要 evidence、limitations、Unknown 與權限停止；節省操作不能犧牲可追溯性。

第一版最低價值門檻：正常 MAF journey 的 Harness→Reviewer 結果搬運為 **0 次**，總人工資訊搬運少於人工 baseline，主動協調時間下降；如沒有明顯改善，Alpha 價值維持未證實，優先修 user journey，不投入 Registry/V2/平台擴建。第一組 pilot 樣本只作方向性證據，不是普遍效能保證。GPT/Codex/OpenCode/Reviewer 全部自動整合尚未完成，不能宣稱已消除跨工具所有搬運。

## 4. 實際 Live 缺口與下一個最小 Increment

尚未接通：approved executable 與 dedicated child/transport composition、current operation 與 recipient/data-flow approval、有效 OpenCode ReadOnly agent/model/configuration 與 downstream custody、live submission/status/receipt/terminal correlation、真正 PCA/Reviewer 模型、自動持久化與跨 CLI 查詢。ProductionProtected genuine trust 條件與公司 hold 仍存在；Personal Alpha 不是替代授權。

下一個最小交付應先讓 Owner 在**已獲准環境**完成本 Offline Journey walkthrough，記錄上述產品指標與阻礙。若 task session 遺失是首要障礙，另行授權最小 local task-history projection，先審核既有 persistence 可重用面與儲存位置，再做跨 invocation 的唯讀 status/result；不順便新增 recovery 或 submit authority。若優先 personal live pilot，另列一個既有 V1 adapter 的最小單任務授權範圍；不以 V2 或企業平台擴建作前置條件。

本次不開始 live activation、持久化 schema 或 V2 Adapter。沒有大範圍重構；先交付並驗證已寫成的最小 offline journey。

## 5. 驗證紀錄與交付界線

- R1：owner 在 launch 前建立；setup exception 攜帶 exact Owner、保留 partial drains/cleanup，不 implicit kill/detach。新增 5 個 Fake Facts，涵蓋 ownership retention、pending cleanup、repeated Dispose、Unresolved、unknown exit 與 persistent disposal failure；**全部 NotRun**。獨立 commit `b325d20`。
- Alpha：LocalSoloDemoTests 新增 **3 Facts + 3 Theories / 8 rows = 11 intended cases**；既有 official Workflow + presenter 的 typed correlation、四種狀態、confirmation/EOF、blocked live、invalid scenario、重複查詢無重送。只使用 memory 與既有目錄 metadata；不建 fixtures。
- Build、上述 16 個新 authored cases、existing Host/Workflow/ownership/guard regressions、OS tests 與 live checks：**NotRun**；沒有 TRX / pass count。靜態 diff/whitespace 與 source review 不代表 compilation 或 runtime success。
- 未建立 DB、未啟動程序或 socket、未發模型請求、未 merge PR；原有 `.opencode/` 不更動。R1 可交付審查，Alpha 可交付未執行程式碼與操作說明；操作驗收與產品價值仍待實際證據。

交付分支：R1 留在 `feat/owned-harness-process-lifecycle`（PR #4）；Alpha 自該 R1 commit 分出 `feat/personal-alpha-offline-journey`，避免將產品功能混入 PR #4。沒有自動合併任何分支。
