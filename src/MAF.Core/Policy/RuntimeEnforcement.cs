namespace MAF.Core.Policy;

public enum ProviderEffect { Validate, CreateSession, SubmitPrompt, Observe, Reattach, Abort, Metadata }

// Local exact authorization plus optional dispatch-intent CAS in one short transaction.
// Never holds a transaction across provider I/O. This is not an HTTP delivery guarantee.
public interface IRuntimeEnforcementCoordinator
{
    Task VerifyEffectAsync(PolicyRequest operation, PolicyDecision assessment, DurableExecution expected,
        DurableExecution? intent, long controlVersion, ProviderEffect effect, ITrustedCallerContext caller,
        IExecutionOwnership ownership, CancellationToken token = default);
}
