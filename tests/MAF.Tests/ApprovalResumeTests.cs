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

public sealed class ApprovalResumeTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("maf-resume-").FullName;
    string Db => Path.Combine(root, "synthetic.db");
    SyntheticApprovalApplication F => new(Db);
    async Task<ApprovalRecord> Setup(SyntheticApprovalApplication f)
    {
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance); await f.Store().ActivatePolicyAsync();
        var p = await f.Application().RequestAsync("resume-approval", f.Runtime.Intent, f.Caller);
        return await f.Application().DecideAsync(new(p.ApprovalId, "resume-decision", p.Version, p.BindingDigest, ApprovalChoice.Approve), f.Operator);
    }
    static ApprovalResumeRequest Request(SyntheticApprovalApplication f, ApprovalRecord approval) => new(approval.ApprovalId, approval.Version, approval.BindingDigest, f.Runtime.Request);
    async Task<object?> Sql(string sql, string? snapshot = null)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString()); await c.OpenAsync();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; if(snapshot is not null) cmd.Parameters.AddWithValue("$j", snapshot); return await cmd.ExecuteScalarAsync();
    }
    [Fact]
    public async Task ExplicitResumeKeepsRequireApprovalAndLaunchesOriginalWorkflowExactlyOnce()
    {
        var f = F; var approved = await Setup(f); using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(fake);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions")); Assert.Empty(fake.Requests);
        var runtime = f.ProtectedRuntime(http); var result = await runtime.ResumeAsync(Request(f, approved), f.Caller, f.Operator);
        Assert.Equal(ExternalExecutionStatus.Completed, result.Result.Status); Assert.NotEmpty(result.WorkflowEvents);
        var consumed = (await f.Store().GetAsync(approved.ApprovalId))!;
        Assert.Equal(ApprovalStatus.Consumed, consumed.Status); Assert.Equal(2, consumed.Version);
        Assert.Equal(approved.Operation, consumed.Operation); Assert.Equal(approved.BindingDigest, consumed.BindingDigest); Assert.Equal(approved.DecisionId, consumed.DecisionId);
        var dispatchAudit=(await f.Store().ReadAuditAsync()).Where(a=>a.Code==ControlAuditCode.Requested && a.Version>0).ToArray();
        Assert.Equal(2,dispatchAudit.Length);Assert.All(dispatchAudit,a=>Assert.Equal(approved.ApprovalId,a.ApprovalId));
        Assert.Equal(PolicyOutcome.RequireApproval, f.Policy.Rules[0].Outcome);
        Assert.Equal("runtime-run", result.Result.WorkflowRunId); Assert.Equal("runtime-execution", result.Result.Handle.ExecutionId); Assert.Equal("msg_runtime", result.Result.Handle.ExternalRequestId);
        Assert.Equal("ExistingExecutionRecoveryOnly",(await Assert.ThrowsAsync<ExternalHarnessException>(()=>runtime.ResumeAsync(Request(f,approved),f.Caller,f.Operator))).Code);
        var again = await runtime.RunAsync(new(ExternalAdmissionMode.Inspect, f.Runtime.Request.TaskId,f.Runtime.Request.Timeout), f.Caller, observeOnce:true);
        Assert.Equal(result.Result.Handle, again.Result.Handle); Assert.Equal(2, fake.Requests.Count(x => x.StartsWith("POST ")));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM executions")); Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
        output.WriteLine($"approval=resume-approval, decision=resume-decision, consumedVersion=2, binding={approved.BindingDigest}, originalBindingPreserved=true, policy=RequireApproval, CreatePOST=1, PromptPOST=1, AbortPOST=0, duplicatePOSTdelta=0");
    }
    [Fact]
    public async Task FreshCompositionRestoresApprovedIdentityMetadataWithoutMintingFromIt()
    {
        var f=F;var approved=await Setup(f);using var fake=new RuntimeHttpFixture(f.Runtime.Root);using var http=new HttpClient(fake);var sql=f.Store();
        var fresh=f.Runtime.Inputs with { OperationId="fresh-operation",NewWorkflowRunId="fresh-run",PreparedHandle=new("fresh-execution","OpenCode",ExternalRequestId:"msg_fresh") };
        var runtime=new ProtectedExternalRuntime(fresh,new ImmutablePolicySource(f.Policy),f,new SqliteExecutionStore(Db,ExecutionStoreProfile.ExplicitV2),sql,sql,sql,http,f.Runtime.Options,f.Clock);
        var result=await runtime.ResumeAsync(Request(f,approved),f.Caller,f.Operator);
        Assert.Equal(ExternalExecutionStatus.Completed,result.Result.Status);Assert.Equal(approved.Operation.WorkflowRunId,result.Result.WorkflowRunId);
        Assert.Equal(approved.Operation.ExecutionId,result.Result.Handle.ExecutionId);Assert.Equal(approved.Operation.MessageId,result.Result.Handle.ExternalRequestId);
        Assert.Equal(approved.Operation,(await f.Store().GetAsync(approved.ApprovalId))!.Operation);
    }
    [Theory]
    [InlineData("missing-input")] [InlineData("text")] [InlineData("root")] [InlineData("timeout")] [InlineData("task")]
    [InlineData("version")] [InlineData("digest")] [InlineData("approval-id")]
    [InlineData("operator")] [InlineData("missing-operator")] [InlineData("self")] [InlineData("expired-operator")]
    [InlineData("expired-approval")] [InlineData("revoked")] [InlineData("policy")]
    [InlineData("self-approved")] [InlineData("forged-decision")] [InlineData("configuration")] [InlineData("connector")] [InlineData("caller")]
    public async Task InvalidResumeCannotConsumeOrDispatch(string variant)
    {
        var f = F; var approved = await Setup(f); var request = Request(f, approved); ITrustedCallerContext actor = f.Operator;
        ITrustedCallerContext caller = f.Caller; var inputs = f.Runtime.Inputs;
        switch(variant)
        {
            case "missing-input": request = request with { Input = null }; break;
            case "text": request = request with { Input = f.Runtime.Request with { TaskText = "changed" } }; break;
            case "root": request = request with { Input = f.Runtime.Request with { ProjectRoot = Path.GetPathRoot(root)! } }; break;
            case "timeout": request = request with { Input = f.Runtime.Request with { Timeout = TimeSpan.FromSeconds(1) } }; break;
            case "task": request = request with { Input = f.Runtime.Request with { TaskId = "other" } }; break;
            case "version": request = request with { ApprovalVersion = 0 }; break;
            case "digest": request = request with { BindingDigest = new('F',64) }; break;
            case "approval-id": request = request with { ApprovalId = "other" }; break;
            case "operator": actor = new SyntheticActor(f.Operator.SubjectId); break;
            case "missing-operator": actor = null!; break;
            case "self": actor = f.Self; break;
            case "expired-operator": f.OperatorIdentity = f.OperatorIdentity with { NotAfter = f.Clock.Now }; break;
            case "expired-approval": f.Clock.Now = approved.ExpiresAt; break;
            case "revoked": await f.Store().RevokeAsync(approved.ApprovalId, 1, f.Operator); break;
            case "policy": f.Policy = f.Policy with { Epoch = new(2), Version = new(2) }; await f.Store().ActivatePolicyAsync(); break;
            case "self-approved": await Sql("UPDATE approval_requests SET snapshot=$j",JsonSerializer.Serialize(approved with { DecidedBy=f.Caller.SubjectId })); break;
            case "forged-decision": await Sql("UPDATE approval_requests SET decision_id=NULL,snapshot=$j",JsonSerializer.Serialize(approved with { DecisionId=null })); break;
            case "configuration": inputs=inputs with { Scope=inputs.Scope with { ConfigurationRevision=99 } }; break;
            case "connector": inputs=inputs with { SubmissionCapability=inputs.SubmissionCapability with { ConnectorId="other" } }; break;
            case "caller": caller=new SyntheticActor(f.Caller.SubjectId); break;
        }
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(fake);
        var sql=f.Store();var runtime=new ProtectedExternalRuntime(inputs,new ImmutablePolicySource(f.Policy),f,
            new SqliteExecutionStore(Db,ExecutionStoreProfile.ExplicitV2),sql,sql,sql,http,f.Runtime.Options,f.Clock);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.ResumeAsync(request, caller, actor));
        if (variant == "missing-input") Assert.Equal("InputRequired", Assert.IsType<ExternalHarnessException>(error).Code);
        Assert.Empty(fake.Requests); Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
        Assert.NotEqual(ApprovalStatus.Consumed, (await f.Store().GetAsync(approved.ApprovalId))!.Status);
    }
    [Theory]
    [InlineData("operator")] [InlineData("presence")] [InlineData("control")] [InlineData("policy")] [InlineData("expiry")] [InlineData("decision")]
    public async Task EffectsRevalidateConsumedApprovalAfterSessionCreation(string change)
    {
        var f = F; var approved = await Setup(f); using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(fake);
        fake.OnRequest = async (request, token) =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/session") return;
            switch(change)
            {
                case "operator": f.OperatorEnabled = false; break;
                case "presence": f.OperatorIdentity = f.OperatorIdentity with { FreshHumanPresence = false }; break;
                case "control": await f.Store().RequestCancellationAsync(approved.ApprovalId,"cancel",0,f.Caller); break;
                case "policy": f.Policy = f.Policy with { Epoch = new(2), Version = new(2) }; await f.Store().ActivatePolicyAsync(); break;
                case "expiry": f.Clock.Now = approved.ExpiresAt; break;
                case "decision":
                    var consumed = (await f.Store().GetAsync(approved.ApprovalId))!;
                    await Sql("UPDATE approval_requests SET decision_id='tampered',snapshot=$j", JsonSerializer.Serialize(consumed with { DecisionId = "tampered" })); break;
            }
        };
        var result = await f.ProtectedRuntime(http).ResumeAsync(Request(f,approved), f.Caller, f.Operator);
        Assert.NotEqual(ExternalExecutionStatus.Completed, result.Result.Status);
        Assert.Equal(1, fake.Requests.Count(x => x == "POST /session")); Assert.DoesNotContain(fake.Requests,x=>x.Contains("prompt_async")||x.Contains("abort"));
    }
    [Fact]
    public async Task ApprovedSubmitDoesNotGrantInspectOrCancel()
    {
        var f = F; f.Policy = f.Policy with { Rules = [f.Policy.Rules[0]] }; var approved = await Setup(f);
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(fake);
        var result = await f.ProtectedRuntime(http).ResumeAsync(Request(f,approved),f.Caller,f.Operator);
        Assert.Equal(ExternalExecutionStatus.Unknown,result.Result.Status); Assert.Equal(2,fake.Requests.Count(x=>x.StartsWith("POST ")));
        Assert.DoesNotContain(fake.Requests,x=>x.Contains("/status")||x.Contains("/abort"));
    }
    [Fact]
    public async Task ConsumedEvidenceIsStillRequiredForObservationAfterPromptPost()
    {
        var f=F;var approved=await Setup(f);using var fake=new RuntimeHttpFixture(f.Runtime.Root);using var http=new HttpClient(fake);
        fake.OnRequest=(request,token)=>{if(request.RequestUri!.AbsolutePath.EndsWith("/prompt_async"))f.OperatorEnabled=false;return Task.CompletedTask;};
        var result=await f.ProtectedRuntime(http).ResumeAsync(Request(f,approved),f.Caller,f.Operator);
        Assert.Equal(ExternalExecutionStatus.Unknown,result.Result.Status);Assert.Equal(2,fake.Requests.Count(x=>x.StartsWith("POST ")));
        Assert.DoesNotContain(fake.Requests,x=>x.Contains("/status")||x.Contains("/abort"));
        Assert.Equal(1,fake.Requests.Count(x=>x=="GET /session/ses_runtime")); // Only the authorized pre-submit session check.
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task LostResponsesKeepOriginalCorrelationAndNeverResubmit(bool create)
    {
        var f = F; var approved = await Setup(f);
        using var fake = new RuntimeHttpFixture(f.Runtime.Root) { LoseCreateResponse = create, LoseSubmitResponse = !create }; using var http = new HttpClient(fake);
        var runtime=f.ProtectedRuntime(http); var first=await runtime.ResumeAsync(Request(f,approved),f.Caller,f.Operator);
        await Assert.ThrowsAsync<ExternalHarnessException>(()=>runtime.ResumeAsync(Request(f,approved),f.Caller,f.Operator));
        var second=await runtime.RunAsync(new(ExternalAdmissionMode.Inspect,f.Runtime.Request.TaskId,f.Runtime.Request.Timeout),f.Caller,observeOnce:true);
        Assert.Equal(first.Result.Handle,second.Result.Handle); Assert.Equal(first.Result.WorkflowRunId,second.Result.WorkflowRunId);
        Assert.Equal(create?1:2,fake.Requests.Count(x=>x.StartsWith("POST "))); Assert.Equal(create?ExternalExecutionStatus.Unknown:ExternalExecutionStatus.Completed,first.Result.Status);
    }
    [Fact]
    public async Task OwnedHandoffIsSingleUseNonSerializableAndCannotBeRecoveredFromConsumedRow()
    {
        var f=F;var approved=await Setup(f);var store=f.Store();var executions=new SqliteExecutionStore(Db,ExecutionStoreProfile.ExplicitV2);
        await using var owner=(await executions.TryAcquireAsync(f.Runtime.Request.TaskId))!;
        var initial=SyntheticApproval.Execution(approved.Operation,f.Clock.Now) with { Handle=f.Runtime.Inputs.PreparedHandle };
        var continuation=await store.ConsumeOwnedAsync(Request(f,approved),approved.Operation,initial,0,f.Caller,f.Operator,owner);
        var handoff=await continuation.ConsumeApprovedAsync(owner);
        Assert.Throws<NotSupportedException>(()=>JsonSerializer.Serialize(handoff.Authorization));
        Assert.Throws<NotSupportedException>(()=>JsonSerializer.Deserialize<ConsumedApprovalAuthorization>("{}"));
        Assert.Empty(typeof(ConsumedApprovalAuthorization).GetConstructors());
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await continuation.ConsumeApprovedAsync(owner));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.ConsumeOwnedAsync(Request(f,approved),approved.Operation,initial,0,f.Caller,f.Operator,owner));
    }
    [Fact]
    public async Task DataOnlyConsumedRowAndFakeAllowCannotAuthorizeApprovedEffects()
    {
        var f=F;var approved=await Setup(f);var store=f.Store();
        var executions=new SqliteExecutionStore(Db,ExecutionStoreProfile.ExplicitV2);
        await using var owner=(await executions.TryAcquireAsync(f.Runtime.Request.TaskId))!;
        var initial=SyntheticApproval.Execution(approved.Operation,f.Clock.Now) with { Handle=f.Runtime.Inputs.PreparedHandle };
        await store.ConsumeAsync(approved.ApprovalId,approved.Version,0,approved.BindingDigest,initial,f.Caller);
        var d=new DeterministicPolicyEvaluator(new ImmutablePolicySource(f.Policy),f.Clock).Evaluate(approved.Operation,f.Caller);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.VerifyEffectAsync(approved.Operation,d with { Outcome=PolicyOutcome.Allow,Reason=PolicyReason.Allowed },initial,null,0,ProviderEffect.Validate,f.Caller,owner));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.VerifyApprovedEffectAsync(null!,approved.Operation,d,initial,null,0,ProviderEffect.Validate,f.Caller,owner));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.ConsumeOwnedAsync(Request(f,approved),approved.Operation,initial,0,f.Caller,f.Operator,owner));
    }
    [Theory]
    [InlineData("ownership")] [InlineData("fake-allow")] [InlineData("abort")]
    public async Task OwnedEvidenceRejectsWrongOwnerOrWrongAuthorizationBasis(string variant)
    {
        var f=F;var approved=await Setup(f);var store=f.Store();var executions=new SqliteExecutionStore(Db,ExecutionStoreProfile.ExplicitV2);
        var owner=(await executions.TryAcquireAsync(f.Runtime.Request.TaskId))!;
        try
        {
            var initial=SyntheticApproval.Execution(approved.Operation,f.Clock.Now) with { Handle=f.Runtime.Inputs.PreparedHandle };
            var c=await store.ConsumeOwnedAsync(Request(f,approved),approved.Operation,initial,0,f.Caller,f.Operator,owner);var handoff=await c.ConsumeApprovedAsync(owner);
            var d=new DeterministicPolicyEvaluator(new ImmutablePolicySource(f.Policy),f.Clock).Evaluate(approved.Operation,f.Caller);
            if(variant=="fake-allow")d=d with { Outcome=PolicyOutcome.Allow,Reason=PolicyReason.Allowed };
            if(variant=="ownership")
            {
                await owner.DisposeAsync();await using var replacement=(await executions.TryAcquireAsync(initial.TaskId))!;
                await Assert.ThrowsAsync<InvalidOperationException>(()=>store.VerifyApprovedEffectAsync(handoff.Authorization,approved.Operation,d,initial,null,0,ProviderEffect.Validate,f.Caller,replacement));
            }
            else await Assert.ThrowsAsync<InvalidOperationException>(()=>store.VerifyApprovedEffectAsync(handoff.Authorization,approved.Operation,d,initial,null,0,variant=="abort"?ProviderEffect.Abort:ProviderEffect.Validate,f.Caller,owner));
        }
        finally{await owner.DisposeAsync();}
    }
    [Fact]
    public async Task CancellationWinsBeforeAtomicConsumption()
    {
        var f=F;var approved=await Setup(f);
        await f.Store().RequestCancellationAsync(approved.ApprovalId,"cancel-first",0,f.Caller);
        using var fake=new RuntimeHttpFixture(f.Runtime.Root);using var http=new HttpClient(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.ProtectedRuntime(http).ResumeAsync(Request(f,approved),f.Caller,f.Operator));
        Assert.Empty(fake.Requests);Assert.Equal(0L,await Sql("SELECT count(*) FROM executions"));
    }
    [Fact]
    public async Task ApprovalExpiringDuringConsumptionRollsBackCreatedAndConsumed()
    {
        var f=F;var approved=await Setup(f);
        var store=new SqliteApprovalStore(Db,f.Authority(),f.Clock,10000,p=>{if(p==ApprovalCheckpoint.ApprovalConsumed)f.Clock.Now=approved.ExpiresAt;});
        using var fake=new RuntimeHttpFixture(f.Runtime.Root);using var http=new HttpClient(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.ProtectedRuntime(http,store).ResumeAsync(Request(f,approved),f.Caller,f.Operator));
        Assert.Empty(fake.Requests);Assert.Equal(0L,await Sql("SELECT count(*) FROM executions"));
        Assert.Equal(ApprovalStatus.Approved,(await f.Store().GetAsync(approved.ApprovalId))!.Status);
    }
    [Fact]
    public async Task ConsumptionWriterWinsThenCancellationBlocksFurtherEffects()
    {
        var f=F;var approved=await Setup(f);var executions=new SqliteExecutionStore(Db,ExecutionStoreProfile.ExplicitV2);
        await using var owner=(await executions.TryAcquireAsync(f.Runtime.Request.TaskId))!;
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var store=new SqliteApprovalStore(Db,f.Authority(),f.Clock,10000,p=>
        {if(p!=ApprovalCheckpoint.ApprovalConsumed)return;entered.Set();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("ConsumeGate");});
        var initial=SyntheticApproval.Execution(approved.Operation,f.Clock.Now) with { Handle=f.Runtime.Inputs.PreparedHandle };
        var consume=Task.Run(()=>store.ConsumeOwnedAsync(Request(f,approved),approved.Operation,initial,0,f.Caller,f.Operator,owner));
        Task<TaskControl>? cancel=null;
        try{Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));cancel=Task.Run(()=>f.Store().RequestCancellationAsync(approved.ApprovalId,"cancel-after",0,f.Caller));}
        finally{release.Set();}
        var handoff=await (await consume).ConsumeApprovedAsync(owner);await cancel!;
        var d=new DeterministicPolicyEvaluator(new ImmutablePolicySource(f.Policy),f.Clock).Evaluate(approved.Operation,f.Caller);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store().VerifyApprovedEffectAsync(handoff.Authorization,approved.Operation,d,initial,null,0,ProviderEffect.Validate,f.Caller,owner));
        Assert.Equal(ApprovalStatus.Consumed,(await f.Store().GetAsync(approved.ApprovalId))!.Status);Assert.Equal(1L,await Sql("SELECT version FROM task_controls"));
    }
    static Process Child(params string[] args)
    {
        var config=new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../MAF.ApprovalAcceptance/bin",config,"net10.0/MAF.ApprovalAcceptance.dll"));
        var info=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(dll);foreach(var arg in args)info.ArgumentList.Add(arg);return Process.Start(info)!;
    }
    static async Task<string?> Line(Process p)=>await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
    static async Task Kill(Process p){if(!p.HasExited)p.Kill(entireProcessTree:false);await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
    [Theory]
    [InlineData("ApprovalConsumed",false)] [InlineData("ConsumptionCommitted",true)] [InlineData("DispatchIntentCommitted",true)]
    public async Task RealProcessCrashNeverRestoresApprovalContinuation(string checkpoint,bool committed)
    {
        var f=F;await Setup(f);using var child=Child("resume-crash",Db,checkpoint);var start=child.StartTime;
        try
        {
            var signal=await Line(child);Assert.True(signal=="resume-checkpoint",$"{signal}; {(child.HasExited?await child.StandardError.ReadToEndAsync():"running")}");
            Assert.Equal(start,child.StartTime);await Kill(child);
            Assert.Equal(committed?1L:0L,await Sql("SELECT count(*) FROM executions"));
            Assert.Equal(committed?ApprovalStatus.Consumed:ApprovalStatus.Approved,(await f.Store().GetAsync("resume-approval"))!.Status);
            using var restart=Child(committed?"resume-recover":"resume-run",Db);var json=await restart.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(0,restart.ExitCode);
            var result=JsonSerializer.Deserialize<ExternalTaskResult>(json)!;Assert.Equal("runtime-run",result.WorkflowRunId);Assert.Equal("runtime-execution",result.Handle.ExecutionId);
            var requests=File.Exists(Db+".http")?File.ReadAllLines(Db+".http"):[];
            Assert.Equal(committed?0:2,requests.Count(x=>x.StartsWith("POST ")));Assert.DoesNotContain(requests,x=>x.Contains("abort"));
            Assert.Equal(1L,await Sql("SELECT count(*) FROM executions"));Assert.Equal("ok",await Sql("PRAGMA integrity_check"));
            output.WriteLine($"Crash PID={child.Id}, startUTC={start.ToUniversalTime():O}, restartPID={restart.Id}, checkpoint={checkpoint}, consumptionCommitted={committed}, task=runtime-task, execution=runtime-execution, run=runtime-run, message=msg_runtime, restartPOST={(committed?0:2)}, integrity=ok");
        }
        finally{await Kill(child);}
    }
    [Fact]
    public async Task TwoActualResumeProcessesHaveOneCommittingOwner()
    {
        var f=F;await Setup(f);using var first=Child("resume-race",Db);using var second=Child("resume-race",Db);
        try
        {
            Assert.Equal("ready",await Line(first));Assert.Equal("ready",await Line(second));
            await first.StandardInput.WriteLineAsync("go");await second.StandardInput.WriteLineAsync("go");
            var signals=await Task.WhenAll(Line(first),Line(second));Assert.Contains("owned",signals);Assert.Contains("busy",signals);
            var winner=signals[0]=="owned"?first:second;await winner.StandardInput.WriteLineAsync("continue");Assert.NotNull(await Line(winner));
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0,first.ExitCode);Assert.Equal(0,second.ExitCode);
            Assert.Equal(2,File.ReadAllLines(Db+".http").Count(x=>x.StartsWith("POST ")));Assert.Equal(1L,await Sql("SELECT count(*) FROM executions"));
            Assert.Equal(ApprovalStatus.Consumed,(await f.Store().GetAsync("resume-approval"))!.Status);
            output.WriteLine($"Resume race PIDs={first.Id}/{second.Id}, winnerPID={winner.Id}, Created=1, consumedVersion=2, CreatePOST=1, PromptPOST=1, AbortPOST=0");
        }
        finally{await Kill(first);await Kill(second);}
    }
    public void Dispose(){foreach(var file in Directory.GetFiles(root,"*",SearchOption.AllDirectories))File.Delete(file);foreach(var dir in Directory.GetDirectories(root))Directory.Delete(dir);Directory.Delete(root);}
}
