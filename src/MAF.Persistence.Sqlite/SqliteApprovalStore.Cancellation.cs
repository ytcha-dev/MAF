using System.Text.Json;
using MAF.Core;
using MAF.Core.Policy;
using Microsoft.Data.Sqlite;

namespace MAF.Persistence.Sqlite;

public sealed partial class SqliteApprovalStore : ICancellationAdmissionCoordinator
{
    public Task<CancellationEvidence?> GetCancellationAsync(string cancelRequestId, CancellationToken token = default)
    {
        Require(profile == ApprovalStoreProfile.ExplicitV3, "CancellationSchemaV3Required"); Id(cancelRequestId);
        return Transaction((c, tx) => ReadCancellation(c, tx, cancelRequestId, token), token);
    }

    public async Task<CancellationAdmissionResult> RequestCancelAsync(CancellationCommand command, CancellationAdmissionInputs inputs,
        ITrustedCallerContext caller, CancellationToken token = default)
    {
        Require(profile == ApprovalStoreProfile.ExplicitV3, "CancellationSchemaV3Required");
        Id(command.CancelRequestId); Id(command.TaskId); Id(command.ExecutionId); Id(inputs.OperationId); var actor = Actor(caller);
        await using var lease = await authority.AcquireAsync(token); var policy = Candidate();
        var result = await GuardedTransaction<CancellationAdmissionResult>(policy, async (c, tx) =>
        {
            // Target metadata is read internally to construct the exact operation. Until its
            // independent authorization succeeds, do not expose existence/binding distinctions.
            var control = await Control(c, tx, command.TaskId, token) ?? throw new InvalidOperationException("CancellationNotAuthorized");
            Require(control.ExecutionId == command.ExecutionId, "CancellationNotAuthorized");
            (DurableExecution? execution, ApprovalRecord? approval, string? targetId) target;
            try { target = await CancelTarget(c, tx, command.TaskId, token); }
            catch (Exception e) when (e is InvalidOperationException or JsonException) { throw new InvalidOperationException("CancellationNotAuthorized"); }
            var (execution, approval, targetId) = target;
            Require(execution is not null || approval is not null, "CancellationNotAuthorized");
            var original = approval?.Operation;
            var op = new PolicyRequest(inputs.AuthorityId, actor, inputs.OperationId, command.TaskId,
                execution?.WorkflowRunId ?? original!.WorkflowRunId, command.ExecutionId,
                execution?.Handle.ExternalRequestId ?? original!.MessageId, execution?.RequestFingerprint ?? original!.RequestFingerprint,
                inputs.Capability.ConnectorId, inputs.Capability.Capability, PolicyOperation.RequestCancelExecution,
                inputs.Scope, inputs.Epoch, inputs.Version, ProviderSessionId: execution?.Handle.ExternalSessionId);
            Metadata(op);
            void Authorized()
            {
                Require(op.Operation == PolicyOperation.RequestCancelExecution && op.Capability == PolicyCapability.ExecutionCancellation
                    && inputs.Capability.Boundary == EnforcementBoundary.TaskAdmission && inputs.Scope.Access == ResourceAccess.ReadOnly
                    && inputs.Capability.SupportedOperations.Length is >= 1 and <= 3
                    && inputs.Capability.SupportedOperations.All(x => x is PolicyOperation.SubmitNewReadOnlyTask or PolicyOperation.InspectExistingExecution or PolicyOperation.RequestCancelExecution), "CancelNotSupported");
                var d = new DeterministicPolicyEvaluator(new ImmutablePolicySource(policy), clock).Evaluate(op, caller);
                Require(d.Outcome != PolicyOutcome.RequireApproval, "CancelApprovalNotSupported");
                Require(d.Outcome == PolicyOutcome.Allow && d.Reason == PolicyReason.Allowed && authority.CanControl(caller, op)
                    && d.BindingDigest == OperationBinding.Digest(op, inputs.Capability), "CancellationNotAuthorized");
                Require(execution is null ? original!.Scope == op.Scope && original.ExecutionId == op.ExecutionId
                    : execution.Handle.ExecutionId == op.ExecutionId && execution.ProjectRoot == op.Scope.ResourceId
                    && execution.ProviderScope == op.Scope.ProviderScopeDigest && authority.MatchesExecutionBinding(op, execution), "TargetBindingMismatch");
            }
            Authorized();
            var existing = await ReadCancellation(c, tx, command.CancelRequestId, token);
            if (existing is not null)
            {
                Require(existing.ExpectedControlVersion == command.ExpectedControlVersion && existing.TargetOperationId == targetId
                    && existing.PolicyDigest == PolicySnapshotBinding.Digest(policy)
                    && OperationBinding.Encode(existing.Operation, existing.Capability).SequenceEqual(OperationBinding.Encode(op, inputs.Capability)), "CancellationIdConflict");
                Require(clock.GetUtcNow() >= existing.EvaluatedAt && clock.GetUtcNow() < existing.ExpiresAt, "CancellationNotAuthorized");
                Authorized(); return new(CancellationAdmissionStatus.AlreadyRecorded, existing);
            }
            var used = (string?)await ApprovalDatabase.Scalar(c, tx, "SELECT task_id FROM task_controls WHERE cancel_id=$id", token, ("$id", command.CancelRequestId));
            Require(used is null || used == command.TaskId, "CancellationIdConflict");
            if (control.CancelRequestId is not null)
            {
                var winner = await ReadCancellation(c, tx, control.CancelRequestId, token);
                if (winner is not null) throw new InvalidOperationException("CancellationAlreadyRequested");
                Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM authorization_operations WHERE task_id=$t AND kind=3", token, ("$t", op.TaskId))) == 0,
                    "CancellationEvidenceMissing");
                return new(CancellationAdmissionStatus.LegacyIntentUnverified, null);
            }
            if (execution?.IsTerminal == true) return new(CancellationAdmissionStatus.AlreadyTerminal, null);
            Require(command.ExpectedControlVersion >= 0 && command.ExpectedControlVersion < long.MaxValue && control.Version == command.ExpectedControlVersion, "ControlConflict");
            Require(approval is null || approval.Status is ApprovalStatus.Pending or ApprovalStatus.Approved or ApprovalStatus.Consumed, "TargetNotCancellable");
            var decision = new DeterministicPolicyEvaluator(new ImmutablePolicySource(policy), clock).Evaluate(op, caller);
            var now = clock.GetUtcNow(); var json = CancellationEvidenceFormat.Write(op, inputs.Capability);
            var normalized = CancellationEvidenceFormat.Read(json);
            var evidence = new CancellationEvidence(command.CancelRequestId, normalized.Operation, normalized.Capability, caller.AuthenticationContextId,
                targetId, PolicySnapshotBinding.Digest(policy), decision.BindingDigest!, control.Version, control.Version + 1, decision.EvaluatedAt, decision.NotAfter, now);
            await ApprovalDatabase.Execute(c, tx, "INSERT INTO authorization_operations VALUES($o,$t,$e,3,1)", token,
                ("$o", op.OperationId), ("$t", op.TaskId), ("$e", op.ExecutionId));
            await ApprovalDatabase.Execute(c, tx, """
                INSERT INTO cancellation_requests VALUES($id,$o,$t,$e,$target,$actor,$context,$authority,$epoch,$policy,$pd,1,1,$binding,$old,$new,$evaluated,$expires,$at,$json)
                """, token, ("$id", command.CancelRequestId), ("$o", op.OperationId), ("$t", op.TaskId), ("$e", op.ExecutionId), ("$target", targetId),
                ("$actor", actor), ("$context", caller.AuthenticationContextId), ("$authority", op.AuthorityId), ("$epoch", op.Epoch.Value), ("$policy", op.Version.Value),
                ("$pd", evidence.PolicyDigest), ("$binding", evidence.BindingDigest), ("$old", control.Version), ("$new", control.Version + 1),
                ("$evaluated", evidence.EvaluatedAt.UtcTicks), ("$expires", evidence.ExpiresAt.UtcTicks), ("$at", now.UtcTicks), ("$json", json));
            cancellationCheckpoint?.Invoke("CancelEvidenceInserted");
            Require(await ApprovalDatabase.Execute(c, tx, "UPDATE task_controls SET version=version+1,cancel_id=$id,actor=$a,requested_at=$at WHERE task_id=$t AND execution_id=$e AND version=$v AND cancel_id IS NULL", token,
                ("$id", command.CancelRequestId), ("$a", actor), ("$at", Utc(now)), ("$t", op.TaskId), ("$e", op.ExecutionId), ("$v", control.Version)) == 1, "ControlConflict");
            cancellationCheckpoint?.Invoke("CancelControlWritten");
            if (approval is { Status: ApprovalStatus.Pending or ApprovalStatus.Approved })
            {
                var next = approval with { Status = ApprovalStatus.Revoked, Version = approval.Version + 1 };
                Require(await ApprovalDatabase.Execute(c, tx, "UPDATE approval_requests SET status=4,version=$v,snapshot=$j WHERE approval_id=$id AND version=$old AND snapshot=$before", token,
                    ("$v", next.Version), ("$j", Json(next)), ("$id", approval.ApprovalId), ("$old", approval.Version), ("$before", Json(approval))) == 1, "StaleApprovalVersion");
                await CancellationAudit(c, tx, ControlAuditCode.Revoked, evidence, approval.ApprovalId, next.Version, token);
            }
            cancellationCheckpoint?.Invoke("CancelApprovalRevoked");
            await CancellationAudit(c, tx, ControlAuditCode.CancellationRequested, evidence, approval?.ApprovalId, evidence.CommittedControlVersion, token);
            cancellationCheckpoint?.Invoke("CancelAuditWritten");
            _ = await ReadCancellation(c, tx, command.CancelRequestId, token) ?? throw new InvalidOperationException("CancellationEvidenceMissing");
            Authorized(); Require(now >= decision.EvaluatedAt && clock.GetUtcNow() < decision.NotAfter, "CancellationNotAuthorized");
            return new CancellationAdmissionResult(CancellationAdmissionStatus.Recorded, evidence);
        }, token);
        cancellationCheckpoint?.Invoke("CancelCommitted");
        return result;
    }

    readonly Action<string>? cancellationCheckpoint;
    async Task CancellationAudit(SqliteConnection c, SqliteTransaction tx, ControlAuditCode code, CancellationEvidence evidence, string? approval, long version, CancellationToken token)
    {
        Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM control_audit", token)) < auditCapacity, "AuditCapacityReached");
        await ApprovalDatabase.Execute(c, tx, "INSERT INTO control_audit(code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version,cancel_request_id) VALUES($code,$a,$t,$approval,$v,$at,$epoch,$policy,$cancel)", token,
            ("$code", (int)code), ("$a", evidence.Operation.CallerSubject), ("$t", evidence.Operation.TaskId), ("$approval", approval), ("$v", version),
            ("$at", Utc(evidence.RequestedAt)), ("$epoch", evidence.Operation.Epoch.Value), ("$policy", evidence.Operation.Version.Value), ("$cancel", evidence.CancelRequestId));
    }

    static async Task<(DurableExecution? Execution, ApprovalRecord? Approval, string? Target)> CancelTarget(SqliteConnection c, SqliteTransaction tx, string task, CancellationToken token)
    {
        DurableExecution? execution = null;
        using (var cmd = ApprovalDatabase.Command(c, tx, "SELECT snapshot,version,terminal FROM executions WHERE task_id=$t", ("$t", task)))
        using (var r = await cmd.ExecuteReaderAsync(token)) if (await r.ReadAsync(token))
        {
            execution = JsonSerializer.Deserialize<DurableExecution>(r.GetString(0));
            Require(execution is not null && execution.TaskId == task && execution.Version == r.GetInt64(1) && execution.IsTerminal == r.GetBoolean(2)
                && execution.IsTerminal == execution.CompletedAt.HasValue && Enum.IsDefined(execution.State) && Enum.IsDefined(execution.SubmissionPhase), "InvalidExecutionSnapshot");
        }
        var target = (string?)await ApprovalDatabase.Scalar(c, tx, "SELECT operation_id FROM authorization_operations WHERE task_id=$t AND kind=1", token, ("$t", task));
        var ids = new List<string>();
        using (var cmd = ApprovalDatabase.Command(c, tx, "SELECT a.approval_id FROM approval_requests a JOIN authorization_operations o ON o.operation_id=a.operation_id WHERE o.task_id=$t", ("$t", task)))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) ids.Add(r.GetString(0));
        Require(ids.Count <= 1, "AssociatedApprovalNotSupported");
        var approval = ids.Count == 0 ? null : await Read(c, tx, ids[0], token);
        Require(approval is null || approval.Operation.Operation == PolicyOperation.SubmitNewReadOnlyTask && approval.Operation.OperationId == target, "AssociatedApprovalNotSupported");
        if (target is not null)
            Require((string?)await ApprovalDatabase.Scalar(c, tx, "SELECT execution_id FROM authorization_operations WHERE operation_id=$id", token,
                ("$id", target)) == (execution?.Handle.ExecutionId ?? approval?.Operation.ExecutionId), "TargetBindingMismatch");
        Require(execution is null || approval is null || execution.Handle.ExecutionId == approval.Operation.ExecutionId
            && execution.WorkflowRunId == approval.Operation.WorkflowRunId && execution.Handle.ExternalRequestId == approval.Operation.MessageId
            && execution.RequestFingerprint == approval.Operation.RequestFingerprint && execution.ProjectRoot == approval.Operation.Scope.ResourceId
            && execution.ProviderScope == approval.Operation.Scope.ProviderScopeDigest, "TargetBindingMismatch");
        return (execution, approval, target);
    }

    static async Task<CancellationEvidence?> ReadCancellation(SqliteConnection c, SqliteTransaction tx, string id, CancellationToken token)
    {
        CancellationEvidence evidence;
        using (var cmd = ApprovalDatabase.Command(c, tx, "SELECT *,length(CAST(evidence_json AS BLOB)) FROM cancellation_requests WHERE cancel_request_id=$id", ("$id", id)))
        using (var r = await cmd.ExecuteReaderAsync(token))
        {
            if (!await r.ReadAsync(token))
            {
                Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM control_audit WHERE cancel_request_id=$id", token, ("$id", id))) == 0
                    && Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM task_controls t JOIN authorization_operations o ON o.task_id=t.task_id WHERE t.cancel_id=$id AND o.kind=3", token, ("$id", id))) == 0,
                    "CancellationEvidenceMissing");
                return null;
            }
            Require(r.GetInt64(20) is > 0 and <= 16384, "InvalidCancellationEvidence");
            var payload = CancellationEvidenceFormat.Read(r.GetString(19)); var op = payload.Operation; var cap = payload.Capability;
            evidence = new(id, op, cap, r.GetString(6), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(10), r.GetString(13),
                r.GetInt64(14), r.GetInt64(15), new(r.GetInt64(16), TimeSpan.Zero), new(r.GetInt64(17), TimeSpan.Zero), new(r.GetInt64(18), TimeSpan.Zero));
            Metadata(op); Id(evidence.AuthenticationContextId); if (evidence.TargetOperationId is not null) Id(evidence.TargetOperationId);
            Require(op.OperationId == r.GetString(1) && op.TaskId == r.GetString(2) && op.ExecutionId == r.GetString(3) && op.CallerSubject == r.GetString(5)
                && op.AuthorityId == r.GetString(7) && op.Epoch.Value == r.GetInt64(8) && op.Version.Value == r.GetInt64(9)
                && r.GetInt32(11) == 1 && r.GetInt32(12) == OperationBinding.EncodingVersion
                && op.Operation == PolicyOperation.RequestCancelExecution && op.Capability == PolicyCapability.ExecutionCancellation
                && cap.Capability == op.Capability && cap.ConnectorId == op.ConnectorId && cap.Boundary == EnforcementBoundary.TaskAdmission
                && cap.Enabled && cap.Healthy
                && op.Scope.Access == ResourceAccess.ReadOnly && cap.SupportedOperations.Contains(PolicyOperation.RequestCancelExecution)
                && cap.SupportedOperations.Length is >= 1 and <= 3
                && cap.SupportedOperations.All(x => x is PolicyOperation.SubmitNewReadOnlyTask or PolicyOperation.InspectExistingExecution or PolicyOperation.RequestCancelExecution)
                && evidence.PolicyDigest.Length == 64 && evidence.PolicyDigest.All(ch => char.IsAsciiHexDigit(ch) && !char.IsLower(ch))
                && evidence.ExpectedControlVersion >= 0 && evidence.ExpectedControlVersion < long.MaxValue
                && evidence.BindingDigest == OperationBinding.Digest(op, cap) && evidence.CommittedControlVersion == evidence.ExpectedControlVersion + 1
                && evidence.EvaluatedAt <= evidence.RequestedAt && evidence.RequestedAt < evidence.ExpiresAt, "InconsistentCancellationEvidence");
        }
        var operation = evidence.Operation;
        Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM authorization_operations WHERE operation_id=$o AND task_id=$t AND execution_id=$e AND kind=3 AND generation=1", token,
            ("$o", operation.OperationId), ("$t", operation.TaskId), ("$e", operation.ExecutionId))) == 1, "CancellationOperationMismatch");
        var control = await Control(c, tx, operation.TaskId, token);
        Require(control is not null && control.ExecutionId == operation.ExecutionId && control.CancelRequestId == id && control.RequestedBy == operation.CallerSubject
            && control.RequestedAt == evidence.RequestedAt && control.Version >= evidence.CommittedControlVersion, "CancellationControlMismatch");
        var (execution, approval, target) = await CancelTarget(c, tx, operation.TaskId, token);
        Require(target == evidence.TargetOperationId && (execution is not null ? execution.Handle.ExecutionId == operation.ExecutionId
            && execution.RequestFingerprint == operation.RequestFingerprint && execution.WorkflowRunId == operation.WorkflowRunId
            && execution.Handle.ExternalRequestId == operation.MessageId && execution.ProjectRoot == operation.Scope.ResourceId
            && execution.Handle.ExternalSessionId == operation.ProviderSessionId
            && execution.ProviderScope == operation.Scope.ProviderScopeDigest
            : approval is not null && approval.Operation.ExecutionId == operation.ExecutionId && approval.Operation.Scope == operation.Scope
                && approval.Operation.WorkflowRunId == operation.WorkflowRunId && approval.Operation.MessageId == operation.MessageId
                && approval.Operation.RequestFingerprint == operation.RequestFingerprint), "CancellationTargetMismatch");
        var audits = new List<ControlAudit>();
        using (var cmd = ApprovalDatabase.Command(c, tx, "SELECT sequence,code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version FROM control_audit WHERE cancel_request_id=$id", ("$id", id)))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) audits.Add(new(r.GetInt64(0), (ControlAuditCode)r.GetInt32(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), DateTimeOffset.Parse(r.GetString(6)), new(r.GetInt64(7)), new(r.GetInt64(8))));
        Require(audits.All(a => a.Actor == operation.CallerSubject && a.TaskId == operation.TaskId && a.RecordedAt == evidence.RequestedAt
            && a.Epoch == operation.Epoch && a.PolicyVersion == operation.Version && a.ApprovalId == approval?.ApprovalId), "CancellationAuditMismatch");
        Require(audits.Count(a => a.Code == ControlAuditCode.CancellationRequested && a.Version == evidence.CommittedControlVersion) == 1
            && audits.Count == (approval?.Status == ApprovalStatus.Revoked ? 2 : 1)
            && (approval?.Status != ApprovalStatus.Revoked || audits.Count(a => a.Code == ControlAuditCode.Revoked && a.Version == approval.Version) == 1), "CancellationAuditMissing");
        return evidence;
    }
}
