using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAF.Core;

public enum ExternalExecutionStatus
{
    Pending, Running, WaitingForApproval, Completed, Partial, Failed, Cancelled, Unknown
}

public enum ExternalExecutionPolicy
{
    ReadOnly
}

public sealed record ExternalTaskRequest(string TaskId, string ProjectRoot, string TaskText,
    TimeSpan Timeout, ExternalExecutionPolicy Policy = ExternalExecutionPolicy.ReadOnly);

public sealed record ExternalExecutionHandle(string ExecutionId, string Provider,
    string? ExternalSessionId = null, string? ExternalRequestId = null);

public sealed record ExternalObservation(ExternalExecutionStatus Status, string? Summary = null,
    string? ErrorCode = null, bool OutputTruncated = false)
{
    internal MAF.Core.Policy.ObservedTerminal? TerminalEvidence { get; init; }
}

public sealed record ExternalTraceEvent(string Kind, DateTimeOffset Timestamp);

public sealed record ExternalTaskResult(string TaskId, string WorkflowRunId, ExternalExecutionStatus Status,
    ExternalExecutionHandle Handle, string Summary, IReadOnlyList<string> Limitations,
    IReadOnlyList<ExternalTraceEvent> Trace, bool AbortRequested = false, string? ErrorCode = null,
    int Requests = 0, double DurationMilliseconds = 0)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public interface IExternalHarnessClient
{
    string Provider { get; }
    int GetRequestCount(string taskId);
    Task ValidateAsync(ExternalTaskRequest request, CancellationToken cancellationToken);
    Task<ExternalExecutionHandle> CreateAsync(ExternalTaskRequest request, string workflowRunId,
        ExternalExecutionHandle handle, CancellationToken cancellationToken);
    Task SubmitAsync(ExternalTaskRequest request, ExternalExecutionHandle handle, CancellationToken cancellationToken);
    Task<ExternalObservation> ObserveAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken);
    Task<bool> AbortAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken);
}

public sealed class ExternalHarnessException(string code, bool ambiguous = false, ExternalExecutionHandle? handle = null) : Exception(code)
{
    public string Code { get; } = code;
    public bool Ambiguous { get; } = ambiguous;
    public ExternalExecutionHandle? Handle { get; } = handle;
}
