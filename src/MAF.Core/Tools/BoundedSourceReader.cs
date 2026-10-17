using System.Collections.Immutable;
using System.Text.Json;

namespace MAF.Core.Tools;

// Trusted composition supplies the exact canonical root. No file discovery, Git or network.
public sealed class BoundedSourceReader
{
    readonly ProjectTools tools;
    readonly TimeProvider clock;
    readonly object issuer = new();
    int reads;
    public string Root => tools.Root;
    public int ReadAttempts => Volatile.Read(ref reads);
    public BoundedSourceReader(string approvedCanonicalRoot, TimeProvider clock)
    {
        if (!Path.IsPathFullyQualified(approvedCanonicalRoot) || approvedCanonicalRoot.StartsWith("\\\\", StringComparison.Ordinal)
            || OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(approvedCanonicalRoot)!).DriveType == DriveType.Network)
            throw new ArgumentException("LocalCanonicalRootRequired");
        tools = new(approvedCanonicalRoot);
        if (tools.Root != approvedCanonicalRoot) throw new ArgumentException("CanonicalRootRequired");
        this.clock = clock;
    }
    public SourceEvidenceBatch Acquire(TaskEvidence context, ImmutableArray<SourceCandidate> candidates, CancellationToken token = default)
    {
        if (context.Task.Workspace != Root || context.ExternalResult.Status != ExternalExecutionStatus.Completed
            || context.Task.TaskId != context.ExternalResult.TaskId || context.Task.WorkflowRunId != context.ExternalResult.WorkflowRunId)
            return Failure(SourceEvidenceStatus.InvalidCandidate);
        if (candidates.IsDefault || candidates.Length > 8) return Failure(SourceEvidenceStatus.LimitExceeded);
        var expected = Binding(context);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        // Validate ALL identity/metadata/budgets before reading any source.
        foreach (var candidate in candidates)
        {
            if (candidate is null || candidate.Binding != expected || !ValidBinding(expected)
                || !HandoffValidation.Text(candidate.RelativePath, 160) || !Enum.IsDefined(candidate.Origin)
                || candidate.StartLine < 1 || candidate.EndLine < candidate.StartLine || (long)candidate.EndLine - candidate.StartLine + 1 > 20
                || candidate.ExpectedAnchor is { Length: > 512 }
                || !seen.Add(string.Join('/', candidate.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))))
                return Failure(SourceEvidenceStatus.InvalidCandidate);
        }
        try { _ = new ProjectTools(Root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { return Failure(SourceEvidenceStatus.Unavailable); }
        var items = ImmutableArray.CreateBuilder<SourceEvidenceItem>();
        for (var index = 0; index < candidates.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var candidate = candidates[index]; SourceEvidenceItem item;
            try
            {
                Interlocked.Increment(ref reads);
                var source = tools.ReadSourceSnapshot(candidate.RelativePath, candidate.StartLine, candidate.EndLine);
                if (source.Redacted) item = new(index, SourceEvidenceStatus.Redacted);
                else if (string.IsNullOrWhiteSpace(candidate.ExpectedAnchor)) item = new(index, SourceEvidenceStatus.ClaimUnverified);
                else if (!source.Content.Contains(candidate.ExpectedAnchor, StringComparison.Ordinal)) item = new(index, SourceEvidenceStatus.Mismatch);
                else item = new(index, SourceEvidenceStatus.Verified, new(issuer, expected, source.Path, candidate.StartLine, candidate.EndLine,
                    source.Content, clock.GetUtcNow(), source.RawDigest, source.Length, source.Written, source.Created));
            }
            catch (FileNotFoundException) { item = new(index, SourceEvidenceStatus.Missing); }
            catch (DirectoryNotFoundException) { item = new(index, SourceEvidenceStatus.Missing); }
            catch (ArgumentOutOfRangeException) { item = new(index, SourceEvidenceStatus.Mismatch); }
            catch (UnauthorizedAccessException) { item = new(index, SourceEvidenceStatus.InvalidCandidate); }
            catch (System.Text.DecoderFallbackException) { item = new(index, SourceEvidenceStatus.InvalidCandidate); }
            catch (ProjectTools.SourceChangedException) { item = new(index, SourceEvidenceStatus.Changed); }
            catch (IOException) { item = new(index, SourceEvidenceStatus.Unavailable); }
            items.Add(item);
            if (JsonSerializer.SerializeToUtf8Bytes(new SourceEvidenceBatch(items.ToImmutable())).Length > 16 * 1024)
                items[^1] = new(index, SourceEvidenceStatus.LimitExceeded);
        }
        return new(items.ToImmutable());
    }
    public bool IsCurrent(VerifiedSourceEvidence source)
    {
        if (!ReferenceEquals(source.Issuer, issuer) || source.Digest != SourceEvidenceDigest.Compute(source.RelativePath, source.StartLine, source.EndLine, source.Content)) return false;
        try
        {
            Interlocked.Increment(ref reads);
            var current = tools.ReadSourceSnapshot(source.RelativePath, source.StartLine, source.EndLine);
            return !current.Redacted && current.RawDigest == source.RawDigest && current.Length == source.Length
                && current.Written == source.Written && current.Created == source.Created && current.Content == source.Content;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public static SourceBinding Binding(TaskEvidence context) => new(context.Task.TaskId, context.Task.WorkflowRunId,
        context.ExternalResult.Handle.ExecutionId, context.ExternalResult.Handle.ExternalSessionId!, context.ExternalResult.Handle.ExternalRequestId!);
    static bool ValidBinding(SourceBinding b) => HandoffValidation.Text(b.TaskId, 100) && HandoffValidation.Text(b.WorkflowRunId, 100)
        && HandoffValidation.Text(b.ExecutionId, 160) && HandoffValidation.Text(b.SessionId, 160) && HandoffValidation.Text(b.MessageId, 160);
    static SourceEvidenceBatch Failure(SourceEvidenceStatus status) => new([new(0, status)]);
}
