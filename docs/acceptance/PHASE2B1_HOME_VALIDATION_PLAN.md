# Phase 2B-1：個人 Windows 驗證計畫（未執行）

日期：2026-10-15。Implementation baseline：`95c963205b742de21cb302f9fea02041921501f5`。本計畫只準備 handoff；**沒有個人機器實測結果，也沒有解除公司 execution hold**。安裝／變數／可執行命令見 [Windows setup](../setup/WINDOWS_VALIDATION_SETUP.md)。

## Gate 0：授權、來源與證據範圍

開始 A 前確認 source及必要 history/fixtures 可合法帶入該個人環境。Git remote access 或既有 push 不足以授權公司材料移轉。若未核准，所有 stages 記 Blocked，不 clone／export；用 owner 提供的 sanitized snapshot 或另行 synthetic implementation。公司 logs、DB、profiles、keys、機器 NuGet 設定、ignored TestResults 不作輸入。資料審查的候選與限制見 setup §11。

另記錄 personal execution／tools／local process termination／provider 各自授權。Stage A–D 不需要公司模型或網路。Stage E 沒有合適的獨立核准 provider 時保持 Blocked；不得回連公司 PoC 或把公司 source 送去個人 cloud。

全程保留自己的 synthetic evidence；任一 endpoint security alert 立即停止該 workflow，不改名／換路徑／換 loader 重試。新個人環境不是公司 security block 的旁路。

## 執行順序與停止規則

| Stage | 前提 | 執行範圍／通過 gate | 現況 |
|---|---|---|---|
| A：build/dependency | Gate 0、setup P0–P2 | Restore、direct/transitive graph、audit、build exit0，警告逐一處理 | Pending，未執行 |
| B：offline regression | A 通過 | 新39 authored cases 真正 discovery／執行，既有 deterministic suite，live excluded，無未解 failure/aborted run | Pending，未執行 |
| C：SQLite durability | B 通過 | cross-instance、cross-process、CAS/ownership、integrity、post-crash reopen；不能只引用舊 smoke | 部分案例存在；cross-process/integrity helper 尚缺 |
| D：dedicated Host crash | C 與下述 runner prerequisite | 在已觀測 checkpoint abrupt terminate 自己 Host；保留 provider；同 DB/IDs recovery；POST counts 不增加 | Runner 尚缺，Blocked pending implementation |
| E：live OpenCode | A–D 及獨立 live 授權／API/policy | 真 OpenCode active restart、相同 session/message 最終結果、無重送、source/index/status 不變、outage後reconcile | Dedicated acceptance runner 尚缺；未執行 |

遇到 failed/skipped/not discovered/blocked 均如實填寫。Stage 的前提不滿足就停止依賴它的驗收；可獨立診斷，但不能把後續偶然成功算 gate 通過。不要自動開始 Phase 2B-2／2C。

## Stage A — Build 與 dependency validation

1. 記錄 source revision、dirty state（只列必要檔名）、Windows／SDK/runtime／CPU architecture、Git version、時間與獲准的 install provenance。不複製個人 username／home paths 或 secret variables 到公開報告。
2. 核對 setup matrix 的 7 個 net10.0 projects、1.24.0 MAF packages 及 SQLite 10.0.0 + bundle3.0.5。沒有 global.json／lockfile，不能宣稱 bit-for-bit reproducible；保存 sanitized resolved graph 作該次基準。
3. 依 setup §7 restore、package list/audit、build；每步記實際命令、start/end、exit code、warnings/errors。不要動 NuGet sources／TLS／安全 policies 或因警告而無關升級。
4. 特別檢查 NU1903 歷史 issue 是否仍存在：native transitive component 的解析版與 audit結果比 top-level bundle version 更重要。Feed/audit不可用不是「無漏洞」。
5. 核對 build output 的 MAF.Host.dll、runtimeconfig/deps 與 native e_sqlite3.dll 資產／RID，沒有 native DLL 不能先宣告 runtime 可行；真正 load 於 B/C 驗證。不要複製別台 DLL 補洞。

Pass evidence：實際 restore/build exit0、完整 warning disposition、resolved dependency graph、SDK/runtime/RID。現況全部 Pending。

## Stage B — Offline regression 與 39 新案例

1. 保持 `MAF_LIVE_OPENCODE` 未設定，明確排除 `MAF.Tests.OpenCodeLiveTests`，不需要 AI／OpenCode 設定或 running server。
2. 先用 `dotnet test --list-tests` 記錄真實 discovery，再執行 setup §7 的 new-cases filter（4 classes），獨立 TRX。
3. Source 靜態計數：DurableExecutionTests=29、DurableExecutionStateTests=3、OpenCodeRecoveryTests=2、DurableConsoleOutputTests=5，合計39；它們不是39個已通過測試。Discovery 數不同先查方法 visibility、theory 展開、filter、SDK/test adapter 與編譯結果。
4. 再執行所有 offline tests，獨立 TRX。舊 baseline112只是歷史，總151只是未改 source 時的估算，不能填成 actual result。測試之間可重疊，不把兩輪 passed 相加當唯一 case 數。
5. 保留 testhost crash、cleanup failure、native loading、Git/junction 被拒等真實失敗；若 endpoint protection 中止，整輪 invalid，不以先前若干 Passed 代替。

Offline fixtures 有 file writes／deletes、Git child processes、Windows junction；「offline」只界定無 live provider，不等於純 memory。資料為 synthetic，不用公司 artifact。

Pass evidence：actual discovery/result counts、run outcome、TRX、任何 skipped/excluded 的理由與安全事件。測試通過仍不證明 real process crash 或 OpenCode interoperability。

## Stage C — SQLite durability

已有可執行入口：setup 的 `FullyQualifiedName~MAF.Tests.DurableExecutionTests` filter；此組仍只是在同一 testhost 中建立新 store/service，或注入 exception。

| 要驗證的性質 | 現有 source case | 所需補充 evidence |
|---|---|---|
| identity / terminal metadata 跨 instance | CompletionAndIdentitySurviveNewServiceAndStoreInstances | Dispose原service後新store/client讀同DB，所有IDs與fingerprint一致 |
| intent / accepted-post fault windows | InjectedCrashAtPersistenceBoundary、CrashAfterRemoteAcceptedBeforeLocalAcknowledgmentRecovers | 記錄注入點、durable state/phase、fake session/prompt count；此為 simulated exception，不是 process crash |
| ownership / stale version | ConcurrentOwnersAcrossStoreInstancesCannotSubmitSameTask、CompetingServicesWhileFirstObservesCannotCreateOrSubmitAgain、InvalidTransitionAndStaleVersionAreRejected | 競爭者不能Create/Submit；DB state/version不被舊值覆寫 |
| schema / sensitive persistence | FutureSchemaIsRejectedWithoutMigration、PersistenceContainsNeitherPromptNorProviderOutputNorExceptionText | 不破壞未知schema；DB／diagnostics無synthetic secret marker，不保存 prompt/body |
| cross-process、OS lock release、integrity | **沒有現成runner/helper** | 下述額外helper與Stage D；不能當成已有test |

### 缺少的 cross-process / integrity helper specification

在獲准個人開發階段補一個 test-only .NET 10 helper（獨立 project 或等效已審查機制），重用 Core／Persistence.Sqlite，不能新增到正式 Host 的 agent loop。這是待實作規格，**目前不存在可直接呼叫的命令**，不要發明 `--crash-at`／`--inspect-db` 等 CLI flags。

- `seed/read`：由不同 process 透過真 SqliteExecutionStore 寫入／讀回同 synthetic snapshot；phase/state須經正式 transitions。使用固定 synthetic TaskId、無 secret的correlation。helper只輸出safe IDs、state、version、schema及native sqlite_version，不輸出完整snapshot/prompt。
- ownership：Process A 持有某 TaskId ownership，透過 stdout signal/IPC 明確表示 acquired，等待 bounded gate；Process B 對同 DB/TaskId應失敗。終止**自己啟動的 A**後，新的 B 應取得 ownership。檔案殘留不等於仍被鎖；不刪檔搶鎖。
- integrity：writer全關閉後，由新process用 `ReadOnly` 模式開既有 DB，`PRAGMA integrity_check` 應為 `ok`、user_version=1、PK唯一、snapshot/version/terminal一致。不要用可自動建空DB的ReadWriteCreate把不存在的資料庫偽裝成恢復成功。
- failure boundary：checkpoint至少包括 Created、SessionCreating、ReadyToSubmit、SubmissionIntended、acknowledged Running、terminal commit；在commit後發signal再終止。磁碟電源中斷／journal中途損壞不是此process-level測試可宣稱的能力。
- 給定5秒busy timeout，bounded等待並記真實 SQLite exceptions；不改lock策略掩蓋failure。所有DB/lock檔為新個人測試資料，沒有用公司DB。

可選 SQLite CLI 只能輔助 read-only integrity 觀測；沒有理由為此安裝系統服務。缺helper或任何子測試未做，C標Partial/Blocked，不能引用歷史SQLite smoke補格。

## Stage D — 真正 dedicated Host restart

### 必須先補的 controlled-provider harness

現有 `FakeClient` 與 `OpenCodeApiFixture : HttpMessageHandler` 不接受跨process HTTP，不能直接供 Host 使用。本次不實作新的 harness executable；個人階段需依現有fake contract建立**test-only loopback provider**及driver，並先審查副作用。它是驗證stub，不是新的coding harness、agent loop或production adapter，不引入 write-enabled model tools。

必要 contract：

1. 與實際 OpenCodeClient 相同的 health、doc、agent、session policy/schema、status、messages、permission/question、abort routes，保留 query directory與readonly rules。穩定 session/message IDs，接受 supplied messageID。
2. 可被 driver 控制 busy→completed／waiting／cancelled／unavailable，使用 synthetic response，不呼叫外部LLM。不讀repo的公司檔案。
3. 記錄 test-owned session POST count、prompt POST count、accepted message IDs；以獨立於Host的counter保留在Host死亡後。只存method/path/correlation/count，不記auth headers或完整body。
4. gate可在「provider accepted prompt但不回response」停住，且driver能獨立觀測；SQLite observer能在短read-only連線看到已commit的SubmissionIntended/Running。不要讓長read transaction阻塞writer。
5. driver以明確的protocol signal而非固定sleep，且每步有deadline；實際Host程序直接 `dotnet MAF.Host.dll`，不使用`dotnet run`parent。保存返回的PID、StartTime及executable/argument identity，所有其他程序不在終止範圍。

### 有runner後的有序操作（目前Blocked）

1. 用新run directory／DB、synthetic source、新TaskId，啟動test-owned stub，保存counter=0。啟動專用Host submission process，stdout/stderr分開收集；secret不放參數。
2. 等到provider記錄同一session、一次prompt accepted，且read-only DB observer確認原TaskId的session/message、state/phase已commit。對intent-window variant可由stub gate控制response尚未返回。
3. 確認Host仍活著且remote仍busy，記checkpoint時間／snapshot metadata／counter。若已完成或逾時，該次不是active-crash scenario，換新的synthetic run重排，不造假。
4. Operator/driver僅 abrupt terminate自己捕捉的Host process object；再次核對PID/StartTime/command以防PID reuse。不使用process-name搜尋後全殺，不停止stub/OpenCode。**Ctrl+C會觸發abort，不能當crash**。
5. 等original process完全離開。不要刪DB、journal、ownership檔，不複製到另一DB；重新啟動Host `--recover <same TaskId>`，同root/scope/database。Runner原生啟動/終止機制需在個人環境另外授權，這裡沒有可直接複製的廣泛kill命令。
6. Stub先保持busy，recovery應觀測原session；再控制completed，應回原message final result。比較pre/post TaskId/fingerprint/run/execution/session/message，與POST counts：**session=1、prompt=1，recovery增量=0**。
7. 另一variant在Host死亡後、recovery前讓provider完成，驗證「已完成待取回」；另做原provider暫不可用→Unknown→同provider恢復→取得結果，counts仍不增。Unavailable用test-owned stub的受控回應模式，不改firewall／網路卡或終止shared provider。
8. 無sessionidentity的creation-response-loss、intent保存但沒有POST者，應保持Unknown，不自動建session或補送；waiting/cancel不得誤報success。終態已保存後localoutput遺失，recovery應重讀原provider。
9. 重新開DB作Stage C integrity檢查。每個checkpointvariant記錄自己的結果，不把一個variant延伸成全部crash-window coverage。

Pass需要實際OS process分離／死亡／新PID、同DB、同遠端session、無新增POST的原始sanitized證據。僅再次new service或throw exception不是D。

## Stage E — 真 OpenCode lifecycle recovery

### 前提與baseline

- 使用setup §8的新synthetic README.md/Program.cs/opencode.json、普通 `.git`及index；模型不接觸MAF源碼。與durable DB/evidence分開，不帶公司configuration。
- 記錄native OpenCode CLI及health version、模型provider/model ID（不含endpoint敏感值或key）、API/schema檢查、readonly agent有效policy。1.18.35是歷史可用版本，不宣稱其他版本已支援。
- 既有3個Phase2A live facts可做獨立compatibility baseline；它們不使用durable service，不算本Stage通過。Permission fact會留下pending的歷史限制必須記錄；不自動批准。
- Dedicated E driver／durable DB observer／POST request counter 尚缺。需先於個人開發階段實作/審查；不能只從「messages中只有一個user message」推論完全沒有重複POST，provider可能去重。

### Source與HTTP evidence

開始前記錄synthetic source所有檔案（含opencode.json、排除.git）的relative-name→SHA256 manifest、`.git/index` bytes/hash及完整 `git -c core.fsmonitor=false --no-optional-locks status --porcelain=v1 --untracked-files=all`。結束時重算完整file集合/hash/index/status；不只比較tracked diff。OpenCode自己的profile資料不在source manifest內，不授權它在source產生檔案。

要證明沒有第二個prompt，需有選定版本可提供的**test-owned server request audit/counters**（只記method/path/session/requestID/數量，禁止headers/body），或在個人環境另行核准、經審查的loopback測試監測設備。此handoff不提供未審查proxy，也不修改安全政策。若server沒有可靠request accounting，目前只能證明同session／correlatedmessage，**no-duplicate-POST gate須記Not demonstrated**。

### 必要live variants

| Variant | 操作／觀測 | Pass條件 |
|---|---|---|
| E1 submit | 透過MAF durable Host提交readonly synthetic task；記DB intent與原handle | 穩定TaskId/fingerprint及run/execution/session/message；一次session/prompt |
| E2 active Host crash | 透過observer確認remote busy及identity已保存，再依D的PID ownership規則終止專用Host，server繼續 | 明確時間/remote active證據，不是正常exit或Ctrl+C abort |
| E3 reconnect | 新Host以同DB／TaskId recovery | 同session/message；沒有新session／prompt |
| E4 already completed | Host停止後由OpenCode完成，再啟新Host recovery | correlated final response重取成功；結果無須高 coding品質但必須真有final evidence |
| E5 still running/timeout | recovery先看busy，durable wait budget到期回Unknown，稍後再recover | 沒有timeout自動abort/重送；之後以新證據分類，而非idle即Completed |
| E6 unavailable then restored | 經核准的test-owned unavailable mechanism後，保留DB與provider sessions，再恢復 | Unknown而非confirmedmodelFailed；恢復後同identity，counter不增加 |
| E7 integrity | 每variant結束比較完整source manifest/index/status | 無任何未授權變更，所有差異均記failure，不自動revert抹證據 |

E6不能任意換OPENCODE_BASE_URL製造outage，因scope會不同。可以在**模型task已保存、具可驗證恢復能力**的dedicated OpenCode程序做planned停止/恢復，但該實驗可能讓provider task本身中斷，必須先評估且如實記錄，不能冒充只有網路不可用。若沒有不改scope、不終止shared server、可保留task的安全機制，就記該variant Blocked；controlled fake的unavailability不替代live證據。

若模型過快，無法證明active window，記Not reproduced，再另行安排bounded synthetic workload；不放寬shell/write權限、沒有測試專用開發loop，也不要求真實敏感repo或數十分鐘耗用。若模型讀完但沒final answer，記Unknown，持續原session的明確recovery，不能用新prompt「提醒」它完成。

每variant均用新的syntheticTaskId與專用session建立新實驗；同一variant中的restart必須保留原TaskId/DB。不能把先前Unknown任務改TaskId重新提交後當成恢復成功。

## Coverage與證據模板

結果另建經審查的home evidence／報告，不覆寫[公司端未驗證結果](PHASE2B1_RESULTS.md)或[安全事件](PHASE2B1_SECURITY_EVENT.md)。實際結果不得預填Passed。

```text
Run label / UTC start-end:
Source revision / approved snapshot provenance / dirty files:
Authorization reference (no private account identifiers):
OS / CPU architecture / SDK / runtime / Git:
Resolved NuGet / native sqlite_version / audit warnings:
Stage / variant / exact command (secrets omitted):
Discovered / executed / passed / failed / skipped / excluded:
Process IDs and start times (test-owned only):
Checkpoint / durable state / submission phase / version:
TaskId / fingerprint / WorkflowRunId / ExecutionId / session / message:
Session POST count before-after / prompt POST count before-after:
Provider/model ID / native OpenCode and health version (live only):
Observed state / Host exit code / provider evidence:
Source-manifest/index/status before-after:
TRX / sanitized driver evidence / integrity check result:
Outcome: Pending | Passed | Failed | Blocked | Not reproduced | Not demonstrated
Limitations / security events / cleanup ownership / next action:
```

Source/hash/paths/sessionIDs可能仍屬metadata，公開前審查。不要附原始prompt/tool transcript、rawHTTP/auth headers、全environment dump、company incident logs或完整user profile。個人synthetic fixture清理由owner確認scope後安排；不寫不受限的recursive cleanup命令，也不清除安全事件。

## 未解 prerequisites 與 acceptance

1. Source/history／資料分類及個人environment/tool/provider授權：**未在本次確認**；新文件不是transfer approval。
2. Clean Windows的SDK/native/dependencygraph／NuGetaudit／build：**未執行**。
3. 39新cases真實discovery及offline regression：**未執行**，公司端baseline中止紀錄不可沿用為pass。
4. Cross-process SQLite helper、read-only checkpoint observer、controlled provider/Host crash driver：**尚未實作**。
5. Dedicated live driver及可靠no-duplicate-POST accounting、保留session的unavailability機制：**尚未具備證據**。
6. Liveprovider/model可用性、版本相容、active crash window可重現與sourceintegrity：**未驗證**。

Acceptance需要A–E的實際證據，包含same-session recovery、uncertain POST不盲目重送、正確Unknown、validated transitions、readonly無regression、真正process restart及清楚區分fake/live。缺runner、modelfinalresponse或requestcounter就如實保留gap，不以文件／commit作驗收。**此handoff完成後停止；不啟動Phase2B-2，也不恢復公司測試。**
