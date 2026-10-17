using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Persistence.Sqlite;

if (args.Length < 2) throw new ArgumentException("Test-only: seed <db> | read <db> | migrate-crash <db> <checkpoint>");
var path = Path.GetFullPath(args[1]);
if (args[0].StartsWith("reconcile-", StringComparison.Ordinal))
{
    var f = new SyntheticReconciliation(path); f.InspectOnly();
    void Gate(string point)
    {
        if (args.Length < 3 || args[2] != point) return;
        Console.WriteLine(point); Console.Out.Flush();
        if (!Console.In.ReadLineAsync().Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("ReconciliationTestGate");
    }
    if (args[0] == "reconcile-read")
    {
        var execution = (await f.F.Executions.GetAsync("runtime-task"))!;
        var row = (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!;
        await using var c = new Microsoft.Data.Sqlite.SqliteConnection(ApprovalDatabase.ConnectionString(path, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite)); await c.OpenAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { execution.TaskId, execution.WorkflowRunId, execution.Handle.ExecutionId,
            execution.Handle.ExternalSessionId, execution.Handle.ExternalRequestId, State = execution.State.ToString(), row.Version, row.Stage, row.Outcome,
            Digest = row.Observation?.TerminalEvidenceDigest, Integrity = await ApprovalDatabase.Scalar(c, null, "PRAGMA integrity_check", default) })); return;
    }
    using var fake = f.Fixture(args.Length > 3 ? args[3] : "Completed"); fake.Provider.LedgerPath = path + ".inspect-http";
    using var http = MAF.Workflows.External.ReceiptTransport.ForTests(fake);
    try { var result = await f.Application(http, Gate).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller); Console.WriteLine(JsonSerializer.Serialize(result)); }
    catch (MAF.Core.ExternalHarnessException e) when (e.Code == "ExecutionOwned") { Console.WriteLine("InspectBusy"); }
    return;
}
if (args[0].StartsWith("abort-", StringComparison.Ordinal))
{
    var a = new SyntheticAbort(path); var f = a.F;
    void Gate(string point)
    {
        if (args.Length < 3 || args[2] != point) return;
        Console.WriteLine(point); Console.Out.Flush();
        if (!Console.In.ReadLineAsync().Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("AbortTestGate");
    }
    var store = f.Store(Gate);
    if (args[0] == "abort-read")
    {
        var row = await store.ReadDeliveryStorageAsync("delivery-one");
        Console.WriteLine(JsonSerializer.Serialize(new { row?.Version, row?.Stage, row?.Outcome })); return;
    }
    if (args[0] == "abort-owner")
    {
        await using var executionOwner = await f.Executions.TryAcquireAsync("runtime-task") ?? throw new InvalidOperationException("ExecutionOwnerRequired");
        Console.WriteLine("ExecutionOwned"); Console.Out.Flush(); await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)); return;
    }
    await using var owner = await store.TryAcquireDeliveryAsync("cancel-one");
    if (owner is null) { Console.WriteLine("DeliveryBusy"); return; }
    try
    {
        var continuation = await store.ReserveAbortAsync(a.Command, a.Inputs, f.Runtime.Caller, owner);
        using var fake = new RuntimeHttpFixture(f.Runtime.Root) { LedgerPath = path + ".abort-http" };
        fake.OnRequest = (_, _) => { Gate("AbortWireEntered"); return Task.CompletedTask; };
        using var http = MAF.Workflows.External.ReceiptTransport.ForTests(fake);
        var result = await MAF.Workflows.External.OpenCode.ProtectedAbortDelivery.DeliverAsync(continuation, http, f.Runtime.Options);
        Gate("AbortEvidenceCommitted"); Console.WriteLine(JsonSerializer.Serialize(result));
    }
    catch (InvalidOperationException e) when (e.Message == "DeliveryAlreadyReserved") { Console.WriteLine("DeliveryAlreadyReserved"); }
    return;
}
if (args[0].StartsWith("receipt-", StringComparison.Ordinal))
{
    var approved = args.Length > 3 && args[3] == "approved";
    var f = new SyntheticCancellation(path, approved, v4: true);
    void Gate(string point)
    {
        if (args.Length < 3 || args[2] != point) return;
        Console.WriteLine(point); Console.Out.Flush();
        if (!Console.In.ReadLineAsync().Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("ReceiptTestGate");
    }
    if (args[0] == "receipt-migrate")
    { await ApprovalSchema.MigrateV4Async(path, SyntheticApproval.Maintenance, f, Gate); return; }
    if (args[0] == "receipt-verify")
    {
        await using var c = new Microsoft.Data.Sqlite.SqliteConnection(ApprovalDatabase.ConnectionString(path, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite));
        await c.OpenAsync();
        var version = Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "PRAGMA user_version", default));
        if (version == 4) await ApprovalSchema.ValidateV4Async(c, null!, default); else await ApprovalSchema.ValidateV3Async(c, null!, default);
        var bytes = await ApprovalSchema.V3Bytes(c, null, default);
        Console.WriteLine(JsonSerializer.Serialize(new { Version = version, Integrity = await ApprovalDatabase.Scalar(c, null, "PRAGMA integrity_check", default),
            OriginalDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bytes))) })); return;
    }
    using var transport = new RuntimeHttpFixture(f.Runtime.Root) { LedgerPath = path + ".http", StatePath = path + ".provider" };
    using var http = MAF.Workflows.External.ReceiptTransport.ForTests(transport);
    if (args[0] == "receipt-recover")
    {
        f.Policy = f.Policy with { Epoch = new(2), Version = new(2), Rules = [..f.Policy.Rules.Where(r => r.Operation == MAF.Core.Policy.PolicyOperation.InspectExistingExecution)] };
        var recovered = await f.Protected(http).RunAsync(new(MAF.Workflows.External.ExternalAdmissionMode.Inspect, f.Command.TaskId, f.Runtime.Request.Timeout), f.Runtime.Caller, true);
        var e = (await f.Executions.GetAsync(f.Command.TaskId))!;
        var receipt = await f.Store().GetSessionReceiptAsync(f.Command.TaskId);
        Console.WriteLine(JsonSerializer.Serialize(new { e.TaskId, e.Handle.ExecutionId, e.WorkflowRunId, e.Handle.ExternalRequestId, e.Handle.ExternalSessionId,
            State = recovered.Result.Status.ToString(), Phase = e.SubmissionPhase.ToString(), Receipt = receipt?.Status.ToString(), Calls = transport.Requests.ToArray() }));
        return;
    }
    var store = f.Store(Gate);
    transport.OnRequest = async (request, _) =>
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
            await f.Application(store).RequestAsync(f.Command, f.Canceller);
    };
    if (approved)
    {
        var app = new MAF.Workflows.External.DurableApprovalApplication(f.Runtime.Inputs, new MAF.Core.Policy.ImmutablePolicySource(f.Policy), f, f.Executions, store, f.Clock);
        var pending = await app.RequestAsync("cancel-approval", f.Runtime.Intent, f.Runtime.Caller);
        var decision = await app.DecideAsync(new(pending.ApprovalId, "decision", 0, pending.BindingDigest, MAF.Core.Policy.ApprovalChoice.Approve), f.Approval.Operator);
        await f.Protected(http, store).ResumeAsync(new(decision.ApprovalId, decision.Version, decision.BindingDigest, f.Runtime.Request), f.Runtime.Caller, f.Approval.Operator);
    }
    else await f.Protected(http, store).RunAsync(f.Runtime.Intent, f.Runtime.Caller);
    Console.WriteLine("finished"); return;
}
if (args[0].StartsWith("m4-", StringComparison.Ordinal))
{
    var f = new SyntheticCancellation(path);
    void Gate(string point)
    {
        if (args.Length < 3 || args[2] != point) return;
        Console.WriteLine(point); Console.Out.Flush();
        if (!Console.In.ReadLineAsync().Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("M4Gate");
    }
    if (args[0] == "m4-migrate")
    { await ApprovalSchema.MigrateV3Async(path, SyntheticApproval.Maintenance, f, Gate); return; }
    if (args[0] == "m4-owner")
    {
        await using var owner = await f.Executions.TryAcquireAsync(f.Command.TaskId) ?? throw new InvalidOperationException("OwnerRequired");
        Console.WriteLine("owned"); Console.Out.Flush(); await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)); return;
    }
    if (args[0] == "m4-read")
    {
        var evidence = await f.Store().GetCancellationAsync(f.Command.CancelRequestId);
        using var transport = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(transport);
        var recovered = await f.Protected(http).RunAsync(new(MAF.Workflows.External.ExternalAdmissionMode.Inspect,
            f.Command.TaskId, f.Runtime.Request.Timeout), f.Runtime.Caller, true);
        Console.WriteLine(JsonSerializer.Serialize(new { evidence!.BindingDigest, evidence.Operation.TaskId, evidence.Operation.ExecutionId,
            evidence.CommittedControlVersion, recovered.Result.WorkflowRunId, recovered.Result.Handle.ExternalRequestId,
            Status = recovered.Result.Status.ToString(), ProviderCalls = transport.Requests.Count })); return;
    }
    if (args[0] == "m4-verify")
    {
        await using var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, ForeignKeys = true }.ToString());
        await c.OpenAsync();
        var version = Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "PRAGMA user_version", default));
        var integrity = (string)(await ApprovalDatabase.Scalar(c, null, "PRAGMA integrity_check", default))!;
        var bytes = await ApprovalSchema.OldBytes(c, null, default);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bytes)));
        Console.WriteLine(JsonSerializer.Serialize(new { Version = version, Integrity = integrity, OriginalDigest = digest })); return;
    }
    if (args[0] == "m4-race")
    { Console.WriteLine("ready"); Console.Out.Flush(); await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
    try
    {
        var command = f.Command;
        if (args[0] == "m4-race") command = command with { CancelRequestId = args[2] };
        var result = await f.Application(f.Store(Gate)).RequestAsync(command, f.Canceller);
        Console.WriteLine(result.Status);
    }
    catch (InvalidOperationException e) when (e.Message == "CancellationAlreadyRequested") { Console.WriteLine("conflict"); }
    return;
}
if (args[0].StartsWith("resume-", StringComparison.Ordinal))
{
    var fixture = new SyntheticApprovalApplication(path);
    using var transport = new RuntimeHttpFixture(fixture.Runtime.Root) { LedgerPath = path + ".http", StatePath = path + ".provider" };
    using var http = new HttpClient(transport);
    var sql = new SqliteApprovalStore(path, fixture.Authority(), fixture.Clock, 10000, point =>
    {
        if (args[0] != "resume-crash" || point.ToString() != args[2]) return;
        Console.WriteLine("resume-checkpoint"); Console.Out.Flush();
        if (!Console.In.ReadLineAsync().Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("ResumeTestGate");
    });
    if (args[0] == "resume-race")
    {
        Console.WriteLine("ready"); Console.Out.Flush(); await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        transport.OnRequest = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath != "/global/health") return;
            Console.WriteLine("owned"); Console.Out.Flush(); await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        };
    }
    try
    {
        var approval = (await sql.GetAsync("resume-approval"))!;
        var runtime = fixture.ProtectedRuntime(http, sql);
        var result = args[0] == "resume-recover"
            ? await runtime.RunAsync(new(MAF.Workflows.External.ExternalAdmissionMode.Inspect, fixture.Runtime.Request.TaskId, fixture.Runtime.Request.Timeout), fixture.Caller, observeOnce: true)
            : await runtime.ResumeAsync(new(approval.ApprovalId, approval.Version, approval.BindingDigest, fixture.Runtime.Request), fixture.Caller, fixture.Operator);
        Console.WriteLine(JsonSerializer.Serialize(result.Result));
    }
    catch (MAF.Core.ExternalHarnessException e) when (e.Code == "TaskAlreadyOwned") { Console.WriteLine("busy"); }
    return;
}
if (args[0] is "approval-app-seed" or "approval-app-read")
{
    var fixture = new SyntheticApprovalApplication(path);
    var pending = await fixture.Application().RequestAsync("app-approval", fixture.Runtime.Intent, fixture.Caller);
    if (args[0] == "approval-app-seed")
    {
        Console.WriteLine("application-pending-committed"); Console.Out.Flush();
        await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }
    else Console.WriteLine(JsonSerializer.Serialize(pending)); // Idempotent request via a fresh application/authority.
    return;
}
if (args[0] is "runtime-crash" or "runtime-recover")
{
    var fixture = new SyntheticRuntime(path);
    using var transport = new RuntimeHttpFixture(fixture.Root) { LedgerPath = path + ".http", StatePath = path + ".provider" };
    using var http = new HttpClient(transport);
    var occurrence = 0;
    var coordinator = new SqliteApprovalStore(path, fixture.Authority(), fixture.Clock, 10000, point =>
    {
        if (args[0] != "runtime-crash" || point.ToString() != args[2] || ++occurrence != int.Parse(args[3])) return;
        Console.WriteLine("runtime-checkpoint"); Console.Out.Flush();
        if (!Console.In.ReadLineAsync().Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("UnreleasedRuntimeGate");
    });
    var runtime = fixture.Runtime(http, coordinator);
    var intent = args[0] == "runtime-recover"
        ? new MAF.Workflows.External.ExternalAdmissionRequest(MAF.Workflows.External.ExternalAdmissionMode.Inspect, fixture.Request.TaskId, fixture.Request.Timeout)
        : fixture.Intent;
    var result = await runtime.RunAsync(intent, fixture.Caller, observeOnce: true);
    Console.WriteLine(JsonSerializer.Serialize(new { result.Result.TaskId, result.Result.WorkflowRunId, result.Result.Handle, result.Result.Status }));
    return;
}
if (args[0] is "admit-child" or "admit-crash")
{
    var fixture = new SyntheticAdmission();
    var executions = new SqliteExecutionStore(path, ExecutionStoreProfile.ExplicitV2);
    await using var owner = await executions.TryAcquireAsync(fixture.Request.TaskId);
    if (owner is null) { Console.WriteLine("owner-busy"); return; }
    Console.WriteLine("owner-acquired"); Console.Out.Flush();
    await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
    var store = new SqliteApprovalStore(path, new SyntheticAuthority { Snapshot = fixture.Policy }, fixture.Clock, 10000, point =>
    {
        if (args[0] != "admit-crash" || point.ToString() != args[2]) return;
        Console.WriteLine("admission-checkpoint"); Console.Out.Flush();
        if (!Console.In.ReadLineAsync().Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("UnreleasedTestGate");
    });
    try
    {
        _ = await fixture.Application(store).AdmitAsync(fixture.Intent, fixture.Caller, owner);
        Console.WriteLine("admitted-committed"); Console.Out.Flush();
        await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }
    catch (InvalidOperationException e) when (e.Message == "ExistingExecutionRecoveryOnly") { Console.WriteLine("recovery-only"); }
    return;
}
if (args[0] == "migrate-crash")
{
    var checkpoint = Enum.Parse<ApprovalCheckpoint>(args[2]);
    await ApprovalSchema.MigrateAsync(path, SyntheticApproval.Maintenance, point =>
    {
        if (point != checkpoint) return;
        Console.WriteLine("migration-checkpoint"); Console.Out.Flush();
        // Driver observes this protocol signal, then kills this specific captured helper process.
        if (!Console.In.ReadLineAsync().Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("UnreleasedTestGate");
    });
    return;
}
if (args[0] == "seed")
{
    await ApprovalSchema.InitializeOrMigrateAsync(path, SyntheticApproval.Maintenance);
    var store = new SqliteApprovalStore(path, new SyntheticAuthority(), new SyntheticClock());
    await store.ActivatePolicyAsync(); // Explicit synthetic setup; seed never activates through a business write.
    await store.RequestAsync("approval-one", SyntheticApproval.Request(), SyntheticApproval.Caller);
    Console.WriteLine("pending-committed"); Console.Out.Flush();
    await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)); return;
}
if (args[0] != "read") throw new ArgumentException("UnknownTestCommand");
var reader = new SqliteApprovalStore(path, new SyntheticAuthority(), new SyntheticClock());
Console.WriteLine(JsonSerializer.Serialize(await reader.GetAsync("approval-one")));
