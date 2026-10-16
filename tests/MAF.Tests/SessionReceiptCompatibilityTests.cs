using System.Text.Json;
using System.Text.Json.Nodes;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Data.Sqlite;

namespace MAF.Tests;

public sealed class SessionReceiptCompatibilityTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-v4-compat-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    public static IEnumerable<object[]> ExecutionProfiles()
    { foreach (var version in new[] { 1, 2, 3, 4 }) foreach (var profile in Enum.GetValues<ExecutionStoreProfile>()) yield return [version, profile]; }
    public static IEnumerable<object[]> ApprovalProfiles()
    { foreach (var version in new[] { 1, 2, 3, 4 }) foreach (var profile in Enum.GetValues<ApprovalStoreProfile>()) yield return [version, profile]; }
    async Task<SyntheticCancellation> Schema(int version)
    {
        var f = new SyntheticCancellation(Db);
        if (version == 1) _ = await new SqliteExecutionStore(Db).GetAsync("absent");
        else
        {
            await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
            if (version >= 3) await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
            if (version == 4) await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, f);
        }
        return f;
    }
    [Theory] [MemberData(nameof(ExecutionProfiles))]
    public async Task EveryExecutionProfileRemainsExact(int version, ExecutionStoreProfile profile)
    {
        await Schema(version); var store = new SqliteExecutionStore(Db, profile);
        if (version == (int)profile + 1) Assert.Null(await store.GetAsync("absent"));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("absent"));
    }
    [Theory] [MemberData(nameof(ApprovalProfiles))]
    public async Task EveryApprovalProfileRemainsExact(int version, ApprovalStoreProfile profile)
    {
        var f = await Schema(version); var store = new SqliteApprovalStore(Db, f.Authority(), f.Clock, profile);
        if (version == (int)profile + 2) Assert.Null(await store.GetControlAsync("absent"));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetControlAsync("absent"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MigratedKnownSessionMayOnlyInspectWhereLegacyCorrelationIsExact(bool mismatch)
    {
        var old = await Schema(3); await old.Store().ActivatePolicyAsync();
        using var fake = new RuntimeHttpFixture(old.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        if (mismatch) fake.OnRequest = async (request, _) => { if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session") await old.Application().RequestAsync(old.Command, old.Canceller); };
        var original = await old.Protected(http).RunAsync(old.Runtime.Intent, old.Runtime.Caller);
        await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, old);
        var f = new SyntheticCancellation(Db, v4: true); var calls = fake.Requests.Count;
        if (mismatch)
        {
            Assert.Equal("LegacyCorrelationUnverified", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetCancellationAsync("cancel-one"))).Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Protected(http).RunAsync(new(ExternalAdmissionMode.Inspect, f.Command.TaskId, f.Runtime.Request.Timeout), f.Runtime.Caller));
            Assert.Equal(calls, fake.Requests.Count);
        }
        else
        {
            var recovery = await f.Protected(http).RunAsync(new(ExternalAdmissionMode.Inspect, f.Command.TaskId, f.Runtime.Request.Timeout), f.Runtime.Caller, true);
            Assert.Equal(original.Result.Handle, recovery.Result.Handle);
            Assert.All(fake.Requests.Skip(calls), x => Assert.StartsWith("GET ", x));
        }
        Assert.Null(await f.Store().GetSessionReceiptAsync(f.Command.TaskId));
        await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite)); await c.OpenAsync();
        Assert.Equal(0L, await ApprovalDatabase.Scalar(c, null, "SELECT session_receipt_required FROM executions", default));
        Assert.Equal(0L, await ApprovalDatabase.Scalar(c, null, "SELECT count(*) FROM cancellation_deliveries", default));
    }
    [Fact]
    public async Task ClosedReceiptCanonicalFormatIsOrderIndependentAndBindsAllOriginalIdentities()
    {
        var f = new SyntheticCancellation(Db, v4: true); await f.Setup();
        await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        var before = (await f.Executions.GetAsync(f.Command.TaskId))!;
        var assessment = new ExternalExecutionApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Clock).Assess(f.Runtime.Intent, f.Runtime.Caller);
        await f.Store().CommitCreateIntentAsync(assessment.Operation!, assessment.Decision, f.Runtime.Submit, before,
            before with { State = DurableExecutionState.SessionCreating, SubmissionPhase = SubmissionPhase.SessionCreationIntended, Version = 1 }, 0,
            f.Runtime.Request.Timeout.Ticks, f.Runtime.Options.ResponseLimitBytes, f.Runtime.Caller, owner, null);
        var receipt = (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!;
        var json = SessionReceiptFormat.Write(receipt.Intent, 16384); var obj = JsonNode.Parse(json)!.AsObject();
        var reverse = new JsonObject(obj.Reverse().Select(x => new KeyValuePair<string, JsonNode?>(x.Key, x.Value?.DeepClone())));
        Assert.Equal(receipt.IntentDigest, SessionReceiptFormat.Digest(SessionReceiptFormat.Read<SessionReceiptIntent>(reverse.ToJsonString(), 16384)));
        foreach (var op in new[] { receipt.Intent.Operation with { CallerSubject = "other" }, receipt.Intent.Operation with { TaskId = "other" },
            receipt.Intent.Operation with { OperationId = "other" }, receipt.Intent.Operation with { Scope = receipt.Intent.Operation.Scope with { ScopeRevision = 2 } } })
            Assert.NotEqual(receipt.IntentDigest, SessionReceiptFormat.Digest(receipt.Intent with { Operation = op }));
        var duplicate = json.Replace("\"ReceiptId\":", "\"ReceiptId\":\"other\",\"ReceiptId\":", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => SessionReceiptFormat.Read<SessionReceiptIntent>(duplicate, 16384));
        obj["Unexpected"] = 1; Assert.Throws<JsonException>(() => SessionReceiptFormat.Read<SessionReceiptIntent>(obj.ToJsonString(), 16384));
        obj.Remove("Unexpected"); obj.Remove("IntentControlVersion"); Assert.Throws<JsonException>(() => SessionReceiptFormat.Read<SessionReceiptIntent>(obj.ToJsonString(), 16384));
        Assert.Throws<InvalidOperationException>(() => SessionReceiptFormat.Read<SessionReceiptIntent>(new string('x', 16385), 16384));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
