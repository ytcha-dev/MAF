# PersonalLab OpenCode downstream configuration boundary

Status: offline descriptive binding only. No live activation, operational factory or public route.

## Existing ownership and the bounded addition

The Independent PersonalLab runtime already owns its offline child/journey and HTTP chain,
uses the existing OpenCodeClient/executor and official Workflow, and enforces Lab admission,
flushed journal intent, original-response receipt, read-only rules and exact model selection.
Lifecycle Closure tracks underlying native work, supports local cancellation/bounded drain,
and rejects active disposal. None of those mechanisms are replaced by this increment.

`LabOpenCodeExecutionPlan` references the exact existing `LocalExecutionPreparation` object.
OperationId and Task/WorkflowRun identities are not allocated again. Its immutable expected
description reuses `HarnessRecipientBinding` for configuration ID/provider/model and adds
the canonical HTTPS API base endpoint, a declared configuration SHA-256 digest and explicit
override/fallback/auxiliary-outbound descriptions. Only PersonalLab preparations may bind.
Creating or comparing a plan does not claim an admission, touch the journal or inspect files.
Existing preparation creation still validates metadata of the chosen existing workspace.

This is a separate description because preparation intentionally lacks a downstream endpoint
or configuration digest. Existing preparation, admission, protocol, journal and production
interfaces remain unchanged. There is no second preparation pipeline or readiness framework.

## Exact validation contract

* `Bind` requires configuration ID/provider/model equality with the existing preparation.
  Missing, default or alternate selection is not resolved from environment/agent defaults.
* The endpoint must be an absolute HTTPS URI of at most 1024 characters. User information,
  query/fragment delimiters, backslashes, surrounding whitespace and unsafe control/format
  characters are rejected. `Uri.AbsoluteUri` canonicalizes scheme/host/default port/path.
  Path and non-default port remain significant; `/v1` and `/v1/` are distinct.
* ConfigurationDigest must be exactly 64 hexadecimal characters; comparison uses lowercase.
  It is the **supplied expected digest**, not a hash MAF computed from effective runtime config.
  Raw settings, credentials and provider configuration payloads are not accepted or retained.
* Overrides, Fallback and AuxiliaryOutbound each default to Unknown. Only explicit None is
  currently supported. Unknown, Present and undefined enum values fail both binding and
  comparison. This milestone does not model or approve an allowlist of additional flows.
* `DemandConfiguration` validates/canonicalizes another description and compares every field
  to the expected value. A rejected comparison cannot replace the binding.
* BindingDigest is SHA-256 over compact System.Text.Json UTF-8 JSON in fixed property order:
  version=1, configurationId, provider, model, endpoint, configurationDigest, overrides,
  fallback, auxiliaryOutbound. The last three values are the string `none`. It excludes
  operation identity so equivalent descriptions have the same deterministic binding digest.
  It does not supersede the existing preparation/journal fingerprint or authenticate content.
* Errors use fixed codes; plan/configuration ToString suppress values. Typed getters are
  descriptive configuration access, not a safe logging/serialization interface. Do not dump
  preparation, recipients or endpoint values into diagnostics.

## Expected configuration is not effective runtime evidence

Both descriptions are caller-supplied offline values. Matching them, or matching either
digest, does not prove what an OpenCode process loaded, whether files/environment changed,
which process owns the loopback port, or where its traffic went. Declaring None does not
prove absence of runtime overrides or auxiliary calls. There is no `Attested` switch, proof
issuer, grant object or new authorization state. The fixed diagnostic says NotRuntimeEvidence
and ExecutionBlocked even for a valid matching plan.

The existing Lab boundary pins `providerID`/`modelID` in prompt_async and checks assistant
labels. Those labels do not attest provider custom baseURL, server-side fallback, proxy
routing, plugins, title/small-model/compaction inference or other downstream tools/flows.
The plan also does not establish free-model eligibility, availability or a cost ceiling.
Future operational evidence must independently address every declared assumption.

## Separate authority domains remain intact

Independent PersonalLab uses cooperative, bounded offline admission and its own journal;
it does not borrow genuine or synthetic ProductionProtected caller/policy/approval authority.
The plan neither implements those authority interfaces nor upgrades Lab receipt authenticity.
`DemandExecution`, `DemandHarnessRecipient`, Network denial, `ProtectedRuntimeRequired`,
`ProductionTrustUnavailable`, inspection-only reopen and no-retry semantics are unchanged.
The production profile still needs its separate genuine trust/custody/activation prerequisites.
An Owner's acceptance of bounded PersonalLab risks must not be presented as production trust.

## Next separately reviewed Process/Transport milestone

Before live delegation, establish an explicit live intention/recipient/data-flow scope that
cannot be derived from this plan or an offline admission. Decide whether the cooperative
child/configuration trust limit is acceptable or stronger externally enforced egress evidence
is required. Do not manufacture attestation from matching metadata.

A future owned-child/transport design must verify native executable identity/version, exact
launch arguments, isolated configuration/environment loading, configuration custody through
use (including change/TOCTOU handling), all model selectors/auxiliary flows and provider endpoints.
It must bind health/schema/effective Agent checks to that owned server, handle occupied-port
and startup races, constrain redirect/proxy/retry behavior, and dispose/terminate only resources
it owns. Native PCA and Reviewer remain separately bound recipients for a complete journey.

Retain current cancellation/drain ownership. Local cancellation, drained tasks, process exit
and Abort acknowledgement are not remote terminal evidence. Lab remote cancellation/recovery
needs its own explicit design; no automatic Abort, retry, replacement session or journal resume.
Network availability/endpoint clearance is an independent deployment gate. Corporate holds
remain unchanged. Successful offline binding authorizes none of these live effects.

## Validation and filesystem scope

New `LabOpenCodeExecutionPlanTests` use existing test-output directory metadata for preparation;
they allocate no files/directories, sockets, processes, HTTP/model clients, journals or stores.
Tests cover exact identity, canonical/golden digest, every bound-field mismatch, incomplete
model/configuration, all unsupported flow states, secret-safe diagnostics and unchanged
PersonalLab/Production/Network/public-submit denial. Existing regression suites retain their
documented explicitly owned filesystem/process fixtures; the new pure tests add none.

## Recorded validation — 2026-10-22

Baseline main/local HEAD: `483b26b7ac0a8dfbccd53f6e798273b4dca0c8ad`, initially clean.
Dedicated branch: `codex/lab-downstream-configuration-binding`. SDK 10.0.402, cached dependencies.
Build command: `dotnet build MAF.slnx --no-restore --disable-build-servers -m:1`.
Final source build: 0 warnings / 0 errors (9.72s).

Initial plan-only selection: 40 passed / 0 failed / 0 skipped (96ms).
An independently calculated golden binding-digest assertion was then added to an existing
case. The final scoped and full runs both include all 40 final plan rows and that assertion:

| Selection | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Final scoped Lab/preparation/boundary/lifecycle/protected isolation | 263 | 0 | 0 |
| Final full offline suite (1627 total, 3m49s) | 1624 | 0 | 3 |

Scoped filter: FullyQualifiedName contains LabOpenCodeExecutionPlanTests, PersonalLab,
LabOpenCodeBoundaryTests, IndependentLabRuntimeTests, BoundNativeRecipientTests,
RuntimeLifecycleTests, RuntimeEnforcementTests or LocalExecutionPreparationTests (OR).
Both use `dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore
--disable-build-servers -m:1 --logger trx`, with `MAF_LIVE_OPENCODE=0`; scoped session bound
240000ms, full 600000ms. Full has no filter. Results remain outside the repository at
`/workspace/work/maf-downstream-tests`; TRX counters and named skipped cases were inspected.
All three skips are exactly the pre-existing opt-in OpenCodeLiveTests; none were newly
suppressed. No failed test sessions occurred. Formal network permission was granted for
VSTest's local communication; no model/server or Environment configuration was activated.
Compared with the recorded 1584/0/3 baseline, 40 passing rows were added. After validation,
only this evidence and a source-comment clarification changed; runtime guards are untouched.
