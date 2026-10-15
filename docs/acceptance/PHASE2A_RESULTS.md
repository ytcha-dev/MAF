# Phase 2A 驗收紀錄

日期：2026-10-15。狀態：**Phase 2A 的唯讀本機 PoC 驗收通過，可進入 Phase 2B hardening**。下列已知限制不表示 production-ready，也不授權寫入或自動 approval。

## 範圍與環境

路徑為 CLI → 官方 MAF Workflow → OpenCodeExecutor → OpenCode Server → typed result。沒有 PCA／Reviewer pipeline、Hybrid、A2A、FileWriter、Shell Tool 或第二套模型迴圈。Core 保留 provider-neutral 契約；既有 Agent 核心未修改。

- .NET 10；Microsoft.Agents.AI 與 Microsoft.Agents.AI.Workflows 固定 1.24.0。
- 原生 OpenCode 1.18.35；health 與實際 OpenAPI（162 paths）核對完成。
- 使用者明確授權的 127.0.0.1:4096 與獨立 Temp/maf-phase2a-* Git fixture，內容為自行建立的 README.md、Program.cs、唯讀 opencode.json。
- 原生 server 使用已授權 AI_BASE_URL／AI_MODEL／AI_API_KEY 的程序環境引用。沒有將憑證寫入專案或全域設定；未將 MAF／公司原始碼交給此模型。
- 未執行 .ps1、未變更系統政策、未安裝額外工具。
- 驗收結束後核對 metadata PID 與原生 executable 路徑，停止本次自行啟動的測試 server；未停止其他服務。停止後再次核對來源與 index hashes，與存證快照一致。下次使用須重新啟動獲授權的 server。

## 已有證據

| 檢查 | 結果 | 說明 |
|---|---|---|
| P0 SDK/API | Passed | 實際編譯 Executor、WorkflowBuilder、InProcessExecution，核對 OpenCode endpoints |
| P1 Fake Provider | Passed | 原 11 個測試，包含官方 WorkflowOutputEvent 與 typed output |
| P2/P3 Fake HTTP／Workflow | Passed | checkpoint 36/36；含 HTTP auth/status、壞 JSON／shape、timeout、pagination、secret redaction、permission/question、一次提交與取消 |
| 初次真實模型 | Failed | 2026-10-14 預設 big-pickle 未於 120 秒完成，abort 對帳再耗 5 秒，初次 Unknown；後續 native message 確認 MessageAbortedError |
| 初次零寫入 | Passed | fixture 來源 hashes、Git index bytes 與 status 未變更 |
| 授權模型初次 E2E | Failed | 2026-10-15 已使用 maf_test provider；兩次 read 完成，120 秒未產出最終回答，Unknown／稍後確認 MessageAbortedError；398 requests、125027 ms、零寫入 |
| 三次可用模型 E2E | Passed | 180 秒、回答最多 120 words；3 次 Completed、不同 session、零來源/index/status 變更；TRX 1 passed 含 3 次委派 |
| 完整 build | Passed | solution build 0 warnings／0 errors；調整 live 預算後隔離輸出 build 亦為 0／0 |
| 完整 test 回歸 | Passed | 82 passed、3 live tests skipped、0 failed；8m22s，phase2a-regression.trx |
| CLI help | Passed | --ask、--review、--external-task 與 /reset 仍有對應說明；另須核對執行行為 |
| CLI offline／無 Host AI 配置 | Passed | 移除三個 AI 變數，指定本機未監聽 port；官方 Workflow 回傳 Failed / TransportDisconnected，exit 1，沒有 session |
| Review CLI Staged | Passed | No Changes，Completed，exit 0；無模型呼叫 |
| 真實取消 | Passed | abort acknowledgment 後確認 MessageAbortedError，Cancelled，4869 ms／17 requests；來源/index/status 未變更 |
| 真實 permission | Passed with limitation | 重驗 1/1 passed：WaitingForApproval、不放行、零變更；cleanup ack=true，仍觀測到殘留 WaitingForApproval，未誤報取消 |

## 三次成功委派

| Trial | WorkflowRunId | OpenCode SessionId | Status | Duration ms | HTTP requests | Unexpected changes |
|---|---|---|---|---:|---:|---|
| 1 | a60fe5328e0a4de4a3b9a1a1c7369b7e | ses_ec2915a9fffecB5ZXd16KY68t3 | Completed | 83476 | 366 | None |
| 2 | cd6589b088544ef3a0e7832af28b7fc5 | ses_ec2900531ffe4fjZ86E77nYj5g | Completed | 68112 | 316 | None |
| 3 | de89b459849a47b68a522ae87c3e2d94 | ses_ec28eeadfffe46eQywxeNRp34x | Completed | 57370 | 276 | None |

每次 result 都保留獨立 ExecutionId／ExternalRequestId，client 以 assistant.parentID 對應提交的 messageID，要求 terminal time／finish 與非空文字。測試確認 WorkflowOutputEvent、不同 session，並比對來源 hashes、index bytes 與完整 porcelain status。成功證據為 phase2a-live-readonly-180.trx 與 read-only-1/2/3.json。

結果品質人工核對：3/3 正確引用 README.md、Program.cs、console entry point 與輸出字串；但 3/3 都以「兩個檔案」描述整個 repository，漏掉 opencode.json，也未實際列舉完整目錄。這是模型回答完整度限制，**不宣稱結構分析完全正確**。本階段 gate 驗證的是可靠委派與狀態／結果回收，並非 coding／分析品質認證。

## 失敗與修正紀錄

初次失敗 task 的 workflowRunId 為 `32e2...`，session 為 `ses_ec6272f2...`，488 HTTP requests、125043 ms（含 abort），沒有盲目重送。完整本機資料保留於忽略的 TestResults/phase2a/live/initial-timeout.json 及 tests/MAF.Tests/TestResults/phase2a-live-initial-timeout.trx。

授權模型首次的 workflowRunId 為 `f95bc4ad4cf44777a365c39fe79d2a4b`、session 為 `ses_ec296d915ffeDzFKLjFMjX3Fv3`。原 session metadata 確認模型為 Qwen3.5-122B-A10B，兩個 read 工具約第 99 秒完成；沒有 correlation mismatch。證據保留為 approved-model-timeout.json 與 phase2a-live-approved-model-timeout.trx。

2026-10-15 完整 solution build 成功，0 warnings／0 errors。其後調整 live test 預算時，建置碰到尚在執行的自有回歸 testhost 鎖住 MAF.Tests.dll（MSB3027／MSB3021），改用 TestResults/phase2a/live-bin 隔離輸出；不停止使用者程序，不把鎖檔誤當程式編譯錯誤。

Review CLI 的 All smoke 看到 fixture 既有的 unstaged opencode.json，因語意審查未完成回傳 Partial，未作為成功案例。之後以無 staged 變更的 Staged 模式確認既有 No Changes 行為。PCA／Review 的原有 46 個確定性案例全包含於 82 個 passing cases 中；未額外宣稱此次完成 PCA 真實模型品質驗收。

新增六個不同任務同時呼叫的 Fake 測試，驗證最多兩個遠端提交進行中，以及六個不同 workflow／session；範圍 37/37 passed，phase2a-final-scoped.trx。完整回歸是在新增此案例前執行，因此合計為 83 個不同確定性測試通過，不宣稱單次執行了 83 個完整 suite。

真實取消 workflowRunId `df27dfdbfdd74fb784e56b967d0bd19c`，session `ses_ec28d0346ffeb8jIsm6bRRGKkI`，來源/index/status unchanged。初次 permission workflowRunId `a410298536d54e149dd6ce179d7ff7fb`，session `ses_ec28cd4d4ffeJdyyBlX3tZw28f`，10523 ms／53 requests 即回傳 WaitingForApproval；但測試 finally 的 5 秒 abort 清理逾時，該次 TRX 為 1 passed／1 failed，不能改稱整批成功。

後續原 session metadata 確認 MessageAbortedError 且 status absent，**GET /permission 仍保留一筆 pending**。目前 adapter 優先回報 pending，不能由 absent status 或 abort ack 推論已取消。這是 OpenCode 1.18.35 實測的殘留 permission／終態對帳限制。新測試分開記錄 permission 行為與清理觀測，不自動 reply/reject，不把 Unknown／WaitingForApproval 說成 Cancelled。

重驗 permission workflowRunId `b885f167b24a49519e57dd42b797bdb1`、session `ses_ec28a7474ffefz4X0fa3292TIY`，7950 ms／43 requests，WaitingForApproval，source/index/status unchanged。獨立 15 秒清理預算內收到 abort acknowledgment，但觀測仍為 WaitingForApproval，cleanupError=null；記錄於 permission-cleanup.json。此測試的 Passed 只涵蓋「不自動放行並正確回報等待」，不宣稱 permission 清理已獲終態確認。

已保存的來源 SHA-256（permission/cancellation 快照，前三次成功亦以相同 fixture 任務前後逐位元核對）：

| File | SHA-256 |
|---|---|
| README.md | 4FBDA67CBB847D3CD6591234668E9AC65D9AF11A1498F7190657B8B166666DCA |
| Program.cs | 08F461CDE1081A833D262816B0F2B0A74D09C58BE5AA0CA0825C50CB35EBCC36 |
| opencode.json | 83332E22D6C635DDE32DC6F68800AC4B999D2E691F1EA52C569A7B6BE1FD4569 |

Git index SHA-256：`892F7782A6625851DEAFD202D3CE35889961188CA9BF30AA1ABA254453DAD7D2`。Fixture 任務前後皆有既存 ` M opencode.json`，不把既有修改算成 harness 寫入，也不宣稱 fixture 起始乾淨。

## P5 覆蓋對照

| 情境 | 實際驗證 | 限制 |
|---|---|---|
| Offline | 真實 CLI 連未監聽 loopback port，typed Failed；Fake provider | 不停止既有使用者服務 |
| Slow／timeout | 兩次真實 120 秒 timeout，Running 與 AbortRequested／Unknown | 真實模型延遲不等於固定 SLA |
| Cancellation | 真實 provider 確認 Cancelled；Fake 未確認則 Unknown | permission cleanup 是另外的限制 |
| Submit response loss | Fake HTTP／provider 對帳原 session、不再 POST | 沒有在真實服務上注入破壞性斷線 |
| Permission/input | 真實 read ask；Fake permission/question | 不測自動 approval，因為功能未提供 |
| Malformed／partial | Fake JSON／shape／截斷／tool-calls／foreign parent／pagination | 不污染真實 provider response |
| Zero writes | 三次真實成功及負面案例 source/index/status 核對 | 比對任務前後，不宣稱 fixture 起始無既有修改 |
| Concurrency | 六任務 Fake、上限 2；重複 TaskId 僅一次 submit | 不宣稱跨程序去重 |
| CLI regression | 82 既有與新增測試、help、Review No Changes、external offline | Review All 語意 smoke Partial，未重做 PCA 模型品質驗收 |

關機前曾發現 session shape 防護遺失 Ambiguous 旗標，造成 35 passed / 1 failed；修正保留旗標後 36/36 通過，已提交 `0bb91c5`。錯誤沒有藉由放寬權限或變更測試預期消除。

## 指令與證據位置

```text
dotnet build MAF.slnx --no-restore -m:1
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 --logger trx;LogFileName=phase2a-regression.trx
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore -m:1 --filter FullyQualifiedName~ThreeReadOnlyTasksRunThroughOfficialWorkflow
dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 -o TestResults/phase2a/live-bin
dotnet test TestResults/phase2a/live-bin/MAF.Tests.dll --filter FullyQualifiedName~ThreeReadOnlyTasksRunThroughOfficialWorkflow --logger trx;LogFileName=phase2a-live-readonly-180.trx --ResultsDirectory tests/MAF.Tests/TestResults
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 --filter FullyQualifiedName~ReadPermissionRequestIsSurfacedAndNeverApproved --logger trx;LogFileName=phase2a-live-permission.trx
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 --filter FullyQualifiedName~ExternalWorkflowTests|FullyQualifiedName~OpenCodeClientTests --logger trx;LogFileName=phase2a-final-scoped.trx
```

真實測試另外明確設定 MAF_LIVE_OPENCODE=1、獲授權 fixture 與本機結果路徑。TRX 在 tests/MAF.Tests/TestResults；JSON／OpenAPI／server process metadata 在 TestResults/phase2a。不提交生成物、原始 HTTP payload 或 credentials。

## 已知限制與 Phase 2B 判定

- Single-flight／mapping 為程序內狀態，沒有跨程序 exactly-once、durable mapping 或 restart recovery；Unknown 後須先人工對帳原 session。
- WaitingForApproval 只回報、不放行；回傳後 session 可能仍等待，操作者須處理或 abort。沒有人工 approval UI。
- 只實作 loopback、單一核准 root、唯讀 policy。這是 harness permission 邊界，不是 OS sandbox；redaction 為盡力而為。
- 1 秒輪詢、最多 200 次；每個 observation 需多次 HTTP。不是 SSE 或 server-side bounded-wait endpoint。排隊時間不計入 task timeout，abort 有獨立 5 秒預算。
- Messages 最多 3 頁各 100 筆，body／summary 有界限；不讀取不可信 artifacts/diff。沒有 token／成本數據，不推估模型呼叫數。
- 2A 僅驗證 selected local PoC，不表示 production reliability。沒有測試真實服務的破壞性斷線或 policy 修改。

P0～P3 的官方 API／契約／Workflow、P4 三次成功與零變更、P5 的上述真實與 Fake 情境及建置回歸均已有證據。因此**符合進入 Phase 2B hardening 的條件**；並非宣告所有真實故障均已注入驗證。

Phase 2B 第一批應優先處理 durable mapping／重啟後不重送、pending permission 與明確終態衝突的對帳、人工處置與清理責任、改善 polling HTTP 成本。仍須驗證寫入授權與審查 handoff 後，才加入 PCA → OpenCode → Reviewer。本次未實作 Phase 2B 或 Hybrid。

相關文件：[API discovery](../plans/PHASE2A_API_DISCOVERY.md)、[ADR-003](../architecture/ADR-003_OPENCODE_WORKFLOW_EXECUTOR.md)、[Execution Plan](../plans/PHASE2A_EXECUTION_PLAN.md)。
