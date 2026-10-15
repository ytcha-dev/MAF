using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAF.Core;
using MAF.Core.Tools;
using MAF.Host;
using MAF.Workflows.External;

namespace MAF.Tests;

public sealed class ExternalConsoleOutputTests
{
    private static ExternalWorkflowRun Run(ExternalExecutionStatus status = ExternalExecutionStatus.Completed,
        string? summary = "## 中文摘要\n\n第一段。\n\n第二段：\"引號\"、路徑 C:\\fixture\t結束\u0001") => new(
        new("task-1", "workflow-1", status, new("execution-1", "OpenCode"), summary!,
            ["Read-only; no edits were performed."], [new("Submitted", DateTimeOffset.Parse("2026-10-15T00:00:00Z"))],
            ErrorCode: status == ExternalExecutionStatus.Completed ? null : "SafeProviderError", Requests: 12, DurationMilliseconds: 1234.5),
        ["ExecutorInvokedEvent", "WorkflowOutputEvent"]);

    private static string Render(ExternalWorkflowRun run, ExternalOutputFormat format)
    {
        using var writer = new StringWriter();
        ExternalConsoleOutput.Write(writer, run, format);
        return writer.ToString();
    }

    [Fact]
    public void DefaultJsonPreservesSchemaAndSharedOptionsWhileShowingUtf8Chinese()
    {
        var run = Run();
        var originalEncoder = ExternalTaskResult.JsonOptions.Encoder;
        var originalConverters = ExternalTaskResult.JsonOptions.Converters.ToArray();
        var original = JsonSerializer.Serialize(run, ExternalTaskResult.JsonOptions);
        var format = ExternalConsoleOutput.Parse([]);
        var output = Render(run, format);
        Assert.Equal(ExternalOutputFormat.Json, format);
        Assert.Equal(output, Render(run, ExternalConsoleOutput.Parse(["--output", "json"])));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(output)));
        var utf8 = Encoding.UTF8.GetBytes(output);
        Assert.Contains("中文摘要", Encoding.UTF8.GetString(utf8));
        Assert.DoesNotContain("\\u4E2D", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\n", output);
        Assert.Contains("\\\"", output);
        Assert.Contains("C:\\\\fixture", output);
        Assert.Contains("\\t", output);
        Assert.Contains("\\u0001", output);
        using var json = JsonDocument.Parse(output); // Rejects banners or a second document.
        var result = json.RootElement.GetProperty("result");
        Assert.Equal(run.Result.Summary, result.GetProperty("summary").GetString());
        Assert.Equal("Completed", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("handle").GetProperty("externalSessionId").ValueKind);
        Assert.Equal("WorkflowOutputEvent", json.RootElement.GetProperty("workflowEvents")[1].GetString());
        Assert.Same(originalEncoder, ExternalTaskResult.JsonOptions.Encoder);
        Assert.Equal(originalConverters, ExternalTaskResult.JsonOptions.Converters.ToArray());
        Assert.Equal(original, JsonSerializer.Serialize(run, ExternalTaskResult.JsonOptions));
    }

    [Theory]
    [InlineData("JSON", 0)]
    [InlineData("Text", 1)]
    [InlineData("verbose", 2)]
    public void FormatValuesAreCaseInsensitive(string value, int expected)
        => Assert.Equal((ExternalOutputFormat)expected, ExternalConsoleOutput.Parse(["--output", value]));

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new[] { "--output" },
        new[] { "--output", "--task-id", "task" },
        new[] { "--output", "xml" },
        new[] { "--output", "0" },
        new[] { "--output", "" },
        new[] { "--output", "json", "--output", "text" }
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task InvalidFormatIsRejectedBeforeConfigurationOrProviderCreation(string[] args)
    {
        var optionRead = false;
        var error = await Assert.ThrowsAsync<ArgumentException>(() => ExternalConsole.RunAsync(args, "unused",
            _ => { optionRead = true; throw new InvalidOperationException("Should not read task/configuration."); }));
        Assert.Contains("--output", error.Message);
        Assert.Contains("json", error.Message);
        Assert.False(optionRead);
    }

    [Fact]
    public void TextPreservesParagraphsAndMaterialLimitations()
    {
        var run = Run(summary: "## 中文摘要\n\n第一段。\n\n第二段。");
        var text = Render(run, ExternalOutputFormat.Text);
        Assert.StartsWith(run.Result.Summary, text);
        Assert.DoesNotContain("\\n", text);
        Assert.DoesNotContain("\"result\"", text);
        Assert.Contains(run.Result.Limitations[0], text);
    }

    [Theory]
    [InlineData(ExternalExecutionStatus.Failed)]
    [InlineData(ExternalExecutionStatus.Unknown)]
    [InlineData(ExternalExecutionStatus.WaitingForApproval)]
    [InlineData(ExternalExecutionStatus.Partial)]
    [InlineData(ExternalExecutionStatus.Cancelled)]
    public void TextRetainsNonSuccessStatusWarningsAndIdentifiers(ExternalExecutionStatus status)
    {
        var text = Render(Run(status), ExternalOutputFormat.Text);
        Assert.Contains("Status: " + status, text);
        Assert.Contains("Task ID: task-1", text);
        Assert.Contains("Workflow run ID: workflow-1", text);
        Assert.Contains("External session ID: (unavailable)", text);
        Assert.Contains("External request ID: (unavailable)", text);
        Assert.Contains("SafeProviderError", text);
        Assert.Contains("Read-only; no edits were performed.", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingSummaryStillReportsStatus(string? summary)
    {
        var text = Render(Run(summary: summary), ExternalOutputFormat.Text);
        Assert.Contains("No summary available.", text);
        Assert.Contains("Status: Completed", text);
    }

    [Fact]
    public void VerboseShowsAllFinalDiagnosticsWithoutUnmaskingSanitizedSummary()
    {
        var run = Run(ExternalExecutionStatus.Unknown, ProjectTools.Redact("api_key=demo-sensitive-value"));
        run = run with
        {
            Result = run.Result with
            {
                Handle = new("execution-1", "OpenCode", "ses_fixture", "msg_fixture"),
                AbortRequested = true
            }
        };
        var text = Render(run, ExternalOutputFormat.Verbose);
        foreach (var expected in new[] { "Unknown", "task-1", "workflow-1", "execution-1", "OpenCode",
            "ses_fixture", "msg_fixture", "1234.5", "HTTP requests: 12", "SafeProviderError", "Abort requested: True",
            "Read-only", "2026-10-15T00:00:00", "Submitted", "WorkflowOutputEvent" })
        {
            Assert.Contains(expected, text);
        }
        Assert.DoesNotContain("demo-sensitive-value", text);
    }

    [Fact]
    public void VerboseMarksUnavailableIdsAndEmptyDiagnosticLists()
    {
        var run = Run();
        run = run with { Result = run.Result with { Trace = [], Limitations = [] }, WorkflowEvents = [] };
        var text = Render(run, ExternalOutputFormat.Verbose);
        Assert.Contains("External session ID: (unavailable)", text);
        Assert.Contains("Error code: (unavailable)", text);
        Assert.Contains("Limitations: (none)", text);
        Assert.Contains("Trace:", text);
        Assert.Contains("Workflow events:", text);
    }

    [Theory]
    [InlineData(ExternalExecutionStatus.Completed, 0)]
    [InlineData(ExternalExecutionStatus.Partial, 3)]
    [InlineData(ExternalExecutionStatus.Unknown, 4)]
    [InlineData(ExternalExecutionStatus.WaitingForApproval, 5)]
    [InlineData(ExternalExecutionStatus.Cancelled, 130)]
    [InlineData(ExternalExecutionStatus.Failed, 1)]
    [InlineData(ExternalExecutionStatus.Pending, 1)]
    [InlineData(ExternalExecutionStatus.Running, 1)]
    public void ExistingExitCodesArePreserved(ExternalExecutionStatus status, int expected)
        => Assert.Equal(expected, ExternalConsole.ExitCode(status));
}
