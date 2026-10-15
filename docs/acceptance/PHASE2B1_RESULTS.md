# Phase 2B-1：實作進度與待驗證結果

日期：2026-10-15。Base revision：`036cba8`。

**結論：實作原始碼與 offline 測試案例已提交供審查；未編譯、未驗證，不滿足 Phase 2B-1 acceptance gate。** Symantec 正式事件後停止所有執行；沒有將靜態核對當成測試通過。不得自動啟動 Phase 2B-2／2C 或重新執行測試。

## 已撰寫的實作

- Core：`IExecutionStore`、ownership、snapshot/fingerprint、提交階段與中央 transition validation；外部 handle 可預先配置及重新連接。
- SQLite：獨立 project、schema v1、TaskId PK、version/snapshot CAS、local OS exclusive ownership、短交易；沒有 network transaction。套件使用已記錄 smoke 組合，未 restore／未 audit 新 dependency graph。
- Workflow：DurableExecutionService 與 client decorator，沿用官方 WorkflowBuilder、InProcessExecution、OpenCodeExecutor。保存 session／prompt intent 再做對應 POST。既有 TaskId 只 recovery；不重新提交。
- Provider：預先穩定 messageID、scope digest，重新驗證相同 readonly root／agent／session policy，再 GET 原 session；reattached handle 的 Submit 被封鎖。
- Host：明確 DB path 配置、單筆／batch recovery、既有 json/text/verbose 及 exit codes；不配置 DB 時保留 ephemeral 2A 路徑。Batch 是新 command 的單一 JSON array。
- 不保存 prompt、摘要、transcript、credentials；只保存 recovery metadata 與固定診斷碼。完整結果 recovery 依賴 provider 留存。
- PCA／Reviewer、既有唯讀權限、模型、server、部署、安全政策與原 `maf-http-` fixture 未修改。

架構決策、state machine、schema／migration、timeout 與限制見 [ADR-004](../architecture/ADR-004_DURABLE_EXECUTION_RECOVERY.md)，狀態為 implemented source / pending validation，尚未 accepted。

## Coverage matrix（靜態盤點，沒有新通過結果）

「Implemented, not executed」指原始碼存在，尚未證實可以編譯或通過。「Blocked by endpoint protection」指必要執行受到事件後 hold 限制，不能從程式碼推定結果。

| 項目 | 分類 | 原始碼／案例與證據 | 待辦 |
|---|---|---|---|
| 1. Durable TaskId persistence | Implemented, not executed | DurableExecutionTests：CompletionAndIdentitySurviveNewServiceAndStoreInstances；同請求共享 identity、不同 fingerprint 拒絕 | 實際 SQLite 保存／重開、Host process acceptance |
| 2. Restart reconciliation | Implemented, not executed | 明確 Dispose original service，再用同 DB 新 store/service/client；重用 run/execution/session/message IDs | 執行測試；真正 process crash runner 尚缺 |
| 3. Uncertain POST | Implemented, not executed | InjectedCrashAtPersistenceBoundary、AcceptedPostWithLostResponse、CrashAfterRemoteAcceptedBeforeLocalAcknowledgment、LostCreateResponse | 執行 deterministic fault injection，不能當成真實 crash |
| 4. Duplicate prevention | Implemented, not executed | 同 TaskId／request、不同 request、intent-before-POST、恢復時不 Create/Submit；OpenCodeRecoveryTests 驗證新 client reattach 防提交 | 確認 POST 次數、HTTP 契約及 regression |
| 5. State transitions | Implemented, not executed | DurableExecutionStateTests + InvalidTransitionAndStaleVersionAreRejected；phase、correlation、terminal、version 檢查 | 執行 transition／CAS 測試 |
| 6. Concurrent TaskId | Implemented, not executed | ConcurrentOwnersAcrossStoreInstancesCannotSubmitSameTask；FileShare.None + PK/CAS | 執行同機互斥；跨 process crash 釋放尚未驗證 |
| 7. Provider unavailable | Implemented, not executed | Unknown 而非 remote failure；ProviderUnavailabilityIsUnknownAndSubsequentRecoveryDoesNotSubmit；FailedDiscoveryNeverCreatesOrSubmits 為送出前 local launch failure | 執行 fake；live outage／恢復待授權及 runner |
| 8. Waiting/cancellation | Implemented, not executed | NonSuccessStatesRetainTheirSemantics、WaitingCanRecoverToCompletion、ExplicitCancellationRequiresConfirmedRemoteCancellation | 執行 fake／CLI exit regression；不自動 approval |
| 9. SQLite persistence | Implemented, not executed；Previously verified before this event 僅限基礎 smoke | 新 store、future-schema rejection、敏感內容排除案例；既有 SQLITE_SMOKE_RESULTS 的跨程序一筆保存／刪除成功屬不同程式 | 新 project restore/build/native load、migration、durability、secret scan 尚待執行 |
| 10. Live OpenCode recovery | Missing implementation（專用 2B-1 acceptance runner）；Blocked by endpoint protection（執行） | 舊 Phase 2A live 三次成功不含 Host restart；本次無 live results | 安全 clearance 後補 dedicated Host/session runner，驗證 active restart、原 session final answer、provider outage、無重送、source/index/status 不變 |

新增測試來源：[DurableExecutionTests](../../tests/MAF.Tests/DurableExecutionTests.cs)、[state tests](../../tests/MAF.Tests/DurableExecutionStateTests.cs)、[OpenCode recovery HTTP fixture tests](../../tests/MAF.Tests/OpenCodeRecoveryTests.cs)、[CLI batch output/path tests](../../tests/MAF.Tests/DurableConsoleOutputTests.cs)。全部尚未執行，包括純記憶體／record 測試。

依 `[Fact]`／`[InlineData]` 的原始碼靜態計數為 39 個 authored cases（29 + 3 + 2 + 5）；這不是 test discovery 的結果，也不代表通過。Fault boundaries 包含初始保存失敗、session creation 前、session identity 保存後、prompt intent 後、acknowledgment 後與 terminal commit 後。另有兩個服務互相競爭同 TaskId、第一個停在 fake observation gate 的案例；不是跨 OS process 測試。

## 實際執行紀錄

### 事件前中止的 baseline

```text
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~ExternalWorkflowTests|FullyQualifiedName~OpenCodeClientTests|FullyQualifiedName~ExternalConsoleOutputTests" --logger "trx;LogFileName=phase2b1-baseline.trx"
```

結果：**Failed／testhost crash**，31 個 recorded Passed、35 個既有案例沒有完成結果。不是 baseline pass。精確時間、hash、對應程式碼及 IT 問題見 [安全事件](PHASE2B1_SECURITY_EVENT.md)。原始 ignored TRX 保留，不提交或清理。

### 事件後本次工作

只做 `git status`、diff／`git diff --check`、source／TRX 唯讀搜尋、project XML／reference／文件連結靜態核對與文字修改。沒有執行 `dotnet build`、`dotnet restore`、`dotnet test`、testhost、新二進位、smoke 或 OpenCode HTTP。沒有停止 shared server、讀取憑證值、修改模型／部署或安全政策。

下列是**安全許可後待執行的 commands，並非本次成功結果**：

```text
dotnet restore MAF.slnx
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~DurableExecutionTests|FullyQualifiedName~DurableExecutionStateTests|FullyQualifiedName~OpenCodeRecoveryTests|FullyQualifiedName~DurableConsoleOutputTests"
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName!~OpenCodeLiveTests"
```

這些測試涉及已封鎖的 testhost 與 Temp fixture；不能自行執行。NuGet restore 須使用既有公司允許來源並重查 vulnerability graph；不得以預設套件「曾 smoke 成功」推定此 project 沒有漏洞。

## Crash/restart 證據界線

新測試**設計**包含提交 intent 保存後中斷、remote accepted/local acknowledgment 前 exception、丟失 response、保存後新 service 重開同 DB，以及 running → provider unavailable → later completion。它們目前只有 source，沒有執行結果；即使之後通過，in-process exception 仍不等於 OS process kill。

Separate-process abrupt Host termination、SQLite journal recovery／lock release及 live server 上 reconnect 原 session 尚未實測。沒有重新提交 prompt、source unchanged、外部 identity 一致等 live 結論目前**沒有證據**。不得以本 commit 宣稱任務生命週期已可靠通過驗收。

Phase 2A 曾觀察公司 `Qwen3.5-122B-A10B` 讀取後沒有 final answer 的 timeout。該模型僅為歷史觀察；本次沒有設定或呼叫模型，沒有 live provider configuration 結果。Durable timeout 不 abort 的新路徑旨在讓後續 recovery 區分仍在執行、已完成待取回與真正 failure，仍待驗證。

## 限制、風險與 gate

1. 所有新 C#／SQLite／SDK API 尚未 build，可能存在編譯或 runtime 問題；需完成 focused tests、全離線 regression 及 live acceptance 才能接受。
2. 無 exactly-once remote guarantee；session creation response 遺失且 ID 未保存時無法自動 recovery。沒有以重送代替證據。
3. DB 只存 metadata。遠端 message 被刪除、history 收集上限或沒有 final response，結果可能持續 Unknown；不捏造 completed output。
4. 單機／同 DB 路徑所有權；不同 DB 副本不互斥，沒有 distributed workers、租約服務或惡意程序隔離。既有 user 權限不等於加密或完整 DLP。
5. 未提供 automatic retry／force resubmit／permission approval；缺證據由 operator 調查。Confirmed terminal evidence 不覆寫；provider later unavailable 的當次 recovery 仍 Unknown。
6. 路徑檢查不抵禦惡意並行 filesystem 替換；production location、ACL 及備份保留由授權 operator／IT 管理，本次未調整。
7. **安全 gate 未解除**：IT/security 尚未確認規則條件、target 物件型態及恢復測試條件；不定性為誤報，不改名／搬路徑繞過。

Phase 2B-0 僅確認既有基本 SQLite smoke 與 2A 歷史 baseline；新 baseline 被中止，安全事件待處理。Phase 2B-1 acceptance **未滿足**，Phase 2B-2／2C **不啟動**。提交後停止，等待 owner 下一步指示。
