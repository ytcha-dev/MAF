# MAF — Product & Engineering Roadmap

> Status: Living document · Version: 0.1 · Updated: 2026-10-14  
> Project: `ytcha-dev/MAF` · Owner decision: **Workflow-first (A), then Hybrid (C)**

## 1. Vision

MAF is an extensible **multi-agent orchestration platform** built initially with Microsoft Agent Framework (.NET). Its purpose is to coordinate specialist agents, existing harnesses, and knowledge services—not to recreate mature coding tools, vector databases, or model runtimes.

**Principle: Build the orchestration. Integrate the execution. Reuse the infrastructure.**

The name MAF refers to this repository/project; Microsoft's Agent Framework is a chosen implementation technology, not a permanent requirement. Preserve boundaries so that agents, executors, models, protocols, and eventually orchestration engines can be replaced.

## 2. Non-goals

- Reimplement OpenCode's coding loop, tool permissions, terminal operations, context compaction, or file writer.
- Build a full vector database or RAG platform merely to demonstrate retrieval.
- Require every external service to be wrapped in another LLM-powered `ChatClientAgent`.
- Adopt A2A, MCP, or any interface simply for fashion or architectural symmetry.
- Give LLMs unrestricted decision, execution, filesystem, network, or approval privileges.
- Promise production readiness based solely on a successful local demonstration.

## 3. Roadmap at a glance

| Stage | Goal | Main deliverable | Exit gate |
| --- | --- | --- | --- |
| Phase 1 — Agent fundamentals | Understand agent internals | PCA + independent Review Agent, evidence-based tests | Can explain model / agent / tool / session / harness boundaries; basic runs verified |
| Phase 2A — Workflow-first integration | Prove delegation to an external harness | Official MAF workflow + OpenCode adapter/executor | Repeatable read-only E2E, state/cancellation/error validation |
| Phase 2B — Workflow hardening | Make external execution trustworthy | Persistent mapping, reconnect, approval handling, observability | No silent duplicate execution; known failure paths tested |
| Phase 3 — Hybrid orchestration | Introduce bounded LLM decisions | Planner/router proposing typed decisions under runtime policy | Deterministic gates remain authoritative; measured improvement |
| Phase 4 — Ecosystem expansion | Plug-and-play services and cross-framework collaboration | Additional providers, optional A2A and knowledge retrieval | New backend through adapter with contract tests |
| Phase 5 — Deployment maturity (optional) | Operate beyond localhost | Secure hosting, multi-user isolation, telemetry, audit | Security/operational readiness accepted explicitly |

Stages are outcome gates, **not calendar commitments**. Future stages may be reordered, split, deleted, or replaced as tools mature.

## 4. Phase 1 — Learn by building (current baseline)

**As of 2026-10-14:** The repository has a .NET Console Host, `IChatClient`, a PCA built using `ChatClientAgent`, `ProjectTools` with bounded read-only repository access, and a Review Agent service with structured findings. The repository includes test and live-model evaluation reports; these are evidence of selected checks, **not** a blanket quality certification.

Keep PCA and Reviewer as reference implementations and regression baselines. Study:

1. `IChatClient` versus `AIAgent` / `ChatClientAgent`.
2. Function calling and trust boundaries (`AIFunctionFactory`, actual tool execution).
3. Sessions, context growth, model errors and cancellation.
4. Structured output, provenance, limitations and reproducible agent evaluations.

**Policy:** Stop broadening PCA until a measured need justifies a change. Use a bounded evaluation set; separate stale external knowledge from incorrect repository reasoning. No automatic transfer of business code to an unapproved model endpoint.

## 5. Phase 2A — A: fixed, deterministic Workflow-first

**First integration:** `MAF Workflow → OpenCodeExecutor → OpenCode Server → Result`.

- Use **official MAF Workflow APIs** (validate actual pinned SDK version first).
- Make OpenCode an **external long-running executor**, not a surrogate `ChatClientAgent` by default.
- Communicate through OpenCode's supported API, maintaining server session identity and progress.
- Start with tasks that do not change files; prefer test-owned fixtures.
- Distinguish connection timeout from remote cancellation and completion.
- Do not add PCA/Reviewer to the first E2E until this foundation passes.

**Gate 2A:** At least three successful repeated task delegations; verified behavior for server unreachable, delayed completion, cancellation, unknown result, permission request, and reconnection; no unauthorized file mutations. See `docs/plans/PHASE2A_EXECUTION_PLAN.md` for exact cases.

## 6. Phase 2B — Reliability before autonomy

Introduce only after Phase 2A evidence:

- Durable mapping of WorkflowRunId ↔ ExternalExecutionId ↔ OpenCode SessionId.
- Idempotent launch/reconciliation strategy and restart recovery.
- Explicit permissions (read-only, write-approved, prohibited) and human approvals.
- Event/trace correlation, redaction, token/cost/time budgets.
- Failure and partial-completion semantics; explicit diff/artifact provenance.
- PCA → OpenCode → Review → Human approval as a fixed integration pipeline.

**Gate 2B:** Restart while external run is active; reconnect to *same* remote session without duplicate prompt submission; maintain audit trail and approval boundary.

## 7. Phase 3 — C: Hybrid orchestration

A deterministic workflow engine remains responsible for security, orchestration state, policy checks and allowed transitions. An LLM may **propose**:

- Which allowed specialist executor should handle the task.
- Whether to request more context or hand off for review.
- How to decompose a user-approved objective into bounded subtasks.

The runtime must validate decisions against typed schemas, whitelisted capabilities, budgets, max depth/iterations, idempotency rules and approval requirements. LLMs **cannot** grant themselves permissions, modify policies, mark remote work complete without evidence, or run unbounded retries.

Start with one bounded routing decision, then compare Hybrid to fixed Workflow on correctness, cost, latency and reliability. Adopt only if the measurable benefit justifies the complexity.

## 8. Future integration candidates (not implementation commitments)

| Candidate | Why | Adoption trigger |
| --- | --- | --- |
| A2A (Agent-to-Agent) | Interoperate with heterogeneous remote agents via common protocol | At least one actual partner speaks A2A or standardized exposure brings clear benefit |
| OpenCode API adapter | Delegate complete coding/repository tasks | **Immediate Phase 2A choice** |
| Open WebUI Knowledge / RAG | Retrieve documents and domain context | Benchmarks identify retrieval recall as a limitation |
| Additional agent runtimes | Avoid vendor or framework lock-in | A second real execution backend exists |
| MCP | Expose tools to models or agents | Tool interoperability is needed; MCP is not a substitute for full task orchestration |
| Workflow checkpoint storage | Restore internal workflow progress | Long-running durable workloads demand it; still reconcile external session separately |
| Alternate orchestration engine | Long-term portability | Clear functional/operational reason, supported by contract boundaries |

Protocol rules: **A2A = agent-to-agent communication; MCP = tool/context exposure; direct provider API = harness-specific integration**. They are not interchangeable. A custom gateway is optional; never assume OpenCode natively exposes A2A without verification.

## 9. Architectural invariants

1. **Execution separation:** coding harness owns editing/execution; MAF coordinates it.
2. **Adapter separation:** provider-specific JSON/API objects never leak into generic workflow contracts.
3. **State separation:** MAF sessions, workflow runs and OpenCode sessions are distinct.
4. **Security:** least privilege; no automatic trust of repository content as instructions; no secret logging.
5. **Observability:** each result records origin, run identity, final/partial status and limitations.
6. **Budgeting:** limit retries, concurrency, task duration and model/tool usage.
7. **Portability:** reuse where appropriate; do not force all executors into `AIAgent`.
8. **Evidence:** explicitly label demonstrated behavior vs assumptions vs future proposal.
9. **Human control:** retain approval gates for writes and high-risk operations.
10. **Learning:** maintain a minimal educational example of native MAF Agent even if a replacement is later selected.

## 10. Decision log and document maintenance

- **ADR-001 (2026-10-14):** Build vs integrate — use existing harnesses rather than rebuild coding/RAG internals.
- **ADR-002 (2026-10-14):** A before C — Workflow-first before Hybrid.
- **ADR-003 (proposed):** OpenCode via custom Workflow Executor and provider adapter for first PoC; A2A remains optional.

These are decision *records*, not claims that matching ADR files already exist. When decisions change, record **context, alternatives, decision, consequences, evidence, revisit trigger**. Update the roadmap; do not erase earlier history.

### Current next action

Execute `docs/plans/PHASE2A_EXECUTION_PLAN.md`, referring to `docs/architecture/EXTERNAL_HARNESS_ARCHITECTURE.md`. Codex must read and reconcile the latest repository before implementing anything. This roadmap alone does not authorize a write-enabled external run.
