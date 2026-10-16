# Phase 2B-2 驗收計畫：Policy／Approval／Cancellation

日期：2026-10-16。狀態：**分層驗收計畫；M1的U層與M2-A資料層／process子集已有增量結果，完整Phase 2B-2尚未通過。** 原計畫基準`64d3c36`。[ADR-005](../architecture/ADR-005_POLICY_APPROVAL_CANCELLATION.md)定義預期契約，[execution plan](../plans/PHASE2B2_EXECUTION_PLAN.md)定義里程碑。歷史2B-1證據不代替本階段結果，亦不解除[公司安全hold](PHASE2B1_SECURITY_EVENT.md)。

後續增量結果：[M1 results](PHASE2B2_M1_RESULTS.md)記錄獨立evaluator／binding的98個runtime cases及253個offline regression結果；只涵蓋下列部分U層行為，不是各列全部U/S/C/L或G0–G5通過。原矩陣為準備時的Planned標記，未完成能力維持未驗證；沒有啟動M2。

再後續增量：[M2-A results](PHASE2B2_M2A_RESULTS.md)記錄51個新persistence/process cases與304個offline全通過，涵蓋migration、durable pending、CAS/decision/consume/cancel races與integrity的適用資料層子集；不是M3身份、live enforcement、provider cancellation或整列G2/G3完成。原Planned矩陣仍是完整target，不以fake身份當production authentication。

M2-B增量：[results](PHASE2B2_M2B_RESULTS.md)記錄43新authority/identity/maintenance contract cases及347 offline全通過。Production source provenance、真正human authentication與Broker restart未示範；功能保持disabled，沒有因此完成runtime G gates或完整2B-2。

## 1. 測試分層與通用oracle

| 層 | Fixture／觀測 | 模型需求 |
|---|---|---|
| U：deterministic unit | fake clock、受信任identity context、固定policy epoch／capability descriptor、table-driven state/digest tests | 無 |
| S：SQLite integration | 新synthetic DB、real stores/UoW、schema v1/v2 fixtures、獨立process與ReadOnly integrity observer | 無 |
| C：controlled provider／Host | 專用Host child、test-owned loopback provider、checkpoint gates、independent POST counters、受控fake可信身分入口 | 無；test identity不能流入production fallback |
| L：optional authorized live | 隔離synthetic Git repo、dedicated OpenCode、明確model授權、可靠method/path/count accounting與manifest | 僅必要interop；多數policy案例不依賴真模型 |

每例的oracle包含：輸入binding/policy epoch／操作actor scope、expected decision與error code、row versions與transaction結果、原/新PID與StartTime（跨程序）、original/recovered TaskId/Run/Execution/Session/Message/Approval/Operation/Cancel IDs、provider session/prompt/abort/reply counter增量。失敗/blocked/skipped分開記錄，不預填Passed、不把重複跑的案例相加。

核心計數：未批准／Deny／過期／revoke／replay／input不符→session/prompt POST增量0；`approve`→所有provider POST增量0。合法新任務完整流程最多session1/prompt1；其後resume/recover增量0。取消不能增加session/prompt；首次必要abort最多1，uncertain後預設不重送；MVP permission/question reply始終0。

## 2. 決策與授權矩陣

下列均為 **Planned**；編號可作未來test/evidence標籤，不代表現有test方法。

| ID | 層 | 情境 | 必要結果／oracle |
|---|---|---|---|
| P01 | U,C | 受信任caller、已知readonly capability、目前Allow | 一次精確admission；provider safety checks仍執行 |
| P02 | U,C | explicit Deny／未配置policy／evaluator exception | Deny、零POST；不fallback ephemeral或別connector |
| P03 | U,S,C | RequireApproval | 原子Pending+operation+audit，沒有executionrow與session/prompt；CLI可停止 |
| P04 | U,C | unknown capability/operation/enum、descriptor缺失 | default Deny，錯誤可觀測；不讓未知值變RequireApproval後強行approve |
| P05 | U,C | enabled+healthy connector，但caller/resource未授權 | Deny；enabled不授權，health不替代policy |
| P06 | U,C | write/shell、scope elevation、unsupported provider challenge reply | Deny；人工Approve也不override，底層readonly rules保持原樣 |
| P07 | U | binding canonicalization、case/path／budget／encoding差異 | 規範允許的同scope同digest；不同operation/caller/connector/request必不同；原2B-1fingerprint不變 |
| P08 | U,S,C | policy由Allow/RequireApproval變新版Deny | consume/dispatch被新epoch拒絕；舊grant不能override |
| P09 | U,S | epoch變寬鬆、config/descriptor revision變化 | 舊approved仍Superseded，需新evaluation/generation，不偷沿用 |
| P10 | C | ephemeral CLI、direct RunAsync、RecoverAsync、batch、resume、abort入口 | 每條入口有正／負向policy test；無public bypass／缺policy預設Allow |
| P11 | U,C | 新提交被Deny但授權inspect/cancel存在 | 可按獨立capability對帳／取消原session；不能因此新提交 |
| P12 | U,C | forged --actor、環境username、repo policy、模型文字 | 不能冒充trusted operator；缺resolver直接拒絕 |

## 3. Durable approval／競爭矩陣

| ID | 層 | 情境 | 必要結果／oracle |
|---|---|---|---|
| A01 | S,C | Pending commit後kill Host、啟新PID | 相同ApprovalId/OperationId/binding/expiry；仍Pending，零autoapprove/POST |
| A02 | S,C | Approve decision commit後、CLI response前kill | 相同DecisionId重放回原decision，不dispatch、不產生第二decision |
| A03 | U,S,C | Reject | Rejected永久不允許該generation；resume拒絕、零POST |
| A04 | U,S,C | Pending/Approved expiry、邊界相等、clock回撥 | deadline時即不可consume；clock異常fail closed，無需真sleep |
| A05 | S,C | explicit revoke在consume前 | Revoked／CAS版本上升，resume拒絕；intent已dispatch則回TooLate並區分cancel |
| A06 | U,S,C | Approval跨TaskId/ExecutionId/MessageId/operation/connector/caller重放 | exact binding mismatch，零POST；不能重綁不可變identity |
| A07 | U,S,C | 同TaskId變root/agent/provider scope/policy rules/request timeout | fingerprint/scope mismatch；舊批准無效，不overwrite新scope |
| A08 | S,C | 同DecisionId同內容／同ID不同內容 | 前者idempotent；後者拒絕；audit不偽造第二個人類操作 |
| A09 | S,C | 同時Approve與Reject、Approve與Revoke、多Host consume | barrier同步，CAS只有合法winner；無last-write-wins；最多一次Created/remote submit |
| A10 | S,C | consume transaction中每個SQL邊界fault | approval consumption與execution insert/audit全commit或全rollback，無半套grant |
| A11 | S,C | consume+Created commit後立即kill、尚未session POST | 恢復Unknown/missing evidence；grant不能再用，不再admit；session/prompt增量0 |
| A12 | C | session/prompt POST不確定後approve/resume | GET原session，無替代session與第二prompt；缺session保持Unknown |
| A13 | U,S,C | Restart已丟prompt | approve只保存；resume InputRequired，保留未expiredgrant，不用placeholder啟動 |
| A14 | C | 重新供給相同input／不同input | 前者重新evaluation後唯一admission；後者fingerprint拒絕；DB無prompt/secretmarker |
| A15 | S,C | 批准後原execution已Completed/Cancelled/Failed/Partial | 關閉待action，保留terminal；禁止重新submit、不能把terminal改Waiting |
| A16 | C | 核准後provider unavailable、running或permission pending | 分別Unknown／authorized observation／ProviderInputPending；無自動tool reply |
| A17 | U,C | operator subject正確但resource/required scope不足 | decision拒絕；list權限不能用來approve |
| A18 | C | agent與human同OS principal／self-approval | 依核准trust profile拒絕或功能disabled；username/SID/terminal確認不被當human presence證明 |
| A19 | S,C | cancel意圖與approval consume競爭 | controlVersion/stopGeneration先commit則consume失敗；consume先commit則cancel按已admit流程；不得duplicate submit |

## 4. Cancellation／reconciliation矩陣

| ID | 層 | 情境 | 必要結果／oracle |
|---|---|---|---|
| K01 | S,C | Pending approval時cancel | durable取消本地operation、invalidategrant；無remote Cancelled假證據，所有POST0 |
| K02 | C | Created、session creation intent、sessionknown/prompt前cancel | owner讀control阻止下一步；有不確定creation則Unknown；不新建session以便abort |
| K03 | C | running owner仍持file lock，另一CLI cancel | cancel intent能短CAS commit，不等整輪lock；owner bounded poll觀測後處理 |
| K04 | S,C | cancel intent保存後kill owner | 新owner讀原intent、相同handle，不能丟意圖或重送prompt |
| K05 | C | explicit cancel在prompt被接受後 | Requested→dispatch evidence→原session對帳；僅首次必要abort≤1 |
| K06 | C | HTTP abort ack=true，但status仍busy | Acknowledged但不是Cancelled；execution Running/Unknown；不approve等待 |
| K07 | C | abort ack=true，但permission/question pending | ProviderInputPending與cancel evidence分欄；reply count0，不能Cancelled |
| K08 | C | adapter有sent evidence、ack=false／no terminal | 存Sent或ResponseReceived(false)的真實資訊；不填Acknowledged/Cancelled |
| K09 | C | provider unavailable於abort前／後；response lost | intent保留、delivery/outcome Unknown；新Host先GET，無自動abort replay或prompt submit |
| K10 | C | DispatchIntended commit後、HTTP前/ack保存前kill | 存在MayHaveBeenSent窗口；不虛構sent/ack，restart GET-only |
| K11 | C | correlated provider terminal cancellation | 才允許execution Cancelled；foreign session/message error不得作證 |
| K12 | S,C | completion與cancel競爭，兩種commit順序 | Completed證據不改Cancelled；TooLate/requestedaudit保留；單一commandowner |
| K13 | U,C | observation timeout、HTTP timeout、inspect token取消 | 無新durable cancel intent、不自動abort；沿用2B-1 Unknown |
| K14 | S,C | explicit Ctrl+C時原CTS已cancel | 獨立budget保存intent與abort/觀測；無法保存時回truthful failure、不宣稱已取消 |
| K15 | S,C | 重複cancel、多個cancel CLI與active owner | 同execution同CancelRequestId或idempotent等價回應；不平行abort |
| K16 | S,C | legacy AbortRequested=true、無ledger | LegacyAbortRequested/DeliveryUnknown，不能合成sent/ack；terminal保持原狀 |
| K17 | C | 新policy撤銷cancel、scope變化、terminal後provider矛盾 | 不越權dispatch；獨立inspection權限仍需驗證；保留terminal，當次觀測Unknown |
| K18 | C | cancel晚於dispatch-intent但早於wire send | 誠實記race；允許已admitted request可能送出再abort，不能承諾按cancel後絕未執行 |

## 5. SQLite／migration與安全回歸矩陣

| ID | 層 | 情境 | 必要結果／oracle |
|---|---|---|---|
| S01 | S | 新DB建立v2；v1非終態/終態fixture遷移 | 新表/索引/FK/schema一次commit；舊executionIDs、snapshot bytes/enum/versions不改，無補造批准 |
| S02 | S | migration各DDL/user_version/commit窗口fault | rollback保持v1可讀；不留v2標號＋缺表；未知版本fail closed |
| S03 | S,C | v1binary開v2、mixed writer、migration busy | 舊binary明確拒絕；maintenance必須停Host；5秒busy timeout不靠改鎖策略掩蓋 |
| S04 | S,C | SQLite跨process Pending/Approved/Consumed/CancelRequested/AbortIntent持久化 | 原PID死亡、新PID同DB與version/correlation；ReadOnly integrity_check=ok、唯一約束與快照一致 |
| S05 | S | approval / control stale CAS、FK、重複ID、unknown state、boundedlist | 全部fail closed／回明確conflict；沒有未授權state overwrite |
| S06 | S,C | synthetic prompt/key/provider body/exception marker | DB、audit、CLI-safe diagnostics不得包含marker；只有必要identity/digests/codes |
| S07 | C | malformed/untrustedscope、reparse、scope TOCTOU、policy source替換 | 保留既有檢查並重新validation；不聲稱可抵禦惡意同帳號filesystem writer |
| R01 | U,S,C | 原2B-1 155 offline、六checkpoint／七controlled-provider情境 | 原success/securityassertions不放寬；新明確readonly Allow fixture context只是授權輸入，不是bypass；記實際數量 |
| R02 | U,C | PCA／Reviewer與readonly OpenCode | 既有tools/permissions/outputs/terminal rules與官方Workflow不退化；無新增write/shell或permission reply |
| R03 | C | 直接application API、ephemeral、不合法CLI mode、restart/batch | 全入口negative bypass tests；資料輸出也有scope；無secretdump／auto fallback |
| R04 | S,C | 任意DB owner修改approval資料 | 記錄威脅模型限制，不能把integrity_check當防竄改；若部署要求抵禦則trust前置未滿足、gate Blocked |

## 6. 真實process驗收順序

1. U＋S通過後，延伸既有`MAF.Acceptance` test-only runner；先controlled provider，不用real model驗policy。
2. 用protocol gate與DB短ReadOnly observer確認checkpoint，記PID/StartTime/executable identity與原始counters；所有等待有deadline，禁止用固定sleep猜crash窗口。
3. 只kill driver自己建立的Host/helper object，非process-name搜尋或process-tree廣殺；保留provider與DB/lock/journal，不刪檔搶鎖。
4. 新PID重新開同DB，讀同Approval/Operation/Task/Execution/Cancel IDs；先重新authorization/原session對帳，驗counter增量與每一state軸。
5. Writer完全關閉後，新process做ReadOnly integrity/schema/uniqueness/JSON-consistency檢查；同時檢查輸出redaction與truthfulUnknown。
6. 一個variant不能代表所有failure window；A11、K10等各自run，記錄actualcase/discovery/runoutcome；沒有checkpoint就Not Reproduced。

測試身分可用明確injectedfake subject驗businesslogic，但不能以此宣稱OS authentication通過。真CLI identity/ACL測試必須另記實際trusted operator機制、negative subject與scope拒絕證據，且不記tokens/password。

## 7. Optional live OpenCode interoperability

大多數2B-2技術gate可由deterministic U/S/C證明；live用於adapter語義的必要驗證，不以model品質當policyoracle。

| ID | 情境 | 必要觀測 |
|---|---|---|
| L01 | 合法批准後一次唯讀新任務；approve前停止重啟 | 原批准pending保持、approve零POST、explicitresume一次session/prompt；actualmodel/version、完整source/index/statusunchanged |
| L02 | active任務cancel，ack後pending/busy/terminal | 分開記本機intent、實際ack、correlatedterminal；API無法重現ack-only則Not Demonstrated，不由fake推論livepass |
| L03 | recovery時provider證據不可用／完成競爭 | 固定scope、原session/message；Unknown/Completed準確，無新prompt；缺安全outagemechanism則Blocked |
| L04 | 細粒度providerpermissionbridge | **MVP不支援，預期Deny／Not Supported**；不送permission/questionreply，不為測試放寬read/write/shell |

必須先有獨立operator/provider/server授權、新synthetic Gitrepo、loopback/read-only配置、source全集SHA256+index hash+完整status、可靠POST計數；禁止公司source/credentials/endpoints、global profile修改與security-policy變更。API不提供精確challenge binding／counter／可靠cancel證據時如實Not Demonstrated，不能不停重試／切模型掩蓋。Endpoint alert立即停止受影響workflow，保留事件，不改名／搬路徑／換loader。

## 8. Gate與報告格式

| Gate | 通過必要條件 |
|---|---|
| G0：owner/design | ADR、trust model、policy authority與migration策略接受；實作與執行分別明確授權 |
| G1：policy | P01–P12中適用必需項通過；unknown/defaultDeny、全入口不可bypass、descriptor不授權 |
| G2：approval durability | A01–A19與S01–S06適用項通過；真Pending processrestart、CAS races、exactbinding、consume/newCreatedatomic、批准後不重送 |
| G3：cancellation | K01–K18適用項通過；真ownercrash、ack≠Cancelled、unavailable/terminalrace、intent不被canceled token丟失 |
| G4：regression | R01–R03通過、R04威脅前提明確成立；build/warnings、實際offlinecounts、processintegrity、no-secrets evidence |
| G5：interop/deployment | 若要聲稱OpenCodecancel/approvalinterop，必須有相應live實測；沒有則縮小technicalgate聲明，單列Not Demonstrated／Blocked；公司clearance始終獨立 |

G0–G4有失敗或未執行必需項時不能接受MVP。Optionallive限制可以由owner接受明確boundary，但不能用文件或fake代替必要的processrestart、trusteddecision或不可bypass證據。MVP不支援providerpermissionreply是明確scope，不是假裝已驗收此能力。未成立的operatortrust不是optional live缺口，必須阻止productionactivation。

未來結果另建`PHASE2B2_RESULTS.md`，不覆寫本計畫或2B-1歷史：

```text
Source revision / dirty scope / authorized environment:
Case IDs / UTC start-end / layer / trust profile:
Actual command (secret-free) / exit / discovered-executed-passed-failed-skipped:
PolicyVersion / Epoch / Subject / Capability / Operation / ScopeDigest:
TaskId / RunId / ExecutionId / SessionId / MessageId:
OperationId / ApprovalId / DecisionId / CancelRequestId / row-control versions:
Original and restarted PID / StartTime / observed committed checkpoint:
Approval status / execution state+phase / cancellation intent+delivery+outcome:
Session / prompt / abort / reply POST counts before-after:
Transaction / migration / integrity / redaction / source-index-status evidence:
Outcome: Passed | Failed | Blocked | Not Executed | Not Reproduced | Not Demonstrated:
Limitations / security events / sanitized artifacts / next owner decision:
```

**原設計輪只有文件與靜態檢視；後續獲准M1的執行結果以獨立M1報告為準。** 原矩陣未完成的S/C/L與其他里程碑維持未驗證；沒有修改原安全事件／歷史testresults。M1之後停止，不自動開始M2或2B-3／2C／Phase3。


## 9. M3-0 gate and evidence classification (2026-10-16)

Before any M3 runtime implementation, use [M3-0 architecture gate](../plans/PHASE2B2_M3_ARCHITECTURE_GATE.md) and [ADR-006](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md). Map all reachable CLI, direct library, durable/ephemeral, batch recovery, inspection, resume, side-effect and abort paths to existing P01–P12, A01–A19, K01–K18, S01–S07 and R01–R04. Demonstrate each supported path cannot bypass policy, and distinguish controlled synthetic logic tests from authenticated production-human evidence. Add explicit cases for policy activation mode, admission-vs-dispatch race, and Broker/source rollback evidence only if existing case IDs do not cover them. M3-0 is documentation-only; the M2-B 347/347 result is historical evidence, **not** proof these runtime gates passed. M3-C remains **Blocked** pending source authenticity, human presence, protected writer and relevant restart/revocation proofs. Preserve original Planned/Executed distinction and G0–G5 acceptance semantics.
