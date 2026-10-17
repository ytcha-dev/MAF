using System.Security.Cryptography;
using System.Text;

namespace MAF.Core.Tools;

public sealed partial class ProjectTools
{
    internal sealed class SourceChangedException : IOException;
    internal sealed record SourceSnapshot(string Path, string Content, string RawDigest, long Length, long Written, long Created, bool Redacted);
    internal SourceSnapshot ReadSourceSnapshot(string relative, int start, int end)
    {
        _ = new ProjectTools(Root); // Recheck root ancestors, not just the final path.
        var path = Resolve(relative);
        var before = new FileInfo(path); var length = before.Length; var written = before.LastWriteTimeUtc.Ticks; var created = before.CreationTimeUtc.Ticks;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[MaxBytes + 1]; var count = 0;
        while (count < bytes.Length) { var n = stream.Read(bytes, count, bytes.Length - count); if (n == 0) break; count += n; }
        if (count > MaxBytes || bytes.AsSpan(0, count).Contains((byte)0)) throw new UnauthorizedAccessException("InvalidSourceFile");
        var text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new UnauthorizedAccessException("InvalidSourceFile");
        _ = new ProjectTools(Root); _ = Resolve(relative);
        var after = new FileInfo(path);
        if (length != count || after.Length != length || after.LastWriteTimeUtc.Ticks != written || after.CreationTimeUtc.Ticks != created)
            throw new SourceChangedException();
        // Do not let multiline redaction alter original line numbering or silently pass as verified.
        var redacted = Redact(text) != text;
        if (redacted) return new("", "", "", length, written, created, true);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Length == 0 ? [] : text.Split('\n');
        var actualCount = lines.Length - (text.EndsWith('\n') ? 1 : 0);
        if (start < 1 || end < start || end > actualCount) throw new ArgumentOutOfRangeException(nameof(start));
        return new(Path.GetRelativePath(Root, path).Replace('\\', '/'), string.Join('\n', lines.Skip(start - 1).Take(end - start + 1)),
            Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, count))), length, written, created, false);
    }
}
