using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using MAF.Core;
using MAF.Core.Tools;

namespace MAF.Workflows.External.OpenCode;

// One concrete versioned ReadTool shape. Proposals only; no local reads, requests or trust grants.
internal static class OpenCodeReadSources
{
    internal static ImmutableArray<UntrustedSourceLocation> Extract(IEnumerable<JsonElement> messages,
        ExternalExecutionHandle handle, string root, string? version)
    {
        if (version is not ("1.18.35" or "1.19.0")) return [];
        try
        {
            var result = ImmutableArray.CreateBuilder<UntrustedSourceLocation>();
            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var message in messages)
            {
                var info = message.GetProperty("info");
                if (Text(info, "role") != "assistant" || Text(info, "sessionID") != handle.ExternalSessionId
                    || Text(info, "parentID") != handle.ExternalRequestId) continue;
                if (!HandoffValidation.Text(Text(info, "id"), 160)) return [];
                foreach (var part in message.GetProperty("parts").EnumerateArray())
                {
                    var type = Text(part, "type");
                    if (type is "text" or "reasoning" or "step-start" or "step-finish") continue;
                    // Unknown source/attachment/tool shapes cannot supplement a partial positive batch.
                    if (type != "tool" || Text(part, "tool") != "read" || result.Count == 8
                        || Encoding.UTF8.GetByteCount(part.GetRawText()) > 16384 || !UniqueProperties(part)
                        || Text(part, "sessionID") != handle.ExternalSessionId || Text(part, "messageID") != Text(info, "id")
                        || !HandoffValidation.Text(Text(part, "id"), 160) || !HandoffValidation.Text(Text(part, "callID"), 160)) return [];
                    var state = part.GetProperty("state"); var input = state.GetProperty("input");
                    var time = state.GetProperty("time"); var metadata = state.GetProperty("metadata");
                    var display = metadata.GetProperty("display");
                    if (Text(state, "status") != "completed" || !HandoffValidation.Text(Text(state, "output"), 16384)
                        || !HandoffValidation.Text(Text(state, "title"), 1024) || state.TryGetProperty("error", out _)
                        || time.TryGetProperty("compacted", out _) || time.GetProperty("start").GetInt64() < 0
                        || time.GetProperty("end").GetInt64() < time.GetProperty("start").GetInt64()
                        || metadata.GetProperty("truncated").ValueKind != JsonValueKind.False
                        || display.GetProperty("truncated").ValueKind != JsonValueKind.False
                        || Text(display, "type") != "file") return [];
                    var start = display.GetProperty("lineStart").GetInt32(); var end = display.GetProperty("lineEnd").GetInt32();
                    var text = Text(display, "text"); var path = Relative(root, Text(display, "path"));
                    if (path is null || path != Relative(root, Text(input, "filePath")) || !seen.Add(path)
                        || start < 1 || end < start || (long)end - start + 1 > 20
                        || display.GetProperty("totalLines").GetInt32() != end
                        || (input.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 1) != start
                        || (input.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 2000) < (long)end - start + 1
                        || !HandoffValidation.Text(text, 512) || text!.Contains('\r')
                        || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))
                        || text.Split('\n').Length != end - start + 1 || ProjectTools.Redact(text) != text
                        || Text(metadata, "preview") != text) return [];
                    result.Add(new(path, start, end, text)); // Entire bounded text is the literal anchor; never derive it locally.
                }
            }
            return result.ToImmutable();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or FormatException or OverflowException or NotSupportedException or IOException)
        { return []; } // Extraction failure cannot rewrite an independently established Completed result.
    }
    static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    static bool UniqueProperties(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count()
            && value.EnumerateObject().All(p => UniqueProperties(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(UniqueProperties),
        _ => true
    };
    static string? Relative(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl)
            || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('?') || path.Contains('*')) return null;
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.'))
            || path.Contains(':') && (!OperatingSystem.IsWindows() || path.Length < 3 || path[1] != ':' || path[2] is not ('/' or '\\') || path[2..].Contains(':'))
            || Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)) return null;
        var full = Path.GetFullPath(path, root);
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        return relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            || !HandoffValidation.Text(relative, 160) ? null : relative;
    }
}
