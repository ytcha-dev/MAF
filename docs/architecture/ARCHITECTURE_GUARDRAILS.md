# MAF — Architecture Guardrails

> Status: Owner-level architectural guardrails. These describe intended boundaries; they do **not** imply that unimplemented components are available or authorize new execution, connectivity, or privileges.

1. **Orchestrator owns coordination.** MAF controls typed workflows, task identity, budgets, handoffs, recovery, and lifecycle decisions; it does not rebuild a harness's internal agent/tool loop.
2. **Harness owns execution.** OpenCode, Codex, Claude Code, and native agents perform their own bounded work behind explicit adapter contracts; provider-specific protocols stay outside orchestration policy.
3. **Policy owns authorization decisions.** Every entry point and external side-effect boundary must enforce current caller, operation, connector, and resource scope. An enabled or healthy connector is not an authorization grant.
4. **Trusted authority owns human identity and approval authenticity.** An approval requires a trustworthy operator identity and exact operation binding. Agent-supplied names, CLI flags, same-user OS identity, and database rows alone do not prove human consent.
5. **Persistence owns durable state and atomicity.** Stores preserve evidence, state transitions, and local transaction invariants; they must never launch workflows, submit prompts, or treat approval records as executable tickets. Remote HTTP calls are not part of a SQLite transaction.
6. **Registry owns connector availability, not permission grants.** Installed, configured, enabled, and healthy are separate signals. External connectors start disabled; disabling must block new dispatch through every entry point, not merely hide a UI option.
7. **LLM recommends; deterministic runtime enforces.** A future Hybrid planner may propose only schema-validated actions from the currently permitted capability set. It cannot grant permissions, enable connectors, approve itself, or retry ambiguous submissions.

## Delivery discipline

- Build **deterministic Workflow A first**, then add bounded Hybrid decision-making (C) only after reliable fixed-workflow baselines.
- Keep PCA and Reviewer as distinct native capabilities; verify PCA → external harness → Reviewer using typed handoffs before general dynamic routing.
- Prefer minimal, replaceable contracts over premature IAM, plugin marketplace, distributed orchestration, or generic RAG/tool-loop infrastructure.
- Preserve honest evidence boundaries: `Implemented` is not `Tested`, `Completed` is not `Accepted`, and `Unknown` is not `Failed` or `Cancelled`.
- Existing corporate endpoint restrictions and data-governance rules remain in force; these guardrails do not relax them.

See [ROADMAP](../ROADMAP.md), [ADR-003](ADR-003_OPENCODE_WORKFLOW_EXECUTOR.md), [ADR-004](ADR-004_DURABLE_EXECUTION_RECOVERY.md), and [ADR-005](ADR-005_POLICY_APPROVAL_CANCELLATION.md) for stage-specific decisions and evidence.
