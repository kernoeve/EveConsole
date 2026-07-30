using System.Globalization;
using System.Text.RegularExpressions;

namespace EveConsole.Monitoring;

/// <summary>
/// Parsing for EVE chat log files, which are a different format from game logs.
///
/// ─── Verified against real chat logs ─────────────────────────────────────────
/// Encoding is UTF-16LE (game logs are UTF-8), and every message line begins with
/// its own U+FEFF — not just the file's BOM. Verified on a real file where all
/// 1,815 message lines carried one. Strip it per line or nothing matches.
///
/// Header:
///         ---------------------------------------------------------------
///           Channel ID:      local
///           Channel Name:    Local
///           Listener:        Baltazar V
///           Session started: 2026.07.27 20:01:30
///         ---------------------------------------------------------------
///
/// Messages:  [ 2026.07.27 20:01:33 ] Sender Name > message text
/// System:    [ 2026.07.27 20:01:33 ] EVE System > Channel changed to Local : ZD1-Z2
///
/// Channel ID is "local" for Local, or "player_&lt;guid&gt;" for player-made channels.
///
/// NOT present, checked across 150 Local files (only 9 distinct EVE System shapes,
/// all channel-change or chat-server status): joins, leaves, or any member list.
/// "Who is in Local" is genuinely not obtainable from these files.
/// ─────────────────────────────────────────────────────────────────────────────
/// </summary>
public static class ChatLogRules
{
    private const RegexOptions Opts =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>The sender name EVE uses for its own messages.</summary>
    public const string SystemSender = "EVE System";

    /// <summary>"[ 2026.07.27 20:01:33 ] Sender > text". Sender is non-greedy so a
    /// "&gt;" inside the message body doesn't get treated as the separator.</summary>
    private static readonly Regex MessageRx = new(
        @"^\[\s*(?<ts>\d{4}\.\d{2}\.\d{2}\s+\d{2}:\d{2}:\d{2})\s*\]\s*(?<sender>.+?)\s*>\s?(?<text>.*)$",
        Opts);

    private static readonly Regex ChannelIdRx   = new(@"^\s*Channel ID:\s*(?<v>.+?)\s*$",   Opts);
    private static readonly Regex ChannelNameRx = new(@"^\s*Channel Name:\s*(?<v>.+?)\s*$", Opts);
    private static readonly Regex ListenerRx    = new(@"^\s*Listener:\s*(?<v>.+?)\s*$",     Opts);

    /// <summary>"Channel changed to Local : ZD1-Z2" — the only system message that
    /// carries usable state, giving the character's current solar system.</summary>
    private static readonly Regex ChannelChangedRx = new(
        @"^Channel\s+changed\s+to\s+.+?\s*:\s*(?<system>.+?)\s*$", Opts);

    public sealed record ParsedMessage(
        DateTimeOffset? Timestamp,
        string          Sender,
        string          Text,
        bool            IsSystem,
        string?         SystemName);

    /// <summary>Channel name from the filename — <c>&lt;Channel&gt;_&lt;date&gt;_&lt;time&gt;_&lt;charId&gt;.txt</c>.
    /// Read from the filename rather than the header so the allowlist can be applied
    /// without opening the file at all.</summary>
    public static string ChannelNameFromFile(string path)
    {
        var name  = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_');

        // Trailing parts are date, time and (usually) character id. Channel names may
        // themselves contain underscores, so strip from the right.
        var drop = 0;
        if (parts.Length >= 3 && long.TryParse(parts[^1], out _)) drop = 3;
        else if (parts.Length >= 2)                               drop = 2;

        return drop > 0 && parts.Length > drop
            ? string.Join('_', parts[..^drop])
            : name;
    }

    public static string? TryParseChannelId(string line)
        => ChannelIdRx.Match(line) is { Success: true } m ? m.Groups["v"].Value : null;

    public static string? TryParseChannelName(string line)
        => ChannelNameRx.Match(line) is { Success: true } m ? m.Groups["v"].Value : null;

    public static string? TryParseListener(string line)
        => ListenerRx.Match(line) is { Success: true } m ? m.Groups["v"].Value : null;

    /// <summary>Parse one message line. Null for header decoration and blanks.
    /// The caller must have stripped the leading U+FEFF first.</summary>
    public static ParsedMessage? TryParseMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var m = MessageRx.Match(raw);
        if (!m.Success) return null;

        DateTimeOffset? ts = DateTimeOffset.TryParseExact(
            m.Groups["ts"].Value, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed : null;

        var sender   = m.Groups["sender"].Value.Trim();
        var text     = m.Groups["text"].Value;
        var isSystem = sender.Equals(SystemSender, StringComparison.OrdinalIgnoreCase);

        string? systemName = null;
        if (isSystem && ChannelChangedRx.Match(text) is { Success: true } changed)
            systemName = changed.Groups["system"].Value.Trim();

        return new ParsedMessage(ts, sender, text, isSystem, systemName);
    }

    /// <summary>ISO-8601 UTC, lexicographically sortable — see ChatMessage.OccurredAt
    /// for why this is stored as a string.</summary>
    public static string FormatTimestamp(DateTimeOffset ts) =>
        ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
