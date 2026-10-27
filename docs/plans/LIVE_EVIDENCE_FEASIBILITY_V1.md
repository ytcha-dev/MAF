# Live Evidence Feasibility & ProtectedHandoff Reliability

日期：2026-10-27。審查 baseline `0be3fda` 及本輪 V1 pin/fake preflight。
**Decision：現有機制多數可重用，但 PersonalLab offline journal/receipt contract 不可原樣升為 genuine Live Evidence。Live 仍 Blocked。** 本文件不實作 schema/migration、Network grant 或 ProductionProtected 變更。

## 1. 逐層可重用性與精確 blocker

| Component / symbol | 可保留的 invariant | 不能代表的 Live Evidence |
| --- | --- | --- |
| `LocalExecutionPreparation` / `LabProvenanceIdentity` | operation/task/run/execution/request、workspace、ReadOnly、三個 recipient bindings、budgets 固定；storage 在 workspace 外 | metadata/config 描述不是批准或 observed egress；`DemandExecution` 與 `DemandHarnessRecipient` 目前仍拒絕 |
| `PersonalLabAdmission.AdmitOffline/Reserve` | one operation、一個 Create/Prompt attempt、expiration/revocation/cancellation、不退款、bounded counters | Target 只接受 ControlledOfflineFixture，Network 明確拒絕；輸入 enum 不是 network authorization。不能改成接受所有 Network 來解鎖 Alpha |
| `PersonalLabOfflineOperation.Begin/Write/Reserve` | CreateNew/exclusive、WriteThrough、intent flush-to-disk 在 wire 前、失敗 revoke、exact identity fingerprint | Header v1 的 identity/fingerprint 沒有 durable effect target、live/fixture discriminator、expected V1 pin 或 observed process/transport/config custody；same-principal file 不是 human consent / authenticity |
| `LabOpenCodeBoundary` / original receipt sequence | 原 Create response 候選 → correlation → receipt 順序、policy/directory echo、Prompt 前 flush、原 session/request 不置換 | event 只存 session/kind 等欄位；沒有把 owner、transport exchange identity、binary/config evidence 寫入 receipt。相同字串或之後 GET 不足以證明原 live response custody |
| `LabProvenanceInspection` | reopening inspection-only、CanResume=false、Unknown/no replacement、舊 event sequencing 嚴格檢查 | `Receipt` 固定為 FixtureVerifiedOriginalResponse / NotProductionAuthenticity；`Provenance` 固定 ControlledOfflineFixture / NotLiveProvider。header reader 要求 Version=1，反序列化拒絕未知欄位。不能只改 presenter 或推測所有 v1 都是 live |
| `ReceiptTransport` / `IOriginalCreateAccess` / protected v4 receipt | 既有 transport/client/request/response object binding、once-only original response finalization、durable intent/response | object identity 是該 transport 的 fidelity，不是 caller authentication、socket peer、下游 endpoint 或 config attestation。ProtectedExternalRuntime 還要求真正 current authority/approval/activation；PersonalLab 不可借 synthetic protected fixtures 或繞過該 trust |
| `BoundNativeRecipient.RecipientGuard` | PCA/Reviewer 分離、exact HTTPS destination/model、no proxy/redirect/cookie、SDK RetryPolicy(0)、budget reservation | `CreateDisabled` 仍走 Network denial；模型 label 與回應 model 不證明 provider routing、真實 egress、cost 或 server-side fallback |
| `LabOpenCodeExecutionPlan` | expected downstream model/endpoint/config digest 與 None-only flows、綁定原 preparation | supplied digest 不是 observed effective config/custody，也沒進現有 journal header 的 live custody envelope；其 ToString 仍 NotRuntimeEvidence |
| `OwnedOpenCodePreflight.ForTests` | expected/observed fake binary/origin/owner pairing、bounded GET-only discovery、R1/stop/cleanup retention | fake object reference 不是 kernel port identity、簽章、binary hash acquisition 或 effective config proof；不能讓 PreparedOffline 解鎖 live admission |

結論是 **representation gap + evidence acquisition gap + authorization gap**，不是缺另一套 orchestration framework。現有原 identity、no-retry、source verification、workflow/Reviewer、durable ordering 都應保留。

## 2. 最小必要改變（提案，未實作）

只有放寬 Network 或把 FixtureVerifiedOriginalResponse 改名會造成不實 evidence。若要用現有檔案型 Lab journal 表示一個真實 personal operation，至少需要一個 **explicit opt-in 的新 journal header/profile version**；舊 v1 不回填、不升級、不獲授權。

最小新欄位語意：

1. **execution evidence profile / effect target**：明確區分 ControlledOfflineFixture 與 Owner-approved PersonalLive observation；是來源分類，不是 PolicyDecision 或 production authenticity。
2. **operation-bound custody binding**：原 operation/task/run/execution/request + pinned `1.19.2`、approved executable identity、原 child/transport-origin binding、expected config/recipient digest 與 observed custody evidence 的固定描述/reference。需持久化可重查的 manifest，不是只存一個無法驗證來源的 hash 或 Attested=true。
3. **original-response receipt binding**：在原 response 的一次性 capture/finalization 同時固定上述 custody reference 和 original session/policy correlation；缺 capture、缺 custody 或 flush 失敗就不能 Prompt。GET/健康/version match 不能補票。

可重用既有 journal 的 flush/sequence/size bounds；只把新 profile、immutable custody envelope/reference 納入 fingerprint 與 strict reader/receipt validation。這應是 Host 的窄幅檔案契約 extension，不新增 DB、persistence project、通用 evidence registry、Broker 或 provider framework。**本輪未定版/未實作 extension 或 migration。** 新 reader 要能把 v1 永遠呈現 offline；舊 reader 對未知版本維持拒絕。新 live profile 也不得宣稱 ProductionProtected authenticity。

即使新格式可表示 LiveOriginalResponseObserved，仍須誠實標示 downstream custody 是否 established；如果來源與 enforcement 不能證明 approved recipients/configuration，就繼續 Blocked，不能以「已記錄」取代「已驗證」。Owner 須明確接受個人 cooperative boundary 的限制；若要求強制 egress/custody 保證，這個小增量做不到，必須回報該部署 blocker。

Network admission 與三個資料流仍需分開核准，不能由新 header 或 preflight 回傳值產生。ProductionProtected v4 的 authority/receipt/trust 完全不修改。相同模式禁止 auto-resume、第二次 Create/Prompt、ambiguous retry、manual copy/paste transcript 變成 verified evidence。

預估：在 Owner 已核准欄位/證據來源且可取得 custody 的前提下，1–2 工程日完成小型 writer/reader/receipt binding extension 與 targeted negative tests；再另安排 dedicated process/socket 驗證與單任務 composition。不承諾只改格式即可開始 live，也不為此先做通用平台。

## 3. 三個既有 ProtectedHandoff failure 的調查

原始證據見 [Alpha validation](../acceptance/PERSONAL_ALPHA_VALIDATION_V1_PILOT_RESULTS.md)：725 scoped tests 中 3 個 assertion failures；一次未改碼 diagnostic 9/9。舊失敗未刪除、不改為 pass。

本輪只在 `FullNestedOfficialGraphUsesRealV4AdmissionReceiptAndEffects` 和 `WrongTrustedHandoffMappingCannotRewriteOriginalExecutionOrReachReviewer` 加入既有 OfflineCheckpointTiming scope／原 run outcome 記錄；第三方法原已有相同 timing。沒有更動 assertion、2s timeout、budgets、授權、receipt、HTTP behavior、OS fixture 或 scheduling 設定。一次 focused run 選三個方法的全部 variants：**9 passed / 0 failed / 0 skipped，17s**；沒有再跑第二次追綠。

| 觀察 | 舊 failure | 本輪 timing / 判定 |
| --- | --- | --- |
| Full nested graph | ReadyForHumanReview 變 NeedsHumanReview；2.66s | 這次 Completed / ReadyForHumanReview，status GET 約 budget +1914.36ms 才開始，距 2s 僅約85.6ms。顯示 margin 很小，不是穩定性已解；cleanup/final observation elapsed 可超過 admission budget，不能只看總 elapsed 就判定未授權 HTTP |
| Wrong mapping / execution | expected Reviewer calls 1，actual 0 | assertion 要求的是先前 original run 已完成 Reviewer；這次 original run Completed、Reviewer calls=1，original child return 約991.63ms；後面的錯誤 mapping 仍被拒絕、無新增 POST。舊 run 缺前段 timing，不能斷言原因已證實 |
| In-flight timeout | 到 2s 前未進 status gate；Unknown / ObservationTimedOut，Create/Prompt/GET/Abort=1/1/4/0 | 這次在 +829.51ms 進 status gate，故能測到預期的持有/timeout 行為；gate 刻意保持至 +2008.45ms cancellation，最後 Unknown 為預期，不是該 case 再失敗 |

fixture/scheduler sensitivity 有實際 margin/不同進度支撐，但 stopwatch 不能分辨磁碟 flush、SQLite、JIT、CPU scheduling 或背景負載的各自因果；更不能推論 EDR 誤報。狀態分類：

- **產品 correctness defect：未在本輪重現**。原 timeout trace 是保守 Unknown、沒有 retry/Abort；沒有 evidence 支持放寬 guard。
- **Fixture/scheduler sensitivity：有支持證據，尚未完成根因定位**。同一2s budget 在不同 run 的 progress 差距明顯；mapping case 的 prerequisite 沒完成會連帶使其 Reviewer count assertion 失敗。
- **Environment effects：可能，但具體來源 Unknown**。未修改安全設定、未強停背景程序、未替換路徑或 executable。
- **剩餘 Unknown：原 Full/mapping failure 的具體停點，以及長期/冷啟動重現率**。本輪9/9不能抹除歷史失敗，也不是 full-regression reliability acceptance。

下一次若再遇到失敗，只保留新 timing 與原 outcome，比對每個 wait 的 budget progress；先定位 prerequisite/IO/scheduler，而非提高 timeout、弱化 expected status 或反覆 rerun。這不阻礙1.19.2 fake pin/preflight的已驗證交付，但 pilot checklist 仍須保留 reliability 風險供 Owner 審核。

Timing artifact（ignored）：`tests/MAF.Tests/TestResults/v1-preflight/protected-timing.trx`。本輪完整 command 沿用單一 test thread、180000ms session bound，filter 為三個指定方法，無 OS-process cases：

```text
FullyQualifiedName~ProtectedHandoffTests.FullNestedOfficialGraphUsesRealV4AdmissionReceiptAndEffects|FullyQualifiedName~ProtectedHandoffTests.WrongTrustedHandoffMappingCannotRewriteOriginalExecutionOrReachReviewer|FullyQualifiedName~ProtectedHandoffTests.InFlightObservationPreservesChildOwnershipAndNeverAborts
```

## 4. First real single-task pilot 的剩餘 blockers / 產品順序

1. Owner 核准唯一 personal workspace/task、PCA/Harness/Reviewer 三 recipients/endpoints/models、資料流與費用限制；目前沒有值或 live grant。
2. 完成上述最小 live evidence profile/custody binding 決策與可取得的證據來源；不要在無法證明的狀態先開 Network。
3. 另行核准並實作 dedicated native 1.19.2 executable/port/transport/config custody。這次只驗證 fake protocol，沒有 kernel socket/occupied-port race、binary approval 或真實 server health。
4. 同一既有 Workflow 自動交接真正 Harness result 到 Reviewer；維持一次 Create/Prompt、intent-before-wire、receipt-before-Prompt、Unknown/no-retry、WaitingForApproval safe stop、drain/owner retention。
5. 使用既有 [A/B product checklist](PERSONAL_ALPHA_V1_FIRST_LIVE_PILOT.md) 量測零 Harness→Reviewer 手動搬運、較少 app switches/主動協調時間與不降級的 evidence；本輪沒有填造真實使用者價值。

**建議下一個可交付 increment：只實作已審核的單一 PersonalLab Live Evidence profile/receipt binding extension，以 fake negative cases 驗證舊 offline 永不升級、custody 缺失不 dispatch；之後才在另外授權的 scope 接 dedicated 1.19.2 preflight 到原 Workflow。** 不再研究 V2、不新增 Web/TUI/Registry/Orchestrator；能證明 custody 才接通一個真實任務。
