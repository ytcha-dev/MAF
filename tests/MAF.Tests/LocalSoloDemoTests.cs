using MAF.Core;
using MAF.Host;

namespace MAF.Tests;

// No new directories, files, processes, sockets, providers, secret configuration or real models.
// CLI cases inspect only existing AppContext directory metadata. All authored tests: NotRun.
public sealed class LocalSoloDemoTests
{
    static async Task<(int Exit, string Output, string Error)> Run(string scenario, string input)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        using var reader = new StringReader(input);
        var exit = await LocalSoloConsole.RunAsync(["--local", "demo", "--workspace", AppContext.BaseDirectory,
            "--objective", "Explain the read-only task", "--scenario", scenario], output, error, _ => null, reader);
        return (exit, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes\n")]
    [InlineData("quit\n")]
    public async Task OnlyExplicitOfflineConfirmationCanCreateTask(string input)
    {
        var result = await Run("completed", input);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        Assert.Contains("Cancelled / NotExecuted", result.Output);
        Assert.DoesNotContain("Task created once:", result.Output);
        Assert.DoesNotContain("Current stage:", result.Output);
    }

    [Fact]
    public async Task OneTaskAutomaticallyPassesHarnessResultToReviewerWithoutReexecutionOnQueries()
    {
        var result = await Run("completed", "run-demo\nstatus\nresult\nstatus\nquit\n");
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        Assert.Equal(1, result.Output.Split("Task created once:").Length - 1);
        Assert.Equal(1, result.Output.Split("Current stage: PCA").Length - 1);
        Assert.Equal(1, result.Output.Split("Current stage: Harness").Length - 1);
        Assert.Equal(1, result.Output.Split("Current stage: Reviewer").Length - 1);
        Assert.Contains("original typed task and Harness result received automatically", result.Output);
        Assert.Contains("Status: Completed", result.Output);
        Assert.Contains("InsufficientEvidence", result.Output);
        Assert.Contains("NotLiveProvider", result.Output);
        Assert.Contains("Models: none", result.Output);
        Assert.Contains("provenance: NotEstablished", result.Output);
        Assert.DoesNotContain("Human result choice: Accepted", result.Output);
    }

    [Theory]
    [InlineData("failed", ExternalExecutionStatus.Failed)]
    [InlineData("unknown", ExternalExecutionStatus.Unknown)]
    [InlineData("waiting", ExternalExecutionStatus.WaitingForApproval)]
    public async Task NoncompletedOutcomesRemainDistinctAndNeverInvokeReviewer(string scenario, ExternalExecutionStatus status)
    {
        var result = await Run(scenario, "run-demo\nstatus\nresult\nquit\n");
        Assert.Equal(0, result.Exit);
        Assert.Contains($"Status: {status}", result.Output);
        Assert.DoesNotContain("Current stage: Reviewer", result.Output);
        Assert.DoesNotContain("Observed status: Completed", result.Output);
        if (status == ExternalExecutionStatus.Unknown) Assert.Contains("no automatic resubmission", result.Output);
        if (status == ExternalExecutionStatus.WaitingForApproval) Assert.Contains("no permission reply or automatic approval", result.Output);
    }

    [Fact]
    public async Task LiveRequestAndApprovalLikeCommandsNeverDispatchOrChangeOfflineStatus()
    {
        var result = await Run("waiting", "run-demo\nsubmit-live\napprove\nretry\nstatus\nquit\n");
        Assert.Equal(0, result.Exit);
        Assert.Contains("Live submission: Blocked / ProductionTrustUnavailable", result.Error);
        Assert.Contains("Unknown command", result.Output);
        Assert.Contains("Status: WaitingForApproval", result.Output);
        Assert.Equal(1, result.Output.Split("Current stage: Harness").Length - 1);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("v2")]
    public async Task InvalidScenarioFailsBeforeConfigurationOrInput(string scenario)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await LocalSoloConsole.RunAsync(["--local", "demo", "--workspace", AppContext.BaseDirectory,
            "--objective", "task", "--scenario", scenario], output, error,
            _ => throw new InvalidOperationException("Unexpected configuration read"), new StringReader("run-demo\n"));
        Assert.Equal(2, exit); Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task ExistingWorkflowRetainsExactInputIdentityAndReviewerCorrelation()
    {
        var task = new HandoffTask("demo-task", "demo-run", AppContext.BaseDirectory, "Read-only objective", TimeSpan.FromSeconds(5));
        using var output = new StringWriter();
        var outcome = await LocalSoloDemo.ExecuteAsync(task, "completed", output);
        Assert.Equal(task.TaskId, outcome.TaskId); Assert.Equal(task.WorkflowRunId, outcome.WorkflowRunId);
        Assert.Equal(task.TaskId, outcome.Review!.TaskId);
        Assert.Equal(outcome.ExternalResult!.Handle.ExecutionId, outcome.Review.ExecutionId);
        Assert.Equal("OfflineDemo", outcome.ExternalResult.Handle.Provider);
        Assert.Equal(HumanGateStatus.NeedsHumanReview, outcome.HumanGate);
        Assert.Equal(new[] { "PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate" }, outcome.Stages.Select(s => s.Name));
        Assert.Empty(LocalSoloOutcomeMapper.FromControlledWorkflow(outcome).Evidence);
    }
}
