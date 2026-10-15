# Phase 2B-1：Symantec 安全事件

記錄日期：2026-10-15。狀態：**測試及執行持續暫停，尚未取得恢復授權或 IT/security 判定**。本文件不將事件定性為誤報。

## 原始事件必要欄位

以下依操作者提供的 Symantec log 保存必要欄位；未複製無關的內網 IP、磁碟硬體 ID、帳號、網域或電腦名稱。原始安全紀錄未修改、刪除或另行清理。

| 欄位 | 值 |
|---|---|
| 時間 | 2026-10-15 14:42:30，UTC+8 |
| 模式／動作 | 正式／終止 |
| API | 檔案寫入 |
| 規則 | 勒索軟體通知檔案 \| Ransom.Haknata |
| 程序 | `D:\_research_repo\MAF\tests\MAF.Tests\bin\Debug\net10.0\testhost.exe` |
| PID | 31268 |
| SHA256 | `fb63eb314f25584a532683de7abf4ca8a0b43103f3699f5d5d87317338eeda76` |
| 目標 | `%LOCALAPPDATA%\Temp\maf-http-sq4ienk4.nm4` |
| 顯示大小 | 0 位元組；不能據此推定是一般檔案或目錄 |
| 其他原始欄位 | `2017-07-11 - 呼叫程序 SHA256=...`；日期欄位意義未確認，不能作為事件時間 |

操作者後來修正副檔名為 `.nm4`；先前口述 `.mp4` 不構成另一事件的證據。

## 程式碼與 revision

事件時 tracked source revision：`036cba8`，`docs: reconcile roadmap history and phase status`。當時尚未撰寫 durable store，僅有未追蹤的 Phase 2B-1 計畫。事件時二進位與 source 的逐一可重現 build 對應尚未經 IT 確認；程序檔 SHA256 的唯讀檢查曾與提供值一致。

- [OpenCodeClientTests.cs](../../tests/MAF.Tests/OpenCodeClientTests.cs)：line 13，欄位初始化呼叫 `Directory.CreateTempSubdirectory("maf-http-").FullName`，每個 xUnit instance 建立 workspace 目錄。
- 同檔 line 203：`Dispose() => Directory.Delete(root)`，非遞迴刪除，預期只移除自己建立的空目錄。程序被終止時，不能保證 Dispose 執行。
- 同檔 line 206 起 `OpenCodeApiFixture : HttpMessageHandler`；line 230 的 `StringContent` 是記憶體 JSON HTTP 回應，line 240 起自訂 `SendAsync`。測試使用 `new HttpClient(handler)`，沒有 socket／真實 server 連線。
- `OpenCodeClient` 使用受限的 MemoryStream 收集 HTTP body；沒有將回應下載為 `.nm4`，沒有啟動外部檔案。

靜態搜尋未在測試／正式 `.cs`、`.csproj` 找到 `.nm4` 或 `.mp4` literal。所選三組測試未包含 `Process.Start`；其他 Git／junction／live 測試不在這次 filter 內。

## 已確認與尚待證實

已確認：Symantec 正式規則終止 testhost；同時段 TRX 記錄 testhost crash；prefix 與程式碼一致。Windows .NET 10 的官方實作產生八個隨機字元、點、三個隨機字元，接著呼叫 `CreateDirectory`，因此目錄名稱也可能看似副檔名。參考 [.NET 10 Directory.Windows.cs](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Directory.Windows.cs)；這不是對當時 .NET 10.0.11 binary 的反組譯驗證。

合理假說：`CreateTempSubdirectory` 產生 `sq4ienk4.nm4`，建立目錄的操作觸發規則。**這只解釋名稱與操作來源，不能證明 Symantec 誤報，也無法證明目標實際物件型態或完整觸發條件。** 事件後對精確路徑的唯讀查詢回報不存在；不能區分建立遭阻止、先前清理或其他原因。

測試初始化器為多個案例共用且 xUnit 可平行執行，無法僅憑路徑指定唯一觸發案例。最可能來源是 `OpenCodeClientTests` 的 instance 初始化，而非某個 HTTP response fixture 寫入媒體檔。

## 中止及失效的驗證

事件前曾執行，**請勿將此指令視為現在的執行授權**：

```text
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~ExternalWorkflowTests|FullyQualifiedName~OpenCodeClientTests|FullyQualifiedName~ExternalConsoleOutputTests" --logger "trx;LogFileName=phase2b1-baseline.trx"
```

保留的 ignored artifact：`tests/MAF.Tests/TestResults/phase2b1-baseline.trx`，RunId `19402bd0-1b4b-4f25-a385-256303463d64`。ResultSummary **Failed**；31 個 recorded results 是 Passed、0 個 assertion Failed，但 RunInfo 在 `2026-10-15T14:42:30.9373229+08:00` 記錄「使用中的測試回合已中止。原因: 測試主機處理序當機」。**整輪不能算通過**。

相對事件前既有測試清單的靜態比對：

| 組別 | 原案例數 | TRX 已記錄 Passed | 沒有完成結果 |
|---|---:|---:|---:|
| ExternalConsoleOutputTests | 29 | 26 | 3 |
| OpenCodeClientTests | 24 | 4 | 20 |
| ExternalWorkflowTests | 13 | 1 | 12 |
| 合計 | 66 | 31 | 35 |

沒有結果不代表一定沒開始執行。HTTP 四筆完成結果為 `IdleAloneAndUnrelatedOrToolMessagesAreNotCompleted` 的 foreign-parent／incomplete／tool-calls 及 `AmbiguousPostAndTransientGetReconcileOriginalSession`；Workflow 一筆為 `OfflineProviderDoesNotSubmit`。這些是中止 TRX 的個別紀錄，不是 Phase 2B-1 驗收。

`760f6c5` 的 112 個 offline tests 與 SQLite smoke 的基本跨程序保存是**事件前歷史證據**，不能代替本次新實作測試。事件後未重新執行 build、restore、tests、testhost、smoke 或 live tests。

## 正當設計改善與 IT 待確認事項

1. HTTP 契約／JSON fixture 可研究抽出純 parser 測試，減少每個測試初始化 filesystem 的需求；需要 ProjectTools 真實 root 驗證的測試仍保留隔離 workspace。這是降低非必要副作用的設計議題，不是改副檔名、prefix 或位置規避偵測。本次未改原 `maf-http-` fixture。
2. IT/security 確認實際 target 是否 directory、被攔截的建立／寫入 API、規則條件及事件關聯程序。以現有紀錄分析，不用反覆重現觸發。
3. 確認 testhost 的來源、簽章／hash、被終止的完整程序樹與影響範圍。需保存哪些額外診斷由 IT 指定，避免收集不必要資料。
4. 由授權人員確認殘留 fixture／鎖／DB 的保存及清理方式；目前不自動清理事件目標，也不修改安全事件紀錄。
5. 恢復前需明確操作者授權，且依公司流程取得必要 IT/security clearance，確認允許的 build、NuGet restore、SQLite native loading、暫存資源、testhost 及 Live E2E 範圍。不得沿用事件前 live 授權自動重試。

沒有修改 Symantec、WDAC、Smart App Control、防火牆、排除規則或允許清單；沒有改名、搬移 fixture 或換程序重試。後續原始碼工作及待驗證清單見 [Phase 2B-1 results](PHASE2B1_RESULTS.md)。
