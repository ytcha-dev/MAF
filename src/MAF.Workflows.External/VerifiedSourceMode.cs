using System.Collections.Immutable;
using MAF.Core;
using MAF.Core.Tools;

namespace MAF.Workflows.External;

// Explicitly selected test composition. No production default and no summary citation parsing.
internal sealed class VerifiedSourceMode(BoundedSourceReader reader,
    Func<TaskEvidence, CancellationToken, Task<ImmutableArray<SourceCandidate>>> candidates)
{
    // Provider path: consume only this observation's candidates. No synthetic callback fallback.
    internal VerifiedSourceMode(BoundedSourceReader reader)
        : this(reader, (e, _) => Task.FromResult(e.ExternalResult.SourceCandidates)) { }
    internal async Task<ReviewAssessment> ReviewAsync(TaskEvidence evidence,
        Func<TaskEvidence, CancellationToken, Task<ReviewAssessment>> reviewer, CancellationToken token)
    {
        var batch = reader.Acquire(evidence, await candidates(evidence, token), token);
        ReviewAssessment Insufficient(SourceEvidenceBatch result) => new(evidence.Task.TaskId, evidence.ExternalResult.Handle.ExecutionId,
            result.Items.Any(i => i.Status == SourceEvidenceStatus.Unavailable) ? EvidenceAssessmentStatus.ReviewUnavailable : EvidenceAssessmentStatus.InsufficientEvidence,
            "Required independent source evidence is absent, limited or changed.",
            result.Admitted.Select(s => s.Reference).ToImmutableArray(),
            ["No semantic correctness or execution-time source consistency is certified."]) { Sources = result };
        if (!batch.Sufficient) return Insufficient(batch); // Never fall back to simulated summary-only success.
        var references = batch.Admitted.Select(s => s.Reference).ToImmutableArray();
        var assessment = await reviewer(evidence with { Sources = batch, EvidenceReferences = references,
            Limitations = ["Independently acquired review-time source snapshot; semantic correctness remains unverified."] }, token);
        token.ThrowIfCancellationRequested();
        var checkedItems = batch.Items.Select(i => reader.IsCurrent(i.Evidence!) ? i : new SourceEvidenceItem(i.CandidateIndex, SourceEvidenceStatus.Changed)).ToImmutableArray();
        var current = new SourceEvidenceBatch(checkedItems);
        if (!current.Sufficient) return Insufficient(current);
        if (assessment is null || assessment.EvidenceReferences.IsDefault || assessment.EvidenceReferences.Any(r => !references.Contains(r))
            || assessment.Status == EvidenceAssessmentStatus.SufficientEvidence && references.Any(r => !assessment.EvidenceReferences.Contains(r)))
            throw new InvalidOperationException("InvalidSourceAssessment");
        return assessment with { Sources = current, Limitations = [..assessment.Limitations,
            "Source verification is review-time only; no semantic correctness or human approval."] };
    }
}
