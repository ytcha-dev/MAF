using System.Diagnostics;
using MAF.Core;
using MAF.Core.Tools;
using Microsoft.Agents.AI.Workflows;

namespace MAF.Workflows.External;

public sealed class OpenCodeExecutor(IExternalHarnessClient client, string workflowRunId, TimeSpan pollInterval,
    CancellationToken ownerCancellation = default)
    : Executor<ExternalTaskRequest, ExternalTaskResult>("OpenCodeExecutor")
{
    public override async ValueTask<ExternalTaskResult> HandleAsync(ExternalTaskRequest request,
        IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var handle = new ExternalExecutionHandle(Guid.NewGuid().ToString("N"), client.Provider);
        var trace = new List<ExternalTraceEvent>();
        var limitations = new List<string>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownerCancellation);
        budget.CancelAfter(request.Timeout);
        var submitted = false;
        var created = false;

        void Record(string kind) => trace.Add(new(kind, DateTimeOffset.UtcNow));
        ExternalTaskResult Result(ExternalExecutionStatus status, string summary, string? errorCode = null, bool abort = false)
        {
            Record(status.ToString());
            return new(request.TaskId, workflowRunId, status, handle, ProjectTools.Redact(summary),
                limitations.Distinct().ToArray(), trace.ToArray(), abort, errorCode,
                client.GetRequestCount(request.TaskId), watch.Elapsed.TotalMilliseconds);
        }

        try
        {
            await client.ValidateAsync(request, budget.Token);
            Record("PolicyVerified");
            handle = await client.CreateAsync(request, workflowRunId, handle, budget.Token);
            created = true;
            Record("SessionCreated");
            // Mark before calling: loss of an acknowledgment cannot authorize another submission.
            submitted = true;
            try
            {
                await client.SubmitAsync(request, handle, budget.Token);
                Record("Submitted");
            }
            catch (ExternalHarnessException error) when (error.Ambiguous)
            {
                Record("SubmissionUncertain");
                limitations.Add("Submission acknowledgment was lost; original session was reconciled without resubmitting.");
            }

            var readFailures = 0;
            ExternalExecutionStatus? previous = null;
            for (var poll = 0; poll < 200; poll++)
            {
                budget.Token.ThrowIfCancellationRequested();
                ExternalObservation observation;
                try
                {
                    observation = await client.ObserveAsync(handle, budget.Token);
                    readFailures = 0;
                }
                catch (ExternalHarnessException error) when (error.Ambiguous && readFailures++ < 2)
                {
                    Record("ReconnectOriginalSession");
                    await Task.Delay(pollInterval, budget.Token);
                    continue;
                }
                if (previous != observation.Status)
                {
                    Record(observation.Status.ToString());
                    previous = observation.Status;
                }
                if (observation.Status is ExternalExecutionStatus.Pending or ExternalExecutionStatus.Running)
                {
                    await Task.Delay(pollInterval, budget.Token);
                    continue;
                }
                if (observation.OutputTruncated)
                {
                    limitations.Add("Provider messages or output exceeded the collection limit.");
                }
                if (observation.Status == ExternalExecutionStatus.WaitingForApproval)
                {
                    limitations.Add("Remote permission or user input is required. Nothing was approved automatically.");
                }
                if (observation.Status == ExternalExecutionStatus.Unknown)
                {
                    limitations.Add("Remote completion is unconfirmed; inspect the existing external session before taking further action.");
                }
                return Result(observation.Status, observation.Summary ?? "External execution state observed.", observation.ErrorCode);
            }
            throw new OperationCanceledException("Observation limit reached.");
        }
        catch (OperationCanceledException)
        {
            limitations.Add("Cancellation or time/observation budget expired; remote work may still be active.");
            if (!created)
            {
                return Result(ExternalExecutionStatus.Unknown, "Execution interrupted before session identity was confirmed.", "Interrupted");
            }
            Record("AbortRequested");
            using var abortBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var acknowledged = await client.AbortAsync(handle, abortBudget.Token);
                Record(acknowledged ? "AbortAcknowledged" : "AbortNotAcknowledged");
                var final = await client.ObserveAsync(handle, abortBudget.Token);
                if (final.Status is ExternalExecutionStatus.Cancelled or ExternalExecutionStatus.Completed or ExternalExecutionStatus.Failed)
                {
                    return Result(final.Status, final.Summary ?? "Remote terminal state confirmed after abort request.", final.ErrorCode, true);
                }
            }
            catch (Exception error) when (error is ExternalHarnessException or OperationCanceledException)
            {
                Record("AbortUnconfirmed");
            }
            return Result(ExternalExecutionStatus.Unknown, "Abort was requested; remote cancellation is not confirmed.", "AbortUnconfirmed", true);
        }
        catch (ExternalHarnessException error)
        {
            if (error.Handle is not null)
            {
                handle = error.Handle;
            }
            var status = error.Ambiguous || submitted ? ExternalExecutionStatus.Unknown : ExternalExecutionStatus.Failed;
            limitations.Add("Provider operation failed; no automatic POST retry or permission escalation was performed.");
            return Result(status, "External harness request could not complete safely.", error.Code);
        }
    }
}
