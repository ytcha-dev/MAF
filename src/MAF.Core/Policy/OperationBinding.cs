using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MAF.Core.Policy;

/// <summary>
/// Binding v1: fixed field order, big-endian signed 64-bit numbers; strict UTF-8 strings prefixed
/// with unsigned 32-bit byte lengths (FFFFFFFF means null). No culture, JSON, or property enumeration.
/// Hash is correlation metadata, not a signature or authorization token.
/// </summary>
public static class OperationBinding
{
    public const int EncodingVersion = 1;
    public static string Digest(PolicyRequest request, CapabilityDescriptor descriptor)
        => Convert.ToHexString(SHA256.HashData(Encode(request, descriptor)));

    public static byte[] Encode(PolicyRequest request, CapabilityDescriptor descriptor)
    {
        if (!PolicyValidation.Request(request) || !Enum.IsDefined(request.Operation) || request.Operation == PolicyOperation.Unknown
            || !Enum.IsDefined(request.Capability) || request.Capability == PolicyCapability.Unknown
            || descriptor is null || !PolicyValidation.Text(descriptor.ConnectorId) || !PolicyValidation.Text(descriptor.AdapterVersion)
            || !Enum.IsDefined(descriptor.Capability) || descriptor.Capability == PolicyCapability.Unknown
            || !Enum.IsDefined(descriptor.Boundary) || descriptor.Boundary == EnforcementBoundary.Unknown
            || descriptor.SupportedOperations.IsDefaultOrEmpty
            || descriptor.SupportedOperations.Any(op => !Enum.IsDefined(op) || op == PolicyOperation.Unknown)
            || descriptor.SupportedOperations.Distinct().Count() != descriptor.SupportedOperations.Length)
            throw new ArgumentException("Invalid operation binding metadata.");
        using var stream = new MemoryStream();
        var utf8 = new UTF8Encoding(false, true);
        void Number(long n) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, n); stream.Write(b); }
        void Text(string? s)
        {
            var bytes = s is null ? null : utf8.GetBytes(s);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, bytes is null ? uint.MaxValue : checked((uint)bytes.Length));
            stream.Write(length); if (bytes is not null) stream.Write(bytes);
        }
        Text("MAF.OperationBinding"); Number(EncodingVersion);
        Text(request.AuthorityId); Number(request.Epoch.Value); Number(request.Version.Value);
        Text(request.CallerSubject); Text(request.OperationId); Text(request.TaskId); Text(request.WorkflowRunId);
        Text(request.ExecutionId); Text(request.MessageId); Text(request.RequestFingerprint.ToUpperInvariant());
        Text(request.ConnectorId); Text(request.Capability.ToString()); Text(request.Operation.ToString());
        Text(request.Scope.ResourceId); Text(request.Scope.ProviderScopeDigest.ToUpperInvariant());
        Text(request.Scope.AgentPolicyDigest.ToUpperInvariant()); Number(request.Scope.ConfigurationRevision);
        Number(request.Scope.ScopeRevision); Text(request.Scope.Access.ToString()); Number(request.Scope.BudgetTicks);
        Number(request.IntentGeneration); Text(request.ProviderSessionId);
        Text(descriptor.ConnectorId); Text(descriptor.AdapterVersion); Text(descriptor.Capability.ToString());
        Text(descriptor.Boundary.ToString()); Number(descriptor.Enabled ? 1 : 0); Number(descriptor.Healthy ? 1 : 0);
        var operations = descriptor.SupportedOperations.OrderBy(op => op.ToString(), StringComparer.Ordinal).ToArray();
        Number(operations.Length); foreach (var op in operations) Text(op.ToString());
        return stream.ToArray();
    }
}
