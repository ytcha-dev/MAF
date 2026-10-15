using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MAF.Core.Policy;

namespace MAF.Tests;

public sealed class OperationBindingTests
{
    static readonly AuthorizationScope Scope = new("resource:資料😀", new('A', 64), new('B', 64), 1, 1, ResourceAccess.ReadOnly, 100);
    static readonly CapabilityDescriptor Descriptor = new("connector", "1", PolicyCapability.ReadOnlyTask, EnforcementBoundary.TaskAdmission,
        [PolicyOperation.SubmitNewReadOnlyTask, PolicyOperation.InspectExistingExecution], true, true);
    static readonly PolicyRequest Request = new("authority", "subject", "operation", "task", "run", "execution", "message", new('C', 64),
        "connector", PolicyCapability.ReadOnlyTask, PolicyOperation.SubmitNewReadOnlyTask, Scope, new(1), new(1));

    [Fact]
    public void VersionOneMatchesIndependentPublishedVector()
        => Assert.Equal("A44044AEF87674BA9046587F4E99EE20CFDF6D1AECB7A58EBDD925C9667FCFB3", OperationBinding.Digest(Request, Descriptor));

    [Fact]
    public void SourceObjectPropertyOrderDoesNotAffectCanonicalBinding()
    {
        var original = JsonSerializer.SerializeToElement(Request);
        var reversed = original.EnumerateObject().Reverse().ToDictionary(p => p.Name, p => p.Value);
        var scope = original.GetProperty("Scope").EnumerateObject().Reverse().ToDictionary(p => p.Name, p => p.Value);
        var changed = reversed.ToDictionary(p => p.Key, p => (object)p.Value);
        changed["Scope"] = scope;
        var reordered = JsonSerializer.Deserialize<PolicyRequest>(JsonSerializer.Serialize(changed))!;
        Assert.Equal(OperationBinding.Encode(Request, Descriptor), OperationBinding.Encode(reordered, Descriptor));
        Assert.Equal(OperationBinding.Digest(Request, Descriptor), OperationBinding.Digest(reordered, Descriptor));
        Assert.Equal(OperationBinding.Digest(Request, Descriptor), OperationBinding.Digest(Request, Descriptor with { SupportedOperations = [..Descriptor.SupportedOperations.Reverse()] }));
    }

    public static IEnumerable<object[]> BindingChanges()
    {
        yield return [Request with { AuthorityId = "other-authority" }]; yield return [Request with { CallerSubject = "other-subject" }];
        yield return [Request with { OperationId = "other-operation" }]; yield return [Request with { TaskId = "other-task" }];
        yield return [Request with { WorkflowRunId = "other-run" }]; yield return [Request with { ExecutionId = "other-execution" }];
        yield return [Request with { MessageId = "other-message" }]; yield return [Request with { RequestFingerprint = new('D', 64) }];
        yield return [Request with { ConnectorId = "other-connector" }];
        yield return [Request with { Capability = PolicyCapability.ExecutionInspection }];
        yield return [Request with { Operation = PolicyOperation.InspectExistingExecution }];
        yield return [Request with { Scope = Scope with { ResourceId = "other-resource" } }];
        yield return [Request with { Scope = Scope with { ProviderScopeDigest = new('D', 64) } }];
        yield return [Request with { Scope = Scope with { AgentPolicyDigest = new('E', 64) } }];
        yield return [Request with { Scope = Scope with { ConfigurationRevision = 2 } }];
        yield return [Request with { Scope = Scope with { ScopeRevision = 2 } }];
        yield return [Request with { Scope = Scope with { BudgetTicks = 101 } }];
        yield return [Request with { Scope = Scope with { Access = ResourceAccess.Write } }];
        yield return [Request with { Epoch = new(2) }]; yield return [Request with { Version = new(2) }];
        yield return [Request with { IntentGeneration = 2 }]; yield return [Request with { ProviderSessionId = "session" }];
    }
    [Theory, MemberData(nameof(BindingChanges))]
    public void EveryRelevantBindingDimensionChangesDigest(PolicyRequest changed)
        => Assert.NotEqual(OperationBinding.Digest(Request, Descriptor), OperationBinding.Digest(changed, Descriptor));

    [Fact]
    public void AdapterAndDescriptorChangesAreBound()
    {
        var digest = OperationBinding.Digest(Request, Descriptor);
        Assert.NotEqual(digest, OperationBinding.Digest(Request, Descriptor with { AdapterVersion = "2" }));
        Assert.NotEqual(digest, OperationBinding.Digest(Request, Descriptor with { Healthy = false }));
        Assert.NotEqual(digest, OperationBinding.Digest(Request, Descriptor with { Enabled = false }));
        Assert.NotEqual(digest, OperationBinding.Digest(Request, Descriptor with { Boundary = EnforcementBoundary.ProviderChallenge }));
    }

    [Fact]
    public void EncodingUsesVersionedBigEndianLengthsOfUtf8Bytes()
    {
        var encoded = OperationBinding.Encode(Request with { AuthorityId = "甲😀" }, Descriptor);
        var domainLength = BinaryPrimitives.ReadUInt32BigEndian(encoded);
        Assert.Equal("MAF.OperationBinding", Encoding.UTF8.GetString(encoded, 4, (int)domainLength));
        var offset = 4 + (int)domainLength;
        Assert.Equal(1, BinaryPrimitives.ReadInt64BigEndian(encoded.AsSpan(offset))); offset += 8;
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset))); offset += 4;
        Assert.Equal(Encoding.UTF8.GetBytes("甲😀"), encoded[offset..(offset + 7)]);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1024)]
    public void LengthBoundariesAreUnambiguous(int length)
    {
        var request = Request with { AuthorityId = new string('界', length) };
        var bytes = OperationBinding.Encode(request, Descriptor);
        var offset = 4 + Encoding.UTF8.GetByteCount("MAF.OperationBinding") + 8;
        Assert.Equal((uint)(3 * length), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
        Assert.NotEqual(OperationBinding.Digest(request, Descriptor), OperationBinding.Digest(request with { AuthorityId = new string('a', length) }, Descriptor));
    }

    [Fact]
    public void FieldBoundariesPreventConcatenationCollisions()
    {
        var left = Request with { OperationId = "ab", TaskId = "c" };
        var right = Request with { OperationId = "a", TaskId = "bc" };
        Assert.Equal(left.OperationId + left.TaskId, right.OperationId + right.TaskId);
        Assert.NotEqual(OperationBinding.Digest(left, Descriptor), OperationBinding.Digest(right, Descriptor));
        Assert.NotEqual(OperationBinding.Digest(Request, Descriptor), OperationBinding.Digest(Request with { ProviderSessionId = "session" }, Descriptor));
    }

    [Fact]
    public void UndefinedValuesMalformedUnicodeAndOversizedInputAreRejected()
    {
        Assert.Throws<ArgumentException>(() => OperationBinding.Encode(Request with { Operation = (PolicyOperation)999 }, Descriptor));
        Assert.Throws<ArgumentException>(() => OperationBinding.Encode(Request with { Capability = (PolicyCapability)999 }, Descriptor));
        Assert.Throws<ArgumentException>(() => OperationBinding.Encode(Request with { CallerSubject = "\uD800" }, Descriptor));
        Assert.Throws<ArgumentException>(() => OperationBinding.Encode(Request with { TaskId = new('x', 1025) }, Descriptor));
        Assert.Throws<ArgumentException>(() => OperationBinding.Encode(Request with { ProviderSessionId = "" }, Descriptor));
        Assert.Throws<ArgumentException>(() => OperationBinding.Encode(Request, Descriptor with { SupportedOperations = [(PolicyOperation)999] }));
    }
}
