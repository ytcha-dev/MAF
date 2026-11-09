# Personal Pipeline MVP — 多 Agent 分工與交流驗證

日期：2026-11-09 · 分支：`claude/happy-bohr-5usl9t`

## 目標

用**一個**明確 opt-in 的 CLI 指令，證明多個不同角色的 Agent 透過官方 Microsoft Agent Framework Workflow 分工、交接與調配：

```text
PCA（理解專案、提出任務簡報）
  → OpenCode（唯讀執行任務）
  → Evidence Reviewer（獨立讀原始碼審查 OpenCode 的證據）
  → Human Gate（彙整，交給人判斷）
```

這是個人、單一操作者、唯讀的 MVP。它**刻意不經過** S2B–S2D 的 production trust／attestation 鏈；那些程式碼與封鎖保持不變，`--external-task`、`--local submit` 依然拒絕 `ProductionTrustUnavailable`。

## 實作範圍（全部重用既有元件）

| 角色 | 重用元件 | 新增 |
|---|---|---|
| 編排 | `ControlledHandoffWorkflow`（官方四階段 Workflow） | — |
| PCA | `TaskBriefingAdapter` + `ComprehensionAgentFactory` | — |
| OpenCode | `OpenCodeClient`、`OpenCodeExecutor`（唯讀 session rules、不重送 POST、逾時 abort） | `PersonalAccess`：每個 provider effect 只綁定原始唯讀 request |
| Reviewer | `VerifiedSourceMode` + `BoundedSourceReader` + `NativeEvidenceReviewer` | — |
| CLI | — | `--pipeline`（`PipelineConsole.cs`、`PersonalPipeline.cs`） |

角色之間的界線（由既有元件執行）：

- PCA 的簡報必須逐字回填 taskId／workspace／objective；擴大範圍會在 OpenCode 被呼叫前直接 Blocked。
- OpenCode 只收到原始 objective，不是模型改寫後的文字；session 權限 deny-by-default，只開 read／glob／grep。
- Reviewer 不相信 OpenCode 的摘要：MAF 從 OpenCode 的 `read` 工具紀錄取出來源候選，**自己**重新讀檔驗證後才交給 Reviewer；Reviewer 必須真的用工具讀過每個 reference 才能給 SufficientEvidence。
- Human Gate 只彙整，不代表人已同意。

## 驗證結果

### 建置與離線測試

- `dotnet build MAF.slnx`：0 警告、0 錯誤。
- 新增 `PersonalPipelineTests` 14 個案例全部通過：真實 OpenCodeClient 協定走 fake OpenCode server，PCA／Reviewer 用 scripted chat client。
  - 四個角色依序交接，Reviewer 獨立驗證來源 → `ReadyForHumanReview`，exit 0；只有 1 次 session POST、1 次 prompt POST，沒有 abort。
  - PCA 擅自擴大 objective → OpenCode 完全沒被呼叫（0 HTTP request）→ `Blocked`，exit 1。
  - OpenCode 只給摘要、沒有讀檔證據 → Reviewer 不被呼叫，判定 `InsufficientEvidence` → `NeedsHumanReview`，exit 3。
  - 參數、loopback-only OpenCode、存取綁定的負面案例。
- 完整離線 regression：見本文末。

### 端對端實跑（真實 OpenCode 1.19.0 server）

`scripts/pipeline-demo/run-demo.sh`：真實 MAF CLI、真實官方 Workflow、真實 OpenCode 1.19.0 server；三個模型角色由本機 scripted OpenAI-compatible endpoint 扮演（不是真模型，只用來證明協定與編排）。

```text
[分工] 各角色階段結果
  - PCA（理解專案、提出任務簡報） → Completed
  - OpenCode（唯讀執行任務） → Completed
  - Reviewer（獨立讀原始碼審查證據） → Completed
  - Human Gate（彙整，交給人判斷） → Completed

[交流] 官方 Workflow 交接順序
  Invoked PCA → Completed PCA → Invoked ExternalDelegation → Completed ExternalDelegation
  → Invoked EvidenceReviewer → Completed EvidenceReviewer → Invoked HumanGate → Completed HumanGate

[OpenCode] 狀態: Completed   HTTP requests: 21   來源候選: 1
[Reviewer] 評估: SufficientEvidence   已獨立驗證來源: 1 筆 [src/Calculator.cs:1-4]
[Human Gate] ReadyForHumanReview      exit=0

模型端看到的呼叫：
  pca      stream=false tools=5  （PCA 自己的專案工具）
  coder    stream=true  tools=3  （OpenCode 只拿到 read/glob/grep）→ 呼叫 read
  coder    stream=true  toolResults=1 → 最終回答
  reviewer stream=false tools=2  （ReadEvidence/SearchEvidence）→ 讀證據
  reviewer stream=false toolResults=1 → SufficientEvidence
```

## 已知限制

- **OpenCode 版本**：來源擷取接受 `1.18.x`／`1.19.x`（見 R2）。其他版本 Reviewer 會拿不到來源，結果固定是 `NeedsHumanReview`。
- **每階段上限 2 分鐘**（`--stage-timeout` 10–120 秒，沿用既有 `HandoffValidation`）。大型任務請縮小 objective。
- 尚未用真實 LLM 跑過；真模型是否穩定產生合法 JSON 簡報、是否乖乖用 read 工具，要用你的模型實測。
- 每次執行都在記憶體內，不支援中斷後續跑；不寫 SQLite。
- 只有唯讀；沒有寫入、shell 或網路工具。

## 完整離線 regression

`dotnet test tests/MAF.Tests`（Linux 容器、.NET SDK 10.0.114）：**1638 通過、0 失敗、3 略過**（既有 3 個 live OpenCode 案例，預設略過）。原本 1624 + 新增 14。

## R1：第一次真模型實跑後的修正（2026-11-09）

**觀察**：Owner 在個人 Windows 機器用公司 OpenAI-compatible 模型 `Qwen3.5-122B-A10B` 第一次實跑。四個階段依序被官方 Workflow 呼叫，但 PCA 是 `Failed / InvalidTaskBrief`，OpenCode 與 Reviewer 照設計 `Skipped`，Human Gate 是 `Blocked`，沒有送出任何 OpenCode HTTP request。

**原因**：pipeline 原本重用共用的 `TaskBriefingAdapter`，它要求模型逐字回填 taskId／workspace／objective、不准多欄位、每條限制 ≤256 字。這是給 scripted 測試模型的嚴格協定；真模型常用 ```json 包住回覆、帶 `<think>`，或加入額外欄位，就被整份退件。

**修正**（共用的 `TaskBriefingAdapter` 及其嚴格測試不變）：

- 新增 pipeline 專用的 `PipelineBriefing`：PCA 只回傳 `plan`／`focusFiles`／`risks`。taskId、workspace、objective、ReadOnly 一律由程式填入原始值，模型的額外欄位（例如自己寫的 `objective`）被忽略，**無法擴大範圍**。
- 計畫／重點檔案／風險會被截斷到既有上限，並顯示在輸出的 `[PCA]` 區塊。
- `JsonReplyCleaner` 包在 PCA 與 Reviewer 的 chat client 外面：只對最終文字回覆去掉 `<think>` 與程式碼框；工具呼叫不變，Reviewer 既有的嚴格驗證也不變。
- 解析失敗時，PCA（或 Reviewer 判定 `ReviewUnavailable` 時）的原始回覆前 800 字會印到 stderr（已遮蔽敏感內容），方便判斷是模型格式還是內容問題。

**驗證**：新增 9 個案例，`PersonalPipelineTests` 共 23 個全部通過，包含：
- 帶 `<think>` 與 ```json 的 PCA 回覆被接受，模型自寫的 objective 無效。
- 無法解析的 PCA 回覆 → OpenCode 0 request、Blocked，stderr 印出原始回覆。
- 包了程式碼框的 Reviewer 回覆仍通過嚴格驗證。

完整 regression **1647 通過、0 失敗、3 略過**；建置 0 警告／錯誤。demo（真實 OpenCode 1.19.0，PCA 回覆刻意包 `<think>` 與 ```json）跑到 `ReadyForHumanReview`，exit 0。**還沒有用真模型重跑。**

## R2：真模型首次完整跑通，放寬 OpenCode 版本線（2026-11-09）

**觀察**：補上 `maf_readonly` agent 後，Owner 用 `Qwen3.5-122B-A10B` 對真實專案 `A02S6` 跑通四個角色：PCA `Proposed`（計畫、7 個重點檔案、5 個風險）→ OpenCode `Completed`（486 HTTP requests，回覆帶檔案路徑）→ Reviewer `Completed` → Human Gate。這是**第一次以真模型完成多 Agent 分工與交接**。但 `來源候選: 0`，Reviewer 判 `InsufficientEvidence`，Human Gate 為 `NeedsHumanReview`。

**原因**：Owner 的 OpenCode 是 1.19.4；來源擷取原本只接受 `1.18.35`／`1.19.0` 兩個精確版本，其他版本一律回傳 0 筆。

**比對**：在容器以真實 OpenCode 1.19.0 與 1.19.4 server 跑同一流程，擷取 `/session/{id}/message` 的 read 工具 part，欄位結構完全相同（part、state、input、metadata、display 各層 key 一致）。

**修正**：版本檢查改為 `^1\.(18|19)\.\d{1,4}$`。格式其餘部分仍嚴格檢查，任何不符都整批不採用；MAF 仍會獨立重讀每個候選檔案並比對 anchor 與 digest，所以版本放寬不會產生假證據，最壞情況是退回 `NeedsHumanReview`。

**驗證**：真實 OpenCode 1.19.4 由 `來源候選: 0 / InsufficientEvidence` 變為 `來源候選: 1 / SufficientEvidence`。新增 11 個版本案例（含 1.20.0、1.17.9、`1.19.4-beta` 等拒絕案例）以及 1.19.4／1.19.5 的既有來源整合案例。完整 regression **1661 通過、0 失敗、3 略過**；建置 0 警告／錯誤。真模型的 1.19.4 重跑待 Owner 執行。
