# Phase 2A 關機前工作盤點

> 2026-10-15 接續更新：三次真實唯讀委派 Completed，permission 與 cancellation 已驗證；Phase 2A PoC 驗收通過。完整最新結論、失敗紀錄與進入 2B 的限制見 [PHASE2A_RESULTS](../acceptance/PHASE2A_RESULTS.md)。以下保留 2026-10-14 關機時的歷史狀態。

日期：2026-10-14。此文件是接續紀錄，不是完成驗收報告。

## 已完成

- 完整閱讀 ROADMAP、PHASE2A_EXECUTION_PLAN、EXTERNAL_HARNESS_ARCHITECTURE 並核對現有程式。
- P0：MAF Agent 與 Workflow SDK 1.24.0；實際編譯官方 Executor、WorkflowBuilder、InProcessExecution API。OpenCode 原生 server 1.18.35 與實際 OpenAPI 已核對。
- P1：Core 外部執行契約、官方 Workflow 單一 OpenCodeExecutor、Fake Provider。已提交及推送 `7849b09 feat: add external workflow contracts and fake lifecycle tests`。
- P2/P3：新增 OpenCode HTTP client、唯讀 session 權限核對、單次提交與原 session 對帳、有限輪詢與輸出限制、取消與 abort、Host 的 --external-task 分支、Fake HTTP 與 opt-in live tests。
- 擴充後 Fake Provider／HTTP／官方 Workflow 範圍共 36 個測試。關機前重跑曾出現 35 passed / 1 failed：session shape 防護轉換遺失 Ambiguous，導致 Unknown 被判成 Failed；修正為保留該旗標後重跑，36/36 通過、0 skipped。證據為本機 phase2a-p2.trx。
- 曾執行完整 solution build，0 warnings、0 errors；後續修改仍需完整回歸。
- PCA／Review 核心行為未修改；沒有新增 FileWriter、Shell Tool、第二套 Agent Loop、Hybrid 或 A2A。

## 實際測試與未完成項目

- P4 第一個真實唯讀任務使用預設 opencode/big-pickle，120 秒逾時，abort 後初次結果為 Unknown，後續查詢確認 MessageAbortedError。任務未重送，fixture 內容、Git index 及 status 沒有變更。
- 尚未取得三次 Completed，不能判定 P4 通過，也尚未滿足 Phase 2B 進入條件。
- P5 的 permission 與 cancellation 真實測試已寫成 opt-in tests，但尚未執行。負面 Fake 測試通過不代表真實模型測試通過。
- 尚需完整 dotnet build/test、CLI 回歸、README 設定範例／舊連結修正、架構決策紀錄及 docs/acceptance/PHASE2A_RESULTS.md。
- Workflow mapping／single-flight 為程序內狀態；尚無持久化或重啟對帳。WaitingForApproval 不會自動放行。

## 授權與模型設定

- 使用者授權啟動本機原生測試 server，以及驗證後 commit 並直接 push origin。
- 最後使用者授權改用既有 AI_BASE_URL／AI_MODEL／AI_API_KEY；僅程序環境與拋棄式 profile，不寫入憑證或修改全域設定。
- 關機前僅檢查目前程序環境，三個變數均不存在；尚未重新設定或啟動配置該模型的 server。下次先核對 Process/User 環境是否已備妥，只輸出存在與否，絕不輸出 key。
- 可依官方 config/providers 文件使用程序內 OPENCODE_CONFIG_CONTENT 與 env reference；仍須核對已安裝 1.18.35 的行為，保持 maf_readonly 權限不變，不自動安裝套件。
- 禁止執行 .ps1 或繞過公司 PowerShell 政策；沒有管理員權限。

## 本機證據與恢復

- 忽略的 TestResults/phase2a/live-environment.json 保存 fixtureRoot、原測試 serverPid（3972）、baseUrl、agent。關機後 PID 不具識別力，必須重新核對程序，不能依舊 PID 終止任何程序。
- Fixture 是獨立 Temp/maf-phase2a-* Git repository；不要在 MAF 子目錄初始化另一個 Git repo。若 Temp 已清除，重新建立無憑證的測試 fixture。
- 初次失敗證據：TestResults/phase2a/live/initial-timeout.json 與 tests/MAF.Tests/TestResults/phase2a-live-initial-timeout.trx。其他 SDK/API probe 與 server logs 也位於忽略的 TestResults，不提交生成物。
- 下次先 git status/log，閱讀本文件及 API_DISCOVERY，核對已提交 checkpoint；不要重做已完成的 P0/P1。
- 模型與 server 就緒後，設 MAF_LIVE_OPENCODE=1、MAF_OPENCODE_TEST_ROOT、MAF_OPENCODE_RESULTS_DIR，先執行 ThreeReadOnlyTasksRunThroughOfficialWorkflow，再 permission/cancellation 測試，記錄實際結果與零寫入證據。
- 若 prompt POST 回應不明，對帳原 session；禁止盲目重送。先前逾時 session 已確認取消，後續新測試須使用新 TaskId/session。

