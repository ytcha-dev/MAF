using System.Diagnostics;
using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class ReconciliationTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-reconcile-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    async Task<object?> Sql(string sql)
    { await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite)); await c.OpenAsync(); return await ApprovalDatabase.Scalar(c, null, sql, default); }
    async Task<SyntheticReconciliation> Seed(string stage = "Acknowledged") { var f = new SyntheticReconciliation(Db); await f.Seed(stage); return f; }
    [Theory]
    [InlineData("Completed", 1)] [InlineData("Partial", 2)] [InlineData("Failed", 3)] [InlineData("Cancelled", 4)] [InlineData("Truncated", 2)]
    public async Task SameAdapterProducesAtomicCorrelatedTerminalEvidence(string outcome, int expected)
    {
        var f = await Seed(); using var fake = f.Fixture(outcome); using var http = ReceiptTransport.ForTests(fake);
        var result = await f.Application(http).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller);
        Assert.True(result.Recorded); Assert.Equal(DeliveryStorageFormat.Outcomes[expected], result.Status.ToString());
        var row = (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!;
        Assert.Equal(expected, row.Outcome); Assert.Equal(2, row.Stage); Assert.Equal(2, row.Version);
        Assert.Equal(result.TerminalDigest, DeliveryStorageFormat.TerminalDigest(row.Observation!.TerminalEvidence!));
        Assert.Equal("InspectExistingExecution", row.Observation.ObserverOperation.GetProperty("Operation").GetString());
        Assert.Equal("msg_runtime", row.Observation.TerminalEvidence!.ParentMessageId);
        Assert.Equal("ses_runtime", row.Observation.TerminalEvidence.SessionId);
        Assert.Equal(result.Status.ToString(), (await f.F.Executions.GetAsync("runtime-task"))!.State.ToString());
        Assert.All(fake.Provider.Requests, x => Assert.StartsWith("GET ", x)); Assert.Equal(9, fake.Provider.Requests.Count);
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
        Assert.DoesNotContain("synthetic result", (string)(await Sql("SELECT observation_json FROM cancellation_deliveries"))!);
        output.WriteLine($"Outcome={outcome}; GET/Create/Prompt/Abort={fake.Provider.Requests.Count}/0/0/0; original=runtime-task/runtime-execution/runtime-run/msg_runtime/ses_runtime; execution+delivery atomic; digest={result.TerminalDigest}");
    }
    [Theory] [InlineData("Running")] [InlineData("Pending")] [InlineData("Empty")] [InlineData("WrongParent")]
    [InlineData("WrongSession")] [InlineData("MissingCompleted")] [InlineData("MissingId")] [InlineData("Malformed")] [InlineData("Unknown")]
    [InlineData("MalformedTime")] [InlineData("InvalidCompleted")] [InlineData("InvalidCode")]
    public async Task NonterminalOrInsufficientEvidenceNeverClaimsTerminal(string outcome)
    {
        var f = await Seed(); var before = await f.F.Executions.GetAsync("runtime-task");
        using var fake = f.Fixture(outcome); using var http = ReceiptTransport.ForTests(fake);
        var result = await f.Application(http).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller);
        Assert.False(result.Recorded); Assert.Null(result.TerminalDigest);
        Assert.Equal(0, (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!.Outcome);
        Assert.Equal(before, await f.F.Executions.GetAsync("runtime-task"));
        Assert.All(fake.Provider.Requests, x => Assert.StartsWith("GET ", x));
    }
    [Theory] [InlineData("SentUncertain", "Cancelled")] [InlineData("SentUncertain", "Completed")]
    [InlineData("Intended", "Cancelled")] [InlineData("Intended", "Completed")]
    public async Task OriginalOutcomeDoesNotDependOnAbortAck(string stage, string outcome)
    {
        var f = await Seed(stage); using var fake = f.Fixture(outcome); using var http = ReceiptTransport.ForTests(fake);
        var result = await f.Application(http).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller);
        Assert.True(result.Recorded); Assert.Equal(outcome, result.Status.ToString());
        var row = (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!;
        Assert.Equal(stage == "Intended" ? 0 : 1, row.Stage); Assert.Equal(1 + (stage == "Intended" ? 0 : 1), row.Version);
        Assert.All(fake.Provider.Requests, x => Assert.StartsWith("GET ", x));
        await using var owner = (await f.F.Store().TryAcquireDeliveryAsync("cancel-one"))!;
        await Assert.ThrowsAnyAsync<Exception>(() => f.F.Store().ReserveAbortAsync(f.Abort.Command, f.Abort.Inputs, f.F.Runtime.Caller, owner));
    }
    [Theory] [InlineData("Abort")] [InlineData("Cancel")] [InlineData("Submit")] [InlineData("revoked")]
    [InlineData("expired")] [InlineData("forged")] [InlineData("scope")] [InlineData("unavailable")]
    public async Task InspectIsIndependentAndUnauthorizedCallerGetsNoProviderEffects(string variant)
    {
        var f = await Seed(); var actor = f.F.Runtime.Caller;
        ITrustedCallerContext caller = variant == "forged" ? new SyntheticActor(actor.SubjectId) : actor;
        if (variant is "Abort" or "Cancel" or "Submit")
        {
            var kind = variant == "Abort" ? PolicyOperation.AbortExistingExecution : variant == "Cancel" ? PolicyOperation.RequestCancelExecution : PolicyOperation.SubmitNewReadOnlyTask;
            f.F.Policy = f.F.Policy with { Epoch = new(3), Version = new(3), Rules = [new(actor.SubjectId, "runtime-connector", PolicyValidationCapability(kind), kind, f.F.Runtime.Scope, PolicyOutcome.Allow)] };
            await f.F.Store().ActivatePolicyAsync();
        }
        if (variant == "revoked") f.F.Enabled = false;
        if (variant == "expired") f.F.ExpiredIdentity = true;
        if (variant == "unavailable") f.F.Available = false;
        if (variant == "scope")
        { f.F.Policy = f.F.Policy with { Epoch = new(3), Version = new(3), Rules = [.. f.F.Policy.Rules.Select(r => r with { Scope = r.Scope with { ScopeRevision = 2 } })] }; await f.F.Store().ActivatePolicyAsync(); }
        using var fake = f.Fixture("Completed"); using var http = ReceiptTransport.ForTests(fake);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Application(http).ReconcileAsync("delivery-one", "runtime-task", caller));
        Assert.Empty(fake.Provider.Requests); Assert.Equal(0, (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!.Outcome);
    }
    static PolicyCapability PolicyValidationCapability(PolicyOperation kind) => kind switch
    { PolicyOperation.AbortExistingExecution => PolicyCapability.ExecutionAbort, PolicyOperation.RequestCancelExecution => PolicyCapability.ExecutionCancellation, _ => PolicyCapability.ReadOnlyTask };
    [Theory] [InlineData("Cancelled")] [InlineData("ChangedTerminal")] [InlineData("Running")]
    public async Task ReplayPreservesDigestAndConflictingLaterEvidencePreservesHistory(string changed)
    {
        var f = await Seed();
        using var completed = f.Fixture("Completed"); using var http = ReceiptTransport.ForTests(completed);
        var first = await f.Application(http).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller);
        var execution = await f.F.Executions.GetAsync("runtime-task"); var audit = await Sql("SELECT count(*) FROM control_audit");
        f.F.Clock.Now = f.F.Clock.Now.AddSeconds(1);
        var again = await f.Application(http).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller);
        Assert.True(again.Recorded); Assert.Equal(first.TerminalDigest, again.TerminalDigest); Assert.Equal("AlreadyRecorded", again.Diagnostic);
        using var cancelled = f.Fixture(changed); using var other = ReceiptTransport.ForTests(cancelled);
        var conflict = await f.Application(other).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller);
        Assert.False(conflict.Recorded); Assert.Equal(ExternalExecutionStatus.Unknown, conflict.Status); Assert.Equal("TerminalEvidenceChanged", conflict.Diagnostic);
        Assert.Equal(first.TerminalDigest, conflict.TerminalDigest); Assert.Equal(execution, await f.F.Executions.GetAsync("runtime-task"));
        Assert.Equal(audit, await Sql("SELECT count(*) FROM control_audit"));
    }
    [Theory] [InlineData("ReconciliationExecutionWritten")] [InlineData("ReconciliationEvidenceWritten")] [InlineData("audit")]
    public async Task JointTransactionFaultRollsBackBothAndAudit(string point)
    {
        var f = await Seed(); var before = await f.F.Executions.GetAsync("runtime-task"); var audits = await Sql("SELECT count(*) FROM control_audit");
        using var fake = f.Fixture("Completed"); using var http = ReceiptTransport.ForTests(fake);
        var store = f.F.Store(p => { if (p == point) throw new InvalidOperationException("SyntheticTransactionFault"); },
            point == "audit" ? Convert.ToInt32(audits) : 10000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application(http, store: store).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller));
        Assert.Equal(before, await f.F.Executions.GetAsync("runtime-task")); Assert.Equal(audits, await Sql("SELECT count(*) FROM control_audit"));
        Assert.Equal(0, (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!.Outcome);
    }
    [Fact]
    public async Task RevocationAfterGetPreventsTerminalCapture()
    {
        var f = await Seed(); using var fake = f.Fixture("Completed"); using var http = ReceiptTransport.ForTests(fake);
        var app = f.Application(http, point => { if (point == "ReconciliationAfterGet") f.F.Enabled = false; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller));
        Assert.Equal(0, (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!.Outcome); Assert.False((await f.F.Executions.GetAsync("runtime-task"))!.IsTerminal);
    }
    [Theory]
    [InlineData("ReconciliationBeforeGet", false, 0)] [InlineData("ReconciliationAfterGet", false, 9)]
    [InlineData("ReconciliationExecutionWritten", false, 9)] [InlineData("ReconciliationEvidenceWritten", false, 9)]
    [InlineData("ReconciliationCommitted", true, 9)]
    public async Task RealProcessCrashPreservesJointTerminalAtomicity(string point, bool committed, int beforeCalls)
    {
        _ = await Seed();
        using var child = CancellationSchemaTests.Child("reconcile-run", Db, point); var start = child.StartTime;
        try
        {
            Assert.Equal(point, await CancellationSchemaTests.Line(child)); await CancellationSchemaTests.Kill(child);
            using var reader = CancellationSchemaTests.Child("reconcile-read", Db);
            var text = await reader.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, reader.ExitCode);
            using var state = JsonDocument.Parse(text); var r = state.RootElement;
            Assert.Equal(committed ? "Completed" : "Unknown", r.GetProperty("State").GetString());
            Assert.Equal(committed ? 1 : 0, r.GetProperty("Outcome").GetInt32()); Assert.Equal("ok", r.GetProperty("Integrity").GetString());
            Assert.Equal("ses_runtime", r.GetProperty("ExternalSessionId").GetString()); Assert.Equal("msg_runtime", r.GetProperty("ExternalRequestId").GetString());
            using var restart = CancellationSchemaTests.Child("reconcile-run", Db);
            var result = await restart.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, restart.ExitCode);
            Assert.Contains(committed ? "AlreadyRecorded" : "Recorded", result);
            Assert.Equal(1, (await new SyntheticReconciliation(Db).F.Store().ReadDeliveryStorageAsync("delivery-one"))!.Outcome);
            var calls = File.ReadAllLines(Db + ".inspect-http"); Assert.Equal(beforeCalls + 9, calls.Length); Assert.All(calls, x => Assert.StartsWith("GET ", x));
            output.WriteLine($"checkpoint={point}; killedPID={child.Id}; startUTC={start.ToUniversalTime():O}; readerPID={reader.Id}; restartPID={restart.Id}; original=runtime-task/runtime-execution/runtime-run/msg_runtime/ses_runtime; before={text.Trim()}; GET/Create/Prompt/Abort={calls.Length}/0/0/0; joint=true");
        }
        finally { await CancellationSchemaTests.Kill(child); }
    }
    [Fact]
    public async Task TwoIndependentInspectProcessesHaveOneExecutionOwnerAndNoPost()
    {
        _ = await Seed();
        using var first = CancellationSchemaTests.Child("reconcile-run", Db, "ReconciliationAfterGet"); var start = first.StartTime;
        try
        {
            Assert.Equal("ReconciliationAfterGet", await CancellationSchemaTests.Line(first));
            using var second = CancellationSchemaTests.Child("reconcile-run", Db);
            var text = await second.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, second.ExitCode); Assert.Contains("InspectBusy", text);
            await first.StandardInput.WriteLineAsync("continue"); await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, first.ExitCode);
            Assert.Equal(9, File.ReadAllLines(Db + ".inspect-http").Length);
            output.WriteLine($"Inspect winnerPID={first.Id}; startUTC={start.ToUniversalTime():O}; competitorPID={second.Id}; GET/Create/Prompt/Abort=9/0/0/0; outcome=Completed; executionOwnerExclusive=true");
        }
        finally { await CancellationSchemaTests.Kill(first); }
    }
    [Fact]
    public void CanonicalTerminalContractHasIndependentGoldenDigest()
    {
        var terminal = new DeliveryStorageFormat.Terminal("OpenCode", new('A', 64), "ses_original", "msg_original", "msg_reply",
            "msg_original", "stop", null, "Completed", 638961696000000000);
        Assert.Equal("5FDD7B77DCF8CB87761924355D14579B25E7F62CAC534B3D97281E2CC3A91A80", DeliveryStorageFormat.TerminalDigest(terminal));
        Assert.NotEqual(DeliveryStorageFormat.TerminalDigest(terminal), DeliveryStorageFormat.TerminalDigest(terminal with { ParentMessageId = "msg_foreign" }));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnavailableOrCancelledInspectNeverIssuesAbort(bool cancelled)
    {
        var f = await Seed(); using var fake = f.Fixture("Completed"); using var http = ReceiptTransport.ForTests(fake);
        using var cancel = new CancellationTokenSource();
        fake.Provider.OnRequest = (_, _) =>
        {
            if (cancelled) { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
            throw new HttpRequestException("SyntheticProviderUnavailable");
        };
        var result = await f.Application(http).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller, cancel.Token);
        Assert.Equal(ExternalExecutionStatus.Unknown, result.Status); Assert.False(result.Recorded);
        Assert.All(fake.Provider.Requests, x => Assert.StartsWith("GET ", x));
        Assert.Equal(0, (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!.Outcome);
    }
    [Theory] [InlineData("receipt")] [InlineData("cancellation")] [InlineData("delivery")] [InlineData("session")]
    public async Task TamperedEvidenceDeniesBeforeGet(string target)
    {
        var f = await Seed();
        await Sql(target switch
        {
            "receipt" => "UPDATE control_audit SET actor='forged' WHERE sequence=(SELECT intent_audit_sequence FROM execution_session_receipts)",
            "delivery" => "UPDATE control_audit SET actor='forged' WHERE sequence=(SELECT intent_audit_sequence FROM cancellation_deliveries)",
            "cancellation" => "UPDATE task_controls SET actor='forged'",
            _ => "UPDATE executions SET snapshot=replace(snapshot,'ses_runtime','ses_foreign')"
        });
        using var fake = f.Fixture("Completed"); using var http = ReceiptTransport.ForTests(fake);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Application(http).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller));
        Assert.Empty(fake.Provider.Requests); Assert.Equal(0L, await Sql("SELECT remote_outcome FROM cancellation_deliveries"));
    }
    [Theory] [InlineData("control")] [InlineData("activation")]
    public async Task ChangeAfterGetCannotGrantStaleCapture(string variant)
    {
        var f = await Seed(); using var fake = f.Fixture("Completed"); using var http = ReceiptTransport.ForTests(fake);
        var app = f.Application(http, point =>
        {
            if (point != "ReconciliationAfterGet") return;
            if (variant == "control") Sql("UPDATE task_controls SET version=version+1").GetAwaiter().GetResult();
            else { f.F.Policy = f.F.Policy with { Epoch = new(3), Version = new(3), Rules = [] }; f.F.Store().ActivatePolicyAsync().GetAwaiter().GetResult(); }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller));
        Assert.False((await f.F.Executions.GetAsync("runtime-task"))!.IsTerminal);
        Assert.Equal(0, (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!.Outcome);
    }
    [Theory] [InlineData("Completed", true)] [InlineData("Cancelled", false)]
    public async Task ExistingTerminalExecutionIsImmutable(string terminal, bool matches)
    {
        var f = await Seed(); var store = f.F.Executions;
        await using (var owner = (await store.TryAcquireAsync("runtime-task"))!)
        {
            var e = (await store.GetAsync("runtime-task"))!;
            await store.UpdateAsync(e, e with { State = Enum.Parse<DurableExecutionState>(terminal), Version = e.Version + 1, CompletedAt = e.UpdatedAt }, owner);
        }
        var before = await store.GetAsync("runtime-task");
        using var fake = f.Fixture("Completed"); using var http = ReceiptTransport.ForTests(fake);
        var result = await f.Application(http).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller);
        Assert.Equal(matches, result.Recorded); Assert.Equal(before, await store.GetAsync("runtime-task"));
        Assert.Equal(matches ? 1 : 0, (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!.Outcome);
    }
    [Fact]
    public async Task InFlightAbortAckRacesCaptureAndCasPreventsPartialTerminalWrite()
    {
        var f = new SyntheticReconciliation(Db); await f.Abort.Seed(); var store = f.F.Store();
        await using var owner = (await store.TryAcquireDeliveryAsync("cancel-one"))!;
        var ticket = await store.ReserveAbortAsync(f.Abort.Command, f.Abort.Inputs, f.F.Runtime.Caller, owner);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var abortProvider = new RuntimeHttpFixture(f.F.Runtime.Root);
        abortProvider.OnRequest = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); };
        using var abortHttp = ReceiptTransport.ForTests(abortProvider);
        var pending = MAF.Workflows.External.OpenCode.ProtectedAbortDelivery.DeliverAsync(ticket, abortHttp, f.F.Runtime.Options);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.InspectOnly(); await f.F.Store().ActivatePolicyAsync();
        using var fake = f.Fixture("Completed"); using var http = ReceiptTransport.ForTests(fake);
        var app = f.Application(http, point =>
        {
            if (point == "ReconciliationAfterGet") { release.SetResult(); Assert.Equal("Acknowledged", pending.GetAwaiter().GetResult().Stage); }
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller));
        Assert.Equal("DeliveryVersionConflict", error.Message); Assert.False((await f.F.Executions.GetAsync("runtime-task"))!.IsTerminal);
        var row = (await store.ReadDeliveryStorageAsync("delivery-one"))!; Assert.Equal(2, row.Stage); Assert.Equal(0, row.Outcome);
        Assert.Single(abortProvider.Requests); Assert.All(fake.Provider.Requests, x => Assert.StartsWith("GET ", x));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LegacyOrQuarantinedSessionCannotReconcile(bool legacy)
    {
        var f = new SyntheticReconciliation(Db);
        var source = legacy ? new SyntheticCancellation(Db) : f.F;
        source.Policy = f.F.Policy;
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, source);
        if (!legacy) await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, source);
        await source.Store().ActivatePolicyAsync();
        using var provider = new RuntimeHttpFixture(source.Runtime.Root) { LoseSubmitResponse = true, Running = true, StatePath = Db + ".provider" };
        if (!legacy) provider.OnRequest = async (request, _) =>
        { if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session") await source.Application().RequestAsync(source.Command, source.Canceller); };
        using var change = new InvalidDirectoryResponse(provider);
        using var http = legacy ? new HttpClient(provider) : ReceiptTransport.ForTests(change);
        await source.Protected(http).RunAsync(source.Runtime.Intent, source.Runtime.Caller);
        if (legacy)
        {
            await source.Application().RequestAsync(source.Command, source.Canceller);
            await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, source);
        }
        f.InspectOnly(); await f.F.Store().ActivatePolicyAsync();
        using var target = f.Fixture("Completed"); using var targetHttp = ReceiptTransport.ForTests(target);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Application(targetHttp).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller));
        Assert.Empty(target.Provider.Requests);
    }
    sealed class InvalidDirectoryResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                json["directory"] = "UNRELATED"; response.Content = new StringContent(json.ToJsonString());
            }
            return response;
        }
    }
    [Fact]
    public async Task ExecutionBecomesTerminalDuringGetNeverOverwritesConfirmedHistory()
    {
        var f = await Seed(); using var fake = f.Fixture("Cancelled"); using var http = ReceiptTransport.ForTests(fake);
        DurableExecution? completed = null;
        fake.Provider.OnRequest = async (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/message")) return;
            Assert.Null(await f.F.Executions.TryAcquireAsync("runtime-task")); // Ordinary competing writer is excluded.
            var e = (await f.F.Executions.GetAsync("runtime-task"))!;
            completed = e with { State = DurableExecutionState.Completed, CompletedAt = e.UpdatedAt, Version = e.Version + 1 };
            // Test-owned privileged writer injects a conflicting snapshot; it is not a supported ownership bypass.
            await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite)); await c.OpenAsync();
            await ApprovalDatabase.Execute(c, null, "UPDATE executions SET terminal=1,version=$v,snapshot=$j", default, ("$v", completed.Version), ("$j", JsonSerializer.Serialize(completed)));
        };
        var result = await f.Application(http).ReconcileAsync("delivery-one", "runtime-task", f.F.Runtime.Caller);
        Assert.Equal("TerminalEvidenceChanged", result.Diagnostic); Assert.False(result.Recorded);
        Assert.Equal(completed, await f.F.Executions.GetAsync("runtime-task")); Assert.Equal(0, (await f.F.Store().ReadDeliveryStorageAsync("delivery-one"))!.Outcome);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
