using System.Text.RegularExpressions;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

public sealed record EveMailRow(
    int            MailId,
    long           CharacterId,
    long           FromId,
    string         FromName,
    string         Subject,
    DateTimeOffset Timestamp,
    bool           IsRead,
    string         Labels,
    bool           BodyFetched,
    string         RecipientSummary,
    IReadOnlyList<EveMailRecipient> Recipients
);

/// <summary>One addressee on a mail, kept as its own row so the header can link each name
/// rather than rendering the joined summary as a single unclickable string.</summary>
public sealed record EveMailRecipient(long Id, string Name, string Type);

public sealed record EveMailLabelOption(
    long   CharacterId,
    int    LabelId,
    string Name
);

public sealed record EveMailResolvedRecipient(
    long   Id,
    string Name,
    string Type  // "character" | "corporation" | "alliance"
);

public class EveMailService(IDbContextFactory<AppDbContext> dbFactory, EsiClient esi, AppErrorLogger errorLogger)
{
    // Uses raw ADO.NET so table creation is completely independent of EF's connection lifecycle.
    // Idempotent (CREATE TABLE IF NOT EXISTS) but only worth running once per process — the tables
    // don't disappear between polls, so every FetchHeaders/query would otherwise re-run the DDL.
    private static bool _tablesEnsured;
    private static readonly SemaphoreSlim _ensureGate = new(1, 1);

    private async Task EnsureTablesAsync(AppDbContext db)
    {
        if (_tablesEnsured) return;
        await _ensureGate.WaitAsync();
        try
        {
        if (_tablesEnsured) return;
        var connStr = db.Database.GetConnectionString() ?? "(null)";
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        string[] ddl =
        [
            """
            CREATE TABLE IF NOT EXISTS "EsiMailHeaders" (
                "MailId"       INTEGER NOT NULL,
                "CharacterId"  INTEGER NOT NULL,
                "FromId"       INTEGER NOT NULL DEFAULT 0,
                "FromName"     TEXT    NOT NULL DEFAULT '',
                "Subject"      TEXT    NOT NULL DEFAULT '',
                "Timestamp"    TEXT    NOT NULL DEFAULT '',
                "IsRead"       INTEGER NOT NULL DEFAULT 0,
                "Labels"       TEXT    NOT NULL DEFAULT '',
                "BodyFetched"  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY ("MailId", "CharacterId")
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS "EsiMailBodies" (
                "MailId" INTEGER NOT NULL PRIMARY KEY,
                "Body"   TEXT    NOT NULL DEFAULT ''
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS "EsiMailRecipients" (
                "Id"            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "MailId"        INTEGER NOT NULL,
                "RecipientId"   INTEGER NOT NULL DEFAULT 0,
                "RecipientType" TEXT    NOT NULL DEFAULT '',
                "RecipientName" TEXT    NOT NULL DEFAULT ''
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS "EsiMailLabels" (
                "CharacterId"  INTEGER NOT NULL,
                "LabelId"      INTEGER NOT NULL,
                "Name"         TEXT    NOT NULL DEFAULT '',
                "Color"        TEXT    NOT NULL DEFAULT '',
                "UnreadCount"  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY ("CharacterId", "LabelId")
            )
            """,
        ];

        foreach (var sql in ddl)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        _tablesEnsured = true;
        }
        catch (Exception ex)
        {
            errorLogger.Log("EveMailService", "EnsureTablesAsync", ex);
            throw;
        }
        finally { _ensureGate.Release(); }
    }

    // ── Header & label polling (called by EsiPollingService) ─────────────────

    public async Task<PollingResult> FetchHeadersAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        await EnsureTablesAsync(db);
        var r = await esi.ExecuteAuthAsync<List<EsiMailListEntry>>(
            charId, $"characters/{charId}/mail/", ct);
        if (!r.IsSuccess) return FromResult(r);

        foreach (var h in r.Data ?? [])
        {
            var existing = await db.EsiMailHeaders.FindAsync([h.MailId, charId], ct);
            if (existing is null)
            {
                db.EsiMailHeaders.Add(new EveMailHeader
                {
                    MailId      = h.MailId,
                    CharacterId = charId,
                    FromId      = h.From,
                    Subject     = h.Subject ?? "(no subject)",
                    Timestamp   = h.Timestamp,
                    IsRead      = h.IsRead ?? false,
                    Labels      = string.Join(",", h.Labels ?? []),
                    BodyFetched = false,
                });

                foreach (var rec in h.Recipients ?? [])
                    db.EsiMailRecipients.Add(new EveMailRecipientEntry
                    {
                        MailId        = h.MailId,
                        RecipientId   = rec.RecipientId,
                        RecipientType = rec.RecipientType,
                    });
            }
            else
            {
                existing.IsRead = h.IsRead ?? existing.IsRead;
                existing.Labels = string.Join(",", h.Labels ?? []);
            }
        }

        // Persist the mail itself before going near name resolution. That call reaches a
        // separate endpoint that can fail on its own, and it used to throw from here with
        // the whole batch still unsaved — so nine characters silently stopped recording new
        // mail entirely. A missing display name is cosmetic; a missing mail is not.
        await db.SaveChangesAsync(ct);

        // Resolve From and Recipient names in bulk so the UI shows names, not numeric IDs.
        //
        // /universe/names/ rejects the ENTIRE batch with 404 if any single id is
        // unresolvable, so mailing lists are dropped first — they are a legitimate
        // recipient type with ids that endpoint cannot resolve, and one of them poisons
        // every name in the request.
        //
        // The long overload is used deliberately: character ids are already within a few
        // percent of int.MaxValue, and the int cast this replaced would silently truncate
        // rather than fail once they pass it.
        var fromIds = (r.Data ?? [])
            .Select(h => h.From).Where(id => id > 0).Distinct().ToList();
        var recIds = (r.Data ?? [])
            .SelectMany(h => h.Recipients ?? [])
            .Where(rc => rc.RecipientType != "mailing_list")
            .Select(rc => rc.RecipientId).Where(id => id > 0).Distinct().ToList();
        var allIds = fromIds.Concat(recIds).Distinct().ToList();

        if (allIds.Count > 0)
        {
            try
            {
                var names   = await esi.GetNamesAsync(allIds, ct);
                var nameMap = names.ToDictionary(n => n.Id, n => n.Name);

                var hdrsNoName = await db.EsiMailHeaders
                    .Where(h => h.CharacterId == charId
                             && fromIds.Contains(h.FromId)
                             && h.FromName == "")
                    .ToListAsync(ct);
                foreach (var hdr in hdrsNoName)
                    if (nameMap.TryGetValue(hdr.FromId, out var name))
                        hdr.FromName = name;

                var mailIds    = (r.Data ?? []).Select(h => h.MailId).ToList();
                var recsNoName = await db.EsiMailRecipients
                    .Where(rc => mailIds.Contains(rc.MailId) && rc.RecipientName == "")
                    .ToListAsync(ct);
                foreach (var rec in recsNoName)
                    if (nameMap.TryGetValue(rec.RecipientId, out var name))
                        rec.RecipientName = name;

                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Best effort. An id this endpoint won't resolve — a biomassed character,
                // a deleted corp — leaves those rows showing a numeric id, which the next
                // poll retries. It must not cost us the mail.
                errorLogger.Log("EveMailService", $"resolve names charId={charId}", ex);
            }
        }

        return FromResult(r);
    }

    public async Task<PollingResult> FetchLabelsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await esi.ExecuteAuthAsync<EsiMailLabelsWrapper>(
            charId, $"characters/{charId}/mail/labels/", ct);
        if (!r.IsSuccess) return FromResult(r);

        foreach (var lbl in r.Data?.Labels ?? [])
        {
            var existing = await db.EsiMailLabels.FindAsync([charId, lbl.LabelId], ct);
            if (existing is null)
                db.EsiMailLabels.Add(new EveMailLabelEntry
                {
                    CharacterId = charId,
                    LabelId     = lbl.LabelId,
                    Name        = lbl.Name ?? $"Label {lbl.LabelId}",
                    Color       = lbl.Color ?? "",
                    UnreadCount = lbl.UnreadCount ?? 0,
                });
            else
            {
                existing.Name        = lbl.Name ?? existing.Name;
                existing.Color       = lbl.Color ?? existing.Color;
                existing.UnreadCount = lbl.UnreadCount ?? 0;
            }
        }

        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    // ── UI-facing reads ───────────────────────────────────────────────────────

    // charId=null + charIds=list → "All Characters" mode
    public async Task<List<EveMailRow>> GetMailsAsync(
        long? charId, List<long>? charIds = null, int? labelFilter = null, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        await EnsureTablesAsync(db);

        IQueryable<EveMailHeader> q = charId.HasValue
            ? db.EsiMailHeaders.Where(h => h.CharacterId == charId.Value)
            : db.EsiMailHeaders.Where(h => charIds == null || charIds.Contains(h.CharacterId));

        if (labelFilter.HasValue)
        {
            var label = labelFilter.Value.ToString();
            q = q.Where(h => h.Labels == label
                           || h.Labels.StartsWith(label + ",")
                           || h.Labels.Contains("," + label + ",")
                           || h.Labels.EndsWith("," + label));
        }

        // Sort by MailId DESC in SQL (int — EF can translate) to get the 500 newest,
        // then reorder by Timestamp in memory (DateTimeOffset ordering unsupported in EF SQLite).
        var headers = (await q.OrderByDescending(h => h.MailId).Take(500).ToListAsync(ct))
            .OrderByDescending(h => h.Timestamp)
            .ToList();

        // Load recipients for these mails
        var mailIds = headers.Select(h => h.MailId).ToList();
        var recs = await db.EsiMailRecipients
            .Where(r => mailIds.Contains(r.MailId))
            .ToListAsync(ct);
        var recsByMail = recs.GroupBy(r => r.MailId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return headers.Select(h =>
        {
            var mailRecs = recsByMail.GetValueOrDefault(h.MailId, []);
            var recSummary = mailRecs.Count > 0
                ? string.Join(", ", mailRecs.Select(r =>
                    !string.IsNullOrEmpty(r.RecipientName) ? r.RecipientName : $"#{r.RecipientId}"))
                : "";
            var recipients = mailRecs
                .Select(r => new EveMailRecipient(r.RecipientId,
                    !string.IsNullOrEmpty(r.RecipientName) ? r.RecipientName : $"#{r.RecipientId}",
                    r.RecipientType))
                .ToList();
            return new EveMailRow(h.MailId, h.CharacterId, h.FromId, h.FromName,
                h.Subject, h.Timestamp, h.IsRead, h.Labels, h.BodyFetched, recSummary, recipients);
        }).ToList();
    }

    // System labels already shown as static folders — skip them so they don't duplicate.
    private static readonly HashSet<int> _systemLabelIds = [1, 2, 4, 8, 16];

    public async Task<List<EveMailLabelOption>> GetLabelsAsync(long charId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var labels = await db.EsiMailLabels
            .Where(l => l.CharacterId == charId)
            .OrderBy(l => l.LabelId)
            .ToListAsync(ct);
        return labels
            .Where(l => !_systemLabelIds.Contains(l.LabelId))
            .Select(l => new EveMailLabelOption(l.CharacterId, l.LabelId, l.Name))
            .ToList();
    }

    public async Task<string> GetBodyAsync(long charId, int mailId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var stored = await db.EsiMailBodies.FindAsync([mailId], ct);
        if (stored is not null) return StripHtml(stored.Body);

        var header = await db.EsiMailHeaders.FindAsync([mailId, charId], ct);
        if (header?.BodyFetched == true) return "";

        var r = await esi.ExecuteAuthAsync<EsiMailDetail>(
            charId, $"characters/{charId}/mail/{mailId}/", ct);
        if (!r.IsSuccess || r.Data is null) return "(could not load mail body)";

        var rawBody = r.Data.Body ?? "";
        db.EsiMailBodies.Add(new EveMailBody { MailId = mailId, Body = rawBody });
        if (header is not null) header.BodyFetched = true;
        await db.SaveChangesAsync(ct);

        return StripHtml(rawBody);
    }

    public async Task<bool> MarkReadAsync(long charId, int mailId, CancellationToken ct = default)
    {
        await esi.PutAuthAsync(charId, $"characters/{charId}/mail/{mailId}/",
            new { read = true }, ct);

        using var db = dbFactory.CreateDbContext();
        var header = await db.EsiMailHeaders.FindAsync([mailId, charId], ct);
        if (header is null) return false;
        header.IsRead = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool Success, string? Error)> SendMailAsync(
        long fromCharId, string subject, string body,
        List<EsiMailRecipientItem> recipients, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject)) return (false, "Subject is required.");
        if (string.IsNullOrWhiteSpace(body))    return (false, "Body is required.");
        if (recipients.Count == 0)              return (false, "At least one recipient is required.");

        var payload = new
        {
            subject,
            body,
            recipients = recipients.Select(r => new
            {
                recipient_id   = r.RecipientId,
                recipient_type = r.RecipientType,
            }).ToList(),
        };

        var (statusCode, error) = await esi.PostAuthRawAsync(
            fromCharId, $"characters/{fromCharId}/mail/", payload, ct);

        return statusCode is >= 200 and < 300
            ? (true, null)
            // ⚠️ ESI's own words, not just the code. A 400 from this endpoint names the field it
            // objected to — body too long, recipient not found — and the code alone sends the
            // reader hunting through every field the request had.
            : (false, $"HTTP {statusCode}{(string.IsNullOrWhiteSpace(error) ? "" : $": {error.Trim()}")}");
    }

    // Resolve character name → id using ESI search
    public async Task<List<EveMailResolvedRecipient>> ResolveRecipientAsync(
        long fromCharId, string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed)) return [];

        // Primary: public universe/ids/ for exact name match (no auth, very reliable).
        var exact = await esi.LookupEntityIdsAsync([trimmed], ct);
        if (exact.Count > 0)
            return exact.Select(e => new EveMailResolvedRecipient(e.Id, e.Name, e.Category)).ToList();

        // Fallback: authenticated prefix search (finds partial names, returns first match).
        var ids = await esi.SearchCharacterIdsAsync(fromCharId, trimmed, ct);
        if (ids.Count == 0) return [];
        var resolved = await esi.GetNamesAsync(ids.Take(10).ToList(), ct);
        return resolved.Select(n => new EveMailResolvedRecipient(n.Id, n.Name, n.Category)).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        // Replace common block-level tags with newlines
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</p>",      "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</div>",    "\n", RegexOptions.IgnoreCase);
        // Strip remaining tags
        html = Regex.Replace(html, @"<[^>]+>", "");
        // Decode common HTML entities
        html = html.Replace("&lt;",   "<")
                   .Replace("&gt;",   ">")
                   .Replace("&amp;",  "&")
                   .Replace("&quot;", "\"")
                   .Replace("&nbsp;", " ")
                   .Replace("&#13;",  "\r")
                   .Replace("&#10;",  "\n");
        // Collapse excessive blank lines
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }

    private static PollingResult FromResult<T>(EsiCallResult<T> r) => new(
        r.IsSuccess, r.StatusCode, r.Error,
        r.RateLimitGroup, r.RateLimitRemaining, r.RetryAfterSeconds,
        r.ErrorLimitRemain, r.ErrorLimitReset);
}
