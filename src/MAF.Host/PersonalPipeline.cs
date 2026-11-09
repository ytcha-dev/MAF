using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using MAF.Agents.Review;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Core.Tools;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace MAF.Host;

// Personal read-only MVP: PCA -> OpenCode -> Evidence Reviewer -> Human gate, composed from the
// existing official four-stage Workflow. The operator explicitly opts in with --pipeline; this is
// single-operator personal use, not production trust, identity or human-consent evidence.
internal sealed record PipelineRun(HandoffRun Run, ExternalExecutionHandle Handle);

internal static class PersonalPipeline
{
    internal static async Task<PipelineRun> RunAsync(HandoffTask task, IChatClient pcaClient, IChatClient reviewerClient,
        OpenCodeOptions options, HttpMessageHandler transport, TimeSpan pollInterval, CancellationToken token, TextWriter? diagnostics = null)
    {
        diagnostics ??= TextWriter.Null;
        var pcaModel = new JsonReplyCleaner(pcaClient);
        var reviewerModel = new JsonReplyCleaner(reviewerClient);
        var tools = new ProjectTools(task.Workspace);
        if (tools.Root != task.Workspace) throw new ArgumentException("Workspace must be a canonical absolute directory.");
        var request = new ExternalTaskRequest(task.TaskId, task.Workspace, task.Objective, task.StageTimeout);
        var access = new PersonalAccess(request, task.WorkflowRunId);
        using var http = new HttpClient(transport, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds + 5) };
        var client = new OpenCodeClient(access, http, options, TimeProvider.System); // Clock enables terminal + read-source capture.
        var handle = client.PrepareExecution();
        var delegation = new Delegation(access, client, handle, request, task.WorkflowRunId, pollInterval);
        var sources = new VerifiedSourceMode(new BoundedSourceReader(task.Workspace, TimeProvider.System));
        var flow = new ControlledHandoffWorkflow(new PipelineBriefing(pcaModel, tools, diagnostics),
            intent => intent == request ? PolicyOutcome.Allow : PolicyOutcome.Deny, // Exact original intent only.
            delegation, new NativeEvidenceReviewer(reviewerModel).ReviewAsync, handle, TimeProvider.System, sources);
        var run = await flow.RunAsync(task, token);
        if (run.Outcome.Review?.Status == EvidenceAssessmentStatus.ReviewUnavailable && reviewerModel.LastFinalText is { } raw)
        {
            diagnostics.WriteLine("[Reviewer] 回應未通過檢查（格式、未讀過的 reference 或被拒的用語），前 800 字：");
            diagnostics.WriteLine(ProjectTools.Redact(raw.Length > 800 ? raw[..800] : raw));
        }
        // The shared Workflow labels itself offline; this route is a live personal run, so say that instead.
        var limitations = run.Outcome.Limitations.Select(l => l == OfflineLabel ? LiveLabel : l).ToImmutableArray();
        return new(run with { Outcome = run.Outcome with { Limitations = limitations } }, handle);
    }

    const string OfflineLabel = "Offline controlled workflow; no real provider or human consent.";
    internal const string LiveLabel = "Personal read-only pipeline (explicit --pipeline opt-in); no production trust or recorded human consent.";

    // The OpenCode executor owns its budget and remote abort; no second racing deadline.
    sealed class Delegation(PersonalAccess access, OpenCodeClient client, ExternalExecutionHandle handle,
        ExternalTaskRequest request, string workflowRunId, TimeSpan pollInterval) : IOwnedHandoffDelegation
    {
        public async Task<ExternalTaskResult> DelegateAsync(ExternalTaskRequest original, string runId, CancellationToken token)
        {
            if (original != request || runId != workflowRunId) throw new ExternalHarnessException("PipelineIdentityMismatch");
            var executor = new OpenCodeExecutor(access, client, workflowRunId, pollInterval, token, handle);
            var graph = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();
            await using var run = await InProcessExecution.RunAsync(graph, request, workflowRunId, CancellationToken.None);
            return run.NewEvents.OfType<WorkflowOutputEvent>().Select(e => e.Data).OfType<ExternalTaskResult>().Single();
        }
    }

    // Binds every provider effect to the single original read-only request. Reattach/metadata are not offered.
    internal sealed class PersonalAccess(ExternalTaskRequest original, string workflowRunId) : IRuntimeAccess
    {
        public bool SuppressImplicitAbort => false;
        public void DemandEntry() { }
        public IDisposable Enter(ProviderEffect effect, ExternalTaskRequest? request = null, string? run = null, ExternalExecutionHandle? handle = null)
        {
            if (effect is not (ProviderEffect.Validate or ProviderEffect.CreateSession or ProviderEffect.SubmitPrompt
                    or ProviderEffect.Observe or ProviderEffect.Abort)
                || request is not null && request != original || run is not null && run != workflowRunId
                || original.Policy != ExternalExecutionPolicy.ReadOnly)
                throw new ExternalHarnessException("PipelineEffectNotAllowed");
            return Scope.Instance;
        }
        public Task BeforeWireAsync(HttpMethod method, string path, string task, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (task != original.TaskId) throw new ExternalHarnessException("PipelineIdentityMismatch");
            return Task.CompletedTask;
        }
        sealed class Scope : IDisposable { internal static readonly Scope Instance = new(); public void Dispose() { } }
    }

    internal static int ExitCode(WorkflowOutcome outcome) => outcome.HumanGate switch
    {
        HumanGateStatus.ReadyForHumanReview => 0,
        HumanGateStatus.NeedsHumanReview => 3,
        HumanGateStatus.WaitingForApproval => 5,
        _ => 1
    };

    static readonly string[] Roles = ["PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate"];
    static string RoleName(string id) => id switch
    {
        "PCA" => "PCA（理解專案、提出任務簡報）",
        "ExternalDelegation" => "OpenCode（唯讀執行任務）",
        "EvidenceReviewer" => "Reviewer（獨立讀原始碼審查證據）",
        "HumanGate" => "Human Gate（彙整，交給人判斷）",
        _ => id
    };

    // Events show the actual official Workflow hand-offs between the role executors.
    internal static ImmutableArray<string> Handoffs(HandoffRun run) => run.Events.OfType<ExecutorEvent>()
        .Where(e => Roles.Contains(e.ExecutorId) && e is ExecutorInvokedEvent or ExecutorCompletedEvent or ExecutorFailedEvent)
        .Select(e => $"{e.GetType().Name.Replace("Executor", "").Replace("Event", "")} {e.ExecutorId}").ToImmutableArray();

    internal static string Text(PipelineRun pipeline)
    {
        var o = pipeline.Run.Outcome;
        var b = new StringBuilder();
        b.AppendLine("=== MAF 多 Agent Pipeline（個人唯讀 MVP）===");
        b.AppendLine($"Task: {o.TaskId}   Workflow run: {o.WorkflowRunId}");
        b.AppendLine();
        b.AppendLine("[分工] 各角色階段結果");
        foreach (var stage in o.Stages) b.AppendLine($"  - {RoleName(stage.Name)} → {stage.State}");
        b.AppendLine();
        b.AppendLine("[交流] 官方 Workflow 交接順序");
        foreach (var h in Handoffs(pipeline.Run)) b.AppendLine("  " + h);
        b.AppendLine();
        b.AppendLine($"[PCA] 任務簡報: {o.Comprehension}");
        foreach (var l in o.Limitations.Where(IsPca)) b.AppendLine("  " + l);
        var r = o.ExternalResult;
        b.AppendLine($"[OpenCode] 狀態: {r?.Status.ToString() ?? "(未執行)"}   session: {r?.Handle.ExternalSessionId ?? "-"}   HTTP requests: {r?.Requests ?? 0}");
        if (r?.ErrorCode is { } code) b.AppendLine($"  錯誤代碼: {code}{(Hints.TryGetValue(code, out var hint) ? " — " + hint : "")}");
        if (r is not null) { b.AppendLine("  摘要:"); foreach (var line in r.Summary.Split('\n')) b.AppendLine("    " + line); }
        b.AppendLine($"  來源候選: {r?.SourceCandidates.Length ?? 0}");
        var a = o.Review;
        b.AppendLine($"[Reviewer] 評估: {a?.Status.ToString() ?? "(未執行)"}");
        if (a is not null)
        {
            b.AppendLine("  " + a.Summary);
            if (a.Sources is { } s) b.AppendLine($"  已獨立驗證來源: {s.Admitted.Length} 筆 [{string.Join(", ", s.Admitted.Select(x => $"{x.RelativePath}:{x.StartLine}-{x.EndLine}"))}]");
        }
        b.AppendLine($"[Human Gate] {o.HumanGate}{(o.ReasonCode is null ? "" : "   原因: " + o.ReasonCode)}");
        b.AppendLine();
        b.AppendLine("[限制]");
        foreach (var l in o.Limitations.Where(l => !IsPca(l)).Distinct()) b.AppendLine("  - " + l);
        return b.ToString();
    }

    // Common OpenCode discovery/session refusals and what to check locally.
    static readonly Dictionary<string, string> Hints = new(StringComparer.Ordinal)
    {
        ["UnapprovedWorkspace"] = "--project 與 OpenCode 啟動目錄不一致或不是本機目錄。",
        ["UnhealthyServer"] = "OpenCode server 回報不健康。",
        ["UnsupportedServerApi"] = "OpenCode 版本的 API 不相容，請改用 opencode-ai@1.19.0。",
        ["SessionPermissionUnsupported"] = "OpenCode 版本不支援 session 權限規則，請改用 opencode-ai@1.19.0。",
        ["InvalidDiscoveryResponse"] = "OpenCode 回應格式無法解析，請改用 opencode-ai@1.19.0。",
        ["InvalidAgentPolicy"] = "/agent 回應不是陣列。",
        ["ReadOnlyAgentMissing"] = "OpenCode 沒有載入 maf_readonly agent：確認 opencode.json 在 OpenCode 啟動的資料夾、JSON 有效、agent 名稱正確。",
        ["AgentAllowsDangerousTools"] = "maf_readonly 的有效權限不是 deny-all 後只開 read/glob/grep；檢查全域或專案設定是否額外開了工具。",
        ["AgentAllowsExternalDirectories"] = "maf_readonly 的有效權限允許 external_directory；請設為 deny。",
        ["TransportDisconnected"] = "連不上 OpenCode：確認 OPENCODE_BASE_URL 與 server 是否在跑。",
    };

    static bool IsPca(string l) => l.StartsWith(PipelineBriefing.PlanPrefix, StringComparison.Ordinal)
        || l.StartsWith(PipelineBriefing.FocusPrefix, StringComparison.Ordinal) || l.StartsWith(PipelineBriefing.RiskPrefix, StringComparison.Ordinal);

    internal static string Json(PipelineRun pipeline) => JsonSerializer.Serialize(new
    {
        pipeline.Run.Outcome,
        Handoffs = Handoffs(pipeline.Run),
        SourceCandidates = pipeline.Run.Outcome.ExternalResult?.SourceCandidates.Length ?? 0
    }, ExternalTaskResult.JsonOptions);
}
