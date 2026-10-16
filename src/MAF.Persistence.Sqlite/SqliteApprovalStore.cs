using System.Globalization;
using System.Text.Json;
using MAF.Core;
using MAF.Core.Policy;
using Microsoft.Data.Sqlite;

namespace MAF.Persistence.Sqlite;

// Not registered with Host. No workflow/provider dependencies or dispatch ticket are exposed.
public sealed partial class SqliteApprovalStore : IApprovalStore, IApprovalTransactionCoordinator, IPolicyActivationCoordinator, IExecutionAdmissionCoordinator
{
    readonly string path;
    readonly IProtectedPolicyAuthority authority;
    readonly TimeProvider clock;
    readonly int auditCapacity;
    readonly Action<ApprovalCheckpoint>? fault;
    public SqliteApprovalStore(string databasePath, IProtectedPolicyAuthority authority, TimeProvider clock, int auditCapacity = 10000)
        : this(databasePath, authority, clock, auditCapacity, null) { }
    internal SqliteApprovalStore(string databasePath, IProtectedPolicyAuthority authority, TimeProvider clock, int auditCapacity, Action<ApprovalCheckpoint>? fault)
    {
        path = ApprovalDatabase.PathFor(databasePath); this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (auditCapacity is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(auditCapacity));
        this.auditCapacity = auditCapacity; this.fault = fault;
    }
    static void Require(bool value, string code) { if (!value) throw new InvalidOperationException(code); }
    static void Id(string value) => Require(value is { Length: > 0 and <= 160 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'), "InvalidMetadataId");
    static string Actor(ITrustedCallerContext caller) { Id(caller.SubjectId); Id(caller.AuthenticationContextId); return caller.SubjectId; }
    static string Json<T>(T value) => JsonSerializer.Serialize(value);
    static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    async Task<T> Transaction<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken token)
    {
        using var gate = ApprovalDatabase.SharedGate(path);
        await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(path, SqliteOpenMode.ReadWrite));
        await c.OpenAsync(token);
        using (var setup = c.CreateCommand()) { setup.CommandText = "PRAGMA synchronous=FULL"; await setup.ExecuteNonQueryAsync(token); }
        using var tx = c.BeginTransaction(deferred: false);
        Require(Convert.ToInt32(await ApprovalDatabase.Scalar(c, tx, "PRAGMA user_version", token)) == 2, "ApprovalSchemaV2Required");
        var result = await action(c, tx);
        fault?.Invoke(ApprovalCheckpoint.BeforeCommit); tx.Commit(); return result;
    }

    // Acquisition verifies a candidate, never activates it. No older active snapshot/cache fallback.
    PolicySnapshot Candidate()
    {
        var policy = authority.GetSnapshot() ?? throw new InvalidOperationException("PolicyUnavailable");
        _ = PolicySnapshotBinding.Digest(policy);
        var now = clock.GetUtcNow();
        Require(now >= policy.NotBefore && now < policy.NotAfter, "InvalidAuthorityPolicy");
        return policy;
    }
    void CheckCandidate(PolicySnapshot policy)
        => Require(PolicySnapshotBinding.Digest(Candidate()) == PolicySnapshotBinding.Digest(policy), "AuthorityChanged");

    // Called under the SQLite writer transaction. Returns true only for explicit initial activation
    // or a strictly newer pair; business operations may only verify the exact durable active tuple.
    async Task<bool> CompareActive(SqliteConnection c, SqliteTransaction tx, PolicySnapshot policy,
        bool activating, CancellationToken token)
    {
        using var cmd = ApprovalDatabase.Command(c, tx, "SELECT authority,epoch,version,digest FROM policy_authority WHERE id=1");
        using var r = await cmd.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token))
        {
            Require(activating, "PolicyNotActivated");
            return true;
        }
        Require(r.GetString(0) == policy.AuthorityId, "AuthorityMismatch");
        var epoch = r.GetInt64(1); var version = r.GetInt64(2);
        Require(policy.Epoch.Value >= epoch && policy.Version.Value >= version, "AuthorityRollback");
        var advance = policy.Epoch.Value > epoch && policy.Version.Value > version;
        Require(advance || policy.Epoch.Value == epoch && policy.Version.Value == version
            && r.GetString(3) == PolicySnapshotBinding.Digest(policy), "AuthorityRevisionConflict");
        Require(!advance || activating, "PolicyActivationRequired");
        return advance;
    }
    // The only authority writer. Trusted code explicitly invokes this unregistered primitive;
    // no production authenticated activation control/CLI is provided. No caller snapshots accepted.
    public async Task ActivatePolicyAsync(CancellationToken token = default)
    {
        await using var authorityLease = await authority.AcquireAsync(token);
        var policy = Candidate();
        await Transaction(async (c, tx) =>
        {
            CheckCandidate(policy);
            if (await CompareActive(c, tx, policy, activating: true, token))
            {
                await ApprovalDatabase.Execute(c, tx, "INSERT INTO policy_authority VALUES(1,$a,$e,$v,$d) ON CONFLICT(id) DO UPDATE SET authority=$a,epoch=$e,version=$v,digest=$d", token,
                    ("$a", policy.AuthorityId), ("$e", policy.Epoch.Value), ("$v", policy.Version.Value), ("$d", PolicySnapshotBinding.Digest(policy)));
                await Audit(c, tx, ControlAuditCode.AuthorityAdvanced, "authority", "authority", null, policy.Epoch.Value, token);
            }
            CheckCandidate(policy);
            return true;
        }, token);
    }
    async Task CheckActive(SqliteConnection c, SqliteTransaction tx, PolicySnapshot policy, CancellationToken token)
    {
        CheckCandidate(policy);
        await CompareActive(c, tx, policy, activating: false, token);
    }
    Task<T> GuardedTransaction<T>(PolicySnapshot policy, Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken token)
        => Transaction(async (c, tx) =>
        {
            await CheckActive(c, tx, policy, token);
            var result = await action(c, tx);
            await CheckActive(c, tx, policy, token);
            return result;
        }, token);

    async Task Audit(SqliteConnection c, SqliteTransaction tx, ControlAuditCode code, string actor, string task, string? approval, long version, CancellationToken token)
    {
        Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM control_audit", token)) < auditCapacity, "AuditCapacityReached");
        await ApprovalDatabase.Execute(c, tx, "INSERT INTO control_audit(code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version) SELECT $c,$a,$t,$i,$v,$at,epoch,version FROM policy_authority WHERE id=1", token,
            ("$c", (int)code), ("$a", actor), ("$t", task), ("$i", approval), ("$v", version), ("$at", Utc(clock.GetUtcNow())));
    }
    static async Task<ApprovalRecord?> Read(SqliteConnection c, SqliteTransaction tx, string id, CancellationToken token)
    {
        using var cmd = ApprovalDatabase.Command(c, tx, "SELECT a.status,a.version,a.decision_id,a.snapshot,o.operation_id,o.task_id,o.execution_id,o.kind,o.generation FROM approval_requests a JOIN authorization_operations o ON a.operation_id=o.operation_id WHERE a.approval_id=$id", ("$id", id));
        using var r = await cmd.ExecuteReaderAsync(token); if (!await r.ReadAsync(token)) return null;
        var result = JsonSerializer.Deserialize<ApprovalRecord>(r.GetString(3)) ?? throw new InvalidOperationException("InvalidApprovalSnapshot");
        Require(result.ApprovalId == id && (int)result.Status == r.GetInt32(0) && result.Version == r.GetInt64(1)
            && result.DecisionId == (r.IsDBNull(2) ? null : r.GetString(2)) && Enum.IsDefined(result.Status) && result.Version >= 0
            && result.Operation.OperationId == r.GetString(4) && result.Operation.TaskId == r.GetString(5) && result.Operation.ExecutionId == r.GetString(6)
            && (int)result.Operation.Operation == r.GetInt32(7) && result.Operation.IntentGeneration == r.GetInt64(8), "InconsistentApprovalSnapshot");
        return result;
    }
    static async Task<TaskControl?> Control(SqliteConnection c, SqliteTransaction tx, string task, CancellationToken token)
    {
        using var cmd = ApprovalDatabase.Command(c, tx, "SELECT execution_id,version,cancel_id,actor,requested_at FROM task_controls WHERE task_id=$t", ("$t", task));
        using var r = await cmd.ExecuteReaderAsync(token); if (!await r.ReadAsync(token)) return null;
        return new(task, r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture));
    }
    async Task Save(SqliteConnection c, SqliteTransaction tx, ApprovalRecord before, ApprovalRecord after, ControlAuditCode code, string actor, CancellationToken token)
    {
        Require(after.Version == before.Version + 1, "StaleApprovalVersion");
        Require(await ApprovalDatabase.Execute(c, tx, "UPDATE approval_requests SET status=$s,version=$n,decision_id=$d,snapshot=$j WHERE approval_id=$id AND version=$v AND snapshot=$old", token,
            ("$s", (int)after.Status), ("$n", after.Version), ("$d", after.DecisionId), ("$j", Json(after)), ("$id", before.ApprovalId), ("$v", before.Version), ("$old", Json(before))) == 1, "StaleApprovalVersion");
        await Audit(c, tx, code, actor, before.Operation.TaskId, before.ApprovalId, after.Version, token);
    }
    void Current(ApprovalRecord record, PolicySnapshot policy)
    {
        var now = clock.GetUtcNow();
        Require(now >= record.CreatedAt && now < record.ExpiresAt && now >= policy.NotBefore && now < policy.NotAfter, "ApprovalExpiredOrClockInvalid");
        Require(record.Operation.AuthorityId == policy.AuthorityId && record.Operation.Epoch == policy.Epoch && record.Operation.Version == policy.Version, "ApprovalPolicyChanged");
        var d = policy.Capabilities.Single(x => x.ConnectorId == record.Operation.ConnectorId && x.Capability == record.Operation.Capability);
        Require(OperationBinding.Digest(record.Operation, d) == record.BindingDigest, "BindingMismatch");
        var decision = authority.EvaluateStoredOperation(record.Operation, now);
        Require(decision.Outcome == PolicyOutcome.RequireApproval && decision.Reason == PolicyReason.ApprovalRequired && decision.BindingDigest == record.BindingDigest
            && decision.Epoch == policy.Epoch && decision.Version == policy.Version && decision.AuthorityId == policy.AuthorityId
            && decision.RequiredOperatorScope == record.RequiredOperatorScope && decision.EvaluatedAt <= now && decision.NotAfter > now, "CurrentPolicyDoesNotPermitApproval");
    }
    static void Metadata(PolicyRequest r)
    {
        foreach (var id in new[] { r.AuthorityId, r.CallerSubject, r.OperationId, r.TaskId, r.WorkflowRunId, r.ExecutionId, r.MessageId, r.ConnectorId }) Id(id);
        if (r.ProviderSessionId is not null) Id(r.ProviderSessionId);
    }

    public Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken token = default)
        => Transaction((c, tx) => Read(c, tx, approvalId, token), token);
    public Task<TaskControl?> GetControlAsync(string taskId, CancellationToken token = default)
        => Transaction((c, tx) => Control(c, tx, taskId, token), token);
    public Task<IReadOnlyList<ApprovalRecord>> ListPendingAsync(int limit = 100, CancellationToken token = default)
    {
        Require(limit is >= 1 and <= 100, "InvalidPageLimit");
        return Transaction<IReadOnlyList<ApprovalRecord>>(async (c, tx) =>
        {
            using var cmd = ApprovalDatabase.Command(c, tx, "SELECT snapshot FROM approval_requests WHERE status=0 ORDER BY approval_id LIMIT $limit", ("$limit", limit));
            using var r = await cmd.ExecuteReaderAsync(token); var results = new List<ApprovalRecord>();
            while (await r.ReadAsync(token)) results.Add(JsonSerializer.Deserialize<ApprovalRecord>(r.GetString(0))!); return results;
        }, token);
    }
    public Task<IReadOnlyList<ControlAudit>> ReadAuditAsync(long afterSequence = 0, int limit = 100, CancellationToken token = default)
    {
        Require(afterSequence >= 0 && limit is >= 1 and <= 100, "InvalidPageLimit");
        return Transaction<IReadOnlyList<ControlAudit>>(async (c, tx) =>
        {
            using var cmd = ApprovalDatabase.Command(c, tx, "SELECT sequence,code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version FROM control_audit WHERE sequence>$after ORDER BY sequence LIMIT $limit", ("$after", afterSequence), ("$limit", limit));
            using var r = await cmd.ExecuteReaderAsync(token); var results = new List<ControlAudit>();
            while (await r.ReadAsync(token)) results.Add(new(r.GetInt64(0), (ControlAuditCode)r.GetInt32(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), new(r.GetInt64(7)), new(r.GetInt64(8)))); return results;
        }, token);
    }

    public async Task<ApprovalRecord> RequestAsync(string approvalId, PolicyRequest operation, ITrustedCallerContext caller, CancellationToken token = default)
    {
        await using var authorityLease = await authority.AcquireAsync(token);
        Id(approvalId); Metadata(operation); var actor = Actor(caller); var policy = Candidate();
        return await GuardedTransaction(policy, async (c, tx) =>
        {
            await CheckActive(c, tx, policy, token);
            var decision = new DeterministicPolicyEvaluator(new ImmutablePolicySource(policy), clock).Evaluate(operation, caller);
            Require(decision.Outcome == PolicyOutcome.RequireApproval && decision.BindingDigest is not null && decision.RequiredOperatorScope is not null, "ApprovalNotRequiredOrDenied");
            Require(authority.CanControl(caller, operation), "CallerNotAuthorized");
            Id(decision.RequiredOperatorScope!);
            var existing = await Read(c, tx, approvalId, token);
            if (existing is not null) { Require(existing.Operation == operation && existing.BindingDigest == decision.BindingDigest, "DuplicateApprovalBinding"); return existing; }
            var control = await Control(c, tx, operation.TaskId, token);
            if (control is null)
                await ApprovalDatabase.Execute(c, tx, "INSERT INTO task_controls(task_id,execution_id,version) VALUES($t,$e,0)", token, ("$t", operation.TaskId), ("$e", operation.ExecutionId));
            else Require(control.ExecutionId == operation.ExecutionId && control.CancelRequestId is null, "TaskBindingOrCancellationConflict");
            if (operation.Operation == PolicyOperation.SubmitNewReadOnlyTask)
                Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM executions WHERE task_id=$t", token, ("$t", operation.TaskId))) == 0, "ExecutionAlreadyExists");
            var record = new ApprovalRecord(approvalId, operation, decision.BindingDigest!, decision.RequiredOperatorScope!, ApprovalStatus.Pending, 0, clock.GetUtcNow(), decision.NotAfter);
            await ApprovalDatabase.Execute(c, tx, "INSERT INTO authorization_operations VALUES($o,$t,$e,$k,$g)", token,
                ("$o", operation.OperationId), ("$t", operation.TaskId), ("$e", operation.ExecutionId), ("$k", (int)operation.Operation), ("$g", operation.IntentGeneration));
            await ApprovalDatabase.Execute(c, tx, "INSERT INTO approval_requests VALUES($a,$o,0,0,NULL,$j)", token, ("$a", approvalId), ("$o", operation.OperationId), ("$j", Json(record)));
            fault?.Invoke(ApprovalCheckpoint.PendingInserted);
            await Audit(c, tx, ControlAuditCode.Requested, actor, operation.TaskId, approvalId, 0, token); return record;
        }, token);
    }

    public async Task<ApprovalRecord> DecideAsync(ApprovalDecisionCommand command, ITrustedCallerContext actor, CancellationToken token = default)
    {
        await using var authorityLease = await authority.AcquireAsync(token);
        Id(command.DecisionId); var subject = Actor(actor); Require(Enum.IsDefined(command.Choice), "UnknownApprovalChoice");
        var policy = Candidate();
        return await GuardedTransaction(policy, async (c, tx) =>
        {
            await CheckActive(c, tx, policy, token);
            var record = await Read(c, tx, command.ApprovalId, token) ?? throw new InvalidOperationException("UnknownApproval");
            Require(authority.CanDecide(actor, record.Operation, record.RequiredOperatorScope) && subject != record.Operation.CallerSubject, "OperatorNotAuthorized");
            Require(command.ExpectedBindingDigest == record.BindingDigest, "BindingMismatch");
            if (record.DecisionId == command.DecisionId)
            { Require(record.Choice == command.Choice && record.DecidedBy == subject, "DecisionIdConflict"); return record; }
            Current(record, policy);
            Require(record.Status == ApprovalStatus.Pending && record.Version == command.ExpectedVersion, "DecisionConflict");
            var control = await Control(c, tx, record.Operation.TaskId, token); Require(control?.CancelRequestId is null, "CancellationRequested");
            var next = record with { Status = command.Choice == ApprovalChoice.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
                Version = record.Version + 1, DecisionId = command.DecisionId, Choice = command.Choice, DecidedBy = subject };
            await Save(c, tx, record, next, next.Status == ApprovalStatus.Approved ? ControlAuditCode.Approved : ControlAuditCode.Rejected, subject, token);
            fault?.Invoke(ApprovalCheckpoint.DecisionWritten); return next;
        }, token);
    }

    public Task<ApprovalRecord> ExpireAsync(string approvalId, long expectedVersion, CancellationToken token = default)
        => Transaction(async (c, tx) =>
        {
            var r = await Read(c, tx, approvalId, token) ?? throw new InvalidOperationException("UnknownApproval");
            Require(r.Version == expectedVersion && r.Status is ApprovalStatus.Pending or ApprovalStatus.Approved && clock.GetUtcNow() >= r.ExpiresAt, "NotExpirable");
            var next = r with { Status = ApprovalStatus.Expired, Version = r.Version + 1 };
            await Save(c, tx, r, next, ControlAuditCode.Expired, "clock", token); return next;
        }, token);

    public async Task<ApprovalRecord> RevokeAsync(string approvalId, long expectedVersion, ITrustedCallerContext actor, CancellationToken token = default)
    {
        await using var authorityLease = await authority.AcquireAsync(token);
        var subject = Actor(actor); var policy = Candidate();
        return await GuardedTransaction(policy, async (c, tx) =>
        {
            await CheckActive(c, tx, policy, token);
            var r = await Read(c, tx, approvalId, token) ?? throw new InvalidOperationException("UnknownApproval");
            Require(authority.CanDecide(actor, r.Operation, r.RequiredOperatorScope) && subject != r.Operation.CallerSubject, "OperatorNotAuthorized");
            Require(r.Version == expectedVersion && r.Status is ApprovalStatus.Pending or ApprovalStatus.Approved, "NotRevocable");
            var next = r with { Status = ApprovalStatus.Revoked, Version = r.Version + 1 };
            await Save(c, tx, r, next, ControlAuditCode.Revoked, subject, token); return next;
        }, token);
    }

    public async Task<DurableExecution> ConsumeAsync(string approvalId, long expectedApprovalVersion, long expectedControlVersion,
        string expectedBindingDigest, DurableExecution initialExecution, ITrustedCallerContext caller, CancellationToken token = default)
    {
        await using var authorityLease = await authority.AcquireAsync(token);
        var subject = Actor(caller); var policy = Candidate();
        return await GuardedTransaction(policy, async (c, tx) =>
        {
            await CheckActive(c, tx, policy, token);
            var r = await Read(c, tx, approvalId, token) ?? throw new InvalidOperationException("UnknownApproval"); Current(r, policy);
            Require(r.Status == ApprovalStatus.Approved && r.Version == expectedApprovalVersion && r.BindingDigest == expectedBindingDigest, "ApprovalNotConsumable");
            Require(r.DecidedBy is not null && authority.IsDecisionSubjectStillAuthorized(r.DecidedBy, r.Operation, r.RequiredOperatorScope), "OperatorNoLongerAuthorized");
            Require(r.Operation.CallerSubject == subject && r.Operation.Operation == PolicyOperation.SubmitNewReadOnlyTask
                && authority.CanControl(caller, r.Operation), "CallerNotAuthorized");
            var control = await Control(c, tx, r.Operation.TaskId, token);
            Require(control is not null && control.Version == expectedControlVersion && control.CancelRequestId is null && control.ExecutionId == r.Operation.ExecutionId, "ControlConflict");
            var e = initialExecution; var op = r.Operation;
            Require(e.TaskId == op.TaskId && e.WorkflowRunId == op.WorkflowRunId && e.Handle.ExecutionId == op.ExecutionId
                && e.Handle.ExternalRequestId == op.MessageId && e.Handle.ExternalSessionId is null && e.RequestFingerprint == op.RequestFingerprint
                && e.ProviderScope == op.Scope.ProviderScopeDigest && e.ProjectRoot == op.Scope.ResourceId && e.State == DurableExecutionState.Created
                && e.SubmissionPhase == SubmissionPhase.None && e.Version == 0 && e.CompletedAt is null && e.ReconciledAt is null
                && e.ErrorCode is null && !e.AbortRequested && e.CreatedAt == e.UpdatedAt && e.CreatedAt >= r.CreatedAt && e.CreatedAt <= clock.GetUtcNow(), "ExecutionBindingMismatch");
            Id(e.Handle.Provider);
            Require(authority.MatchesExecutionBinding(op, e), "ExecutionProviderBindingMismatch");
            var next = r with { Status = ApprovalStatus.Consumed, Version = r.Version + 1 };
            await Save(c, tx, r, next, ControlAuditCode.Consumed, subject, token); fault?.Invoke(ApprovalCheckpoint.ApprovalConsumed);
            await ApprovalDatabase.Execute(c, tx, "INSERT INTO executions VALUES($t,0,0,$j)", token, ("$t", e.TaskId), ("$j", Json(e)));
            fault?.Invoke(ApprovalCheckpoint.ExecutionInserted); return e;
        }, token);
    }

    public async Task<TaskControl> RequestCancellationAsync(string approvalId, string cancelRequestId, long expectedControlVersion, ITrustedCallerContext actor, CancellationToken token = default)
    {
        await using var authorityLease = await authority.AcquireAsync(token);
        Id(cancelRequestId); var subject = Actor(actor); var policy = Candidate();
        return await GuardedTransaction(policy, async (c, tx) =>
        {
            await CheckActive(c, tx, policy, token);
            var r = await Read(c, tx, approvalId, token) ?? throw new InvalidOperationException("UnknownApproval");
            Require(authority.CanControl(actor, r.Operation), "CallerNotAuthorized");
            var control = await Control(c, tx, r.Operation.TaskId, token) ?? throw new InvalidOperationException("MissingControl");
            if (control.CancelRequestId == cancelRequestId) { Require(control.RequestedBy == subject, "CancellationIdConflict"); return control; }
            Require(control.Version == expectedControlVersion && control.CancelRequestId is null, "ControlConflict");
            var now = clock.GetUtcNow();
            Require(await ApprovalDatabase.Execute(c, tx, "UPDATE task_controls SET version=version+1,cancel_id=$i,actor=$a,requested_at=$at WHERE task_id=$t AND version=$v AND cancel_id IS NULL", token,
                ("$i", cancelRequestId), ("$a", subject), ("$at", Utc(now)), ("$t", control.TaskId), ("$v", expectedControlVersion)) == 1, "ControlConflict");
            using var cmd = ApprovalDatabase.Command(c, tx, "SELECT snapshot FROM approval_requests WHERE operation_id IN (SELECT operation_id FROM authorization_operations WHERE task_id=$t) AND status IN (0,1)", ("$t", control.TaskId));
            var approvals = new List<ApprovalRecord>(); using (var reader = await cmd.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) approvals.Add(JsonSerializer.Deserialize<ApprovalRecord>(reader.GetString(0))!);
            foreach (var a in approvals) await Save(c, tx, a, a with { Status = ApprovalStatus.Revoked, Version = a.Version + 1 }, ControlAuditCode.Revoked, subject, token);
            await Audit(c, tx, ControlAuditCode.CancellationRequested, subject, control.TaskId, approvalId, control.Version + 1, token);
            fault?.Invoke(ApprovalCheckpoint.CancellationWritten);
            return control with { Version = control.Version + 1, CancelRequestId = cancelRequestId, RequestedBy = subject, RequestedAt = now };
        }, token);
    }
}
