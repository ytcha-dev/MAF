using MAF.Core;

namespace MAF.Host;

// Output-only projection of the existing INTERNAL controlled Workflow composition.
// No operational factory/dispatch, trust acquisition, JSON proof ingestion or public ingress.
internal static class LocalSoloOutcomeMapper
{
    internal static LocalResultView FromControlledWorkflow(WorkflowOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var external = outcome.ExternalResult;
        if (external is not null && (external.TaskId != outcome.TaskId || external.WorkflowRunId != outcome.WorkflowRunId))
            throw new ArgumentException("OutcomeCorrelationMismatch");
        var review = outcome.Review;
        if (review is not null && (external is null || review.TaskId != outcome.TaskId || review.ExecutionId != external.Handle.ExecutionId))
            throw new ArgumentException("OutcomeCorrelationMismatch");
        // Source references come only from reader-issued, exactly bound admitted observations,
        // not the outcome's mixed execution/summary/PCA reference strings. View records are NOT proof.
        var evidence = review?.Sources?.Admitted.Where(s => external is not null
            && s.Binding.TaskId == outcome.TaskId && s.Binding.WorkflowRunId == outcome.WorkflowRunId
            && s.Binding.ExecutionId == external.Handle.ExecutionId
            && s.Binding.SessionId == external.Handle.ExternalSessionId && s.Binding.MessageId == external.Handle.ExternalRequestId)
            .Take(8).Select(s => new LocalEvidenceView(s.Reference,
                $"ControlledOfflineFixture / NotLiveProvider; independently acquired review-time snapshot {s.RelativePath}:{s.StartLine}-{s.EndLine}; not semantic correctness or currentness")).ToArray() ?? [];
        return new()
        {
            Kind = LocalViewKind.ControlledOfflineFixture,
            TaskId = outcome.TaskId, WorkflowRunId = outcome.WorkflowRunId,
            Harness = external?.Handle.Provider, ExecutionId = external?.Handle.ExecutionId,
            Status = external?.Status, Summary = external?.Summary,
            // Fixed semantic summaries precede bounded source lists; no heuristic severity parser.
            Limitations = new[]
                { "Operational execution blocked / ProductionTrustUnavailable; controlled fixture output grants no execution rights.",
                  external?.Status == ExternalExecutionStatus.Unknown
                    ? "Unknown execution outcome; not Failed/Cancelled or safely retryable. No automatic replacement or resubmission."
                    : $"External status: {external?.Status.ToString() ?? "Unavailable"}; completion is not human acceptance.",
                  $"Evidence/Reviewer: {review?.Status.ToString() ?? "Unavailable"}; source provenance is not semantic correctness." }
                .Concat(review?.Limitations.Take(16) ?? [])
                .Concat(external?.Limitations.Take(16) ?? [])
                .Concat(outcome.Limitations.Take(50)).Distinct()
                .Concat(new[] { "Controlled offline dependencies; scripted models and fake transport are not live-provider evidence.",
                    "Display references cannot restore VerifiedSourceEvidence or authorize execution." }).ToArray(),
            Evidence = evidence, Assessment = review?.Summary, ReviewStatus = review?.Status,
            Readiness = outcome.HumanGate,
            Stages = outcome.Stages.Take(8).Select(s => $"{s.Name}: {s.State}").ToArray(),
            MafApproval = outcome.ReasonCode == "RequireApproval" ? "RequireApproval (not approved or executable)" : "NotEstablished",
            HarnessPermission = external?.Status == ExternalExecutionStatus.WaitingForApproval
                ? "WaitingForApproval observation; authority NotEstablished" : "NotEstablished",
            HumanChoice = LocalHumanChoice.NotEstablished,
            AdapterDiagnostics = external is null ? [] :
                [ $"Session: {external.Handle.ExternalSessionId ?? "Unavailable"}",
                  $"Request: {external.Handle.ExternalRequestId ?? "Unavailable"}",
                  $"Adapter request count: {external.Requests}; duration milliseconds: {external.DurationMilliseconds}" ]
        };
    }
}
