using MAF.Core;

namespace MAF.Tests;

// Pure record tests, still not executed while the test workflow is paused.
public sealed class DurableExecutionStateTests
{
    private static DurableExecution Initial()
    {
        var now = DateTimeOffset.UtcNow;
        return new("task", DurableExecution.Hash("request"), DurableExecution.Hash("scope"), "root", "workflow",
            new("execution", "Fake", ExternalRequestId: "msg_stable"), DurableExecutionState.Created,
            SubmissionPhase.None, now, now);
    }

    [Fact]
    public void SubmissionCannotSkipPersistedSessionAndIntent()
    {
        var before = Initial();
        Assert.Throws<InvalidOperationException>(() => DurableExecutionTransitions.Validate(before,
            before with { State = DurableExecutionState.Running, Version = 1, SubmissionPhase = SubmissionPhase.Acknowledged }));
        Assert.Throws<InvalidOperationException>(() => DurableExecutionTransitions.Validate(before,
            before with { State = DurableExecutionState.Created, Version = 1, SubmissionPhase = SubmissionPhase.PromptIntended,
                Handle = before.Handle with { ExternalSessionId = "ses_fake" } }));
    }

    [Fact]
    public void TerminalOutcomeCannotBeReplaced()
    {
        var terminal = Initial() with { State = DurableExecutionState.Completed, CompletedAt = DateTimeOffset.UtcNow };
        Assert.Throws<InvalidOperationException>(() => DurableExecutionTransitions.Validate(terminal,
            terminal with { State = DurableExecutionState.Unknown, CompletedAt = null, Version = 1 }));
    }

    [Fact]
    public void CorrelationCannotBeReboundAndDiagnosticsCannotContainRawText()
    {
        var before = Initial() with { State = DurableExecutionState.Running, SubmissionPhase = SubmissionPhase.Acknowledged,
            Handle = Initial().Handle with { ExternalSessionId = "ses_original" } };
        Assert.Throws<InvalidOperationException>(() => DurableExecutionTransitions.Validate(before,
            before with { Version = 1, Handle = before.Handle with { ExternalSessionId = "ses_other" } }));
        Assert.Throws<InvalidOperationException>(() => DurableExecutionTransitions.Validate(before,
            before with { Version = 1, ErrorCode = "secret=value" }));
    }
}
