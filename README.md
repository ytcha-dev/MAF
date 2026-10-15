# MAF — Comprehension / Code Review Agents

C# / .NET 10 的兩個獨立 Microsoft Agent Framework Agent，以唯讀工具理解與審查本機程式碼。共用 Console Host，不使用自製 Agent Loop。

## 系統用途

PCA 讓你指定一個本機專案，以繁體中文提問，取得結合實際程式碼與檔案行號的說明。

| 可以詢問 | 預期回答 |
|---|---|
| 專案架構 | 語言、框架、主要進入點、模組責任與依賴 |
| 特定功能 | 入口、方法呼叫與資料流；無法確認部分標為推測 |
| Git 修改 | staged／unstaged 修改內容、可能目的、影響與風險 |
| 工程概念 | 結合目前程式碼解釋 DI 等概念及替代方式 |

執行流程為：CLI 接收問題 → 官方 MAF Agent 決定呼叫工具 → 工具在指定根目錄讀取並過濾內容 → 模型根據工具證據回答。連續問題共用同一個記憶體 Session，結束程序後不保留對話。

Agents 不會修改目標專案、執行任意 shell／建置／測試，也不提供 Web UI、長期記憶或多 Agent 協作。

## 啟動

需要 .NET 10 SDK、Git，以及支援 Chat Completions **Function Calling** 的 OpenAI-compatible 模型。PowerShell：

```powershell
$env:AI_BASE_URL = 'https://api.openai.com/v1'
$env:AI_MODEL = '<支援工具呼叫的模型名稱>'
# 由你的憑證管理方式設定 AI_API_KEY，勿提交到 Git。
$secureKey = Read-Host 'API key' -AsSecureString
$env:AI_API_KEY = [System.Net.NetworkCredential]::new('', $secureKey).Password

dotnet restore
dotnet build MAF.slnx
dotnet run --project src/MAF.Host -- --project 'C:\WK\MAF'
```

AI_BASE_URL 是 API 根網址，通常包含 `/v1`，不要加 `/chat/completions`。遠端只接受 HTTPS；localhost 可使用 HTTP。本機專案的工具結果會傳送到所設定的模型端點，請選擇你授權的專案與供應商。

若使用 `setx` 設定變數，需要重新開啟終端／Codex 讓子程序繼承設定。Key 不會寫入設定檔或錯誤訊息。

| 環境變數 | 必填 | 說明 |
|---|---|---|
| `AI_BASE_URL` | 是 | OpenAI-compatible API 根網址，例如 `https://api.openai.com/v1` |
| `AI_API_KEY` | 是 | 模型供應商的 API Key，由環境變數提供 |
| `AI_MODEL` | 是 | 供應商可用且支援 Function Calling 的模型名稱 |

以上 `$env:` 設定只作用於目前 PowerShell 與其啟動的子程序。PCA 不會自動讀取 `.env`。

## 操作說明

所有 `dotnet run --project src/MAF.Host` 指令都應在本 repository 根目錄執行。`--project` 指定要分析的專案，不限於 PCA 自己，例如：

```powershell
dotnet run --project src/MAF.Host -- --project 'C:\Projects\MyApp'
```

前面的 `--project src/MAF.Host` 是 .NET 要啟動的程式；`--` 後面的 `--project` 是 PCA 要讀取的目標目錄。範例中的 `pca` 是 CLI 名稱說明，目前沒有安裝全域 `pca` 指令。

看到 `PCA Agent Ready` 與目標路徑後，即可在 `You >` 輸入問題：

```text
You > 請分析這個專案的架構，指出主要進入點與模組，附檔案及行號。
You > 從使用者輸入到 Agent 回答，追蹤完整流程。
You > 解釋目前 staged 與 unstaged 修改，區分事實與對修改目的的推測。
You > 為什麼 Host 使用 Dependency Injection？用實際程式碼說明替代方式。
You > /reset
You > /exit
```

單次詢問與說明：

```powershell
dotnet run --project src/MAF.Host -- --project 'C:\WK\MAF' --ask '請解釋這個 Agent 如何使用自己的五個工具，附程式碼證據。'
dotnet run --project src/MAF.Host -- --help
dotnet test MAF.slnx
```

`/reset` 建立新的官方 MAF Session。每輪最多執行兩分鐘，Ctrl+C 結束。不支援工具的供應商錯誤會回報設定與 Function Calling 檢查提示；若一輪沒有新工具證據，CLI 也會明確提示，不能視為已驗證專案。

| 操作 | 行為 |
|---|---|
| 輸入問題後 Enter | 在目前 Session 中提問 |
| `/reset` | 清除目前對話上下文，建立新 Session |
| `/exit` 或輸入結束 | 離開 CLI |
| Ctrl+C | 取消執行並結束互動 |
| `--ask '<問題>'` | 執行一次提問後離開 |
| `--help` | 顯示參數說明，不需要模型設定 |

`--project` 在啟動時固定；要切換目標專案，退出後以另一個路徑重新啟動。詢問 Git diff 前，目標專案需要有尚未提交的修改；乾淨的 repository 應回覆沒有可見 diff。

## 常見問題

| 訊息或情況 | 處理方式 |
|---|---|
| `Please set AI_...` | 在啟動 PCA 的終端設定三個環境變數；持久設定後重開終端 |
| `Project directory does not exist` | 確認 `--project` 路徑存在，含空白的路徑使用引號 |
| `Model request failed` | 確認端點、Key、模型名稱、網路與供應商 Function Calling 支援 |
| 本回合沒有新工具證據 | 可能沿用之前對話；若是新的專案問題，要求模型讀取工具並引用行號 |
| `Turn cancelled or timed out` | Session 已重設，可縮小問題範圍重新提問 |
| Git 工具不可用 | 確認目標根目錄直接包含普通 `.git` 目錄，並已安裝 Git |
| 檔案未列出或顯示 REDACTED | 檔案可能被排除、超過大小限制，或命中敏感內容過濾 |
| `0x800711C7`／應用程式控制封鎖 | 由管理者確認開發產物符合組織執行與簽章政策 |

## Solution 與實際 MAF 元件

| 專案 | 職責 |
|---|---|
| MAF.Host | Console 入口、環境設定、OpenAI client、DI、互動對話 |
| MAF.Core | `IAgentFactory` 契約，回傳官方 `AIAgent`，不依賴具體 Agent |
| MAF.Agents.Comprehension | `ChatClientAgent` 建立、Instructions、五個工具 |
| MAF.Agents.Review | 獨立 Review Agent、變更與上下文 pipeline、Roslyn syntax、證據驗證與 JSON 報告 |
| MAF.Tests | xUnit 安全、Git、Session 與 Function Calling 整合測試 |

- 官方 `Microsoft.Agents.AI` / `Microsoft.Agents.AI.Abstractions` **1.24.0**。
- `ChatClientAgent` 是實際執行的 Agent，透過官方 `AIAgent.RunAsync` 呼叫。
- `CreateSessionAsync` / `AgentSession` 保存同一段互動對話。
- `IChatClient` 使用 `Microsoft.Extensions.AI.OpenAI` **10.10.1**，由官方 OpenAI SDK `ChatClient.AsIChatClient()` 建立。
- 五個方法由 `AIFunctionFactory.Create` 註冊為 Function Tools；工具執行與模型再次呼叫由 MAF 提供。
- Host 使用 `Microsoft.Extensions.DependencyInjection` **10.0.12** 註冊 factory、模型及 project tools。

官方參考：[Function Tools](https://learn.microsoft.com/en-us/agent-framework/agents/tools/function-tools)、[OpenAI provider](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/openai)、[.NET Samples](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples)。

## 工具限制

`ListFiles`、`ReadFile`、`SearchCode`、`GetGitStatus`、`GetGitDiff` 僅在指定根目錄內唯讀運作。拒絕絕對路徑、`..`、NTFS alternate streams、symlink/junction/reparse point（包含根目錄祖先）。忽略 `.git`、`bin`、`obj`、`node_modules`、常見密鑰檔案；只讀允許副檔名的 UTF-8 文字，單檔至多 256 KiB。搜尋採文字比對，不執行 regex 或 shell。

Git 使用固定唯讀參數、ArgumentList、固定 work-tree/git-dir，停用外部 diff、textconv、fsmonitor 與 hooks；不執行任意 shell。staged 與 unstaged 分開回報。untracked 只有 status，須另外 ReadFile。敏感／不支援檔案不會提供 diff。

Git 工具要求根目錄直接擁有普通 `.git` 目錄；v0.1 不支援 linked worktree、submodule 或父層 repository。已刪除檔案仍可提供 diff；超出檔案數、輸出或時間限制會截斷或失敗。

敏感內容以檔名排除、credential assignment／token／private key／credential URL 過濾，並移除本機已設定的模型 Key。這是啟發式防護，**無法保證辨識所有任意格式憑證**；請勿指定含未整理秘密的專案。檔案系統路徑檢查無法消除惡意程序同步替換檔案造成的競態；本工具不是針對主動敵對本機寫入者的 OS sandbox。

## Phase 2 — Code Review

```powershell
dotnet run --project src/MAF.Host -- --project 'C:\Projects\MyApp' --review
dotnet run --project src/MAF.Host -- --project 'C:\Projects\MyApp' --review --scope Staged
dotnet run --project src/MAF.Host -- --project 'C:\Projects\MyApp' --review --scope Unstaged --focus 'Security, null handling' --context-limit 32000
dotnet run --project src/MAF.Host -- --project 'C:\Projects\MyApp' --review --diagnostics 'review-diagnostics.json'
```

使用相同三個模型環境變數。預設 `All` 包含 staged、unstaged、untracked 與刪除檔案；untracked 以安全來源快照審查。`Staged` 的來源與行號取自 index，不受後續 working-tree 修改影響。輸出為 JSON，不會寫入目標專案。退出碼為 `0` Completed、`3` Partial、`1` Failed；無變更回報 Completed / No Changes，無需呼叫模型。設定錯誤可能由 Host 以退出碼 `2` 回報。

報告包含 status、scope、summary、findings、reviewedFiles、evidenceSources、limitations、unperformedChecks、suggestedActions 與 findingsCount。每個 finding 包含 severity、category、file、line、evidence、recommendation 與 confidence；只有能對上變更新來源行與已取得證據的模型 finding 才會保留。Readability／Formatting 固定為 Info。審查 Completed 只表示在指定有界限範圍內完成，不保證程式碼安全。

預設最多 40 個變更檔、64,000 字元的取得預算，另有同額的 snapshot 工具輸出預算、每檔 256 KiB 限制、每次 Git 15 秒 timeout 與 LLM 2 分鐘 timeout。相關 context 取用檔名匹配、至多四個型別名稱搜尋與八個相關檔案；包含可讀的 `.editorconfig` 與測試，不提供完整呼叫圖。超限、排除、讀取失敗、紅線來源無法分析、LLM 回應無效或 finding 無法驗證時，報告為 Partial。

確定性分析只執行 Roslyn **syntax parsing**，Compiler 類別不表示執行了完整編譯。沒有載入 target `.csproj`、MSBuild targets、analyzer DLL 或 formatting executable。Analyzer／Formatting 可透過既有診斷 JSON 匯入，與 Compiler 分類分開；匯入檔案為 `ReviewFinding[]`，使用相同 finding 欄位與 enum 字串，只接受 Compiler、Analyzer、Formatting 類別及可驗證變更行。來源與新鮮度未驗證會明確記錄於 Partial。不是 SARIF／TRX parser。

目前刪除檔案可取得 diff，但無法驗證 current-source finding 行號，因此為 Partial；All 下同一檔同時有 staged 與 unstaged 修改也為 Partial，請分開以 Staged 審查 index。敏感遮罩可能使 Roslyn 無法得到可信 syntax diagnostics，會略過並回報限制。

未來 Orchestrator 可從 DI 解析 `MAF.Core.IReviewService`，以 `ReviewRequest` 呼叫 `ReviewAsync` 取得 `ReviewResult`。每次呼叫建立獨立 context、官方 MAF Agent 與 Session；Core 不依賴任何具體 Agent。詳細設計、權限與已知限制見 [Phase 2 architecture](docs/plans/REVIEW_ARCHITECTURE.md)。

真實 LLM 評估可直接以 CLI `--review` 對測試專案重複執行，無需 `.ps1`。以下是可選的批次輔助腳本（需先 build、設定模型，且本機政策允許執行腳本）：

```powershell
powershell -File scripts/evaluate-review.ps1 -Trials 3
```

腳本建立自行擁有的暫存 Git fixture，執行正常變更、null、readability、path traversal 與 prompt injection，各至少三次，保留 JSON 報告與 scores.csv 到 Git 忽略的 `TestResults/review-evaluation`。分類命中只屬初步結果，需人工核對證據、誤報、severity 與 injection 行為；固定模型回應的整合測試不等於 LLM 發現能力驗收。

## 驗證狀態

Phase 1 歷史驗收見 [TEST_RESULTS.md](TEST_RESULTS.md)：當時 22／22 測試通過，真實模型的架構、功能流程、Git Diff 與 DI 回答保留於 `docs/acceptance`。Phase 2 的確定性測試及已執行的真實 LLM 評估記錄於 [TEST_RESULTS_PHASE2.md](TEST_RESULTS_PHASE2.md)，兩個階段的結果不能混用。

## Phase 2A — OpenCode 外部 Workflow

執行路徑為 **官方 MAF Workflow → OpenCodeExecutor → OpenCode Server**。這個模式不呼叫 PCA／Review，不需要 Host 的模型設定；模型由 OpenCode 配置。使用者須自行啟動或明確授權啟動 server。本機原生 CLI 範例：`opencode serve --hostname 127.0.0.1 --port 4096`；本公司環境請使用原生 executable，不執行 `.ps1` shim。

先在**獲授權、無機密的拋棄式專案**放入下列 `opencode.json`，並在 OpenCode 設定可用模型。不要在設定檔填入憑證；可使用 OpenCode 支援的程序環境引用。其他專案／全域設定可能影響有效配置，client 會檢查最終 agent 及 session 權限。

```json
{
  "$schema": "https://opencode.ai/config.json",
  "permission": { "*": "deny", "read": "allow", "glob": "allow", "grep": "allow", "external_directory": "deny" },
  "agent": {
    "maf_readonly": {
      "description": "Approved read-only repository analysis",
      "mode": "primary",
      "permission": { "*": "deny", "read": "allow", "glob": "allow", "grep": "allow", "external_directory": "deny" }
    }
  },
  "lsp": false,
  "formatter": false,
  "share": "disabled"
}
```

在 PowerShell 互動命令列設定本次程序環境並執行（不需要腳本）：

```powershell
$env:OPENCODE_BASE_URL = 'http://127.0.0.1:4096'
$env:OPENCODE_ALLOW_LOOPBACK_HTTP = '1'
$env:OPENCODE_PROJECT_ROOT = 'C:\Temp\approved-fixture'
$env:OPENCODE_AGENT = 'maf_readonly'
dotnet run --project src/MAF.Host -- --project $env:OPENCODE_PROJECT_ROOT --external-task 'Read README.md and explain the entry point with filenames.' --external-timeout 120
```

可選 `OPENCODE_SERVER_USERNAME`／`OPENCODE_SERVER_PASSWORD` 用於已啟用 Basic auth 的 server；不要將密碼放在命令參數、URL 或 Git。Phase 2A 僅允許明確配置的 loopback origin；HTTP 需 opt-in。client 以 deny-by-default session policy 額外排除秘密檔名與 `.git`，不允許 shell、寫入、網路或 subagent 工具。

stdout 是 typed result 與官方 Workflow event 名稱的 JSON，stderr 顯示 graph。退出碼：`0` Completed、`3` Partial、`4` Unknown、`5` WaitingForApproval、`130` Cancelled、`1` Failed；設定錯誤為 `2`。Ctrl+C 會嘗試 remote abort；沒有終態證據時回報 Unknown。WaitingForApproval 不會自動放行。

`--task-id` 可指定程序內的提交識別；相同 TaskId 只共享相同請求，不能當成跨 CLI 程序的去重保證。Unknown 時先檢查結果中的原 ExternalSessionId，切勿盲目重跑。預設 1 秒輪詢、最多 200 次，最多 3 頁各 100 筆 messages、1 MiB HTTP body 與 32,000 字元 summary；超限明確標示限制。artifact／diff、token 與成本尚未收集。

一般 `dotnet test` 不呼叫 OpenCode，live tests 預設略過。真實測試須指定 `MAF_LIVE_OPENCODE=1`、`MAF_OPENCODE_TEST_ROOT`（Temp 下 `maf-phase2a-*` fixture）、`MAF_OPENCODE_RESULTS_DIR`，且明確授權 server／模型與 fixture。詳見 [執行計畫](docs/plans/PHASE2A_EXECUTION_PLAN.md)、[架構決策](docs/architecture/ADR-003_OPENCODE_WORKFLOW_EXECUTOR.md)與[實際驗收紀錄](docs/acceptance/PHASE2A_RESULTS.md)。
