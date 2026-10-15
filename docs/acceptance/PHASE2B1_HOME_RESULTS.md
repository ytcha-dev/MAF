# Phase 2B-1 家用環境離線驗證

日期：2026-10-15（Asia/Taipei）。驗證 revision：`072fc8c`。
使用者確認目前在家並要求協助驗證；本次限既有 source 的 restore、build 與離線測試，未連接模型或 OpenCode，也未移轉公司 artifacts。公司安全事件歷史結論不變。

## 實際結果

環境：Windows / win-x64，.NET SDK 10.0.401（另有 10.0.201），Git 2.53.0.windows.2。執行前 `MAF_LIVE_OPENCODE` 與 `MAF_EXECUTION_DB` 均未設定。

| 檢查 | 結果 |
|---|---|
| `dotnet restore MAF.slnx --verbosity minimal` | exit 0，7 projects 還原成功 |
| `dotnet build MAF.slnx --no-restore --configuration Debug` | exit 0，0 warnings / 0 errors |
| `dotnet list MAF.slnx package --vulnerable --include-transitive` | exit 0，目前來源未回報任何專案有已知易受攻擊套件；不等於保證無漏洞 |
| Offline test discovery | 成功列出測試，排除 OpenCodeLiveTests |
| 新增 4 classes focused run | TRX Completed；39 executed / 39 passed / 0 failed / 0 skipped |
| 完整 offline regression | TRX Completed；151 executed / 151 passed / 0 failed / 0 skipped；live tests 由 filter 排除 |

Focused filter：`FullyQualifiedName~MAF.Tests.DurableExecutionTests|FullyQualifiedName~MAF.Tests.DurableExecutionStateTests|FullyQualifiedName~MAF.Tests.OpenCodeRecoveryTests|FullyQualifiedName~MAF.Tests.DurableConsoleOutputTests`。

Regression filter：`FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests`。兩輪均使用 `dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore`，並設定各自的 TRX logger 與 results directory。39 cases 已包含在 151 中，不能相加。

Resolved graph：Microsoft.Data.Sqlite / Core 10.0.0、SQLitePCLRaw bundle/config/core/provider 3.0.5、SQLite 3.53.4。Host output 存在 win-x64 native e_sqlite3.dll；SQLite integration tests 實際通過。這不是以 SQL 查詢取得的 native sqlite_version。

本機 ignored evidence：`TestResults/home-validation/phase2b1-new.trx`、`phase2b1-offline.trx`、`dependencies.txt`。未提交原始 TRX 或含本機路徑的依賴輸出。

## 執行限制與驗收界線

初次 sandbox restore exit 1、沒有診斷輸出；獲准擴大執行權限後成功。第一次 dependencies 輸出因目錄不存在失敗，建立 evidence 目錄後已重新產生。補充 `dotnet --info` 在 sandbox 因 Service Control Manager 存取被拒而 exit 1，仍回報 x64 / win-x64；未修改服務或安全設定。成功 build/test 使用獲准執行權限，未觀察到 testhost crash 或 endpoint security alert。

Stage A 的 restore/build/audit 及 Stage B 現有 offline suite 通過。Stage C 僅現有跨 instance、CAS/ownership、exception fault injection、敏感內容排除等案例有執行證據；獨立 process helper、強制終止後鎖釋放與 read-only integrity_check 尚未實作／執行。Stage D controlled provider / Host crash driver 與 Stage E live recovery 尚未驗證。

因此 Phase 2B-1 **尚未完整驗收**。本次沒有強制終止程序、啟動 server、提交 live prompt，亦未開始 Phase 2B-2／2C。下一步為補齊 Stage C/D 測試工具，再另外配置 synthetic-only live provider 驗證 Stage E。原公司端報告保留其當時未驗證狀態。
