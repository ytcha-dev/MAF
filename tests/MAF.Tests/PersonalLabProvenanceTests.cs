using MAF.Core;
using MAF.Host;

namespace MAF.Tests;

public sealed class PersonalLabProvenanceTests
{
    internal sealed class Owned : IDisposable
    {
        public DirectoryInfo Root { get; }
        public DirectoryInfo Workspace { get; }
        public LocalExecutionPreparation Preparation { get; }
        public PersonalLabAdmission Admission { get; }
        public ExternalExecutionHandle Handle { get; } = new("runtime-execution", "OpenCode", ExternalRequestId: "msg_runtime");
        // Explicitly invoked only by file-backed tests, never xUnit construction.
        internal Owned(TimeProvider? clock = null, bool sharedNativeRecipient = false)
        {
            Root = Directory.CreateTempSubdirectory("maf-lab-provenance-");
            Workspace = Directory.CreateDirectory(Path.Combine(Root.FullName, "workspace"));
            var input = PersonalLabAdmissionTests.Input(Workspace.FullName);
            input = input with { StageTimeout = TimeSpan.FromSeconds(5),
                Pca = input.Pca! with { Provider = "openai" }, Reviewer = input.Reviewer! with { Provider = "openai" } };
            if (sharedNativeRecipient) input = input with { Reviewer = input.Reviewer! with { Endpoint = input.Pca!.Endpoint, Model = input.Pca.Model } };
            Preparation = LocalExecutionPreparation.Prepare(input);
            Admission = PersonalLabAdmission.AdmitOffline(Preparation, Preparation.Binding, Handle.ExecutionId, clock ?? TimeProvider.System);
        }
        public PersonalLabOfflineOperation Begin() => PersonalLabOfflineOperation.Begin(Preparation, Admission, Handle, Root.FullName);
        public LabProvenanceInspection Read() => PersonalLabOfflineOperation.Inspect(Root.FullName, Preparation.OperationId);
        public void Dispose() { foreach (var file in Root.EnumerateFiles()) file.Delete(); Workspace.Delete(); Root.Delete(); } // Exact owned flat files/empty workspace only.
    }
    [Theory]
    [InlineData("create")] [InlineData("prompt")]
    public void UncertainDeliverySurvivesReopenAndCannotRetry(string kind)
    {
        var uncertain = kind == "create" ? LabEffect.CreateSession : LabEffect.SubmitPrompt;
        using var f = new Owned();
        using (var op = f.Begin())
        {
            if (uncertain == LabEffect.SubmitPrompt)
                using (var create = op.Reserve(LabEffect.CreateSession)) create.RecordReturned("ses_fixture");
            using (op.Reserve(uncertain)) { /* lost response; never refunded */ }
            Assert.Throws<ExternalHarnessException>(() => op.Reserve(uncertain));
            Assert.Throws<IOException>(() => f.Read()); // Exclusive writer/inspection ownership.
        }
        var read = f.Read(); read.DemandExact(f.Preparation, f.Handle);
        Assert.Equal(f.Preparation.Task, read.Header.Identity.Task);
        Assert.Equal(f.Preparation.Binding, read.Header.Identity.Binding);
        Assert.Equal(f.Preparation.EvidenceBudgets, read.Header.Identity.Budgets);
        Assert.Equal(f.Handle, read.Header.Identity.Handle);
        Assert.Equal(PersonalLabOfflineOperation.Fingerprint(read.Header.Identity), read.Header.Fingerprint);
        Assert.Single(read.Events, x => x.Kind == "Uncertain");
        Assert.False(read.CanResume); Assert.Contains("Unknown", read.Recovery);
        Assert.Equal("NotEstablished", read.Receipt); Assert.Equal("NotEstablished", read.HumanAcceptance);
        Assert.Throws<ExternalHarnessException>(() => f.Begin()); // Same consumed admission, never a second task.
        Assert.Throws<ExternalHarnessException>(() => f.Preparation.DemandExecution());
    }
    [Theory]
    [InlineData("objective")] [InlineData("profile")] [InlineData("recipient")] [InlineData("timeout")] [InlineData("execution")]
    public void ChangedOperationCannotAttach(string change)
    {
        using var f = new Owned(); using (f.Begin()) { }
        var read = f.Read();
        var input = f.Preparation.Binding;
        input = change switch { "objective" => input with { Objective = "Another task" },
            "profile" => input with { Profile = LocalExecutionProfile.ProductionProtected },
            "recipient" => input with { HarnessRecipient = input.HarnessRecipient! with { Model = "other" } },
            "timeout" => input with { StageTimeout = TimeSpan.FromSeconds(6) }, _ => input };
        var changed = LocalExecutionPreparation.Prepare(input);
        var sameOperationMutation = read.Header.Identity with { Binding = input,
            Handle = change == "execution" ? f.Handle with { ExecutionId = "other" } : f.Handle };
        Assert.Equal(read.Header.Identity.OperationId, sameOperationMutation.OperationId);
        Assert.NotEqual(read.Header.Fingerprint, PersonalLabOfflineOperation.Fingerprint(sameOperationMutation));
        Assert.Throws<ExternalHarnessException>(() => read.DemandExact(changed, f.Handle));
        Assert.Throws<ExternalHarnessException>(() => read.DemandExact(f.Preparation, f.Handle with { ExecutionId = "other" }));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PersistenceFailureNeverReturnsDurableAdmission(bool incomplete)
    {
        using var f = new Owned();
        var file = Path.Combine(f.Root.FullName, f.Preparation.OperationId + ".lab-journal");
        File.WriteAllText(file, incomplete ? "{" : "existing evidence");
        Assert.Throws<IOException>(() => f.Begin()); Assert.Equal(PersonalLabState.Revoked, f.Admission.State);
        Assert.ThrowsAny<Exception>(() => f.Read());
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(f.Admission, PersonalLabAdmissionTests.Request(f.Preparation)));
        Assert.Equal(incomplete ? "{" : "existing evidence", File.ReadAllText(file));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RevocationConsumptionAndNetworkDenialRemainSeparate(bool revoke)
    {
        using var f = new Owned(); using var op = f.Begin();
        Assert.Throws<ExternalHarnessException>(() => op.Reserve(LabEffect.PcaInference, LabEffectTarget.Network));
        Assert.Equal(0, f.Admission.ReservedEffects);
        if (revoke) op.Revoke(); else f.Admission.Consume();
        Assert.Throws<ExternalHarnessException>(() => op.Reserve(LabEffect.Observe));
        Assert.Throws<ExternalHarnessException>(() => f.Preparation.DemandHarnessRecipient(f.Preparation.Binding.HarnessRecipient));
    }
    [Fact]
    public void ExpiredDurableOperationCannotReserveAnEffect()
    {
        var clock = new PersonalLabAdmissionTests.Clock();
        using var f = new Owned(clock); using (var op = f.Begin())
        {
            clock.Advance = TimeSpan.FromSeconds(21); // Four 5s stages, no renewal/restart grant.
            Assert.Throws<ExternalHarnessException>(() => op.Reserve(LabEffect.CreateSession));
        }
        Assert.Empty(f.Read().Events);
    }
    [Fact]
    public void FlushedIntentWithoutAnyResponseRemainsUnknownAfterReopen()
    {
        using var f = new Owned();
        var op = f.Begin();
        _ = op.Reserve(LabEffect.CreateSession); // Simulate ending after intent, before any provider effect/callback.
        op.Dispose(); // Closes owned stream; deliberately no response or Uncertain callback is invented.
        var read = f.Read();
        Assert.Equal("Intent", Assert.Single(read.Events).Kind);
        Assert.Equal("NotEstablished", read.Receipt); Assert.Contains("Unknown", read.Recovery); Assert.False(read.CanResume);
        read.DemandExact(f.Preparation, f.Handle);
    }
    [Fact]
    public void PureMissingAdmissionDoesNotAllocateStorage()
    {
        var input = PersonalLabAdmissionTests.Input();
        var p = LocalExecutionPreparation.Prepare(input);
        Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(null, PersonalLabAdmissionTests.Request(p)));
        Assert.Equal("PersonalLabNotApproved", Assert.Throws<ExternalHarnessException>(() => p.DemandExecution()).Code);
        var a = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", TimeProvider.System);
        Assert.Equal("LabStorageOutsideWorkspaceRequired", Assert.Throws<ExternalHarnessException>(() =>
            PersonalLabOfflineOperation.Begin(p, a, new("runtime-execution", "OpenCode", ExternalRequestId: "msg_runtime"), p.Task.Workspace)).Code);
        Assert.Equal(PersonalLabState.Admitted, a.State); // No journal or effect created for an invalid storage target.
    }
}
