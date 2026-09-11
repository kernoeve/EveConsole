using System.Globalization;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Services;

namespace EveConsole.Agent.Tools.Data;

/// <summary>
/// Runs a query and puts its whole result set in a grid tab — without the rows ever passing
/// through the model.
///
/// <para>The other half of <c>show_table</c>. That tool takes the rows as the model wrote them,
/// which means every row went through the model's context twice: once coming back from
/// query_database, once being written into the call — and the second trip is what a 94-row
/// answer ran out of output tokens on. Here the rows go straight from the database to the grid.
/// A ten-thousand-row listing costs the model the length of its SQL.</para>
///
/// <para>⚠️ The trade is that the model never sees the rows, so the query has to produce the
/// finished table: aliases for headers, names joined in rather than ids, ISK and dates formatted,
/// ordered for a reader. There is no step between the database and the screen.</para>
///
/// <para>⚠️ The result handed back is the count, the headers and the FIRST ROW. The count and
/// headers are what the model needs to describe the tab; the sample row is a formatting check,
/// and on SQLite it is the only way the model can tell that a guessed column name came back as a
/// column of literals rather than an error.</para>
/// </summary>
public sealed class ShowQueryTool : IAgentTool
{
    /// <summary>
    /// The grid virtualises, so this is a bound on memory and on the reader's patience rather than
    /// on rendering. A listing this long wanted a WHERE clause.
    /// </summary>
    private const int MaxRows = 10_000;

    /// <summary>Longest a cell in the sample row is echoed back; the sample is a check, not data.</summary>
    private const int SampleCellChars = 40;

    private readonly AgentSchema? _schema;
    private readonly Func<string, string, string[], List<string[]>, string> _open;

    public string Name => "show_query";

    public string Description =>
        $$"""
        Runs a read-only SQL SELECT and shows its ENTIRE result set to the capsuleer in a new tab.
        The rows go straight from the database to the grid and never enter your context, so they
        cost you nothing however many there are. PREFER THIS over show_table whenever the rows
        come from a query and you do not need to reshape them by hand — and ALWAYS for more than
        a few dozen rows, which show_table cannot carry.

        The trade: you never see the rows, so you cannot analyse or reformat them afterwards. The
        query must produce the finished table:
          - Column aliases become the headers: SELECT t."Name" AS "Ship", …
          - Join names in rather than showing ids; the reader cannot look up a TypeId.
          - Format in SQL, since values are shown exactly as returned:
        {{Formatting}}
          - ORDER BY what the reader would want first.
        The database is {{AgentSqlDialect.Name}}; the same rules as query_database apply. Up to
        10,000 rows are shown — add LIMIT if the set could be larger.

        The result gives you the row count, the headers and the first row as a sample. Check the
        sample: a cell holding a column's own name means that column did not exist. For a summary
        figure — a total, a largest — run a separate small query_database; do not guess it from
        the sample. Each call opens a NEW tab. After calling this, say what the tab holds in a
        sentence or two and name it. Do NOT attempt to list rows you have not seen.
        """;

    /// <summary>The engine's own formatting functions, verified against both engines.</summary>
    private static string Formatting => DbEngine.IsPostgres
        ? """
              ISK:    to_char("Price", 'FM999,999,999,999,990.00')      → 1,234,567,890.50
              short:  round("Price"::numeric / 1e9, 2) || 'B'          → 23.46B
              dates:  to_char("Date", 'YYYY-MM-DD HH24:MI')             → 2026-08-05 01:26
              counts: to_char("Quantity", 'FM999,999,999')
          """
        : """
              ISK:    printf('%,.2f', "Price")                          → 1,234,567,890.50
              short:  round("Price" / 1e9, 2) || 'B'                    → 23.46B
              dates:  strftime('%Y-%m-%d %H:%M', "Date")                → 2026-08-05 01:26
              counts: printf('%,d', "Quantity")
          """;

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            title = new
            {
                type = "string",
                description = "Short tab name, ideally two to four words — e.g. \"Losses, 24h\". " +
                              "This is what the capsuleer sees on the tab, so make it specific.",
            },
            caption = new
            {
                type = "string",
                description = "Optional single line shown above the grid: what it is and any " +
                              "qualification that matters — the period covered, what was excluded, " +
                              "which valuation was used.",
            },
            sql = new
            {
                type = "string",
                description = "The SELECT whose result set IS the table. Aliases are the headers; " +
                              "values are shown exactly as returned.",
            },
        },
        required = new[] { "title", "sql" },
    };

    public ShowQueryTool(Func<string, string, string[], List<string[]>, string> openCallback, AgentSchema? schema = null)
    {
        _open   = openCallback;
        _schema = schema;
    }

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var title   = Text(input, "title");
        var caption = Text(input, "caption");
        var sql     = Text(input, "sql").Trim();

        if (string.IsNullOrWhiteSpace(sql))
            return "No sql was supplied, so there is nothing to show.";
        if (string.IsNullOrWhiteSpace(title)) title = "Results";

        if (ReadOnlySql.Reject(sql, _schema) is { } complaint)
            return complaint;

        string[]       columns;
        List<string[]> rows;
        bool           truncated;

        try
        {
            await using var conn = AppDb.Connect();
            await conn.OpenAsync(ct);
            await using var cmd = conn.Command(sql);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);

            var count = rdr.FieldCount;
            columns   = Enumerable.Range(0, count).Select(rdr.GetName).ToArray();
            rows      = [];

            while (rows.Count < MaxRows && await rdr.ReadAsync(ct))
            {
                var cells = new string[count];
                for (int i = 0; i < count; i++)
                    cells[i] = rdr.IsDBNull(i) ? "" : Cell(rdr.GetValue(i));
                rows.Add(cells);
            }

            truncated = rows.Count == MaxRows && await rdr.ReadAsync(ct);
        }
        catch (Exception ex)
        {
            // The database's own words. The model fixes the query from this, exactly as it does
            // for query_database — a tab is not opened for a query that did not run.
            return $"Query failed — nothing was shown. {ex.Message}";
        }

        if (rows.Count == 0)
            return "The query returned no rows, so no tab was opened. Say so in your reply, "
                 + "or widen the query.";

        var opened = _open(title, caption, columns, rows);

        var sample = string.Join(" | ", rows[0].Select(c =>
            c.Length <= SampleCellChars ? c : c[..SampleCellChars] + "…"));

        return $"{opened} Columns: {string.Join(", ", columns)}. First row: {sample}. "
             + (truncated
                ? $"⚠️ The result was cut at {MaxRows:N0} rows — tell the capsuleer, and narrow the query. "
                : "")
             + "Say what the tab holds in a sentence or two and name it; do not list rows.";
    }

    /// <summary>
    /// A value as the grid shows it. The SQL is expected to have done the formatting; this only
    /// keeps the engines' raw spellings of the types it did not — no "T" in a timestamp, no
    /// exponent in a large number, and booleans as words.
    /// </summary>
    private static string Cell(object value) => value switch
    {
        string s         => s,
        bool b           => b ? "yes" : "no",
        DateTime d       => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        decimal m        => m.ToString("0.############################", CultureInfo.InvariantCulture),
        double f         => f.ToString("0.############", CultureInfo.InvariantCulture),
        float f          => f.ToString("0.######", CultureInfo.InvariantCulture),
        _                => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
