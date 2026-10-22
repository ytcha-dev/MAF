using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MAF.Host;

// Descriptions supplied for offline comparison, not observations or permission grants.
// Only explicit absence is supported in this bounded first contract.
internal enum LabDownstreamFlow { Unknown, None, Present }

internal sealed record LabOpenCodeDownstreamConfiguration(HarnessRecipientBinding Recipient,
    string Endpoint, string ConfigurationDigest,
    LabDownstreamFlow Overrides = LabDownstreamFlow.Unknown,
    LabDownstreamFlow Fallback = LabDownstreamFlow.Unknown,
    LabDownstreamFlow AuxiliaryOutbound = LabDownstreamFlow.Unknown)
{
    public override string ToString() => "LabOpenCodeDownstreamConfiguration / ValuesRedacted / NotRuntimeEvidence";
}

// No constructors/deserialization/record-with path can substitute the original preparation.
// This object has no authority, admission, transport, process or persistence dependency.
internal sealed class LabOpenCodeExecutionPlan
{
    readonly LabOpenCodeDownstreamConfiguration expected;
    public LocalExecutionPreparation Preparation { get; }
    public string OperationId => Preparation.OperationId;
    public HarnessRecipientBinding Recipient => expected.Recipient;
    public Uri ExpectedEndpoint { get; }
    public string ConfigurationDigest => expected.ConfigurationDigest;
    public string BindingDigest { get; }

    private LabOpenCodeExecutionPlan(LocalExecutionPreparation preparation, LabOpenCodeDownstreamConfiguration configuration)
    {
        Preparation = preparation;
        expected = configuration;
        ExpectedEndpoint = new(configuration.Endpoint, UriKind.Absolute);
        // Fixed field order + explicit version; no environment, workspace paths, credentials or execution IDs.
        // This digest describes the binding; it does not authenticate the supplied configuration digest.
        BindingDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, configurationId = Recipient.ConfigurationId, provider = Recipient.Provider,
            model = Recipient.Model, endpoint = expected.Endpoint, configurationDigest = ConfigurationDigest,
            overrides = "none", fallback = "none", auxiliaryOutbound = "none"
        }))).ToLowerInvariant();
    }

    internal static LabOpenCodeExecutionPlan Bind(LocalExecutionPreparation preparation,
        LabOpenCodeDownstreamConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.Binding.Profile != LocalExecutionProfile.PersonalLab)
            throw new ArgumentException("LabPlanPersonalLabRequired");
        // Preparation already validates the role/model identifiers, workspace and immutable intent.
        // No second preparation, workspace probe or claim of an admission is needed here.
        var canonical = Canonical(configuration);
        if (canonical.Recipient != preparation.Binding.HarnessRecipient)
            throw new ArgumentException("LabPlanRecipientMismatch");
        return new(preparation, canonical);
    }

    // Compares descriptions only. In particular, a matching caller-supplied digest is not
    // effective-config custody, downstream attestation, consent or live authorization.
    internal void DemandConfiguration(LabOpenCodeDownstreamConfiguration candidate)
    {
        if (Canonical(candidate) != expected)
            throw new ArgumentException("LabPlanConfigurationMismatch");
    }

    static LabOpenCodeDownstreamConfiguration Canonical(LabOpenCodeDownstreamConfiguration value)
    {
        if (value is null || value.Recipient is null)
            throw new ArgumentException("LabPlanConfigurationRequired");
        if (value.Overrides != LabDownstreamFlow.None || value.Fallback != LabDownstreamFlow.None
            || value.AuxiliaryOutbound != LabDownstreamFlow.None)
            throw new ArgumentException("LabPlanUnresolvedOutboundFlow");
        if (value.ConfigurationDigest is null || !Regex.IsMatch(value.ConfigurationDigest, "\\A[0-9a-fA-F]{64}\\z"))
            throw new ArgumentException("LabPlanInvalidConfigurationDigest");
        var text = value.Endpoint;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 || text != text.Trim()
            || LocalSoloApplication.UnsafeCharacters(text) || text.IndexOfAny(['?', '#', '\\']) >= 0
            || !Uri.TryCreate(text, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || endpoint.UserInfo.Length != 0 || endpoint.Host.Length == 0)
            throw new ArgumentException("LabPlanInvalidEndpoint");
        return value with { Endpoint = endpoint.AbsoluteUri, ConfigurationDigest = value.ConfigurationDigest.ToLowerInvariant() };
    }

    public override string ToString() => "PersonalLab / OfflineConfigurationBinding / NotRuntimeEvidence / ExecutionBlocked";
}
