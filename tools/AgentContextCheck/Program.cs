// ─────────────────────────────────────────────────────────────────────────────
//  Agent context check
//
//  The agent's instructions are prose, and prose is code that no compiler reads. That is
//  not a theory: AgentSqlDialect exists because an engine switch left the SQL guidance
//  quietly wrong, and the schema notes on query_database documented MarketItemPrices as
//  BuyMax/SellMin when the real columns are BuyPrice/SellPrice/Midpoint — which cost two
//  failed queries in a single conversation before describe_tables supplied the truth.
//
//  WHAT THIS CHECKS. Every table and column NAMED in the agent's instructions against the
//  entity model, and the size of the always-resident prompt.
//
//  ⚠️ WHAT IT CANNOT CHECK, and this matters more than what it can. It verifies that a name
//  EXISTS, never that a sentence about it is TRUE. The notes said CharacterAffiliations
//  answers "which corp is this character in now"; every identifier in that sentence was
//  real and the claim was wrong — it is a first-seen cache that never refreshes, so the
//  answer would have been months stale and confident. No harness catches that. A green run
//  here means the names are right, not that the guidance is.
// ─────────────────────────────────────────────────────────────────────────────

using System.Text.RegularExpressions;
using EveConsole.Agent;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

var repo = FindRepoRoot();
if (repo is null) { Console.Error.WriteLine("Not inside the repository."); return 2; }

// A throwaway database purely to instantiate the model. Nothing is written to it.
var modelDb = Path.Combine(Path.GetTempPath(), $"agentctx-{Guid.NewGuid():N}.db");
AgentSchema schema;
try
{
    using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={modelDb}").Options);
    schema = AgentSchema.Build(db);
}
finally
{
    try { File.Delete(modelDb); } catch { }
}

var failures = new List<string>();
var checkedNames = 0;

// The text the agent is actually given. Anything added here is checked from then on.
var sources = new (string Label, string Text)[]
{
    ("AgentDataNotes",  AgentDataNotes.Notes),
    ("system prompt",   AgentService.BuildSystemPrompt(new AgentSettings(), schema.Prompt)),
};

foreach (var (label, text) in sources)
{
    // ── "TableName: col, col, col" ───────────────────────────────────────────
    //
    // The shape the stale notes used, and the one most likely to rot: a table followed by a
    // hand-maintained column list. Exactly how BuyMax and SellMin survived being deleted from
    // the model.
    foreach (Match m in Regex.Matches(text, @"^\s{0,12}([A-Z][A-Za-z0-9]{3,}):\s*([A-Za-z0-9_'(). |]+(?:,\s*[A-Za-z0-9_'(). |]+)+)\s*$",
                                      RegexOptions.Multiline))
    {
        var table = m.Groups[1].Value;
        if (!schema.Has(table)) continue;   // not a table reference — ordinary prose with a colon

        checkedNames++;
        var known = schema.Columns(table)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in m.Groups[2].Value.Split(','))
        {
            // Trim the annotations these lists carry: "Runs(-1=BPO)", "Status('active'|...)".
            var col = raw.Trim();
            var paren = col.IndexOf('(');
            if (paren > 0) col = col[..paren].Trim();
            if (col.Length == 0 || !Regex.IsMatch(col, @"^[A-Za-z_][A-Za-z0-9_]*$")) continue;

            checkedNames++;
            if (!known.Contains(col))
                failures.Add($"{label}: {table} has no column '{col}'. It has: "
                           + string.Join(", ", known.Order(StringComparer.Ordinal).Take(12)) + "…");
        }
    }

    // ── "Table.Column" ───────────────────────────────────────────────────────
    foreach (Match m in Regex.Matches(text, @"\b([A-Z][A-Za-z0-9]{3,})\.([A-Z][A-Za-z0-9_]*)\b"))
    {
        var (table, col) = (m.Groups[1].Value, m.Groups[2].Value);
        if (!schema.Has(table)) continue;   // a class name or a sentence, not a table reference

        checkedNames++;
        if (!schema.Columns(table).Any(c => c.Name.Equals(col, StringComparison.OrdinalIgnoreCase)))
            failures.Add($"{label}: {table}.{col} does not exist. {table} has: "
                       + string.Join(", ", schema.Columns(table).Select(c => c.Name).Take(12)) + "…");
    }

    // ── Names that are ALMOST a table ────────────────────────────────────────
    //
    // Catches a plural DbSet name used where the table is singular — BackgroundWorkerStatuses
    // against BackgroundWorkerStatus — and ordinary typos. Deliberately only flags a token one
    // character away from a real name, so prose is left alone.
    foreach (Match m in Regex.Matches(text, @"\b([A-Z][A-Za-z0-9]{6,})\b"))
    {
        var word = m.Groups[1].Value;
        if (schema.Has(word)) continue;

        // ⚠️ Compound CamelCase only. Learned by tripping over it: "Character", "Structure" and
        // "Corporation" are each one letter from a real table name and each perfectly ordinary
        // English, and a check that cries wolf on prose is a check somebody turns off.
        if (word.Count(char.IsUpper) < 2) continue;

        var near = schema.TableNames.FirstOrDefault(t => OneEditApart(t, word));
        if (near is not null)
            failures.Add($"{label}: '{word}' is not a table — did you mean '{near}'?");
    }
}

// ── The always-resident prompt is paid for on every turn ─────────────────────
//
// Cached, so most of it bills at a tenth after the first turn — but it is still sent, still
// counted, and still competes for the model's attention. A ceiling makes growth a decision
// rather than an accident.
var prompt = AgentService.BuildSystemPrompt(new AgentSettings(), schema.Prompt);
var approxTokens = prompt.Length / 4;
const int Budget = 12_000;

Console.WriteLine($"tables in model      : {schema.TableCount}");
Console.WriteLine($"identifiers checked  : {checkedNames}");
Console.WriteLine($"resident prompt      : ~{approxTokens:N0} tokens ({prompt.Length:N0} chars)");
Console.WriteLine();

if (approxTokens > Budget)
    failures.Add($"resident prompt is ~{approxTokens:N0} tokens, over the {Budget:N0} budget. "
               + "Either trim it or raise the budget deliberately — it is sent on every turn.");

if (failures.Count == 0)
{
    Console.WriteLine("Agent context check: every name in the agent's instructions exists.");
    Console.WriteLine();
    Console.WriteLine("⚠️ Names only. This cannot tell whether the guidance about them is TRUE —");
    Console.WriteLine("   see the note at the top of this file.");
    return 0;
}

foreach (var f in failures.Distinct()) Console.Error.WriteLine($"  {f}");
Console.Error.WriteLine();
Console.Error.WriteLine($"{failures.Distinct().Count()} problem(s). The agent is being told about");
Console.Error.WriteLine("schema that does not exist, which makes it fail confidently rather than look.");
return 1;

// ── Helpers ──────────────────────────────────────────────────────────────────

static bool OneEditApart(string a, string b)
{
    if (Math.Abs(a.Length - b.Length) > 1) return false;
    if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return false;

    // Same length: exactly one substitution.
    if (a.Length == b.Length)
    {
        var diffs = 0;
        for (var i = 0; i < a.Length; i++)
            if (char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[i]) && ++diffs > 1) return false;
        return diffs == 1;
    }

    // Lengths differ by one: exactly one insertion or deletion.
    var (longer, shorter) = a.Length > b.Length ? (a, b) : (b, a);
    int li = 0, si = 0, skipped = 0;
    while (li < longer.Length && si < shorter.Length)
    {
        if (char.ToLowerInvariant(longer[li]) == char.ToLowerInvariant(shorter[si])) { li++; si++; }
        else if (++skipped > 1) return false;
        else li++;
    }
    return true;
}

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "EveConsole.csproj"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
