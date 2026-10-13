# Project Comprehension Agent v0.1

C# / .NET 10 的單一 Microsoft Agent Framework Agent，以唯讀 Function Tools 解釋本機程式碼。沒有 OpenCode Harness、Adapter、向量資料庫或自製 Agent Loop。

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

詳見 [TEST_RESULTS.md](TEST_RESULTS.md)。建置成功；Phase 1／2 曾通過 17 項測試。最新版本的 Agent 執行受此電腦 Windows Code Integrity 企業簽章政策阻擋，無法宣稱完成真實模型品質驗收。需要管理者允許開發產物或使用組織核准的簽章流程後，重跑測試及四個詢問。
