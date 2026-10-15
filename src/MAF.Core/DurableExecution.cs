using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MAF.Core;

public enum DurableExecutionState
{
    Created, SessionCreating, ReadyToSubmit, SubmissionIntended, SubmissionUncertain,
    Running, WaitingForApproval, Completed, Partial, Failed, Cancelled, Unknown
}

public enum SubmissionPhase { None, SessionCreationIntended, SessionKnown, PromptIntended, Acknowledged }

// Deliberately excludes prompt, output, credentials and provider transcripts.
public sealed record DurableExecution(
    string TaskId, string RequestFingerprint, string ProviderScope, string ProjectRoot,
    string WorkflowRunId, ExternalExecutionHandle Handle, DurableExecutionState State,
    SubmissionPhase SubmissionPhase, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    long Version = 0, DateTimeOffset? CompletedAt = null, DateTimeOffset? ReconciledAt = null,
    string? ErrorCode = null, bool AbortRequested = false)
{
    public bool IsTerminal => State is DurableExecutionState.Completed or DurableExecutionState.Partial
        or DurableExecutionState.Failed or DurableExecutionState.Cancelled;

    public static string Fingerprint(ExternalTaskRequest request) => Hash(JsonSerializer.Serialize(new
    {
        Root = NormalizeRoot(request.ProjectRoot), request.TaskText, TimeoutTicks = request.Timeout.Ticks, request.Policy
    }));

    public static string NormalizeRoot(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class DurableExecutionTransitions
{
    public static void Validate(DurableExecution before, DurableExecution after)
    {
        if (before.IsTerminal || after.TaskId != before.TaskId || after.RequestFingerprint != before.RequestFingerprint
            || after.ProviderScope != before.ProviderScope || after.ProjectRoot != before.ProjectRoot
            || after.WorkflowRunId != before.WorkflowRunId || after.CreatedAt != before.CreatedAt
            || after.Handle.ExecutionId != before.Handle.ExecutionId || after.Handle.Provider != before.Handle.Provider
            || after.Handle.ExternalRequestId != before.Handle.ExternalRequestId
            || (before.Handle.ExternalSessionId is not null && before.Handle.ExternalSessionId != after.Handle.ExternalSessionId)
            || (before.Handle.ExternalSessionId is null && after.Handle.ExternalSessionId is not null
                && after.SubmissionPhase != SubmissionPhase.SessionKnown)
            || after.Version != before.Version + 1 || after.UpdatedAt < before.UpdatedAt
            || after.SubmissionPhase < before.SubmissionPhase || (before.AbortRequested && !after.AbortRequested))
            throw new InvalidOperationException("Invalid durable identity, version or terminal transition.");

        var valid = after.State == DurableExecutionState.Unknown || before.State == after.State || (before.State switch
        {
            DurableExecutionState.Created => after.State is DurableExecutionState.SessionCreating or DurableExecutionState.Failed,
            DurableExecutionState.SessionCreating => after.State is DurableExecutionState.ReadyToSubmit or DurableExecutionState.Failed,
            DurableExecutionState.ReadyToSubmit => after.State == DurableExecutionState.SubmissionIntended,
            DurableExecutionState.SubmissionIntended => after.State is DurableExecutionState.SubmissionUncertain or DurableExecutionState.Running
                or DurableExecutionState.WaitingForApproval or DurableExecutionState.Completed or DurableExecutionState.Partial
                or DurableExecutionState.Failed or DurableExecutionState.Cancelled,
            DurableExecutionState.SubmissionUncertain or DurableExecutionState.Running or DurableExecutionState.WaitingForApproval
                or DurableExecutionState.Unknown => after.State is DurableExecutionState.Running or DurableExecutionState.WaitingForApproval
                    or DurableExecutionState.Completed or DurableExecutionState.Partial or DurableExecutionState.Failed or DurableExecutionState.Cancelled,
            _ => false
        });
        if (!valid || !Enum.IsDefined(after.State) || !Enum.IsDefined(after.SubmissionPhase)
            || (after.IsTerminal != after.CompletedAt.HasValue)
            || (after.SubmissionPhase >= SubmissionPhase.SessionKnown && string.IsNullOrEmpty(after.Handle.ExternalSessionId))
            || ((after.State is DurableExecutionState.Running or DurableExecutionState.WaitingForApproval or DurableExecutionState.Completed
                or DurableExecutionState.Partial or DurableExecutionState.Cancelled) && after.SubmissionPhase < SubmissionPhase.PromptIntended)
            || (after.ErrorCode is not null && (after.ErrorCode.Length > 80 || after.ErrorCode.Any(c => !char.IsAsciiLetterOrDigit(c)))))
            throw new InvalidOperationException("Invalid durable state transition or diagnostic code.");
        if (after.SubmissionPhase != before.SubmissionPhase)
        {
            var phaseValid = (before.SubmissionPhase, after.SubmissionPhase, after.State) switch
            {
                (SubmissionPhase.None, SubmissionPhase.SessionCreationIntended, DurableExecutionState.SessionCreating) => true,
                (SubmissionPhase.SessionCreationIntended, SubmissionPhase.SessionKnown, DurableExecutionState.ReadyToSubmit or DurableExecutionState.Unknown) => true,
                (SubmissionPhase.SessionKnown, SubmissionPhase.PromptIntended, DurableExecutionState.SubmissionIntended) => true,
                (SubmissionPhase.PromptIntended, SubmissionPhase.Acknowledged, DurableExecutionState.Running) => true,
                _ => false
            };
            if (!phaseValid) throw new InvalidOperationException("Invalid submission phase transition.");
        }
    }
}

public interface IExecutionOwnership : IAsyncDisposable { string TaskId { get; } }

public interface IExecutionStore
{
    ValueTask<IExecutionOwnership?> TryAcquireAsync(string taskId, CancellationToken cancellationToken = default);
    Task<DurableExecution?> GetAsync(string taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken cancellationToken = default);
    Task CreateAsync(DurableExecution execution, IExecutionOwnership ownership, CancellationToken cancellationToken = default);
    Task UpdateAsync(DurableExecution expected, DurableExecution next, IExecutionOwnership ownership, CancellationToken cancellationToken = default);
}

public interface IResumableExternalHarnessClient : IExternalHarnessClient
{
    string Scope { get; }
    ExternalExecutionHandle PrepareExecution();
    Task ReattachAsync(string taskId, string projectRoot, ExternalExecutionHandle handle, CancellationToken cancellationToken);
}
