using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Data;

/// <summary>
/// The agent telemetry tables, for a SQLite database that already exists.
///
/// <para>⚠️ <c>EnsureCreated</c> builds a schema only into an EMPTY database, so the entity model
/// alone reaches nobody who is upgrading. Every table introduced after a database first existed
/// has to be spelled out somewhere, and for SQLite that is here — the same reason
/// <c>SdeImportService.EnsureSdeSchema</c> exists, and the same gap that let 0.9.13 ship a table
/// no upgrading user would ever have received.</para>
///
/// <para>The PostgreSQL spelling of these lives in <see cref="PostgresSchema"/>. They are written
/// out twice rather than shared because the types genuinely differ — AUTOINCREMENT is rejected by
/// PostgreSQL at parse time even under IF NOT EXISTS, and a boolean is an INTEGER on one engine
/// and a BOOLEAN on the other.</para>
///
/// <para>⚠️ Every column is NOT NULL with a DEFAULT, except the one that is genuinely optional.
/// A column added without a default fails on the first insert into a fresh install — a break that
/// only ever shows up on a clean machine, never on a developer's own database.</para>
/// </summary>
public static class AgentTelemetrySchema
{
    public static void Ensure(AppDbContext db)
    {
        foreach (var sql in Tables) db.Database.ExecuteSqlRaw(sql);
        foreach (var sql in Indexes) db.Database.ExecuteSqlRaw(sql);

        // ⚠️ SeedRates is NOT called here. The rate list is data rather than schema and both
        // engines need it, so App.axaml.cs calls it once outside the engine branch — seeding it
        // here as well would run it twice on SQLite and not at all on PostgreSQL.
    }

    /// <summary>
    /// Writes the default price list, once, for any row not already present.
    ///
    /// <para>⚠️ Insert-if-absent rather than replace. These are a starting point the capsuleer is
    /// meant to correct, and overwriting on every start would silently undo their corrections —
    /// leaving a figure that is wrong AND looks maintained.</para>
    ///
    /// <para>Rates are USD per single unit. Published list prices at the time of writing, which is
    /// exactly why they live in a table: they will drift, and the UI says when each was last
    /// touched so a stale one is visible rather than assumed.</para>
    /// </summary>
    public static void SeedRates(AppDbContext db)
    {
        // kind, provider, model, input, output, cacheRead, cacheWrite, note
        (string, string, string, decimal, decimal, decimal, decimal, string)[] defaults =
        [
            ("llm", "Claude (Anthropic)", "claude-sonnet-4-6",
             0.000003m, 0.000015m, 0.0000003m, 0.00000375m, "Sonnet list: $3/$15 per Mtok"),
            ("llm", "Claude (Anthropic)", "",
             0.000003m, 0.000015m, 0.0000003m, 0.00000375m, "Fallback for any Claude model"),
            ("llm", "OpenAI", "",
             0.0000025m, 0.00001m, 0.00000125m, 0m, "GPT-4o list: $2.50/$10 per Mtok"),
            ("llm", "Local", "", 0m, 0m, 0m, 0m, "Runs on this machine"),

            ("tts", "OpenAi",     "", 0.000015m, 0m, 0m, 0m, "OpenAI TTS: $15 per 1M characters"),
            ("tts", "ElevenLabs", "", 0.00003m,  0m, 0m, 0m, "Approximate — depends on plan"),
            ("tts", "Kokoro",     "", 0m, 0m, 0m, 0m, "Local"),
            ("tts", "Piper",      "", 0m, 0m, 0m, 0m, "Local"),

            ("stt", "OpenAiWhisper", "", 0.0001m, 0m, 0m, 0m, "Whisper: $0.006 per minute"),
            ("stt", "LocalWhisper",  "", 0m, 0m, 0m, 0m, "Local"),
        ];

        var existing = db.ServiceRates
            .Select(r => new { r.Kind, r.Provider, r.Model })
            .ToHashSet();

        var added = false;
        foreach (var (kind, provider, model, inp, outp, cr, cw, note) in defaults)
        {
            if (existing.Any(e => e.Kind == kind && e.Provider == provider && e.Model == model))
                continue;

            db.ServiceRates.Add(new ServiceRate
            {
                Kind              = kind,
                Provider          = provider,
                Model             = model,
                InputPerUnit      = inp,
                OutputPerUnit     = outp,
                CacheReadPerUnit  = cr,
                CacheWritePerUnit = cw,
                Notes             = note,
                UpdatedAt         = DateTimeOffset.UtcNow,
            });
            added = true;
        }

        if (added) db.SaveChanges();
    }

    private static readonly string[] Tables =
    [
        """
        CREATE TABLE IF NOT EXISTS "AgentInteractions" (
            "Id"             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "ConversationId" TEXT    NOT NULL DEFAULT '',
            "StartedAt"      TEXT    NOT NULL DEFAULT '',
            "DurationMs"     INTEGER NOT NULL DEFAULT 0,
            "Provider"       TEXT    NOT NULL DEFAULT '',
            "Model"          TEXT    NOT NULL DEFAULT '',
            "RoundTrips"     INTEGER NOT NULL DEFAULT 0,
            "ToolCallCount"  INTEGER NOT NULL DEFAULT 0,
            "QueryCount"     INTEGER NOT NULL DEFAULT 0,
            "ToolsUsed"      TEXT    NOT NULL DEFAULT '',
            "StopReason"     TEXT    NOT NULL DEFAULT '',
            "Error"          TEXT    NOT NULL DEFAULT '',
            "UserChars"      INTEGER NOT NULL DEFAULT 0,
            "ResponseChars"  INTEGER NOT NULL DEFAULT 0
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS "AgentToolCalls" (
            "Id"            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "InteractionId" INTEGER NOT NULL DEFAULT 0,
            "Sequence"      INTEGER NOT NULL DEFAULT 0,
            "OccurredAt"    TEXT    NOT NULL DEFAULT '',
            "ToolName"      TEXT    NOT NULL DEFAULT '',
            "DurationMs"    INTEGER NOT NULL DEFAULT 0,
            "InputJson"     TEXT    NOT NULL DEFAULT '',
            "ResultChars"   INTEGER NOT NULL DEFAULT 0,
            "RowCount"      INTEGER NOT NULL DEFAULT -1,
            "Error"         TEXT    NOT NULL DEFAULT ''
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS "ServiceUsage" (
            "Id"                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "OccurredAt"        TEXT    NOT NULL DEFAULT '',
            "InteractionId"     INTEGER NULL,
            "Kind"              TEXT    NOT NULL DEFAULT '',
            "Provider"          TEXT    NOT NULL DEFAULT '',
            "Model"             TEXT    NOT NULL DEFAULT '',
            "IsLocal"           INTEGER NOT NULL DEFAULT 0,
            "UnitKind"          TEXT    NOT NULL DEFAULT '',
            "InputUnits"        INTEGER NOT NULL DEFAULT 0,
            "OutputUnits"       INTEGER NOT NULL DEFAULT 0,
            "CacheReadUnits"    INTEGER NOT NULL DEFAULT 0,
            "CacheWriteUnits"   INTEGER NOT NULL DEFAULT 0,
            "UnitsAreEstimated" INTEGER NOT NULL DEFAULT 0,
            "DurationMs"        INTEGER NOT NULL DEFAULT 0,
            "Error"             TEXT    NOT NULL DEFAULT ''
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS "ServiceRates" (
            "Id"                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "Kind"              TEXT    NOT NULL DEFAULT '',
            "Provider"          TEXT    NOT NULL DEFAULT '',
            "Model"             TEXT    NOT NULL DEFAULT '',
            "InputPerUnit"      TEXT    NOT NULL DEFAULT '0',
            "OutputPerUnit"     TEXT    NOT NULL DEFAULT '0',
            "CacheReadPerUnit"  TEXT    NOT NULL DEFAULT '0',
            "CacheWritePerUnit" TEXT    NOT NULL DEFAULT '0',
            "Notes"             TEXT    NOT NULL DEFAULT '',
            "UpdatedAt"         TEXT    NOT NULL DEFAULT ''
        )
        """,
    ];

    /// <summary>
    /// ⚠️ Must match the indexes declared on these entities in <c>OnModelCreating</c>, or a fresh
    /// install and an upgraded one end up with different query plans for the same screen.
    /// Identical syntax on both engines, so <see cref="PostgresSchema"/> carries the same list.
    /// </summary>
    private static readonly string[] Indexes =
    [
        """CREATE INDEX IF NOT EXISTS "IX_AgentInteractions_StartedAt" ON "AgentInteractions" ("StartedAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_AgentInteractions_Conversation" ON "AgentInteractions" ("ConversationId", "StartedAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_AgentToolCalls_Interaction" ON "AgentToolCalls" ("InteractionId", "Sequence")""",
        """CREATE INDEX IF NOT EXISTS "IX_AgentToolCalls_OccurredAt" ON "AgentToolCalls" ("OccurredAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_ServiceUsage_OccurredAt" ON "ServiceUsage" ("OccurredAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_ServiceUsage_Kind_OccurredAt" ON "ServiceUsage" ("Kind", "OccurredAt")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ServiceRates_Key" ON "ServiceRates" ("Kind", "Provider", "Model")""",
    ];
}
