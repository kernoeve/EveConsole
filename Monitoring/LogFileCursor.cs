using System.Text;

namespace EveConsole.Monitoring;

/// <summary>
/// Incremental reading of an EVE log file that the game client is still writing to.
///
/// Shared by the game log and chat log importers — the mechanics are identical and
/// each one is fiddly enough that having two copies would guarantee they drift:
///   • sharing flags that let the client keep writing and rotating,
///   • encoding sniffing (game logs are UTF-8, chat logs are UTF-16LE),
///   • byte-offset resumption,
///   • stopping at the last complete line so a half-written line isn't parsed.
///
/// Read-only throughout. Nothing here ever writes to an EVE-owned file.
/// </summary>
public static class LogFileCursor
{
    public sealed record ReadResult(IReadOnlyList<string> Lines, long ConsumedBytes);

    /// <summary>
    /// Read everything appended since <paramref name="lastOffset"/>, returning only
    /// complete lines. Null when there is nothing new, or nothing but a partial line.
    ///
    /// ConsumedBytes counts only what was actually consumed, so a partial trailing
    /// line stays unread until the rest of it arrives.
    /// </summary>
    public static async Task<ReadResult?> ReadNewLinesAsync(
        FileInfo fi, long lastOffset, Encoding encoding, CancellationToken ct)
    {
        // FileShare.ReadWrite is mandatory — the client holds the file open for
        // writing. FileShare.Delete lets it rotate without failing our read.
        using var fs = new FileStream(
            fi.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (lastOffset > fs.Length) lastOffset = 0;
        if (lastOffset == fs.Length) return null;

        fs.Seek(lastOffset, SeekOrigin.Begin);

        var buffer = new byte[fs.Length - lastOffset];
        var read   = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
        if (read <= 0) return null;

        var text        = encoding.GetString(buffer, 0, read);
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0) return null;   // nothing but a partial line so far

        var consumed = encoding.GetByteCount(text[..(lastNewline + 1)]);
        var lines    = text[..lastNewline]
                       .Split('\n')
                       .Select(l => l.TrimEnd('\r'))
                       .ToList();

        return new ReadResult(lines, consumed);
    }

    /// <summary>
    /// Sniff the encoding from the byte-order mark. Game logs are UTF-8; chat logs are
    /// UTF-16LE. Guessing wrong decodes an entire file to gibberish, so this is not
    /// optional.
    /// </summary>
    public static Encoding DetectEncoding(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            Span<byte> bom = stackalloc byte[4];
            var n = fs.Read(bom);
            if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
            if (n >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        }
        catch { /* fall through to UTF-8 */ }

        return new UTF8Encoding(false);
    }

    /// <summary>
    /// Strip a leading byte-order mark from a single line.
    ///
    /// Chat logs emit U+FEFF at the start of EVERY message line, not just once at the
    /// top of the file — verified across a real file where all 1,815 message lines
    /// carried one. Without this, no line-level pattern ever matches.
    /// </summary>
    public static string StripLeadingBom(string line) =>
        line.Length > 0 && line[0] == '﻿' ? line[1..] : line;

    /// <summary>Filenames end in <c>…_&lt;characterId&gt;.txt</c>. Older logs omit it.</summary>
    public static long? TryParseCharacterId(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('_');
        return parts.Length >= 3 && long.TryParse(parts[^1], out var id) ? id : null;
    }
}
