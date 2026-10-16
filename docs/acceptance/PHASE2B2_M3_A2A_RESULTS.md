# Phase 2B-2 M3-A2a：Explicit-only durable policy activation

日期：2026-10-16（Asia/Taipei）。開發基準 `3c397f31c061f50c6551feff74b7acd0882eebe1`；fetch 後 origin/main 相同。Tracked tree 起始乾淨，既有未追蹤 `.vscode/` 保留。Owner 採納 D02，本輪只做 M3-A2a，沒有 Atomic Admission 或 Runtime Enforcement 實作。

## 實作與理由

- [SqliteApprovalStore](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs)：移除普通業務操作可寫入的 PinAuthority。`Candidate` 只取得 lease 內的 verified snapshot，驗 canonical digest/shape/有效期；`CompareActive` 在 SQLite writer transaction 比對 durable authority/epoch/version/digest；`CheckActive` 在 business action 前後驗證來源未改、且與 active tuple 完全一致。
- `ActivatePolicyAsync` 是**唯一寫入 policy_authority 的方法**。它沒有 caller snapshot/revision 參數，只從注入的 protected authority 取得 candidate；初次啟用或嚴格新版時，revision + AuthorityAdvanced audit 同一 transaction commit。相同 tuple 是 idempotent no-op，不新增 audit。沒有 production activation CLI、控制服務、Broker 或 authenticated operator integration。
- [Core contract](../../src/MAF.Core/Policy/ApprovalPersistence.cs) 與 [TrustedPolicyAuthority](../../src/MAF.Core/Policy/TrustedAuthority.cs) 只補清楚語意：Acquire/GetSnapshot 暴露的是 candidate，不是 durable active 證據；source acquisition 在 transaction 外，lease 內 GetSnapshot/授權 callbacks 必須 local。Authority adapter 的來源／digest／epoch/version／clock validation 行為未改，沒有新增 trusted source 或 fallback。

Request / Decide / Consume / Revoke / RequestCancellation 都維持原 authority lease、身份/權限、exact binding、CAS、control 與 audit 邏輯，但只驗 active revision，**不再先開獨立 transaction 提升高水位**。資料唯讀 API 與純減權 Expire 保留原行為；它們也不會啟用 policy。Schema 仍v2，table/column/index/enum/snapshot格式與 fingerprint/digest encoding 全部不變。

## 明確生效規則

| 來源與 durable active 狀態 | 業務 mutation | 明確 ActivatePolicyAsync |
|---|---|---|
| 只有來源發布／Acquire成功，DB尚無active | PolicyNotActivated；不建立authority、approval、control或audit | 驗證後原子建立active+audit |
| 完全相同 authority/epoch/version/digest，且來源fresh有效 | 繼續原有業務授權/CAS；不代表一定獲准 | Idempotent no-op，不新增audit |
| Candidate 的 epoch/version 兩者都高於active | PolicyActivationRequired；active與業務資料不變 | 明確啟用新版+audit |
| 任一revision低於active | AuthorityRollback | 拒絕，無降級／自動回復 |
| 同revision不同digest，或只升epoch/version其中一項 | AuthorityRevisionConflict | 拒絕，無自動修補 |
| Authority不符、來源缺失/故障、snapshot失效 | 拒絕；不以DB高水位重建舊policy | 拒絕；不寫active |

**Publication ≠ activation；candidate取得/驗證成功也不等於activation。** 新版尚未啟用時，甚至原active對應的舊operation也會因來源candidate不一致被擋住；選擇暫停而非cache fallback。這個 fail-closed 協定不需要新增「取回舊active snapshot」的信任協定。若未來要求staged publication期間繼續用舊active，必須另審可驗證active-snapshot/revocation protocol；本輪不提供替代或降級路徑。

Source authority 的 in-memory highest observed candidate 仍保持單調，不因業務拒絕而退回。**Durable高水位現在只記已明確commit的activation**，不記每個尚未啟用的candidate。新instance依同DB active tuple拒絕rollback，但不能據此偵測所有「未啟用candidate的發布歷史」回退；publisher的外部持久anchor仍是production trust前提，沒有以備份回退或重建instance繞過已commit高水位的自動化。

## Atomicity、race 與失敗

取得candidate/proof在transaction外，authority lease涵蓋完整mutation/activation commit；transaction內只讀local snapshot與local authorization evidence，不新增network IO。所有相關transactions沿用schema shared gate、Pooling=false、5秒SQLite busy timeout、synchronous FULL、IMMEDIATE writer交易。

- 同authority instance：lease串行business與activation，後者不能在前者commit前換snapshot。
- 不同instances、同DB：SQLite writer決定先後。Business先取得writer、驗active並commit後，activation才可提升；activation先commit時，舊candidate business在取得writer後拒絕。沒有單獨「讀active→釋放→再寫business」的窗口。
- 兩個activation相同tuple競爭：一筆新activation audit，另一個idempotent成功；同revision不同digest競爭：只有一個winner，另一個拒絕。已commit高revision不被較舊來源覆蓋。
- Activation在revision/audit寫入後、commit前注入failure：整筆rollback；initial activation仍無active，upgrade仍保留原tuple/audit。只允許明確retry，不讓下一business代為啟用。
- Audit滿額：新activation與高水位一起rollback，原security evidence保留；沒有prune或自動擴容。已active相同tuple的idempotent no-op不需新audit。
- Source失聯/撤回candidate、active digest損壞、policy變更或operator權撤銷：不授予consume；原approval/control/execution資料保持。沒有provider dispatch，亦未聲稱此輪涵蓋commit-to-HTTP race。

## Tests 與實際結果

測試僅使用GUID隔離的synthetic DB、固定clock、test-owned trusted source/identities；沒有正式DB遷移或修改。Full offline包含原有fake-provider及disposable migration/process regression，沒有執行live model、實際provider session或2B-1 controlled/live acceptance runner。未觀察到endpoint-security alert。

| 檢查 | 最終實際結果 |
|---|---|
| Solution build Debug/no-restore | 9 projects；**0 warnings / 0 errors** |
| Focused discovery | **118 entries**（Authority67 + ApprovalPersistence51） |
| Focused run | **118 executed / 118 passed / 0 failed / 0 skipped** |
| Full offline | **433 executed / 433 passed / 0 failed / 0 skipped** |
| 與上一輪比較 | 原M3-A1的409全部通過 + 新24 = 433；沒有將focused重複相加 |
| 歷史分組 | 原155 + M1的98 + M2-A的51 + M2-B原43 + M3-A1的62 + 新24 = 433 |

新增24 cases在 [TrustedAuthorityTests](../../tests/MAF.Tests/TrustedAuthorityTests.cs)：初次explicit/idempotent activation；5種業務operation × 無active/較新未啟用candidate（10）；source缺失/故障（2）；same revision digest/單revision/rollback/損壞active（5）；initial/upgrade activation fault（2）；相同/衝突candidate concurrent activation（2）；activation先commit拒絕stale business（1）；新instance restart不promote candidate（1）。既有測試繼續驗operator撤權後不能consume、schema/byte-preservation、CAS、取消競爭與audit容量。

### 只因 implicit activation 而調整的舊測試

| 調整 | 原期待／新期待與保留的安全斷言 |
|---|---|
| ApprovalPersistenceTests.Setup、TrustedAuthorityTests.Setup | 原先第一筆業務mutation自動pin；現在fixture建立DB後明確activate。後者可選activate:false驗尚無active。沒有production註冊或fallback |
| MAF.ApprovalAcceptance seed helper | 寫Pending前顯式activate，仍使用原driver-owned PID／checkpoint；跨process Pending identity/integrity斷言不改 |
| AuthorityHighWaterSurvivesRejectedOperationAndRejectsRollback | 發布v2後先明確activate，再驗失敗consume不撤回v2、舊來源不能rollback；原Approved/零execution安全斷言保留 |
| FailedBusinessMutationCannotRollbackActivation | 明確activate v2再觸發失敗business，仍驗active保持v2；新增tests另驗business本身不能activate v2 |
| AtomicMutationFaultDoesNotLeavePartialState | 原首次Request fault可額外留下1筆authority audit；現在authority已由setup明確commit，failure後audit必須與before**完全相同**。這是移除隱含寫入後的更嚴格斷言，不是放寬CAS/rollback |

沒有修改原Workflow/provider/recovery/security assertions。Ignored evidence：`TestResults/home-validation/phase2b2-m3-a2a-focused.trx`、`phase2b2-m3-a2a-offline.trx`；已用TRX counters/class grouping核對，未提交raw路徑或任意outputs。

```powershell
dotnet build MAF.slnx --no-restore --configuration Debug
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~MAF.Tests.TrustedAuthorityTests|FullyQualifiedName~MAF.Tests.ApprovalPersistenceTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~MAF.Tests.TrustedAuthorityTests|FullyQualifiedName~MAF.Tests.ApprovalPersistenceTests' --logger 'trx;LogFileName=phase2b2-m3-a2a-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m3-a2a-offline.trx' --results-directory TestResults/home-validation
```

## Limitations、production status 與 M3-A2b 前提

Activation仍是注入protected authority後由trusted code呼叫的資料primitive，**不是authenticated production control**。沒有新增operator authentication、activation CLI、Broker、服務或Host DI。Default UnavailableTrust、same-account human/agent區分不足、writer隔離與external rollback anchor限制不變。直接惡意改DB的owner仍不受SQLite digest保護；損壞fixture測試只能證拒絕，不是tamper-proof認證。

Scope只完成D02。Host、M3-A1 assessment、Workflow、Provider APIs、execution submission、SqliteExecutionStore、schema/migration、execution snapshots/IDs、canonical encodings皆未改。M3-0所列runtime bypass仍存在；沒有Allow admission、新owner continuation、provider effect guard、human approval/resume或cancel dispatch。Expire保留純減權語意，read APIs仍是trusted data primitives，這輪不新增其application授權。

下一個**需另授權**的M3-A2b應收斂到Atomic Admission：沿用本輪active-tuple驗證，把current policy/control/唯一Created/audit與approval consumption等必要資料操作放入固定短transaction，定義TaskId ownership與committing owner continuation；釐清explicit v2 read/update相容性，不自動migration、不讓existing execution resubmit。Runtime Enforcement留M3-A2c，Approval/Resume留M3-B；Cancellation/Reconciliation必要驗證再往後，不在本輪擴張trust infrastructure。

**M3-A2a已實作並通過118 focused／433 offline與solution build。選定的strict fail-closed activation語意沒有阻擋本輪交付的blocker；production trust仍Blocked。提交推送後停止，不自動進M3-A2b/c/B或正式activation。**
