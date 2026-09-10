using System.Text;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Agent;

/// <summary>
/// The database as the agent sees it, read from the entity model rather than written by hand.
///
/// <para>⚠️ Generated because the hand-written version could not keep up and nothing said so. The
/// schema block in query_database's description described 48 tables; the model has over 200. The
/// agent was told about a quarter of the database and had no way to discover the rest, so a
/// question needing EsiContractItems or CharacterAffiliations — both real, both unmentioned —
/// looked to it like a question with no data behind it.</para>
///
/// <para>⚠️ Names come from <c>GetTableName()</c>, never from the DbSet property. They differ: the
/// DbSet is <c>BackgroundWorkerStatuses</c> and the table is <c>BackgroundWorkerStatus</c>. An
/// inventory built from property names produces SQL that fails on the real database.</para>
///
/// <para>Two renderings, for two different costs. <see cref="Index"/> is every table name grouped
/// by family, small enough to sit in the system prompt so the agent knows what EXISTS.
/// <see cref="Describe"/> is the columns, fetched only for the handful a question actually needs —
/// the full column list is around 8k tokens and has no business being resident.</para>
/// </summary>
public sealed class AgentSchema
{
    private readonly Dictionary<string, List<Column>> _tables;

    public sealed record Column(string Name, string Type, bool Nullable, bool IsKey);

    private AgentSchema(Dictionary<string, List<Column>> tables)
    {
        _tables = tables;
        Index   = BuildIndex(tables.Keys);
    }

    /// <summary>Every table name, grouped by family. Sits in the system prompt.</summary>
    public string Index { get; }

    public int TableCount => _tables.Count;

    public bool Has(string table) => _tables.ContainsKey(table);

    public IReadOnlyList<string> TableNames => [.. _tables.Keys];

    public IReadOnlyList<Column> Columns(string table)
        => _tables.TryGetValue(table, out var cols) ? cols : [];

    /// <summary>
    /// Reads the model through a scope. Built once at startup — the model does not change while
    /// the app runs, and reflecting over 200 entity types is not something to repeat per question.
    /// </summary>
    public static AgentSchema Build(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return Build(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>
    /// The same, from a context the caller already has. ⚠️ Exists so the drift check can build the
    /// schema without standing up the application's container — a guard that needs the app running
    /// is a guard that does not run in CI.
    /// </summary>
    public static AgentSchema Build(AppDbContext db)
    {
        var tables = new Dictionary<string, List<Column>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (string.IsNullOrEmpty(table)) continue;

            var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
            var columns = new List<Column>();

            foreach (var p in entity.GetProperties())
            {
                var name = p.GetColumnName(storeObject) ?? p.Name;
                columns.Add(new Column(
                    name,
                    Friendly(p.ClrType),
                    p.IsNullable,
                    p.IsPrimaryKey()));
            }

            // ⚠️ Merged, not overwritten. Two entity types can share a table, and taking the last
            // one seen would silently drop the other's columns.
            if (tables.TryGetValue(table, out var existing))
            {
                foreach (var c in columns)
                    if (!existing.Any(e => e.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                        existing.Add(c);
            }
            else
            {
                tables[table] = columns;
            }
        }

        return new AgentSchema(tables);
    }

    /// <summary>
    /// Columns for the named tables, with a "did you mean" when a name is wrong.
    ///
    /// <para>⚠️ The near-miss list matters more than it looks. On SQLite an unknown double-quoted
    /// identifier is treated as a string literal rather than an error, so a guessed name returns
    /// plausible rows full of the guess itself. Naming the real tables turns a wrong guess into a
    /// correction instead of a confident wrong answer.</para>
    /// </summary>
    public string Describe(IEnumerable<string> tables)
    {
        var sb = new StringBuilder();

        foreach (var requested in tables.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var actual = _tables.Keys.FirstOrDefault(
                k => k.Equals(requested, StringComparison.OrdinalIgnoreCase));

            if (actual is null)
            {
                var near = Nearest(requested, 5);
                sb.AppendLine($"{requested}: NO SUCH TABLE.");
                sb.AppendLine(near.Count > 0
                    ? $"  Closest names: {string.Join(", ", near)}"
                    : "  Nothing similar. Check the table index in your instructions.");
                sb.AppendLine();
                continue;
            }

            var cols = _tables[actual];
            sb.AppendLine($"{actual} ({cols.Count} columns)");
            foreach (var c in cols)
            {
                sb.Append("  ").Append(c.Name).Append(' ').Append(c.Type);
                if (c.IsKey)      sb.Append(" PK");
                if (c.Nullable)   sb.Append(" NULL");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Table names most like <paramref name="guess"/>, best first.</summary>
    public IReadOnlyList<string> Nearest(string guess, int max)
    {
        var g = guess.Trim();
        if (g.Length == 0) return [];

        return _tables.Keys
            .Select(name => (name, score: Score(name, g)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.name.Length)
            .Take(max)
            .Select(x => x.name)
            .ToList();

        static int Score(string name, string guess)
        {
            if (name.Equals(guess, StringComparison.OrdinalIgnoreCase))      return 100;
            if (name.StartsWith(guess, StringComparison.OrdinalIgnoreCase))  return 80;
            if (name.Contains(guess, StringComparison.OrdinalIgnoreCase))    return 60;
            if (guess.Contains(name, StringComparison.OrdinalIgnoreCase))    return 50;

            // A shared prefix catches the common near-miss: a plural DbSet name against a
            // singular table, or a family guessed correctly and the suffix wrong.
            var shared = 0;
            while (shared < name.Length && shared < guess.Length
                   && char.ToLowerInvariant(name[shared]) == char.ToLowerInvariant(guess[shared]))
                shared++;

            return shared >= 4 ? shared : 0;
        }
    }

    // ── The index ────────────────────────────────────────────────────────────

    /// <summary>
    /// Families, in the order they are worth reading. Curated because a machine cannot tell that
    /// "Sde" means CCP's static export while "Esi" means polled live data — but the MEMBERSHIP is
    /// derived, so a new table joins its family without anyone editing this list.
    /// </summary>
    private static readonly (string Prefix, string Label)[] Families =
    [
        ("Esi",       "Polled from ESI (live game data)"),
        ("Sde",       "Static Data Export (CCP's item, map and blueprint reference)"),
        ("Hobo",      "Hoboleaks (dogma and blueprint data CCP does not publish)"),
        ("EveRef",    "EVE Ref archives (historical market and structure snapshots)"),
        ("Market",    "Market pricing configuration and computed prices"),
        ("Worklist",  "Worklist / industry planner configuration"),
        ("Indy",      "Industry parks — which structures build what"),
        ("InvLevel",  "Inventory levels — your own stock targets"),
        ("MarketLevel", "Market levels — stock targets on a market"),
        ("Build",     "Computed build costs"),
        ("Contract",  "Contract pricing"),
        ("Sale",      "Sales tracking and posting"),
        ("Order",     "Order tracking"),
        ("Store",     "EVE Mail store"),
        ("KillMail",  "Killmails"),
        ("Intel",     "Intel reports parsed from chat"),
        ("GameLog",   "Game client logs read from this PC"),
        ("Chat",      "Chat logs read from this PC"),
        ("Map",       "Map overlays and statistics"),
        ("Alarm",     "Alarms"),
        ("Agent",     "This assistant's own activity log"),
        ("Character", "Character records and affiliations"),
        ("Corp",      "Corporation records and standing projects"),
        ("Type",      "Per-type snapshots"),
        ("Lp",        "Loyalty point stores and valuations"),
        ("Structure", "Player structures"),
        ("Net",       "Net worth history"),
        ("App",       "Application settings and error log"),
    ];

    private static string BuildIndex(IEnumerable<string> tableNames)
    {
        var remaining = new List<string>(tableNames);
        remaining.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine($"## Database tables ({remaining.Count})");
        sb.AppendLine();
        sb.AppendLine("Every table in the database is listed here. This is the complete set — if a");
        sb.AppendLine("question needs data, it is in one of these. Names are exact and case-sensitive.");
        sb.AppendLine();
        sb.AppendLine("⚠️ You do NOT know the columns of any of these. Call describe_tables for the ones");
        sb.AppendLine("you intend to use BEFORE writing SQL against them. Guessing a column name does not");
        sb.AppendLine("reliably fail — on SQLite an unknown quoted name is read as a string literal, so a");
        sb.AppendLine("wrong guess returns rows full of the guess rather than an error.");
        sb.AppendLine();

        foreach (var (prefix, label) in Families)
        {
            var members = remaining
                .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            if (members.Count == 0) continue;

            remaining.RemoveAll(t => members.Contains(t, StringComparer.Ordinal));
            sb.AppendLine($"{label} ({members.Count}):");
            sb.AppendLine("  " + string.Join(", ", members));
            sb.AppendLine();
        }

        if (remaining.Count > 0)
        {
            sb.AppendLine($"Other ({remaining.Count}):");
            sb.AppendLine("  " + string.Join(", ", remaining));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>CLR types named the way somebody writing SQL thinks about them.</summary>
    private static string Friendly(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;

        if (u == typeof(string))         return "text";
        if (u == typeof(bool))           return "bool";
        if (u == typeof(byte[]))         return "blob";
        if (u == typeof(DateTimeOffset) || u == typeof(DateTime)) return "datetime";
        if (u == typeof(decimal))        return "decimal";
        if (u == typeof(double) || u == typeof(float)) return "float";
        if (u == typeof(long) || u == typeof(int) || u == typeof(short) || u == typeof(byte))
            return "int";
        if (u.IsEnum)                    return "int";

        return u.Name.ToLowerInvariant();
    }
}
