# Personal Alpha Validation & V1 Pilot Preparation — 2026-10-27

**Decision：Offline CLI journey 的 scoped gate 通過；整體 regression 仍 Conditional，Live pilot 為 Blocked。** 這不是無條件 Alpha acceptance，也不是 ProductionProtected 或 OpenCode 執行授權。

## 1. Identity、環境與授權

本輪開始時 source HEAD `a78006349ee3a9dbb55e4a9bb15afd2f0800b143`，分支 `feat/personal-alpha-offline-journey`；包含 R1 `b325d20d3aa2560d50b62ce79a14cc23e85d04e3`。本機與 connected GitHub branches GET 均核對相同 SHA；原 PR #4 feature branch 停在 b325d20，main 為 6844033。此次後續只變更文件；沒有測試後 source fix，也沒有 merge。

初始 working tree 僅原有未追蹤 `.opencode/`，未讀取或改動其設定。測試與 CLI 的 source 都是上述 baseline，不能將文件 commit 當作另一輪測試。

本輪起初依既有公司 hold 只做靜態讀取。Owner 隨後明確回答目前裝置「已有 clearance」；依當時問題所列範圍，採用 **solution build、離線 tests、interactive offline CLI walkthrough** 授權。沒有 OS process tests、真實 OpenCode、live model/data-flow 或廣泛解除公司政策的授權。沒有宣稱代理自行核驗 IT ticket。此記錄不追溯改寫舊 NotRun/HOLD，也不構成未來永久授權。

| 環境欄位 | 實際值 |
| --- | --- |
| Workspace | `D:\_research_repo\MAF`；本次 CLI 只檢查 directory metadata，不傳出其來源內容 |
| OS / RID | Windows `10.0.26300` / win-x64 |
| Shell | Windows PowerShell；未執行 `.ps1` |
| SDK / MSBuild | .NET SDK `10.0.103` / MSBuild `18.0.11+c2435c3e0` |
| Runtime / VSTest | dotnet host `10.0.11`；VSTest `18.0.1` x64 |
| SDK selection | repository 無 global.json；實際 SDK 如上，不借用歷史 SDK 10.0.402 的結果 |
| Test effects | in-memory fake HTTP/model，加上既有 test-owned local file/SQLite fixtures；沒有實際 provider/socket HTTP server |
| Runner control | 一個 test assembly/process，xUnit collections 不平行、MaxParallelThreads=1，session timeout 180s / 300s |
| Opt-ins | test process 設 `MAF_LIVE_OPENCODE=0`、`MAF_OWNED_PROCESS_OS_TESTS=0`；這些只是停用，不是 clearance |

VSTest 自身的本機通訊屬本次離線 test runner 範圍；不是對 live Harness loopback server 的授權。既有 fixture cleanup 僅管理 test-owned resources，未清理公司資料或 `.opencode/`。

## 2. 實際 Build / Test Results

| 階段 | 結果 | 範圍 / 解讀 |
| --- | --- | --- |
| 首次 solution build，no restore | **Failed**：0 warnings / 1 error，NETSDK1004；74.51s | MAF.Acceptance 缺 project.assets.json；Host/Test assemblies 有產出，但整個 solution 未通過 |
| 第一次 sandbox restore | exit 1，沒有診斷輸出 | 根因未確認，不能稱產品缺陷或安全誤報。唯讀 Application log 查詢無相符 crash；程序 command-line 查詢被拒絕，沒有繞過；未強制關閉背景程序 |
| 明確核准的單 node restore | **Passed**，exit 0 | `-m:1 --disable-parallel`，現有 solution dependencies，無套件版本修改；不更動安全設定 |
| restore 後 solution build | **Passed**：0 warnings / 0 errors；17.03s | 全 solution，包括兩個 acceptance projects 與 MAF.Tests；沒有執行 acceptance runner |
| 16 new authored cases | **16 passed / 0 failed / 0 skipped**；828ms | 5 R1 Facts + Alpha 3 Facts、3 Theories/8 rows，共 11 Alpha cases |
| 既有 scoped regression | **722 passed / 3 failed / 0 skipped**，725 total；3m11s | 21 個 class，排除未授權 OS/process-crash cases；**不是 clean regression** |
| 失敗方法的一次 diagnostic rerun | **9 passed / 0 failed / 0 skipped**；15s | 包含原失敗方法的 theory variants；未改 source、timeout、fixture、assertion、parallelism 或安全設定。與上一列重疊，不能加成 750 個 unique pass |

本輪涵蓋 741 個不同 test cases（16 + 725），有 3 個初跑失敗；diagnostic 9 是重疊再觀察，不抹除初始失敗。本輪沒有 full-suite acceptance。

TRX 的 session start/finish（UTC+8）：new16 `11:36:05.9306990–11:36:15.0192097`；regression `11:36:56.3196932–11:40:34.7953106`；diagnostic `11:41:08.0147107–11:41:31.8899710`。上表耗時為 runner 顯示的 tests duration，與含啟動的 session elapsed 分開。

實際 command：

```text
dotnet --info
dotnet build MAF.slnx --no-restore --disable-build-servers -m:1 -v minimal
dotnet restore MAF.slnx --disable-parallel --verbosity minimal
dotnet restore MAF.slnx -m:1 --disable-parallel --verbosity minimal
dotnet build MAF.slnx --no-restore --disable-build-servers -m:1 -v minimal
```

以下 test commands 之前，在該 PowerShell process 設定兩個 opt-in variables 為 `0`：

```text
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~MAF.Tests.LocalSoloDemoTests|FullyQualifiedName~MAF.Tests.OwnedHarnessProcessTests.SetupFailure' --logger 'trx;LogFileName=new16.trx' --results-directory tests/MAF.Tests/TestResults/personal-alpha-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

Regression 使用相同 command options，logger=`regression.trx`，timeout=`300000`，filter 精確為：

```text
(FullyQualifiedName~MAF.Tests.OwnedHarnessProcessTests|FullyQualifiedName~MAF.Tests.LocalSoloHostTests|FullyQualifiedName~MAF.Tests.LocalSoloReadinessTests|FullyQualifiedName~MAF.Tests.LocalSoloWorkflowTests|FullyQualifiedName~MAF.Tests.ExternalConsoleOutputTests|FullyQualifiedName~MAF.Tests.ExternalWorkflowTests|FullyQualifiedName~MAF.Tests.RuntimeLifecycleTests|FullyQualifiedName~MAF.Tests.IndependentLabRuntimeTests|FullyQualifiedName~MAF.Tests.LabOpenCodeExecutionPlanTests|FullyQualifiedName~MAF.Tests.LabOpenCodeBoundaryTests|FullyQualifiedName~MAF.Tests.BoundNativeRecipientTests|FullyQualifiedName~MAF.Tests.LocalExecutionPreparationTests|FullyQualifiedName~MAF.Tests.PersonalLabAdmissionTests|FullyQualifiedName~MAF.Tests.PersonalLabFirstRunBoundaryTests|FullyQualifiedName~MAF.Tests.NativeEvidenceReviewerTests|FullyQualifiedName~MAF.Tests.TypedHandoffTests|FullyQualifiedName~MAF.Tests.PolicyTests|FullyQualifiedName~MAF.Tests.TrustedAuthorityTests|FullyQualifiedName~MAF.Tests.OperationBindingTests|FullyQualifiedName~MAF.Tests.RuntimeEnforcementTests|FullyQualifiedName~MAF.Tests.ProtectedHandoffTests)&FullyQualifiedName!~SetupFailure&FullyQualifiedName!~ActualProcessCrashAroundEachDispatchIntentNeverReplaysPost&FullyQualifiedName!~RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost&FullyQualifiedName!~CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive
```

Diagnostic 使用相同 options、timeout=`180000`、logger=`protected-diagnostic.trx`，filter：

```text
FullyQualifiedName~ProtectedHandoffTests.FullNestedOfficialGraphUsesRealV4AdmissionReceiptAndEffects|FullyQualifiedName~ProtectedHandoffTests.WrongTrustedHandoffMappingCannotRewriteOriginalExecutionOrReachReviewer|FullyQualifiedName~ProtectedHandoffTests.InFlightObservationPreservesChildOwnershipAndNeverAborts
```

TRX 直接讀取 Counters 與 named outcomes 核對；artifacts 位於 ignored `tests/MAF.Tests/TestResults/personal-alpha-validation/`，不 commit 原始 logs：

| Artifact | SHA-256 |
| --- | --- |
| new16.trx | `F2B1AED054B9CF1A04C94D46E0671A9CA9D7B007C016F029A2357C2FFE3C837D` |
| regression.trx | `61F79342E9DA3C8FC157AEDEFD83AFFDCBCC095634E3F260F91B7AC1745D9AC2` |
| protected-diagnostic.trx | `1F65FF95B7E22E3A0C11F206CC85254D83E5215A0CFD14CEC362EE59421AF092` |

### Failures / 分類

1. `ProtectedHandoffTests.FullNestedOfficialGraphUsesRealV4AdmissionReceiptAndEffects`：expected ReadyForHumanReview，actual NeedsHumanReview，duration 2.6597603s。
2. `ProtectedHandoffTests.WrongTrustedHandoffMappingCannotRewriteOriginalExecutionOrReachReviewer(variant: execution)`：expected Reviewer calls=1，actual 0，duration 3.8237818s。測試先建立原 run，再檢查錯誤 mapping；不能僅由 review count 推論有錯誤授權通過。
3. `ProtectedHandoffTests.InFlightObservationPreservesChildOwnershipAndNeverAborts(variant: timeout)`：未到 observation gate，duration 2.8977788s。trace 保留 Unknown/ObservationTimedOut、Create/Prompt/GET/Abort=`1/1/4/0`，2s budget 在約 2020.76ms 觸發 cancellation。sqlite/receipt/preflight 已用掉大部分 budget；沒有自動重送或 Abort。

三個方法的 9 cases 在未改碼 diagnostic 全通過；現有 fixture 採 2s bound，觀察支持時間敏感/環境延遲假說，但沒有足夠資料將所有失敗歸為環境、測試缺陷或已修產品缺陷。**分類：既有 regression assertions failed，原因未完全確認；沒有已重現的 R1/Alpha correctness defect。** 未調大 timeout、弱化斷言或改 guard；本次無產品 bug fix。整體 regression reliability 維持 Conditional，後續若再發生，先做最小 timing/IO characterization，不反覆重跑直到綠燈。

### NotRun / NotSelected inventory

- `OwnedHarnessProcessOsTests` 5 個 opt-in OS process cases：**NotRun / 未授權**；同 class 的 KnownPlatformShellIsRejected metadata case 本輪未選。
- `RuntimeEnforcementTests.ActualProcessCrashAroundEachDispatchIntentNeverReplaysPost` 4 rows：**NotRun / 未授權 OS process scope**。
- `ProtectedHandoffTests.RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost` 7 rows、`CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive` 2 rows：**NotRun / 未授權 OS process scope**。
- OpenCodeLiveTests 與其他未選 class：**NotRun / NotSelected**；本輪 TRX skipped=0 不代表 live/OS 已執行或通過。
- 真實 OpenCode、HTTP preflight、三個 native/provider model recipients、A/B live comparison：**NotRun / 未授權**。沒有沿用歷史 live fixtures；其中有 direct-client/trust 與 cleanup Abort 路徑，不適合作為新的 pilot shortcut。

## 3. 實際 interactive CLI walkthrough

以本次 build 產物、PowerShell PTY 執行四次，逐次等待 preview 再輸入確認；沒有 source edit、OpenCode process 或模型。

```text
dotnet src/MAF.Host/bin/Debug/net10.0/MAF.Host.dll --local demo --workspace D:\_research_repo\MAF --objective "Offline coordination walkthrough" --scenario <completed|failed|unknown|waiting>
```

每次輸入 `run-demo` → `status` → `result` → `submit-live` → `quit`；completed 另多查一次 status。四個程序皆 exit 0，表示 CLI session 完成，**不是 task 都成功**。輸出每次只有一次 Task created、一次 PCA 與一次 Harness stage；重複查詢沒有新增執行 stage。

| Scenario | 實際 observed status | Reviewer | 其他結果 |
| --- | --- | --- | --- |
| completed | Completed，result 再查相同 | 執行 1 次，自動收到原 typed task/result | InsufficientEvidence、NeedsHumanReview、Evidence Unavailable、Human choice NotEstablished |
| failed | Failed，查詢不變 | 未執行 | 不顯示 Completed，不自動 retry |
| unknown | Unknown，查詢不變 | 未執行 | 顯示 Unknown 非 Failed/Completed、no automatic resubmission |
| waiting | WaitingForApproval，查詢不變 | 未執行 | safe stop，no permission reply or automatic approval |

每次 `submit-live` 都回 `Blocked / ProductionTrustUnavailable`，隨後正常 quit。以下是實際生成且在該次 status/result 維持不變的 identity，不是可重用的 live handles：

| Scenario | Task | Workflow run | Execution |
| --- | --- | --- | --- |
| completed | demo-153a0a275bfe4814b90178ba55e6bd1e | demo-run-0834640e39c44e588cc8488e1665a3c0 | demo-execution-077ca9079d01426f94a8b14553321df9 |
| failed | demo-9d00fda1a23f49a0952dbd3095d477a6 | demo-run-f1c1692452414a15b44f245bd4a61c1f | demo-execution-1550fb5b45c147eb9e134dd62450a022 |
| unknown | demo-ac12d56b294549d2b348b21a697aed3b | demo-run-64713b893f254c6f9a494ddf8f81e836 | demo-execution-b89e34b892ab44a391c9d141051a153e |
| waiting | demo-1316be13cfd64525bd4c90ce9d6ad51f | demo-run-5deabf1c243042aaa8b1e78df3d51069 | demo-execution-e255e6fccc34490e8377ca5b5a3c9b14 |

PTY stdout/input evidence 在本次工具紀錄；本報告保存結果摘要與 identity。未確認/EOF 安全退出另由 new16 的三個 confirmation theory rows 覆蓋，本輪未額外開第五個互動程序。使用者端人工計時與真實 app-switch/copy-paste 效益沒有由代理代填。

## 4. Critical review 結論

| 檢查 | Evidence / 限制 |
| --- | --- |
| post-start ownership | source 顯示 owner/backend 在 Start 前建立，setup-failed exception 回 exact Owner；5 個 R1 Fake Facts 通過。其餘31個 OwnedHarnessProcessTests 也在 regression 通過；未做原生 Windows handle/OS proof |
| exactly-one / no replay | new16 + 四次 CLI；task 只在 run-demo 後建立，status/result 只讀同一 view，不呼叫 ExecuteAsync |
| task/run/execution correlation | existing Workflow checks、mapper checks、new typed identity test 與真實 CLI IDs；沒有用 session ID 代替 run ID |
| completed / reviewer | completed scripted Harness 才進 Reviewer；assessment 始終 InsufficientEvidence，沒有 verified source/Accepted claim；其餘三 scenario 跳過 Reviewer |
| unknown / waiting | 保留明確狀態、no resubmit / no permission reply；失敗回歸的 timing trace 亦 fail closed，不虛構 Completed |
| activation | Program 的 --local 仍在 native/DI 前 return；固定 demo participants 不構造 OpenCodeClient；live submit unconditional denial 未改 |
| remaining reliability | 3 個既有 protected regression 初跑失敗，diagnostic green 不等於原因已解或整體 acceptance |

## 5. Pilot 與下一步

[V1 pilot plan、exact seams/工時、receipt blockers 與 A/B checklist](../plans/PERSONAL_ALPHA_V1_FIRST_LIVE_PILOT.md) 已完成。只查證官方 1.18.35 release metadata；沒有下載或驗證 binary，更未指定真實 live workspace、三 recipients/endpoints、config custody 或 live receipt evidence。

建議：Owner 可審核通過 **已執行的 offline journey scoped gate**，但保留 regression reliability Conditional。下一個最小 code increment 為既有 Host/Adapter 內 exact V1 pin + dedicated preflight/ownership，先 fake、維持零 Create/Prompt；process/loopback 仍需另授權。若現有 journal 無法誠實保留 live provenance/receipt，不增加 schema 或改標籤繞過，pilot 維持 Blocked。A/B 實際價值比較全部 NotRun，不能由 scripted walkthrough 宣稱成本已降低。
