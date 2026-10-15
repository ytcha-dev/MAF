# Phase 2B-1 家用驗收（Stage A–E）

**最新結論（2026-10-15）：受控家用 technical acceptance gate 通過。** Build、43 個 focused tests、155 個 offline tests、六個 SQLite process checkpoints、七個 controlled-provider Host crash 情境與三個真 OpenCode recovery 情境均有實際結果。這不是公司 endpoint clearance 或 production readiness；沒有開始 Phase 2B-2。

本次開發基準為 `676350c`，新增內容限 test-only tooling、四個 launcher cases、solution entry 與文件；production execution implementation／權限／既有 assertions 未修改。使用者明確授權專用程序 abrupt termination、synthetic live validation，並進一步授權免費模型。安全事件與公司端結果原檔均未修改。

## 第一輪離線基線（歷史記錄）

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

第一輪當時 Phase 2B-1 **尚未完整驗收**，沒有終止程序或 live prompt。以下是後續另行授權的實際 C/D/E 結果；公司端報告保留其當時狀態。

## 最終 build／focused／regression

- `dotnet build MAF.slnx --no-restore --configuration Debug`：exit 0，0 warnings / 0 errors，含 test-only `MAF.Acceptance`（solution 現有 8 projects）。
- Focused：原四個 classes 加 `OpenCodeLauncherTests`，43 executed / 43 passed / 0 failed / 0 skipped。原 39 assertions 未削弱；新增四個 cases 驗證 npm `.cmd`／`.ps1`／extensionless wrapper、native `.exe` 解析與 unknown/missing launcher 拒絕。
- Full offline：155 executed / 155 passed / 0 failed / 0 skipped；`OpenCodeLiveTests` 明確 excluded。43 已包含在155，不相加。TRX outcome 皆 Completed。
- 最終八個 projects 的 direct/transitive NuGet vulnerability query 未回報已知易受攻擊套件。Native `SELECT sqlite_version()` 實際回傳 `3.53.4`。
- 最終 TRX：`TestResults/home-validation/phase2b1-crash-focused.trx`、`phase2b1-crash-offline.trx`（ignored）。

最終可重現命令與 fixture／process ownership 規則見 [runner README](../../tests/MAF.Acceptance/README.md)。Runner 以 `dotnet MAF.Acceptance.dll run <built-Host.dll> <new-local-directory>` 與 `live <built-Host.dll> <new-local-directory> <installed-launcher> opencode/big-pickle` 明確啟動；不由一般 `dotnet test` 自動執行。

## Stage C — Passed：真實跨程序 SQLite

最終 run label：`process-final-20261015`，runner exit 0。每個 checkpoint 使用獨立新 DB。Writer 在正式 store transitions commit 後發 stdout signal，仍持有 ownership；另一 OS process 必須取得失敗。Driver 驗證自己建立的 Process object、PID、StartTime 與 executable，強制終止該 writer，再由新 child 成功取得同 TaskId lock，不刪 DB／journal／lock file。

| 已 commit checkpoint | Writer PID | Fresh reader PID | 保留 version |
|---|---:|---:|---:|
| Created | 53288 | 55844 | 0 |
| SessionCreating | 52096 | 54016 | 1 |
| ReadyToSubmit | 35680 | 55160 | 2 |
| SubmissionIntended | 55596 | 2584 | 3 |
| Running / Acknowledged | 56028 | 48016 | 4 |
| Completed / terminal commit | 55272 | 54140 | 5 |

六輪均確認：跨 process identity/state/version 不變；所有 writer 已死亡後，由新 process 透過實際 store 讀回，並以 ReadOnly SQLite connection 得到 `integrity_check=ok`、`user_version=1`、row count=1、snapshot/version/terminal consistency count=1。Invalid identity transition 被拒絕，duplicate TaskId INSERT 觸發 SQLite constraint，不產生第二筆。

Correlation：`synthetic-task`／`synthetic-run`／`synthetic-execution`／`msg_synthetic`；ReadyToSubmit 起 session=`ses_synthetic`。Fingerprint/scope 為固定 synthetic hashes。這證明已 commit 的程序級重啟；不等於 power-loss、磁碟故障或 journal 中途斷電驗證。

## Stage D — Passed：真實 production Host crash／recovery

同一最終 process run 的七個情境，使用實際 `dotnet MAF.Host.dll`，非新 service instance 或 `dotnet run` parent。Controlled provider 留在 driver；Host 死亡不影響它的 session/message 與 request counters。先確認 provider accepted exactly one prompt 與 DB checkpoint，再 abrupt terminate 專用 Host，重新啟動同 DB／TaskId／scope 的新 Host。

| Variant | 原 Host PID → 新 recovery PID | Kill checkpoint | 初次 recovery 結果 | 後續 |
|---|---|---|---|---|
| active | 22376 → 49616 | Running / Acknowledged | Completed / exit 0 | 先觀測 busy，再由 gate 允許完成 |
| lost-response | 7972 → 46284 | SubmissionIntended / PromptIntended | Completed / exit 0 | Provider 已接受，但 acknowledgment 保持未回；不重送 |
| unavailable | 48536 → 47956 | Running / Acknowledged | Unknown / exit 4 | 同 provider 恢復後新 Host 取得 Completed |
| completed-while-stopped | 31748 → 54936 | Running / Acknowledged | Completed / exit 0 | Host 停止後 provider 才完成 |
| running | 45352 → 49192 | Running / Acknowledged | Unknown / exit 4 | 保持 busy 至 wait timeout；稍後 Completed |
| permission | 46496 → 50888 | Running / Acknowledged | WaitingForApproval / exit 5 | 沒有 approval POST；controlled evidence 改變後 Completed |
| cancelled | 49364 → 45408 | Running / Acknowledged | Cancelled / exit 130 | 必須有 correlated MessageAbortedError |

**每個 variant 的 session POST=1、prompt POST=1；recovery 增量皆0，abort POST=0。** 每輪原／新 TaskId、fingerprint、provider scope、workflowRunId、executionId、sessionId、messageId 完全一致，完成時 body 必須等於 synthetic expected result。檢查各自 DB integrity/count 也全部通過。

例：active 原／恢復皆為 workflow `0fb816703b5d4dec8ebdd4a361d1dbd1`、execution `3b7431795f0d4407b6bf219679f70780`、session `ses_controlled`、message `msg_01a14025a62450f7923e21d3`。七輪完整 IDs、版本、checkpoint UTC、PID/StartTime、後續 recovery PID 與 counters 見 [sanitized process evidence](phase2b1-home-process-evidence.json)。每個 variant 使用自己的 provider／DB，因此固定 synthetic session 字串不代表跨 variant 共用 session。

## Stage E — Passed：真 OpenCode／免費模型

使用者要求尋找既有 launcher，不重裝、不改全域 PATH。`Get-Command opencode -All`／`where.exe opencode` 找到 npm `.cmd`／`.ps1`／extensionless launcher；`.cmd` 指向套件內原生 executable。`opencode --version` 與 server health 均為 **1.19.0**。個人 config 只有 schema，沒有預設 model/custom provider；列出可用 built-in free models 後，依使用者授權選擇 **opencode/big-pickle**。[官方 Zen 說明](https://docs.opencode.ai/docs/zen/) 列其為免費模型；本次每輪實際 assistant metadata 也確認 provider=`opencode`、model=`big-pickle`，不是只抄配置。

最終 run label：`live-final-20261015`，runner exit 0。使用新 synthetic Git repository；僅 README、Program 與完整 config。新 XDG config/data/cache/state、`--pure`、移除 inherited provider keys/OpenCode overrides，不修改原個人 profile。Agent/session 權限維持 deny-default，只有 read/glob/grep 可用，shell/write/external_directory 不允許。不送 MAF/company source、公司 endpoint 或 credentials。

Test-owned transparent loopback proxy 每次轉送前計數，不重試 POST、不跟隨 redirect、不記錄 headers/body。所有 Host 的 POST 只能經該固定 origin；旁路 observer 僅 GET 真 server。它的受控503可讓 Host失去證據，同時保留真 OpenCode工作，且不改 scope。

| Variant | 原 PID → 首次 recovery PID → final PID | 初次結果 | 最終結果 | session/prompt POST |
|---|---|---|---|---|
| active | 50812 → 16344 → 54956 | Unknown / ObservationTimedOut / exit 4；此時真 server 仍 busy | Completed / exit 0 | 1 / 1 |
| completed-while-stopped | 53592 → 49764 → 49764 | Provider 在 Host 停止期間完成；新 Host Completed | Completed / exit 0 | 1 / 1 |
| unavailable | 49856 → 48692 → 25276 | Unknown / ReconciliationUnavailable / exit 4；真 server 仍 busy | 恢復同 origin 後 Completed / exit 0 | 1 / 1 |

三輪都在 durable identity 已 commit、實際 GET status 證明 busy、原 Host 仍活著時強制終止。下表的 IDs 在原 record、每次恢復 stdout 與最終 DB 均相同：

| Variant | workflowRunId | executionId | sessionId | messageId |
|---|---|---|---|---|
| active | `0c94e8cb9dc743e4a5fec4c05d27f453` | `d1b13382e6854752948946b9b61eea2a` | `ses_ebfda28d3ffeptEFwzRRIOFT1R` | `msg_01a14025d2140415904a0303` |
| completed-while-stopped | `b94985cc56c84f7a9639e3fd78a8922a` | `d79df8d5d2b44392a8a0b8f39dc3c697` | `ses_ebfd9e1a9ffejd6YLyK0kpL2nz` | `msg_01a140261dd2db8bbd8a9f3f` |
| unavailable | `8f837a1ae3184dd4a3476b4bbbf02b6c` | `4762d7e1f0374f1d94956bef99fd13ba` | `ses_ebfd9840bffer5ObeOk5hOdqQB` | `msg_01a140267b7770158ee91f03` |

TaskIds 分別為 `live-active`、`live-completed-while-stopped`、`live-unavailable`，各自 DB。三輪完整 source relative-name→SHA256 manifests、`.git/index` SHA256 與完整 porcelain status 全部相同，沒有寫入差異。Sanitized evidence 包含 before/after manifests、actual model IDs、first Unknown snapshot、busy-after-recovery、最終 correlation 與 count。

## 真實失敗、修正與保留的界線

最初 preflight／fixture 問題都沒有算成 pass：

1. 起初僅查 `opencode.exe` 漏掉 npm launcher；已支援並測試現有 Windows wrapper→native 解析，未重裝或硬編碼個人路徑。
2. 非標準隔離 XDG data path 讓 OpenCode 自動加入的 tool-output-cache permission 不符既有 narrow allowlist，`AgentAllowsExternalDirectories`；改用該既有 layout 預期的 `.local/share`，未放寬 production validator。
3. MSIX AppData 轉址造成 native OpenCode session directory 與 .NET approved root 不同，`SessionPolicyMismatch`，未送 prompt；改用普通非 AppData local directory，保留 strict workspace validation。
4. `live-20261015-07` 的 OpenCode startup 自動補 `$schema`，manifest check 正確判失敗。原 fixture 未 revert；新 fixture 從建立時包含完整 schema。後續 `live-20261015-08` 三輪通過；最終 runner 加強 first Unknown／busy／correlation metadata 後，再執行 `live-final-20261015` 三輪全部通過。

原始本機 DB、OpenCode profile、raw runner evidence（有本機路徑）保留於 test-owned run directories，不提交。公開證據只保留 synthetic IDs、必要 timestamps/PIDs、hashes/counters/relative manifests；未提交 prompt/tool transcript/auth headers/key。未觀察到 endpoint security alert，沒有改防毒或 execution policies。

**未證明的能力：** 真實模型供應商 outage／provider程序死亡後task本身可恢復、live permission-pending／cancellation cleanup、live session-creation-response loss、power-loss/journal corruption、跨機或跨 DB副本去重、任意 OpenCode版本／cache layout／模型相容性。這些標為 Not Demonstrated；不能用本次 forwarder outage 或 fake cases 推論通過。Lost prompt acknowledgment、waiting/cancelled 的本次證據範圍是 Stage D controlled provider。SQLite 與 HTTP 仍無共同 transaction，尤其 session creation response 遺失仍可能 Unknown，沒有 remote exactly-once 保證。

**Technical gate 判定：** A/B/C/D/E 對本次單機單 DB、唯讀 synthetic task、所列版本與情境的 acceptance 通過：實際 OS crash／新 PID、原 identity/session recovery、uncertain prompt不重送、可靠 POST accounting、正確 Unknown 與 source integrity 均有證據。上述未涵蓋能力保留限制，並非所有可能 failure windows 的證明。公司安全 hold 未解除；Phase 2B-2 的 policy/approval/cancellation 仍由 owner 另行決定，本次到報告為止。
