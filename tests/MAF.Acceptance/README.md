# Phase 2B-1 process acceptance

This explicitly invoked .NET 10 executable is test-only. It reuses the real Core/SQLite store and launches the built production Host directly; it does not add a production loop or service. `MAF.slnx` builds it, but `dotnet test` does not run its crash/live experiments automatically.

From the repository root, after build and offline tests:

```powershell
dotnet tests/MAF.Acceptance/bin/Debug/net10.0/MAF.Acceptance.dll run src/MAF.Host/bin/Debug/net10.0/MAF.Host.dll '<new-absolute-local-run-directory>'
```

Choose a new ordinary local directory outside Temp and every source repository. The production Host enforces this for its DB. Source and DB are separate children of the run directory. AppData may be virtualized differently by MSIX and native tools; use an ordinary non-AppData directory for live interoperability. All persisted artifacts are synthetic and retained for inspection; nothing is automatically deleted or reverted.

Stage C commits six checkpoints in independent helper processes. A second child must fail to acquire ownership; the driver kills only the captured writer, then another child must acquire the retained lock file. A fresh process reads through the actual store and checks SQLite integrity/schema/row count and snapshot consistency using a ReadOnly connection. Identity, state and version must survive unchanged. Invalid transitions and duplicate INSERT are rejected before each checkpoint signal.

Stage D runs seven isolated controlled-provider scenarios. The fake provider stays in the driver, independently of the killed Host. It stores supplied messageID and counts actual session/prompt POST handlers. The driver waits for accepted submission plus a committed DB checkpoint, captures PID/StartTime/executable identity, kills that exact process without a process tree or Ctrl+C, and recovers in a new Host. Acknowledgment loss holds the accepted POST response until fixture shutdown. Other scenarios cover active recovery, completion while stopped, outage, bounded running timeout, permission pending and provider-confirmed cancellation. Recovery must preserve all correlation and request fingerprint, retrieve the expected body when Completed, and leave POST counts at 1/1 with zero aborts.

For separately authorized personal/free-provider live execution:

```powershell
dotnet tests/MAF.Acceptance/bin/Debug/net10.0/MAF.Acceptance.dll live src/MAF.Host/bin/Debug/net10.0/MAF.Host.dll '<new-absolute-local-run-directory>' '<absolute-opencode-launcher>' opencode/big-pickle
```

The launcher accepts a native `.exe` or an installed npm `.cmd`, `.ps1`, or extensionless wrapper pointing to `node_modules/opencode-ai/bin/opencode.exe`. It executes the native child for precise PID ownership and does not invoke the script, bypass execution policy, modify PATH or install anything. Unknown layouts fail explicitly; launcher parsing has focused xUnit tests.

Live execution creates a disposable Git repo containing only synthetic README, Program and config; the config declares `$schema` before baseline to prevent OpenCode's startup insertion. Read-only permissions deny shell, writes and external directories. `--pure` disables external plugins; fresh XDG config/data/cache/state directories isolate personal profiles. The data directory uses the standard `.local/share` suffix expected by the existing narrow tool-output-cache validation. Provider API-key environment variables and inherited OpenCode overrides are removed from this child; only the explicit synthetic config and free provider are selected. No original profile is changed.

A transparent loopback proxy counts each session/prompt POST before forwarding once, without redirects, custom retry, header/body logs or policy rewriting. All Host traffic uses that stable origin; direct observer traffic is GET-only. Unavailability returns 503 at this test-owned boundary, preserving the real server/session and provider scope. It models Host-to-server unavailability, not an actual model-service outage. Three live variants cover active crash (one-second recovery may be Unknown then later Completed), completion while stopped, and unavailable recovery followed by restoration. Evidence records actual model IDs, process identities, durable snapshots, counters, status and complete source/index/status manifests. Missing final evidence is Not Demonstrated; failed invariants stop the run.

`evidence.json` / `live-evidence.json` in the run directory contain local paths and should not be published unreviewed. Commit only sanitized evidence. Each wait has a deadline; the holding seed expires after 60 seconds. A timeout or thrown assertion fails the executable and preserves prior artifacts. Partial evidence files alone do not mean the entire run passed. Endpoint security alerts require stopping the workflow, without renaming or relocating to evade the block.

These tests prove process-level crash/restart for the observed checkpoints, not power-loss durability, distributed ownership, exactly-once remote delivery, arbitrary OpenCode versions, or corporate endpoint compatibility. They do not implement Phase 2B-2.
