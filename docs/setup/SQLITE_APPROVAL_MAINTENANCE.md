# SQLite approval/execution：離線 schema 維護與回復程序

最新受控增量為 [M4-B1b](../acceptance/PHASE2B2_M4_B1B_RESULTS.md) 的明確 v3→v4；操作限制見文末。以下 M2-A/M2-B 段落保留原版本背景，不代表正常 Host 可以自動升級或使用 v4。Production trust／maintenance CLI／Host external execution 仍未啟用。

狀態：M2-A資料層與M2-B授權邊界，**沒有production migration/approval CLI，也未接入Host**。任何實際資料庫作業都需要owner另行選定受信任維護工具／操作者與資料保管位置；本次只對disposable synthetic DB執行。

## 啟用邊界

`ApprovalSchema.InitializeOrMigrateAsync(path, OfflineMaintenance, token)`只接受三項皆為true：明確maintenance mode、所有old/new Hosts已停止、backup/rollback procedure已確認。這些是**受信任維護呼叫者的attestations，不是身分驗證或自動程序盤點證明**。不得由repository設定、任務payload或agent環境變數自行提供。M2-A未提供正式維護身份resolver；也不自動變更ACL、安全政策、profile或備份資料。

正常`SqliteApprovalStore`只用ReadWrite開既有schema v2，不建立／遷移DB；一般`SqliteExecutionStore`／Host仍只允許v1並拒絕v2。兩者共用同一DB的既有`executions`表格式，但v2 runtime integration刻意尚未開啟。將DB遷到v2**不會讓現有Host具有批准或執行權限**；不要把仍要供2B-1 Host使用的真DB當成M2-A試驗資料。

## 遷移前

1. 確認授權、精確absolute local DB路徑、受信任maintenance caller與維護時段。真實資料不可放repository/Temp/network/link；tests可直接使用隔離Temp synthetic DB。
2. 正常停止所有會讀寫此DB的Host/worker、operator工具與舊binary，禁止重新啟動；不是只停止目前shell，也不是process-name廣殺。確認自己管理的PID/StartTime與deployment清單。檢查既有TaskId ownership與連線皆釋放；舊Host不遵守新schema gate，不能由「目前沒鎖」推定它永遠不會寫。
3. 在沒有writer的情況確認v1 schema、`integrity_check=ok`、journal/checkpoint狀態。若存在hot journal／WAL未完成，先由授權SQLite程序完成正常recovery/clean close，不能只複製主DB後丟棄sidecars。
4. 依owner核准策略，在受保護的非repo位置建立**一致性**備份：可用經核准的SQLite Backup API，或確認完全clean/closed後的完整offline備份。保存原schema版本、file SHA256、必要sidecar manifest、備份位置與授權參考；不得把含source/IDs/control metadata的備份提交Git。驗證備份能以ReadOnly開啟且integrity為ok，記錄execution snapshot BLOB hash基線。
5. 全新、空且disposable的synthetic DB可記「無既有資料可備份」；仍須明確確認回復程序。測試fixture的確認不代表真實operator備份已完成。

## 明確執行

- 維護API取得`<db>.locks/.schema.lock`的FileShare.None，並持有所有已存在TaskId lock檔；任何現有owner未退出即失敗，不能刪檔搶鎖。
- 新版普通storage operations對同schema gate持共享lease；execution ownership建立時也先檢查gate。舊binary不參與此協定，因此operator停機仍是必要前提。
- SQLite使用Pooling=false、busy timeout5秒、foreign_keys、synchronous FULL、locking_mode EXCLUSIVE與**BEGIN EXCLUSIVE**；busy/permission/security failure停止，不能修改timeout/loader/path繞過。
- 只接受空schema0或精確已知v1 layout/constraints、columns與一致snapshot。未知／較新／已是v2版本、額外schema objects或非空未版本DB拒絕，沒有silent downgrade或「修復」猜測。
- 同一transaction建立authority/control/operation/approval/audit表與indexes，前後比對所有execution row的snapshot BLOB bytes及version/terminal；不reserialize或UPDATE舊snapshot。最後才user_version=2並commit。任何fault整體rollback。
- 不在DB transaction內進行HTTP、人類等待或provider execution。不將migration成功等同runtime enforcement啟用。

## 成功後

以新連線驗schema2、integrity、foreign keys、原snapshot byte基線、uniqueness與必要tables/indexes，保存sanitized結果。保留原一致性備份與維護記錄。**M2-A不啟動任何Host或provider**；只有經核准的資料層測試可以寫v2。正式policy authority、operator identity、Host v2整合與admission enforcement須後續里程碑批准。

## 失敗與回復

- Transaction失敗或maintenance process被終止：保留DB/journal/locks，不手動刪journal。由新SQLite連線執行正常journal recovery，確認schema仍1（fresh DB則0）、tables未半套、snapshot BLOB hashes不變、integrity=ok，再決定是否重新執行明確maintenance。
- 若schema已2，視為可能commit完成；不要直接重跑遷移、設回user_version=1或讓v1binary打開。先唯讀確認實際狀態與snapshot基線。
- 只有在未發生任何新v2 control寫入、且owner明確決定放棄遷移時，才於**所有程序停止**後依已核准備份程序恢復整個一致性備份；先保全失敗DB以便診斷。不得只覆寫主檔而留下不相符journal/WAL，也不對不明computed path做recursive delete。
- 一旦有新approval/control/authority高水位寫入，舊備份不再是可無損rollback來源。回復可能遺失revocation/cancellation/epoch資訊，需owner與protected authority對帳；禁止自動downgrade或讓舊批准重放。M2-A不提供這種災難回復自動化。

本機同DB的high-water與SQL constraint無法抵禦惡意DB owner或整份備份回退；外部protected authority仍須保存其可信monotonic revision，並在後續部署中與維護／activation序列化。Power-loss、磁碟損壞、mixed-version deployment與公司endpoint compatibility不由目前process-crash tests證明。

## M2-B：明確維護授權與 production disabled

未來維護工具應經 `AuthorizedApprovalMaintenance.InitializeOrMigrateAsync`：由 trusted composition 注入 `ITrustedApprovalMaintenanceAuthorization`，對精確 canonical absolute DB path 授權；缺授權、錯resource、明確拒絕或resolver故障均在開DB前失敗。未安裝production authorizer，沒有CLI；不得把agent可控制的bool/actor/環境變數包裝成實作。現有 `ApprovalSchema` 是trusted storage primitive而非authenticated入口，保留供測試與受信任maintenance工具；wrapper不是阻止惡意程式直接呼叫primitive的OS邊界。

Code 已驗證：schema互斥/SQLite EXCLUSIVE、版本與layout、atomic rollback、原snapshot bytes、integrity。Operational 程序仍須驗證：真正owner授權、所有舊/新Hosts停機且禁止重啟、一致備份及還原演練。`OfflineMaintenance`三項true不證明這些事實。禁止遷移現有個人/公司DB作測試；本輪只用synthetic fixtures，正式資料維護仍需另行批准。

Policy candidate publishing與durable activation不同：trusted acquisition lease先取得candidate，coordinator在同DB提交high-water+audit後才算啟用。Policy activation與admission的先後由SQLite寫入交易決定。Production source、human identity、writer isolation與外部rollback anchor未成立，故不啟用正式authority/approval。不得在maintenance restore時清除high-water、調低revision或繞過audit上限。

## Audit archive/retention 提案（disabled，未實作）

目前硬上限10000 rows、每頁讀100；滿額拒絕整筆mutation及需要audit的activation，不默默刪除。不得直接DELETE/VACUUM清額度或restore舊DB來繼續工作。

後續可審查單DB離線、固定sequence區間的受控archive，而非通用event store：

1. 由另行認證/授權的maintenance operator停所有writers，保存已驗證的一致性DB備份及當前policy/control/approval checkpoint；先保證未結案approval/consumed/取消/authority歷史仍可追溯。
2. 設定有限archive batch/保留期間；以完整連續sequence區間（含事件數、首尾sequence、authority epoch/version、schema/encoding、DB resource identity）輸出至受保護非repo儲存；保留必要歷史時窗，不能只留下最新Approved。
3. 建立可獨立驗證的manifest/digest與外部可信checkpoint，實測可讀、連續性與restore；hash本身不證明檔案未被同owner一起竄改。Source/control DB與archive的保管/權限及容量需owner批准，不由Agent改ACL。
4. 必須先設計durable archive receipt/外部anchor及crash recovery協定，再在短exclusive transaction驗證相同checkpoint、保存receipt與刪除已驗證區間；永不重設sequence或遺失pending/decision/consume/cancel/authority必要證據。未完成archive、驗證失敗、receipt缺失或race均拒絕刪除。
5. 回復需同時對帳live DB、archive與外部authority high-water，不能只restore舊DB或假造缺失audit。所有fault windows需synthetic測試與owner批准後才可能啟用。

現有schema v2沒有archive receipt/anchor，保管信任與retention期限未定，**本輪不提供archive/export/prune方法**。容量即將滿時應停止新control寫入並安排另行授權維護，不以降低證據要求解決。結果與M3前提見 [M2-B results](../acceptance/PHASE2B2_M2B_RESULTS.md)。

## M4-B1b：明確 offline v3→v4

[已接受契約](../plans/PHASE2B2_M4_B1A_V4_SCHEMA_CONTRACT.md) 的 v4 delta 已在 disposable synthetic SQLite 驗證：兩張窄用途表、execution profile 欄位、一個 explicit unique index、八個 trigger。沒有正式 migration CLI、production authorizer、Host 啟用或真實應用 DB 遷移；以下是供另行授權維護工具使用的契約，不是要求在個人／公司 DB 立即執行。

入口為 `AuthorizedApprovalMaintenance.MigrateV3ToV4Async(path, prerequisites, authorization)`，底層 `ApprovalSchema.MigrateV3ToV4Async` 同樣要求精確資源的可信 maintenance authorization 與三項停機／備份 attestations。缺 resolver、拒絕、錯路徑、source failure 均拒絕；attestation 不能取代實際停機與備份驗證。原 v1→v2、v2→v3 API 的意義不變，不自動串接遷移。

1. 依上方備份保管規則停止所有舊／新 Host、writer、受控 runtime 及工具，禁止重啟；盤點自己管理的 PID/StartTime。從精確已知 v3 和一致性備份開始，保留 journal/WAL 與必要 manifest，不單獨複製仍有 writer 的主檔。保存七張 v3 application tables、sqlite_sequence 的原值／storage type／NULL／serialized snapshot bytes 與 audit.cancel_request_id 基線；實體 DB file hash 在成功遷移後會改變，不能拿它代替 logical-byte 比對。
2. 維護取得 `<db>.locks/.schema.lock` exclusive handle，再以固定順序獨占既有 task lock 與未來 `<db>.delivery-locks/*.lock`。不刪鎖、不更改 ACL；linked path/lock 拒絕，持鎖或 busy 以有界失敗停止。舊 binary 不遵守 gate，不能由「目前沒鎖」推論無 writer。
3. ReadWrite existing DB、Pooling=false、foreign_keys=ON、synchronous FULL、busy timeout 5秒、SQLite EXCLUSIVE transaction。要求 user_version=3、exact known v3 manifest、integrity/FK/data/control/approval/cancellation/audit consistency。預期存在 kind3 operations；不能套用舊 v2→v3 的 kind3 拒絕或忽略 cancel_request_id 的 byte projection。
4. 已知 B0 historical-session-null／current-session-known 時間差只可保全為 **legacy unverified**，且其他 exact identity/binding/control/audit 必須一致；不是補造 provenance 或放行新 delivery。其他已知 Session 替換、損壞資料／額外物件／部分或未來版本均拒絕，不 repair。
5. 單一 transaction 加 `executions.session_receipt_required`（所有原行=0，不改 snapshot）、建立兩張空表/index/trigger、逐項驗證舊值 bytes/sequence 不變及新 manifest/integrity/FK，最後寫 user_version=4、commit。遷移不新增 receipt、delivery、consent、authority activation 或 audit row。新 admission/consume 必須明寫 profile=1，不能變更既有 profile。
6. 保持 schema gate，以 fresh connection 再驗 exact v4、原 bytes、integrity/FK。失敗保全 DB/sidecars，停止維護；不可為了通過驗證而重寫舊 evidence 或 snapshot。

| Store profile | 唯一可開的 schema |
|---|---|
| Execution LegacyV1（預設） | 舊有 0→1 初始化與精確 v1；拒絕 v2/v3/v4 |
| Execution ExplicitV2／approval ExplicitV2（approval 預設） | 精確 v2 |
| Execution／approval ExplicitV3 | 精確 v3 |
| Execution／approval ExplicitV4（明確 opt-in） | 精確 v4，不建庫／遷移 |

v4 profile0 保留歷史資料，不可再進入 Create／Submit dispatch。原 session binding 可獨立驗證的 legacy GET 仍須當前 Inspect 授權與既有 root/provider/session policy 驗證；不升級成 Verified。B0 類型 legacy mismatch 回 `LegacyCorrelationUnverified`，不做 provider GET／Abort。profile1 在 intent 後缺 receipt 是錯誤，不得降級成 legacy；普通 UpdateAsync 不可設定新 SessionId。Verified receipt 也不授權 Inspect／Abort；Quarantined 只允許結構性 control/evidence 讀取。

Crash 在 ALTER／receipt table／delivery table／最後版本寫入但未 commit 時，新程序應只看到完整 v3；commit 後只看到完整 v4。以 actual process termination + independent fresh reader 驗證，不以 exception 代替 crash。遺失 commit 回應先驗真實狀態；不盲目重跑、不降版、不手動刪 journal。

一旦有 v4 receipt/control/authority 等新寫入，舊 v3 備份不再是無損 rollback 來源：回復可能遺失取消意圖、原 session 關聯或 high-water，必須另行 owner/protected-authority 對帳。禁止自動 restore、降低 epoch、重建 registration capability 或重送 Create/Prompt。v4 收據與 delivery 的 audit FK 需要保留原 journal；現有10000硬上限仍有效，沒有 prune/archive/reset 例外。Ack 不等於 Cancelled；本增量沒有 Abort reservation/transport continuation、POST 或 worker。
