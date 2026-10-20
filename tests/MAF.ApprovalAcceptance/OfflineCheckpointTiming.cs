using System.Diagnostics;
using System.Text.Json;
using MAF.Core;

namespace MAF.ApprovalAcceptance;

// Explicit per-test scope; no files, network, policy changes or global collection switch.
internal sealed class OfflineCheckpointTiming : IDisposable
{
    static readonly AsyncLocal<OfflineCheckpointTiming?> slot = new();
    internal static OfflineCheckpointTiming? Current => slot.Value;
    readonly OfflineCheckpointTiming? previous;
    readonly Action<string> output;
    readonly long started = Stopwatch.GetTimestamp();
    readonly object sync = new();
    readonly List<(string Point, long Tick)> points = [];
    string? status, code;
    bool reviewed;
    int create, prompt, get, abort;
    internal OfflineCheckpointTiming(Action<string> output)
    { this.output = output; previous = slot.Value; slot.Value = this; Mark("TestStart"); }
    internal void Mark(string point)
    {
        var tick = Stopwatch.GetTimestamp();
        lock (sync) { if (points.Count < 256) points.Add((point, tick)); }
    }
    internal void Observe(WorkflowOutcome outcome)
    { status = outcome.ExternalResult?.Status.ToString(); code = outcome.ExternalResult?.ErrorCode; Mark("HostOutcome"); }
    internal void Reviewer() { reviewed = true; Mark("ReviewerEntered"); }
    internal void Request(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get) get++;
        else if (request.RequestUri!.AbsolutePath == "/session") create++;
        else if (request.RequestUri.AbsolutePath.EndsWith("/prompt_async", StringComparison.Ordinal)) prompt++;
        else if (request.RequestUri.AbsolutePath.EndsWith("/abort", StringComparison.Ordinal)) abort++;
    }
    public void Dispose()
    {
        Mark("TestScopeEnd");
        slot.Value = previous;
        lock (sync)
        {
            var ordered = points.OrderBy(p => p.Tick).ToArray();
            var budget = ordered.FirstOrDefault(p => p.Point == "BudgetStart").Tick;
            var child = ordered.LastOrDefault(p => p.Point == "ChildWorkflowReturned").Tick;
            var last = started;
            var samples = ordered.Select(p =>
            {
                var delta = Stopwatch.GetElapsedTime(last, p.Tick).TotalMilliseconds; last = p.Tick;
                return new { point = p.Point, ms = Stopwatch.GetElapsedTime(started, p.Tick).TotalMilliseconds, deltaMs = delta,
                    budgetMs = budget == 0 ? (double?)null : Stopwatch.GetElapsedTime(budget, p.Tick).TotalMilliseconds };
            }).ToArray();
            output("CHECKPOINT_TIMING " + JsonSerializer.Serialize(new { status, code, reviewed, create, prompt, get, abort,
                budgetElapsedMs = budget == 0 || child == 0 ? (double?)null : Stopwatch.GetElapsedTime(budget, child).TotalMilliseconds,
                points = samples }));
        }
    }
}
