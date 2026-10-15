# ADR-003：以官方 Workflow Executor 委派 OpenCode

日期：2026-10-15。狀態：採用於 Phase 2A；驗收結果另見 Phase 2A 報告。

## 背景與決策

第一個目標是證明 MAF 能把完整唯讀任務交給外部 harness，取得可追溯的狀態與結果。既有 PCA、Review 是原生 MAF Agent；OpenCode 已擁有自己的模型、工具、權限和執行迴圈。

採用 `Microsoft.Agents.AI.Workflows` 1.24.0 的 `Executor<ExternalTaskRequest, ExternalTaskResult>`、`WorkflowBuilder` 與 `InProcessExecution.RunAsync`。Host 使用相同 Console／DI，透過 `--external-task` 選擇獨立單節點 Workflow。Core 僅保留契約；provider JSON 與 HTTP 留在 OpenCodeClient。

OpenCode 1.18.35 的實際 OpenAPI 支援建立 session、非同步 prompt、status、messages、permission/question 與 abort。使用 direct API，分開啟動、觀察、收集及取消。以 WorkflowRunId、ExecutionId、ExternalSessionId、ExternalRequestId 區別各層識別；不使用 AgentSession 代表外部 session。

## 替代方案

- A2A Gateway：目前沒有必須互通的 A2A partner；額外 gateway 會增加協定、權限與狀態映射，無助於第一個驗收目標。
- LLM tool 包裝 OpenCode：會增加另一個模型決策層，使單次提交與取消責任不清楚。
- 自製 coding loop／檔案寫入工具：重複 harness 能力，超出範圍。
- 手動串方法：可作為 executor 內部生命週期，但不能替代官方 Workflow runtime；測試要求實際 WorkflowOutputEvent。

## 結果與限制

只有允許的 loopback server、指定 root 與唯讀 policy。Host 的 HttpClient 禁用自動 redirect；任何自行注入的 transport 也必須禁用 redirect，避免離開允許 origin。檢查 agent 與 session policy，不自動放行 permission/question。不確定的 POST 不重送；取消未獲終態證據則為 Unknown。

單例 facade 在程序內依 TaskId single-flight，最多保留 100 筆、同時執行 2 筆；不是跨程序 exactly-once。完成／等待／Unknown 結果會保留，沒有重啟復原或持久化對帳。任務 timeout 不包含進入 executor 前的排隊時間。單次 HTTP 預設 15 秒，task 最長 10 分鐘，觀察最多 200 次，abort 對帳另有 5 秒預算。權限是 harness 邊界，不是 OS sandbox；結果遮罩是盡力而為。

Phase 2B 再處理 durable mapping、重啟與人工 approval。只有出現實際 A2A backend 或互通需求時重新評估 gateway。Phase 2A 不串 PCA／Reviewer，不做 Hybrid。

## 證據

- [API 核對與最小調整](../plans/PHASE2A_API_DISCOVERY.md)
- [官方 Workflow executors](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/executors)
- [OpenCode server](https://opencode.ai/docs/server/)
- [OpenCode config](https://opencode.ai/docs/config/)
