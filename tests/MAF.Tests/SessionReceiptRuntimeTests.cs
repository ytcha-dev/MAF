using System.Net;
using System.Text;
using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class SessionReceiptRuntimeTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-receipt-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    async Task<SyntheticCancellation> Init(bool approved = false)
    {
        var f = new SyntheticCancellation(Db, approved, v4: true);
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, f);
        await f.Store().ActivatePolicyAsync(); return f;
    }
    async Task<object?> Sql(string sql)
    { await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite)); await c.OpenAsync(); return await ApprovalDatabase.Scalar(c, null, sql, default); }
    static async Task<ExternalWorkflowRun> Run(SyntheticCancellation f, HttpClient http, SqliteApprovalStore? store = null, bool approved = false)
    {
        store ??= f.Store();
        if (!approved) return await f.Protected(http, store).RunAsync(f.Runtime.Intent, f.Runtime.Caller);
        var app = new DurableApprovalApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Executions, store, f.Clock);
        var pending = await app.RequestAsync("cancel-approval", f.Runtime.Intent, f.Runtime.Caller);
        var decision = await app.DecideAsync(new(pending.ApprovalId, "decision", 0, pending.BindingDigest, ApprovalChoice.Approve), f.Approval.Operator);
        return await f.Protected(http, store).ResumeAsync(new(decision.ApprovalId, decision.Version, decision.BindingDigest, f.Runtime.Request), f.Runtime.Caller, f.Approval.Operator);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FullOfficialWorkflowUsesOneOriginalReceipt(bool approved)
    {
        var f = await Init(approved); using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        var result = await Run(f, http, approved: approved);
        Assert.Equal(ExternalExecutionStatus.Completed, result.Result.Status);
        var receipt = (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!;
        Assert.Equal(SessionReceiptStatus.Verified, receipt.Status); Assert.Equal(1, receipt.Version);
        Assert.Equal("ses_runtime", receipt.Response!.ProviderSessionId);
        Assert.Equal(approved, receipt.Intent.ApprovalId is not null);
        Assert.Equal(1, fake.Requests.Count(x => x == "POST /session"));
        Assert.Equal(1, fake.Requests.Count(x => x.EndsWith("/prompt_async"))); Assert.DoesNotContain(fake.Requests, x => x.Contains("abort"));
        Assert.Equal(1L, await Sql("SELECT session_receipt_required FROM executions"));
        output.WriteLine($"v4 full Workflow approved={approved}; original session=ses_runtime; receipt Verified/version1; Create/Prompt/Abort=1/1/0.");
    }
    [Fact]
    public async Task OpaqueTransportIsRejectedBeforeAdmissionOrHttp()
    {
        var f = await Init(); using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(fake);
        Assert.Equal("ReceiptTransportRequired", (await Assert.ThrowsAsync<ExternalHarnessException>(() => Run(f, http))).Code);
        Assert.Empty(fake.Requests); Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
    }
    [Theory]
    [InlineData("foreign-response")] [InlineData("origin")] [InlineData("route")] [InlineData("method")]
    [InlineData("invalid-id")] [InlineData("malformed")] [InlineData("oversize")]
    public async Task UnverifiableOriginalResponseCannotEstablishSession(string mutation)
    {
        var f = await Init(); using var fake = new RuntimeHttpFixture(f.Runtime.Root);
        using var transform = new Transform(fake, (request, response) =>
        {
            if (mutation == "foreign-response") response.RequestMessage = new(HttpMethod.Post, request.RequestUri);
            if (mutation == "origin") request.RequestUri = new("http://127.0.0.1:9999/session");
            if (mutation == "route") request.RequestUri = new("http://127.0.0.1:4096/other");
            if (mutation == "method") request.Method = HttpMethod.Get;
            if (mutation == "malformed") response.Content = new StringContent("{");
            if (mutation == "oversize") response.Content = new StringContent(new string('x', f.Runtime.Options.ResponseLimitBytes + 1));
            if (mutation == "invalid-id") response.Content = new StringContent("{\"id\":\"foreign\"}");
            return response;
        });
        using var http = ReceiptTransport.ForTests(transform); await Run(f, http);
        Assert.Equal(SessionReceiptStatus.Pending, (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!.Status);
        Assert.Null((await f.Executions.GetAsync(f.Command.TaskId))!.Handle.ExternalSessionId);
        Assert.Equal(1, fake.Requests.Count(x => x == "POST /session")); Assert.DoesNotContain(fake.Requests, x => x.Contains("prompt_async") || x.Contains("abort"));
    }
    [Fact]
    public async Task GenuinePolicyRejectedResponseIsQuarantinedButCancelMetadataRemainsReadable()
    {
        var f = await Init(); using var fake = new RuntimeHttpFixture(f.Runtime.Root);
        fake.OnRequest = async (request, _) => { if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session") await f.Application().RequestAsync(f.Command, f.Canceller); };
        using var transform = new Transform(fake, (_, response) =>
        { using var data = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
          response.Content = new StringContent(JsonSerializer.Serialize(new { id = "ses_runtime", directory = Path.GetPathRoot(f.Runtime.Root), permission = data.RootElement.GetProperty("permission").Clone() }), Encoding.UTF8, "application/json"); return response; });
        using var http = ReceiptTransport.ForTests(transform); await Run(f, http);
        var receipt = (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!;
        Assert.Equal(SessionReceiptStatus.Quarantined, receipt.Status); Assert.Equal("ses_runtime", receipt.Response!.ProviderSessionId);
        Assert.NotNull(await f.Store().GetCancellationAsync("cancel-one")); Assert.Equal(1, (await f.Store().GetControlAsync(f.Command.TaskId))!.Version);
        var before = fake.Requests.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Protected(http).RunAsync(new(ExternalAdmissionMode.Inspect, f.Command.TaskId, f.Runtime.Request.Timeout), f.Runtime.Caller));
        Assert.Equal(before, fake.Requests.Count); Assert.DoesNotContain(fake.Requests, x => x.Contains("prompt_async") || x.Contains("abort"));
    }
    [Theory]
    [InlineData("ReceiptSnapshotWritten")] [InlineData("ReceiptResponseWritten")] [InlineData("audit")]
    public async Task FailedFinalizationNeverSplitsReceiptAndSnapshot(string point)
    {
        var f = await Init(); using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        var store = f.Store(checkpoint => { if (checkpoint == point) throw new InvalidOperationException("SyntheticReceiptFault"); }, point == "audit" ? 3 : 10000);
        await Run(f, http, store);
        var r = (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!;
        Assert.Equal(SessionReceiptStatus.Pending, r.Status); Assert.Null((await f.Executions.GetAsync(f.Command.TaskId))!.Handle.ExternalSessionId);
        Assert.Equal(1, fake.Requests.Count(x => x == "POST /session")); Assert.DoesNotContain(fake.Requests, x => x.Contains("prompt_async") || x.Contains("abort"));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PolicyAndCancellationAfterSendDoNotEraseOriginalResponse(bool cancel)
    {
        var f = await Init(); using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        fake.OnRequest = async (request, _) =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/session") return;
            if (cancel) await f.Application().RequestAsync(f.Command, f.Canceller);
            f.Policy = f.Policy with { Epoch = new(2), Version = new(2), Rules = [] }; await f.Store().ActivatePolicyAsync();
        };
        await Run(f, http); Assert.Equal(SessionReceiptStatus.Verified, (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!.Status);
        Assert.Equal(1, (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!.Intent.Operation.Epoch.Value);
        Assert.Equal(1, fake.Requests.Count(x => x == "POST /session")); Assert.DoesNotContain(fake.Requests, x => x.Contains("prompt_async") || x.Contains("abort"));
    }
    [Fact]
    public async Task OrdinaryPhaseValidOwnedUpdateCannotInsertSessionEvenAfterGenuineIntent()
    {
        var f = await Init(); using var fake = new RuntimeHttpFixture(f.Runtime.Root) { LoseCreateResponse = true }; using var http = ReceiptTransport.ForTests(fake);
        await Run(f, http); var before = (await f.Executions.GetAsync(f.Command.TaskId))!;
        await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        var injected = before with { Version = before.Version + 1, SubmissionPhase = SubmissionPhase.SessionKnown,
            Handle = before.Handle with { ExternalSessionId = "ses_unrelated" } };
        Assert.Equal("SessionReceiptRequired", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Executions.UpdateAsync(before, injected, owner))).Message);
        Assert.Equal(before, await f.Executions.GetAsync(f.Command.TaskId));
        Assert.Equal(SessionReceiptStatus.Pending, (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!.Status);
    }
    [Theory] [InlineData("audit")] [InlineData("payload")] [InlineData("missing")]
    public async Task CorruptedMandatoryReceiptNeverDowngradesToLegacy(string mutation)
    {
        var f = await Init(); using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake); await Run(f, http);
        if (mutation == "audit") await Sql("UPDATE control_audit SET actor='forged' WHERE sequence=(SELECT response_audit_sequence FROM execution_session_receipts)");
        else
        {
            await Sql("DROP TRIGGER session_receipt_finalize_once; DROP TRIGGER session_receipt_no_delete");
            if (mutation == "payload") await Sql("UPDATE execution_session_receipts SET intent_json='{}'");
            else await Sql("DELETE FROM execution_session_receipts");
            // Restore the exact manifest so this exercises data verification, not only schema rejection.
            var start = ApprovalSchema.V4Delta.IndexOf("CREATE TRIGGER session_receipt_finalize_once", StringComparison.Ordinal);
            var end = ApprovalSchema.V4Delta.IndexOf("CREATE TABLE cancellation_deliveries", start, StringComparison.Ordinal);
            await Sql(ApprovalSchema.V4Delta[start..end]);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Executions.GetAsync(f.Command.TaskId));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Store().GetSessionReceiptAsync(f.Command.TaskId));
        var calls = fake.Requests.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Protected(http).RunAsync(new(ExternalAdmissionMode.Inspect, f.Command.TaskId, f.Runtime.Request.Timeout), f.Runtime.Caller));
        Assert.Equal(calls, fake.Requests.Count);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeIntentOrArmNeverCreatesSession(bool afterIntent)
    {
        var f = await Init(); using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        SqliteApprovalStore store;
        if (afterIntent) store = f.Store(point => { if (point == "ReceiptIntentCommitted") f.Application().RequestAsync(f.Command, f.Canceller).GetAwaiter().GetResult(); });
        else store = new(Db, f.Authority(), f.Clock, 10000, point =>
        { if (point == ApprovalCheckpoint.AdmissionCommitted) f.Application().RequestAsync(f.Command, f.Canceller).GetAwaiter().GetResult(); }, ApprovalStoreProfile.ExplicitV4);
        await Run(f, http, store);
        Assert.DoesNotContain(fake.Requests, x => x.StartsWith("POST "));
        Assert.Equal(afterIntent ? SessionReceiptStatus.Pending : null, (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))?.Status);
        Assert.Equal(1, (await f.Store().GetControlAsync(f.Command.TaskId))!.Version);
    }
    [Theory] [InlineData("no-exchange")] [InlineData("transport")] [InlineData("method")] [InlineData("origin")] [InlineData("route")]
    public async Task ForgedProtectedResponseContextCannotFinalize(string variant)
    {
        var f = new SyntheticCancellation(Db, v4: true); await f.Setup();
        var before = (await f.Executions.GetAsync(f.Command.TaskId))!;
        await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        using var access = new ProtectedExternalRuntime.ProtectedAccess(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Executions,
            f.Store(), owner, f.Runtime.Caller, 0, f.Runtime.Intent, false, f.Clock, f.Runtime.Options);
        var intent = before with { State = DurableExecutionState.SessionCreating, SubmissionPhase = SubmissionPhase.SessionCreationIntended, Version = before.Version + 1 };
        await access.VerifyAsync(ProviderEffect.CreateSession, before, intent, default);
        using var effect = access.Enter(ProviderEffect.CreateSession, f.Runtime.Request, f.Runtime.Inputs.NewWorkflowRunId, before.Handle);
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake); using var opaque = new HttpClient();
        using var request = new HttpRequestMessage(variant == "method" ? HttpMethod.Get : HttpMethod.Post,
            (variant == "origin" ? "http://127.0.0.1:9999" : "http://127.0.0.1:4096") + (variant == "route" ? "/other" : "/session") + "?directory=" + Uri.EscapeDataString(f.Runtime.Root));
        if (variant != "no-exchange") await Assert.ThrowsAsync<ExternalHarnessException>(() => Task.Run(() => access.ArmOriginalCreate(variant == "transport" ? opaque : http, request)));
        else
        {
            access.ArmOriginalCreate(http, request);
            using var forged = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            Assert.Throws<ExternalHarnessException>(() => access.CaptureOriginalResponse(http, request, forged));
            await Assert.ThrowsAsync<ExternalHarnessException>(() => access.FinalizeOriginalCreate(intent.Handle with { ExternalSessionId = "ses_forged" }, true, "None"));
        }
        Assert.Empty(fake.Requests); Assert.Null((await f.Executions.GetAsync(f.Command.TaskId))!.Handle.ExternalSessionId);
        Assert.Equal(SessionReceiptStatus.Pending, (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!.Status);
    }
    [Fact]
    public async Task RegistrationCannotBeSerializedReissuedOrUsedByDisposedOwner()
    {
        var f = new SyntheticCancellation(Db, v4: true); await f.Setup();
        var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        try
        {
            var e = (await f.Executions.GetAsync(f.Command.TaskId))!;
            var assessment = new ExternalExecutionApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Clock).Assess(f.Runtime.Intent, f.Runtime.Caller);
            var next = e with { State = DurableExecutionState.SessionCreating, SubmissionPhase = SubmissionPhase.SessionCreationIntended, Version = e.Version + 1 };
            var cap = await f.Store().CommitCreateIntentAsync(assessment.Operation!, assessment.Decision, f.Runtime.Submit, e, next, 0, f.Runtime.Request.Timeout.Ticks,
                f.Runtime.Options.ResponseLimitBytes, f.Runtime.Caller, owner, null);
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(cap));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<SessionReceiptRegistration>("{}"));
            await Assert.ThrowsAnyAsync<Exception>(() => f.Store().CommitCreateIntentAsync(assessment.Operation!, assessment.Decision, f.Runtime.Submit, e, next, 0,
                f.Runtime.Request.Timeout.Ticks, f.Runtime.Options.ResponseLimitBytes, f.Runtime.Caller, owner, null));
            // Controlled reflection tests the lifecycle, NOT malicious same-principal isolation.
            var arm = typeof(SessionReceiptRegistration).GetMethod("Arm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            arm.Invoke(cap, null);
            Assert.IsType<InvalidOperationException>(Assert.Throws<System.Reflection.TargetInvocationException>(() => arm.Invoke(cap, null)).InnerException);
            await owner.DisposeAsync();
            var complete = typeof(SessionReceiptRegistration).GetMethod("Complete", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var task = (Task<DurableExecution>)complete.Invoke(cap, ["ses_forged", true, "None", f.Clock.Now.UtcTicks, CancellationToken.None])!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => task);
            Assert.Equal(SessionReceiptStatus.Pending, (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!.Status);
        }
        finally { await owner.DisposeAsync(); }
    }
    sealed class Transform(HttpMessageHandler inner, Func<HttpRequestMessage, HttpResponseMessage, HttpResponseMessage> change) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var response = await base.SendAsync(request, token); return request.RequestUri!.AbsolutePath == "/session" && request.Method == HttpMethod.Post ? change(request, response) : response; }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
