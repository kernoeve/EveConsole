using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace EveConsole.Services;

// ⚠️ EF1002/EF1003 are suppressed for this file: table and column names cannot be parameters, so
// they are interpolated. Every one is built here from SDE file and field names and then reduced to
// letters and digits (see Identifier) — nothing from anywhere else reaches the SQL text, and every
// VALUE goes in as a parameter.
#pragma warning disable EF1002, EF1003

/// <summary>
/// Everything in the SDE the typed import does not read, kept anyway.
///
/// <para>The typed import (SdeImportService) maps the files and fields the app uses onto tables it
/// designed. This keeps the rest, so the database holds the whole SDE:</para>
/// <list type="bullet">
/// <item>A field a typed file does not read becomes a column on that file's own table, where the
/// table has a row per record — <c>SdeFactions."MemberRaces"</c>, <c>SdeGraphics."SofHullName"</c>.
/// Where it does not (typeMaterials, whose table is a row per material), the fields go to a table
/// of their own beside it: <c>SdeTypeMaterialsExtra</c>, keyed by the record.</item>
/// <item>A file nothing reads becomes a table of its own, named after it — <c>SdeTypeBonus</c>,
/// <c>SdeMissions</c> — with a column per field.</item>
/// </list>
/// <para>A plain value is a column of its type. A list or a block is JSON text. Localised text is
/// two columns: the English under the field's own name, and every language as JSON beside it,
/// <c>…Localized</c>. Columns are added as the data asks for them, and on PostgreSQL widened when a
/// later release needs a larger number type, so a field CCP adds next month is kept by the next
/// import with no change here.</para>
///
/// <para>⚠️ None of this is in the entity model. The tables and columns are made by this class
/// from what it finds; the app reads them by SQL when it comes to need one, and moves a field into
/// the model then — at which point this stops writing it, because the typed import reads it.</para>
///
/// <para>⚠️ Additive only, as the rest of the SDE schema is: columns are added and numeric ones
/// widened, never dropped, renamed or narrowed. Several builds can share one PostgreSQL
/// database.</para>
/// </summary>
public sealed class SdeCompleteness
{
    /// <summary>
    /// A file the typed import reads. <paramref name="Table"/> has one row per record, keyed by the
    /// record's key, so fields <paramref name="Classes"/> do not read become columns on it; null
    /// puts them in <c>Sde&lt;File&gt;Extra</c> instead. No classes: the file is read some other way,
    /// wholly, and is left alone. <paramref name="FullCopy"/> keeps every field of every record in
    /// a table of its own as well — for a file the typed import reads only part of the records of.
    /// </summary>
    public sealed record TypedFile(string File, string? Table, Type[] Classes, bool FullCopy = false, string[]? Ignore = null);

    private enum Mode { Supplement, Side, Full }

    private sealed record Job(string File, ZipArchiveEntry Entry, Mode Mode, string Table, string KeyColumn,
                              bool KeyIsText, IReadOnlySet<string> Leave);

    private enum Kind { Bool, Int, Long, Double, Text }

    private readonly List<Job> _jobs = [];
    private readonly List<string> _ownTables = [];

    /// <summary>Tables of this class's own making: emptied, set aside and verified with the rest.</summary>
    public IReadOnlyList<string> Tables => _ownTables;

    /// <summary>What did not go as it should, for the import's own warnings.</summary>
    public List<string> Warnings { get; } = [];

    private SdeCompleteness() { }

    /// <summary>
    /// Works out what to keep and makes its tables, keyed and otherwise empty — before the import
    /// sets its tables aside, so a failed import puts these back with the rest.
    /// </summary>
    public static async Task<SdeCompleteness> PrepareAsync(AppDbContext db, ZipArchive zip, string root,
        IReadOnlyList<TypedFile> typed, CancellationToken ct)
    {
        var plan        = new SdeCompleteness();
        var modelTables = db.Model.GetEntityTypes().Select(t => t.GetTableName()).OfType<string>()
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byFile      = typed.ToDictionary(t => t.File, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (!entry.FullName.StartsWith(root, StringComparison.Ordinal)) continue;
            var file = entry.FullName[root.Length..];
            if (file.Contains('/') || !file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.StartsWith('_')) continue;   // _sde.yaml: the build stamp, kept in SdeBuildInfos

            var stem = file[..^".yaml".Length];

            if (byFile.TryGetValue(file, out var t))
            {
                if (t.Classes.Length == 0) continue;   // read wholly some other way

                var read  = t.Classes.SelectMany(c => c.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                                     .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var leave = new HashSet<string>(read.Concat(t.Ignore ?? []), StringComparer.Ordinal);
                var scan  = Scan(entry);

                if (t.FullCopy)
                {
                    // Every field already, read or not: nothing further to add beside it.
                    plan.Add(new Job(file, entry, Mode.Full, TableName(stem, "", modelTables), "Id", scan.KeyIsText, new HashSet<string>()));
                    continue;
                }

                if (!scan.Fields.Any(f => !leave.Contains(f))) continue;   // the typed import reads it all

                if (t.Table is { } table && KeyColumnOf(db, table) is { } key)
                    plan.Add(new Job(file, entry, Mode.Supplement, table, key, scan.KeyIsText, leave));
                else
                    plan.Add(new Job(file, entry, Mode.Side, TableName(stem, "Extra", modelTables), "Id", scan.KeyIsText, leave));
            }
            else
                plan.Add(new Job(file, entry, Mode.Full, TableName(stem, "", modelTables), "Id", Scan(entry).KeyIsText, new HashSet<string>()));
        }

        // Tables of ours a previous import made, whose file this release no longer has: still
        // emptied with the rest, rather than left holding rows of an SDE that is gone.
        foreach (var existing in await TablesInDatabaseAsync(db, ct))
            if (existing.StartsWith("Sde", StringComparison.Ordinal) && !modelTables.Contains(existing)
                && existing.All(char.IsLetterOrDigit) && !plan._ownTables.Contains(existing, StringComparer.Ordinal))
                plan._ownTables.Add(existing);

        foreach (var job in plan._jobs.Where(j => j.Mode != Mode.Supplement))
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE TABLE IF NOT EXISTS \"{job.Table}\" (\"{job.KeyColumn}\" {(job.KeyIsText ? "TEXT" : DbEngine.IsPostgres ? "BIGINT" : "INTEGER")} NOT NULL PRIMARY KEY)", ct);

        return plan;
    }

    private void Add(Job job)
    {
        _jobs.Add(job);
        if (job.Mode != Mode.Supplement && !_ownTables.Contains(job.Table, StringComparer.Ordinal)) _ownTables.Add(job.Table);
    }

    /// <summary>
    /// Reads each file it was given and writes what it keeps. Runs after the typed import, so a
    /// supplementing column lands on the rows that import has just written.
    /// </summary>
    public async Task RunAsync(AppDbContext db, Action<string, double> report, double from, double to, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            for (var i = 0; i < _jobs.Count; i++)
            {
                var job = _jobs[i];
                report(job.File, from + (to - from) * i / Math.Max(1, _jobs.Count));

                // ⚠️ On PostgreSQL the whole import is one transaction, and a statement that fails
                // poisons it: everything after is refused and the import is lost. A savepoint per
                // file confines a failure to that file.
                var tx = db.Database.CurrentTransaction;
                if (tx is not null) await tx.CreateSavepointAsync("sde_complete", ct);
                try
                {
                    await RunJobAsync(db, job, ct);
                    if (tx is not null) await tx.ReleaseSavepointAsync("sde_complete", ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (tx is not null) await tx.RollbackToSavepointAsync("sde_complete", ct);
                    // One file going wrong costs that file, not the SDE: the typed tables are what
                    // the app runs on, and these are kept for completeness.
                    Warnings.Add($"{job.File}: not kept in full — {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                }
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private async Task RunJobAsync(AppDbContext db, Job job, CancellationToken ct)
    {
        // ── Read: the fields this job keeps, record by record ─────────────────────
        var rows = new List<(JsonNode Key, Dictionary<string, JsonNode?> Values)>();
        await using (var stream = job.Entry.Open())
        using (var reader = new StreamReader(stream))
        {
            foreach (var (key, value) in SdeYamlJson.Records(reader))
            {
                ct.ThrowIfCancellationRequested();
                Dictionary<string, JsonNode?> values;
                if (value is JsonObject o && !SdeYamlJson.IsLocalized(o) && o.All(kv => IsFieldName(kv.Key)))
                    values = o.Where(kv => !job.Leave.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                else if (job.Mode == Mode.Full)
                    // A record that is not a block of named fields — a list, one value, or numbers
                    // as keys (masteries by level) — is kept whole.
                    values = new() { [value is JsonObject && !SdeYamlJson.IsLocalized(value) ? "data" : "value"] = value };
                else continue;

                if (values.Count == 0 && job.Mode != Mode.Full) continue;
                rows.Add((key, values));
            }
        }
        if (rows.Count == 0) return;

        // ── The columns those fields need ─────────────────────────────────────────
        var modelColumns = ModelColumnsOf(db, job.Table);
        var columns = new List<(string Name, Kind Kind, Func<Dictionary<string, JsonNode?>, JsonNode?> Get)>();
        foreach (var field in rows.SelectMany(r => r.Values.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var nodes     = rows.Select(r => r.Values.GetValueOrDefault(field)).Where(n => n is not null).ToList();
            var localized = nodes.Count > 0 && nodes.All(SdeYamlJson.IsLocalized);
            var name      = ColumnName(field, job, modelColumns);
            if (localized)
            {
                columns.Add((name, Kind.Text, v => v.GetValueOrDefault(field) is JsonObject o ? o["en"] : null));
                columns.Add((ColumnName(field + "Localized", job, modelColumns), Kind.Text, v => v.GetValueOrDefault(field)));
            }
            else
                // An all-null field has nothing to say about its type: text, which takes anything.
                columns.Add((name, nodes.Count == 0 ? Kind.Text : nodes.Select(KindOf).Aggregate(Wider), v => v.GetValueOrDefault(field)));
        }

        var existing = await ColumnsInDatabaseAsync(db, job.Table, ct);
        var kinds    = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, kind, _) in columns)
        {
            if (!existing.TryGetValue(name, out var had))
            {
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{job.Table}\" ADD COLUMN \"{name}\" {SqlType(kind)}", ct);
                kinds[name] = kind;
                continue;
            }
            // ⚠️ PostgreSQL holds a column to its type, so one a later release outgrows is widened.
            // SQLite stores any value in any column and reports a flag's column as plain INTEGER,
            // so there the value's own type is written, never the column's guessed one.
            if (!DbEngine.IsPostgres) { kinds[name] = kind; continue; }
            var need = Wider(had, kind);
            if (need != had)
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{job.Table}\" ALTER COLUMN \"{name}\" TYPE {SqlType(need)} USING \"{name}\"::{SqlType(need)}", ct);
            kinds[name] = need;
        }

        // ── Write ─────────────────────────────────────────────────────────────────
        var conn  = db.Database.GetDbConnection();
        var outer = db.Database.CurrentTransaction?.GetDbTransaction();
        await using var own = outer is null ? await conn.BeginTransactionAsync(ct) : null;
        var tx = outer ?? own;

        if (job.Mode == Mode.Supplement)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE \"{job.Table}\" SET " +
                string.Join(", ", columns.Select((c, n) => $"\"{c.Name}\" = @v{n}")) + $" WHERE \"{job.KeyColumn}\" = @k";
            var pars = columns.Select((_, n) => Param(cmd, $"v{n}")).ToList();
            var keyPar = Param(cmd, "k");
            var missed = 0;
            foreach (var (key, values) in rows)
            {
                keyPar.Value = KeyValue(key, job.KeyIsText);
                for (var n = 0; n < columns.Count; n++) pars[n].Value = Convert(columns[n].Get(values), kinds[columns[n].Name]);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0) missed++;
            }
            if (missed > 0)
                Warnings.Add($"{job.File}: {missed:N0} record(s) have no row in {job.Table} to keep their other fields on.");
        }
        else
        {
            var perStatement = Math.Max(1, 900 / (columns.Count + 1));
            foreach (var chunk in rows.Chunk(perStatement))
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var sql = new StringBuilder($"INSERT INTO \"{job.Table}\" (\"{job.KeyColumn}\"" +
                    string.Concat(columns.Select(c => $", \"{c.Name}\"")) + ") VALUES ");
                var p = 0;
                for (var r = 0; r < chunk.Length; r++)
                {
                    sql.Append(r == 0 ? "(" : ", (");
                    Param(cmd, $"p{p}").Value = KeyValue(chunk[r].Key, job.KeyIsText);
                    sql.Append($"@p{p++}");
                    foreach (var c in columns)
                    {
                        Param(cmd, $"p{p}").Value = Convert(c.Get(chunk[r].Values), kinds[c.Name]);
                        sql.Append($", @p{p++}");
                    }
                    sql.Append(')');
                }
                cmd.CommandText = sql.ToString();
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (own is not null) await own.CommitAsync(ct);
    }

    // ── Names ────────────────────────────────────────────────────────────────────

    private static bool IsFieldName(string key) => key.Length > 0 && (char.IsLetter(key[0]) || key[0] == '_');

    /// <summary>"mapSolarSystems" → "MapSolarSystems", "solarSystemID" → "SolarSystemId",
    /// "mutually_exclusive_group" → "MutuallyExclusiveGroup": the app's own spelling, letters and
    /// digits only, so it can never carry anything into the SQL it is interpolated into.</summary>
    public static string Identifier(string s)
    {
        var sb = new StringBuilder();
        foreach (var part in s.Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries))
            sb.Append(char.ToUpperInvariant(part[0])).Append(part[1..]);
        var id = Regex.Replace(sb.ToString(), "ID(?=s?$|s?[A-Z])", "Id");
        id = new string(id.Where(char.IsLetterOrDigit).ToArray());
        return id.Length == 0 || char.IsDigit(id[0]) ? "F" + id : id;
    }

    private static string TableName(string stem, string suffix, IReadOnlySet<string> modelTables)
    {
        var name = "Sde" + Identifier(stem) + suffix;
        return modelTables.Contains(name) ? name + "File" : name;
    }

    /// <summary>A field's column. Not the key's name, and not a column the model maps — a typed
    /// import's own column is never written over; the field takes a name beside it instead.</summary>
    private static string ColumnName(string field, Job job, IReadOnlySet<string> modelColumns)
    {
        var name = Identifier(field);
        if (name.Equals(job.KeyColumn, StringComparison.OrdinalIgnoreCase)) name += "Value";
        while (modelColumns.Contains(name)) name += "Sde";
        return name;
    }

    private static string? KeyColumnOf(AppDbContext db, string table)
    {
        var entity = db.Model.GetEntityTypes().FirstOrDefault(t => t.GetTableName() == table);
        var key    = entity?.FindPrimaryKey();
        if (entity is null || key is null || key.Properties.Count != 1) return null;
        return key.Properties[0].GetColumnName(StoreObjectIdentifier.Table(table, entity.GetSchema()));
    }

    private static IReadOnlySet<string> ModelColumnsOf(AppDbContext db, string table)
    {
        var entity = db.Model.GetEntityTypes().FirstOrDefault(t => t.GetTableName() == table);
        if (entity is null) return new HashSet<string>();
        var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
        return entity.GetProperties().Select(p => p.GetColumnName(store)).OfType<string>()
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ── Types ────────────────────────────────────────────────────────────────────

    private static Kind KindOf(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<bool>(out _)   => Kind.Bool,
        JsonValue v when v.TryGetValue<long>(out var l) => l is >= int.MinValue and <= int.MaxValue ? Kind.Int : Kind.Long,
        JsonValue v when v.TryGetValue<double>(out _) => Kind.Double,
        _                                             => Kind.Text,
    };

    /// <summary>The narrowest kind both fit: whole numbers widen to larger ones and to decimals;
    /// anything else that differs is text.</summary>
    private static Kind Wider(Kind a, Kind b)
    {
        if (a == b) return a;
        if (a == Kind.Bool) return b == Kind.Bool ? a : b is Kind.Int or Kind.Long or Kind.Double ? Kind.Text : b;
        if (b == Kind.Bool) return Wider(b, a);
        if (a == Kind.Text || b == Kind.Text) return Kind.Text;
        return (Kind)Math.Max((int)a, (int)b);   // Int < Long < Double
    }

    private static string SqlType(Kind kind) => DbEngine.IsPostgres
        ? kind switch { Kind.Bool => "BOOLEAN", Kind.Int => "INTEGER", Kind.Long => "BIGINT", Kind.Double => "DOUBLE PRECISION", _ => "TEXT" }
        : kind switch { Kind.Double => "REAL", Kind.Text => "TEXT", _ => "INTEGER" };

    private static Kind KindOfSql(string type) => type.ToLowerInvariant() switch
    {
        "boolean"                        => Kind.Bool,
        "integer" when DbEngine.IsPostgres => Kind.Int,
        "integer" or "bigint" or "int"   => Kind.Long,
        "double precision" or "real" or "float" => Kind.Double,
        _                                => Kind.Text,
    };

    private static object Convert(JsonNode? node, Kind kind)
    {
        if (node is null) return DBNull.Value;
        if (node is JsonValue v)
        {
            switch (kind)
            {
                case Kind.Bool   when v.TryGetValue<bool>(out var b):   return b;
                case Kind.Int    when v.TryGetValue<long>(out var i):   return (int)i;
                case Kind.Long   when v.TryGetValue<long>(out var l):   return l;
                case Kind.Long   when v.TryGetValue<bool>(out var lb):  return lb ? 1L : 0L;   // SQLite: a flag in an INTEGER column
                case Kind.Double when v.TryGetValue<double>(out var d): return d;
                case Kind.Double when v.TryGetValue<long>(out var dl):  return (double)dl;
            }
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var tb)) return tb ? "true" : "false";
            if (v.TryGetValue<long>(out var tl)) return tl.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out var td)) return td.ToString("R", CultureInfo.InvariantCulture);
        }
        return SdeYamlJson.Text(node);
    }

    private static object KeyValue(JsonNode key, bool isText) =>
        !isText && key is JsonValue v && v.TryGetValue<long>(out var l) ? l
        : key is JsonValue s && s.TryGetValue<string>(out var str) ? str : SdeYamlJson.Text(key);

    private static DbParameter Param(DbCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    // ── The database's own view ──────────────────────────────────────────────────

    private static async Task<List<string>> TablesInDatabaseAsync(AppDbContext db, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<string>(DbEngine.IsPostgres
            ? "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = current_schema()"
            : "SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'table'").ToListAsync(ct);

    private static async Task<Dictionary<string, Kind>> ColumnsInDatabaseAsync(AppDbContext db, string table, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ColumnRow>(DbEngine.IsPostgres
            ? $"SELECT column_name AS \"Name\", data_type AS \"Type\" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '{table}'"
            : $"SELECT name AS \"Name\", type AS \"Type\" FROM pragma_table_info('{table}')").ToListAsync(ct);
        return rows.ToDictionary(r => r.Name, r => KindOfSql(r.Type), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ColumnRow { public string Name { get; set; } = ""; public string Type { get; set; } = ""; }

    // ── The quick look ───────────────────────────────────────────────────────────

    /// <summary>
    /// A file's field names and whether its keys are text, from its lines alone — no parse. The
    /// fields of a record are the keys one indent in from it. Cheap enough to run over every file
    /// on every import, so a field CCP adds is noticed without reading a third of a million moons
    /// to find out there is nothing new.
    /// </summary>
    private static (HashSet<string> Fields, bool KeyIsText) Scan(ZipArchiveEntry entry)
    {
        // ⚠️ On bytes, not lines of text. The typed files run to a gigabyte between them — a third of
        // a million moons — and a string per line made this the slowest stage of the import (51 s).
        // A field name becomes a string once, the first time its bytes are seen.
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var seen   = new HashSet<ulong>();
        bool? keyIsText = null;
        var indent = -1;

        using var stream = entry.Open();
        var buf  = new byte[1 << 16];
        var line = new byte[512];          // the start of the current line: all a key ever needs
        var len  = 0;                       // bytes of the current line kept in `line`
        var full = false;                   // the line ran past what `line` keeps

        void EndLine()
        {
            var n = len;
            if (n == 0 || line[0] == (byte)'#') return;
            if (line[0] != (byte)' ' && line[0] != (byte)'-')
            {
                if (keyIsText is null)
                {
                    var colon = Array.IndexOf(line, (byte)':', 0, n);
                    var key = Encoding.UTF8.GetString(line, 0, colon < 0 ? n : colon).Trim().Trim('\'', '"');
                    keyIsText = !long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
                }
                indent = -1;
                return;
            }
            var spaces = 0;
            while (spaces < n && line[spaces] == (byte)' ') spaces++;
            if (spaces == n || line[spaces] == (byte)'-' || line[spaces] == (byte)'#') return;
            if (indent < 0) indent = spaces;
            if (spaces != indent) return;
            var end = Array.IndexOf(line, (byte)':', spaces, n - spaces);
            if (end <= spaces) return;
            ulong hash = 14695981039346656037UL;   // FNV-1a over the name's bytes
            for (var i = spaces; i < end; i++) hash = (hash ^ line[i]) * 1099511628211UL;
            if (seen.Add(hash)) fields.Add(Encoding.UTF8.GetString(line, spaces, end - spaces).Trim('\'', '"'));
        }

        int read;
        while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            for (var i = 0; i < read; i++)
            {
                var c = buf[i];
                if (c == (byte)'\n') { EndLine(); len = 0; full = false; continue; }
                if (c == (byte)'\r') continue;
                if (len < line.Length) line[len++] = c; else full = true;
            }
        EndLine();
        _ = full;
        return (fields, keyIsText ?? false);
    }
}
