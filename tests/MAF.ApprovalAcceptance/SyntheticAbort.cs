using MAF.Core.Policy;
using MAF.Persistence.Sqlite;

namespace MAF.ApprovalAcceptance;

// Test-only composition. No production authority or identity fallback.
public sealed class SyntheticAbort
{
    public string[] SeedCalls { get; private set; } = [];
    public SyntheticCancellation F { get; }
    public CapabilityDescriptor Capability => F.Runtime.Submit with { Capability = PolicyCapability.ExecutionAbort, SupportedOperations = [PolicyOperation.AbortExistingExecution] };
    public AbortDeliveryInputs Inputs => new(F.Policy.AuthorityId, F.Policy.Epoch, F.Policy.Version, F.Runtime.Scope, Capability, "abort-operation");
    public AbortDeliveryCommand Command => new("delivery-one", "cancel-one", "runtime-task", "runtime-execution");
    readonly bool approved;
    public SyntheticAbort(string database, bool approved = false)
    {
        this.approved = approved; F = new(database, approved, v4: true);
        F.Policy = F.Policy with { Capabilities = [..F.Policy.Capabilities, Capability], Rules = [..F.Policy.Rules,
            new(F.Runtime.Caller.SubjectId, Capability.ConnectorId, Capability.Capability, PolicyOperation.AbortExistingExecution, F.Runtime.Scope, PolicyOutcome.Allow)] };
    }
    public async Task Seed(bool cancellation = true, bool emptySession = false)
    {
        await ApprovalSchema.InitializeOrMigrateAsync(F.Runtime.Database, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(F.Runtime.Database, SyntheticApproval.Maintenance, F);
        await ApprovalSchema.MigrateV3ToV4Async(F.Runtime.Database, SyntheticApproval.Maintenance, F);
        await F.Store().ActivatePolicyAsync();
        using var fake = new RuntimeHttpFixture(F.Runtime.Root) { LoseSubmitResponse = true, Running = true, StatePath = F.Runtime.Database + ".provider" };
        using var http = MAF.Workflows.External.ReceiptTransport.ForTests(fake);
        if (emptySession) fake.OnRequest = async (request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                await F.Application().RequestAsync(F.Command, F.Canceller);
        };
        if (approved)
        {
            var store = F.Store();
            var app = new MAF.Workflows.External.DurableApprovalApplication(F.Runtime.Inputs, new ImmutablePolicySource(F.Policy), F, F.Executions, store, F.Clock);
            var pending = await app.RequestAsync("cancel-approval", F.Runtime.Intent, F.Runtime.Caller);
            var decision = await app.DecideAsync(new(pending.ApprovalId, "decision", 0, pending.BindingDigest, ApprovalChoice.Approve), F.Approval.Operator);
            await F.Protected(http).ResumeAsync(new(decision.ApprovalId, decision.Version, decision.BindingDigest, F.Runtime.Request), F.Runtime.Caller, F.Approval.Operator);
        }
        else await F.Protected(http).RunAsync(F.Runtime.Intent, F.Runtime.Caller);
        SeedCalls = fake.Requests.ToArray();
        if (cancellation && !emptySession)
        {
            var cancel = await F.Application().RequestAsync(F.Command, F.Canceller);
            if (cancel.Status != CancellationAdmissionStatus.Recorded) throw new InvalidOperationException("SyntheticCancelNotRecorded:" + cancel.Status);
        }
    }
}
