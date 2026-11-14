using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>
/// One sighting of one pilot, as handed to <see cref="IntelLastSeenWriter.UpsertAsync"/>: from a
/// chat report, a pasted Local or a killmail.
/// </summary>
public sealed record LastSeenRow
{
    public required long   CharacterId { get; init; }

    /// <summary>ISO text, "yyyy-MM-ddTHH:mm:ssZ", like IntelReports.ReportedAt.</summary>
    public required string SeenAt      { get; init; }

    /// <summary>One of <see cref="IntelLastSeenSource"/>.</summary>
    public required int    Source      { get; init; }

    public required int    SystemId    { get; init; }

    public string? Name       { get; init; }
    public int?    ReportId   { get; init; }
    public int?    KillMailId { get; init; }
    public int?    ShipTypeId { get; init; }
    public string? ShipName   { get; init; }
    public bool    NoVisual   { get; init; }

    public long?   CorporationId { get; init; }
    public long?   AllianceId    { get; init; }

    /// <summary>When the affiliation was true (the kill's time, or when it was pulled); null when
    /// the row carries none.</summary>
    public string? AffiliationAt     { get; init; }

    /// <summary>One of <see cref="IntelAffiliationSource"/>.</summary>
    public int     AffiliationSource { get; init; }

    internal int Rank => IntelLastSeenSource.RankOf(Source);

    /// <summary>What breaks a tie of time and rank: the later report, or the later kill.</summary>
    internal long EventId => ReportId ?? KillMailId ?? 0;
}

/// <summary>
/// The one writer of <see cref="IntelLastSeen"/>: where each pilot was last seen.
///
/// <para>Batched, idempotent and newer-wins. Reports are parsed late and kills arrive late (a
/// backfill can bring last week's), so a write OLDER than what a row holds never moves the pilot
/// back. Writing the same rows twice changes nothing. A tie in the same second goes to the better
/// source (<see cref="IntelLastSeenSource.RankOf"/>), then to the later report or kill.</para>
///
/// <para>Also kept, in the same statement so two PCs writing at once cannot tear it:</para>
/// <list type="bullet">
/// <item>PrevSystemId/PrevSeenAt: where the pilot was before this system, set when a newer
/// sighting is in another system. What tells a pilot moving closer from one sitting still.</item>
/// <item>InSystemSince: when this stay began. Reset by a move, or by a sighting after the pilot
/// was gone longer than a named sighting lasts. An alarm keys a pilot on system + stay, so
/// someone sitting in a system for an hour is one event, not one per report.</item>
/// <item>The affiliation, by its own rule: a kill's affiliation replaces a pulled one whatever its
/// time, and an older kill's when it is newer. A pulled affiliation only fills a row that has none
/// from a kill. A kill shows who the pilot flew for at that moment, which is what a reader of intel
/// wants; a pull can be days stale and is a fallback.</item>
/// <item>WrittenAt: when the row was written, whatever time it describes. Readers take only the
/// rows written since they last looked, and a late kill with an old time is still among them.</item>
/// </list>
///
/// <para>⚠️ The SQL is the same upsert on both engines (SQLite 3.24+, PostgreSQL 9.5+), apart from
/// one date subtraction. PostgreSQL refuses a statement that touches one key twice, so a batch that
/// names a pilot more than once is written in rounds, oldest first, one row per pilot per round:
/// the result is the same as writing them one by one.</para>
/// </summary>
public static class IntelLastSeenWriter
{
    /// <summary>The shared setting for how long a named sighting lasts. Read here for the stay rule.</summary>
    public const string KeyNamedLifetime = "intel.life.named_min";

    /// <summary>The default named lifetime, in minutes, and its bounds.</summary>
    public const int DefaultNamedMinutes = 15;
    public const int MinNamedMinutes     = 1;
    public const int MaxNamedMinutes     = 120;

    /// <summary>Rows per statement: well under both engines' parameter limits (16 a row).</summary>
    private const int RowsPerStatement = 200;

    private const string Iso = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>ISO text of a time, in the form every intel time column holds.</summary>
    public static string Format(DateTimeOffset at) => at.UtcDateTime.ToString(Iso, CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the rows. Returns how many rows the database inserted or changed: 0 when everything
    /// was already known, or older.
    /// </summary>
    /// <param name="stay">How long a pilot may go unseen and still be in the same stay; null reads
    /// the shared named lifetime.</param>
    /// <param name="now">The time written into WrittenAt; null for now.</param>
    public static async Task<int> UpsertAsync(
        AppDbContext db, IEnumerable<LastSeenRow> rows, CancellationToken ct = default,
        TimeSpan? stay = null, DateTimeOffset? now = null)
    {
        var valid = new List<LastSeenRow>();
        foreach (var r in rows)
            if (Normalise(r) is { } n) valid.Add(n);
        if (valid.Count == 0) return 0;

        var gap = stay ?? await NamedLifetimeAsync(db, ct);
        var written = Format(now ?? DateTimeOffset.UtcNow);

        // A pilot named more than once goes in rounds, oldest first: one statement may not touch a
        // key twice on PostgreSQL. The exact same sighting twice is one.
        var byPilot = valid
            .Distinct()
            .GroupBy(r => r.CharacterId)
            .Select(g => g.OrderBy(r => r.SeenAt, StringComparer.Ordinal).ThenBy(r => r.Rank).ThenBy(r => r.EventId).ToList())
            .ToList();

        var total = 0;
        var rounds = byPilot.Max(g => g.Count);
        for (var round = 0; round < rounds; round++)
        {
            // ⚠️ In key order, in every statement: two PCs writing overlapping pilots on PostgreSQL then
            // take the row locks in the same order and cannot deadlock.
            var slice = byPilot.Where(g => g.Count > round).Select(g => g[round]).OrderBy(r => r.CharacterId).ToList();
            for (var i = 0; i < slice.Count; i += RowsPerStatement)
                total += await WriteAsync(db, slice.Skip(i).Take(RowsPerStatement).ToList(), gap, written, ct);
        }
        return total;
    }

    /// <summary>The shared named lifetime, clamped; the default when unset or unreadable.</summary>
    public static async Task<TimeSpan> NamedLifetimeAsync(AppDbContext db, CancellationToken ct = default)
    {
        var text = await db.AppPreferences.AsNoTracking()
            .Where(p => p.Key == KeyNamedLifetime).Select(p => p.Value).FirstOrDefaultAsync(ct);
        var minutes = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : DefaultNamedMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, MinNamedMinutes, MaxNamedMinutes));
    }

    /// <summary>The row with its times in the one ISO form, or null when it cannot be placed.</summary>
    private static LastSeenRow? Normalise(LastSeenRow r)
    {
        if (r.CharacterId <= 0 || r.SystemId <= 0) return null;
        if (IsoText(r.SeenAt) is not { } seen) return null;
        var aff = r.AffiliationAt is null ? null : IsoText(r.AffiliationAt);
        return r with
        {
            SeenAt        = seen,
            AffiliationAt = aff,
            Name          = string.IsNullOrWhiteSpace(r.Name) ? null : r.Name,
            CorporationId = r.CorporationId is > 0 ? r.CorporationId : null,
            AllianceId    = r.AllianceId    is > 0 ? r.AllianceId    : null,
        };
    }

    private static string? IsoText(string text)
    {
        if (DateTimeOffset.TryParseExact(text, Iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _)) return text;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? Format(at) : null;
    }

    private static async Task<int> WriteAsync(
        AppDbContext db, List<LastSeenRow> rows, TimeSpan gap, string writtenAt, CancellationToken ct)
    {
        await using var probe = db.Database.GetDbConnection().CreateCommand();
        var parameters = new List<DbParameter>(rows.Count * 16 + 1);

        // ⚠️ Typed even when null: PostgreSQL will not put a null typed as text into an integer column.
        string P(object? value, DbType type)
        {
            var p = probe.CreateParameter();
            p.ParameterName = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            p.Value = value ?? DBNull.Value;
            p.DbType = type;
            parameters.Add(p);
            return p.ParameterName;
        }

        var written = P(writtenAt, DbType.String);
        var values = new StringBuilder();
        foreach (var r in rows)
        {
            if (values.Length > 0) values.Append(",\n");
            var seen = P(r.SeenAt, DbType.String);
            values.Append('(')
                .Append(P(r.CharacterId, DbType.Int64)).Append(", ")
                .Append(P(r.Name, DbType.String)).Append(", ")
                .Append(seen).Append(", ")
                .Append(P(r.Rank, DbType.Int32)).Append(", ")
                .Append(P(r.Source, DbType.Int32)).Append(", ")
                .Append(P(r.SystemId, DbType.Int32)).Append(", ")
                .Append(P(r.ReportId, DbType.Int32)).Append(", ")
                .Append(P(r.KillMailId, DbType.Int32)).Append(", ")
                .Append(P(r.ShipTypeId, DbType.Int32)).Append(", ")
                .Append(P(r.ShipName, DbType.String)).Append(", ")
                .Append(P(r.NoVisual, DbType.Boolean)).Append(", ")
                .Append(P(r.CorporationId, DbType.Int64)).Append(", ")
                .Append(P(r.AllianceId, DbType.Int64)).Append(", ")
                .Append(P(r.AffiliationAt, DbType.String)).Append(", ")
                .Append(P(r.AffiliationSource, DbType.Int32)).Append(", ")
                .Append("NULL, NULL, ")
                .Append(seen).Append(", ")
                .Append(written).Append(')');
        }

        var sql = Sql(values.ToString(), StayStart((int)gap.TotalSeconds), written);
        return await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
    }

    /// <summary>
    /// The earliest old SeenAt that still continues the stay: the new sighting's time minus the gap,
    /// as ISO text. The one part that differs between the engines.
    /// </summary>
    private static string StayStart(int seconds) => DbEngine.IsSqlite
        ? $"""strftime('%Y-%m-%dT%H:%M:%SZ', excluded."SeenAt", '-{seconds} seconds')"""
        : $"""to_char(((excluded."SeenAt")::timestamptz AT TIME ZONE 'UTC') - interval '{seconds} seconds', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')""";

    private static string Sql(string values, string stayStart, string written)
    {
        const string T = "\"IntelLastSeen\"";
        // Newer: a later time; or the same second from a better source; or the same second and
        // source from a later report or kill (a re-parse writes the same sighting with a new id).
        var newer = $"""
            (excluded."SeenAt" > {T}."SeenAt"
              OR (excluded."SeenAt" = {T}."SeenAt"
                  AND (excluded."SourceRank" > {T}."SourceRank"
                       OR (excluded."SourceRank" = {T}."SourceRank"
                           AND COALESCE(excluded."ReportId", excluded."KillMailId", 0) > COALESCE({T}."ReportId", {T}."KillMailId", 0)))))
            """;
        var moved = $"""(excluded."SystemId" <> {T}."SystemId")""";
        var gone  = $"""({T}."SeenAt" < {stayStart})""";
        // A sighting that names no hull keeps the hull known in the same stay.
        var keepShip = $"""(excluded."ShipTypeId" IS NULL AND excluded."ShipName" IS NULL AND NOT {moved} AND NOT {gone})""";
        // The affiliation: a kill's beats a pulled one, and a newer kill's an older; a pulled one
        // only fills a row with none from a kill.
        var affiliation = $"""
            (excluded."AffiliationAt" IS NOT NULL
              AND ((excluded."AffiliationSource" = {IntelAffiliationSource.Kill}
                    AND ({T}."AffiliationSource" <> {IntelAffiliationSource.Kill} OR {T}."AffiliationAt" IS NULL
                         OR excluded."AffiliationAt" > {T}."AffiliationAt"))
                OR (excluded."AffiliationSource" <> {IntelAffiliationSource.Kill}
                    AND {T}."AffiliationSource" <> {IntelAffiliationSource.Kill}
                    AND ({T}."AffiliationAt" IS NULL OR excluded."AffiliationAt" > {T}."AffiliationAt"))))
            """;
        var nameFill = $"""({T}."Name" IS NULL AND excluded."Name" IS NOT NULL)""";

        string Take(string column) => $"""
            "{column}" = CASE WHEN {newer} THEN excluded."{column}" ELSE {T}."{column}" END
            """;
        string Aff(string column) => $"""
            "{column}" = CASE WHEN {affiliation} THEN excluded."{column}" ELSE {T}."{column}" END
            """;

        return $"""
            INSERT INTO {T} ("CharacterId", "Name", "SeenAt", "SourceRank", "Source", "SystemId", "ReportId",
                "KillMailId", "ShipTypeId", "ShipName", "NoVisual", "CorporationId", "AllianceId", "AffiliationAt",
                "AffiliationSource", "PrevSystemId", "PrevSeenAt", "InSystemSince", "WrittenAt")
            VALUES
            {values}
            ON CONFLICT ("CharacterId") DO UPDATE SET
                "PrevSystemId"  = CASE WHEN {newer} AND {moved} THEN {T}."SystemId" ELSE {T}."PrevSystemId" END,
                "PrevSeenAt"    = CASE WHEN {newer} AND {moved} THEN {T}."SeenAt"   ELSE {T}."PrevSeenAt"   END,
                "InSystemSince" = CASE WHEN {newer} AND ({moved} OR {gone}) THEN excluded."SeenAt"
                                       ELSE COALESCE({T}."InSystemSince", {T}."SeenAt") END,
                {Take("SeenAt")},
                {Take("SourceRank")},
                {Take("Source")},
                {Take("SystemId")},
                {Take("ReportId")},
                {Take("KillMailId")},
                {Take("NoVisual")},
                "ShipTypeId" = CASE WHEN {newer} AND NOT {keepShip} THEN excluded."ShipTypeId" ELSE {T}."ShipTypeId" END,
                "ShipName"   = CASE WHEN {newer} AND NOT {keepShip} THEN excluded."ShipName"   ELSE {T}."ShipName"   END,
                "Name" = CASE WHEN {newer} THEN COALESCE(excluded."Name", {T}."Name") ELSE COALESCE({T}."Name", excluded."Name") END,
                {Aff("CorporationId")},
                {Aff("AllianceId")},
                {Aff("AffiliationAt")},
                {Aff("AffiliationSource")},
                "WrittenAt" = {written}
            WHERE {newer} OR {affiliation} OR {nameFill}
            """;
    }
}
