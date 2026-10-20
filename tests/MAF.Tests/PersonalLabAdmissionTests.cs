using MAF.Core;
using MAF.Core.Policy;
using MAF.Host;

namespace MAF.Tests;

// No workspace allocation. Valid preparation reads metadata of the existing test output only.
public sealed class PersonalLabAdmissionTests
{
    internal static LocalExecutionInput Input(string? workspace = null) => new("Explain the personal fixture",
        workspace ?? Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), LocalExecutionProfile.PersonalLab,
        new(NativeModelRole.Pca, "pca-fixture-v1", new("https://pca.example.invalid/v1"), "fixture-pca", "pca-model"),
        new(NativeModelRole.Reviewer, "review-fixture-v1", new("https://review.example.invalid/v1"), "fixture-reviewer", "review-model"),
        new("harness-fixture-v1", "fixture-provider", "fixture-model"), TimeSpan.FromSeconds(2));
    internal static LabEffectRequest Request(LocalExecutionPreparation p, LabEffect effect = LabEffect.PcaInference)
        => new(p.OperationId, p.Task, "runtime-execution", p.Binding, p.EvidenceBudgets, effect, LabEffectTarget.ControlledOfflineFixture);
    internal sealed class Clock : TimeProvider
    {
        public TimeSpan Advance;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero) + Advance;
        public override long GetTimestamp() => Advance.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    [Fact]
    public void ExactAdmissionIsOfflineSingleUseAndNotProductionAuthority()
    {
        var p = LocalExecutionPreparation.Prepare(Input());
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", new Clock());
        Assert.Equal(PersonalLabState.Admitted, lab.State);
        Assert.Empty(lab.Observations); Assert.Equal(0, lab.ReservedEffects);
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, Request(p)));
        lab.BeginOffline();
        using (var attempt = PersonalLabAdmission.Reserve(lab, Request(p)))
        {
            Assert.Empty(lab.Observations); attempt.RecordReturned();
            Assert.Throws<ExternalHarnessException>(attempt.RecordReturned);
        }
        var observed = Assert.Single(lab.Observations);
        Assert.Equal(p.OperationId, observed.OperationId); Assert.True(observed.Returned);
        Assert.Contains("ControlledOfflineFixture / NotLiveProvider", observed.Provenance);
        Assert.Empty(typeof(PersonalLabAdmission).GetInterfaces()); // No trusted caller/policy/receipt interface.
        Assert.Equal("PersonalLabNotApproved", Assert.Throws<ExternalHarnessException>(p.DemandExecution).Code);
        lab.Consume(); Assert.Equal(PersonalLabState.Consumed, lab.State);
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, Request(p)));
        Assert.Throws<ExternalHarnessException>(lab.BeginOffline);
        Assert.Equal("LabAdmissionAlreadyIssued", Assert.Throws<ExternalHarnessException>(() =>
            PersonalLabAdmission.AdmitOffline(p, p.Binding, "other-execution", new Clock())).Code);
    }
    [Fact]
    public void UnpreparedUnadmittedOrProductionIntentCannotAdmitOrExecute()
    {
        var input = Input();
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.AdmitOffline(null, input, "execution", new Clock()));
        var p = LocalExecutionPreparation.Prepare(input);
        Assert.Equal("LabAdmissionRequired", Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(null, Request(p))).Code);
        Assert.Throws<ArgumentException>(() => PersonalLabAdmission.AdmitOffline(p, input with { Objective = "changed" }, "execution", new Clock()));
        Assert.Throws<ArgumentException>(() => LocalExecutionPreparation.Prepare(input with { HarnessRecipient = null }));
        var production = LocalExecutionPreparation.Prepare(input with { Profile = LocalExecutionProfile.ProductionProtected });
        Assert.Equal("LabProfileRequired", Assert.Throws<ExternalHarnessException>(() =>
            PersonalLabAdmission.AdmitOffline(production, production.Binding, "execution", new Clock())).Code);
        Assert.Equal("ProductionTrustUnavailable", Assert.Throws<ExternalHarnessException>(production.DemandExecution).Code);
        Assert.Null(UnavailableTrust.Instance.Resolve(new SyntheticCaller()));
    }
    sealed record SyntheticCaller : ITrustedCallerContext
    { public string SubjectId => "fixture"; public string AuthenticationContextId => "not-authenticated"; }
    [Theory]
    [InlineData("operation")] [InlineData("task")] [InlineData("run")] [InlineData("execution")]
    [InlineData("workspace")] [InlineData("objective")] [InlineData("timeout")] [InlineData("budgets")]
    [InlineData("provider")] [InlineData("pca")] [InlineData("reviewer")] [InlineData("harness-recipient")]
    [InlineData("profile")] [InlineData("access")]
    public void EveryEffectRequiresTheWholeExactPreparedOperation(string field)
    {
        var p = LocalExecutionPreparation.Prepare(Input()); var request = Request(p);
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", new Clock()); lab.BeginOffline();
        request = field switch
        {
            "operation" => request with { OperationId = "other" },
            "task" => request with { Task = p.Task with { TaskId = "other" } },
            "run" => request with { Task = p.Task with { WorkflowRunId = "other" } },
            "execution" => request with { ExecutionId = "other" },
            "workspace" => request with { Task = p.Task with { Workspace = "other" } },
            "objective" => request with { Task = p.Task with { Objective = "other" } },
            "timeout" => request with { Binding = p.Binding with { StageTimeout = TimeSpan.FromSeconds(1) } },
            "budgets" => request with { Budgets = p.EvidenceBudgets with { Sources = 16 } },
            "provider" => request with { Binding = p.Binding with { Harness = "other" } },
            "pca" => request with { Binding = p.Binding with { Pca = p.Binding.Pca! with { ConfigurationId = "other" } } },
            "reviewer" => request with { Binding = p.Binding with { Reviewer = p.Binding.Reviewer! with { Model = "other" } } },
            "harness-recipient" => request with { Binding = p.Binding with { HarnessRecipient = p.Binding.HarnessRecipient! with { Provider = "other" } } },
            "profile" => request with { Binding = p.Binding with { Profile = LocalExecutionProfile.ProductionProtected } },
            _ => request with { Binding = p.Binding with { Access = (ExternalExecutionPolicy)99 } }
        };
        Assert.Equal("LabEffectBindingMismatch", Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, request)).Code);
        Assert.Equal(0, lab.ReservedEffects); Assert.Empty(lab.Observations);
    }
    [Theory]
    [InlineData("expired")] [InlineData("revoked")] [InlineData("cancelled")] [InlineData("consumed")]
    [InlineData("rollback")]
    public void InvalidatedAdmissionCannotAuthorizeOrRenew(string state)
    {
        var p = LocalExecutionPreparation.Prepare(Input()); var clock = new Clock(); using var stop = new CancellationTokenSource();
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", clock, stop.Token); lab.BeginOffline();
        switch (state)
        {
            case "expired": clock.Advance = TimeSpan.FromSeconds(8); break;
            case "rollback": clock.Advance = TimeSpan.FromTicks(-1); break;
            case "revoked": lab.Revoke(); break;
            case "cancelled": stop.Cancel(); break;
            default: lab.Consume(); break;
        }
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, Request(p)));
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", clock));
        Assert.Empty(lab.Observations); Assert.Equal(0, lab.ReservedEffects);
    }
    [Theory]
    [InlineData(90)] [InlineData(91)] [InlineData(92)] [InlineData(93)] [InlineData(94)]
    public void WriteShellElevationPermissionReplyAndAbortAreNotSupported(int action)
    {
        var p = LocalExecutionPreparation.Prepare(Input());
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", new Clock()); lab.BeginOffline();
        Assert.Equal("LabEffectUnavailable", Assert.Throws<ExternalHarnessException>(() =>
            PersonalLabAdmission.Reserve(lab, Request(p) with { Effect = (LabEffect)action })).Code);
        Assert.Equal("LabEffectUnavailable", Assert.Throws<ExternalHarnessException>(() =>
            PersonalLabAdmission.Reserve(lab, Request(p) with { Target = LabEffectTarget.Network })).Code);
        Assert.Equal(0, lab.ReservedEffects);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AmbiguousPostNeverRefundsItsOneAttempt(bool prompt)
    {
        var p = LocalExecutionPreparation.Prepare(Input());
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", new Clock()); lab.BeginOffline();
        using (var create = PersonalLabAdmission.Reserve(lab, Request(p, LabEffect.CreateSession)))
            if (prompt) create.RecordReturned();
        if (prompt) PersonalLabAdmission.Reserve(lab, Request(p, LabEffect.SubmitPrompt)).Dispose();
        Assert.False(lab.Observations.Last().Returned);
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, Request(p, LabEffect.CreateSession)));
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, Request(p, LabEffect.SubmitPrompt)));
    }
    [Fact]
    public async Task ConcurrentCreateAndRepeatedBeginCannotCreateTwoAttempts()
    {
        var p = LocalExecutionPreparation.Prepare(Input());
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", new Clock()); lab.BeginOffline();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { PersonalLabAdmission.Reserve(lab, Request(p, LabEffect.CreateSession)).Dispose(); return true; }
            catch (ExternalHarnessException) { return false; }
        })));
        Assert.Single(results, x => x); Assert.Equal(1, lab.ReservedEffects);
        Assert.Throws<ExternalHarnessException>(lab.BeginOffline);
    }
    [Fact]
    public void ModelAndTotalEffectBudgetsAreBoundedAndCancelledReservationHasNoEffect()
    {
        var p = LocalExecutionPreparation.Prepare(Input());
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", new Clock()); lab.BeginOffline();
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => PersonalLabAdmission.Reserve(lab, Request(p), stop.Token));
        Assert.Equal(0, lab.ReservedEffects);
        foreach (var effect in new[] { LabEffect.PcaInference, LabEffect.ReviewerInference })
        {
            for (var i = 0; i < 8; i++) PersonalLabAdmission.Reserve(lab, Request(p, effect)).Dispose();
            Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, Request(p, effect)));
        }
        for (var i = lab.ReservedEffects; i < 128; i++) PersonalLabAdmission.Reserve(lab, Request(p, LabEffect.Observe)).Dispose();
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, Request(p, LabEffect.Observe)));
        Assert.Equal(128, lab.Observations.Length);
    }
    [Theory]
    [InlineData("--external-task")] [InlineData("--recover")] [InlineData("--recover-pending")]
    public async Task LabAdmissionCannotUnblockExistingProductionEntrypoints(string mode)
    {
        var p = LocalExecutionPreparation.Prepare(Input());
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", new Clock()); lab.BeginOffline();
        var denied = await Assert.ThrowsAsync<ExternalHarnessException>(() => ExternalConsole.RunAsync([mode], "unused",
            _ => throw new InvalidOperationException("UnexpectedConfigurationRead")));
        Assert.Equal("ProductionTrustUnavailable", denied.Code);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, LocalSoloConsole.Run(["--local", "submit"], output, error,
            _ => throw new InvalidOperationException("UnexpectedConfigurationRead")));
        Assert.Equal(2, LocalSoloConsole.Run(["--local", "personal-run"], output, error));
        Assert.Equal(0, lab.ReservedEffects); Assert.Empty(lab.Observations);
    }
    [Fact]
    public void ObservationAfterRevocationRecordsHistoryWithoutRestoringPermission()
    {
        var p = LocalExecutionPreparation.Prepare(Input());
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", new Clock()); lab.BeginOffline();
        using var inFlight = PersonalLabAdmission.Reserve(lab, Request(p, LabEffect.CreateSession));
        lab.Revoke(); inFlight.RecordReturned();
        Assert.True(Assert.Single(lab.Observations).Returned);
        Assert.Equal(PersonalLabState.Revoked, lab.State);
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, Request(p, LabEffect.SubmitPrompt)));
    }
}
