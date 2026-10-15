# .NET 10 SQLite 可行性 Smoke 驗證

日期：2026-10-15。狀態：基本本機可行性通過；不是 Phase 2B durable execution 驗收。

## 範圍與環境

使用者明確授權建立獨立、拋棄式 .NET 10 專案，在 Temp 建立 SQLite .db、寫入、關閉重開、驗證資料、刪除並清理。沒有修改 MAF 正式程式碼、solution、套件參照或 NuGet 設定。

- Windows 一般使用者，.NET SDK 10.0.103，target `net10.0`。
- 沿用機器已啟用的 nuget.org 與 Microsoft Visual Studio Offline Packages；未新增來源或改公司政策。這不是獨立的組織安全審查證明。
- `Microsoft.Data.Sqlite` 10.0.0，明確參照 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5；實際載入的 SQLite native version 為 3.53.4。
- 專案與資料庫位置：`%TEMP%\maf-sqlite-smoke-ps1mp3jx\smoke.db`。專案未加入 MAF 或初始化 Git，現已移除。
- 每個操作使用不同 .NET 程序，連線 `Pooling=false`；using 區塊釋放連線。讀取使用 ReadWrite 開啟既有檔案，避免不存在時默默建立空資料庫。

## 實際執行與結果

| 檢查 | 結果 |
|---|---|
| 還原最終相依套件 | 成功，未關閉 NuGet audit |
| 最終 Build | 成功，0 warnings、0 errors |
| 建表與寫入 | 建立 Smoke 表，參數化插入 Id=1 與固定測試文字，明確 commit transaction |
| 實體檔案 | .db 存在，8192 bytes，前 16 bytes 為 SQLite format 3 標頭 |
| 關閉後重新讀取 | 新程序讀到恰好 1 筆，Id 與文字值完全符合 |
| 刪除 | 新程序 DELETE 恰好 1 筆測試資料 |
| 刪除持久化 | 再以另一程序重新開啟，COUNT(*)=0 |
| 清理 | 解析絕對路徑，確認為 Temp 直屬且具專案標記後刪除；專案、.db、bin、obj 全部移除 |
| MAF 狀態 | 正式程式碼無 diff；原有未追蹤檔案保留。當次無 repository 變更，因此沒有 smoke 專案 commit |

實際使用的指令（原 Temp 專案已刪除，並非目前可直接重跑的 MAF 指令）：

```text
dotnet nuget list source
dotnet restore SqliteSmoke.csproj --disable-parallel --verbosity minimal
dotnet build SqliteSmoke.csproj --no-restore -m:1
dotnet bin/Debug/net10.0/SqliteSmoke.dll write smoke.db
dotnet bin/Debug/net10.0/SqliteSmoke.dll verify smoke.db
dotnet bin/Debug/net10.0/SqliteSmoke.dll delete smoke.db
dotnet bin/Debug/net10.0/SqliteSmoke.dll verify-empty smoke.db
```

## 遇到的問題與修正

1. 最初預設 bundle 的 `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 由 NuGet 回報 NU1903 高嚴重性弱點。只在拋棄式專案中改用 bundle 3.0.5，重新還原／建置後無警告；沒有停用 audit 或更動 MAF 相依套件。
2. 初輪寫入已 commit，但測試工具後續查詢版本時仍引用已結束的 transaction，產生 InvalidOperationException。清除 command.Transaction 參照，移除初輪資料庫，再從乾淨檔案完整重跑，所有步驟 exit 0。
3. 沙箱曾阻擋 Python 讀取 Temp .db 標頭；改以已授權的一般使用者程序核對同一個測試檔案成功。未提權為管理員、未執行 .ps1、未繞過公司執行政策。

此文件依本次實際工具輸出整理；沒有把拋棄式程式或生成物保留為永久測試套件。NuGet 一般共享快取未清除，以免影響其他專案。

## 限制與後續邊界

只確認這組 Windows／.NET／native SQLite 組合能建立檔案並跨程序保存及刪除資料。尚未驗證 crash consistency、WAL／鎖競爭、lease／多 worker、schema migration、加密、遠端 POST 原子性或 MAF restart reconciliation；也不代表所有公司機器或不同平台均通過。

Phase 2B-0 尚需在真正實作前核對當時的程式／測試與 provider baseline。沒有新增 `IExecutionStore`、durable mapping、approval 或 Connector Registry。下一步以 [Roadmap v0.2](../ROADMAP.md) 為準，實作另需授權。
