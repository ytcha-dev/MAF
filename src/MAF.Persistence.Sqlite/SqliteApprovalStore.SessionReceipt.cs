using MAF.Core;
using MAF.Core.Policy;
using Microsoft.Data.Sqlite;

namespace MAF.Persistence.Sqlite;

public sealed partial class SqliteApprovalStore : ISessionReceiptCoordinator
{
    public bool RequiresSessionReceipts => profile == ApprovalStoreProfile.ExplicitV4;
    public Task<SessionReceiptRecord?> GetSessionReceiptAsync(string taskId, CancellationToken token = default)
    {
        Require(RequiresSessionReceipts, "ApprovalSchemaV4Required"); Id(taskId);
        return Transaction((c, tx) => ReadSessionReceipt(c, tx, taskId, token), token);
    }

    public async Task<SessionReceiptRegistration> CommitCreateIntentAsync(PolicyRequest operation, PolicyDecision assessment,
        CapabilityDescriptor capability, DurableExecution expected, DurableExecution intent, long controlVersion,
        long requestTimeoutTicks, long responseLimitBytes, ITrustedCallerContext caller, IExecutionOwnership owner,
        ConsumedApprovalAuthorization? approval, CancellationToken token = default)
    {
        Require(RequiresSessionReceipts && requestTimeoutTicks is > 0 and <= 600000000
            && responseLimitBytes is >= 1000 and <= 3000000, "InvalidReceiptConfiguration");
        Require(capability.Capability == PolicyCapability.ReadOnlyTask && capability.ConnectorId == operation.ConnectorId
            && OperationBinding.Digest(operation, capability) == assessment.BindingDigest, "ReceiptOperationMismatch");
        SessionReceiptRecord? record = null;
        await VerifyEffectTransactionAsync(operation, assessment, expected, intent, controlVersion,
            ProviderEffect.CreateSession, caller, owner, token, approval, async (c, tx) =>
            {
                var sequence = Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT last_insert_rowid()", token));
                var a = approval?.Approval;
                var storedOperation = operation with { RequestFingerprint = operation.RequestFingerprint.ToUpperInvariant(),
                    Scope = operation.Scope with { ProviderScopeDigest = operation.Scope.ProviderScopeDigest.ToUpperInvariant(),
                        AgentPolicyDigest = operation.Scope.AgentPolicyDigest.ToUpperInvariant() } };
                var storedCapability = capability with { SupportedOperations = [..capability.SupportedOperations.OrderBy(x => x.ToString(), StringComparer.Ordinal)] };
                var payload = new SessionReceiptIntent(storedOperation, storedCapability, expected.Handle.Provider, Guid.NewGuid().ToString("N"),
                    a?.ApprovalId, a?.Version, a?.BindingDigest, caller.AuthenticationContextId,
                    PolicySnapshotBinding.Digest(Candidate()), expected.Version, intent.Version, controlVersion, sequence,
                    DateTimeOffset.Parse((string)(await ApprovalDatabase.Scalar(c, tx, "SELECT recorded_at FROM control_audit WHERE sequence=$s", token, ("$s", sequence)))!).UtcTicks,
                    requestTimeoutTicks, responseLimitBytes);
                var digest = SessionReceiptFormat.Digest(payload); var json = SessionReceiptFormat.Write(payload, 16384);
                await ApprovalDatabase.Execute(c, tx, """
                    INSERT INTO execution_session_receipts(receipt_id,task_id,execution_id,operation_id,approval_id,provider_id,provider_scope,
                    intent_execution_version,intent_control_version,intent_audit_sequence,intent_at_ticks,format_version,binding_version,intent_digest,intent_json,version,status)
                    VALUES($id,$t,$e,$o,$a,$provider,$scope,$v,$control,$audit,$at,1,1,$digest,$json,0,0)
                    """, token, ("$id", payload.ReceiptId), ("$t", operation.TaskId), ("$e", operation.ExecutionId), ("$o", operation.OperationId),
                    ("$a", a?.ApprovalId), ("$provider", payload.ProviderId), ("$scope", operation.Scope.ProviderScopeDigest),
                    ("$v", intent.Version), ("$control", controlVersion), ("$audit", sequence), ("$at", payload.IntentAtTicks), ("$digest", digest), ("$json", json));
                record = await ReadSessionReceipt(c, tx, operation.TaskId, token);
                cancellationCheckpoint?.Invoke("ReceiptIntentWritten");
            });
        cancellationCheckpoint?.Invoke("ReceiptIntentCommitted");
        return new(owner, record ?? throw new InvalidOperationException("SessionReceiptRequired"), FinalizeOriginalResponse);
    }

    async Task<DurableExecution> FinalizeOriginalResponse(SessionReceiptRegistration registration, string session, bool verified,
        string diagnostic, long observed, CancellationToken token)
    {
        Id(session); var original = registration.Receipt; var p = original.Intent; var op = p.Operation;
        Require(verified ? diagnostic == "None" : diagnostic is "SessionPolicyMismatch" or "InvalidSessionResponse", "InvalidReceiptDiagnostic");
        await using var use = await SqliteExecutionStore.UseAdmissionOwnershipAsync(path, op.TaskId, registration.Owner, token, ExecutionStoreProfile.ExplicitV4);
        var committed = await Transaction(async (c, tx) =>
        {
            var receipt = await ReadSessionReceipt(c, tx, op.TaskId, token) ?? throw new InvalidOperationException("SessionReceiptRequired");
            Require(receipt.IntentDigest == original.IntentDigest && receipt.Status == SessionReceiptStatus.Pending, "ReceiptAlreadyFinalized");
            var (before, _, _) = await CancelTarget(c, tx, op.TaskId, token);
            Require(before is not null && before.Handle.ExternalSessionId is null && !before.IsTerminal
                && before.SubmissionPhase == SubmissionPhase.SessionCreationIntended
                && before.State is DurableExecutionState.SessionCreating or DurableExecutionState.Unknown, "ReceiptExecutionConflict");
            var control = await Control(c, tx, op.TaskId, token);
            Require(control is not null && control.ExecutionId == op.ExecutionId && control.Version >= p.IntentControlVersion, "ReceiptControlConflict");
            if (control!.CancelRequestId is not null) _ = await ReadCancellation(c, tx, control.CancelRequestId, token);
            else Require(control.Version == p.IntentControlVersion, "ReceiptControlConflict");
            var now = clock.GetUtcNow(); Require(p.IntentAtTicks <= observed && observed <= now.UtcTicks, "ReceiptClockRollback");
            var sequence = await ReceiptAudit(c, tx, p, 1, now, token);
            var response = new SessionReceiptResponse(p.ReceiptId, receipt.IntentDigest, session,
                verified ? SessionReceiptStatus.Verified : SessionReceiptStatus.Quarantined, diagnostic, observed, now.UtcTicks, sequence);
            var next = before! with { Handle = before.Handle with { ExternalSessionId = session },
                State = verified && before.State == DurableExecutionState.SessionCreating ? DurableExecutionState.ReadyToSubmit : DurableExecutionState.Unknown,
                SubmissionPhase = SubmissionPhase.SessionKnown, Version = before.Version + 1,
                UpdatedAt = now < before.UpdatedAt ? before.UpdatedAt : now, ErrorCode = verified ? null : diagnostic };
            DurableExecutionTransitions.Validate(before, next);
            Require(await ApprovalDatabase.Execute(c, tx, "UPDATE executions SET version=$v,snapshot=$j WHERE task_id=$t AND version=$old AND snapshot=$before", token,
                ("$v", next.Version), ("$j", Json(next)), ("$t", op.TaskId), ("$old", before.Version), ("$before", Json(before))) == 1, "ExecutionVersionConflict");
            cancellationCheckpoint?.Invoke("ReceiptSnapshotWritten");
            Require(await ApprovalDatabase.Execute(c, tx, """
                UPDATE execution_session_receipts SET version=1,status=$status,provider_session_id=$s,response_digest=$digest,response_json=$json,
                finalized_at_ticks=$at,response_audit_sequence=$audit WHERE receipt_id=$id AND version=0 AND status=0 AND intent_digest=$intent
                """, token, ("$status", (int)response.Verification), ("$s", session), ("$digest", SessionReceiptFormat.Digest(response)),
                ("$json", SessionReceiptFormat.Write(response, 2048)), ("$at", now.UtcTicks), ("$audit", sequence), ("$id", p.ReceiptId), ("$intent", original.IntentDigest)) == 1, "ReceiptVersionConflict");
            cancellationCheckpoint?.Invoke("ReceiptResponseWritten");
            _ = await ReadSessionReceipt(c, tx, op.TaskId, token);
            if (control.CancelRequestId is not null) _ = await ReadCancellation(c, tx, control.CancelRequestId, token);
            return next;
        }, token);
        cancellationCheckpoint?.Invoke("ReceiptResponseCommitted");
        return committed;
    }
    async Task<long> ReceiptAudit(SqliteConnection c, SqliteTransaction tx, SessionReceiptIntent p, long version, DateTimeOffset at, CancellationToken token)
    {
        Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM control_audit", token)) < auditCapacity, "AuditCapacityReached");
        await ApprovalDatabase.Execute(c, tx, "INSERT INTO control_audit(code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version) VALUES(0,$a,$t,$approval,$v,$at,$epoch,$policy)", token,
            ("$a", p.Operation.CallerSubject), ("$t", p.Operation.TaskId), ("$approval", p.ApprovalId), ("$v", version), ("$at", Utc(at)),
            ("$epoch", p.Operation.Epoch.Value), ("$policy", p.Operation.Version.Value));
        return Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT last_insert_rowid()", token));
    }

    internal static async Task<SessionReceiptRecord?> ReadSessionReceipt(SqliteConnection c, SqliteTransaction? tx, string task, CancellationToken token,
        bool validateCancellation = true)
    {
        SessionReceiptRecord result;
        using (var cmd = ApprovalDatabase.Command(c, tx, "SELECT *,length(CAST(intent_json AS BLOB)),length(CAST(response_json AS BLOB)) FROM execution_session_receipts WHERE task_id=$t", ("$t", task)))
        using (var r = await cmd.ExecuteReaderAsync(token))
        {
            if (!await r.ReadAsync(token))
            {
                await r.DisposeAsync();
                using var missing = ApprovalDatabase.Command(c, tx, "SELECT snapshot,session_receipt_required FROM executions WHERE task_id=$t", ("$t", task));
                using var existing = await missing.ExecuteReaderAsync(token);
                if (await existing.ReadAsync(token) && existing.GetInt64(1) == 1)
                {
                    var snapshot = System.Text.Json.JsonSerializer.Deserialize<DurableExecution>(existing.GetString(0));
                    Require(snapshot is not null && snapshot.TaskId == task && snapshot.SubmissionPhase == SubmissionPhase.None, "SessionReceiptRequired");
                }
                return null;
            }
            Require(r.GetInt64(22) is > 0 and <= 16384 && (r.IsDBNull(23) || r.GetInt64(23) is > 0 and <= 2048), "ReceiptEvidenceSize");
            var p = SessionReceiptFormat.Read<SessionReceiptIntent>(r.GetString(14), 16384);
            var response = r.IsDBNull(19) ? null : SessionReceiptFormat.Read<SessionReceiptResponse>(r.GetString(19), 2048);
            result = new(p, r.GetString(13), response, r.IsDBNull(18) ? null : r.GetString(18), r.GetInt64(15), (SessionReceiptStatus)r.GetInt32(16));
            var op = p.Operation; Metadata(op); Id(p.ReceiptId); Id(p.CallerAuthenticationContextId); Id(p.ProviderId);
            Require(p.ReceiptId == r.GetString(0) && op.TaskId == task && op.ExecutionId == r.GetString(2) && op.OperationId == r.GetString(3)
                && p.ApprovalId == (r.IsDBNull(4) ? null : r.GetString(4)) && p.ProviderId == r.GetString(5)
                && op.Scope.ProviderScopeDigest == r.GetString(6) && p.IntentExecutionVersion == r.GetInt64(7)
                && p.IntentControlVersion == r.GetInt64(8) && p.IntentAuditSequence == r.GetInt64(9) && p.IntentAtTicks == r.GetInt64(10)
                && r.GetInt64(11) == 1 && r.GetInt64(12) == 1 && SessionReceiptFormat.Digest(p) == result.IntentDigest
                && p.IntentExecutionVersion == p.PriorExecutionVersion + 1 && p.PriorExecutionVersion >= 0 && p.IntentControlVersion >= 0
                && p.RequestTimeoutTicks is > 0 and <= 600000000 && p.ResponseLimitBytes is >= 1000 and <= 3000000
                && op.Operation == PolicyOperation.SubmitNewReadOnlyTask && op.Capability == PolicyCapability.ReadOnlyTask
                && p.Capability.Capability == op.Capability && p.Capability.ConnectorId == op.ConnectorId
                && p.Capability.Enabled && p.Capability.Healthy && p.Capability.Boundary == EnforcementBoundary.TaskAdmission
                && p.Capability.SupportedOperations.Contains(PolicyOperation.SubmitNewReadOnlyTask)
                && p.Capability.SupportedOperations.All(x => x is PolicyOperation.SubmitNewReadOnlyTask or PolicyOperation.InspectExistingExecution or PolicyOperation.RequestCancelExecution)
                && op.ProviderSessionId is null && op.Scope.Access == ResourceAccess.ReadOnly
                && p.PolicyDigest.Length == 64 && p.PolicyDigest.All(ch => char.IsAsciiHexDigit(ch) && !char.IsLower(ch)), "InvalidSessionReceipt");
            if (response is null) Require(result.Status == SessionReceiptStatus.Pending && result.Version == 0, "InvalidSessionReceipt");
            else
            {
                Id(response.ProviderSessionId);
                Require(response.ReceiptId == p.ReceiptId && response.IntentDigest == result.IntentDigest
                && response.ProviderSessionId == r.GetString(17) && SessionReceiptFormat.Digest(response) == result.ResponseDigest
                && response.FinalizedAtTicks == r.GetInt64(20) && response.ResponseAuditSequence == r.GetInt64(21)
                && p.IntentAtTicks <= response.ObservedAtTicks && response.ObservedAtTicks <= response.FinalizedAtTicks
                && response.ResponseAuditSequence > p.IntentAuditSequence && response.Verification == result.Status && result.Version == 1
                && (result.Status == SessionReceiptStatus.Verified ? response.Diagnostic == "None"
                    : result.Status == SessionReceiptStatus.Quarantined && response.Diagnostic is "SessionPolicyMismatch" or "InvalidSessionResponse"), "InvalidSessionReceipt");
            }
        }
        var intent = result.Intent; var operation = intent.Operation;
        var (execution, approval, target) = await CancelTarget(c, tx!, task, token);
        Require(execution is not null && target == operation.OperationId && execution.Handle.ExecutionId == operation.ExecutionId
            && execution.WorkflowRunId == operation.WorkflowRunId && execution.Handle.ExternalRequestId == operation.MessageId
            && execution.Handle.Provider == intent.ProviderId && execution.ProjectRoot == operation.Scope.ResourceId
            && execution.ProviderScope == operation.Scope.ProviderScopeDigest && execution.RequestFingerprint == operation.RequestFingerprint
            && execution.Version >= intent.IntentExecutionVersion && execution.Handle.ExternalSessionId == result.Response?.ProviderSessionId
            && (result.Response is null ? execution.SubmissionPhase == SubmissionPhase.SessionCreationIntended
                : execution.SubmissionPhase >= SubmissionPhase.SessionKnown)
            && Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT session_receipt_required FROM executions WHERE task_id=$t", token, ("$t", task))) == 1,
            "ReceiptExecutionMismatch");
        Require(intent.ApprovalId is null ? approval is null && intent.ApprovalVersion is null && intent.ApprovalBindingDigest is null
            : approval is not null && approval.ApprovalId == intent.ApprovalId && approval.Status == ApprovalStatus.Consumed
                && approval.Version == intent.ApprovalVersion && approval.BindingDigest == intent.ApprovalBindingDigest
                && approval.DecisionId is not null && approval.DecidedBy is not null && approval.Choice == ApprovalChoice.Approve
                && approval.DecidedBy != operation.CallerSubject && OperationBinding.Digest(approval.Operation, intent.Capability) == approval.BindingDigest
                && OperationBinding.Encode(approval.Operation, intent.Capability).SequenceEqual(OperationBinding.Encode(operation, intent.Capability)), "ReceiptApprovalMismatch");
        if (approval is not null)
        {
            Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, """
                SELECT count(*) FROM control_audit WHERE approval_id=$id AND task_id=$t AND code=1 AND actor=$actor
                AND version=$v AND policy_epoch=$epoch AND policy_version=$policy AND sequence<$intent
                """, token, ("$id", approval.ApprovalId), ("$t", task), ("$actor", approval.DecidedBy), ("$v", approval.Version - 1),
                ("$epoch", operation.Epoch.Value), ("$policy", operation.Version.Value), ("$intent", intent.IntentAuditSequence))) == 1
                && Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, """
                SELECT count(*) FROM control_audit WHERE approval_id=$id AND task_id=$t AND code=5 AND actor=$actor
                AND version=$v AND policy_epoch=$epoch AND policy_version=$policy AND sequence<$intent
                """, token, ("$id", approval.ApprovalId), ("$t", task), ("$actor", operation.CallerSubject), ("$v", approval.Version),
                ("$epoch", operation.Epoch.Value), ("$policy", operation.Version.Value), ("$intent", intent.IntentAuditSequence))) == 1,
                "ReceiptApprovalAuditMismatch");
        }
        Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx,
            "SELECT count(*) FROM authorization_operations WHERE operation_id=$id AND task_id=$t AND execution_id=$e AND kind=1 AND generation=1", token,
            ("$id", operation.OperationId), ("$t", task), ("$e", operation.ExecutionId))) == 1, "ReceiptOperationMismatch");
        var control = await Control(c, tx!, task, token);
        Require(control is not null && control.ExecutionId == operation.ExecutionId && control.Version >= intent.IntentControlVersion, "ReceiptControlMismatch");
        if (control!.CancelRequestId is null) Require(control.Version == intent.IntentControlVersion, "ReceiptControlMismatch");
        else if (validateCancellation) _ = await ReadCancellation(c, tx!, control.CancelRequestId, token);
        await CheckReceiptAudit(c, tx, intent, intent.IntentAuditSequence, intent.IntentExecutionVersion, intent.IntentAtTicks, token);
        if (result.Response is { } final) await CheckReceiptAudit(c, tx, intent, final.ResponseAuditSequence, 1, final.FinalizedAtTicks, token);
        return result;
    }
    static async Task CheckReceiptAudit(SqliteConnection c, SqliteTransaction? tx, SessionReceiptIntent p, long sequence, long version, long ticks, CancellationToken token)
    {
        using var cmd = ApprovalDatabase.Command(c, tx, "SELECT code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version,cancel_request_id FROM control_audit WHERE sequence=$s", ("$s", sequence));
        using var r = await cmd.ExecuteReaderAsync(token);
        Require(await r.ReadAsync(token) && r.GetInt64(0) == 0 && r.GetString(1) == p.Operation.CallerSubject && r.GetString(2) == p.Operation.TaskId
            && (r.IsDBNull(3) ? null : r.GetString(3)) == p.ApprovalId && r.GetInt64(4) == version
            && DateTimeOffset.Parse(r.GetString(5)).UtcTicks == ticks && r.GetInt64(6) == p.Operation.Epoch.Value
            && r.GetInt64(7) == p.Operation.Version.Value && r.IsDBNull(8), "ReceiptAuditMismatch");
        await r.DisposeAsync();
        Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx,
            "SELECT count(*) FROM execution_session_receipts WHERE intent_audit_sequence=$s OR response_audit_sequence=$s", token, ("$s", sequence))) == 1
            && Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx,
            "SELECT count(*) FROM cancellation_deliveries WHERE intent_audit_sequence=$s OR last_audit_sequence=$s", token, ("$s", sequence))) == 0, "ReceiptAuditReuse");
    }
}
