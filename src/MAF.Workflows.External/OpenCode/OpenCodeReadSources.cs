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
            var seen = new Dictionary<string, (int Start, int End, int Total, bool Truncated, string Text)>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var partIds = new HashSet<string>(StringComparer.Ordinal);
            var callIds = new HashSet<string>(StringComparer.Ordinal);
            var toolCount = 0;
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
                    if (type != "tool" || ++toolCount > 16
                        || Encoding.UTF8.GetByteCount(part.GetRawText()) > 16384 || !UniqueProperties(part)
                        || Text(part, "sessionID") != handle.ExternalSessionId || Text(part, "messageID") != Text(info, "id")
                        || !HandoffValidation.Text(Text(part, "id"), 160) || !HandoffValidation.Text(Text(part, "callID"), 160)
                        || !partIds.Add(Text(part, "id")!) || !callIds.Add(Text(info, "id") + ":" + Text(part, "callID"))) return [];
                    var state = part.GetProperty("state"); var input = state.GetProperty("input");
                    var time = state.GetProperty("time"); var metadata = state.GetProperty("metadata");
                    var tool = Text(part, "tool");
                    if (Text(state, "status") != "completed" || !HandoffValidation.Text(Text(state, "output"), 16384)
                        || Text(state, "title") is not { Length: <= 1024 } || state.TryGetProperty("error", out _)
                        || time.TryGetProperty("compacted", out _) || time.GetProperty("start").GetInt64() < 0
                        || time.GetProperty("end").GetInt64() < time.GetProperty("start").GetInt64()) return [];
                    if (tool is "grep" or "glob")
                    {
                        if (!Unrelated(tool, input, metadata, state, root)) return [];
                        continue; // Never derive source candidates from grep/glob output.
                    }
                    if (tool != "read" || !HandoffValidation.Text(Text(state, "title"), 1024)) return [];
                    var display = metadata.GetProperty("display");
                    var truncated = metadata.GetProperty("truncated").GetBoolean();
                    if (display.GetProperty("truncated").GetBoolean() != truncated || Text(display, "type") != "file") return [];
                    var start = display.GetProperty("lineStart").GetInt32(); var end = display.GetProperty("lineEnd").GetInt32();
                    var text = Text(display, "text"); var path = Relative(root, Text(display, "path"));
                    var total = display.GetProperty("totalLines").GetInt32();
                    if (path is null || path != Relative(root, Text(input, "filePath"))
                        || start < 1 || end < start || total < end || (truncated ? total <= end : total != end)
                        || (input.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 1) != start
                        || (input.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 2000) < (long)end - start + 1
                        || !HandoffValidation.Text(text, 16384) || text!.Contains('\r')
                        || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))
                        || ProjectTools.Redact(text) != text) return [];
                    var lines = text.Split('\n');
                    if (lines.Length != (long)end - start + 1 || Text(metadata, "preview") != string.Join('\n', lines.Take(20))) return [];
                    var observation = (start, end, total, truncated, text);
                    if (seen.TryGetValue(path, out var previous))
                    {
                        if (previous != observation) return []; // Compare the ENTIRE display, including unselected lines.
                        continue;
                    }
                    if (result.Count == 8) return [];
                    seen.Add(path, observation);
                    var chosen = 0; var length = 0;
                    while (chosen < Math.Min(20, lines.Length) && (long)length + lines[chosen].Length + (chosen == 0 ? 0 : 1) <= 512)
                    { length += lines[chosen].Length + (chosen == 0 ? 0 : 1); chosen++; }
                    if (chosen == 0) return []; // Never skip a long first line to select a favorable later range.
                    var anchor = string.Join('\n', lines.Take(chosen));
                    if (!HandoffValidation.Text(anchor, 512)) return [];
                    var selectedEnd = start + chosen - 1;
                    result.Add(new(path, start, selectedEnd, anchor)
                    {
                        CoverageLimitation = truncated || selectedEnd != end
                            ? $"Source excerpt {start}-{selectedEnd} only; provider display {start}-{end}, provider-truncated={truncated.ToString().ToLowerInvariant()}. Other lines remain unverified."
                            : null
                    });
                }
            }
            return result.ToImmutable();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or FormatException or OverflowException or NotSupportedException or IOException)
        { return []; } // Extraction failure cannot rewrite an independently established Completed result.
    }
    // Exact completed non-source shapes from the same pinned tools; no generic tool whitelist.
    static bool Unrelated(string tool, JsonElement input, JsonElement metadata, JsonElement state, string root)
    {
        var countName = tool == "grep" ? "matches" : "count";
        if (state.TryGetProperty("attachments", out _) || !HandoffValidation.Text(Text(input, "pattern"), 200)
            || input.EnumerateObject().Any(p => p.Name is not ("pattern" or "path") && !(tool == "grep" && p.Name == "include"))
            || metadata.EnumerateObject().Any(p => p.Name != countName && p.Name != "truncated")
            || metadata.GetProperty(countName).GetInt32() is < 0 or > 100
            || metadata.GetProperty("truncated").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || metadata.GetProperty("truncated").GetBoolean() != (metadata.GetProperty(countName).GetInt32() == 100)) return false;
        if (input.TryGetProperty("include", out _) && !HandoffValidation.Text(Text(input, "include"), 200)) return false;
        if (!input.TryGetProperty("path", out _)) return true;
        var path = Text(input, "path");
        return path is not null && (path == root || Relative(root, path) is not null);
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
