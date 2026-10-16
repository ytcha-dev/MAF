using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Persistence.Sqlite;

if (args.Length < 2) throw new ArgumentException("Test-only: seed <db> | read <db> | migrate-crash <db> <checkpoint>");
var path = Path.GetFullPath(args[1]);
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
