# PRD — Project Comprehension Agent (PCA)

**版本：** v0.1 MVP  
**技術：** C# / .NET 10 / Microsoft Agent Framework  
**開發方式：** Codex 協助實作  
**狀態：** Ready for Development

## 1. 專案背景

目前個人 Side Project 大量使用 AI 協助開發。雖然開發效率提高，但開發者可能無法即時理解 AI 產出的程式碼、架構與技術決策。

希望建立一個能夠直接讀取本機專案的 AI Agent，協助開發者理解：

- 專案整體架構。
- 特定功能如何實作。
- 程式碼執行流程與依賴關係。
- AI 最近修改了什麼，以及修改原因。
- 程式碼涉及的工程概念與設計原理。

本專案同時作為第一次實際導入 **Microsoft Agent Framework（MAF）** 的實驗。

## 2. MVP 核心目標

建立一個基於 MAF 的獨立 Agent，透過自然語言詢問本機專案相關問題，並得到有程式碼證據的解釋。

**完成標準：能讓開發者理解一個原本不熟悉的 AI 開發專案。**

## 3. 技術方案

| 項目 | 選擇 |
|---|---|
| 語言 | C# |
| Runtime | .NET 10 |
| Agent Framework | Microsoft Agent Framework |
| 模型介接 | OpenAI-compatible API |
| 初期介面 | CLI 互動式對話 |
| 專案存取 | 本機檔案系統，只讀 |
| Git | 讀取 Diff、檔案變更 |
| 資料儲存 | 暫不需要資料庫 |

.NET 10 為初始技術選擇，若實際 MAF 套件存在相容性限制，Codex 應先說明並使用官方支援版本。

模型設定使用環境變數：

- `AI_BASE_URL`
- `AI_API_KEY`
- `AI_MODEL`

不得在程式碼內硬編碼 API Key。

### MAF 整合要求

必須真正使用 Microsoft Agent Framework 建立和執行 Agent。

建議使用 `ChatClientAgent` 或目前官方建議的等效類型，搭配 `IChatClient` 與 Function Tools。

不得只使用一般 HTTP Client 呼叫 LLM，卻將其命名為 MAF Agent。

此版本只需要單一 Agent，不需要 Multi-Agent Workflow。

## 4. MVP 功能

### F01 — 專案架構理解

使用者指定一個本機專案路徑。

範例：

`請幫我分析這個專案的架構`

Agent 應能：

- 辨識專案使用的語言與框架。
- 尋找主要進入點。
- 說明重要目錄與模組的責任。
- 解釋模組之間的依賴關係。
- 附上相關檔案路徑。

### F02 — 特定功能追蹤

範例：

`這個專案的登入流程是怎麼運作的？`

Agent 應能：

- 搜尋相關程式碼。
- 追蹤主要方法呼叫鏈。
- 以步驟說明資料如何流動。
- 說明各層程式碼的責任。
- 解釋為什麼可能採用這樣的設計。

無法確認的動態呼叫、外部服務或間接依賴，必須明確標示為推測或待確認。

### F03 — Git Diff 解釋

範例：

`解釋這次修改了什麼，以及有什麼影響`

Agent 應能：

- 分析目前未提交的 Git 修改。
- 包含 staged 與 unstaged diff。
- 說明修改目的與可能影響。
- 識別值得學習的工程概念。
- 提醒可能的風險。

修改目的只能根據程式碼和可取得的上下文推論，不得將推論宣稱為開發者的真實意圖。

### F04 — 工程知識解釋

當使用者詢問：

`為什麼這裡要用 Dependency Injection？`

Agent 不應只解釋 DI 定義，而應結合實際專案說明：

1. 目前程式碼如何使用。
2. 解決什麼工程問題。
3. 不使用會有什麼差異。
4. 有哪些替代方式。
5. 對這個專案是否合理。

回答應以繁體中文為主，保留必要的英文技術術語。

## 5. Agent Tools

透過 MAF Function Tools 提供以下能力：

| Tool | 功能 |
|---|---|
| `ListFiles` | 列出專案檔案 |
| `ReadFile` | 讀取指定檔案及行號 |
| `SearchCode` | 搜尋程式碼與關鍵字 |
| `GetGitDiff` | 取得 Git 修改 |
| `GetGitStatus` | 查看檔案異動狀態 |

所有工具必須限制在指定的專案根目錄。

需要防止 `../`、符號連結、重新解析點等方式越界讀取未授權檔案。

預設忽略 `.git` 內部資料、`bin`、`obj`、`node_modules`、大型二進位檔與常見密鑰檔案。

Git 工具只允許明確指定的唯讀操作，不得執行任意 Shell 指令。

檔案內容視為不可信輸入，不得執行其中的指令。讀取到的 API Key、密碼或其他憑證不得包含在送往模型的內容中。

## 6. CLI 操作體驗

預期操作：

```text
pca --project "C:\Projects\MyApp"

PCA Agent Ready
Project: C:\Projects\MyApp

You > 幫我分析整體架構

Agent > 專案包含以下核心模組...
         ...
         參考：src/Program.cs:12-45

You > 解釋登入流程

Agent > 登入從 LoginController 開始...
```

需要支援在同一個互動 Session 中連續提問。

第一版不需要 Web UI。

## 7. 回答品質要求

每次回答盡可能遵循以下結構：

**結論：** 先回答問題。

**程式流程：** 以步驟解釋實際執行方式。

**背後原理：** 說明設計原因及工程概念。

**程式碼證據：** 附上檔案路徑、可驗證的行號。

**注意事項：** 尚未確認的推論與潛在風險。

不要求每次都輸出全部段落，應依問題複雜度調整。

Agent 必須區分「程式碼能證明的事實」與「模型推測」。

## 8. MVP 不包含

以下功能不在 v0.1 範圍：

- Web UI。
- RAG、Embedding、向量資料庫。
- 多 Agent 協作。
- 自動修改程式碼。
- 自動執行測試或建置命令。
- 專案長期記憶。
- 自動生成架構圖。
- 自製完整 Agent Runtime。
- 與 Open WebUI 或 OpenCode Adapter 整合。

## 9. 開發階段

### Phase 1 — MAF 基礎

- 建立 .NET Console 專案。
- 安裝 MAF 所需 NuGet 套件。
- 配置 OpenAI-compatible 模型。
- 建立真正的 MAF Agent。
- 完成基本互動式聊天。

驗收：使用者可以與 MAF Agent 成功對話。

### Phase 2 — 專案工具

- 實作本機檔案工具。
- 實作程式碼搜尋。
- 實作 Git Diff 與 Status。
- 將工具註冊為 MAF Function Tools。
- 完成工具路徑安全限制。

驗收：Agent 能正確取得指定專案內的程式碼與 Git 資訊，無法存取專案外檔案。

### Phase 3 — 理解能力

- 設計 PCA System Instructions。
- 實作架構分析。
- 實作功能追蹤。
- 實作修改解釋。
- 實作工程概念解釋。

驗收：使用者能透過自然語言理解一個真實專案的功能與變更。

### Phase 4 — 品質驗證

以至少一個真實 Side Project 進行驗證：

1. 能指出主要進入點與模組。
2. 能追蹤一項功能的主要流程。
3. 能正確解釋一組 Git Diff。
4. 能根據實際程式碼解釋一個工程概念。
5. 關鍵結論具有可驗證的檔案證據。
6. 專案外檔案無法被存取。
7. 敏感檔案內容不得洩漏到模型請求。

每個驗收項目需提供成功或失敗的結果，不得以「看起來正常」代替測試。

## 10. 開發規則

Codex 執行時必須遵守：

1. 優先使用 Microsoft Agent Framework 官方 SDK 與文件。
2. 不自行重新實作 MAF 已提供的 Agent Loop。
3. 不過度設計抽象層。
4. 一個 Phase 完成後再進行下一個。
5. 重大技術變更需說明原因。
6. 專案必須可以獨立啟動。
7. 提供 README、設定方式與使用範例。
8. 不要為尚未存在的需求加入架構。
9. 若模型供應商不支援 Function Calling，需清楚回報，不得假裝工具已成功執行。
10. 完成後交付實際測試結果及已知限制。

## 11. 最終交付

- 可執行的 .NET CLI 專案。
- Microsoft Agent Framework 整合。
- 五個唯讀專案工具。
- 四種核心理解能力。
- README 與設定範例。
- 測試及驗收結果。

**MVP 定義：開發者能使用真正的 MAF Agent，透過自然語言理解本機 AI 開發專案，並取得有證據的回答。**


## PRD v0.1 — Solution 架構補充要求

本專案不採用單一 Console Project，而是採用 Multi-Project Solution。

### 專案結構

- `MAF.Host`：Console 入口、DI 註冊、模型設定、Agent 啟動。
- `MAF.Core`：Agent 共用契約、基本執行協調機制。
- `MAF.Agents.Comprehension`：PCA Agent 實作、Instructions、Tools、專案分析能力。
- `MAF.Tests`：核心功能與安全性測試。

### 核心規則

1. 每個 Agent 使用獨立的 Class Library。
2. Agent 可以擁有自己的 Services、Tools 與設定。
3. Core 不得直接依賴任何具體 Agent。
4. Host 負責 Agent 註冊與依賴注入。
5. 使用 MAF 官方的 `AIAgent` 抽象與執行 API。
6. 不重新實作 MAF 既有的 Agent Loop、Session 或 Workflow Engine。
7. 不要建立尚未使用的 Agent 專案。
8. 不要預先實作多 Agent 協作、通訊協定或分散式架構。
9. 未來應能新增 Agent Class Library，而不需要大幅修改 Core。

請依這個 Solution 結構調整原本的 PRD 實作計畫，直接進行開發。