using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using MAF.Core;
using MAF.Core.Policy;
using Microsoft.Agents.AI.Workflows;

namespace MAF.Workflows.External;

// Offline proof boundary, internal to the test-owned composition. It cannot construct an
// OpenCode client or a production trust context. Policy assessment is not a dispatch ticket.
internal interface IControlledDelegation
{
    Task<ExternalTaskResult> DelegateAsync(ExternalTaskRequest request, string workflowRunId, CancellationToken token);
}

internal sealed record HandoffRun(WorkflowOutcome Outcome, ImmutableArray<WorkflowEvent> Events);

internal sealed class ControlledHandoffWorkflow(ITaskBriefing briefingSource,
    Func<ExternalTaskRequest, PolicyOutcome> assessmentSource, IControlledDelegation delegationSource,
    Func<TaskEvidence, CancellationToken, Task<ReviewAssessment>> reviewSource, ExternalExecutionHandle originalHandle,
    TimeProvider clock)
{
    readonly ITaskBriefing briefing = briefingSource;
    readonly Func<ExternalTaskRequest, PolicyOutcome> assess = assessmentSource;
    readonly IControlledDelegation delegation = delegationSource;
    readonly Func<TaskEvidence, CancellationToken, Task<ReviewAssessment>> reviewer = reviewSource;
    readonly ExternalExecutionHandle expectedHandle = originalHandle;
    sealed record Entry(HandoffTask Input, Lazy<Task<HandoffRun>> Run);
    readonly ConcurrentDictionary<string, Entry> runs = new(StringComparer.Ordinal);
    readonly object identityGate = new();
    internal Task<HandoffRun> RunAsync(HandoffTask task, CancellationToken token = default)
    {
        if (!HandoffValidation.ValidTask(task) || !ValidHandle(expectedHandle)) throw new ArgumentException("InvalidHandoffInput");
        Entry entry;
        lock (identityGate)
        {
            if (runs.Count >= 100 && !runs.ContainsKey(task.TaskId)) throw new InvalidOperationException("HandoffCapacityReached");
            entry = runs.GetOrAdd(task.TaskId, _ => new(task, new(() => ExecuteAsync(task, token), LazyThreadSafetyMode.ExecutionAndPublication)));
        }
        if (entry.Input != task) throw new InvalidOperationException("TaskIdentityConflict");
        return entry.Run.Value; // One attempt per instance; no durable restart/replay claim.
    }

    async Task<HandoffRun> ExecuteAsync(HandoffTask task, CancellationToken token)
    {
        var pca = new Pca(this, token);
        var external = new Delegate(this, token);
        var review = new Reviewer(this, token);
        var gate = new HumanGate();
        var workflow = new WorkflowBuilder(pca).AddEdge(pca, external).AddEdge(external, review)
            .AddEdge(review, gate).WithOutputFrom(gate).Build();
        // Keep the official engine alive to carry typed blocked/cancelled outcomes to the gate.
        await using var run = await InProcessExecution.RunAsync(workflow, task, task.WorkflowRunId, CancellationToken.None);
        var events = run.NewEvents.ToImmutableArray();
        var outcome = events.OfType<WorkflowOutputEvent>().Select(e => e.Data).OfType<WorkflowOutcome>().Single();
        if (run.SessionId != outcome.WorkflowRunId) throw new InvalidOperationException("WorkflowIdentityMismatch");
        return new(outcome, events);
    }

    // Bounded waits also handle an uncooperative specialist. Cancellation never calls provider Abort.
    async Task<T> Within<T>(Func<CancellationToken, Task<T>> action, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        return await action(linked.Token).WaitAsync(linked.Token);
    }

    sealed record BriefHandoff(HandoffTask Task, TaskBrief? Brief, HandoffStageState State, string? Reason);
    sealed record ExternalHandoff(BriefHandoff Input, ExternalTaskResult? Result, HandoffStageState State, string? Reason);
    sealed record ReviewHandoff(ExternalHandoff Input, ReviewAssessment? Assessment, HandoffStageState State, string? Reason);

    sealed class Pca(ControlledHandoffWorkflow owner, CancellationToken token) : Executor<HandoffTask, BriefHandoff>("PCA")
    {
        public override async ValueTask<BriefHandoff> HandleAsync(HandoffTask task, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var brief = await owner.Within(t => owner.briefing.BriefAsync(task, t), task.StageTimeout, token);
                return HandoffValidation.ValidBrief(brief, task) ? new(task, brief, HandoffStageState.Completed, null)
                    : new(task, null, HandoffStageState.Failed, "InvalidTaskBrief");
            }
            catch (JsonException) { return new(task, null, HandoffStageState.Failed, "InvalidTaskBrief"); }
            catch (InvalidOperationException error) when (error.Message == "InvalidTaskBrief")
            { return new(task, null, HandoffStageState.Failed, "InvalidTaskBrief"); }
            catch (Exception) { return new(task, null, HandoffStageState.Unavailable, "BriefingUnavailable"); }
        }
    }

    sealed class Delegate(ControlledHandoffWorkflow owner, CancellationToken token) : Executor<BriefHandoff, ExternalHandoff>("ExternalDelegation")
    {
        public override async ValueTask<ExternalHandoff> HandleAsync(BriefHandoff input, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            if (input.State != HandoffStageState.Completed || token.IsCancellationRequested)
                return new(input, null, HandoffStageState.Skipped, input.Reason ?? "WorkflowCancelled");
            var task = input.Task;
            // Original application input, never model-selected task text, workspace or privileges.
            var request = new ExternalTaskRequest(task.TaskId, task.Workspace, task.Objective, task.StageTimeout);
            bool started = false;
            try
            {
                var decision = owner.assess(request);
                if (decision != PolicyOutcome.Allow)
                    return new(input, null, HandoffStageState.Skipped,
                        decision == PolicyOutcome.RequireApproval ? "RequireApproval" : "DelegationDenied");
                token.ThrowIfCancellationRequested();
                started = true;
                var result = await owner.Within(t => owner.delegation.DelegateAsync(request, task.WorkflowRunId, t), task.StageTimeout, token);
                if (!owner.ValidResult(result, task)) return new(input, null, HandoffStageState.Failed, "ExternalCorrelationOrShapeInvalid");
                // Snapshot existing DTO collections before passing them to a different specialist.
                result = result with { Limitations = result.Limitations.ToImmutableArray(), Trace = result.Trace.ToImmutableArray() };
                return new(input, result, HandoffStageState.Completed, null);
            }
            catch (Exception)
            {
                // No retry, replacement session, terminal invention or abort after uncertain delivery.
                return new(input, started ? new(task.TaskId, task.WorkflowRunId, ExternalExecutionStatus.Unknown,
                    owner.expectedHandle, "Delegation interrupted; original execution requires inspection.",
                    ["No automatic retry or Abort."], [], ErrorCode: "DelegationUncertain") : null,
                    HandoffStageState.Unavailable, started ? "DelegationUncertain" : "DelegationUnavailable");
            }
        }
    }

    bool ValidResult(ExternalTaskResult? result, HandoffTask task) => result is not null && result.Handle is not null
        && result.TaskId == task.TaskId && result.WorkflowRunId == task.WorkflowRunId && Enum.IsDefined(result.Status)
        && result.Handle.ExecutionId == expectedHandle.ExecutionId && result.Handle.Provider == expectedHandle.Provider
        && result.Handle.ExternalRequestId == expectedHandle.ExternalRequestId
        && (expectedHandle.ExternalSessionId is null || result.Handle.ExternalSessionId == expectedHandle.ExternalSessionId)
        && ValidHandle(result.Handle) && HandoffValidation.Text(result.Summary, 4000)
        && result.Limitations is { Count: <= 16 } && result.Limitations.All(v => HandoffValidation.Text(v, 256))
        && result.Trace is { Count: <= 64 } && result.Trace.All(v => v is not null && HandoffValidation.Text(v.Kind, 100))
        && (result.ErrorCode is null || HandoffValidation.Text(result.ErrorCode, 100))
        && result.Requests >= 0 && double.IsFinite(result.DurationMilliseconds) && result.DurationMilliseconds >= 0;
    static bool ValidHandle(ExternalExecutionHandle handle) => HandoffValidation.Text(handle.ExecutionId, 160)
        && HandoffValidation.Text(handle.Provider, 100) && (handle.ExternalSessionId is null || HandoffValidation.Text(handle.ExternalSessionId, 160))
        && (handle.ExternalRequestId is null || HandoffValidation.Text(handle.ExternalRequestId, 160));

    static ImmutableArray<string> Evidence(ExternalTaskResult? result) => result is null ? [] :
        [$"execution:{result.Handle.ExecutionId}", "Correlated external summary; not independently verified source evidence."];

    sealed class Reviewer(ControlledHandoffWorkflow owner, CancellationToken token) : Executor<ExternalHandoff, ReviewHandoff>("EvidenceReviewer")
    {
        static ReviewHandoff Unavailable(ExternalHandoff input, string reason) => new(input,
            new(input.Input.Task.TaskId, input.Result!.Handle.ExecutionId, EvidenceAssessmentStatus.ReviewUnavailable,
                "Independent evidence assessment is unavailable.", [], ["No successful evidence review is claimed."]),
            HandoffStageState.Unavailable, reason);
        public override async ValueTask<ReviewHandoff> HandleAsync(ExternalHandoff input, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            if (input.Result is not { Status: ExternalExecutionStatus.Completed } result || input.State != HandoffStageState.Completed)
                return new(input, null, HandoffStageState.Skipped, input.Reason ?? "ExternalNotCompleted");
            var evidence = new TaskEvidence(input.Input.Task, input.Input.Brief!, result, Evidence(result),
                ["Code-diff review is NotApplicable; no change evidence was supplied.", "Provider summary alone does not prove source-level correctness."]);
            try
            {
                var assessment = await owner.Within(t => owner.reviewer(evidence, t), input.Input.Task.StageTimeout, token);
                if (assessment is null || assessment.TaskId != result.TaskId || assessment.ExecutionId != result.Handle.ExecutionId
                    || !Enum.IsDefined(assessment.Status) || !HandoffValidation.Text(assessment.Summary, 2000)
                    || !HandoffValidation.References(assessment.EvidenceReferences) || !HandoffValidation.References(assessment.Limitations)
                    || assessment.EvidenceReferences.Any(r => !evidence.EvidenceReferences.Contains(r)))
                    return Unavailable(input, "InvalidReviewAssessment");
                return new(input, assessment, HandoffStageState.Completed, null);
            }
            catch (Exception) { return Unavailable(input, "ReviewUnavailable"); }
        }
    }

    sealed class HumanGate() : Executor<ReviewHandoff, WorkflowOutcome>("HumanGate")
    {
        public override ValueTask<WorkflowOutcome> HandleAsync(ReviewHandoff input, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var external = input.Input; var pca = external.Input; var result = external.Result;
            var gate = result?.Status == ExternalExecutionStatus.WaitingForApproval || external.Reason == "RequireApproval"
                ? HumanGateStatus.WaitingForApproval : result?.Status == ExternalExecutionStatus.Completed
                    && input.Assessment?.Status == EvidenceAssessmentStatus.SufficientEvidence ? HumanGateStatus.ReadyForHumanReview
                    : result is null ? HumanGateStatus.Blocked : HumanGateStatus.NeedsHumanReview;
            return ValueTask.FromResult(new WorkflowOutcome(pca.Task.TaskId, pca.Task.WorkflowRunId,
                pca.Brief?.Status ?? (pca.State == HandoffStageState.Failed ? ComprehensionStatus.Invalid : ComprehensionStatus.Unavailable),
                result, input.Assessment, gate, Evidence(result),
                [.. (pca.Brief?.Limitations ?? []), .. (result?.Limitations ?? []), .. (input.Assessment?.Limitations ?? []),
                    "Offline controlled workflow; no real provider or human consent.", "Source evidence and production semantic review remain unverified."],
                [new("PCA", pca.State), new("ExternalDelegation", external.State), new("EvidenceReviewer", input.State), new("HumanGate", HandoffStageState.Completed)],
                input.Reason ?? external.Reason ?? pca.Reason));
        }
    }
}
