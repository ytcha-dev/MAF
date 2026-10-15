# Phase 2B-2 M2-A：Durable Approval Persistence 結果

日期：2026-10-16（Asia/Taipei）。開發基準`9849933`。Owner明確批准M2-A資料層、offline migration、synthetic persistence/process tests；未批准M2-B/M3、provider POST或任何外部agent執行。

**結論：本輪M2-A build、51個focused cases與304個offline cases通過。** Approval與execution為獨立durable lifecycles；寫入Approved不會啟動task，consume只做資料交易，不授予可執行dispatch權限。完整Phase2B-2與production authorization尚未驗收。

## 實作與安全邊界

| 項目 | 本輪實際行為 |
|---|---|
| [Core contracts](../../src/MAF.Core/Policy/ApprovalPersistence.cs) | IApprovalStore讀取介面＋IApprovalTransactionCoordinator窄mutation API；typed immutable approval/decision/control/audit metadata；Core無SQLite transaction type |
| [Approval store](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs) | Pending、Approve/Reject、Expire、Revoke、Consume；完整snapshot+version CAS、exactbinding、全域DecisionId uniqueness與idempotent duplicate decision；decision本身不建立execution |
| [Explicit maintenance](../../src/MAF.Persistence.Sqlite/ApprovalSchema.cs) | 新空DB→v2、已知v1→v2；停機/backup確認、exclusive file gate、既有TaskId ownership檢查、BEGIN EXCLUSIVE與atomic DDL/user_version；normal open不遷移 |
| Atomic admission | 重新檢查current policy／authority revision／binding、originalcaller、當前operator權限、control version與cancel intent；同transaction consume批准、insert唯一Created execution、audit。無Workflow/HTTP、無dispatch ticket |
| Durable cancellation intent | task_controls初始version0；首次request保存唯一CancelRequestId/actor/time並提升version，同transaction revoke尚未consume批准；重複同ID安全返回，衝突拒絕。不改execution遠端狀態、不設定remote Cancelled、不發abort |
| Bounded audit | fixed enum code、actor/task/approval IDs、record version、UTC、policy epoch/version；最多10000 rows，超限整筆mutation rollback。read page 1–100，無自由文字reason或任意payload；不自動刪除舊audit |
| Runtime disabled | 沒有Host DI/CLI整合。一般SqliteExecutionStore／Host仍拒絕v2；approval store只開已存在v2。v2的executions表延續IExecutionStore原格式，但目前不開放runtime使用，避免未完成enforcement前產生旁路 |

一般v1execution store只新增短schema共享lease（connection lifetime、ownership建立時），配合維護exclusive gate；原state machine、fingerprint、CAS、provider safety邏輯與既有tests/assertions均未修改。沒有自動schema upgrade、downgrade或未知版本猜測。M1的98個cases原樣保留。

### Protected authority boundary

只有明確注入的`IProtectedPolicyAuthority`能提供policy snapshot、重新評估stored operation、檢查operator/caller scope與provider identity mapping。它不是request／repo設定可選擇的source；本輪**沒有production implementation、identity resolver或fake fallback**。SyntheticAuthority/SyntheticActor只在test-only helper assembly，Host不引用。

DB的singleton policy_authority保存AuthorityId、epoch、version及固定欄位/排序collection的policy digest。首次可信source pin後，更新需同一authority、epoch與version都嚴格提高；同epoch/version內容必須相同。Caller request內epoch只能match，不能activate。Authority high-water先以獨立短transaction commit，再執行business mutation，因此被拒絕的stale approval不能把已觀測的高水位rollback。Mutation前後再比對source/pin；測試涵蓋source在decision transaction中變更時整筆decision/audit rollback。

Authority high-water audit是獨立可信控制事件；即使後續business mutation rollback，該event仍可存在，並非全部操作資料被回滾。Policy source必須遵守受保護、單一authority、revision activation與storage maintenance序列化的契約；這不是production activation或跨process身份驗證實作。惡意DB owner、整份backup rollback或不遵守契約的host/source不在此保證內。

## 實際測試結果

Windows / .NET SDK10.0.401。新增test-only `MAF.ApprovalAcceptance`，solution共9 projects；沒有新增NuGet dependency版本。

| 檢查 | Discovery／執行／結果 |
|---|---|
| Build Debug（最終） | exit0，0 warnings / 0 errors |
| Focused discovery | `--list-tests`實際列出51 entries，全部為Fact／已展開InlineData |
| M2-A focused | 51 executed / 51 passed / 0 failed / 0 skipped；TRX Completed，exit0 |
| Full offline regression | 304 executed / 304 passed / 0 failed / 0 skipped；TRX Completed，exit0 |
| 原有基線 | 同一full run中的155個原2B-1/native tests＋98個M1 tests＝253，全部通過；新增51已包含於304，不相加 |
| Live／provider dispatch | 未執行；OpenCodeLiveTests由filter明確排除；未啟動舊MAF.Acceptance provider runner。既有HTTP fixtures僅是記憶體handler，不是外部provider POST |

命令（實際執行）：

```powershell
dotnet build MAF.slnx --no-restore --configuration Debug
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~MAF.Tests.ApprovalPersistenceTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~MAF.Tests.ApprovalPersistenceTests' --logger 'trx;LogFileName=phase2b2-m2a.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m2a-offline.trx' --results-directory TestResults/home-validation
```

首次build含新增helper的restore；後續最終build使用no-restore。初次xUnit analyzer提醒Assert.Single filter寫法，已改用predicate overload；最終0warnings。沒有assertion failure或安全alert。TRX/discovery raw artifacts保留於ignored `TestResults/home-validation/phase2b2-m2a*`；[sanitized evidence](phase2b2-m2a-evidence.json)只摘取counts與必要process/race觀測，無user paths、DB或credentials。

## Migration與fault證據

- Fresh v2、v1 Created／Completed兩種snapshot遷移通過；v1snapshot刻意含非canonical whitespace，migration比較`hex(CAST(snapshot AS BLOB))`與version/terminal，前後完全不變。沒有重寫／reserialize原snapshot。
- 缺任一maintenance確認、活躍TaskId owner、exclusive schema gate、未知/較新/已是v2版本、未知schema objects／不符constraints layout、非空schema0、version/snapshot不一致均拒絕；normal v1 open沒有自動升級，normal Host不接受v2。
- Exception fault injection在MigrationValidated、TablesCreated、SchemaVersionWritten三個checkpoint，結果均rollback DDL/user_version且原v1仍可讀。Fresh DB建表fault後回schema0且無業務tables，明確maintenance可重試成功。
- PendingInserted、DecisionWritten、ApprovalConsumed、ExecutionInserted、CancellationWritten的exception injection均無半套business rows/audit；consume失敗不留Consumed但缺execution，cancel失敗不留cancel flag或部分revocation。

最終focused run的真實OS process證據（UTC start time見JSON）：

| 情境 | 實際PID | 觀察結果 |
|---|---|---|
| Pending commit後kill writer，新process讀取 | writer36348 → reader39364 | ApprovalId=`approval-one`、OperationId=`operation-one`、TaskId=`task-one`、ExecutionId=`execution-one`、Pending/version0保持；binding一致、integrity=ok |
| Migration建表後kill helper | 55176 | TablesCreated gate；新連線recovery後schema1、approval tables不存在、原snapshot bytes不變、integrity=ok |
| Migration寫schema version後、commit前kill helper | 25428 | SchemaVersionWritten gate；同樣rollback到schema1、snapshot bytes不變、integrity=ok |

Helper只包含migration/approval storage命令，無provider code。Driver等明確stdout gate、有deadline，檢查捕捉的Process object／StartTime，僅kill自己的helper，不殺process tree或shared server。這是process-level SQLite journal recovery；不是斷電／磁碟損壞實驗。

## Concurrency／授權資料證據

- Concurrent Approve/Reject：獨立store/connection在barrier後競爭，focused run由Reject勝出；只有一筆decision/audit、version1、execution0。相同DecisionId/內容安全重放，改choice／actor／binding或跨approval重用ID被拒絕。
- Concurrent consumption：只有一個winner、execution1、consumption audit1。Pending/Rejected/Expired/Revoked/Consumed均無法重新consume。
- Cancel/admission race：focused run cancel先勝出，controlVersion1、cancelId=`cancel`、execution0，批准Revoked；另外有兩個deterministic tests覆蓋admission先／cancel先。Admission先時可保存Created＋後來cancel intent，但不改寫execution為Cancelled；不存在provider dispatch。
- Stale CAS、duplicate Approval/Operation IDs、異scope／execution／provider binding、失去operator授權、過期（含deadline相等）、重複cancel、混合epoch/version與caller指定epoch均拒絕。相同policy revision換內容拒絕；trusted epoch2已pin後，即使consume被拒，回退epoch1仍拒絕。
- Metadata-only契約無prompt/transcript/credential欄位；synthetic credential-like context、非法payload ID與含marker的exception fault均未寫入DB/audit。這是結構／測試證據，不是對任意identifier中秘密的DLP保證。

## 維護程序與後續前提

[離線維護／備份／rollback程序](../setup/SQLITE_APPROVAL_MAINTENANCE.md)是實際遷移前必要操作規格。Maintenance三個flags是可信呼叫者attestation，不是自動驗證人類或證明舊Host不存在。舊binary不遵守新schema gate；operator必須真的停機、阻止重啟，核對deployment清單與一致性備份。未知schema拒絕，沒有自動改version或刪journal搶鎖。這輪只使用disposable synthetic DB，未遷移任何個人／公司真實DB。

仍未完成／未授權：

1. M2-B：production protected authority配置與activation序列化、可信operator identity與self-approval防護、audit retention/archive、遷移受信任入口與真正deployment停機/備份證據；同帳號SID不等於human presence。
2. M3：完整application/adapter enforcement、safe v2 Host/IExecutionStore runtime integration、explicit resume/input reentry、operator CLI與一次性admission continuation。M2-A的Created資料不是可執行ticket，不可拿它繞過未完成gate。
3. Provider取消dispatch、sent/ack/terminal ledger、permission bridge、live interoperability、Registry、M4或後續階段均未開始。
4. v2 audit硬上限會fail closed；不自動刪audit，滿額後需要另行授權維護。取消目前只有sticky intent/monotonic control version，不處理provider或撤銷遠端效果。
5. IApprovalStore讀取是受信任資料層API，尚無production讀取ACL/identity enforcement。單DB high-water不能抵禦整份舊backup回退或任意DB owner；外部protected authority的單一可信revision仍是部署前提。
6. 沒有重跑2B-1 controlled-provider/live runner；原資料層回歸案例通過不代替那些歷史live證據。公司Symantec事件／原驗收檔案未修改，公司clearance仍獨立。

提交並推送後停止；任何M2-B/M3或provider活動都需owner另行批准。
