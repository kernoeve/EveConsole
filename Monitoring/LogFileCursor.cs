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
    /// The files in a log folder that could still be being written to.
    ///
    /// <para>⚠️ Found by matching the date EVE puts in the filename, NOT by listing the folder
    /// and filtering on the write time. The two give the same answer, but the cost is nothing
    /// alike: these folders accumulate a file per channel per character per session and reach
    /// tens of thousands of entries, and they are routinely on OneDrive or a network share.
    /// Listing all of them every few seconds to keep the handful being appended to stalls the
    /// machine for seconds at a time — worst exactly while the game is running, because that is
    /// when the share is busiest. Passing the date as a wildcard makes the filesystem (or the
    /// SMB server) do the filtering and returns single digits of entries.</para>
    ///
    /// <para>EVE names log files <c>&lt;name&gt;_yyyyMMdd_HHmmss[_charId].txt</c>. Yesterday is
    /// included as well as today so a tail window that crosses midnight is not cut off, and both
    /// the local and the UTC date are tried because the two importers do not agree on which the
    /// client uses. Write time is still checked afterwards — by then it costs nothing.</para>
    ///
    /// <para>History imports must keep enumerating the folder in full: they exist precisely to
    /// reach files this does not return.</para>
    /// </summary>
    public static List<FileInfo> RecentFiles(string dir, DateTime cutoffUtc)
    {
        var info  = new DirectoryInfo(dir);
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();

        foreach (var stamp in DateStamps())
            foreach (var f in info.EnumerateFiles($"*_{stamp}_*.txt"))
                if (f.LastWriteTimeUtc >= cutoffUtc && seen.Add(f.FullName))
                    files.Add(f);

        return files;
    }

    /// <summary>Today and yesterday, in both local and UTC terms — at most three distinct days,
    /// usually two.</summary>
    private static IEnumerable<string> DateStamps()
    {
        var stamps = new HashSet<string>(StringComparer.Ordinal);
        var utc    = DateTime.UtcNow;
        var local  = DateTime.Now;

        for (var back = 0; back <= 1; back++)
        {
            stamps.Add(utc.AddDays(-back).ToString("yyyyMMdd"));
            stamps.Add(local.AddDays(-back).ToString("yyyyMMdd"));
        }

        return stamps;
    }

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
