using EveConsole.Services;

namespace EveConsole.Agent;

/// <summary>
/// The SQL guidance handed to the model, in the dialect of whichever database is actually open.
///
/// <para>⚠️ Without this the agent writes confident, well-formed SQLite against a PostgreSQL
/// server and every query fails. Nothing in the porting work reached it, because these are
/// prompts rather than code: no compiler sees them and no test exercises them, so an engine
/// switch left the SQL tools quietly broken while the rest of the app worked.</para>
///
/// <para>⚠️ Not a translation of the same advice. Half of the SQLite guidance exists because that
/// engine stores dates and booleans as text and integers, and the pitfalls it warns about do not
/// exist on a server — a date comparison there is between real timestamps. Rewriting the warning
/// in PostgreSQL syntax would teach the model to worry about something that cannot happen; what
/// it needs instead is the one rule SQLite never had, that identifiers are case-sensitive and
/// must be quoted.</para>
/// </summary>
public static class AgentSqlDialect
{
    /// <summary>What to call the engine in prose.</summary>
    public static string Name => DbEngine.IsPostgres ? "PostgreSQL" : "SQLite";

    /// <summary>
    /// How to compare dates, which is where a wrong answer is most likely to look right.
    /// </summary>
    public static string Dates => DbEngine.IsPostgres
        ? """
          DATES
          Dates are real timestamptz columns, so compare them with intervals and let the server
          do the arithmetic:
            WHERE "OccurredAt" >= now() - interval '10 minutes'
            WHERE "Date"       >= date_trunc('day', now())
          The session runs in UTC, so now() is UTC and no conversion is needed.
          """
        : """
          DATES — READ THIS BEFORE WRITING ANY DATE COMPARISON
          There are two different text formats in this database and comparing across them
          returns wrong rows silently rather than failing:

            Log-style, written by the log importers:   2026-08-05T02:05:55Z   ('T', trailing Z)
              GameLogEvents.OccurredAt, ChatMessages.OccurredAt, IntelReports.ReportedAt
            EF-style, everything else:                 2026-08-05 01:26:22+00:00   (space, offset)
              CharacterStatuses.*, EsiContracts.*, EsiIndustryJobs.*, AlarmEvents.FiredAt, …

          SQLite compares these as plain strings, and 'T' sorts above a space. So
              WHERE "OccurredAt" >= datetime('now','-10 minutes')     -- on a log-style column
          is true for EVERY row sharing today's date whatever its time. Measured: with a
          one-second window that returns 3 rows instead of 0.

          Match the column's own shape:
            log-style:  WHERE "OccurredAt" >= strftime('%Y-%m-%dT%H:%M:%SZ','now','-10 minutes')
            EF-style:   WHERE "LastLogin"  >= datetime('now','-10 minutes')
          """;

    /// <summary>
    /// The rules that differ everywhere else: quoting, and how a boolean is spelled.
    /// </summary>
    public static string Syntax => DbEngine.IsPostgres
        ? """
          IDENTIFIERS AND BOOLEANS
          Table and column names are case-sensitive and MUST be double-quoted, because they are
          mixed case: FROM "EsiAssets" a JOIN "SdeTypes" t ON t."TypeId" = a."TypeId".
          Unquoted names are folded to lower case and will not be found.
          Booleans are real: WHERE "IsHistory" = FALSE, not = 0.
          """
        : """
          IDENTIFIERS AND BOOLEANS
          Names may be quoted or not; both work. Booleans are stored as 0 and 1,
          so: WHERE "IsHistory" = 0.
          """;

    /// <summary>A predicate selecting the last N minutes of a log-style timestamp column.</summary>
    public static string RecentLogRows(string column, int hours) => DbEngine.IsPostgres
        ? $"""{column} >= now() - interval '{hours} hours'"""
        : $"""{column} >= strftime('%Y-%m-%dT%H:%M:%SZ','now','-{hours} hours')""";

    /// <summary>Comparing a boolean column to true, in the spelling this engine accepts.</summary>
    public static string IsTrue(string column) => DbEngine.IsPostgres ? $"{column} = TRUE" : $"{column} = 1";

    /// <summary>The same, negated.</summary>
    public static string IsFalse(string column) => DbEngine.IsPostgres ? $"{column} = FALSE" : $"{column} = 0";
}
