using System.Text.RegularExpressions;
using MAF.Agents.Comprehension;
using MAF.Agents.Review;
using MAF.Core;
using MAF.Core.Tools;
using Microsoft.Extensions.AI;

namespace MAF.Host;

internal enum LocalExecutionProfile { PersonalLab, ProductionProtected }
internal enum NativeModelRole { Pca, Reviewer }

// Configuration values, not approved recipients or capabilities. No credentials retained.
internal sealed record NativeRecipientBinding(NativeModelRole Role, string ConfigurationId,
    Uri Endpoint, string Provider, string Model);
internal sealed record HarnessRecipientBinding(string ConfigurationId, string Provider, string Model);
// Versioned existing bounded-evidence contract, not configurable promises about provider cost.
internal sealed record LocalEvidenceBudgets(int Sources = 8, int LinesPerSource = 20,
    int ReviewerModelCalls = 8, int ReviewerToolOutputCharacters = 32768);
internal sealed record LocalExecutionInput(string Objective, string Workspace,
    LocalExecutionProfile Profile, NativeRecipientBinding? Pca, NativeRecipientBinding? Reviewer,
    HarnessRecipientBinding? HarnessRecipient, TimeSpan StageTimeout,
    string Harness = "OpenCode", ExternalExecutionPolicy Access = ExternalExecutionPolicy.ReadOnly);

// A private constructor and get-only state prevent record-with mutation or proof deserialization.
// An operation identity is correlation only; this object cannot dispatch anything.
internal sealed class LocalExecutionPreparation
{
    public string OperationId { get; }
    public HandoffTask Task { get; }
    public LocalExecutionInput Binding { get; }
    public LocalEvidenceBudgets EvidenceBudgets { get; } = new();
    private LocalExecutionPreparation(LocalExecutionInput binding)
    {
        Binding = binding;
        OperationId = Guid.NewGuid().ToString("N");
        Task = new("local-" + OperationId, "workflow-" + OperationId, binding.Workspace,
            binding.Objective, binding.StageTimeout);
    }

    public static LocalExecutionPreparation Prepare(LocalExecutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.IsDefined(input.Profile)) throw new ArgumentException("UnsupportedExecutionProfile");
        if (input.Harness != "OpenCode") throw new ArgumentException("UnsupportedHarness");
        if (input.Access != ExternalExecutionPolicy.ReadOnly) throw new ArgumentException("ReadOnlyRequired");
        if (string.IsNullOrWhiteSpace(input.Objective) || input.Objective.Length > LocalSoloApplication.MaxObjectiveLength
            || LocalSoloApplication.UnsafeCharacters(input.Objective)) throw new ArgumentException("InvalidObjective");
        if (input.StageTimeout < TimeSpan.FromMilliseconds(10) || input.StageTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("InvalidStageBudget");
        ValidateNative(input.Pca, NativeModelRole.Pca);
        ValidateNative(input.Reviewer, NativeModelRole.Reviewer);
        if (input.Pca!.ConfigurationId == input.Reviewer!.ConfigurationId)
            throw new ArgumentException("IndependentRoleConfigurationRequired");
        if (input.HarnessRecipient is not { } harness) throw new ArgumentException("HarnessRecipientBindingMissing");
        Identifier(harness.ConfigurationId); Identifier(harness.Provider); Identifier(harness.Model);
        return new(input with { Workspace = LocalSoloApplication.ValidateWorkspace(input.Workspace) });
    }

    // Compare the complete value, including roles/recipients/budget. No partial-match upgrade.
    public void DemandExact(LocalExecutionInput candidate)
    {
        if (candidate != Binding) throw new ArgumentException("PreparedIntentMismatch");
        if (LocalSoloApplication.ValidateWorkspace(Binding.Workspace) != Binding.Workspace)
            throw new ArgumentException("PreparedWorkspaceChanged");
    }

    static void Identifier(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:/-]{0,159}\z"))
            throw new ArgumentException("InvalidRecipientConfiguration");
    }
    static void ValidateNative(NativeRecipientBinding? binding, NativeModelRole role)
    {
        if (binding is null) throw new ArgumentException(role + "BindingMissing");
        if (binding.Role != role) throw new ArgumentException("ModelRoleMismatch");
        Identifier(binding.ConfigurationId); Identifier(binding.Provider); Identifier(binding.Model);
        var uri = binding.Endpoint;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsoluteUri.Length > 1024) throw new ArgumentException("InvalidNativeRecipient");
    }

    // Useful adapter wiring without client construction or requests. Metadata matching is only
    // compatibility, not proof of transport/redirect/tool egress or independent data approval.
    public (ITaskBriefing Pca, NativeEvidenceReviewer Reviewer) BindNativeAdapters(
        IChatClient pca, IChatClient reviewer)
    {
        DemandExact(Binding);
        if (ReferenceEquals(pca, reviewer)) throw new ArgumentException("IndependentRoleClientsRequired");
        Match(pca, Binding.Pca!); Match(reviewer, Binding.Reviewer!);
        return (new TaskBriefingAdapter(pca, new ProjectTools(Task.Workspace)), new NativeEvidenceReviewer(reviewer));
    }
    static void Match(IChatClient client, NativeRecipientBinding binding)
    {
        ArgumentNullException.ThrowIfNull(client);
        var metadata = client.GetService<ChatClientMetadata>();
        if (metadata is null || metadata.ProviderUri != binding.Endpoint
            || metadata.ProviderName != binding.Provider || metadata.DefaultModelId != binding.Model)
            throw new ArgumentException("NativeRecipientMismatch");
    }

    public void DemandHarnessRecipient(HarnessRecipientBinding? observed)
    {
        if (observed is null || observed != Binding.HarnessRecipient)
            throw new ArgumentException("HarnessRecipientMismatch");
        // OpenCodeOptions/Scope do not attest downstream recipient. Equality is not attestation.
        throw new ExternalHarnessException("HarnessRecipientVerificationUnavailable");
    }

    // No callback/client/store/runtime accepted. Both profiles are disabled, independently.
    public void DemandExecution() => throw new ExternalHarnessException(
        Binding.Profile == LocalExecutionProfile.PersonalLab ? "PersonalLabNotApproved" : LocalSoloApplication.Blocker);
}
