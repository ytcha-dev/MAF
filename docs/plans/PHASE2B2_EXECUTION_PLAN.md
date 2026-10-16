# Phase 2B-2 執行計畫：Policy、人工批准與取消

日期：2026-10-16。狀態：**M1、M2-A與另批M2-B foundation已完成；production trust未部署，M3–M5仍為Proposed，沒有後續實作授權。** 原計畫source baseline：`64d3c36`，M1開發基準`009a96e`，M2-A開發基準`9849933`。[ADR-005](../architecture/ADR-005_POLICY_APPROVAL_CANCELLATION.md) 是完整決策提案；[acceptance plan](../acceptance/PHASE2B2_ACCEPTANCE_PLAN.md) 是分層矩陣，不是完整2B-2驗收結果。

後續狀態：owner另行批准的M1已實作並通過98個focused與253個offline cases，詳見 [M1 results](../acceptance/PHASE2B2_M1_RESULTS.md)。本檔保留原始里程碑提案；M2–M5、migration、identity resolver與runtime enforcement均未因此獲准或完成。

再後續狀態：owner只批准M2-A資料層；[M2-A results](../acceptance/PHASE2B2_M2A_RESULTS.md)記錄explicit offline schema v2、approval/control/coordinator與51 focused／304 offline通過。M2其餘整合、protected production authority與identity、M3–M5仍待授權；沒有provider活動。

M2-B增量：[results](../acceptance/PHASE2B2_M2B_RESULTS.md)記錄43新測試、94 focused／347 offline通過；serialized activation、replaceable identity/authorization與maintenance邊界已建立，沒有可信production source/human resolver，正式activation/approval保持disabled。M3 checklist見該報告；下面保留原始target，不能視為現在狀態。

## 1. 目標與現況

為本機單DB的唯讀外部任務建立default-deny application authorization、可跨restart的精確人工批准，以及不混淆ack/terminal的取消ledger。MAF控制啟動／觀測／取消的操作邊界；OpenCode仍控制其內部tool loop和實際permissions。未支援的細粒度tool approval不能以任務Allow代替。

已檢視Core durable契約、DurableExecutionService、OpenCodeExecutor/Client、SQLite v1 store、Host取消入口與既有fake/process/live測試。2B-1家用基線是43 focused、155 offline、C六checkpoint、D七情境、E三live；只引用[原報告](../acceptance/PHASE2B1_HOME_RESULTS.md)，本次未重跑。公司[安全事件](../acceptance/PHASE2B1_SECURITY_EVENT.md)仍保留，不因設計文件恢復執行。

目前沒有IPolicyEvaluator或MAF approval API；`PolicyVerified` trace只是provider safety preflight。`WaitingForApproval`是remote pending，不能讓它變成本地grant。現有AbortRequested bool、長期task file lock、各store method獨立transaction都需要明確integration設計，不能只追加CLI flags。

## 2. 分階段交付與複雜度

估算為相對複雜度，不是時程承諾；每個里程碑需要owner核准執行範圍與相應環境。先完成負向測試與持久化，再開provider副作用。

| 階段 | 交付／最小變更 | 複雜度 | Exit gate |
|---|---|---|---|
| M0：決策凍結 | 接受ADR-005；選operator trust、policy authority、scope/digest規格、v2 migration與期限。盤點所有入口 | 中 | Owner逐項確認；沒有決策的能力保持disabled |
| M1：純policy契約 | Core provider-neutral request/decision/capability/scope；可控clock、versioned canonical operation digest；小型deterministic evaluator | 中 | Allow/Deny/RequireApproval、unknown/high-risk Deny、epoch/scope/expiry tests；零HTTP、零schema migration |
| M2：durable approval與control | 同DB IApprovalStore、窄coordinator、v1→v2原子migration、Pending/decision/audit CAS與task_controls | 高 | pending crash survival、duplicate/race、revocation/expiry、migration rollback、SQLite integrity；仍不dispatch工作 |
| M3：trusted CLI與admission | 真operator subject resolver／受保護配置；list/inspect/approve/reject/revoke；explicit resume與input再供給；consume+Created一次admission | 高 | unauthorized CLI拒絕；approve不啟動；同operation consume一次；Created後crash不重送；ephemeral/direct entry無bypass |
| M4：cancel/reconcile | durable cancel intent、nonblocking control polling、command owner、abort evidence ledger、獨立budgets、truthfulstatus輸出 | 高 | requested/sent/ack/confirmed分開；ack-only、unavailable、completion race、crash window全部controlled-provider通過 |
| M5：可靠性驗收與文件 | 延伸既有test-only runner、全offline regression、必要且另行授權的synthetic live tests | 中至高 | acceptance matrix每項有結果/證據/限制；owner審閱technical gate與部署前提 |

**建議第一個實作里程碑：M1。** 先驗證policy決策、binding與trust context輸入，沒有live model、副作用或DB升級。若M0身份模型尚未核准，可審查M1契約草案但不得以測試用identity resolver作production fallback。

## 3. 預計程式邊界（未修改）

| 層 | 未來變更 | 保留／限制 |
|---|---|---|
| MAF.Core | policy/approval/cancel metadata、固定operation enums與結果、窄transaction coordinator契約 | 不依賴SQLite/OpenCode JSON、OS credential APIs或registry |
| Host／application boundary | trusted caller/context解析、fixed descriptor/config、control CLI、統一protected-operation入口 | 不以CLI是否顯示按鈕作authorization；無Web UI、`--actor`任意字串或autoapproval |
| Workflows.External | 新admission seam接回同一official Workflow；每個副作用前check control/policy；recovery授權GET而不submit | 不自製agent loop，不把existing Created row當newtask重送；不改2B-1 fingerprint算法 |
| Persistence.Sqlite | IApprovalStore、schema v2、同DB短transactions、CAS與audit | 不用兩次獨立store calls冒充atomic；不改舊snapshot格式／enum numeric值；不做network transaction |
| OpenCode adapter | 保留Validate/reattach、readonly policy與correlation；若需要，新增窄的abort delivery evidence結果 | 無write/shell，不發permission/question reply，不宣稱所有版本支援未知API |
| Tests／MAF.Acceptance | unit／SQLite／跨process／fake provider fixtures與sanitized metadata | 先沿用已驗證runner，不建立通用test framework；live獨立opt-in |

Authority放在application service及adapter副作用邊界。M3前必須列出call graph：`--external-task`（durable和ephemeral）、RunAsync首次/既有TaskId、RecoverAsync、batch recovery、cancel/abort、future resume；direct library方法不能持有無檢查的旁路。預設policy不是「缺設定沿用Allow」；2B-1正向測試可用**明確受信任的唯讀Allow fixture policy**保留原成功assertions，另外測缺設定Deny，不關閉新gate求綠燈。

## 4. 持久化與鎖順序

1. 最初request在authorization_operations/task_controls預綁TaskId、request fingerprint、immutable IDs；評估RequireApproval後同transaction保存Pending與audit。沒有execution row或remote POST。
2. approve/reject使用trusted operator＋required scope＋expected version，一次短transaction；不拿整輪execution lock，不觸發Workflow。
3. explicit resume先校驗原input（若process已丟失），取得最新control、policy、scope；若已有execution，只授權GET並沿用原handle。
4. 新任務consume先持有TaskId command ownership，再開短DB transaction重新核對policy epoch、controlVersion、expiry與批准CAS；同commit consume＋insert Created＋audit。不能從HTTP callback取得新批准或在transaction等人。
5. Transaction後只有當次new admission continuation可進既有launch path。Crash後任何existingrow都走2B-1 recovery，哪怕尚未送prompt也不補送。operator需理解這一安全優先的Unknown窗口。
6. 取消意圖只需短DB CAS；不得等現有running owner釋放file lock。owner在poll與副作用前查control；沒有owner時cancel worker取得相同TaskId ownership，先GET再決定首次abort。

固定鎖順序：command workflow先TaskId ownership，再短DB transaction；control decision/cancel-intent只DB，不反向等待TaskId鎖。Migration另有DB全域schema coordination，要求所有Host停機；禁止一邊持長transaction一邊等running owner退出。既有5秒SQLite busy timeout不放寬，超時回明確失敗，不刪鎖檔。

Race linearization：cancel/control epoch先commit→admission/dispatch CAS拒絕；dispatch intent先commit→可能已開始，cancel為best effort reconcile，不保證遠端從未執行。已consume的批准不能被重用；已dispatch操作不能靠改approval row「撤銷」。

## 5. 身分與CLI實作前置條件

MVP先支援單機受信任operator，而不是RBAC平台。建議OS token subject與scope allowlist在受保護config解析，與submitter身份分離，預設禁止self-approval。Username、terminal confirmation、repo檔案、模型文字均不是可信identity。若同OS帳號可讓agent執行approve，必須有另核准的人類在場驗證或不同trusted principal；無此條件則功能保持disabled。

同DB可寫者能改approval資料，故不聲稱防惡意同principal/DB owner。若owner要求抵禦這種威脅，需在M0另定隔離或簽章信任方案，不能以MAC/hash欄位、命名或nonce代替。不得在本次為達成demo自動改ACL、改execution policy、安裝本機service或讀出憑證。

CLI只提供概念上的list/inspect/approve/reject/revoke/resume/cancel/status。批准與resume是兩個行為；批准後scope變了／task完成或取消了就關閉操作；provider unavailable保持Unknown與原identity。回應分開呈現execution、MAF approval、provider waiting與cancel delivery/outcome，保留舊external mode exit codes。新control CLI的exit/code mapping在M3固定並寫tests，不重用Completed宣稱「人類同意」。

## 6. 取消與provider能力限制

M4需把取消來源分開：explicit caller command先commit意圖再觸發owner CTS；timeout／HTTP cancellation／程序終止／inspect等待取消沒有新取消授權。Abort與最終觀測使用獨立bounded budget，即使原CTS已取消仍可保存truthful outcome。

Cancel ledger存requested、dispatch-intent、可取得的sent/ack evidence與correlated outcome；bool AbortRequested只是相容projection。Abort ack後若busy／permission pending→Running/ProviderInputPending或Unknown，不能Cancelled；完成先到就保留Completed。DispatchUncertain restart預設GET-only；要重送abort需先證明該adapter語義與新授權，不假設POST idempotent。

MAF human approval MVP不回答OpenCode內部permission。若未來bridge成立，至少需版本化challenge ID、exact capability/resource digest、同session、可驗證freshness與reply idempotence，並先reconcile provider。能力不成立就Deny/Not Supported。這是後續受控增量，不把「批准唯讀任務」擴張成能控制所有工具。

## 7. Migration／相容與發布檢查

- Schema v2只有在owner明確批准後實作；exclusive maintenance、stop v1 Hosts、核准備份策略、原子建表與user_version更新、fault injection rollback、unknown-schema fail closed。
- v1執行metadata繼續可讀；無批准歷史的舊row只能經目前policy允許做inspect/cancel，不能補grant。v1 AbortRequested=true無ledger時delivery為Unknown，不填ack。
- v1binary拒絕v2是預期保護；禁止mixed writer、自動downgrade、restore舊DB後直接重新提交。新binary不能跳過schema gate以相容舊test。
- 每個code里程碑跑相關focused測試，必要時完整offline與process regression；不因一輪通過反覆無目的重跑。live只在獨立授權的synthetic fixture，有可靠request accounting與source/index/status baseline。
- 新文件需把Proposed／Implemented／Executed／Accepted與corporate clearance分開。任何安全alert立即停止affected workflow，保留證據，不搬檔／換loader繞過。

## 8. 範圍與驗收交接

不做write-enabled agent、shell tools、Web UI、distributed policy server、全面RBAC、generic event bus、background scheduler、Connector Registry、multi-agent orchestration、A2A、2B-3、2C或Phase3。不改公司安全事件／原test results，不把新policy設計當IT clearance。

Owner需核准：trust/identity與self-approval、policy epochauthority、schema v2停機/備份、input reentry UX、bounded approval期限與audit保留。建議provider permission bridge與uncertain abort retry保持disabled。若owner只批准M1，後續M2–M5不因此自動獲准。

原文件設計輪只做source/evidence檢視與Markdown，沒有restore/build/test或live prompt。後續明確核准的M1已完成build與offline tests，結果見開頭連結；沒有live prompt。其餘里程碑仍須owner明確核准。


## 9. M3-0 revision — architecture gate before runtime work

The original M3 table above remains a target, **not implementation authorization**. Adopt the documentation-first [M3-0 architecture gate](PHASE2B2_M3_ARCHITECTURE_GATE.md) and [ADR-006](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md): source call graph and bypass review; explicit-vs-opportunistic policy activation decision; exact admission / dispatch-intent / cancel ordering; approval/resume separation; durable recovery-only behavior. Afterwards independently consider M3-A isolated policy admission, M3-B synthetic approval/resume and **blocked** M3-C production trust and CLI activation. No production human identity, policy publisher, protected writer or Broker currently exists; default-deny controls remain in effect. Do not proceed automatically to M3-A.
