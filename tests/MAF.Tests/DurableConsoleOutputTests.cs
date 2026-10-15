using System.Text.Json;
using MAF.Core;
using MAF.Host;
using MAF.Workflows.External;

namespace MAF.Tests;

// Pure formatting tests, authored but not executed under the current security hold.
public sealed class DurableConsoleOutputTests
{
    private static ExternalWorkflowRun Run(ExternalExecutionStatus status) => new(new("task", "workflow", status,
        new("execution", "OpenCode", "ses_original", "msg_original"), "中文結果", [], []), []);

    [Fact]
    public void RecoveryBatchIsOneJsonArrayAndDoesNotChangeSharedSerializer()
    {
        var sharedEncoder = ExternalTaskResult.JsonOptions.Encoder;
        using var output = new StringWriter();
        ExternalConsoleOutput.WriteMany(output, [Run(ExternalExecutionStatus.Completed), Run(ExternalExecutionStatus.Unknown)], ExternalOutputFormat.Json);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Equal("Completed", json.RootElement[0].GetProperty("result").GetProperty("status").GetString());
        Assert.Equal("ses_original", json.RootElement[1].GetProperty("result").GetProperty("handle").GetProperty("externalSessionId").GetString());
        Assert.Contains("中文結果", output.ToString());
        Assert.Same(sharedEncoder, ExternalTaskResult.JsonOptions.Encoder);
    }

    [Fact]
    public void EmptyRecoveryBatchHasValidJson()
    {
        using var output = new StringWriter();
        ExternalConsoleOutput.WriteMany(output, [], ExternalOutputFormat.Json);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, json.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("verbose")]
    public void UnknownRecoveryKeepsOriginalIdsInReadableOutput(string format)
    {
        using var output = new StringWriter();
        ExternalConsoleOutput.WriteMany(output, [Run(ExternalExecutionStatus.Unknown)], Enum.Parse<ExternalOutputFormat>(format, ignoreCase: true));
        Assert.Contains("Unknown", output.ToString());
        Assert.Contains("ses_original", output.ToString());
        Assert.Contains("msg_original", output.ToString());
    }

    [Fact]
    public void HostRejectsTempAndRelativePersistentDatabasePaths()
    {
        Assert.Throws<ArgumentException>(() => DurableDatabasePath.Validate("relative.db", Directory.GetCurrentDirectory()));
        Assert.Throws<ArgumentException>(() => DurableDatabasePath.Validate(Path.Combine(Path.GetTempPath(), "execution.db"), Directory.GetCurrentDirectory()));
        Assert.Throws<ArgumentException>(() => DurableDatabasePath.Validate(Path.Combine(Directory.GetCurrentDirectory(), "execution.db"), Directory.GetCurrentDirectory()));
    }
}
