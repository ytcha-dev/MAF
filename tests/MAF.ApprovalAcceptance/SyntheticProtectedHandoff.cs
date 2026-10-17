using System.Text.Json;
using MAF.Agents.Comprehension;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Core.Tools;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Extensions.AI;

namespace MAF.ApprovalAcceptance;

// Test-owned only. Initialization creates schema/authority but NEVER pre-admits the task.
public sealed class SyntheticProtectedHandoff : IDisposable
{
    public SyntheticCancellation F { get; }
    public RuntimeHttpFixture Fake { get; }
    public HttpClient Http { get; }
    public int PcaCalls, ReviewCalls;
    public bool BadBrief, ReviewFails;
    internal Action<TaskEvidence>? OnReview;
    internal EvidenceAssessmentStatus SourceReviewStatus = EvidenceAssessmentStatus.SufficientEvidence;
    internal Func<HttpRequestMessage, HttpResponseMessage, HttpResponseMessage>? ResponseTransform;
    public HandoffTask Task => new(F.Runtime.Request.TaskId, F.Runtime.Inputs.NewWorkflowRunId, F.Runtime.Root,
        F.Runtime.Request.TaskText, F.Runtime.Request.Timeout);
    internal ProtectedHandoffDelegation? Adapter { get; private set; }
    public SyntheticProtectedHandoff(string db, Action<string>? gate = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        F = new(db, v4: true);
        Fake = new(F.Runtime.Root) { StatePath = db + ".provider", LedgerPath = db + ".http" };
        Http = ReceiptTransport.ForTests(new ResponseGate(Fake, gate, (request, response) => ResponseTransform?.Invoke(request, response) ?? response));
    }
    public async Task Initialize()
    {
        await ApprovalSchema.InitializeOrMigrateAsync(F.Runtime.Database, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(F.Runtime.Database, SyntheticApproval.Maintenance, F);
        await ApprovalSchema.MigrateV3ToV4Async(F.Runtime.Database, SyntheticApproval.Maintenance, F);
        await F.Store().ActivatePolicyAsync();
    }
    internal ControlledHandoffWorkflow Flow(ProtectedExternalRuntime? runtime = null, ITrustedCallerContext? caller = null,
        ExternalAdmissionMode mode = ExternalAdmissionMode.Submit, Action? handoff = null, ExternalExecutionHandle? expected = null,
        VerifiedSourceMode? sources = null)
    {
        var original = F.Runtime.Request; var input = F.Runtime.Inputs;
        expected ??= input.PreparedHandle;
        Adapter = new(runtime ?? F.Protected(Http), caller ?? F.Runtime.Caller, original, input.NewWorkflowRunId, expected, mode)
        { BeforeHandoff = handoff };
        return new(new TaskBriefingAdapter(new Chat(this), new ProjectTools(F.Runtime.Root)),
            _ => PolicyOutcome.Allow, Adapter, Review, expected, TimeProvider.System, sources);
    }
    Task<ReviewAssessment> Review(TaskEvidence evidence, CancellationToken token)
    {
        ReviewCalls++;
        OnReview?.Invoke(evidence);
        if (ReviewFails) throw new InvalidOperationException("SyntheticReviewUnavailable");
        return System.Threading.Tasks.Task.FromResult(new ReviewAssessment(evidence.Task.TaskId, evidence.ExternalResult.Handle.ExecutionId,
            SourceReviewStatus, "Simulated summary evidence review; no production semantic certification.",
            evidence.EvidenceReferences, evidence.Limitations));
    }
    public SqliteApprovalStore Store(Action<string> gate) => new(F.Runtime.Database, F.Authority(), F.Clock, 10000,
        point =>
        {
            gate(point.ToString());
            if (point == ApprovalCheckpoint.DispatchIntentCommitted)
            {
                var record = F.Executions.GetAsync(F.Runtime.Request.TaskId).GetAwaiter().GetResult();
                if (record?.SubmissionPhase == SubmissionPhase.PromptIntended) gate("PromptIntentCommitted");
            }
        }, ApprovalStoreProfile.ExplicitV4, gate);
    sealed class Chat(SyntheticProtectedHandoff owner) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            owner.PcaCalls++;
            var task = owner.Task;
            var brief = new TaskBrief(task.TaskId, owner.BadBrief ? task.Workspace + "-outside" : task.Workspace, task.Objective,
                ExternalExecutionPolicy.ReadOnly, ComprehensionStatus.Proposed, [], []);
            return System.Threading.Tasks.Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                JsonSerializer.Serialize(brief, ExternalTaskResult.JsonOptions))));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await System.Threading.Tasks.Task.CompletedTask; yield break; }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    sealed class ResponseGate(HttpMessageHandler inner, Action<string>? gate,
        Func<HttpRequestMessage, HttpResponseMessage, HttpResponseMessage> transform) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/prompt_async", StringComparison.Ordinal))
                gate?.Invoke("PromptHttpReturned");
            return transform(request, response);
        }
    }
    public void Dispose() => Http.Dispose();
}
