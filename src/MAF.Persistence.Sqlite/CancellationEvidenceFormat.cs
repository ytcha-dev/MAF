using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MAF.Core.Policy;

namespace MAF.Persistence.Sqlite;

internal static class CancellationEvidenceFormat
{
    internal sealed record Payload(PolicyRequest Operation, CapabilityDescriptor Capability);
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly JsonSerializerOptions Options = CreateOptions();
    static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(PolicyEpoch) || info.Type == typeof(PolicyVersion))
                for (int i = info.Properties.Count - 1; i >= 0; i--) if (info.Properties[i].Name != "Value") info.Properties.RemoveAt(i);
        });
        var options = new JsonSerializerOptions { MaxDepth = 8, TypeInfoResolver = resolver, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)); return options;
    }
    internal static string Write(PolicyRequest operation, CapabilityDescriptor descriptor)
    {
        _ = OperationBinding.Encode(operation, descriptor);
        var value = new Payload(operation with { RequestFingerprint = operation.RequestFingerprint.ToUpperInvariant(),
            Scope = operation.Scope with { ProviderScopeDigest = operation.Scope.ProviderScopeDigest.ToUpperInvariant(), AgentPolicyDigest = operation.Scope.AgentPolicyDigest.ToUpperInvariant() } },
            descriptor with { SupportedOperations = [.. descriptor.SupportedOperations.OrderBy(x => x.ToString(), StringComparer.Ordinal)] });
        var json = JsonSerializer.Serialize(value, Options); _ = Read(json); return json;
    }
    internal static Payload Read(string json)
    {
        if (Utf8.GetByteCount(json) is < 1 or > 16384) throw new InvalidOperationException("InvalidCancellationEvidence");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        Walk(doc.RootElement);
        var root = doc.RootElement; Fields(root, "Operation Capability");
        var op = root.GetProperty("Operation");
        Fields(op, "AuthorityId CallerSubject OperationId TaskId WorkflowRunId ExecutionId MessageId RequestFingerprint ConnectorId Capability Operation Scope Epoch Version IntentGeneration ProviderSessionId");
        Fields(op.GetProperty("Scope"), "ResourceId ProviderScopeDigest AgentPolicyDigest ConfigurationRevision ScopeRevision Access BudgetTicks");
        Fields(op.GetProperty("Epoch"), "Value"); Fields(op.GetProperty("Version"), "Value");
        var cap = root.GetProperty("Capability"); Fields(cap, "ConnectorId AdapterVersion Capability Boundary SupportedOperations Enabled Healthy");
        Named<PolicyOperation>(op.GetProperty("Operation")); Named<PolicyCapability>(op.GetProperty("Capability"));
        Named<ResourceAccess>(op.GetProperty("Scope").GetProperty("Access"));
        Named<PolicyCapability>(cap.GetProperty("Capability")); Named<EnforcementBoundary>(cap.GetProperty("Boundary"));
        foreach (var item in cap.GetProperty("SupportedOperations").EnumerateArray()) Named<PolicyOperation>(item);
        var result = JsonSerializer.Deserialize<Payload>(json, Options) ?? throw new InvalidOperationException("InvalidCancellationEvidence");
        _ = OperationBinding.Encode(result.Operation, result.Capability);
        return result;
    }
    static void Named<T>(JsonElement value) where T : struct, Enum
    { if (value.ValueKind != JsonValueKind.String || !Enum.GetNames<T>().Contains(value.GetString(), StringComparer.Ordinal)) throw new InvalidOperationException("InvalidCancellationEnum"); }
    static void Fields(JsonElement value, string names)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(names.Split(' ').Order()))
            throw new InvalidOperationException("InvalidCancellationFields");
    }
    static void Walk(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidOperationException("DuplicateCancellationField"); Walk(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { if (value.GetArrayLength() > 3) throw new InvalidOperationException("CancellationArrayTooLarge"); foreach (var item in value.EnumerateArray()) Walk(item); }
        else if (value.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(value.GetString()!);
    }
}
