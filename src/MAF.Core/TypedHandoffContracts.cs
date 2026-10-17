using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MAF.Core;

// Proposals and evidence assessment only: none of these DTOs is an authorization ticket.
public enum ComprehensionStatus { Proposed, Unavailable, Invalid }
public enum EvidenceAssessmentStatus { SufficientEvidence, NeedsHumanReview, InsufficientEvidence, NotApplicable, ReviewUnavailable }
public enum HumanGateStatus { ReadyForHumanReview, NeedsHumanReview, WaitingForApproval, Blocked }
public enum HandoffStageState { Completed, Failed, Skipped, Unavailable }

public sealed record HandoffTask(string TaskId, string WorkflowRunId, string Workspace, string Objective, TimeSpan StageTimeout);
public sealed record TaskBrief([property: JsonRequired] string TaskId, [property: JsonRequired] string Workspace,
    [property: JsonRequired] string Objective, [property: JsonRequired] ExternalExecutionPolicy Intention,
    [property: JsonRequired] ComprehensionStatus Status, [property: JsonRequired] ImmutableArray<string> SourceReferences,
    [property: JsonRequired] ImmutableArray<string> Limitations);
public sealed record TaskEvidence(HandoffTask Task, TaskBrief Brief, ExternalTaskResult ExternalResult,
    ImmutableArray<string> EvidenceReferences, ImmutableArray<string> Limitations);
public sealed record ReviewAssessment(string TaskId, string ExecutionId, EvidenceAssessmentStatus Status,
    string Summary, ImmutableArray<string> EvidenceReferences, ImmutableArray<string> Limitations);
public sealed record HandoffStage(string Name, HandoffStageState State);
public sealed record WorkflowOutcome(string TaskId, string WorkflowRunId, ComprehensionStatus Comprehension,
    ExternalTaskResult? ExternalResult, ReviewAssessment? Review, HumanGateStatus HumanGate,
    ImmutableArray<string> EvidenceReferences, ImmutableArray<string> Limitations,
    ImmutableArray<HandoffStage> Stages, string? ReasonCode);

public interface ITaskBriefing
{
    Task<TaskBrief> BriefAsync(HandoffTask task, CancellationToken token);
}

public static class HandoffValidation
{
    public static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    public static bool References(ImmutableArray<string> values) => !values.IsDefault && values.Length <= 16 && values.All(v => Text(v, 256));
    public static bool ValidTask(HandoffTask task) => Text(task.TaskId, 100) && Text(task.WorkflowRunId, 100)
        && Text(task.Workspace, 1024) && Text(task.Objective, 4000)
        && task.StageTimeout >= TimeSpan.FromMilliseconds(10) && task.StageTimeout <= TimeSpan.FromMinutes(2);

    // Exact objective/workspace echoes prevent a proposal from expanding the admitted intention.
    // PCA citations are not independently admitted source evidence in this increment.
    public static bool ValidBrief(TaskBrief? brief, HandoffTask task) => brief is not null
        && brief.TaskId == task.TaskId && brief.Workspace == task.Workspace && brief.Objective == task.Objective
        && brief.Intention == ExternalExecutionPolicy.ReadOnly && brief.Status == ComprehensionStatus.Proposed
        && References(brief.SourceReferences) && brief.SourceReferences.IsEmpty && References(brief.Limitations);
}
