using System.ComponentModel;
using System.Text;
using MAF.Core.Tools;

namespace MAF.Agents.Review;

/// <summary>Per-run snapshot. The model cannot expand the filesystem or execution boundary.</summary>
public sealed class ReviewContext
{
    public Dictionary<string, SourceDocument> Documents { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<int>> ChangedLines { get; } = new(StringComparer.Ordinal);
    public List<string> Limitations { get; } = [];
    private readonly int limit;
    private readonly object outputLock = new();
    private int gathered;
    private int returned;

    public ReviewContext(int limit)
    {
        this.limit = limit;
    }

    public string Bound(string content)
    {
        var remaining = Math.Max(0, limit - gathered);
        gathered += Math.Min(remaining, content.Length);
        if (content.Length <= remaining)
        {
            return content;
        }
        Limitations.Add("Context or diff exceeds the character budget.");
        return content[..remaining] + "\n[truncated]";
    }

    public void AddDocument(SourceDocument document)
    {
        var numbered = string.Join('\n', document.Lines.Select((line, index) => $"{document.File}:{index + 1}: {line}"));
        var bounded = Bound(numbered);
        // Admit only complete lines; original line numbers remain stable.
        var count = bounded == numbered ? document.Lines.Count : bounded.Count(c => c == '\n') - 1;
        Documents[document.File] = new(document.File, document.Lines.Take(Math.Max(0, count)).ToArray());
    }

    [Description("Read admitted, sanitized snapshot context with original line numbers. No filesystem access. Output budget is shared across calls.")]
    public string ReadContext(string file, int startLine = 1, int maxLines = 100)
    {
        if (startLine < 1 || maxLines < 1 || maxLines > 200 || !Documents.TryGetValue(file, out var document))
        {
            return "Context unavailable or invalid range.";
        }
        return ReturnBounded(string.Join('\n', document.Lines.Skip(startLine - 1).Take(maxLines)
            .Select((line, index) => $"{file}:{startLine + index}: {line}")));
    }

    [Description("Search admitted sanitized snapshot context for a literal keyword. Up to 50 hits; no regex or filesystem access.")]
    public string SearchContext(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword) || keyword.Length > 200)
        {
            return "Invalid keyword.";
        }
        var result = new StringBuilder();
        var count = 0;
        foreach (var document in Documents.Values)
        {
            for (var i = 0; i < document.Lines.Count; i++)
            {
                if (document.Lines[i].Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    result.AppendLine($"{document.File}:{i + 1}: {document.Lines[i][..Math.Min(500, document.Lines[i].Length)]}");
                    if (++count == 50)
                    {
                        return ReturnBounded(result.ToString());
                    }
                }
            }
        }
        return ReturnBounded(result.Length == 0 ? "No matches in admitted context." : result.ToString());
    }

    private string ReturnBounded(string text)
    {
        lock (outputLock)
        {
            var remaining = Math.Max(0, limit - returned);
            returned += Math.Min(remaining, text.Length);
            if (text.Length <= remaining)
            {
                return text;
            }
            Limitations.Add("Snapshot tool output budget exhausted.");
            return text[..remaining] + "\n[truncated]";
        }
    }
}
