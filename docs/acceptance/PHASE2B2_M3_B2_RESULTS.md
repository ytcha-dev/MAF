# Phase 2B-2 M3-B2 — Explicit Approval Resume

日期：2026-10-16（Asia/Taipei）。基準 `d6009acd4a43183acac020caa79e6336547a71f2`；main / fetch 後 origin/main 相同，起始 tracked tree 乾淨，既有 `.vscode/` 保留、不讀取、不提交。本輪只實作 synthetic-trust Explicit Resume，沒有啟用 Host production trust、Broker、operational approval CLI、Abort 或 live provider。

## 結論與 schema gate

三個核心規則均保留：**Approved 本身不執行；RequireApproval 不改為 Allow；commit 後當機不重新 Submit。** Controlled B2 increment 已實作，44 focused／604 offline 全通過。這不是 production human approval 或完整 Phase 2B-2 的驗收。

依據已核對的 [M3-0](PHASE2B2_M3_0_REVIEW.md)、[A1](PHASE2B2_M3_A1_RESULTS.md)、[A2a](PHASE2B2_M3_A2A_RESULTS.md)、[A2b](PHASE2B2_M3_A2B_RESULTS.md)、[A2c](PHASE2B2_M3_A2C_RESULTS.md)、[B1](PHASE2B2_M3_B1_RESULTS.md)、[ADR-004](../architecture/ADR-004_DURABLE_EXECUTION_RECOVERY.md)、[ADR-005](../architecture/ADR-005_POLICY_APPROVAL_CANCELLATION.md)、[ADR-006](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md) 與 source，現有 v2 足以支持**受控 trusted-writer / synthetic trust** 的 linkage：

| Durable evidence | 使用方式 |
|---|---|
| approval_requests 的 ApprovalId、operation FK、status/version、decision_id 與 typed snapshot | 讀取時核對 SQL columns／snapshot／operation join 一致性；保存 DecisionId、Choice、DecidedBy、required operator scope、期限、原 operation 與 binding digest |
| authorization_operations 的 operation/task/execution/kind/generation、initial-admission uniqueness | 原批准不能換 TaskId、execution 或 generation；D12 replacement 不新增 |
| executions 的 TaskId PK 與完整原 identity snapshot | Consume 同交易插入 Created，逐欄核對原 run/execution/message/root/provider/fingerprint，唯一 TaskId；effect 時再核對整個 current snapshot |
| task_controls 的 execution identity、monotonic version、sticky cancellation | Consume 與每個 effect 在同 DB writer transaction 檢查；cancellation 不能因 approval 被略過 |
| policy_authority / bounded audit | active authority/epoch/version/digest 不符即拒絕；消耗 audit 與 row/Created 同 commit；approved dispatch-intent audit 額外使用既有 approval_id 欄位關聯原 approval |

沒有新 table/column/index、schema version、migration 或 `IsApproved` 放行旗標。**這些欄位不構成簽章人類 consent receipt**；同帳號惡意 DB writer、防備份 rollback、真正 human proof 等仍是 M3-C blockers，見最後一節。沒有以 v2 足夠做 synthetic linkage 冒稱 production authenticity。

## 實作與執行邊界

- [Core ApprovalResume.cs](../../src/MAF.Core/Policy/ApprovalResume.cs)：窄 `ApprovalResumeRequest`、`IApprovalResumeCoordinator`、sealed `ConsumedApprovalAuthorization`。證據只能由 persistence internal constructor 在成功 consumption 後建立，綁定該 DB、實際 owner 物件、完整 Consumed record、fresh operator context；無 public factory、無 JSON serialize/restore、無 persisted representation。
- [AdmissionContinuation](../../src/MAF.Core/Policy/AtomicAdmission.cs)：沿用原 owner/use guard 與 single-use CAS，新增 `ConsumeApprovedAsync` handoff。原 Direct Allow `ConsumeAsync` 保持；不能把 Approved／Consumed metadata 轉成 continuation。
- [SqliteApprovalStore.Resume](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Resume.cs)：`ConsumeOwnedAsync` 驗真實 ExplicitV2 TaskId owner，重用既有 [Consume transaction](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs)。舊公開 ConsumeAsync 仍只回 data，不 mint grant；私有交易共用原檢查，再於 transaction 前後兩端加入 B2 精確 input/operation 與 fresh operator/approval-expiry validation。
- [ProtectedExternalRuntime.ResumeAsync](../../src/MAF.Workflows.External/ProtectedExternalRuntime.cs)：重新提交原 input；缺 input 固定 `InputRequired`、0 HTTP。verified caller → canonical root/config → owner → fresh execution read → 載入原 approval identity metadata → A1 RequireApproval assessment → atomic Consume+Created → same-owner handoff → **既有 DurableExecutionService owned launch、官方 Workflow**。從 approval 還原的是 IDs，不是權限；SQL consumption 才重新驗證 exact row/binding/current authority。
- [SqliteApprovalStore.Runtime](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Runtime.cs)：保留一個固定 effect transaction，兩種 authorization basis 分開驗。原 Direct Allow 走 evaluator Revalidate(Allow)；Approved Resume 則要求 evaluator 真正回 RequireApproval，並驗 sealed consumed evidence。沒有 fake Allow，也沒有跳過 [OpenCodeClient wire guard](../../src/MAF.Workflows.External/OpenCode/OpenCodeClient.cs)。
- [Test fixture](../../tests/MAF.ApprovalAcceptance/SyntheticApprovalApplication.cs)、[helper Program](../../tests/MAF.ApprovalAcceptance/Program.cs)、[ApprovalResumeTests](../../tests/MAF.Tests/ApprovalResumeTests.cs)：只有 synthetic verified agent/operator、fake HTTP、test-owned SQLite/sidecars 與 captured helper processes。未改 provider adapter、Host、DurableExecutionService、fingerprint、state transitions 或既有 tests assertions。

Resume 必須提供原 Approved version/digest、original input 與目前可驗證的原 approver context。Consume 重新核對其 subject=DecidedBy、DecisionId 存在、Choice=Approve、非 self、HumanOperator/FreshHumanPresence、required operator scope、current rights；caller、scope、connector、config revision、epoch/version、期限、control、execution binding 全須符合。Approved→Consumed + unique Created + audit 全部 commit 或全部 rollback。只有 committing owner 在 commit 後可拿到一次 handoff；當時仍持 ownership use lease，Dispose 不能中途釋鎖。

**既有 execution 不走 Resume launch**：固定 `ExistingExecutionRecoveryOnly`，沒有新 continuation。呼叫端另行明確 `RunAsync(Inspect)`，依目前 Inspect policy 使用原 correlation；不拿原 Submit approval 代替 Inspect。這也避免拿錯 ApprovalId／stale resume data 掩飾成成功 Resume。

## Evidence 如何延續到每個 effect

每次 approved-runtime preflight、intent CAS 及 HTTP wire preflight 都會：

1. 核對證據 DB/owner **物件實體**與真實 ownership 存活；換 owner、disposed owner 或 null evidence 拒絕。
2. 在 authority lease / SQLite writer transaction 內重讀該 ApprovalId，要求完整 row 等於 consumption 時的 evidence，包括 Consumed status/version、DecisionId、DecidedBy、Choice、原 operation/binding/期限。不是只看狀態。
3. 以原 operation 重算 canonical digest、驗 current RequireApproval、active authority/epoch/version、operator scope、fresh human context 與仍有權；原 binding 不更寫。執行中取得的 session 另由 current execution / effect binding 核對，不能反寫原批准的 session-null operation。
4. 綁原 caller/task/run/execution/message/fingerprint/scope/config/connector；檢查 current execution snapshot、control version/cancellation。Create/Submit 仍須先原子提交原 state-machine 的 dispatch intent 與 audit，再到 wire 檢查。
5. Observe/Inspect 仍須獨立 deterministic **Allow for InspectExistingExecution**，此外當前 owned approved run 仍檢查 consumed evidence。Submit approval 不授 Inspect 或 Cancel；Abort 仍一律不支援。重啟後另行 Inspect 是獨立 read authorization，不還原 Submit evidence。

單獨 Approved row、舊 data-only ConsumeAsync 回傳值、單獨 Consumed row、fake Allow assessment、反序列化 metadata、重取鎖都不能取得 approved dispatch basis。即使 consumption 已 commit，後續撤權、human-presence 失效、expiry、policy activation、control 更新或 decision metadata 不符，也會阻止下一 effect。這不撤回已送出的 HTTP，也不把 remote outcome 猜成 Cancelled。

鎖順序仍為 TaskId ownership → authority acquisition/lease（SQL 外）→短 SQLite IMMEDIATE transaction；local callbacks 不進行 network I/O。Explicit-only activation 不變，普通 Resume/effects 不啟用新 policy。沒有跨 HTTP 的 SQLite transaction；最後 preflight 與送出之間仍有不可消除的窗口，既有 A2c evidence/限制繼續適用。

## 實際驗證

Windows / .NET 10、Debug。最後 focused **44 executed / 44 passed / 0 failed / 0 skipped**；full offline **604 executed / 604 passed / 0 failed / 0 skipped**；solution build **0 warnings / 0 errors**。原 B1 560 全部保留 + B2 44 = 604，focused 不重複相加。A2c 的 37 runtime、原 durable recovery、approval CAS／migration、fingerprint regression 全通過，沒有放寬原 assertions。

最終 scope/whitespace diff 已檢查；README、Roadmap 與本報告共 77 個 relative file links 解析成功。ApprovalSchema.cs 的唯一變更是附加 internal ConsumptionCommitted 測試 checkpoint，DDL／schema version 未改。

| Cases | 實際驗證 |
|---|---|
| 1 + 1 | Approved 未 Resume 時 execution/HTTP=0；Explicit Resume 用原官方 Workflow 完成，Consumed v2、原 digest/IDs 不變、Create/Prompt 各 1；新 composition 預配不同 IDs 仍還原原批准 metadata |
| 20 | missing/changed input、root/budget/TaskId、version/digest/ApprovalId、forged caller/operator、missing/self/expired operator、expired/revoked approval、new active policy、forged decision/self-approved metadata、config/connector mismatch，皆不 consume、不 dispatch；InputRequired 穩定 |
| 6 + 1 | session POST 後 operator/presence/control/policy/expiry/DecisionId 變更擋 prompt；prompt POST 後 operator 撤權也擋 Observe GET，證明 observation 未丟掉 approval evidence |
| 1 | approved Submit 不授 Inspect / Cancel，0 Abort POST |
| 2 | lost Create / Submit response，原 session/message/run correlation 保留，duplicate Resume 拒絕，明確 Inspect 無新增 POST |
| 1 + 1 + 3 | single-use／不可序列化 evidence；data-only Consumed row/null evidence/fake Allow 不放行；換 owner、假 Allow、Abort 均拒絕 |
| 2 + 1 | cancellation 先到則 consume 拒絕；consume writer 先到後 cancel 提升 control，擋 effect；transaction 中 approval 過期則完整 rollback Approved/Created/audit |
| 3 + 1 | 下列真 process crash 與兩個 Resume processes 競爭，只能一個 committing owner |

正常 focused case 的原 binding digest（fixture root 每次不同，digest 僅屬本次）：`2BD2576246FB4DD26DD4F24947D104D51981DF06E80E80CB4D8DCD331B7A2ACB`。Approval=`resume-approval`、Decision=`resume-decision`、Consumed version=2；Policy 維持 RequireApproval。Raw fake-transport method/path 帳目：POST `/session`=1、POST `/session/ses_runtime/prompt_async`=1、POST abort=0，duplicate POST delta=0。

最後 focused run 的 OS-process evidence（UTC，stdin/stdout gate，driver 等待最多 20 秒、child gate 最多 30 秒，只 kill captured process，確認 StartTime）：

| Point | Killed PID / startUTC | Restart PID | Restart / durable result |
|---|---|---|---|
| ApprovalConsumed，Consume commit 前 | 5148 / `2026-10-16T11:22:19.0583287Z` | 7332 | rollback 後仍 Approved v1、execution=0；fresh **Explicit Resume** 重新驗證，才允許 Create=1、Prompt=1 |
| ConsumptionCommitted，mint/handoff 前 | 13980 / `2026-10-16T11:22:19.8670121Z` | 56684 | Consumed v2 + Created/None；fresh **Inspect**，Unknown，新增 Create/Prompt=0 |
| DispatchIntentCommitted，Create POST 前 | 21304 / `2026-10-16T11:22:18.3795146Z` | 43520 | Consumed v2 + SessionCreationIntended；fresh Inspect，Unknown，新增 Create/Prompt=0 |

三例重啟後唯一 execution=1、integrity_check=ok、Abort POST=0；沒有重建 continuation。Original/recovered `runtime-task / runtime-run / runtime-execution / msg_runtime` 一致；commit 後這兩個 kill windows 尚無 session，所以沒有用另一 session 替代。HTTP sidecars 只記 method/path，不含 headers、prompt 或 transcript。

競爭程序 PID **53432 / 26848**，winner **53432**。兩者先由 driver 放行，winner 在已進 protected transport 的 health GET gate 持住 owner，另一個明確回 TaskAlreadyOwned；釋放後只一筆 Created、Consumed v2、Create POST=1、Prompt POST=1、Abort POST=0。這是兩個 OS processes 的競爭，不以 exception injection 代替。

```powershell
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore --filter FullyQualifiedName~ApprovalResumeTests --logger 'trx;LogFileName=phase2b2-m3-b2-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m3-b2-offline.trx' --results-directory TestResults/home-validation
```

Raw TRX 在 ignored TestResults/home-validation。沒有 live OpenCode/model、公司 endpoint、security-policy/ACL 修改或 credentials 輸出；未觀察到 endpoint-security alert，既有公司安全事件記錄未改。此 home/synthetic acceptance 不構成企業 compatibility clearance。

## 限制與後續 gate

- **M3-C 仍未通過**：沒有 genuine production human proof、Broker、protected identity channel、signed durable consent receipt、writer/secret isolation、external anti-rollback/revocation anchor。v2 metadata 與 current synthetic operator context 能驗證本輪算法，不能抵抗有權改 DB/binary/DI 的惡意 same-principal 程式。未來若需要 durable proof reference/schema，仍須另審，這輪不承諾零儲存變更。
- 同一 approver 的 fresh trusted context 必須在 Resume 及 owned runtime effects 仍能驗證；尚無 production proof refresh/receipt 協定。失效時 fail closed，可能留 Consumed/Created/Unknown，不自動恢復 Approved、不發新 session/prompt。
- **M4 仍未實作**：cancellation 只以既有 durable control 阻止下一 effect，沒有 provider Abort delivery、ack/retry 或新的 cancellation CLI。Inspect cancellation/timeout 仍不隱含 Abort。
- D12 generation replacement、ephemeral/batch/list authorization、native PCA/Reviewer policy integration、正式 activation/approval CLI 仍不支援。Host production trust 仍停用，public/raw missing-context guard 不變。
- No distributed ownership、power-loss 或 remote exactly-once 保證。已送出的 HTTP 不因後續 policy/control 更新撤回；保持原 durable correlation，只 reconciliation，不自動重送。

**B2 controlled synthetic Explicit Resume 完成。提交推送後停止，不自動進 M3-C、M4 或其他階段。**
