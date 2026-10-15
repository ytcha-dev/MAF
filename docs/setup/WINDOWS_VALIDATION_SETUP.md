# Windows：Phase 2B-1 可攜驗證設定

日期：2026-10-15。靜態核對的 implementation revision：`95c963205b742de21cb302f9fea02041921501f5`。請另記錄實際驗證 checkout 的 SHA。本文件沒有執行結果；Phase 2B-1 尚未 accepted。

## 1. 使用與資料授權 gate

**所有安裝、preflight、build、restore、test、HTTP、process 與 fixture 指令僅供另行授權的個人 Windows 開發環境使用。公司工作站的 execution hold 完全不變。** 本次沒有執行這些指令，也沒有新增可執行 preflight 程式。

個人環境可執行程式，不等於公司程式／歷史紀錄可移轉。開始前，資料 owner／必要 IT/security 流程必須核准 source、版本／history、測試內容、provider、安裝方式及本機資料範圍。既有 GitHub access 或 push 不是資料移轉授權。未核准時停在文件審查，不 clone／export 全 repository，不帶公司帳號、端點、憑證或 logs 回家。

此 handoff 不需要公司網路／VPN／PoC endpoint。Offline tests 使用 synthetic fixtures；live 使用個人環境**另行核准**的 provider 及新 synthetic repository。若 source 仍受限制，由 owner 提供經審查的最小 source snapshot 或重新撰寫的 synthetic equivalent；不能把刪除目前檔案當成已清除 Git history。本次沒有製作或轉移 snapshot。

## 2. 軟硬體與 dependency matrix

建議起始配置：受支援 Windows 11 x64、2 logical cores、8 GiB RAM、至少 5 GiB 可用本機磁碟；這是安排驗證的估算，**不是已量測最低需求**。MAF 本身不需 GPU／本機 LLM；若選本機模型，其硬體需求另評估。Windows ARM64／x86 的 native dependency 組合尚未驗證，不當成 x64 證據。

| 項目 | 實際要求／版本 | 類別／依據 |
|---|---|---|
| .NET SDK | 能 build `net10.0` 的 .NET 10 SDK；事件前曾用 10.0.103 | build/test 必需；所有 7 個 csproj 均 target net10.0 |
| .NET runtime | .NET 10；SDK 含對應 runtime | 執行 Host/test 必需；只有 runtime 不能 build |
| SDK pin | 無 tracked global.json、Directory.Build.*、packages.lock.json | 不可宣稱固定 SDK／transitive graph；記錄實際 SDK、runtime、resolved packages |
| Microsoft.Agents.AI.Abstractions | 1.24.0 | Core direct reference |
| Microsoft.Agents.AI | 1.24.0 | Comprehension、Review direct reference |
| Microsoft.Agents.AI.Workflows | 1.24.0 | Workflows.External direct reference；保留官方 Workflow API |
| Microsoft.Extensions.AI.OpenAI | 10.10.1 | Host direct reference；external mode 不使用 Host AI endpoint |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | Host direct reference |
| Microsoft.CodeAnalysis.CSharp | 5.0.0 | Review direct reference；NuGet Roslyn，不需額外安裝 compiler tools |
| Microsoft.Data.Sqlite | 10.0.0 | Persistence.Sqlite direct reference |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Persistence.Sqlite explicit override；managed provider + native SQLite 的 bundle |
| SQLite transitive/native | 由 restore 解析 core/provider/native library packages；不能只看 top-level | 不需系統 SQLite service、ODBC、SQL Server 或 sqlite3.exe；核對 resolved graph 及架構 |
| xunit / xunit.runner.visualstudio | 2.9.3 / 3.1.4 | development-only，MAF.Tests |
| Microsoft.NET.Test.Sdk / coverlet.collector | 17.14.1 / 6.0.4 | development-only，VSTest/testhost 與可選 coverage collector |
| Git for Windows | Git 2.x；repo 未 pin exact version，記錄實測版 | source 管理；ToolTests／ReviewTests／live fixture 也需 `git.exe` 在 PATH |
| OpenCode native CLI/server | **歷史實測 1.18.35**；沒有宣告最低版或任意新版皆相容 | live-only；offline fake/SQLite tests 不需 OpenCode |
| PowerShell | Windows PowerShell 5.1 的互動命令可用；7.x 可選 | 操作說明；不要求執行 .ps1、變更 ExecutionPolicy 或 admin |
| Python | **不需要** | 沒有 tracked .py 或 runtime 呼叫；以前使用 Python 是一次性開發輔助，不是安裝要求 |
| Node.js / npm / Bun | **不是 MAF build/runtime dependency** | 未追蹤 `.opencode/` 是本機 tooling；不納入可攜依賴，不執行 npm install |
| Visual Studio / VS Code | 可選 editor，CLI 足夠 | 若選 IDE，依官方 .NET 10 支援版本另核對；本指南不要求 IDE |
| SQLite CLI / DB viewer | 可選、需獨立授權與來源核對 | 進階 read-only DB integrity／checkpoint 觀測；不是現有測試的必要套件 |

版本來自各 `src/*/*.csproj` 與 `tests/MAF.Tests/MAF.Tests.csproj`，不是機器快取推測。套件 direct versions 固定不代表所有 transitive versions、SDK 或 native loader 已驗證。

Tracked `scripts/evaluate-review.ps1` 是可選的 Review／真實模型評估輔助，非 Phase 2B-1 build/runtime 或本指南的必要步驟；本指南不執行它。既有 obj/bin 若來自較早 build，也不能作為新 Persistence project 的完整 dependency resolution 證據。

SQLite 載入由 Microsoft.Data.Sqlite 的 bundle [自動初始化](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)；目前沒有額外 `Batteries.Init()`、自編 native library 或 schema migration executable。Store 首次連線建立 v1 schema，`user_version` 非 0/1 時拒絕，Pooling=false、busy timeout 5 秒、synchronous FULL。不需另裝 SQLite Server。歷史 [smoke](../acceptance/SQLITE_SMOKE_RESULTS.md) 曾載入 native 3.53.4，**本次新 graph 尚未 restore，不能保證解析後一定是該版本**。先前預設 transitive 2.1.11 觸發 NU1903；不可移除 explicit bundle、停用 audit 或忽略警告來取得綠燈。

## 3. 個人 Windows 初始安裝

1. 先完成上述 source／個人環境／下載工具授權。使用一般帳號，選本機普通目錄；避免 OneDrive、network share、symlink/junction ancestor。
2. 依 [.NET 官方 Windows manual install](https://learn.microsoft.com/en-us/dotnet/core/install/windows#manual-install) 從 [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) 取得獲准的 Windows x64 SDK ZIP，核對官方 checksum，再手動解壓至使用者工具目錄。不要執行 dotnet-install.ps1、winget/system-wide installer 或修改機器 PATH。
3. 若需重現歷史 SDK 可另行選 10.0.103 archive；這不是 repo 強制 pin，也不是最新安全版本建議。若改採獲准的 patched .NET 10 SDK，記錄差異，不自行升級專案套件。
4. 由 [Git 官方 Windows downloads](https://git-scm.com/downloads/win) 選獲准的 portable distribution，核對來源／簽章後由 operator 安裝到使用者目錄。本文件不執行下載的 self-extractor，不授權機器範圍安裝。
5. 只取得經核准的 source checkout/snapshot。不要複製公司 NuGet cache、bin/obj、使用者設定、`.opencode/`、.env 或 TestResults。若取得的是 snapshot，另記錄由 owner 提供的 revision/hash；Git-dependent tests 仍會建立自己的 synthetic Git repo。
6. Live 階段才需要由 [OpenCode 官方安裝說明](https://opencode.ai/docs/)／其官方 release links 取得獲准的 native Windows executable，記錄 version。不要把 npm PowerShell shim 當必要項，不改 .ps1 policy。如果所選版本只提供 WSL 路線或無原生套件，本原生 Windows handoff 視為 prerequisite 未滿足，不自行改驗證平台。

以下只設定**目前個人 PowerShell session**。先把 placeholder 換成已核准的使用者目錄，不要使用公司路徑：

```powershell
$env:DOTNET_ROOT = '<user-owned-dotnet-sdk-directory>'
$gitBin = '<user-owned-portable-git-directory>\cmd'
$env:PATH = "$env:DOTNET_ROOT;$gitBin;$env:PATH"
Set-Location '<authorized-source-directory>'
$repoRoot = (Get-Location).Path
$validationHome = Join-Path $env:LOCALAPPDATA 'MAFValidation'
$evidenceDir = Join-Path $validationHome ('evidence\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$env:MAF_EXECUTION_DB = Join-Path $validationHome 'data\executions.db'
```

沒有修改 `$HOME`／`$CODEX_HOME`、registry、system environment 或安全設定。`$validationHome` 不得位於任何 Git/SVN ancestor、Temp 或 source root 下。Host CLI 使用 DB path 後自行建立 parent；evidence directory 只在獲准驗證時建立。

## 4. Environment 與 CLI

不要 `Get-ChildItem Env:`、輸出 secret value 或將憑證寫入命令／TRX／opencode.json。使用該個人環境核准的 secret injection，過程不需公司 key。

| 名稱／選項 | 需要時機／含義 |
|---|---|
| `DOTNET_ROOT`、session PATH | portable SDK／Git/native executable lookup；不是 MAF 特有變數 |
| `MAF_EXECUTION_DB` 或 `--execution-db <absolute-file>` | durable 必需；CLI flag 優先。無兩者時仍是 ephemeral 2A，不能驗收 restart |
| `OPENCODE_BASE_URL` | Host external/recovery 必需；例如 `http://127.0.0.1:4096`，純 origin，不能含 userinfo、path、query、fragment |
| `OPENCODE_PROJECT_ROOT` | external/recovery 必需；與 `--project` 相同 canonical synthetic root，整條 ancestor 不可為 reparse point |
| `OPENCODE_AGENT` | 預設 `maf_readonly`；只准 readonly agent |
| `OPENCODE_ALLOW_LOOPBACK_HTTP=1` | Host 在 loopback HTTP 時必需；HTTPS 仍只接受 loopback |
| `OPENCODE_SERVER_USERNAME/PASSWORD` | server啟用 Basic auth 時才用；server 與 Host 必須相符，不能在 URL／參數填 password |
| `AI_BASE_URL`、`AI_API_KEY`、`AI_MODEL` | 僅直接 PCA／Review CLI；external/recovery 與 offline tests 不需要。這些變數不會自動替 OpenCode 配模型 |
| `OPENCODE_CONFIG`／`OPENCODE_CONFIG_DIR`／`OPENCODE_CONFIG_CONTENT` | OpenCode 自身可選設定來源，不是 Host 讀取項；注意 project/global/managed settings merge，不能以 custom config 宣稱已隔離一切全域設定 |
| `MAF_LIVE_OPENCODE=1` | 只啟用既有 3 個 Phase 2A live facts；offline 階段保持未設定，並用 filter 排除 |
| `MAF_OPENCODE_TEST_ROOT` | 既有 live tests 必需；真正在 Temp 下、basename `maf-phase2a-*`、有 opencode.json、README.md、Program.cs、普通 `.git` directory 與已建立的 index |
| `MAF_OPENCODE_RESULTS_DIR` | 既有 live tests 必需；新的個人測試證據目錄，不使用公司 artifact |
| `--task-id` | durable fingerprint 的穩定識別；不可放秘密。同 TaskId 不同 task/root/timeout/policy 拒絕 |
| `--external-task`／`--recover <id>`／`--recover-pending` | 互斥模式；recovery 不需要原 task text，不建立 session 或 prompt |
| `--external-timeout` | Host 整數 1–600 秒，預設 120；durable observation timeout 不 abort。recovery discovery 與 observation 各受 budget 限制 |
| `--output json|text|verbose` | 預設 JSON；單筆沿用原 schema，batch 輸出單一 array |

Recovery 必須保留同一 DB、TaskId、provider origin／agent／policy scope、project root、WorkflowRunId、ExecutionId、ExternalSessionId、ExternalRequestId。不能透過換 DB／workspace／TaskId 掩蓋不確定的提交。

C# `OpenCodeOptions` 另有非 CLI 設定：RequestTimeoutSeconds 預設15（允許1–60）、ResponseLimitBytes 預設1 MiB（1000–3000000）、SummaryLimit預設32000（100–64000）、RequireReadApproval預設false（permission live fact 使用true）。Host目前不提供對應environment flags，不要自行發明變數。Executor預設1秒poll、最多200次；messages最多3頁各100筆，abort有獨立5秒budget。Task等待時間不等於單一HTTP timeout；即使external-timeout設600，poll上限仍存在。

## 5. Network、filesystem 與副作用

- 初次工具下載／NuGet restore 需要允許的 HTTPS 來源，通常為官方 download sites、NuGet feed 及其 CDN（TCP 443）。Repo 沒有 tracked NuGet.Config；個人機器的 user/machine feed 可能不同。不要匯入公司 feed credentials、憑證、proxy 或 VPN；不可因 unreachable 就關 audit／TLS。
- Offline tests 是不呼叫 live AI/server，**不是完全無副作用**：會建立／寫入／刪除 Temp fixtures、DB/journal/lock files、執行 Git；Windows `ToolTests.RejectsJunctions` 還會呼叫 `cmd.exe /c mklink /J` 並清理 junction。不保證所有 filesystem／EDR policy 允許。
- Live 的 Host→OpenCode 只連 loopback，預設 TCP 4096；此 port 是慣例，不是硬編碼限制。既有 live tests 缺 URL 時 fallback 4096，故仍應明確設 URL。不要占用／停止既有 shared server；另選獲准的 dedicated port 並記錄即可。
- OpenCode→模型需要獲准的 endpoint；可為個人環境核准的本機或遠端服務，授權只包含 synthetic data。OpenCode 可能寫自己的 profile/cache/session資料，這不等於可以修改 fixture source。
- 不需 IIS、SQL Server、Windows service、Docker、WSL、Node、Python 或系統 SQLite。若 native loader 指出缺失 runtime component，先核對官方依賴／架構，不自行安裝 machine-wide component 或改用另一 loader。
- 所有測試持久資料放本機普通磁碟。Host DB 拒絕 Temp、任何 Git/SVN ancestor、network drives、已知 reparse point；測試直接使用 Store 可在 Temp 建 DB。正式 durable DB 與 live synthetic source 必須分開。

## 6. Preflight specification（只在獲准個人環境執行）

順序 P0→P4；任一步不符就記錄 blocker，不自動修機器。雖以 inspection 為主，`--version` 仍會執行既有工具，並非公司 execution hold 的例外。

| Gate | 檢查 | Pass evidence／停止條件 |
|---|---|---|
| P0 授權 | source/revision/history、個人執行、provider、資料、安裝授權 | owner 記錄；無授權不取得 source 或執行 |
| P1 SDK/Git | 下列 version commands；核對所有 7 個 csproj | .NET 10 SDK、matching architecture/runtime、Git available；記錄實值 |
| P2 metadata/config | flags、變數存在性（只記 boolean）、path ancestor attributes、existing listeners | 未設 live flag；DB 不在 Temp/repo/link/network；不碰 shared server |
| P3 package/native 靜態 | Stage A restore 後讀 assets/deps/native DLL 路徑 | 有 SQLitePCLRaw provider/native graph、對應 win-x64 e_sqlite3.dll；有檔案不代表已載入成功 |
| P4 獲准 runtime 驗證 | Stage B/C focused tests；live 才另做 server GET | native 真正 loading／DB IO 需執行測試才能確認，不能算 read-only preflight 已完成 |

```powershell
Get-Command dotnet.exe, git.exe | Select-Object Name, Source
dotnet --list-sdks
dotnet --info
git --version
git rev-parse HEAD
git status --short
dotnet nuget list source
# 只記設定是否存在，不輸出值；feed URL 顯示也應審查後才分享。
'MAF_EXECUTION_DB','OPENCODE_BASE_URL','OPENCODE_PROJECT_ROOT','OPENCODE_AGENT','MAF_LIVE_OPENCODE' |
  ForEach-Object { [pscustomobject]@{ Name=$_; Present= -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_)) } }
Get-Item -LiteralPath $repoRoot | Select-Object FullName, Attributes
# Live-only，僅核對該環境已授權的 executable：
Get-Command opencode.exe | Select-Object Name, Source
opencode.exe --version
Get-NetTCPConnection -LocalPort 4096 -State Listen -ErrorAction SilentlyContinue |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

不要把含 personal paths 的 `dotnet --info` 原始輸出直接公開。P2 逐層檢查 root/DB parents 的 ReparsePoint、`.git`／`.svn`，並確認 local drive；不靠一次 root Attributes 推定所有 ancestor 安全。唯讀 ACL inspection 不能證明寫入成功；寫入測試須納入已授權 Stage C，不在 preflight 偷做 smoke。

## 7. Restore、build 與 offline tests

僅在個人環境完成 P0–P2 後執行。由 solution root 操作，每步檢查 `$LASTEXITCODE`；非零停止，不串成忽略錯誤的批次。命令會產生 cache、bin/obj、testhost、Temp、TRX；不是本次公司端執行紀錄。

```powershell
New-Item -ItemType Directory -Path $evidenceDir -Force
dotnet restore MAF.slnx --verbosity minimal
dotnet list MAF.slnx package --include-transitive
dotnet list MAF.slnx package --vulnerable --include-transitive
dotnet build MAF.slnx --no-restore --configuration Debug
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests'
```

如選定 SDK 的 package-list 命令不接受 slnx，對各 csproj 執行同一唯讀 package-list 操作並記錄；不要為此升級 dependencies。任何 NU190x、NU1605、NU1301、native incompatibility 或 build failure 記錄後處理，不能稱 Stage A 成功。NuGet audit 查詢也需網路。

先確保本次 session 未設定 `MAF_LIVE_OPENCODE=1`；若有，先在**個人 session**清除此變數，不改其他程序。新增 39 cases 的 filter 與離線 regression：

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~MAF.Tests.DurableExecutionTests|FullyQualifiedName~MAF.Tests.DurableExecutionStateTests|FullyQualifiedName~MAF.Tests.OpenCodeRecoveryTests|FullyQualifiedName~MAF.Tests.DurableConsoleOutputTests' --logger 'trx;LogFileName=phase2b1-new.trx' --results-directory $evidenceDir
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b1-offline.trx' --results-directory $evidenceDir
```

39 是 source 的 `[Fact]`／`[InlineData]` 計數（29+3+2+5），不是本次 discovery。歷史舊 baseline 112 cases；若 source 未改，offline source 推估為 151，但**只記真正 discovered/executed/passed/skipped/failed counts**，不能照抄預估。Excluded live tests 不等於實際 Skip；不要與歷史三個 live facts 混計。

SQLite focused integration（fake provider，無外部網路，但會 native loading／Temp IO）：

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~MAF.Tests.DurableExecutionTests' --logger 'trx;LogFileName=phase2b1-sqlite.trx' --results-directory $evidenceDir
```

此組包含 new service/store instance、terminal commit 後 fault、before/after intent faults、concurrent owner、state/CAS、provider outage、waiting/cancel、future schema rejection、敏感內容排除。**沒有 separate-process crash runner**；focused tests 通過不能當成 Stage C cross-process 或 Stage D 已通過。

## 8. Live synthetic fixture 與 server

此節僅在 Stage A–D prerequisite 及 live provider 授權完成後使用；Phase 2A baseline 可獨立安排，但不能替代 Phase 2B-1 restart acceptance。不要將 MAF repository 本身當成發送給模型的 workspace。

先從下列全新 synthetic fixture 開始；沒有複製任何公司檔案。保留原 `maf-phase2a-*` 測試契約，名稱不是安全規避方式。若個人 EDR 也阻擋，停止，不改 suffix／位置重試：

```powershell
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('maf-phase2a-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot
Set-Content -LiteralPath (Join-Path $fixtureRoot 'README.md') -Encoding UTF8 -Value '# Synthetic demo: Program.cs prints a greeting.'
Set-Content -LiteralPath (Join-Path $fixtureRoot 'Program.cs') -Encoding UTF8 -Value 'Console.WriteLine("Synthetic greeting");'
```

在 fixture 建立 `opencode.json`，provider/model 使用**該個人環境獨立核准**的配置，secret 經核准方式引用，不放此 JSON。先依官方 [Providers](https://opencode.ai/docs/providers/) 設定所選 provider 的個人 endpoint／authentication（若使用 `/connect`，其 auth storage 也不可提交／分享），再依 [Models](https://opencode.ai/docs/models/) 取得真正的 provider/model ID。下列 model placeholder 必須替換並確認可用，不依賴預設免費 provider 或 fallback；readonly policy 與原 2A 邊界相同：

```json
{
  "$schema": "https://opencode.ai/config.json",
  "model": "<approved-provider-id>/<approved-model-id>",
  "permission": { "*": "deny", "read": "allow", "glob": "allow", "grep": "allow", "external_directory": "deny" },
  "agent": {
    "maf_readonly": {
      "description": "Synthetic read-only validation",
      "mode": "primary",
      "permission": { "*": "deny", "read": "allow", "glob": "allow", "grep": "allow", "external_directory": "deny" }
    }
  },
  "lsp": false,
  "formatter": false,
  "share": "disabled"
}
```

然後由個人 operator 建立普通 Git baseline；不要匯入公司 user config／hooks：

```powershell
git -C $fixtureRoot init --quiet
git -C $fixtureRoot add -- README.md Program.cs opencode.json
git -C $fixtureRoot -c user.name='Synthetic Validation' -c user.email='validation@example.invalid' commit --quiet -m 'synthetic baseline'
$env:OPENCODE_BASE_URL = 'http://127.0.0.1:4096'
$env:OPENCODE_PROJECT_ROOT = $fixtureRoot
$env:OPENCODE_AGENT = 'maf_readonly'
$env:OPENCODE_ALLOW_LOOPBACK_HTTP = '1'
$env:MAF_OPENCODE_TEST_ROOT = $fixtureRoot
$env:MAF_OPENCODE_RESULTS_DIR = Join-Path $evidenceDir 'phase2a-live'
```

在**專用第二個 terminal**進入同一 fixture，套用相同的獲准 OpenCode provider 設定並確認 port 未占用，再啟動：

```powershell
Set-Location '<same-synthetic-fixture>'
opencode.exe serve --hostname 127.0.0.1 --port 4096
```

使用原生 foreground server，operator 可在該 terminal 正常停止自己啟動的程序；不要終止共享 server。OpenCode CLI/UI 啟動有可能自行下載工具／插件或連 provider，必須先核對選定版本及有效設定；不用 `curl | shell`、npm shim 或放寬權限解決問題。

核對 effective agent 的 model override 與實際 message 所報 provider/model，不只抄 root config。選定模型需與 OpenCode 的 readonly tool calling／final response 相容；這是 lifecycle 驗證，不是 coding 品質評分。若無 final response 就保留 Unknown／模型限制，不另送 prompt 或偷偷切到未核准 provider。

### API preflight 與支援範圍

1. 記錄 `opencode.exe --version`；歷史 1.18.35 僅為已驗證基準，不是可宣告的 supported semver range。新版須以實際 `/doc`、readonly checks 及本次結果重新確認。官方 API 參考見 [OpenCode Server](https://opencode.ai/docs/server/)，配置合併與環境引用見 [OpenCode Config](https://opencode.ai/docs/config/)；實際版本的 discovery 才是相容性 gate。
2. 授權的 GET `/global/health` 要有 healthy=true／version；GET `/doc` 是 OpenAPI。需要 POST `/session`、GET `/session/{sessionID}`、POST `/session/{sessionID}/prompt_async`（204、接受穩定 messageID）、GET `/session/status`、GET `/session/{sessionID}/message`、POST `/session/{sessionID}/abort`、GET `/permission`、GET `/question`、GET `/agent`。
3. `/session` request schema 的 `permission.$ref` 必須為 `#/components/schemas/PermissionRuleset` 且該 schema 為 array。Agent 必須有效 deny-by-default，session 保留完全相符的唯讀 policy。缺 API／schema 不符就停止，不改 client 盲目相容。
4. 讀取 GET agent、session、permission、question 可能包含 metadata；只保留 sanitized necessary fields，不 dump 全部 provider response 或 auth headers。

既有 Phase 2A live baseline（會提交數個 synthetic prompts，且 permission/cancellation facts 會對自己 session 要求 abort）：

```powershell
$env:MAF_LIVE_OPENCODE = '1'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2a-home-live.trx' --results-directory $evidenceDir
```

這三個 facts 使用 ephemeral `ExternalWorkflow`，不使用 durable store，即使設了 MAF_EXECUTION_DB 也不會因此驗收 recovery。執行後只在個人 session 清除 opt-in flag；不要帶著 flag 跑無 filter 的 regression。

## 9. Durable Host、crash／restart 操作入口

已有 CLI 可直接執行 build 輸出的 DLL，避免用 `dotnet run` 的 parent/child launcher 混淆 dedicated PID：

```powershell
Set-Location $repoRoot
dotnet src/MAF.Host/bin/Debug/net10.0/MAF.Host.dll --project $fixtureRoot --external-task 'Read README.md and Program.cs only; cite both filenames.' --task-id home-recovery-001 --external-timeout 180 --output json
dotnet src/MAF.Host/bin/Debug/net10.0/MAF.Host.dll --project $fixtureRoot --recover home-recovery-001 --external-timeout 180 --output json
dotnet src/MAF.Host/bin/Debug/net10.0/MAF.Host.dll --project $fixtureRoot --recover-pending --output verbose
```

MAF_EXECUTION_DB 必須仍指向同一個非 Temp 的 DB。第一個命令會提交，後兩個只觀察；它們是三個獨立指令，不應在第一個命令失敗／identity 不明時機械式全部執行。重用 TaskId 是 recovery，不是第二次任務。

**真正 crash checkpoint 的自動化 runner 目前不存在。** 不用 sleep 固定秒數、CLI stdout 最後才輸出的結果或隨機 kill 宣稱 crash window 已驗證。必須依 [Stage C–E plan](../acceptance/PHASE2B1_HOME_VALIDATION_PLAN.md) 補齊 controlled provider + read-only durable checkpoint inspection；同時記錄專用 Host PID／StartTime／command identity，僅終止那個 test-owned process，保持 server 存活。Ctrl+C 會走 abort，不是 abrupt crash 的替代測試。沒有 observer 就記為 Blocked，不偽造重啟成功。

## 10. 輸出、故障排查與安裝 checklist

| 結果 | stdout／退出碼與解讀 |
|---|---|
| Completed | 單筆 JSON 有 result/taskId/workflowRunId/status/handle，官方 WorkflowOutputEvent；exit 0。只表示外部完成，不代表答案獲接受 |
| Partial／Unknown／WaitingForApproval | exit 3／4／5；Unknown 可繼續對原 identity recovery；Waiting 不自動 approve |
| Cancelled | exit 130，須有 remote evidence；abort acknowledgment 不等於取消 |
| Failed | exit 1；送出前 discovery 失敗是 local launch failure，不能宣稱模型 task failure |
| Running/Pending | 既有 exit 1；batch 的 active record 不等於 confirmed failure，讀 status |
| 設定錯誤 | exit 2，例如無 DB 的 recovery、root/scope mismatch、非法 CLI 值 |
| Batch | JSON array；依 TaskId 排序，最多100 nonterminal；退出碼為第一個非0，空集合0。不能僅靠 batch exit code分類所有任務 |

故障處理：

- `net10.0` unsupported：核對 `Get-Command dotnet` 實際位置與 SDK，不以改 target framework／升套件解決。
- NU1903／其他 NuGet warnings：保留 warning ID 與 sanitized dependency graph，核對 bundle override／feed／native package；不關 audit。沒有來源或 feed TLS 問題是 Stage A blocker。
- DllNotFoundException／BadImageFormatException：記錄 SDK/process architecture、resolved SQLitePCLRaw graph、native file path（分享前遮罩使用者名稱）；不要把系統 sqlite DLL 拷入 output 或改 loader 來掩蓋。
- mklink／Temp write／native load 遭 security block：停 affected workflow、保存事件並尋求該環境授權處理；不改 suffix／路徑／程序名規避，不能把跳過測試報成完整 regression。
- UnapprovedWorkspace／SessionPolicyMismatch／UnsupportedServerApi：核對 canonical root、effective agent/session policy 與實際 OpenAPI；不放寬工具。
- Timeout／Unknown：保留同 DB/identity，查原 session。模型仍處理或已完成但 Host 未取回均有可能；沒有 final evidence 不算 Completed。
- Locked DB／active owner：先查是否仍有自己啟動的 Host，不能刪鎖檔搶執行。不要 force terminate unrelated processes。
- Restore/build/test 的 exit code 與 Host task exit code 是不同層次；TRX run aborted 即使有個別 Passed 也不是整輪通過。

安裝／執行前 checklist：

- [ ] Source、history、個人設備及 synthetic provider 授權已確認，沒有依賴公司 endpoint/key/data。
- [ ] 獲准的 supported Windows x64、user-local .NET 10 SDK、Git 在 session PATH；記錄真實版本。
- [ ] 無需 Python／npm／IDE／SQLite service；可選工具未被誤列必需。
- [ ] DB／evidence／fixture 三種目錄分開，非 network／reparse；DB 不在 Temp/repo。
- [ ] 已核對 NuGet sources／resolved graph／vulnerability warnings，未使用公司 credentials。
- [ ] Offline 階段沒有 live flag；理解 Git、junction、Temp、SQLite native 副作用。
- [ ] Live native OpenCode 版本／API／readonly policy／模型及 port 已核准，使用自己啟動的 server。
- [ ] Stage C/D/E runner 缺口先處理；有 checkpoint、HTTP POST counters、PID ownership 及完整 source/index/status baseline。
- [ ] 測試證據只含 synthetic/sanitized 資料；任何 security event 都停止，沒有接受宣稱或自動進入 Phase 2B-2。

## 11. 本次靜態資料可攜性審查與未解前提

範圍：tracked text 檔名／source/config references、明文 credential-like／URL／machine-path patterns、本機根目錄及 `.opencode/` metadata。沒有讀出憑證值、掃完整 Git history、匯出 ignored artifacts 或檢查 binary secrets；不能當成完整 secret/DLP audit。

| 觀察 | 風險與處理 |
|---|---|
| Host/provider source 沒有找到必須使用的公司 endpoint／帳號；endpoint 由 environment 注入 | 可用新 synthetic source 與個人授權 provider。未找到不代表所有歷史／設定無機密 |
| tracked 安全事件文件包含原公司 workstation process path／事件 hash；Phase 2A/results/handoff 有真實 session/run IDs、模型／provider 歷史 | 證據保留，不重複敏感值到本指南；需 owner 審查是否可移轉，不能整包 clone 當成已核准 |
| `docs/acceptance/*.txt`、`review-phase2.json` 等含模型生成的 repository 分析／diff | 可能是內部 source/log derivative；不是 clean setup 必需，需資料分類；本次不刪除／修改 |
| Credential-like pattern 命中 `docs/acceptance/diff-session.txt` 22/24 及 ToolTests／OpenCodeClientTests 的 literals | 測試有 synthetic credential markers；不能由 regex 推定全為真 key 或全為假值。歷史文件候選需 owner 確認，不複製命中值；未確認任何真實 credential 外洩 |
| 未追蹤 `.opencode/`（含 package metadata／node_modules／agent tooling）、CLI enhancement 文件仍存在 | 不 stage、不移轉、不執行；不是 MAF 依賴。未審查其內容，不能保證安全 |
| `.env*`、bin/obj、TestResults、SQLite DB/sidecars/lock dirs 被 ignore | Ignore 不等於可傳輸；可能有 endpoint、source、auth、paths、sessions。個人環境重建 synthetic artifacts，不複製公司 cache／logs／DB |
| 沒有 tracked NuGet.Config／global.json／package lock | 來源及 SDK／transitive graph 可漂移；需個人環境記錄，不複製機器 NuGet credentials 或 policy |
| 公司 endpoint／model/profile 曾可用是歷史，不是個人使用授權 | 不需公司 VPN／AI PoC；若唯一獲准 provider 仍是公司專用，Stage E 保持 Blocked，Stage A–D 可在 source獲准且無外部模型條件下另安排 |

未解前提：source/history 可移轉範圍、個人設備安裝／執行授權、真正可用的 SDK/package graph、OpenCode/model 授權及版本相容性、cross-process/checkpoint/native-integrity runner 缺口。逐項 gate 與 evidence template 見 [Home validation plan](../acceptance/PHASE2B1_HOME_VALIDATION_PLAN.md)。本 handoff 不解除公司 security hold。
