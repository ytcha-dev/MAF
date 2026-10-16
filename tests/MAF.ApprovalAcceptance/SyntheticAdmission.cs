using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;

namespace MAF.ApprovalAcceptance;

// Only test executables reference this fixture. Never a production identity fallback.
public sealed class SyntheticAdmission : ITrustedIdentityResolver
{
    public SyntheticClock Clock { get; } = new();
    public SyntheticActor Caller { get; } = new("submitter");
    public string Root { get; } = DurableExecution.NormalizeRoot(Path.Combine(Path.GetPathRoot(Path.GetFullPath("."))!, "maf-synthetic-admission"));
    public AuthorizationScope Scope => SyntheticApproval.Scope with { ResourceId = Root };
    public ExternalTaskRequest Request => new("task-admit", Root, "SYNTHETIC_PROMPT_NOT_FOR_STORAGE", TimeSpan.FromSeconds(30));
    public PolicySnapshot Policy => SyntheticApproval.Policy with
    { Rules = [new("submitter", SyntheticApproval.Descriptor.ConnectorId, PolicyCapability.ReadOnlyTask,
        PolicyOperation.SubmitNewReadOnlyTask, Scope, PolicyOutcome.Allow)] };
    public ExternalAdmissionInputs Inputs => new("authority", new(1), new(1), "Synthetic", Scope,
        SyntheticApproval.Descriptor, SyntheticApproval.Descriptor with
        { Capability = PolicyCapability.ExecutionInspection, SupportedOperations = [PolicyOperation.InspectExistingExecution] },
        "operation-admit", "run-admit", new("execution-admit", "Synthetic", ExternalRequestId: "message-admit"), []);
    public ExternalAdmissionRequest Intent => new(ExternalAdmissionMode.Submit, Request.TaskId, Request.Timeout, Request);
    public AuthenticatedIdentity? Resolve(ITrustedCallerContext context) => ReferenceEquals(context, Caller)
        ? new(Caller.SubjectId, Caller.AuthenticationContextId, IdentityKind.Agent, Clock.Now.AddHours(-1), Clock.Now.AddMinutes(5), false) : null;
    public ExternalAdmissionResult Assessment => new ExternalExecutionApplication(Inputs, new ImmutablePolicySource(Policy), this, Clock).Assess(Intent, Caller);
    public DurableExecution Initial => SyntheticApproval.Execution(Assessment.Operation!, Clock.Now);
    public AtomicAdmissionApplication Application(IExecutionAdmissionCoordinator coordinator) => new(Inputs, new ImmutablePolicySource(Policy), this, coordinator, Clock);
}
