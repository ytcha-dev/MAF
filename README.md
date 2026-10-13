# Project Comprehension Agent v0.1

C# / .NET 10 的單一 Microsoft Agent Framework Agent，以唯讀 Function Tools 解釋本機程式碼。沒有 OpenCode Harness、Adapter、向量資料庫或自製 Agent Loop。

## 系統用途

PCA 讓你指定一個本機專案，以繁體中文提問，取得結合實際程式碼與檔案行號的說明。

| 可以詢問 | 預期回答 |
|---|---|
| 專案架構 | 語言、框架、主要進入點、模組責任與依賴 |
| 特定功能 | 入口、方法呼叫與資料流；無法確認部分標為推測 |
| Git 修改 | staged／unstaged 修改內容、可能目的、影響與風險 |
| 工程概念 | 結合目前程式碼解釋 DI 等概念及替代方式 |

執行流程為：CLI 接收問題 → 官方 MAF Agent 決定呼叫工具 → 工具在指定根目錄讀取並過濾內容 → 模型根據工具證據回答。連續問題共用同一個記憶體 Session，結束程序後不保留對話。

v0.1 不會修改專案、執行 shell／建置／測試，也不提供 Web UI、長期記憶或多 Agent 協作。

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

## 驗證狀態

詳見 [TEST_RESULTS.md](TEST_RESULTS.md)。建置成功，22／22 測試通過；原先系統封鎖已解除。已以 PCA 自身專案完成真實模型架構、功能流程、Git Diff 與 DI 概念驗收，回答保存於 `docs/acceptance`。驗收範圍為本專案與目前設定模型，回答細節仍需核對程式碼。
