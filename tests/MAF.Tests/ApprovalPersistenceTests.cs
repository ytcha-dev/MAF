using System.Diagnostics;
using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class ApprovalPersistenceTests : IDisposable
{
    readonly ITestOutputHelper output;
    public ApprovalPersistenceTests(ITestOutputHelper output) => this.output = output;
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-approval-" + Guid.NewGuid().ToString("N"));
    readonly SyntheticClock clock = new();
    readonly SyntheticAuthority authority = new();
    string Db => Path.Combine(root, "synthetic.db");
    SqliteApprovalStore Store(Action<ApprovalCheckpoint>? fault = null, int capacity = 10000) => new(Db, authority, clock, capacity, fault);
    async Task Setup() { Directory.CreateDirectory(root); await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance); }
    async Task<ApprovalRecord> Pending() => await Store().RequestAsync("approval-one", SyntheticApproval.Request(), SyntheticApproval.Caller);
    async Task<ApprovalRecord> Approved()
    {
        var r = await Pending();
        return await Store().DecideAsync(new(r.ApprovalId, "decision-one", r.Version, r.BindingDigest, ApprovalChoice.Approve), SyntheticApproval.Operator);
    }
    async Task<object?> Sql(string sql)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await c.OpenAsync(); using var command = c.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }
    static async Task<bool> Succeeds(Func<Task> action) { try { await action(); return true; } catch (Exception e) when (e is InvalidOperationException or SqliteException) { return false; } }

    [Fact]
    public async Task FreshV2AndDecisionNeverCreateExecution()
    {
        await Setup(); var approved = await Approved();
        Assert.Equal(ApprovalStatus.Approved, approved.Status); Assert.Equal(1, approved.Version);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions")); Assert.Equal(2L, await Sql("PRAGMA user_version"));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteExecutionStore(Db).GetAsync("task-one"));
    }

    [Theory]
    [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, false)]
    public async Task ExplicitMaintenancePrerequisitesAreMandatory(bool mode, bool stopped, bool backup)
    {
        Directory.CreateDirectory(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovalSchema.InitializeOrMigrateAsync(Db, new(mode, stopped, backup)));
        Assert.False(File.Exists(Db));
    }

    async Task<string> V1(bool terminal = false)
    {
        Directory.CreateDirectory(root); var legacy = new SqliteExecutionStore(Db);
        await using (var owner = await legacy.TryAcquireAsync("task-one"))
        {
            var record = SyntheticApproval.Execution(SyntheticApproval.Request(), clock.Now);
            await legacy.CreateAsync(record, owner!);
            if (terminal)
            {
                var states = new[] { DurableExecutionState.SessionCreating, DurableExecutionState.ReadyToSubmit, DurableExecutionState.SubmissionIntended, DurableExecutionState.Running, DurableExecutionState.Completed };
                var phases = new[] { SubmissionPhase.SessionCreationIntended, SubmissionPhase.SessionKnown, SubmissionPhase.PromptIntended, SubmissionPhase.Acknowledged, SubmissionPhase.Acknowledged };
                for (var i = 0; i < states.Length; i++)
                {
                    var next = record with { State = states[i], SubmissionPhase = phases[i], Version = record.Version + 1,
                        Handle = i == 1 ? record.Handle with { ExternalSessionId = "synthetic-session" } : record.Handle,
                        CompletedAt = i == 4 ? clock.Now : null };
                    await legacy.UpdateAsync(record, next, owner!); record = next;
                }
            }
        }
        // Preserve deliberately noncanonical whitespace as exact bytes, not reserialized records.
        await Sql("UPDATE executions SET snapshot='  '||snapshot||char(10)");
        return (string)(await Sql("SELECT hex(CAST(snapshot AS BLOB)) FROM executions"))!;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MigrationPreservesV1SnapshotBytesAndNoImplicitMigrationOccurs(bool terminal)
    {
        var before = await V1(terminal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().GetAsync("approval-one"));
        Assert.Equal(1L, await Sql("PRAGMA user_version"));
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        Assert.Equal(before, await Sql("SELECT hex(CAST(snapshot AS BLOB)) FROM executions"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests"));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task FreshMigrationFaultLeavesNoPartialSchemaAndCanBeExplicitlyRetried()
    {
        Directory.CreateDirectory(root);
        await Assert.ThrowsAsync<IOException>(() => ApprovalSchema.MigrateAsync(Db, SyntheticApproval.Maintenance,
            p => { if (p == ApprovalCheckpoint.TablesCreated) throw new IOException("synthetic fault"); }));
        Assert.Equal(0L, await Sql("PRAGMA user_version"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        Assert.Equal(2L, await Sql("PRAGMA user_version"));
    }

    [Fact]
    public async Task CorruptV1SnapshotIsRejectedWithoutChangingBytesOrSchema()
    {
        await V1(); await Sql("UPDATE executions SET version=999");
        var before = await Sql("SELECT hex(CAST(snapshot AS BLOB)) FROM executions");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance));
        Assert.Equal(1L, await Sql("PRAGMA user_version"));
        Assert.Equal(before, await Sql("SELECT hex(CAST(snapshot AS BLOB)) FROM executions"));
    }

    [Fact]
    public async Task ExclusiveMaintenanceGateBlocksNormalStorageAccess()
    {
        await Setup();
        using var exclusive = new FileStream(Path.Combine(Db + ".locks", ".schema.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => Store().GetAsync("approval-one"));
        await Assert.ThrowsAsync<IOException>(() => new SqliteExecutionStore(Db).GetAsync("task-one"));
        await Assert.ThrowsAsync<IOException>(() => ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance));
    }

    [Theory]
    [InlineData("MigrationValidated")] [InlineData("TablesCreated")] [InlineData("SchemaVersionWritten")]
    public async Task MigrationFaultRollsBackAllDDLAndVersion(string checkpointName)
    {
        var checkpoint = Enum.Parse<ApprovalCheckpoint>(checkpointName);
        var before = await V1();
        await Assert.ThrowsAsync<IOException>(() => ApprovalSchema.MigrateAsync(Db, SyntheticApproval.Maintenance,
            p => { if (p == checkpoint) throw new IOException("synthetic fault"); }));
        Assert.Equal(1L, await Sql("PRAGMA user_version"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE name='approval_requests'"));
        Assert.Equal(before, await Sql("SELECT hex(CAST(snapshot AS BLOB)) FROM executions"));
        Assert.NotNull(await new SqliteExecutionStore(Db).GetAsync("task-one"));
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(99)]
    public async Task UnknownOrAlreadyMigratedVersionIsNotDowngraded(int version)
    {
        await V1(); await Sql($"PRAGMA user_version={version}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance));
        Assert.Equal((long)version, await Sql("PRAGMA user_version"));
    }

    [Fact]
    public async Task ActiveTaskOwnershipBlocksMigration()
    {
        await V1(); var legacy = new SqliteExecutionStore(Db); await using var owner = await legacy.TryAcquireAsync("task-one");
        await Assert.ThrowsAsync<IOException>(() => ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance));
        Assert.Equal(1L, await Sql("PRAGMA user_version"));
    }

    [Fact]
    public async Task MalformedSchemaAndUnversionedContentAreRejected()
    {
        Directory.CreateDirectory(root);
        await using (var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWriteCreate)))
        {
            await c.OpenAsync(); using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE executions(task_id TEXT PRIMARY KEY NOT NULL,version INTEGER NOT NULL,terminal INTEGER NOT NULL,snapshot TEXT NOT NULL); PRAGMA user_version=1;";
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance));
        await Sql("PRAGMA user_version=0");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE name='approval_requests'"));
    }

    [Fact]
    public async Task ConcurrentApproveRejectHasExactlyOneWinnerAndDuplicateDecisionIsIdempotent()
    {
        await Setup(); var r = await Pending();
        var approve = new ApprovalDecisionCommand(r.ApprovalId, "decision-a", 0, r.BindingDigest, ApprovalChoice.Approve);
        var reject = approve with { DecisionId = "decision-b", Choice = ApprovalChoice.Reject };
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Run(ApprovalDecisionCommand command) { await barrier.Task; return await Succeeds(() => Store().DecideAsync(command, SyntheticApproval.Operator)); }
        var a = Task.Run(() => Run(approve)); var b = Task.Run(() => Run(reject)); barrier.SetResult();
        Assert.Single(await Task.WhenAll(a, b), result => result);
        var result = (await Store().GetAsync(r.ApprovalId))!; Assert.Equal(1, result.Version);
        var winning = result.Choice == ApprovalChoice.Approve ? approve : reject;
        Assert.Equal(result, await Store().DecideAsync(winning, SyntheticApproval.Operator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().DecideAsync(winning with { Choice = winning.Choice == ApprovalChoice.Approve ? ApprovalChoice.Reject : ApprovalChoice.Approve }, SyntheticApproval.Operator));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM control_audit WHERE code IN (1,2)"));
        output.WriteLine($"Decision race: winner={result.Choice}, version={result.Version}, decisionRows=1, executionRows=0");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExpiryRejectsConsumptionAndCanBePersisted(bool approved)
    {
        await Setup(); var r = approved ? await Approved() : await Pending(); clock.Now = r.ExpiresAt;
        Assert.False(await Succeeds(() => Store().ConsumeAsync(r.ApprovalId, r.Version, 0, r.BindingDigest, SyntheticApproval.Execution(r.Operation, clock.Now), SyntheticApproval.Caller)));
        var expired = await Store().ExpireAsync(r.ApprovalId, r.Version);
        Assert.Equal(ApprovalStatus.Expired, expired.Status); Assert.Equal(r.Version + 1, expired.Version);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
    }

    [Fact]
    public async Task RevocationAndStaleVersionAreProtected()
    {
        await Setup(); var r = await Approved();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().RevokeAsync(r.ApprovalId, 0, SyntheticApproval.Operator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().RevokeAsync(r.ApprovalId, r.Version, SyntheticApproval.Caller));
        var revoked = await Store().RevokeAsync(r.ApprovalId, r.Version, SyntheticApproval.Operator);
        Assert.Equal(ApprovalStatus.Revoked, revoked.Status);
        Assert.False(await Succeeds(() => Store().ConsumeAsync(r.ApprovalId, revoked.Version, 0, r.BindingDigest, SyntheticApproval.Execution(r.Operation, clock.Now), SyntheticApproval.Caller)));
    }

    [Fact]
    public async Task DuplicateIdsCannotChangeBindingOrOperation()
    {
        await Setup(); var r = await Pending(); Assert.Equal(r, await Pending());
        Assert.False(await Succeeds(() => Store().RequestAsync(r.ApprovalId, r.Operation with { TaskId = "other" }, SyntheticApproval.Caller)));
        await Assert.ThrowsAsync<SqliteException>(() => Store().RequestAsync("another-approval", r.Operation, SyntheticApproval.Caller));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM approval_requests"));
    }

    [Fact]
    public async Task DecisionIdsAreGloballyUniqueAndSelfApprovalIsRejected()
    {
        await Setup(); await Approved();
        var second = await Store().RequestAsync("approval-two", SyntheticApproval.Request("two"), SyntheticApproval.Caller);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().DecideAsync(new(second.ApprovalId, "decision-two", 0, second.BindingDigest, ApprovalChoice.Approve), SyntheticApproval.Caller));
        await Assert.ThrowsAsync<SqliteException>(() => Store().DecideAsync(new(second.ApprovalId, "decision-one", 0, second.BindingDigest, ApprovalChoice.Approve), SyntheticApproval.Operator));
        Assert.Equal(ApprovalStatus.Pending, (await Store().GetAsync(second.ApprovalId))!.Status);
    }

    [Theory]
    [InlineData("PendingInserted")] [InlineData("DecisionWritten")]
    [InlineData("ApprovalConsumed")] [InlineData("ExecutionInserted")] [InlineData("CancellationWritten")]
    public async Task AtomicMutationFaultDoesNotLeavePartialState(string checkpointName)
    {
        var point = Enum.Parse<ApprovalCheckpoint>(checkpointName);
        await Setup(); var r = point == ApprovalCheckpoint.PendingInserted ? null : await Approved();
        var before = Convert.ToInt64(await Sql("SELECT count(*) FROM control_audit"));
        var failing = Store(p => { if (p == point) throw new IOException("synthetic fault"); });
        if (point == ApprovalCheckpoint.PendingInserted)
            await Assert.ThrowsAsync<IOException>(() => failing.RequestAsync("approval-one", SyntheticApproval.Request(), SyntheticApproval.Caller));
        else if (point == ApprovalCheckpoint.DecisionWritten)
        {
            var second = await Store().RequestAsync("approval-two", SyntheticApproval.Request("two"), SyntheticApproval.Caller);
            before++;
            await Assert.ThrowsAsync<IOException>(() => failing.DecideAsync(new(second.ApprovalId, "decision-two", 0, second.BindingDigest, ApprovalChoice.Approve), SyntheticApproval.Operator));
            Assert.Equal(ApprovalStatus.Pending, (await Store().GetAsync(second.ApprovalId))!.Status);
        }
        else if (point == ApprovalCheckpoint.CancellationWritten)
            await Assert.ThrowsAsync<IOException>(() => failing.RequestCancellationAsync(r!.ApprovalId, "cancel", 0, SyntheticApproval.Caller));
        else await Assert.ThrowsAsync<IOException>(() => failing.ConsumeAsync(r!.ApprovalId, r.Version, 0, r.BindingDigest, SyntheticApproval.Execution(r.Operation, clock.Now), SyntheticApproval.Caller));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
        if (r is not null) { Assert.Equal(r, await Store().GetAsync(r.ApprovalId)); Assert.Null((await Store().GetControlAsync(r.Operation.TaskId))!.CancelRequestId); }
        else Assert.Null(await Store().GetAsync("approval-one"));
        // First request may independently pin the trusted authority; operation/audit changes are atomic.
        Assert.Equal(before + (r is null ? 1 : 0), Convert.ToInt64(await Sql("SELECT count(*) FROM control_audit")));
    }

    [Fact]
    public async Task ConsumptionAtomicallyCreatesOnlyOneExactCreatedSnapshot()
    {
        await Setup(); var r = await Approved(); var e = SyntheticApproval.Execution(r.Operation, clock.Now);
        Assert.Equal(e, await Store().ConsumeAsync(r.ApprovalId, r.Version, 0, r.BindingDigest, e, SyntheticApproval.Caller));
        Assert.Equal(ApprovalStatus.Consumed, (await Store().GetAsync(r.ApprovalId))!.Status);
        Assert.Equal(e, JsonSerializer.Deserialize<DurableExecution>((string)(await Sql("SELECT snapshot FROM executions"))!));
        Assert.False(await Succeeds(() => Store().ConsumeAsync(r.ApprovalId, r.Version, 0, r.BindingDigest, e, SyntheticApproval.Caller)));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM executions"));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task ConcurrentConsumptionHasOneWinner()
    {
        await Setup(); var r = await Approved(); var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Consume() { await barrier.Task; return await Succeeds(() => Store().ConsumeAsync(r.ApprovalId, r.Version, 0, r.BindingDigest, SyntheticApproval.Execution(r.Operation, clock.Now), SyntheticApproval.Caller)); }
        var first = Task.Run(Consume); var second = Task.Run(Consume); barrier.SetResult();
        Assert.Single(await Task.WhenAll(first, second), value => value);
        Assert.Equal(1L, await Sql("SELECT count(*) FROM executions"));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM control_audit WHERE code=5"));
        output.WriteLine("Consumption race: winners=1, executionRows=1, consumptionAuditRows=1");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationAndAdmissionBothDeterministicOrdersRemainHonest(bool admissionFirst)
    {
        await Setup(); var r = await Approved();
        if (admissionFirst) await Store().ConsumeAsync(r.ApprovalId, r.Version, 0, r.BindingDigest, SyntheticApproval.Execution(r.Operation, clock.Now), SyntheticApproval.Caller);
        await Store().RequestCancellationAsync(r.ApprovalId, "cancel", 0, SyntheticApproval.Caller);
        if (!admissionFirst) Assert.False(await Succeeds(() => Store().ConsumeAsync(r.ApprovalId, r.Version, 0, r.BindingDigest, SyntheticApproval.Execution(r.Operation, clock.Now), SyntheticApproval.Caller)));
        Assert.Equal(admissionFirst ? 1L : 0L, await Sql("SELECT count(*) FROM executions"));
        if (admissionFirst)
        {
            var execution = JsonSerializer.Deserialize<DurableExecution>((string)(await Sql("SELECT snapshot FROM executions"))!)!;
            Assert.Equal(DurableExecutionState.Created, execution.State); Assert.False(execution.AbortRequested);
        }
    }

    [Theory]
    [InlineData("binding")] [InlineData("execution")] [InlineData("scope")] [InlineData("operator")] [InlineData("control")] [InlineData("approval")] [InlineData("provider")]
    public async Task ConsumptionRevalidatesAllAdmissionInputs(string change)
    {
        await Setup(); var r = await Approved(); var e = SyntheticApproval.Execution(r.Operation, clock.Now);
        if (change == "execution") e = e with { Handle = e.Handle with { ExecutionId = "other" } };
        if (change == "scope") e = e with { ProjectRoot = "other" };
        if (change == "provider") e = e with { Handle = e.Handle with { Provider = "DifferentProvider" } };
        if (change == "operator") authority.OperatorEnabled = false;
        Assert.False(await Succeeds(() => Store().ConsumeAsync(r.ApprovalId, change == "approval" ? 0 : r.Version, change == "control" ? 1 : 0,
            change == "binding" ? new('D', 64) : r.BindingDigest, e, SyntheticApproval.Caller)));
        Assert.Equal(ApprovalStatus.Approved, (await Store().GetAsync(r.ApprovalId))!.Status);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
    }

    [Fact]
    public async Task CancellationRacingAdmissionHasAValidSerialOrderAndNeverDispatches()
    {
        await Setup(); var r = await Approved(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admit = Task.Run(async () => { await gate.Task; return await Succeeds(() => Store().ConsumeAsync(r.ApprovalId, r.Version, 0, r.BindingDigest, SyntheticApproval.Execution(r.Operation, clock.Now), SyntheticApproval.Caller)); });
        var cancel = Task.Run(async () => { await gate.Task; return await Store().RequestCancellationAsync(r.ApprovalId, "cancel", 0, SyntheticApproval.Caller); });
        gate.SetResult(); var admitted = await admit; var control = await cancel;
        Assert.Equal(1, control.Version); Assert.Equal("cancel", control.CancelRequestId);
        Assert.Equal(admitted ? 1L : 0L, await Sql("SELECT count(*) FROM executions"));
        Assert.Equal(admitted ? ApprovalStatus.Consumed : ApprovalStatus.Revoked, (await Store().GetAsync(r.ApprovalId))!.Status);
        Assert.Equal(control, await Store().RequestCancellationAsync(r.ApprovalId, "cancel", 0, SyntheticApproval.Caller));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().RequestCancellationAsync(r.ApprovalId, "different", 0, SyntheticApproval.Caller));
        output.WriteLine($"Cancellation/admission race: admitted={admitted}, controlVersion={control.Version}, cancelId={control.CancelRequestId}, executionRows={(admitted ? 1 : 0)}, providerDispatch=notImplemented");
    }

    [Fact]
    public async Task AuthorityHighWaterSurvivesRejectedOperationAndRejectsRollback()
    {
        await Setup(); var r = await Approved();
        authority.Snapshot = authority.Snapshot with { Epoch = new(2), Version = new(2), Rules = [authority.Snapshot.Rules[0] with { Outcome = PolicyOutcome.Deny }] };
        Assert.False(await Succeeds(() => Store().ConsumeAsync(r.ApprovalId, r.Version, 0, r.BindingDigest, SyntheticApproval.Execution(r.Operation, clock.Now), SyntheticApproval.Caller)));
        Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority"));
        authority.Snapshot = SyntheticApproval.Policy;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().RequestAsync("other", SyntheticApproval.Request("two"), SyntheticApproval.Caller));
        Assert.Equal(ApprovalStatus.Approved, (await Store().GetAsync(r.ApprovalId))!.Status); // Stored Approved is not execution authority.
    }

    [Theory]
    [InlineData("epoch")] [InlineData("version")] [InlineData("same-version-policy")] [InlineData("untrusted-request")]
    public async Task CallerCannotActivateAuthorityRevision(string change)
    {
        await Setup(); await Pending(); var request = SyntheticApproval.Request("two");
        if (change == "epoch") authority.Snapshot = authority.Snapshot with { Epoch = new(2) };
        if (change == "version") authority.Snapshot = authority.Snapshot with { Version = new(2) };
        if (change == "same-version-policy") authority.Snapshot = authority.Snapshot with { Rules = [] };
        if (change == "untrusted-request") request = request with { Epoch = new(999), Version = new(999) };
        Assert.False(await Succeeds(() => Store().RequestAsync("other", request, SyntheticApproval.Caller)));
        Assert.Equal(1L, await Sql("SELECT epoch FROM policy_authority"));
    }

    [Fact]
    public async Task AuthorityChangeDuringDecisionRollsBackDecisionAndAudit()
    {
        await Setup(); var r = await Pending();
        var store = Store(point =>
        {
            if (point == ApprovalCheckpoint.DecisionWritten)
                authority.Snapshot = authority.Snapshot with { Epoch = new(2), Version = new(2) };
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DecideAsync(new(r.ApprovalId, "decision", 0, r.BindingDigest, ApprovalChoice.Approve), SyntheticApproval.Operator));
        Assert.Equal(r, await Store().GetAsync(r.ApprovalId));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM control_audit WHERE code=1"));
    }

    [Fact]
    public async Task AuditIsStructuredBoundedAndCapacityFailureRollsBackMutation()
    {
        await Setup(); await Approved();
        var audit = await Store().ReadAuditAsync(); Assert.Equal(3, audit.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(capacity: 3).RevokeAsync("approval-one", 1, SyntheticApproval.Operator));
        Assert.Equal(ApprovalStatus.Approved, (await Store().GetAsync("approval-one"))!.Status);
        Assert.Equal(3, (await Store().ReadAuditAsync()).Count);
        Assert.Single(await Store().ReadAuditAsync(audit[1].Sequence));
        Assert.Empty(await Store().ListPendingAsync());
    }

    [Fact]
    public async Task MetadataStorageDoesNotAcceptPromptTranscriptOrCredentialPayloads()
    {
        await Setup(); await Approved();
        var pending = await Store().RequestAsync("approval-two", SyntheticApproval.Request("two"), SyntheticApproval.Caller);
        await Assert.ThrowsAsync<IOException>(() => Store(p => { if (p == ApprovalCheckpoint.DecisionWritten) throw new IOException("synthetic-provider-transcript"); })
            .DecideAsync(new(pending.ApprovalId, "decision-two", 0, pending.BindingDigest, ApprovalChoice.Approve), SyntheticApproval.Operator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().RequestAsync("approval-secret", SyntheticApproval.Request("secret"), new SyntheticActor("submitter", "synthetic-api-key")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().RequestAsync("synthetic-private-prompt with spaces", SyntheticApproval.Request("three"), SyntheticApproval.Caller));
        var bytes = await File.ReadAllBytesAsync(Db); var text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var marker in new[] { "synthetic-private-prompt", "synthetic-api-key", "synthetic-provider-transcript" }) Assert.DoesNotContain(marker, text);
        Assert.DoesNotContain("AuthenticationContextId", text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().RequestAsync("bad approval containing private payload", SyntheticApproval.Request("two"), SyntheticApproval.Caller));
    }

    static Process Child(string[] args)
    {
        var dll = typeof(SyntheticApproval).Assembly.Location;
        // Use the helper's original build output so its runtimeconfig/deps are present.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MAF.ApprovalAcceptance/bin", configuration, "net10.0/MAF.ApprovalAcceptance.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(dll); foreach (var a in args) start.ArgumentList.Add(a); return Process.Start(start)!;
    }
    [Fact]
    public async Task PendingSurvivesActualProcessTerminationAndFreshProcessRead()
    {
        Directory.CreateDirectory(root); using var writer = Child(["seed", Db]); var start = writer.StartTime;
        try
        {
            Assert.Equal("pending-committed", await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.False(writer.HasExited); Assert.Equal(start, writer.StartTime); writer.Kill(entireProcessTree: false); await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var reader = Child(["read", Db]); var json = await reader.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20)); await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, reader.ExitCode); var record = JsonSerializer.Deserialize<ApprovalRecord>(json)!;
            Assert.Equal("approval-one", record.ApprovalId); Assert.Equal(ApprovalStatus.Pending, record.Status); Assert.Equal(SyntheticApproval.Request(), record.Operation);
            Assert.Equal(OperationBinding.Digest(record.Operation, SyntheticApproval.Descriptor), record.BindingDigest);
            Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
            output.WriteLine($"Pending persisted: writer PID={writer.Id}, startUTC={start.ToUniversalTime():O}, reader PID={reader.Id}, approval={record.ApprovalId}, operation={record.Operation.OperationId}, task={record.Operation.TaskId}, execution={record.Operation.ExecutionId}, state={record.Status}, version={record.Version}, integrity=ok");
        }
        finally { if (!writer.HasExited) { writer.Kill(entireProcessTree: false); writer.WaitForExit(5000); } }
    }
    [Theory]
    [InlineData("TablesCreated")] [InlineData("SchemaVersionWritten")]
    public async Task AbruptMigrationProcessDeathRollsBack(string checkpointName)
    {
        var point = Enum.Parse<ApprovalCheckpoint>(checkpointName);
        var before = await V1(); using var writer = Child(["migrate-crash", Db, point.ToString()]); var start = writer.StartTime;
        try
        {
            Assert.Equal("migration-checkpoint", await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.False(writer.HasExited); Assert.Equal(start, writer.StartTime); writer.Kill(entireProcessTree: false); await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1L, await Sql("PRAGMA user_version")); Assert.Equal(before, await Sql("SELECT hex(CAST(snapshot AS BLOB)) FROM executions"));
            Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE name='approval_requests'")); Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
            output.WriteLine($"Migration crash: PID={writer.Id}, startUTC={start.ToUniversalTime():O}, checkpoint={point}, schema=1, snapshotBytesUnchanged=true, integrity=ok");
        }
        finally { if (!writer.HasExited) { writer.Kill(entireProcessTree: false); writer.WaitForExit(5000); } }
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        // Only this instance's synthetic directory, never a supplied path.
        foreach (var file in Directory.GetFiles(root)) File.Delete(file);
        if (Directory.Exists(Db + ".locks")) { foreach (var file in Directory.GetFiles(Db + ".locks")) File.Delete(file); Directory.Delete(Db + ".locks"); }
        Directory.Delete(root);
    }
}
