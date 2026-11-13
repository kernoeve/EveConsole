using System.Globalization;
using System.Text.RegularExpressions;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>
/// Follow-up lines: a line with no system of its own ("+3", "=12", "nv", "Tester Loki", "heading
/// XQ1-Z2", "clr") adds to a report already made, and becomes a report of its own that restates it.
///
/// <para>Which report, in this order:</para>
/// <list type="number">
/// <item>The same reporter's latest sighting in the same channel within the follow-up window
/// (<see cref="KeyWindow"/>, 180 s by default). Someone typing "+2" a minute after their own report
/// is adding to it.</item>
/// <item>Otherwise, when the line names a pilot, the latest sighting holding that pilot within the
/// named lifetime (15 min by default), from chat or a pasted Local: their current report, whoever
/// made it.</item>
/// <item>Otherwise nothing. The line stays unattached and is not stored as intel: a report with no
/// system would break everything that reads intel by system.</item>
/// </list>
///
/// <para>⚠️ The parent is always BEFORE the line, in time and in the order lines are parsed, never
/// after. So a line gets the same parent whenever it is parsed: live, a minute late, or in a
/// re-parse of the whole history. A thread whose system was called clear after the parent, or
/// whose pilot has since been on a kill, is over: the line does not revive it.</para>
///
/// <para>The new report restates the latest report of the thread with the additions: the same
/// system, pilots merged, "+N" added to the unnamed count, "=N" setting the total, flags and no
/// visual added, the heading from the follow-up. It supersedes what it restates, so whatever reads
/// the newest report for a system or a pilot just works. A clear word makes a clear row on the
/// thread's system instead.</para>
///
/// <para><see cref="IntelReport.ParentReportId"/> always holds the thread's FIRST report, never a
/// follow-up, so a thread is one level deep however long it runs.</para>
///
/// <para>One instance per parse batch: it holds the reports stored around the batch's lines and
/// every report the batch has made so far, saved or not.</para>
/// </summary>
public sealed class IntelThreads
{
    // ── The shared setting ───────────────────────────────────────────────────

    /// <summary>How long after a reporter's own report a line of theirs with no system still adds to it, in
    /// seconds. Shared in the database, like the lifetimes.</summary>
    public const string KeyWindow = "intel.followup_seconds";

    public const int DefaultWindowSeconds = 180;
    public const int MinWindowSeconds     = 30;
    public const int MaxWindowSeconds     = 600;

    /// <summary>The follow-up window as the database holds it now, clamped; the default when unset or unreadable.</summary>
    public static async Task<TimeSpan> WindowAsync(AppDbContext db, CancellationToken ct = default)
    {
        var text = await db.AppPreferences.AsNoTracking()
            .Where(p => p.Key == KeyWindow).Select(p => p.Value).FirstOrDefaultAsync(ct);
        var seconds = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : DefaultWindowSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, MinWindowSeconds, MaxWindowSeconds));
    }

    // ── What the batch knows ─────────────────────────────────────────────────

    /// <summary>A report the batch knows: stored before it, or made by it.</summary>
    public sealed class Entry
    {
        internal Entry(IntelReport report, IReadOnlyList<IntelReportCharacter> pilots, object? head, long order)
        {
            Report = report;
            Pilots = pilots;
            Head   = head;
            Order  = order;
        }

        public IntelReport                         Report { get; }
        public IReadOnlyList<IntelReportCharacter> Pilots { get; }

        /// <summary>The thread's first report: its id (boxed) once stored, the entry itself while it is not,
        /// or null when this entry is the first.</summary>
        internal object? Head { get; }

        /// <summary>The order the batch came to know it: a stored report by its id, then each new one after.</summary>
        internal long Order { get; }

        internal object HeadKey => Head ?? this;
    }

    private readonly List<Entry> _entries = [];

    /// <summary>New reports come after every stored one, whatever their ids turn out to be.</summary>
    private long _nextOrder = (long)int.MaxValue + 1;

    /// <summary>Follow-ups whose first report is new in this batch: its id is known only once it is saved.</summary>
    private readonly List<(IntelReport Child, IntelReport Head)> _unsavedHeads = [];

    /// <summary>Stored reports a new restatement replaces, to mark superseded after the save.</summary>
    private readonly List<int> _restatedStored = [];

    /// <summary>
    /// The reports stored between <paramref name="since"/> and <paramref name="until"/> (ISO text), with
    /// their pilots: everything a line of the batch could follow. Empty when the times cannot be read.
    /// </summary>
    public static async Task<IntelThreads> LoadAsync(AppDbContext db, string since, string until, CancellationToken ct = default)
    {
        var threads = new IntelThreads();
        if (since.Length == 0 || until.Length == 0) return threads;

        // ⚠️ Compared as text on both engines: every intel time column is ISO text.
        var reports = await db.IntelReports.AsNoTracking()
            .Where(r => string.Compare(r.ReportedAt, since) >= 0 && string.Compare(r.ReportedAt, until) <= 0)
            .ToListAsync(ct);
        if (reports.Count == 0) return threads;

        var pilots = (await (from c in db.IntelReportCharacters.AsNoTracking()
                             join r in db.IntelReports.AsNoTracking() on c.IntelReportId equals r.Id
                             where string.Compare(r.ReportedAt, since) >= 0 && string.Compare(r.ReportedAt, until) <= 0
                             select c).ToListAsync(ct))
            .ToLookup(c => c.IntelReportId);

        foreach (var r in reports.OrderBy(r => r.Id))
            threads.AddStored(r, [.. pilots[r.Id]]);
        return threads;
    }

    /// <summary>A report stored before this batch.</summary>
    public void AddStored(IntelReport report, IReadOnlyList<IntelReportCharacter> pilots) =>
        _entries.Add(new Entry(report, pilots, report.ParentReportId ?? report.Id, report.Id));

    /// <summary>A report this batch made from a line with a system of its own: it starts a thread.</summary>
    public Entry AddNew(IntelReport report, IReadOnlyList<IntelReportCharacter> pilots)
    {
        var entry = new Entry(report, pilots, null, _nextOrder++);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// A follow-up this batch made from <paramref name="basis"/> (see <see cref="Restate"/>). Points it at the
    /// thread's first report, and marks the report it restates superseded: the new one says everything
    /// that one did. A clear supersedes nothing here; the clear itself retires the system's sightings.
    /// </summary>
    public Entry AddFollowUp(Entry basis, IntelReport report, IReadOnlyList<IntelReportCharacter> pilots, DateTimeOffset now)
    {
        var head = basis.HeadKey;
        if (head is int id) report.ParentReportId = id;
        else _unsavedHeads.Add((report, ((Entry)head).Report));

        if (report.Kind == IntelReportKind.Sighting && !basis.Report.Obsolete)
        {
            if (basis.Report.Id > 0) _restatedStored.Add(basis.Report.Id);
            else { basis.Report.Obsolete = true; basis.Report.ObsoleteSetOn = now; }
        }

        var entry = new Entry(report, pilots, head, _nextOrder++);
        _entries.Add(entry);
        return entry;
    }

    // ── Finding the parent ───────────────────────────────────────────────────

    /// <summary>
    /// The first rule: the reporter's own latest sighting in this channel, at most <paramref name="window"/>
    /// before <paramref name="at"/>. Null when there is none, or when its system has been called clear since.
    /// </summary>
    public Entry? BySameReporter(string channel, string reporter, string at, TimeSpan window)
    {
        var since = Shift(at, -window);
        if (since is null) return null;

        var latest = Latest(_entries.Where(e =>
            e.Report.Kind == IntelReportKind.Sighting
            && string.Equals(e.Report.ChannelName,  channel,  StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Report.ReporterName, reporter, StringComparison.OrdinalIgnoreCase)
            && Within(e.Report.ReportedAt, since, at)));

        return latest is null || ClearedSince(latest, at) ? null : latest;
    }

    /// <summary>
    /// The second rule: the latest sighting from chat or a pasted Local holding one of <paramref name="pilots"/>,
    /// at most <paramref name="lifetime"/> (the named lifetime) before <paramref name="at"/>. Null when there is
    /// none; when its system has been called clear since; or when one of its pilots named on the line has been on
    /// a kill since (<paramref name="kills"/>, by <see cref="KillTimesAsync"/>), which is newer news of them.
    /// </summary>
    public Entry? ByPilot(IReadOnlyCollection<long> pilots, string at, TimeSpan lifetime,
                          IReadOnlyDictionary<long, List<string>> kills)
    {
        if (pilots.Count == 0) return null;
        var since = Shift(at, -lifetime);
        if (since is null) return null;

        var latest = Latest(_entries.Where(e =>
            e.Report.Kind == IntelReportKind.Sighting
            && e.Report.Source is IntelReportSource.Chat or IntelReportSource.LocalPaste
            && Within(e.Report.ReportedAt, since, at)
            && e.Pilots.Any(p => pilots.Contains(p.CharacterId))));
        if (latest is null || ClearedSince(latest, at)) return null;

        // A kill in the same second as the report wins it, as it does for where a pilot was last seen.
        var killed = latest.Pilots.Where(p => pilots.Contains(p.CharacterId))
            .Any(p => kills.TryGetValue(p.CharacterId, out var times)
                   && times.Any(t => Within(t, latest.Report.ReportedAt, at)));
        return killed ? null : latest;
    }

    /// <summary>The latest sighting of <paramref name="parent"/>'s thread up to <paramref name="at"/>: what a
    /// follow-up restates. The parent itself when nothing has been added to it since.</summary>
    public Entry Newest(Entry parent, string at)
    {
        var key = parent.HeadKey;
        return Latest(_entries.Where(e =>
                   e.Report.Kind == IntelReportKind.Sighting && Equals(e.HeadKey, key)
                   && string.CompareOrdinal(e.Report.ReportedAt, at) <= 0))
               ?? parent;
    }

    private static Entry? Latest(IEnumerable<Entry> entries) =>
        entries.OrderByDescending(e => e.Report.ReportedAt, StringComparer.Ordinal).ThenByDescending(e => e.Order).FirstOrDefault();

    /// <summary>Whether the parent's system was called clear after it, up to <paramref name="at"/>.</summary>
    private bool ClearedSince(Entry parent, string at) =>
        _entries.Any(e =>
            e.Report.Kind == IntelReportKind.Clear && e.Report.SystemId == parent.Report.SystemId
            && string.CompareOrdinal(e.Report.ReportedAt, at) <= 0
            && (string.CompareOrdinal(e.Report.ReportedAt, parent.Report.ReportedAt) > 0
                || (e.Report.ReportedAt == parent.Report.ReportedAt && e.Order > parent.Order)));

    private static bool Within(string time, string from, string to) =>
        string.CompareOrdinal(time, from) >= 0 && string.CompareOrdinal(time, to) <= 0;

    /// <summary>An ISO time moved by <paramref name="by"/>, in the same form; null when it cannot be read.</summary>
    public static string? Shift(string iso, TimeSpan by) =>
        LiveIntelService.TryParseReported(iso, out var at) ? IntelLastSeenWriter.Format(at + by) : null;

    // ── The restated report ──────────────────────────────────────────────────

    /// <summary>
    /// The report a follow-up makes from the latest report of its thread (<paramref name="basis"/>, from
    /// <see cref="Newest"/>). <paramref name="line"/> carries the line's own facts: the message and its id, the
    /// time, the channel and the reporter; the rest is filled here. <paramref name="named"/> are the pilots the
    /// line names, resolved. Nothing is changed; <see cref="AddFollowUp"/> takes the result.
    /// </summary>
    public static (IntelReport Report, List<IntelReportCharacter> Pilots) Restate(
        Entry basis, IntelRules.ParsedFollowUp followUp, IReadOnlyList<IntelReportCharacter> named,
        IntelReport line, int? headingSystemId)
    {
        var b = basis.Report;
        line.SystemId   = b.SystemId;
        line.SystemName = b.SystemName;
        line.Source     = IntelReportSource.Chat;
        line.Obsolete   = false;
        line.Note       = string.IsNullOrWhiteSpace(followUp.Note) ? null : followUp.Note;

        if (followUp.Clear)
        {
            // The thread's system, called clear by someone who was watching it.
            line.Kind        = IntelReportKind.Clear;
            line.PlayerCount = 0;
            line.NoVisual    = followUp.NoVisual;
            return (line, []);
        }

        // The pilots of the report, then the ones the line adds. A hull the line gives a pilot replaces
        // the one they had: it is the newer word on what they fly.
        var pilots = basis.Pilots.Select(Copy).ToList();
        foreach (var p in named)
        {
            var known = pilots.FindIndex(x => x.CharacterId == p.CharacterId);
            if (known < 0) pilots.Add(Copy(p));
            else if (p.ShipTypeId is not null || p.ShipName is not null)
            {
                pilots[known].ShipTypeId = p.ShipTypeId;
                pilots[known].ShipName   = p.ShipName;
            }
        }

        // Ships: the larger number of each hull. A follow-up naming a hull the report already lists is
        // nearly always the same ships again ("Loki" after "QZ-X77 Loki"), not more of them.
        var ships = ReadShips(b.Ships);
        foreach (var s in followUp.Ships)
        {
            var known = ships.FindIndex(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase));
            if (known < 0) ships.Add((s.Name, s.Count));
            else if (s.Count > ships[known].Count) ships[known] = (ships[known].Name, s.Count);
        }

        // The people nobody named stay as many as the report had, plus the line's "+N"; "=N" sets the whole.
        var unnamed = Math.Max(0, b.PlayerCount - basis.Pilots.Count);
        var count = followUp.Total > 0
            ? Math.Max(followUp.Total, pilots.Count)
            : Math.Max(pilots.Count + unnamed + followUp.Plus, ships.Sum(s => s.Count));

        line.Kind            = IntelReportKind.Sighting;
        line.PlayerCount     = count;
        line.Flags           = b.Flags | (int)followUp.Flags;
        line.NoVisual        = b.NoVisual || followUp.NoVisual;
        line.Gate            = b.Gate;
        line.Ships           = FormatShips(ships);
        line.FromSystemId    = b.FromSystemId;
        line.HeadingSystemId = headingSystemId ?? b.HeadingSystemId;
        line.Note            = JoinNotes(b.Note, followUp.Note);
        return (line, pilots);
    }

    private static IntelReportCharacter Copy(IntelReportCharacter p) => new()
    {
        CharacterId   = p.CharacterId,
        CharacterName = p.CharacterName,
        ShipTypeId    = p.ShipTypeId,
        ShipName      = p.ShipName,
    };

    /// <summary>The report's note and the line's, once each: a restatement says what the report said too.</summary>
    private static string? JoinNotes(string? report, string line)
    {
        var parts = new[] { report?.Trim(), line.Trim() }.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal);
        var joined = string.Join(' ', parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>"N× Name" in <see cref="IntelReport.Ships"/>.</summary>
    private static readonly Regex ShipRx = new(@"^(?<n>\d+)× (?<name>.+)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>The ships of a report, as <see cref="FormatShips"/> wrote them.</summary>
    public static List<(string Name, int Count)> ReadShips(string? text)
    {
        var ships = new List<(string Name, int Count)>();
        if (string.IsNullOrWhiteSpace(text)) return ships;
        foreach (var part in text.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            ships.Add(ShipRx.Match(part) is { Success: true } m && int.TryParse(m.Groups["n"].Value, CultureInfo.InvariantCulture, out var n)
                ? (m.Groups["name"].Value, n) : (part, 1));
        return ships;
    }

    /// <summary>
    /// Ships as a reader reads them: "3× Loki, Interdictor". The same hull named twice is two of it:
    /// "Loki  Loki  Sabre" is "2× Loki, Sabre". Null when there are none.
    /// </summary>
    public static string? FormatShips(IEnumerable<(string Name, int Count)> ships)
    {
        var grouped = ships.GroupBy(s => s.Name).Select(g => (Name: g.Key, Count: g.Sum(s => s.Count))).ToList();
        return grouped.Count == 0 ? null
            : string.Join(", ", grouped.Select(s => s.Count > 1 ? $"{s.Count}× {s.Name}" : s.Name));
    }

    // ── After the save ───────────────────────────────────────────────────────

    /// <summary>
    /// Points the follow-ups whose first report was new in this batch at it, now that it has an id. Call after
    /// the reports are saved and before the change tracker is cleared: the follow-ups are still tracked, and the
    /// next save writes the change.
    /// </summary>
    public void LinkSaved()
    {
        foreach (var (child, head) in _unsavedHeads)
            child.ParentReportId = head.Id;
        _unsavedHeads.Clear();
    }

    /// <summary>Marks the stored reports this batch restated as superseded.</summary>
    public async Task SupersedeRestatedAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        if (_restatedStored.Count == 0) return;
        var ids = _restatedStored.Distinct().ToList();
        _restatedStored.Clear();
        await db.IntelReports
            .Where(r => ids.Contains(r.Id) && !r.Obsolete)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Obsolete, true)
                                      .SetProperty(r => r.ObsoleteSetOn, now), ct);
    }

    // ── Kills ────────────────────────────────────────────────────────────────

    private sealed class KillSeen
    {
        public long           CharacterId  { get; set; }
        public DateTimeOffset KillMailTime { get; set; }
    }

    /// <summary>
    /// When each of <paramref name="characters"/> was on a kill, attacking or losing a ship, between
    /// <paramref name="from"/> and <paramref name="to"/>, as ISO text: what <see cref="ByPilot"/> checks a pilot's
    /// report against.
    ///
    /// <para>⚠️ The times are compared in SQL as the kill table's own type, bound as values, and only then turned
    /// into text: the column is stored differently on each engine, so a text bound would compare on neither.</para>
    /// </summary>
    public static async Task<Dictionary<long, List<string>>> KillTimesAsync(
        AppDbContext db, IReadOnlyCollection<long> characters, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var result = new Dictionary<long, List<string>>();
        if (characters.Count == 0) return result;

        // ⚠️ Ids are longs from our own name cache, never text anyone typed, so they are written into the
        // statement rather than bound one parameter each, as the supersede by kills does.
        var ids = string.Join(",", characters.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var sql = $$"""
            SELECT a."CharacterId" AS "CharacterId", d."KillMailTime" AS "KillMailTime"
            FROM "KillMailAttackers" a
            JOIN "KillMailDetails" d ON d."KillMailId" = a."KillMailId"
            WHERE a."CharacterId" IN ({{ids}}) AND d."KillMailTime" >= {0} AND d."KillMailTime" <= {1}
            UNION ALL
            SELECT d."VictimCharId" AS "CharacterId", d."KillMailTime" AS "KillMailTime"
            FROM "KillMailDetails" d
            WHERE d."VictimCharId" IN ({{ids}}) AND d."KillMailTime" >= {0} AND d."KillMailTime" <= {1}
            """;
        foreach (var k in await db.Database.SqlQueryRaw<KillSeen>(sql, from, to).ToListAsync(ct))
        {
            if (!result.TryGetValue(k.CharacterId, out var list)) result[k.CharacterId] = list = [];
            list.Add(IntelLastSeenWriter.Format(k.KillMailTime));
        }
        return result;
    }
}
