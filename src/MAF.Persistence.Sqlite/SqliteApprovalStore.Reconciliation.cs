using MAF.Core;
using System.Text.Json;
using MAF.Core.Policy;
using Microsoft.Data.Sqlite;
using static MAF.Persistence.Sqlite.DeliveryStorageFormat;

namespace MAF.Persistence.Sqlite;

public sealed partial class SqliteApprovalStore : IReconciliationCoordinator
{
    public async Task<ReconciliationSession> BeginReconciliationAsync(string deliveryId, string taskId, ReconciliationInputs inputs,
        ITrustedCallerContext caller, IExecutionOwnership owner, CancellationToken token = default)
    {
        Require(RequiresSessionReceipts, "ApprovalSchemaV4Required"); Id(deliveryId); Id(taskId);
        await using var use = await SqliteExecutionStore.UseAdmissionOwnershipAsync(path, taskId, owner, token, ExecutionStoreProfile.ExplicitV4);
        await using var lease = await authority.AcquireAsync(token); var policy = Candidate();
        var started = await GuardedTransaction(policy, async (c, tx) =>
        {
            DurableExecution? execution;
            try { (execution, _, _) = await CancelTarget(c, tx, taskId, token); }
            catch (Exception error) when (error is InvalidOperationException or JsonException) { throw new InvalidOperationException("InspectNotAuthorized"); }
            Require(execution is not null && caller is not null, "InspectNotAuthorized");
            var e = execution!;
            var op = new PolicyRequest(inputs.AuthorityId, Actor(caller!), inputs.OperationId, taskId, e.WorkflowRunId,
                e.Handle.ExecutionId, e.Handle.ExternalRequestId!, e.RequestFingerprint, inputs.Capability.ConnectorId,
                inputs.Capability.Capability, PolicyOperation.InspectExistingExecution, inputs.Scope, inputs.Epoch, inputs.Version,
                ProviderSessionId: e.Handle.ExternalSessionId);
            AuthorizeInspection(policy, op, inputs.Capability, caller!, e);
            var row = await ReconciliationTarget(c, tx, deliveryId, op, token);
            Require(row.Outcome == 0 || e.IsTerminal && e.State.ToString() == Outcomes[row.Outcome], "TerminalLedgerConflict");
            var control = await Control(c, tx, taskId, token);
            return (e, op, row, context: caller!.AuthenticationContextId, control!.Version);
        }, token);
        return new(started.e, started.Version, (observation, t) => CaptureReconciliation(deliveryId, inputs, caller, owner,
            started.e, started.op, started.row, started.context, started.Version, observation, t));
    }
    void AuthorizeInspection(PolicySnapshot policy, PolicyRequest op, CapabilityDescriptor capability, ITrustedCallerContext caller, DurableExecution e)
    {
        Metadata(op);
        var decision = new DeterministicPolicyEvaluator(new ImmutablePolicySource(policy), clock).Evaluate(op, caller);
        Require(op.Operation == PolicyOperation.InspectExistingExecution && op.Capability == PolicyCapability.ExecutionInspection
            && capability.SupportedOperations.SequenceEqual([PolicyOperation.InspectExistingExecution])
            && decision.Outcome == PolicyOutcome.Allow && decision.BindingDigest == OperationBinding.Digest(op, capability)
            && authority.CanControl(caller, op) && authority.MatchesExecutionBinding(op, e)
            && op.Scope.Access == ResourceAccess.ReadOnly && op.Scope.ResourceId == e.ProjectRoot && op.Scope.ProviderScopeDigest == e.ProviderScope
            && op.WorkflowRunId == e.WorkflowRunId && op.ExecutionId == e.Handle.ExecutionId && op.MessageId == e.Handle.ExternalRequestId
            && op.ProviderSessionId == e.Handle.ExternalSessionId && op.RequestFingerprint == e.RequestFingerprint, "InspectNotAuthorized");
    }
    async Task<DeliveryStorageRecord> ReconciliationTarget(SqliteConnection c, SqliteTransaction tx, string id, PolicyRequest op, CancellationToken token)
    {
        var receipt = await ReadSessionReceipt(c, tx, op.TaskId, token);
        Require(receipt?.Status == SessionReceiptStatus.Verified && receipt.Response?.ProviderSessionId == op.ProviderSessionId, "InspectVerifiedReceiptRequired");
        var original = receipt!.Intent.Operation;
        Require(original.TaskId == op.TaskId && original.ExecutionId == op.ExecutionId && original.WorkflowRunId == op.WorkflowRunId
            && original.MessageId == op.MessageId && original.RequestFingerprint == op.RequestFingerprint && original.ConnectorId == op.ConnectorId
            && original.Scope with { BudgetTicks = op.Scope.BudgetTicks } == op.Scope, "InspectBindingMismatch");
        var row = await ReadDeliveryStorage(c, tx, id, token) ?? throw new InvalidOperationException("DeliveryReservationRequired");
        Require(S(row.Intent.Operation, "TaskId") == op.TaskId && S(row.Intent.Operation, "ExecutionId") == op.ExecutionId
            && row.Intent.ProviderSessionId == op.ProviderSessionId, "InspectDeliveryMismatch");
        return row;
    }
    async Task<ReconciliationResult> CaptureReconciliation(string deliveryId, ReconciliationInputs inputs, ITrustedCallerContext caller,
        IExecutionOwnership owner, DurableExecution expected, PolicyRequest op, DeliveryStorageRecord expectedDelivery,
        string context, long controlVersion, ExternalObservation observation, CancellationToken token)
    {
        await using var use = await SqliteExecutionStore.UseAdmissionOwnershipAsync(path, op.TaskId, owner, token, ExecutionStoreProfile.ExplicitV4);
        await using var lease = await authority.AcquireAsync(token); var policy = Candidate();
        var result = await GuardedTransaction(policy, async (c, tx) =>
        {
            Require(caller.AuthenticationContextId == context, "InspectCallerChanged");
            var (current, _, _) = await CancelTarget(c, tx, op.TaskId, token);
            Require(current is not null, "InspectMissingExecution"); AuthorizeInspection(policy, op, inputs.Capability, caller, current!);
            var row = await ReconciliationTarget(c, tx, deliveryId, op, token);
            Require((await Control(c, tx, op.TaskId, token))?.Version == controlVersion, "ControlConflict");
            Require(row.Version == expectedDelivery.Version && row.IntentDigest == expectedDelivery.IntentDigest, "DeliveryVersionConflict");
            var evidence = observation.TerminalEvidence;
            if (evidence is null)
            {
                if (current!.IsTerminal) return new(ExternalExecutionStatus.Unknown, false, "TerminalEvidenceChanged", row.Observation?.TerminalEvidenceDigest);
                return new ReconciliationResult(observation.Status is ExternalExecutionStatus.Running or ExternalExecutionStatus.WaitingForApproval
                    ? observation.Status : ExternalExecutionStatus.Unknown, false, "EvidenceInsufficient");
            }
            Require(evidence.ProviderId == current!.Handle.Provider && evidence.ProviderScope == current.ProviderScope
                && evidence.SessionId == op.ProviderSessionId && evidence.OriginalMessageId == op.MessageId && evidence.ParentMessageId == op.MessageId
                && evidence.Outcome == observation.Status && evidence.ObservedAtTicks >= row.Intent.IntentAtTicks
                && evidence.ObservedAtTicks <= clock.GetUtcNow().UtcTicks, "TerminalBindingMismatch");
            var outcome = evidence.Outcome switch
            { ExternalExecutionStatus.Completed => 1, ExternalExecutionStatus.Partial => 2, ExternalExecutionStatus.Failed => 3, ExternalExecutionStatus.Cancelled => 4, _ => 0 };
            Require(outcome != 0 && current.SubmissionPhase >= SubmissionPhase.PromptIntended, "TerminalEvidenceRequired");
            var terminal = new Terminal(evidence.ProviderId, evidence.ProviderScope, evidence.SessionId, evidence.OriginalMessageId,
                evidence.ObservedMessageId, evidence.ParentMessageId, evidence.FinishReason, evidence.ErrorCode, Outcomes[outcome], evidence.ObservedAtTicks);
            if (row.Outcome != 0)
            {
                if (!current.IsTerminal || current.State.ToString() != Outcomes[row.Outcome])
                    return new(ExternalExecutionStatus.Unknown, false, "TerminalLedgerConflict", row.Observation!.TerminalEvidenceDigest);
                var old = row.Observation!.TerminalEvidence!;
                if (row.Outcome != outcome || terminal with { ObservedAtTicks = old.ObservedAtTicks } != old)
                    return new(ExternalExecutionStatus.Unknown, false, "TerminalEvidenceChanged", row.Observation.TerminalEvidenceDigest);
                return new(observation.Status, true, "AlreadyRecorded", row.Observation.TerminalEvidenceDigest);
            }
            if (current.IsTerminal && current.State.ToString() != Outcomes[outcome])
                return new(ExternalExecutionStatus.Unknown, false, "TerminalEvidenceChanged");
            Require(Json(current) == Json(expected), "ExecutionVersionConflict");
            var now = clock.GetUtcNow();
            if (!current.IsTerminal)
            {
                var executionAt = now < current.UpdatedAt ? current.UpdatedAt : now;
                var next = current with { State = Enum.Parse<DurableExecutionState>(Outcomes[outcome]), Version = current.Version + 1,
                    UpdatedAt = executionAt, CompletedAt = executionAt, ReconciledAt = executionAt, ErrorCode = observation.ErrorCode };
                DurableExecutionTransitions.Validate(current, next);
                Require(await ApprovalDatabase.Execute(c, tx, "UPDATE executions SET version=$v,terminal=1,snapshot=$j WHERE task_id=$t AND version=$old AND snapshot=$before", token,
                    ("$v", next.Version), ("$j", Json(next)), ("$t", op.TaskId), ("$old", current.Version), ("$before", Json(current))) == 1, "ExecutionVersionConflict");
            }
            cancellationCheckpoint?.Invoke("ReconciliationExecutionWritten");
            var receipt = (await ReadSessionReceipt(c, tx, op.TaskId, token))!;
            var at = now.UtcTicks; var version = row.Version + 1;
            var audit = await AbortAudit(c, tx, op, receipt.Intent.ApprovalId, version, at, token);
            var digest = TerminalDigest(terminal);
            var stored = new Observation(deliveryId, row.IntentDigest, DeliveryElement(op), DeliveryElement(inputs.Capability), context,
                PolicySnapshotBinding.Digest(policy), Stages[row.Stage], Outcomes[outcome], "None", at, row.Observation?.AcknowledgementAtTicks,
                op.MessageId, terminal, digest, audit);
            Require(await ApprovalDatabase.Execute(c, tx, """
                UPDATE cancellation_deliveries SET version=$v,remote_outcome=$outcome,last_observed_at_ticks=$at,
                observation_digest=$digest,observation_json=$json,last_audit_sequence=$audit
                WHERE delivery_id=$d AND version=$old AND intent_digest=$intent
                """, token, ("$v", version), ("$outcome", outcome), ("$at", at), ("$digest", Digest(stored)),
                ("$json", SessionReceiptFormat.Write(stored, 8192)), ("$audit", audit), ("$d", deliveryId), ("$old", row.Version), ("$intent", row.IntentDigest)) == 1,
                "DeliveryVersionConflict");
            cancellationCheckpoint?.Invoke("ReconciliationEvidenceWritten");
            var after = (await ReadDeliveryStorage(c, tx, deliveryId, token))!; ValidateDeliveryStorageTransition(row, after);
            AuthorizeInspection(policy, op, inputs.Capability, caller, current);
            return new(observation.Status, true, "Recorded", digest);
        }, token);
        cancellationCheckpoint?.Invoke("ReconciliationCommitted"); return result;
    }
}
