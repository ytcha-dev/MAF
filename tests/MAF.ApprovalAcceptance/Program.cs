using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Persistence.Sqlite;

if (args.Length < 2) throw new ArgumentException("Test-only: seed <db> | read <db> | migrate-crash <db> <checkpoint>");
var path = Path.GetFullPath(args[1]);
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
