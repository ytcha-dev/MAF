using MAF.Core;
using MAF.Core.Policy;

namespace MAF.Workflows.External;

// Trusted configuration is a controlled dependency, never request JSON. No execution/provider dependency.
public sealed class CancellationAdmissionApplication(CancellationAdmissionInputs inputs,
    ITrustedIdentityResolver identities, ICancellationAdmissionCoordinator coordinator, TimeProvider clock)
{
    public Task<CancellationAdmissionResult> RequestAsync(CancellationCommand command, ITrustedCallerContext? caller, CancellationToken token = default)
    {
        try
        {
            var identity = caller is null ? null : identities.Resolve(caller); var now = clock.GetUtcNow();
            if (identity is null || identity.SubjectId != caller!.SubjectId || identity.AuthenticationContextId != caller.AuthenticationContextId
                || !Enum.IsDefined(identity.Kind) || identity.Kind == IdentityKind.Unknown || now < identity.NotBefore || now >= identity.NotAfter)
                throw new InvalidOperationException("InvalidCaller");
        }
        catch { throw new ExternalHarnessException("InvalidCaller"); }
        return coordinator.RequestCancelAsync(command, inputs, caller!, token);
    }
}
