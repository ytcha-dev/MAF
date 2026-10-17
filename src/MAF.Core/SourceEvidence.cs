using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAF.Core;

public enum SourceCandidateOrigin { SyntheticTest }
public enum SourceEvidenceStatus { Verified, InvalidCandidate, Missing, Mismatch, Redacted, Changed, LimitExceeded, ClaimUnverified, Unavailable }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SourceBinding([property: JsonRequired] string TaskId, [property: JsonRequired] string WorkflowRunId,
    [property: JsonRequired] string ExecutionId, [property: JsonRequired] string SessionId, [property: JsonRequired] string MessageId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SourceCandidate([property: JsonRequired] SourceBinding Binding, [property: JsonRequired] string RelativePath,
    [property: JsonRequired] int StartLine, [property: JsonRequired] int EndLine,
    string? ExpectedAnchor, [property: JsonRequired] SourceCandidateOrigin Origin);
public sealed record SourceEvidenceItem(int CandidateIndex, SourceEvidenceStatus Status, VerifiedSourceEvidence? Evidence = null);
public sealed record SourceEvidenceBatch(ImmutableArray<SourceEvidenceItem> Items)
{
    [JsonIgnore] public bool Sufficient => !Items.IsDefaultOrEmpty && Items.All(i => i.Status == SourceEvidenceStatus.Verified && i.Evidence is not null);
    [JsonIgnore] public ImmutableArray<VerifiedSourceEvidence> Admitted => Items.Where(i => i.Status == SourceEvidenceStatus.Verified && i.Evidence is not null).Select(i => i.Evidence!).ToImmutableArray();
}

// Issued only by the independent reader; JSON is an output format, never input authority.
[JsonConverter(typeof(VerifiedSourceEvidenceConverter))]
public sealed class VerifiedSourceEvidence
{
    internal readonly object Issuer;
    internal readonly string RawDigest;
    internal readonly long Length, Written, Created;
    internal VerifiedSourceEvidence(object issuer, SourceBinding binding, string path, int start, int end, string content,
        DateTimeOffset at, string rawDigest, long length, long written, long created)
    {
        Issuer = issuer; Binding = binding; RelativePath = path; StartLine = start; EndLine = end; Content = content;
        AcquiredAt = at; RawDigest = rawDigest; Length = length; Written = written; Created = created;
        Digest = SourceEvidenceDigest.Compute(path, start, end, content);
    }
    public SourceBinding Binding { get; }
    public string RelativePath { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public string Content { get; }
    public DateTimeOffset AcquiredAt { get; }
    public string Digest { get; }
    public string Reference => "source-v1:" + Digest;
}

public static class SourceEvidenceDigest
{
    // Version 1, big-endian UTF-8 length-prefix encoding. No platform newline/JSON dependence.
    public static string Compute(string path, int start, int end, string sanitizedContent)
    {
        using var stream = new MemoryStream();
        void Number(int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); stream.Write(bytes); }
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); Number(bytes.Length); stream.Write(bytes); }
        Text("MAF.SourceEvidence"); Number(1); Text(path); Number(start); Number(end); Text(sanitizedContent);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}

public sealed class VerifiedSourceEvidenceConverter : JsonConverter<VerifiedSourceEvidence>
{
    public override VerifiedSourceEvidence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new JsonException("SourceEvidenceMustBeIndependentlyAcquired");
    public override void Write(Utf8JsonWriter writer, VerifiedSourceEvidence value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, new { value.Binding, value.RelativePath, value.StartLine, value.EndLine,
            value.Content, value.AcquiredAt, value.Digest, value.Reference }, options);
}
