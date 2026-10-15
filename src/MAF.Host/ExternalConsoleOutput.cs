using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using MAF.Core;
using MAF.Workflows.External;

namespace MAF.Host;

internal enum ExternalOutputFormat { Json, Text, Verbose }

internal static class ExternalConsoleOutput
{
    // Console JSON only; never reuse this encoder for HTML or provider requests.
    private static readonly JsonSerializerOptions JsonOptions = new(ExternalTaskResult.JsonOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static ExternalOutputFormat Parse(string[] args)
    {
        var index = Array.IndexOf(args, "--output");
        if (index < 0)
        {
            return ExternalOutputFormat.Json;
        }
        if (Array.LastIndexOf(args, "--output") != index || index + 1 >= args.Length)
        {
            throw new ArgumentException("Specify --output once with json, text, or verbose (default: json).");
        }
        return args[index + 1].ToLowerInvariant() switch
        {
            "json" => ExternalOutputFormat.Json,
            "text" => ExternalOutputFormat.Text,
            "verbose" => ExternalOutputFormat.Verbose,
            _ => throw new ArgumentException("Invalid --output value; expected json, text, or verbose (default: json).")
        };
    }

    public static void Write(TextWriter output, ExternalWorkflowRun run, ExternalOutputFormat format)
    {
        if (format == ExternalOutputFormat.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(run, JsonOptions));
            return;
        }
        var result = run.Result;
        var hasSummary = !string.IsNullOrWhiteSpace(result.Summary);
        output.WriteLine(hasSummary ? result.Summary : "No summary available.");
        var diagnostics = format == ExternalOutputFormat.Verbose || !hasSummary
            || result.Status != ExternalExecutionStatus.Completed || result.ErrorCode is not null || result.AbortRequested;
        if (diagnostics)
        {
            output.WriteLine();
            output.WriteLine($"Status: {result.Status}");
            output.WriteLine($"Task ID: {result.TaskId}");
            output.WriteLine($"Workflow run ID: {result.WorkflowRunId}");
            output.WriteLine($"External session ID: {Available(result.Handle.ExternalSessionId)}");
            output.WriteLine($"External request ID: {Available(result.Handle.ExternalRequestId)}");
            output.WriteLine($"Error code: {Available(result.ErrorCode)}");
            output.WriteLine($"Abort requested: {result.AbortRequested}");
        }
        if (result.Limitations.Count > 0)
        {
            output.WriteLine("Limitations:");
            foreach (var limitation in result.Limitations)
            {
                output.WriteLine($"- {limitation}");
            }
        }
        if (format == ExternalOutputFormat.Verbose)
        {
            output.WriteLine($"Provider: {result.Handle.Provider}");
            output.WriteLine($"Execution ID: {result.Handle.ExecutionId}");
            output.WriteLine($"Duration (ms): {result.DurationMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}");
            output.WriteLine($"HTTP requests: {result.Requests}");
            if (result.Limitations.Count == 0)
            {
                output.WriteLine("Limitations: (none)");
            }
            output.WriteLine("Trace:");
            if (result.Trace.Count == 0)
            {
                output.WriteLine("- (none)");
            }
            foreach (var entry in result.Trace)
            {
                output.WriteLine($"- {entry.Timestamp:O} {entry.Kind}");
            }
            output.WriteLine("Workflow events:");
            if (run.WorkflowEvents.Count == 0)
            {
                output.WriteLine("- (none)");
            }
            foreach (var entry in run.WorkflowEvents)
            {
                output.WriteLine($"- {entry}");
            }
        }
    }

    private static string Available(string? value) => string.IsNullOrWhiteSpace(value) ? "(unavailable)" : value;
}
