# v0.1 測試與驗收紀錄

日期：2026-10-13（Asia/Taipei）。Windows、.NET SDK 10.0.401、Runtime 10.0.12。驗收專案為 PCA 自己：`C:\WK\MAF`。

## 最終結果

- `dotnet build -m:1 --no-restore`：成功，0 warnings、0 errors。
- `dotnet test -m:1 --no-restore`：22 成功、0 失敗、0 略過。
- 原先 Windows Code Integrity 封鎖已由使用者解除；本次 CLI、官方 MAF Session 與 Function Calling 整合測試皆可執行。
- 使用者設定的 OpenAI-compatible 模型與 `https://api.openai.com` 端點完成真實回答。測試未輸出、保存或提交 API key。
- 同一 CLI Session 的第二輪能正確回憶 staged/unstaged 檔名；該輪沒有新工具呼叫，CLI 如實顯示使用先前對話證據的提示。

## 各 Phase

| Phase | 結果 | 驗證 |
|---|---|---|
| 1 MAF 基礎 | 成功 | 官方 ChatClientAgent 型別、RunAsync、Session 訊息保留測試；真實 CLI 模型回答 |
| 2 專案工具 | 成功 | 5 tools 註冊、安全讀檔、原始行號、文字搜尋、staged/unstaged/deleted diff、敏感內容過濾 |
| 3 理解能力 | 成功（MVP 案例） | 真實模型執行架構、功能流程、Git diff、DI 概念四項詢問；Function Calling 整合測試通過 |
| 4 品質驗證 | 成功（本專案案例） | 下方 7 項逐項核對；不代表其他專案或所有模型均有相同品質 |

## Phase 4 七項驗收

| PRD 項目 | 結果 | 證據 |
|---|---|---|
| 主要進入點與模組 | 成功 | architecture.txt 指出 Program.cs 入口、四個專案責任、Factory 的 new ChatClientAgent；對照實際原始碼 |
| 一项功能主要流程 | 成功 | flow.txt：CLI → DI → Agent/Session → RunAsync；工具包裝與 ReadFile → Resolve → Lines → Redact 流程皆能對照來源 |
| 一組 Git Diff 解釋 | 成功 | staged 為 ProjectTools.cs，unstaged 為 ToolTests.cs；模型辨識 PEM 過濾修正與回歸測試，目的標為推測 |
| 結合程式碼解釋工程概念 | 成功 | di.txt：引用 Host 27–33 行與 Factory 5–11 行，解釋建構子注入、抽象工廠、手動 new 與容器替代方式 |
| 關鍵結論有檔案證據 | 成功 | 入口、工廠、DI、工具註冊及 ReadFile 的主要引用經人工對照；完整回答保留於 docs/acceptance |
| 無法存取專案外檔案 | 成功（測試覆蓋路徑） | `..`、絕對路徑、NTFS ADS、junction、junction 根目錄、父層 Git repository 拒絕測試 |
| 敏感檔案不洩漏到模型請求 | 成功（測試覆蓋格式） | .env 排除；read/search/diff redaction；MAF 整合測試記錄模型端收到的工具結果，不含測試憑證 |

真實模型回答已保存：

- [architecture.txt](docs/acceptance/architecture.txt)
- [flow.txt](docs/acceptance/flow.txt)
- [diff.txt](docs/acceptance/diff.txt)：第一次 diff 驗收，之後另補 diff 前綴及未結束 PEM 測試。
- [di.txt](docs/acceptance/di.txt)
- [diff-session.txt](docs/acceptance/diff-session.txt)：最終修正的 diff 與同一 Session 連續提問。

自動測試原始結果：`tests/MAF.Tests/TestResults/phase-final.trx`（保留本機，Git 忽略）。

## 本次驗收發現並修正

1. 原先只要出現 PRIVATE KEY 字樣就遮蔽整份檔案，導致 Agent 無法理解 ProjectTools 自己。改為實際 PEM block 偵測，保留換行與原始證據行號。
2. cancellationToken 命名參數原先被 generic token 規則遮蔽。加入識別字邊界及 access/refresh/auth/session token 規則，使正常取消參數可讀，憑證仍遮蔽。
3. 新增私鑰 diff 前綴、未結束 PEM block 回歸測試，避免修正誤遮蔽時降低保護。

新增測試共 4 個案例，22/22 通過。

## 品質限制與未完成項目

- 第一次 diff 回答把既存 credential URL 過濾 context 描述為新增；核心 staged/unstaged 判定正確，但细節仍需人工對照。最終詢問明確要求區分新增與 context。
- 最終 diff 回答正確限制在新增內容，但省略了要求的 hunk 行號；第一次回答的 hunk 已核對正確。引用格式仍需持續改善。
- flow 回答出現一次 IAgent 字樣，但實際型別為 AIAgent；其他引用與主要流程正確。
- DI 回答把部分已可由程式碼證明的結論也稱為推測；沒有把未知開發者意圖當作事實。
- 沒有靜態 AST 呼叫圖，動態呼叫與外部 SDK 內部行為需標為待確認。
- 敏感資訊過濾為啟發式；任意格式、編碼或多行憑證不保證完全識別。
- 未驗收其他 Side Project 或模型供應商；linked worktree、submodule、非 UTF-8 文字目前不支援。
- 路徑檢查不提供對抗惡意本機程序同步替換檔案的 OS 級 sandbox 保證。

## 重跑

```powershell
dotnet build MAF.slnx
dotnet test MAF.slnx
dotnet run --project src/MAF.Host -- --project C:\WK\MAF --ask '分析架構與主要進入點，引用檔案及行號'
dotnet run --project src/MAF.Host -- --project C:\WK\MAF --ask '追蹤從 CLI 輸入到工具呼叫與回答的流程，附證據'
dotnet run --project src/MAF.Host -- --project C:\WK\MAF --ask '解釋 staged 與 unstaged diff；修改目的必須標為推測'
dotnet run --project src/MAF.Host -- --project C:\WK\MAF --ask '用本專案程式碼解釋 DI 的用途、不使用的差異與替代方式'
```

Diff 驗收需有有意義的未提交修改。本次使用實際 redaction 修正與測試；驗收後一起 commit/push，交付時不刻意保留修改。
