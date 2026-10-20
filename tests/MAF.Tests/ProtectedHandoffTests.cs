using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class ProtectedHandoffTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-protected-handoff-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    async Task<SyntheticProtectedHandoff> Fixture()
    { var f = new SyntheticProtectedHandoff(Db); await f.Initialize(); return f; }
    async Task<object?> Sql(string sql)
    {
        await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite));
        await c.OpenAsync(); return await ApprovalDatabase.Scalar(c, null, sql, default);
    }
    static void Graph(HandoffRun run)
    {
        Assert.Equal(new[] { "PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate" }, run.Events.OfType<ExecutorInvokedEvent>().Select(e => e.ExecutorId));
        Assert.Equal(new[] { "BriefHandoff", "ExternalHandoff", "ReviewHandoff", "WorkflowOutcome" }, run.Events.OfType<ExecutorCompletedEvent>().Select(e => e.Data?.GetType().Name));
        Assert.IsType<WorkflowOutcome>(Assert.Single(run.Events.OfType<WorkflowOutputEvent>()).Data);
    }
    static int[] Counts(IEnumerable<string> calls) => [calls.Count(x => x == "POST /session"), calls.Count(x => x.EndsWith("/prompt_async")),
        calls.Count(x => x.StartsWith("GET ")), calls.Count(x => x.EndsWith("/abort"))];

    [Fact]
    public async Task FullNestedOfficialGraphUsesRealV4AdmissionReceiptAndEffects()
    {
        using var f = await Fixture(); Assert.Null(await f.F.Executions.GetAsync(f.Task.TaskId));
        var run = await f.Flow().RunAsync(f.Task); Graph(run);
        Assert.Equal(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate);
        Assert.Equal(1, f.PcaCalls); Assert.Equal(1, f.ReviewCalls);
        var result = run.Outcome.ExternalResult!; var row = (await f.F.Executions.GetAsync(f.Task.TaskId))!;
        Assert.Equal(DurableExecutionState.Completed, row.State); Assert.Equal(SubmissionPhase.Acknowledged, row.SubmissionPhase);
        Assert.Equal(row.Handle, result.Handle); Assert.Equal(f.Task.WorkflowRunId, row.WorkflowRunId);
        Assert.Equal("ses_runtime", result.Handle.ExternalSessionId); Assert.Equal("msg_runtime", result.Handle.ExternalRequestId);
        Assert.Equal("runtime-execution", result.Handle.ExecutionId); Assert.NotEqual(row.WorkflowRunId, row.Handle.ExternalSessionId);
        var receipt = (await f.F.Store().GetSessionReceiptAsync(f.Task.TaskId))!;
        Assert.Equal(SessionReceiptStatus.Verified, receipt.Status); Assert.Equal(row.Handle.ExternalSessionId, receipt.Response!.ProviderSessionId);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests"));
        Assert.Equal(1L, await Sql("SELECT session_receipt_required FROM executions")); Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
        Assert.Equal(new[] { 1, 1, 9, 0 }, Counts(f.Fake.Requests));
        Assert.Contains("POST /session/ses_runtime/prompt_async", f.Fake.Requests);
        using var provider = JsonDocument.Parse(File.ReadAllText(Db + ".provider"));
        Assert.Equal(row.Handle.ExternalRequestId, provider.RootElement.GetProperty("message").GetString());
        Assert.Contains("WorkflowOutputEvent", f.Adapter!.InnerRun!.WorkflowEvents);
        Assert.Equal(4, run.Events.OfType<ExecutorInvokedEvent>().Count());
        output.WriteLine($"Task={row.TaskId}, parent/child run={row.WorkflowRunId}, execution={row.Handle.ExecutionId}, session={row.Handle.ExternalSessionId}, message={row.Handle.ExternalRequestId}; Create/Prompt/GET/Abort={string.Join('/', Counts(f.Fake.Requests))}");
        output.WriteLine("Inner=" + string.Join(',', f.Adapter.InnerRun.WorkflowEvents));
    }

    [Theory]
    [InlineData("deny")] [InlineData("approval")] [InlineData("forged")] [InlineData("expired")]
    [InlineData("unavailable")] [InlineData("revoked")] [InlineData("provider")] [InlineData("root")]
    [InlineData("capability")] [InlineData("configuration")] [InlineData("pca")]
    public async Task OuterAllowCannotBypassActualRuntimeDenial(string variant)
    {
        using var f = await Fixture(); var inputs = f.F.Runtime.Inputs; ITrustedCallerContext caller = f.F.Runtime.Caller;
        if (variant is "deny" or "approval")
        {
            f.F.Policy = f.F.Policy with { Rules = [..f.F.Policy.Rules.Select(r => r.Operation == PolicyOperation.SubmitNewReadOnlyTask
                ? r with { Outcome = variant == "deny" ? PolicyOutcome.Deny : PolicyOutcome.RequireApproval, RequiredOperatorScope = variant == "approval" ? "operator:readonly" : null } : r)] };
            // Equal revision/different digest cannot be activated; this fixture was already activated.
            f.F.Policy = f.F.Policy with { Epoch = new(2), Version = new(2) }; await f.F.Store().ActivatePolicyAsync();
            inputs = inputs with { Epoch = new(2), Version = new(2) };
        }
        if (variant == "forged") caller = new SyntheticActor(caller.SubjectId);
        if (variant == "expired") f.F.ExpiredIdentity = true;
        if (variant == "unavailable") f.F.Available = false;
        if (variant == "revoked") { f.F.Policy = f.F.Policy with { Epoch = new(2), Version = new(2), Rules = [] }; await f.F.Store().ActivatePolicyAsync(); }
        if (variant == "provider") inputs = inputs with { ProviderId = "other" };
        if (variant == "root") inputs = inputs with { Scope = inputs.Scope with { ResourceId = inputs.Scope.ResourceId + "-outside" } };
        if (variant == "capability") inputs = inputs with { SubmissionCapability = inputs.SubmissionCapability with { Capability = PolicyCapability.Unknown } };
        if (variant == "configuration") inputs = inputs with { Scope = inputs.Scope with { ConfigurationRevision = 2 } };
        if (variant == "pca") f.BadBrief = true;
        var store = f.F.Store(); var runtime = new ProtectedExternalRuntime(inputs, new ImmutablePolicySource(f.F.Policy), f.F,
            f.F.Executions, store, store, store, f.Http, f.F.Runtime.Options, f.F.Clock);
        var run = await f.Flow(runtime, caller).RunAsync(f.Task); Graph(run);
        Assert.NotEqual(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate);
        if (variant == "approval") Assert.Equal(HumanGateStatus.WaitingForApproval, run.Outcome.HumanGate);
        Assert.Empty(f.Fake.Requests); Assert.Equal(0, f.ReviewCalls);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions")); Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests"));
    }

    [Fact]
    public async Task SubmitAloneCannotObserveAndRecoveryRequiresIndependentInspect()
    {
        using var f = await Fixture();
        f.F.Policy = f.F.Policy with { Epoch = new(2), Version = new(2), Rules = [..f.F.Policy.Rules.Where(r => r.Operation != PolicyOperation.InspectExistingExecution)] };
        await f.F.Store().ActivatePolicyAsync();
        var run = await f.Flow().RunAsync(f.Task); Graph(run);
        Assert.Equal(ExternalExecutionStatus.Unknown, run.Outcome.ExternalResult!.Status); Assert.Equal(0, f.ReviewCalls);
        Assert.Equal(new[] { 1, 1, 4, 0 }, Counts(f.Fake.Requests));
        f.Fake.Requests.Clear(); var recovered = await f.Flow(mode: ExternalAdmissionMode.Inspect).RunAsync(f.Task);
        Assert.NotEqual(HumanGateStatus.ReadyForHumanReview, recovered.Outcome.HumanGate); Assert.Empty(f.Fake.Requests);
    }

    [Theory]
    [InlineData("lost-submit")] [InlineData("lost-create")] [InlineData("running")]
    [InlineData("pending")] [InlineData("outage")] [InlineData("review")]
    public async Task UncertaintyAndReviewFailureNeverRetryOrAbort(string variant)
    {
        using var f = await Fixture();
        f.Fake.LoseSubmitResponse = variant == "lost-submit"; f.Fake.LoseCreateResponse = variant == "lost-create";
        f.Fake.Running = variant == "running"; f.Fake.Pending = variant == "pending"; f.ReviewFails = variant == "review";
        if (variant == "outage") f.Fake.OnRequest = (_, _) => throw new HttpRequestException("SyntheticOutage");
        var run = await f.Flow().RunAsync(f.Task); Graph(run);
        if (variant == "lost-submit") Assert.Equal(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate);
        else Assert.NotEqual(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate);
        var counts = Counts(f.Fake.Requests); Assert.InRange(counts[0], 0, 1); Assert.InRange(counts[1], 0, 1); Assert.Equal(0, counts[3]);
        if (variant == "pending") Assert.Equal(HumanGateStatus.WaitingForApproval, run.Outcome.HumanGate);
        if (variant == "review") Assert.Equal(EvidenceAssessmentStatus.ReviewUnavailable, run.Outcome.Review!.Status);
        if (variant != "outage")
        {
            f.Fake.Requests.Clear(); f.Fake.LoseCreateResponse = f.Fake.LoseSubmitResponse = false; f.Fake.Running = f.Fake.Pending = false;
            var again = await f.Flow(mode: ExternalAdmissionMode.Inspect).RunAsync(f.Task);
            Assert.DoesNotContain(f.Fake.Requests, x => x.StartsWith("POST "));
            if (variant == "lost-create") Assert.Equal(ExternalExecutionStatus.Unknown, again.Outcome.ExternalResult!.Status);
        }
    }

    [Theory]
    [InlineData("cancel")] [InlineData("policy")]
    public async Task PolicyOrControlChangeAfterCreatePreventsPrompt(string variant)
    {
        using var f = await Fixture();
        f.Fake.OnRequest = async (request, _) =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/session") return;
            if (variant == "cancel") await f.F.Application().RequestAsync(f.F.Command, f.F.Canceller);
            else { f.F.Policy = f.F.Policy with { Epoch = new(2), Version = new(2), Rules = [] }; await f.F.Store().ActivatePolicyAsync(); }
        };
        var run = await f.Flow().RunAsync(f.Task); Graph(run);
        Assert.NotEqual(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate); Assert.Equal(0, f.ReviewCalls);
        Assert.Equal(1, Counts(f.Fake.Requests)[0]); Assert.Equal(0, Counts(f.Fake.Requests)[1]); Assert.Equal(0, Counts(f.Fake.Requests)[3]);
        Assert.Equal(SessionReceiptStatus.Verified, (await f.F.Store().GetSessionReceiptAsync(f.Task.TaskId))!.Status);
    }

    [Theory]
    [InlineData("missing")] [InlineData("payload")] [InlineData("fingerprint")]
    public async Task CorruptedRecoveryEvidenceFailsBeforeAnyHttp(string variant)
    {
        using var f = await Fixture(); await f.Flow().RunAsync(f.Task); f.Fake.Requests.Clear();
        if (variant is "missing" or "payload")
        {
            // Existing corruption-fixture technique: restore the exact accepted manifest afterward.
            await Sql("DROP TRIGGER session_receipt_finalize_once; DROP TRIGGER session_receipt_no_delete");
            await Sql(variant == "missing" ? "DELETE FROM execution_session_receipts" : "UPDATE execution_session_receipts SET intent_json='{}'");
            var start = ApprovalSchema.V4Delta.IndexOf("CREATE TRIGGER session_receipt_finalize_once", StringComparison.Ordinal);
            var end = ApprovalSchema.V4Delta.IndexOf("CREATE TABLE cancellation_deliveries", start, StringComparison.Ordinal);
            await Sql(ApprovalSchema.V4Delta[start..end]);
        }
        var task = variant == "fingerprint" ? f.F.Runtime.Request with { TaskText = "changed" } : f.F.Runtime.Request;
        var adapter = new ProtectedHandoffDelegation(f.F.Protected(f.Http), f.F.Runtime.Caller, task, f.Task.WorkflowRunId, f.F.Runtime.Inputs.PreparedHandle);
        await Assert.ThrowsAnyAsync<Exception>(() => adapter.DelegateAsync(task, f.Task.WorkflowRunId, default));
        Assert.Empty(f.Fake.Requests);
    }

    [Theory]
    [InlineData("quarantined")] [InlineData("wrong-message")]
    public async Task ProviderIdentityOrReceiptMismatchCannotReachSuccessfulReview(string variant)
    {
        using var f = await Fixture();
        f.ResponseTransform = (request, response) =>
        {
            if (variant == "quarantined" && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
            {
                using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                response.Content = new StringContent(JsonSerializer.Serialize(new { id = "ses_runtime", directory = Path.GetPathRoot(f.F.Runtime.Root),
                    permission = json.RootElement.GetProperty("permission").Clone() }), System.Text.Encoding.UTF8, "application/json");
            }
            if (variant == "wrong-message" && request.RequestUri!.AbsolutePath.EndsWith("/message"))
                response.Content = new StringContent("[{\"info\":{\"id\":\"msg_wrong\",\"sessionID\":\"ses_other\",\"parentID\":\"msg_other\",\"role\":\"assistant\",\"time\":{\"completed\":2},\"finish\":\"stop\"},\"parts\":[{\"type\":\"text\",\"text\":\"not original evidence\"}]}]", System.Text.Encoding.UTF8, "application/json");
            return response;
        };
        var run = await f.Flow().RunAsync(f.Task); Graph(run);
        Assert.NotEqual(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate); Assert.Equal(0, f.ReviewCalls);
        Assert.Equal(0, Counts(f.Fake.Requests)[3]);
        if (variant == "quarantined")
        {
            Assert.Equal(SessionReceiptStatus.Quarantined, (await f.F.Store().GetSessionReceiptAsync(f.Task.TaskId))!.Status);
            f.Fake.Requests.Clear(); await f.Flow(mode: ExternalAdmissionMode.Inspect).RunAsync(f.Task); Assert.Empty(f.Fake.Requests);
        }
    }

    [Theory]
    [InlineData("execution")] [InlineData("message")] [InlineData("session")]
    public async Task WrongTrustedHandoffMappingCannotRewriteOriginalExecutionOrReachReviewer(string variant)
    {
        using var f = await Fixture(); await f.Flow().RunAsync(f.Task); f.Fake.Requests.Clear();
        var original = (await f.F.Executions.GetAsync(f.Task.TaskId))!;
        var expected = variant switch
        {
            "execution" => f.F.Runtime.Inputs.PreparedHandle with { ExecutionId = "other" },
            "message" => f.F.Runtime.Inputs.PreparedHandle with { ExternalRequestId = "other" },
            _ => f.F.Runtime.Inputs.PreparedHandle with { ExternalSessionId = "ses_guessed" }
        };
        var run = await f.Flow(mode: ExternalAdmissionMode.Inspect, expected: expected).RunAsync(f.Task); Graph(run);
        Assert.NotEqual(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate); Assert.Equal(1, f.ReviewCalls);
        Assert.Null(run.Outcome.ExternalResult); Assert.Equal("ProtectedHandoffIdentityMismatch", run.Outcome.ReasonCode);
        Assert.DoesNotContain(f.Fake.Requests, x => x.StartsWith("POST "));
        Assert.Equal(original, await f.F.Executions.GetAsync(f.Task.TaskId));
        if (variant == "session") Assert.Empty(f.Fake.Requests);
    }

    [Theory]
    [InlineData("cancel-wait")] [InlineData("timeout")] [InlineData("dispose-owner")]
    [InlineData("cancel-control")] [InlineData("policy")]
    public async Task InFlightObservationPreservesChildOwnershipAndNeverAborts(string variant)
    {
        using var timing = new OfflineCheckpointTiming(output.WriteLine);
        using var f = await Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(TimeSpan.FromSeconds(2), f.Task.StageTimeout);
        Assert.Equal(f.Task.StageTimeout.Ticks, f.F.Runtime.Inputs.Scope.BudgetTicks);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource(); var spy = new OwnershipSpy(f.F.Executions);
        f.Fake.OnRequest = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath != "/session/status") return;
            entered.TrySetResult(); await release.Task.WaitAsync(token);
        };
        var store = f.TimingStore(); var runtime = new ProtectedExternalRuntime(f.F.Runtime.Inputs, new ImmutablePolicySource(f.F.Policy), f.F,
            spy, store, store, store, f.Http, f.F.Runtime.Options, f.F.Clock);
        var pending = f.Flow(runtime).RunAsync(f.Task, cancel.Token);
        try
        {
            // Observe early Workflow termination as well as the gate; a missing callback must
            // not hide the completed child's status behind an unrelated gate timeout.
            await Task.WhenAny(entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(5));
            if (!entered.Task.IsCompletedSuccessfully)
            {
                var early = await pending;
                timing.Observe(early.Outcome);
                output.WriteLine($"Observation gate not reached: external={early.Outcome.ExternalResult?.Status}; "
                    + $"code={early.Outcome.ExternalResult?.ErrorCode}; reason={early.Outcome.ReasonCode}; "
                    + $"trace={string.Join(',', early.Outcome.ExternalResult?.Trace.Select(t => t.Kind) ?? [])}; "
                    + $"Create/Prompt/GET/Abort={string.Join('/', Counts(f.Fake.Requests))}");
            }
            Assert.True(entered.Task.IsCompletedSuccessfully, "Original child must reach the observation gate.");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(spy.Owner);
            Assert.Null(await f.F.Executions.TryAcquireAsync(f.Task.TaskId));
            if (variant == "cancel-wait") cancel.Cancel();
            if (variant == "dispose-owner") await spy.Owner!.DisposeAsync();
            if (variant == "cancel-control") await f.F.Application().RequestAsync(f.F.Command, f.F.Canceller);
            if (variant == "policy") { f.F.Policy = f.F.Policy with { Epoch = new(2), Version = new(2), Rules = [] }; await store.ActivatePolicyAsync(); }
            if (variant is not ("timeout" or "cancel-wait")) release.TrySetResult();
            var run = await pending.WaitAsync(TimeSpan.FromSeconds(10)); Graph(run);
            timing.Observe(run.Outcome);
            Assert.NotEqual(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate); Assert.Equal(0, f.ReviewCalls);
            Assert.Equal(1, Counts(f.Fake.Requests)[0]); Assert.Equal(1, Counts(f.Fake.Requests)[1]); Assert.Equal(0, Counts(f.Fake.Requests)[3]);
            await using var releasedOwner = await f.F.Executions.TryAcquireAsync(f.Task.TaskId); Assert.NotNull(releasedOwner);
            output.WriteLine($"{variant}: Create/Prompt/GET/Abort={string.Join('/', Counts(f.Fake.Requests))}; owner released before outer completion; gate={run.Outcome.HumanGate}");
        }
        finally
        {
            // Assertion failure must not dispose HTTP/SQLite while the original child
            // still runs. Cancellation is local observation cancellation, never Abort.
            cancel.Cancel();
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    sealed class OwnershipSpy(IExecutionStore inner) : IExecutionStore
    {
        public IExecutionOwnership? Owner;
        public async ValueTask<IExecutionOwnership?> TryAcquireAsync(string task, CancellationToken token = default) => Owner = await inner.TryAcquireAsync(task, token);
        public Task<DurableExecution?> GetAsync(string task, CancellationToken token = default) => inner.GetAsync(task, token);
        public Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken token = default) => inner.ListRecoverableAsync(token);
        public Task CreateAsync(DurableExecution e, IExecutionOwnership owner, CancellationToken token = default) => inner.CreateAsync(e, owner, token);
        public Task UpdateAsync(DurableExecution before, DurableExecution after, IExecutionOwnership owner, CancellationToken token = default) => inner.UpdateAsync(before, after, owner, token);
    }

    [Theory]
    [InlineData("ExecutionInserted", 0, 0, false)]
    [InlineData("AdmissionCommitted", 0, 0, true)]
    [InlineData("ReceiptIntentCommitted", 0, 0, true)]
    [InlineData("ReceiptResponseCommitted", 1, 0, true)]
    [InlineData("PromptIntentCommitted", 1, 0, true)]
    [InlineData("PromptHttpReturned", 1, 1, true)]
    [InlineData("ChildResultBeforeReview", 1, 1, true)]
    public async Task RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost(string checkpoint, int creates, int prompts, bool admitted)
    {
        using var f = await Fixture(); using var child = CancellationSchemaTests.Child("handoff-run", Db, checkpoint);
        try
        {
            Assert.Equal(checkpoint, await CancellationSchemaTests.Line(child));
            await CancellationSchemaTests.Kill(child);
            var before = File.Exists(Db + ".http") ? File.ReadAllLines(Db + ".http") : [];
            Assert.Equal(creates, Counts(before)[0]); Assert.Equal(prompts, Counts(before)[1]); Assert.Equal(0, Counts(before)[3]);
            var original = await f.F.Executions.GetAsync(f.Task.TaskId);
            Assert.Equal(admitted, original is not null);
            using var restart = CancellationSchemaTests.Child("handoff-inspect", Db);
            var json = await restart.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, restart.ExitCode);
            using var result = JsonDocument.Parse(json); var r = result.RootElement;
            Assert.False(r.GetProperty("ParentResumed").GetBoolean());
            var after = File.Exists(Db + ".http") ? File.ReadAllLines(Db + ".http") : [];
            Assert.Equal(Counts(before)[0], Counts(after)[0]); Assert.Equal(Counts(before)[1], Counts(after)[1]); Assert.Equal(0, Counts(after)[3]);
            if (admitted)
            {
                Assert.Equal(original!.TaskId, r.GetProperty("TaskId").GetString()); Assert.Equal(original.WorkflowRunId, r.GetProperty("WorkflowRunId").GetString());
                var handle = r.GetProperty("Handle"); Assert.Equal(original.Handle.ExecutionId, handle.GetProperty("ExecutionId").GetString());
                Assert.Equal(original.Handle.ExternalRequestId, handle.GetProperty("ExternalRequestId").GetString());
                Assert.Equal(original.Handle.ExternalSessionId, handle.GetProperty("ExternalSessionId").GetString());
                Assert.Equal(prompts == 1 ? "Completed" : checkpoint == "PromptIntentCommitted" ? "Running" : "Unknown", r.GetProperty("Status").GetString());
            }
            else Assert.True(r.TryGetProperty("Error", out _));
            Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
            output.WriteLine($"crashPID={child.Id}, restartPID={restart.Id}, checkpoint={checkpoint}, phase={original?.SubmissionPhase}, Task={original?.TaskId}, run={original?.WorkflowRunId}, execution={original?.Handle.ExecutionId}, session={original?.Handle.ExternalSessionId}, message={original?.Handle.ExternalRequestId}; before={string.Join('/', Counts(before))}, after={string.Join('/', Counts(after))}; parentResumed=false");
        }
        finally { await CancellationSchemaTests.Kill(child); }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive(bool inspect)
    {
        using var f = await Fixture(); using var child = CancellationSchemaTests.Child("handoff-run", Db, "PromptHttpReturned");
        try
        {
            Assert.Equal("PromptHttpReturned", await CancellationSchemaTests.Line(child));
            using var competing = CancellationSchemaTests.Child(inspect ? "handoff-inspect" : "handoff-run", Db);
            var json = await competing.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await competing.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, competing.ExitCode);
            using var parsed = JsonDocument.Parse(json);
            if (inspect) Assert.Equal("TaskAlreadyOwned", parsed.RootElement.GetProperty("Error").GetString());
            else { Assert.NotEqual("ReadyForHumanReview", parsed.RootElement.GetProperty("Gate").GetString()); Assert.Equal(0, parsed.RootElement.GetProperty("ReviewCalls").GetInt32()); }
            Assert.Empty(parsed.RootElement.GetProperty("Calls").EnumerateArray());
            await CancellationSchemaTests.Kill(child);
            using var restart = CancellationSchemaTests.Child("handoff-inspect", Db);
            var recovered = await restart.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, restart.ExitCode);
            using var final = JsonDocument.Parse(recovered); Assert.Equal("Completed", final.RootElement.GetProperty("Status").GetString());
            output.WriteLine($"Owner PID={child.Id}, competing PID={competing.Id}, inspect={inspect}, competing HTTP=0, recoveryPID={restart.Id}");
        }
        finally { await CancellationSchemaTests.Kill(child); }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
