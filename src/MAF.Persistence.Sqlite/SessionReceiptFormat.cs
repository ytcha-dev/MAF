using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MAF.Core.Policy;

namespace MAF.Persistence.Sqlite;

internal static class SessionReceiptFormat
{
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly JsonSerializerOptions Options = CreateOptions();
    static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(PolicyEpoch) || info.Type == typeof(PolicyVersion))
                for (var i = info.Properties.Count - 1; i >= 0; i--) if (info.Properties[i].Name != "Value") info.Properties.RemoveAt(i);
            if (info.Kind == JsonTypeInfoKind.Object) foreach (var p in info.Properties) p.IsRequired = true;
        });
        var options = new JsonSerializerOptions { MaxDepth = 8, TypeInfoResolver = resolver, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new ExactEnums()); return options;
    }
    sealed class ExactEnums : JsonConverterFactory
    {
        public override bool CanConvert(Type t) => t.IsEnum;
        public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(ExactEnum<>).MakeGenericType(t))!;
    }
    sealed class ExactEnum<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions options)
            => r.TokenType == JsonTokenType.String && Enum.GetNames<T>().Contains(r.GetString(), StringComparer.Ordinal)
                ? Enum.Parse<T>(r.GetString()!) : throw new JsonException("UnknownReceiptEnum");
        public override void Write(Utf8JsonWriter w, T v, JsonSerializerOptions options)
        { if (!Enum.IsDefined(v)) throw new JsonException("UnknownReceiptEnum"); w.WriteStringValue(v.ToString()); }
    }
    internal static string Write<T>(T value, int limit)
    { var json = JsonSerializer.Serialize(value, Options); _ = Read<T>(json, limit); return json; }
    internal static T Read<T>(string json, int limit)
    {
        if (Utf8.GetByteCount(json) is < 1 || Utf8.GetByteCount(json) > limit) throw new InvalidOperationException("ReceiptEvidenceSize");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); Walk(doc.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException("ReceiptEvidenceMissing");
    }
    static void Walk(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new JsonException("DuplicateReceiptField"); Walk(p.Value); } }
        else if (e.ValueKind == JsonValueKind.Array)
        { if (e.GetArrayLength() > 3) throw new JsonException("ReceiptArrayTooLarge"); foreach (var x in e.EnumerateArray()) Walk(x); }
        else if (e.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(e.GetString()!);
    }
    internal static string Digest(SessionReceiptIntent r) => Hash("ReceiptIntentBinding", w =>
    {
        w.Bytes(OperationBinding.Encode(r.Operation, r.Capability)); w.Text(r.ProviderId); w.Text(r.ReceiptId);
        w.Text(r.ApprovalId); w.Nullable(r.ApprovalVersion); w.Text(r.ApprovalBindingDigest);
        w.Text(r.CallerAuthenticationContextId); w.Text(r.PolicyDigest); w.Number(r.PriorExecutionVersion);
        w.Number(r.IntentExecutionVersion); w.Number(r.IntentControlVersion); w.Number(r.IntentAuditSequence);
        w.Number(r.IntentAtTicks); w.Number(r.RequestTimeoutTicks); w.Number(r.ResponseLimitBytes);
    });
    internal static string Digest(SessionReceiptResponse r) => Hash("ReceiptResponseBinding", w =>
    { w.Text(r.ReceiptId); w.Text(r.IntentDigest); w.Text(r.ProviderSessionId); w.Text(r.Verification.ToString()); w.Text(r.Diagnostic); w.Number(r.ObservedAtTicks); w.Number(r.FinalizedAtTicks); w.Number(r.ResponseAuditSequence); });
    internal static string Link(SessionReceiptRecord r) => Hash("ReceiptLink", w =>
    { w.Text(r.Intent.ReceiptId); w.Text(r.IntentDigest); w.Text(r.ResponseDigest); });
    internal static string Hash(string domain, Action<Writer> append)
    { using var w = new Writer(); w.Text("MAF." + domain); w.Number(1); append(w); return Convert.ToHexString(SHA256.HashData(w.Stream.ToArray())); }
    internal sealed class Writer : IDisposable
    {
        internal readonly MemoryStream Stream = new();
        internal void Number(long n) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, n); Stream.Write(b); }
        internal void Nullable(long? n) { Number(n.HasValue ? 1 : 0); if (n.HasValue) Number(n.Value); }
        internal void Bytes(byte[]? b) { Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(size, b is null ? uint.MaxValue : checked((uint)b.Length)); Stream.Write(size); if (b is not null) Stream.Write(b); }
        internal void Text(string? s) => Bytes(s is null ? null : Utf8.GetBytes(s));
        public void Dispose() => Stream.Dispose();
    }
}
