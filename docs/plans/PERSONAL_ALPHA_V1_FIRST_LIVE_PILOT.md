# Personal Alpha — First OpenCode V1 ReadOnly Pilot

日期：2026-10-27。狀態：**Preparation only / Live Blocked**。本文件不授權下載、啟動 OpenCode、連線模型或將公司資料送出。V2、ProductionProtected、schema migration 皆不在範圍。

## 1. 最小 pilot

一次只執行一個明確 ReadOnly task、一個既有本機 workspace、一個專用 V1 server、一個 session 與最多一次 prompt submission；復用既有 PCA → OpenCode → Reviewer → HumanGate。結果、evidence、limitations、Unknown 都由原 outcome 呈現；Completed 不等於 Accepted。禁止從 OfflineDemo 的 scripted result 或 PolicyOutcome.Allow 推導 live 授權。

建議第一個真實任務：在 Owner 明確核准、無秘密的本機個人 workspace，讀取兩個事先列名的文字檔（例如 README 與入口程式），回覆用途、入口點與兩處可核對的引用，再由 Reviewer 獨立檢視。禁止 edit、shell、外部目錄、web/MCP、subagent 或未列出的來源；遇到等待權限就停止，不回覆 permission/question。不把公司 repo `D:\_research_repo\MAF` 自動當成 live workspace。workspace 的實際路徑與兩個檔案須由 Owner 指定並核准，現為 **Missing**。

第一個 pilot 不提供自動恢復、重新提交或 remote Abort；逾時/失聯保留原 identity 與 Unknown，停止新增 dispatch。專用 server 的 stop/exit 不是遠端 terminal proof。若需 remote Abort，必須另行核准 exact operation 與原 session delivery，不以 cleanup 名義呼叫既有 Abort。

## 2. 精確 V1 pin 與仍缺的驗證

候選固定為 **OpenCode 1.18.35**，與 repository 歷史 API discovery 相同；不選擇 latest，不因 1.x 名稱而推論相容。

2026-10-27 以官方 GitHub release GET 核對：[v1.18.35](https://github.com/anomalyco/opencode/releases/tag/v1.18.35)，published `2026-10-06T20:18:39Z`。Windows x64 候選 artifact：

```text
opencode-windows-x64.zip
https://github.com/anomalyco/opencode/releases/download/v1.18.35/opencode-windows-x64.zip
Published archive SHA-256:
c90d248cca75e42fd15422a29b5f83a1b30c441af83565635d6b2682adc58dd1
```

這是官方 API 提供的 archive digest，**不是本次對下載檔的 hash 驗證**。本次未下載、解壓、安裝或執行；解壓後 executable digest、簽章/來源審核與核准絕對路徑都是 Missing。將來先取得 binary 使用授權，驗證 archive 與內部 executable，再在核准環境比對 executable 版本、owned server `/global/health.version == 1.18.35` 及實際 `/doc`。任何 mismatch 在 Create/Prompt 前停止，不 fallback、更名或換成 shell launcher。

[官方 V1 Server 文件](https://opencode.ai/docs/server/) 說明 `serve`、Basic auth 與 `/doc`。文件會更新，不能取代固定版本的現場 schema 驗證；本 repo 的 [1.18.35 discovery](PHASE2A_API_DISCOVERY.md) 也是歷史證據，不是本次 live pass。

## 3. Pilot 設定與核准清單

每一列都需要實際值或可核對證據。秘密只保留 approved secret reference，不寫入此文件、Git 或 console log。

| Gate | 需要的確切內容 | 目前狀態 |
| --- | --- | --- |
| 執行環境 | Owner/IT 核准環境、OS、時間與允許的程序/loopback/儲存/模型 scope | 本次只有 build/offline tests/CLI walkthrough clearance；**沒有 live approval** |
| Workspace / task | Owner 核准絕對路徑、兩個來源檔、原 objective、ReadOnly、單一 task/run/operation | Missing；不得選用未經核准的公司資料 |
| Binary / version | 上述 artifact、verified archive/exe hashes、approved absolute exe、1.18.35 health/schema | 只有官方 artifact metadata；實體驗證 Missing |
| PCA recipient | configuration ID、provider、完整 HTTPS API base endpoint、精確 model、secret reference | Missing；和 Reviewer 分開綁定 |
| Harness recipient | configuration ID、providerID/modelID、完整 HTTPS downstream endpoint、secret reference | Missing；server loopback origin 與 provider HTTPS endpoint 是兩個不同對象 |
| Reviewer recipient | configuration ID、provider、完整 HTTPS endpoint、精確 model、secret reference | Missing；不從 PCA client 共用或默認推導 |
| Downstream config | 實際載入的 config files/environment/agent merge、digest、custody through use；明示 override/fallback/title/small-model/summary/compaction/plugin/MCP/telemetry/update 等流向 | Missing；目前 plan 只接受描述為 None，不證明 runtime 真為 None。不可用 prompt 指令假稱已禁用 |
| Data-flow approval | PCA source/tool context、Harness prompt/tools、Reviewer output+source evidence 各送何處、可接受的資料類型/保留/費用 | Missing；模型可用、免費或 endpoint 可連不代表允許傳送 |
| 專用 server | loopback-only、approved fixed port/origin、明確參數/env/cwd/auth、原 child ownership、禁止共享 service/PID attach | Missing；不採共享等價方案，因此無額外的共享程序責任豁免 |
| Durable evidence | intent fsync-before-wire、one-attempt reservation、original Create response→candidate→correlation→receipt、session/model/workspace/rules 一致、原 outcome flush、inspection-only reopen | offline 機制可重用；live provenance/receipt 證據未建立，**硬 gate** |
| Budgets / stop | stage ≤120s、總 admission lifetime、GET/POST/model/source/output budgets 與費用上限、stop/retain-owner/人工處置人 | Missing；不能只以現有技術上限宣稱成本保證 |

ReadOnly 驗收需比較前後核准來源與 Git index/status（若適用），亦檢查 workspace 未新增內容；這些檢查本身不可呼叫未核准的 git hooks/工具。MAF 的 journal、server config/state、test evidence 放在另外明確核准且 owner-owned 的 storage，不能寫入 ReadOnly workspace。若任何檔案改變，停止並保留證據，不自動 revert 或清理。

## 4. Exact code seams 與工作量

以下為讀取目前 source 後的差距，**不是已實作工作**。估算以單一工程師、範圍已核准、cached dependencies、現有 contracts 能保留為前提；含 scoped tests，不含等待 security/recipient 決策、模型排隊或產品 pilot。沒有新 framework / interface / factory / Registry / schema 的預算。

| Seam | 現有行為與必須補足之處 | 最小實作 / 預估 |
| --- | --- | --- |
| `OpenCodeClient.ValidateAsync`（`MAF.Workflows.External/OpenCode/OpenCodeClient.cs`） | health 只記錄 ServerVersion，再核 schema/agent，沒有 exact version pin | 在既有 OpenCode options/validation 增加明確 expected V1 pin；mismatch 在 POST 前拒絕，無 V2 negotiation 或 fallback；0.5–1 日 |
| `OwnedHarnessProcess.Start`、`OwnedProcessInitializationException.Owner`（Host） | R1 保留 setup-failed owner；未接 server composition，output 全丟棄，survival 不是 readiness | 既有 Host 內最小 dedicated V1 preflight：approved exe/env/args/cwd、固定 loopback origin、bounded health/schema/agent、owned-only teardown。埠被佔用或 child exit 即拒絕，不能只信任 health 或接管現有服務；1–2 日。endpoint identity/auth 與 custody 證明仍需核准設計 |
| `PersonalLabAdmission.AdmitOffline/Reserve`、`LocalExecutionPreparation.DemandExecution/DemandHarnessRecipient` | offline-only；Network 明確拒絕；description matching 後仍拒絕 recipient verification | **沒有 live grant seam**。Owner 需先接受 exact PersonalLab operation/recipient 與 cooperative custody 限制，並決定可用 evidence；不能刪掉 Network check 或讓 ForTests 接真實 HTTP。條件確立後，既有 Host 邊界內加入僅限該 scope 的 live effect path；1–2 日，決策未定不能承諾工期 |
| `LabOpenCodeExecutionPlan`、`LabOpenCodeBoundary`、`BoundNativeRecipient` | plan 是 supplied digest；boundary 只由 ForTests 構造；native CreateDisabled 使用 Network 因而被拒絕；模型 pin 有 guard | 重用 routing/model/redirect/no-retry/role checks；先以獨立 evidence 對照有效 config 和三 recipients，再提供單一 reviewed composition，禁止 blanket enable；1–2 日，與上一列共用修改，不能直接相加 |
| `PersonalLabOfflineOperation.Reserve/RecordOutcome`、`LabProvenanceInspection` | journal 已 flush、原 response receipt、Unknown/no replay；但 provenance/receipt 固定寫明 ControlledOfflineFixture / NotLiveProvider / NotProductionAuthenticity | 機制可重用但不能直接把 fake evidence 換 live 標籤。先證明現有格式能誠實表示 live original-response custody 且舊 offline rows 永不升級；不能證明則 **Blocked**。不在本任務改 schema、回填或借用 ProductionProtected v4 authority。可行性與 scoped regression 0.5–1 日；不可行不繼續實作 |
| `PersonalLabOfflineRuntime.RunAsync/RequestCancellationAsync/WaitForCompletionAsync`、`LocalSoloConsole` | runtime 只 ForTests；CLI demo 使用 scripted participants；mapper 也固定 ControlledOfflineFixture | 重用 actual V1 executor、native adapters、Workflow、ledger、drain、原 journal outcome；補一個 separately approved internal pilot composition，輸出誠實來源標籤；local submit 仍 blocked，不能從 demo 的參數打開 live。1–2 日，含停止/identity/展示 regression |

最小真正 live journey 仍約 **4–7 工程日**（各列重疊，不逐項相加），且 receipt/custody gate 可能使此估算失效。它不是改兩行 URL 就能啟用。驗證成功前不擴建底層平台。

建議下一個 code increment：**既有 Host/Adapter 的 exact V1 pin + dedicated preflight/ownership 小增量**，先用 fake transport 覆蓋 mismatch、port conflict、startup/health race、R1 owner retention 與 teardown；保持零 Session Create / Prompt / 模型請求、live admission 仍 blocked。只在另行取得 process/loopback scope 後驗證 preflight；不先做新的通用 launcher/provider 系統。與此同時由 Owner 提供上表實際值並審核 live provenance 可行性，避免寫完仍無法合法接通。

## 5. Durable evidence 與無重送規則

1. submission 前固定原 operation/task/run/execution/request identity、workspace、scope、recipient/model/config binding 與 budgets；intent 持久化成功後才可 wire。
2. Create 最多一次；只有原 HTTP response 可建立該 candidate/correlation/receipt。後續 GET、log、程序存活或 matching digest 不能補造 missing original response。
3. verified receipt 在 Prompt 前 flush；Prompt 最多一次，ambiguous response 不退款、不重試、不建立 replacement session。
4. Observe 只查原 session/request；assistant parent/message/model/source binding 與 terminal finish 皆通過才進 Reviewer。idle、失聯、Abort ack、process exit 不等於 terminal。
5. Reviewer 使用同一 typed TaskEvidence 與獨立 bounded source acquisition；缺證據就 InsufficientEvidence/ReviewUnavailable，不能以 prompt copy/paste 宣稱已驗證。
6. Unknown 或 WaitingForApproval 保留原 identity 與 limitation，停止自動 dispatch；reopen 只 inspect，不重建 admission/resume authority。
7. cancel/drain 超時保留相關 resources；process owner 未 resolved 就不得只 Dispose handle。journal/native clients 必須活到原 work drain、outcome 記錄完成。

## 6. A/B 產品比較與驗收表（尚未執行）

A：操作者手動 OpenCode → Reviewer；B：MAF 協調相同工作。兩邊使用相同 workspace snapshot、task、V1/model/provider、Reviewer prompt 與證據要求；若 A 沒有 PCA，單列 B 的額外 PCA 成本，不把它藏在模型等待。兩次各只一個 task/session/prompt；順序先由操作者記錄，第一組只作方向性證據，不宣稱統計顯著。不得跨組復用原 session、cached answer 或偽造 receipt。

| 觀察項 | A 手動 | B MAF | 記錄方式 / gate |
| --- | --- | --- | --- |
| task/context 複製貼上 | NotRun | NotRun | 每次人工複製或貼入資訊算一次，原始任務輸入另列；不把一次貼多段當零成本 |
| Harness→Reviewer 搬運 | NotRun | NotRun | B 正常 journey 必須 0 次；不得靠人工讀 JSON 拼 Reviewer input |
| 應用切換 | NotRun | NotRun | 為協調而切換前景 app/window 每次計 1，記錄來源→目的；模型等待不計切換 |
| active coordination time | NotRun | NotRun | 計時建立上下文、搬運、發 Reviewer、拼結果；模型等待另記，不能以總 elapsed 偷換 |
| 首次任務建立 | NotRun | NotRun | B 一次 invocation + 一次明確確認，無重填原 task |
| evidence quality | NotRun | NotRun | 兩引用可獨立核對，task/run/execution/session/request 相符；缺證據不評為 verified |
| reliability / intervention | NotRun | NotRun | Failed/Unknown/Completed/Waiting 清楚；permission 不自動答覆；ambiguous 無重送 |
| task quality / extra repair | NotRun | NotRun | 是否答對入口點；是否仍需人工重貼或改寫上下文才能 review；記錄失敗，不刪除失敗 trial |

通過條件：必要安全與 durable evidence 全部成立、B 的 Harness→Reviewer 搬運為 0、總人工資訊搬運少於 A、主動協調時間下降、evidence quality 不降低。app switches 列實數與原因；任何改善都不能抵銷安全失敗。若沒有明顯協調成本改善，產品價值維持 Unproven，下一步修 journey，不增加 V2/Registry/平台抽象。

### Pilot readiness checklist（未勾選即不可執行）

- [x] Offline Alpha build、16 new cases、四 scenario walkthrough 具實際 [evidence](../acceptance/PERSONAL_ALPHA_VALIDATION_V1_PILOT_RESULTS.md)。
- [ ] 既有 scoped regression 的 3 個初跑失敗已釐清並獲 Owner 接受；diagnostic 9/9 不取代初始失敗。
- [ ] 專用 process 的必要 OS cases 另獲授權並驗證；OS-specific claims 不借用 fake 結果。
- [ ] 環境與 live process/loopback/storage/model 各操作獲核准；非自動沿用本次 offline clearance。
- [ ] Owner 指定並核准 workspace、來源檔、task、三 recipients、模型、完整 endpoints 與資料流。
- [ ] Archive/executable digest 與 fixed 1.18.35 health/schema/agent 驗證一致；無自動 upgrade/fallback。
- [ ] 專用 child/transport ownership、auth、occupied-port/race rejection、config custody/effective downstream flows 已驗證。
- [ ] 現有 journal/receipt 契約可表示 live evidence，舊 offline evidence 永不升級；否則維持 Blocked。
- [ ] 原 identity、intent-before-wire、one-attempt Create/Prompt、Unknown/no-retry 與 inspection-only reopen 不弱化。
- [ ] Reviewer 自動接收原 typed evidence；來源/模型/limitations 可核對，完成不冒充接受。
- [ ] 有限 budgets、成本上限、停止條件與 Unresolved 人工處置人已確認。
- [ ] A/B 表格、操作者、計時定義、同等品質要求與結果保存位置已預先固定。

目前判定：**Offline validation 是獨立 gate；Live pilot 尚未 Ready，執行仍 Blocked。**
