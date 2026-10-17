using System.Diagnostics;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class AbortDeliveryTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-abort-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    async Task<object?> Sql(string sql)
    { await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite)); await c.OpenAsync(); return await ApprovalDatabase.Scalar(c, null, sql, default); }
    async Task<SyntheticAbort> Seed() { var a = new SyntheticAbort(Db); await a.Seed(); return a; }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExactAbortAllowSendsOriginalSessionOnceWithNoInspectAndAckIsNonterminal(bool approved)
    {
        var a = new SyntheticAbort(Db, approved); await a.Seed(); var f = a.F; var store = f.Store();
        var before = await f.Executions.GetAsync("runtime-task");
        await using var executionOwner = (await f.Executions.TryAcquireAsync("runtime-task"))!;
        await using var owner = (await store.TryAcquireDeliveryAsync("cancel-one"))!;
        var ticket = await store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(ticket));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<AbortDeliveryContinuation>("{}"));
        using var fake = new RuntimeHttpFixture(f.Runtime.Root);
        fake.OnRequest = async (request, _) =>
        {
            Assert.Equal(new Uri(f.Runtime.Options.BaseUri, "session/ses_runtime/abort?directory=" + Uri.EscapeDataString(f.Runtime.Root)), request.RequestUri);
            Assert.Null(request.Content); Assert.Equal(1L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
        };
        using var http = ReceiptTransport.ForTests(fake);
        var evidence = await ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options);
        Assert.Equal("Acknowledged", evidence.Stage); Assert.Equal("None", evidence.RemoteOutcome);
        Assert.Equal(new[] { "POST /session/ses_runtime/abort" }, fake.Requests);
        Assert.Equal(before, await f.Executions.GetAsync("runtime-task")); Assert.False(before!.IsTerminal); Assert.False(before.AbortRequested);
        var row = (await store.ReadDeliveryStorageAsync("delivery-one"))!;
        Assert.Equal(2, row.Stage); Assert.Equal(0, row.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner));
        Assert.Single(fake.Requests);
        output.WriteLine($"lifecycle setup GET/Create/Prompt/Abort={a.SeedCalls.Count(x => x.StartsWith("GET "))}/{a.SeedCalls.Count(x => x == "POST /session")}/{a.SeedCalls.Count(x => x.Contains("prompt_async"))}/0; delivery GET/Create/Prompt/Abort=0/0/0/1; original/target=runtime-task/runtime-execution/runtime-run/msg_runtime/ses_runtime; Ack=nonterminal; execution unchanged");
    }
    [Theory]
    [InlineData("submit")] [InlineData("inspect")] [InlineData("cancel")] [InlineData("deny")]
    [InlineData("approval")] [InlineData("forged")] [InlineData("missing")] [InlineData("wrong-caller")]
    [InlineData("scope")] [InlineData("epoch")] [InlineData("revision")] [InlineData("task")] [InlineData("execution")]
    [InlineData("expired")] [InlineData("source")] [InlineData("revoked")] [InlineData("expired-identity")]
    public async Task UnauthorizedReservationCannotProduceAnyProviderEffect(string variant)
    {
        var a = await Seed(); var f = a.F;
        var inputs = a.Inputs; var command = a.Command; ITrustedCallerContext caller = f.Runtime.Caller;
        if (variant is "submit" or "inspect" or "cancel" or "deny" or "approval")
        {
            var rules = f.Policy.Rules.Where(r => r.Operation != PolicyOperation.AbortExistingExecution).ToImmutableArray();
            if (variant is "submit" or "inspect" or "cancel")
            {
                var only = variant == "submit" ? PolicyOperation.SubmitNewReadOnlyTask : variant == "inspect" ? PolicyOperation.InspectExistingExecution : PolicyOperation.RequestCancelExecution;
                rules = [.. rules.Where(r => r.Operation == only)];
                if (variant == "cancel") caller = f.Canceller;
            }
            if (variant is "deny" or "approval") rules = rules.Add(new(f.Runtime.Caller.SubjectId, a.Capability.ConnectorId, a.Capability.Capability,
                PolicyOperation.AbortExistingExecution, inputs.Scope, variant == "deny" ? PolicyOutcome.Deny : PolicyOutcome.RequireApproval, "synthetic-operator"));
            f.Policy = f.Policy with { Epoch = new(2), Version = new(2), Rules = rules };
            await f.Store().ActivatePolicyAsync(); inputs = inputs with { Epoch = new(2), Version = new(2) };
        }
        if (variant == "forged") caller = new SyntheticActor(f.Runtime.Caller.SubjectId);
        if (variant == "wrong-caller") caller = f.Canceller;
        if (variant == "missing") caller = null!;
        if (variant == "scope") inputs = inputs with { Scope = inputs.Scope with { ScopeRevision = 2 } };
        if (variant == "epoch") inputs = inputs with { Epoch = new(2) };
        if (variant == "revision") inputs = inputs with { Version = new(2) };
        if (variant == "task") command = command with { TaskId = "other-task" };
        if (variant == "execution") command = command with { ExecutionId = "other-execution" };
        if (variant == "expired") f.Clock.Now = f.Clock.Now.AddHours(2);
        if (variant == "expired-identity") f.ExpiredIdentity = true;
        if (variant == "source") f.Available = false;
        if (variant == "revoked") f.Enabled = false;
        await using var owner = (await f.Store().TryAcquireDeliveryAsync("cancel-one"))!;
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Store().ReserveAbortAsync(command, inputs, caller, owner));
        Assert.Empty(fake.Requests); Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
    }
    [Theory] [InlineData("lost")] [InlineData("timeout")] [InlineData("outage")] [InlineData("invalid")] [InlineData("foreign")]
    public async Task UncertainHttpCannotAuthorizeRetryOrTerminalState(string variant)
    {
        var a = await Seed(); var f = a.F; var before = await f.Executions.GetAsync("runtime-task");
        await using var owner = (await f.Store().TryAcquireDeliveryAsync("cancel-one"))!;
        var ticket = await f.Store().ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        using var fake = new AbortHandler(variant); using var http = ReceiptTransport.ForTests(fake);
        var e = await ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options);
        Assert.Equal("SentUncertain", e.Stage); Assert.Equal("None", e.RemoteOutcome); Assert.Equal(1, fake.Calls);
        Assert.Equal(before, await f.Executions.GetAsync("runtime-task"));
        Assert.Equal(1, (await f.Store().ReadDeliveryStorageAsync("delivery-one"))!.Stage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner));
    }
    [Theory] [InlineData("policy")] [InlineData("unactivated")] [InlineData("identity")] [InlineData("cancel-token")]
    [InlineData("origin")] [InlineData("root")] [InlineData("opaque")] [InlineData("control")]
    public async Task FinalPrewireChangesSuppressWithoutPost(string variant)
    {
        var a = await Seed(); var f = a.F; var store = f.Store();
        await using var owner = (await store.TryAcquireDeliveryAsync("cancel-one"))!;
        var ticket = await store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        if (variant is "policy" or "unactivated")
        {
            f.Policy = f.Policy with { Epoch = new(2), Version = new(2), Rules = [.. f.Policy.Rules.Where(r => r.Operation != PolicyOperation.AbortExistingExecution)] };
            if (variant == "policy") await store.ActivatePolicyAsync();
        }
        if (variant == "identity") f.Enabled = false;
        if (variant == "control") await Sql("UPDATE task_controls SET version=version+1");
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = variant == "opaque" ? new HttpClient(fake) : ReceiptTransport.ForTests(fake);
        var options = f.Runtime.Options;
        if (variant == "origin") options = options with { BaseUri = new("http://127.0.0.1:12345") };
        if (variant == "root") options = options with { ApprovedProjectRoot = Path.GetTempPath() };
        var result = await ProtectedAbortDelivery.DeliverAsync(ticket, http, options, variant == "cancel-token" ? new CancellationToken(true) : default);
        Assert.Equal("SuppressedBeforeWire", result.Stage); Assert.Empty(fake.Requests);
        Assert.Equal(3, (await store.ReadDeliveryStorageAsync("delivery-one"))!.Stage);
    }
    [Fact]
    public async Task ChangedAuthorityDuringPostDoesNotRewriteOriginalAck()
    {
        var a = await Seed(); var f = a.F;
        await using var owner = (await f.Store().TryAcquireDeliveryAsync("cancel-one"))!;
        var ticket = await f.Store().ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        using var fake = new RuntimeHttpFixture(f.Runtime.Root);
        fake.OnRequest = async (_, _) =>
        { f.Policy = f.Policy with { Epoch = new(2), Version = new(2), Rules = [] }; await f.Store().ActivatePolicyAsync(); };
        using var http = ReceiptTransport.ForTests(fake);
        Assert.Equal("Acknowledged", (await ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options)).Stage);
        var row = (await f.Store().ReadDeliveryStorageAsync("delivery-one"))!;
        Assert.Equal(1, row.Intent.Operation.GetProperty("Epoch").GetProperty("Value").GetInt32()); Assert.Equal(0, row.Outcome);
    }
    [Theory] [InlineData("DeliveryIntentWritten")] [InlineData("DeliveryEvidenceWritten")]
    public async Task FaultRollsBackAuditAndCorrespondingLedgerMutation(string point)
    {
        var a = await Seed(); var f = a.F; var beforeAudit = await Sql("SELECT count(*) FROM control_audit");
        var store = f.Store(p => { if (p == point) throw new InvalidOperationException("SyntheticFault"); });
        await using var owner = (await store.TryAcquireDeliveryAsync("cancel-one"))!;
        if (point == "DeliveryIntentWritten")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner));
            Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_deliveries")); Assert.Equal(beforeAudit, await Sql("SELECT count(*) FROM control_audit"));
        }
        else
        {
            var ticket = await store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
            var reservedAudit = await Sql("SELECT count(*) FROM control_audit");
            using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options));
            Assert.Single(fake.Requests); Assert.Equal(reservedAudit, await Sql("SELECT count(*) FROM control_audit"));
            Assert.Equal(0, (await f.Store().ReadDeliveryStorageAsync("delivery-one"))!.Stage);
        }
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    [Theory] [InlineData("DeliveryIntentWritten", false, 0)] [InlineData("DeliveryIntentCommitted", true, 0)]
    [InlineData("AbortWireEntered", true, 1)] [InlineData("DeliveryEvidenceWritten", true, 1)] [InlineData("AbortEvidenceCommitted", true, 1)]
    public async Task RealProcessCrashNeverReissuesCommittedAttempt(string point, bool committed, int calls)
    {
        _ = await Seed();
        using var child = CancellationSchemaTests.Child("abort-run", Db, point); var start = child.StartTime;
        try
        {
            Assert.Equal(point, await CancellationSchemaTests.Line(child)); await CancellationSchemaTests.Kill(child);
            using var reader = CancellationSchemaTests.Child("abort-read", Db);
            var json = await reader.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, reader.ExitCode);
            Assert.Equal(committed ? 1L : 0L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
            using var restart = CancellationSchemaTests.Child("abort-run", Db);
            var result = await restart.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, restart.ExitCode);
            Assert.Contains(committed ? "DeliveryAlreadyReserved" : "Acknowledged", result);
            var requests = File.Exists(Db + ".abort-http") ? File.ReadAllLines(Db + ".abort-http") : [];
            Assert.Equal(committed ? calls : 1, requests.Length);
            Assert.All(requests, x => Assert.Equal("POST /session/ses_runtime/abort", x));
            Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
            output.WriteLine($"checkpoint={point}; PID={child.Id}; startUTC={start.ToUniversalTime():O}; readerPID={reader.Id}; restartPID={restart.Id}; original/target=runtime-task/runtime-execution/ses_runtime; GET/Create/Prompt=0/0/0; Abort={requests.Length}; evidence={json.Trim()}; integrity=ok");
        }
        finally { await CancellationSchemaTests.Kill(child); }
    }
    [Fact]
    public async Task IndependentProcessesCompeteWhileExecutionOwnerIsHeld()
    {
        _ = await Seed();
        using var execution = CancellationSchemaTests.Child("abort-owner", Db);
        using var first = CancellationSchemaTests.Child("abort-run", Db, "DeliveryIntentCommitted");
        try
        {
            Assert.Equal("ExecutionOwned", await CancellationSchemaTests.Line(execution));
            Assert.Equal("DeliveryIntentCommitted", await CancellationSchemaTests.Line(first));
            using var second = CancellationSchemaTests.Child("abort-run", Db);
            var result = await second.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, second.ExitCode); Assert.Contains("DeliveryBusy", result);
            await first.StandardInput.WriteLineAsync("continue"); await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, first.ExitCode);
            Assert.Single(File.ReadAllLines(Db + ".abort-http")); Assert.Equal(1L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
            output.WriteLine($"ExecutionOwnerPID={execution.Id} startUTC={execution.StartTime.ToUniversalTime():O}; winnerPID={first.Id} startUTC={first.StartTime.ToUniversalTime():O}; competitorPID={second.Id}; original/target=runtime-task/runtime-execution/ses_runtime; reservations=1; GET/Create/Prompt/Abort=0/0/0/1");
        }
        finally
        {
            await CancellationSchemaTests.Kill(first);
            if (!execution.HasExited)
            { await execution.StandardInput.WriteLineAsync("release"); await execution.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }
    [Theory] [InlineData("missing-cancel")] [InlineData("empty-session")] [InlineData("terminal")]
    public async Task IneligibleTargetsDoNotReserveOrSend(string variant)
    {
        var a = new SyntheticAbort(Db); await a.Seed(cancellation: variant != "missing-cancel", emptySession: variant == "empty-session");
        var f = a.F;
        if (variant == "terminal")
        {
            await using var executionOwner = (await f.Executions.TryAcquireAsync("runtime-task"))!;
            var e = (await f.Executions.GetAsync("runtime-task"))!;
            await f.Executions.UpdateAsync(e, e with { State = DurableExecutionState.Completed, CompletedAt = f.Clock.Now, Version = e.Version + 1 }, executionOwner);
        }
        await using var owner = (await f.Store().TryAcquireDeliveryAsync("cancel-one"))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
    }
    [Fact]
    public async Task AbortOnlyGrantDoesNotNeedInspectSubmitOrCancelGrant()
    {
        var a = await Seed(); var f = a.F;
        f.Policy = f.Policy with { Epoch = new(2), Version = new(2), Rules = [.. f.Policy.Rules.Where(r => r.Operation == PolicyOperation.AbortExistingExecution)] };
        await f.Store().ActivatePolicyAsync();
        await using var owner = (await f.Store().TryAcquireDeliveryAsync("cancel-one"))!;
        var ticket = await f.Store().ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        Assert.Equal("Acknowledged", (await ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options)).Stage);
        Assert.Equal(new[] { "POST /session/ses_runtime/abort" }, fake.Requests);
    }
    [Theory] [InlineData("disposed")] [InlineData("foreign")] [InlineData("wrong-target")]
    public async Task OwnershipCannotBeForgedOrReused(string variant)
    {
        var a = await Seed(); var f = a.F;
        await using var owner = (await f.Store().TryAcquireDeliveryAsync(variant == "wrong-target" ? "other-cancel" : "cancel-one"))!;
        if (variant == "disposed") await owner.DisposeAsync();
        var store = variant == "foreign" ? new SyntheticAbort(Path.Combine(root, "foreign.db")).F.Store() : f.Store();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
    }
    [Fact]
    public async Task StaleLockFileIsNotLiveOwnershipAndDisposedContinuationCannotSend()
    {
        var a = await Seed(); var f = a.F;
        var directory = Db + ".delivery-locks"; Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, DurableExecution.Hash("cancel-one") + ".lock"), "stale");
        var owner = (await f.Store().TryAcquireDeliveryAsync("cancel-one"))!; Assert.NotNull(owner);
        var ticket = await f.Store().ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        await owner.DisposeAsync();
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options));
        Assert.Empty(fake.Requests); Assert.Equal(0, (await f.Store().ReadDeliveryStorageAsync("delivery-one"))!.Stage);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AuditExhaustionCannotFabricateReservationOrAck(bool afterReservation)
    {
        var a = await Seed(); var f = a.F;
        var capacity = Convert.ToInt32(await Sql("SELECT count(*) FROM control_audit")) + (afterReservation ? 1 : 0);
        var store = f.Store(capacity: capacity);
        await using var owner = (await store.TryAcquireDeliveryAsync("cancel-one"))!;
        if (!afterReservation)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner));
            Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
        }
        else
        {
            var ticket = await store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
            using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options));
            Assert.Single(fake.Requests); Assert.Equal(0, (await f.Store().ReadDeliveryStorageAsync("delivery-one"))!.Stage);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner));
        }
    }
    [Fact]
    public async Task NewAbortCanonicalEncodingMatchesReservedV4Bytes()
    {
        var a = new SyntheticAbort(Db); var op = new PolicyRequest("runtime-authority", "runtime-agent", "abort-operation", "runtime-task", "runtime-run", "runtime-execution", "msg_runtime",
            new('A', 64), a.Capability.ConnectorId, a.Capability.Capability, PolicyOperation.AbortExistingExecution, a.Inputs.Scope, new(1), new(1), ProviderSessionId: "ses_runtime");
        using var operation = JsonDocument.Parse(SessionReceiptFormat.Write(op, 16384)); using var capability = JsonDocument.Parse(SessionReceiptFormat.Write(a.Capability, 16384));
        Assert.Equal(OperationBinding.Encode(op, a.Capability), DeliveryStorageFormat.Pair(operation.RootElement, capability.RootElement));
    }
    [Fact]
    public async Task TaskCompletesDuringAbortPostAndAckDoesNotRewriteTerminal()
    {
        var a = await Seed(); var f = a.F;
        await using var owner = (await f.Store().TryAcquireDeliveryAsync("cancel-one"))!;
        var ticket = await f.Store().ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        DurableExecution? completed = null;
        using var fake = new RuntimeHttpFixture(f.Runtime.Root);
        fake.OnRequest = async (_, _) =>
        {
            await using var executionOwner = (await f.Executions.TryAcquireAsync("runtime-task"))!;
            var e = (await f.Executions.GetAsync("runtime-task"))!;
            completed = e with { State = DurableExecutionState.Completed, CompletedAt = f.Clock.Now, Version = e.Version + 1 };
            await f.Executions.UpdateAsync(e, completed, executionOwner);
        };
        using var http = ReceiptTransport.ForTests(fake);
        var result = await ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options);
        Assert.Equal("Acknowledged", result.Stage); Assert.Equal("None", result.RemoteOutcome);
        Assert.Equal(completed, await f.Executions.GetAsync("runtime-task"));
    }
    [Theory] [InlineData("quarantined")] [InlineData("legacy")] [InlineData("wrong-session")]
    public async Task UnverifiedOrChangedOriginalSessionCannotAuthorizeAbort(string variant)
    {
        var a = new SyntheticAbort(Db); var f = a.F;
        if (variant == "legacy")
        {
            var old = new SyntheticCancellation(Db); old.Policy = f.Policy;
            await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
            await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, old); await old.Store().ActivatePolicyAsync();
            using var fake = new RuntimeHttpFixture(old.Runtime.Root) { LoseSubmitResponse = true, Running = true }; using var http = new HttpClient(fake);
            await old.Protected(http).RunAsync(old.Runtime.Intent, old.Runtime.Caller);
            await old.Application().RequestAsync(old.Command, old.Canceller);
            await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, f);
        }
        else if (variant == "quarantined")
        {
            await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
            await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
            await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, f); await f.Store().ActivatePolicyAsync();
            using var fake = new RuntimeHttpFixture(f.Runtime.Root);
            fake.OnRequest = async (request, _) => { if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session") await f.Application().RequestAsync(f.Command, f.Canceller); };
            using var change = new QuarantineHandler(fake); using var http = ReceiptTransport.ForTests(change);
            await f.Protected(http).RunAsync(f.Runtime.Intent, f.Runtime.Caller);
            Assert.Equal(SessionReceiptStatus.Quarantined, (await f.Store().GetSessionReceiptAsync("runtime-task"))!.Status);
        }
        else
        {
            await a.Seed();
            await Sql("UPDATE executions SET snapshot=replace(snapshot,'ses_runtime','ses_foreign')");
        }
        await using var owner = (await f.Store().TryAcquireDeliveryAsync("cancel-one"))!;
        using var target = new RuntimeHttpFixture(f.Runtime.Root); using var targetHttp = ReceiptTransport.ForTests(target);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Store().ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner));
        Assert.Empty(target.Requests); Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
    }
    [Fact]
    public async Task OwnerDisposalAfterPreflightMustPreventDispatch()
    {
        var a = await Seed(); var f = a.F;
        IDeliveryOwnership? owner = null;
        var store = f.Store(point =>
        {
            if (point == "DeliveryPreflightReturned") owner!.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
        owner = (await store.TryAcquireDeliveryAsync("cancel-one"))!;
        await using var cleanup = owner;
        var ticket = await store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options));
        output.WriteLine($"Disposed after completed preflight: AbortPOST={fake.Requests.Count(x => x.EndsWith("/abort"))}; ledgerStage={(await store.ReadDeliveryStorageAsync("delivery-one"))!.Stage}");
        Assert.Empty(fake.Requests);
        Assert.Equal(0, (await store.ReadDeliveryStorageAsync("delivery-one"))!.Stage);
    }
    [Fact]
    public async Task DisposalWaitsForInFlightOwnedDispatchWithoutSqliteLease()
    {
        var a = await Seed(); var f = a.F; var store = f.Store();
        var owner = (await store.TryAcquireDeliveryAsync("cancel-one"))!;
        await using var cleanup = owner;
        var ticket = await store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fake = new RuntimeHttpFixture(f.Runtime.Root);
        fake.OnRequest = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); };
        using var http = ReceiptTransport.ForTests(fake);
        var delivery = ProtectedAbortDelivery.DeliverAsync(ticket, http, f.Runtime.Options);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var disposal = owner.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
        f.Policy = f.Policy with { Epoch = new(2), Version = new(2), Rules = [] }; await f.Store().ActivatePolicyAsync();
        release.SetResult(); await Assert.ThrowsAsync<InvalidOperationException>(() => delivery); await disposal;
        Assert.Single(fake.Requests); Assert.Equal(0, (await store.ReadDeliveryStorageAsync("delivery-one"))!.Stage);
    }
    sealed class QuarantineHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
            {
                using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                response.Content = new StringContent(JsonSerializer.Serialize(new { id = "ses_runtime", directory = "UNRELATED", permission = data.RootElement.GetProperty("permission").Clone() }));
            }
            return response;
        }
    }
    sealed class AbortHandler(string variant) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (variant == "lost") throw new HttpRequestException("SyntheticLostResponse");
            if (variant == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(variant == "outage" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(variant == "invalid" ? "false" : "true"), RequestMessage = variant == "foreign" ? new(HttpMethod.Post, "http://127.0.0.1:1234") : request };
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
