# v0.1 測試與驗收紀錄

日期：2026-10-13（Asia/Taipei）。測試平台：Windows、.NET SDK 10.0.401、Runtime 10.0.12。

## Phase 1 — MAF 基礎

- 成功：四專案 Solution 建置，官方 `ChatClientAgent` 型別、`RunAsync`、同一 Session 第二輪保留第一輪訊息的測試。
- 成功：CLI `--help`。
- 尚未通過：真實模型對話。使用者已設定模型變數，並明確授權傳送此專案經過濾程式碼至 `https://api.openai.com`；實際 CLI 在送出請求前被 OS 封鎖。

## Phase 2 — 專案工具

Phase 2 commit `54db15e`：17/17 成功（含 Phase 1 Session 測試）。

- 成功：列檔、程式碼文字搜尋、ReadFile 原始行號。
- 成功：6 種越界／排除路徑（`..`、絕對路徑、`.env`、`bin`、NTFS ADS、`.git`）。
- 成功：Windows junction 拒絕、junction 根目錄拒絕、列檔不跟隨 junction。
- 成功：二進位／超大檔案拒絕。
- 成功：敏感 source 讀取與搜尋過濾、4 種常見 credential 格式過濾。
- 成功：測試 Git repository 的 staged、unstaged、已刪除 source diff；敏感 diff/status 排除。
- 成功：沒有根目錄 `.git` 時拒絕 Git 工具，避免存取父層 repository。

## Phase 3 — 理解能力

已實作四種任務 Instructions、可信證據／推論區分、prompt injection 防護指令，以及缺乏工具呼叫時的 CLI 提示。

新增 `MafExecutesRegisteredToolsAndReturnsSanitizedEvidenceToClient` 整合測試：測試 IChatClient 要求 ListFiles、ReadFile，驗證由官方 MAF 自動執行五個已註冊工具中的兩個，並回傳過濾內容。它是 deterministic 測試模型，不代表真實 LLM 理解品質。

最新測試：18 項，16 成功、2 失敗；2 項因 Windows 拒絕載入 `MAF.Core.dll` 無法執行。Debug 與 Release 都得到 `FileLoadException` / `0x800711C7`。並非以 skip 取代測試。

## Phase 4 — 以 PCA 本身作為真實專案

| PRD 驗收項目 | 結果 | 證據／限制 |
|---|---|---|
| 主要進入點與模組 | 未通過：環境受阻 | 自我架構分析 CLI 在模型請求前被 Code Integrity 拒絕 |
| 一項功能主要流程 | 未通過：環境受阻 | 未取得真實模型回答 |
| 一組 Git Diff 解釋 | 未通過：環境受阻 | Git 工具測試成功；模型解釋未完成 |
| 結合程式碼解釋工程概念 | 未通過：環境受阻 | 未取得 DI 概念真實模型回答 |
| 關鍵結論有檔案證據 | 未通過：環境受阻 | 工具行號已測試；模型引用正確性未驗證 |
| 無法存取專案外檔案 | 成功（工具層） | 越界、ADS、junction 測試通過 |
| 敏感檔案不洩漏到模型請求 | 部分成功，尚未完整驗收 | 檔案、搜尋、diff 過濾測試成功；模型端整合測試被 OS 阻擋，任意格式 secrets 無法保證 |

## 系統封鎖證據

Windows `Microsoft-Windows-CodeIntegrity/Operational` 事件 3077 / 3033：
`MAF.Core.dll` 未符合 Enterprise signing level requirements。
Policy ID：`{0283ac0f-fff1-49ae-ada1-8a933130cad6}`。

本次未停用或繞過系統政策。需管理者允許本機開發產物或使用核准簽章流程後重跑。

## 重跑命令

```powershell
dotnet build MAF.slnx
dotnet test MAF.slnx
dotnet run --project src/MAF.Host -- --project C:\WK\MAF --ask '分析架構與主要進入點，引用檔案及行號'
dotnet run --project src/MAF.Host -- --project C:\WK\MAF --ask '追蹤從 CLI 輸入到工具呼叫與回答的流程，附證據'
dotnet run --project src/MAF.Host -- --project C:\WK\MAF --ask '解釋 staged 與 unstaged diff；修改目的必須標為推測'
dotnet run --project src/MAF.Host -- --project C:\WK\MAF --ask '用本專案程式碼解釋 DI 的用途、不使用的差異與替代方式'
```

Diff 驗收需要保留至少一項有意義的未提交修改；乾淨 repository 應回答沒有可見 diff。依專案 commit／push 原則，交付時不刻意留下未提交修改。
