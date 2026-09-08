using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public record HoboImportProgress(string Stage, string Detail, double Fraction);

/// <summary>One file as the Hoboleaks manifest describes it.</summary>
public record HoboFileMeta(bool Deprecated, bool Stale, string Md5, long Revision);

/// <summary>
/// What <c>meta.json</c> says about the current Hoboleaks publication.
/// </summary>
/// <remarks>
/// ⚠️ Hoboleaks publishes a manifest and the app was not reading it. It carries a revision number
/// per file, an md5 that is exactly the file's own ETag, and deprecated/stale flags — which
/// together answer both "is there an update?" and "is anything we depend on being abandoned?".
/// Neither was answerable before: the only record of a Hoboleaks import was the time it ran, so
/// the Settings screen could say when you last imported and never whether you needed to.
/// </remarks>
public record HoboMeta(long Revision, IReadOnlyDictionary<string, HoboFileMeta> Files);

// Thrown when the Hoboleaks manifest no longer publishes a file this version needs.
// Existing data is left intact so the app stays functional.
public class HoboCompatibilityException(IReadOnlyList<string> missing)
    : Exception(BuildMessage(missing))
{
    public IReadOnlyList<string> MissingFiles { get; } = missing;

    private static string BuildMessage(IReadOnlyList<string> missing) =>
        $"EVE Console needs to be updated before it can refresh the Hoboleaks data. The following " +
        $"file(s) are no longer published in the Hoboleaks manifest: {string.Join(", ", missing)}. " +
        $"Your existing Hoboleaks data has NOT been cleared.";
}

public class HoboImportService
{
    private const string BaseUrl = "https://sde.hoboleaks.space/tq/";
    private const int    Batch   = 2000;

    /// <summary>The files this version reads. Absent from the manifest is a hard stop.</summary>
    private static readonly string[] RequiredFiles =
        ["blueprints.json", "typematerials.json", "repackagedvolumes.json", "compressibletypes.json"];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory   _httpFactory;
    private readonly AppErrorLogger       _errors;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public HoboImportService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory)
    {
        _scopeFactory = scopeFactory;
        _httpFactory  = httpFactory;
        _errors       = new AppErrorLogger(scopeFactory);
    }

    /// <summary>
    /// The published manifest, for asking whether there is anything new without importing it.
    /// </summary>
    /// <remarks>Null on any failure to read it, the way the SDE's build check behaves.</remarks>
    public async Task<HoboMeta?> GetLatestMetaAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            await using var stream = await http.GetStreamAsync($"{BaseUrl}meta.json", ct);
            var doc = await JsonSerializer.DeserializeAsync<MetaJson>(stream, _json, ct);
            if (doc?.Files is null || doc.Files.Count == 0) return null;

            var files = doc.Files.ToDictionary(
                kv => kv.Key,
                kv => new HoboFileMeta(kv.Value.Deprecated, kv.Value.Stale, kv.Value.Md5 ?? "", kv.Value.Revision),
                StringComparer.OrdinalIgnoreCase);

            // The publication's revision, taken from the files still being maintained: a stale one
            // reports the revision it was last rebuilt at, which is not the current publication's.
            var revision = files.Values.Where(f => !f.Stale && !f.Deprecated)
                                       .Select(f => f.Revision).DefaultIfEmpty(0).Max();

            return new HoboMeta(revision, files);
        }
        catch { return null; }
    }

    /// <summary>
    /// Replaces every table the app derives from Hoboleaks.
    /// </summary>
    /// <returns>Warnings about tables that imported oddly but not badly enough to reject.</returns>
    /// <exception cref="HoboCompatibilityException">A file this version reads is no longer published.</exception>
    /// <exception cref="ImportVerificationException">
    /// The import ran to the end but lost a table that previously held data. Nothing was kept.
    /// </exception>
    public async Task<IReadOnlyList<string>> ImportAsync(IProgress<HoboImportProgress> progress, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // SQLite PRAGMAs, and journal_mode cannot be set inside a transaction — so this stays
        // above the undo opened below.
        await AppDb.TuneForBulkImportAsync(db.Database, ct);

        Report(progress, "Preparing", "Creating Hobo schema…", 0.01);
        await EnsureHoboSchemaAsync(db, ct);

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        // ⚠️ The manifest, then every file, all BEFORE anything is wiped. This used to clear the
        // tables and then fetch four files one at a time over the network, so a dropped connection
        // partway through left the app with no blueprint data at all and nothing saying why. The
        // four together are about 17 MB, which is worth holding to make that impossible.
        Report(progress, "Preparing", "Reading the Hoboleaks manifest…", 0.02);
        var meta = await GetLatestMetaAsync(ct)
            ?? throw new InvalidOperationException(
                "The Hoboleaks manifest could not be read, so no import was attempted. " +
                "Your existing Hoboleaks data has NOT been changed.");
        ValidateManifest(meta, progress);

        var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var fetched  = 0;
        foreach (var file in RequiredFiles)
        {
            Report(progress, "Downloading", $"{file}…", 0.04 + 0.16 * fetched / RequiredFiles.Length);
            payloads[file] = await http.GetByteArrayAsync($"{BaseUrl}{file}", ct);
            fetched++;
        }

        var tables = BulkImport.TablesFor(db, "Hobo", "HoboBuildInfos");
        var before = await BulkImport.CountAsync(db, tables, ct);

        // One transaction on PostgreSQL, a side-file copy on SQLite. See BulkImportUndo for why
        // those cannot be the same thing.
        await using var undo = await BulkImportUndo.CreateAsync(db, "hobo", tables,
            (stage, detail, frac) => progress.Report(new HoboImportProgress(stage, detail, frac)), ct);

        Report(progress, "Preparing", "Clearing existing Hobo data…", 0.22);
        await BulkImport.ClearAsync(db, tables, ct);

        db.ChangeTracker.AutoDetectChangesEnabled = false;

        await ImportBlueprintsAsync(db, payloads["blueprints.json"], progress, ct);
        await ImportTypeMaterialsAsync(db, payloads["typematerials.json"], progress, ct);
        await ImportRepackagedVolumesAsync(db, payloads["repackagedvolumes.json"], progress, ct);
        await ImportCompressibleTypesAsync(db, payloads["compressibletypes.json"], progress, ct);

        // The revision alongside the timestamp, so the next check can say whether there is
        // anything new rather than only when we last asked.
        db.ChangeTracker.AutoDetectChangesEnabled = true;
        var existing = await db.HoboBuildInfos.FindAsync([1], ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
            db.HoboBuildInfos.Add(new HoboBuildInfo { Id = 1, ImportedAt = now, Revision = meta.Revision });
        else
        {
            existing.ImportedAt = now;
            existing.Revision   = meta.Revision;
        }
        await db.SaveChangesAsync(ct);

        Report(progress, "Verifying", "Checking row counts…", 0.99);
        var (lost, warnings) = BulkImport.Compare(before, await BulkImport.CountAsync(db, tables, ct));

        if (lost.Count > 0)
        {
            await undo.RollbackAsync(CancellationToken.None);
            foreach (var line in lost)
                _errors.Log("HoboImport", "Verification", line);
            throw new ImportVerificationException("Hoboleaks", lost);
        }

        await undo.CommitAsync(ct);

        foreach (var line in warnings)
            _errors.Log("HoboImport", "Verification", line);

        Report(progress, "Done", "Hoboleaks import complete.", 1.0);
        return warnings;
    }

    /// <summary>Checks the manifest before anything is wiped.</summary>
    /// <remarks>
    /// ⚠️ Only a file the manifest no longer lists is a hard stop, for the same reason only a fall
    /// to zero rejects an import: a rule invented here that is stricter than "we cannot get the
    /// data" would one day block a perfectly good update with no way past it. Hoboleaks marks
    /// files deprecated and stale while still serving them — attributeorders.json is stale today,
    /// hundreds of thousands of revisions behind — so those are reported and imported, not refused.
    /// </remarks>
    private void ValidateManifest(HoboMeta meta, IProgress<HoboImportProgress> p)
    {
        var missing = RequiredFiles.Where(f => !meta.Files.ContainsKey(f)).ToList();
        if (missing.Count > 0) throw new HoboCompatibilityException(missing);

        var flagged = RequiredFiles
            .Select(f => (Name: f, Meta: meta.Files[f]))
            .Where(x => x.Meta.Deprecated || x.Meta.Stale)
            .Select(x => $"{x.Name} is marked {(x.Meta.Deprecated ? "deprecated" : "stale")} in the " +
                         $"Hoboleaks manifest; it was last rebuilt at revision {x.Meta.Revision:N0}.")
            .ToList();

        foreach (var line in flagged)
            _errors.Log("HoboImport", "Manifest", line);

        Report(p, "Preparing", flagged.Count == 0
            ? $"Manifest revision {meta.Revision:N0}, all {RequiredFiles.Length} files current."
            : $"Manifest revision {meta.Revision:N0}, {flagged.Count} file(s) flagged — see Errors.", 0.03);
    }

    // -----------------------------------------------------------------------
    // Schema + clear
    // -----------------------------------------------------------------------

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await EnsureHoboSchemaAsync(db, ct);
    }

    private static async Task EnsureHoboSchemaAsync(AppDbContext db, CancellationToken ct)
    {
        var ddl = new[]
        {
            """CREATE TABLE IF NOT EXISTS "HoboBuildInfos" ("Id" INTEGER NOT NULL PRIMARY KEY, "ImportedAt" TEXT NOT NULL, "Revision" INTEGER NOT NULL DEFAULT 0)""",
            """CREATE TABLE IF NOT EXISTS "HoboBlueprints" ("TypeId" INTEGER NOT NULL PRIMARY KEY, "MaxProductionLimit" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "HoboBlueprintActivities" ("TypeId" INTEGER NOT NULL, "Activity" TEXT NOT NULL, "Time" INTEGER NOT NULL, PRIMARY KEY ("TypeId", "Activity"))""",
            """CREATE TABLE IF NOT EXISTS "HoboBlueprintMaterials" ("TypeId" INTEGER NOT NULL, "Activity" TEXT NOT NULL, "MaterialTypeId" INTEGER NOT NULL, "Quantity" INTEGER NOT NULL, PRIMARY KEY ("TypeId", "Activity", "MaterialTypeId"))""",
            """CREATE TABLE IF NOT EXISTS "HoboBlueprintProducts" ("TypeId" INTEGER NOT NULL, "Activity" TEXT NOT NULL, "ProductTypeId" INTEGER NOT NULL, "Quantity" INTEGER NOT NULL, "Probability" REAL NOT NULL, PRIMARY KEY ("TypeId", "Activity", "ProductTypeId"))""",
            """CREATE TABLE IF NOT EXISTS "HoboBlueprintSkills" ("TypeId" INTEGER NOT NULL, "Activity" TEXT NOT NULL, "SkillTypeId" INTEGER NOT NULL, "Level" INTEGER NOT NULL, PRIMARY KEY ("TypeId", "Activity", "SkillTypeId"))""",
            """CREATE TABLE IF NOT EXISTS "HoboTypeMaterials" ("TypeId" INTEGER NOT NULL, "MaterialTypeId" INTEGER NOT NULL, "Quantity" INTEGER NOT NULL, PRIMARY KEY ("TypeId", "MaterialTypeId"))""",
            """CREATE TABLE IF NOT EXISTS "HoboRepackagedVolumes" ("TypeId" INTEGER NOT NULL PRIMARY KEY, "Volume" REAL NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "HoboCompressibleTypes" ("SourceTypeId" INTEGER NOT NULL PRIMARY KEY, "CompressedTypeId" INTEGER NOT NULL)""",
        };
        foreach (var sql in ddl)
            await db.Database.ExecuteSqlRawAsync(sql, ct);

        // ALTER TABLE ADD COLUMN for databases that predate the column.
        // SQLite ALTER TABLE does not support IF NOT EXISTS, so the duplicate-column error is
        // caught, exactly as EnsureSdeSchemaAsync does it.
        var alters = new[]
        {
            """ALTER TABLE "HoboBuildInfos" ADD COLUMN "Revision" INTEGER NOT NULL DEFAULT 0""",
        };
        foreach (var sql in alters)
        {
            try { await db.Database.ExecuteSqlRawAsync(sql, ct); }
            catch { /* column already exists - idempotent */ }
        }
    }

    // -----------------------------------------------------------------------
    // Section importers
    // -----------------------------------------------------------------------

    private async Task ImportBlueprintsAsync(AppDbContext db, byte[] json,
        IProgress<HoboImportProgress> p, CancellationToken ct)
    {
        Report(p, "Blueprints", "Parsing blueprints.json…", 0.23);
        var raw = JsonSerializer.Deserialize<Dictionary<string, HoboBpJson>>(json, _json) ?? [];

        Report(p, "Blueprints", $"Parsed {raw.Count:N0} blueprints — saving…", 0.25);

        var bps = raw.Select(kv => new HoboBlueprint
            { TypeId = int.Parse(kv.Key), MaxProductionLimit = kv.Value.MaxProductionLimit });

        var acts = raw.SelectMany(kv =>
            (kv.Value.Activities ?? []).Select(act => new HoboBlueprintActivity
                { TypeId = int.Parse(kv.Key), Activity = act.Key, Time = act.Value.Time }))
            .DistinctBy(x => (x.TypeId, x.Activity));

        var mats = raw.SelectMany(kv =>
            (kv.Value.Activities ?? []).SelectMany(act =>
                (act.Value.Materials ?? []).Select(m => new HoboBlueprintMaterial
                    { TypeId = int.Parse(kv.Key), Activity = act.Key, MaterialTypeId = m.TypeId, Quantity = m.Quantity })))
            .DistinctBy(x => (x.TypeId, x.Activity, x.MaterialTypeId));

        var prods = raw.SelectMany(kv =>
            (kv.Value.Activities ?? []).SelectMany(act =>
                (act.Value.Products ?? []).Select(pr => new HoboBlueprintProduct
                    { TypeId = int.Parse(kv.Key), Activity = act.Key, ProductTypeId = pr.TypeId, Quantity = pr.Quantity, Probability = pr.Probability })))
            .DistinctBy(x => (x.TypeId, x.Activity, x.ProductTypeId));

        var skills = raw.SelectMany(kv =>
            (kv.Value.Activities ?? []).SelectMany(act =>
                (act.Value.Skills ?? []).Select(sk => new HoboBlueprintSkill
                    { TypeId = int.Parse(kv.Key), Activity = act.Key, SkillTypeId = sk.TypeId, Level = sk.Level })))
            .DistinctBy(x => (x.TypeId, x.Activity, x.SkillTypeId));

        await SaveBatchesAsync(db, db.HoboBlueprints,          bps,    "Blueprints",          raw.Count, p, 0.25, 0.40, ct);
        await SaveBatchesAsync(db, db.HoboBlueprintActivities, acts,   "Blueprint Activities", -1,        p, 0.40, 0.50, ct);
        await SaveBatchesAsync(db, db.HoboBlueprintMaterials,  mats,   "Blueprint Materials",  -1,        p, 0.50, 0.65, ct);
        await SaveBatchesAsync(db, db.HoboBlueprintProducts,   prods,  "Blueprint Products",   -1,        p, 0.65, 0.75, ct);
        await SaveBatchesAsync(db, db.HoboBlueprintSkills,     skills, "Blueprint Skills",     -1,        p, 0.75, 0.83, ct);
    }

    private async Task ImportTypeMaterialsAsync(AppDbContext db, byte[] json,
        IProgress<HoboImportProgress> p, CancellationToken ct)
    {
        Report(p, "Type Materials", "Parsing typematerials.json…", 0.83);
        var raw = JsonSerializer.Deserialize<Dictionary<string, HoboTypeMatsJson>>(json, _json) ?? [];

        var rows = raw.SelectMany(kv =>
            (kv.Value.Materials ?? []).Select(m => new HoboTypeMaterial
                { TypeId = int.Parse(kv.Key), MaterialTypeId = m.MaterialTypeId, Quantity = m.Quantity }))
            .DistinctBy(x => (x.TypeId, x.MaterialTypeId));

        await SaveBatchesAsync(db, db.HoboTypeMaterials, rows, "Type Materials", -1, p, 0.83, 0.90, ct);
    }

    private async Task ImportRepackagedVolumesAsync(AppDbContext db, byte[] json,
        IProgress<HoboImportProgress> p, CancellationToken ct)
    {
        Report(p, "Repackaged Volumes", "Parsing repackagedvolumes.json…", 0.90);
        var raw = JsonSerializer.Deserialize<Dictionary<string, double>>(json, _json) ?? [];

        var rows = raw.Select(kv => new HoboRepackagedVolume
            { TypeId = int.Parse(kv.Key), Volume = kv.Value });

        await SaveBatchesAsync(db, db.HoboRepackagedVolumes, rows, "Repackaged Volumes", raw.Count, p, 0.90, 0.95, ct);
    }

    private async Task ImportCompressibleTypesAsync(AppDbContext db, byte[] json,
        IProgress<HoboImportProgress> p, CancellationToken ct)
    {
        Report(p, "Compressible Types", "Parsing compressibletypes.json…", 0.95);
        var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(json, _json) ?? [];

        var rows = raw.Select(kv => new HoboCompressibleType
            { SourceTypeId = int.Parse(kv.Key), CompressedTypeId = kv.Value });

        await SaveBatchesAsync(db, db.HoboCompressibleTypes, rows, "Compressible Types", raw.Count, p, 0.95, 0.99, ct);
    }

    // -----------------------------------------------------------------------
    // Batch save helper
    // -----------------------------------------------------------------------

    private async Task SaveBatchesAsync<T>(
        AppDbContext db,
        Microsoft.EntityFrameworkCore.DbSet<T> set,
        IEnumerable<T> source,
        string stage,
        int estimatedTotal,
        IProgress<HoboImportProgress> p,
        double fracStart,
        double fracEnd,
        CancellationToken ct) where T : class
    {
        var buffer = new List<T>(Batch);
        int saved  = 0;

        foreach (var item in source)
        {
            buffer.Add(item);
            if (buffer.Count >= Batch)
            {
                await set.AddRangeAsync(buffer, ct);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                saved += buffer.Count;
                buffer.Clear();

                var frac   = estimatedTotal > 0
                    ? Math.Clamp(fracStart + (fracEnd - fracStart) * ((double)saved / estimatedTotal), fracStart, fracEnd)
                    : fracStart;
                var detail = estimatedTotal > 0 ? $"{saved:N0} / {estimatedTotal:N0}" : $"{saved:N0} rows";
                p.Report(new HoboImportProgress(stage, detail, frac));
            }
        }

        if (buffer.Count > 0)
        {
            await set.AddRangeAsync(buffer, ct);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            saved += buffer.Count;
        }

        p.Report(new HoboImportProgress(stage, $"{saved:N0} rows saved", fracEnd));

        // ⚠️ A stage that stores NOTHING from a file that downloaded is a silent failure, and this
        // import is a wipe followed by a refill: whatever it fails to store is simply gone. The
        // SDE import lost ten tables that way, type materials among them, and nothing said so.
        if (saved == 0)
            _errors.Log("HoboImport", stage,
                "Stored 0 rows: the file was downloaded and parsed to nothing.");
    }

    private static void Report(IProgress<HoboImportProgress> p, string stage, string detail, double frac)
        => p.Report(new HoboImportProgress(stage, detail, frac));

    // -----------------------------------------------------------------------
    // JSON DTOs
    // -----------------------------------------------------------------------

    private sealed class MetaJson
    {
        [JsonPropertyName("files")]
        public Dictionary<string, MetaFileJson>? Files { get; set; }
    }

    private sealed class MetaFileJson
    {
        [JsonPropertyName("deprecated")] public bool    Deprecated { get; set; }
        [JsonPropertyName("stale")]      public bool    Stale      { get; set; }
        [JsonPropertyName("md5")]        public string? Md5        { get; set; }
        [JsonPropertyName("revision")]   public long    Revision   { get; set; }
    }

    private sealed class HoboBpJson
    {
        [JsonPropertyName("maxProductionLimit")]
        public int MaxProductionLimit { get; set; }

        [JsonPropertyName("activities")]
        public Dictionary<string, HoboBpActivityJson>? Activities { get; set; }
    }

    private sealed class HoboBpActivityJson
    {
        [JsonPropertyName("time")]
        public int Time { get; set; }

        [JsonPropertyName("materials")]
        public List<HoboBpMatJson>? Materials { get; set; }

        [JsonPropertyName("products")]
        public List<HoboBpProdJson>? Products { get; set; }

        [JsonPropertyName("skills")]
        public List<HoboBpSkillJson>? Skills { get; set; }
    }

    private sealed class HoboBpMatJson
    {
        [JsonPropertyName("typeID")]
        public int TypeId { get; set; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }
    }

    private sealed class HoboBpProdJson
    {
        [JsonPropertyName("typeID")]
        public int TypeId { get; set; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("probability")]
        public double Probability { get; set; }
    }

    private sealed class HoboBpSkillJson
    {
        [JsonPropertyName("typeID")]
        public int TypeId { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    private sealed class HoboTypeMatsJson
    {
        [JsonPropertyName("materials")]
        public List<HoboTypeMaterialEntryJson>? Materials { get; set; }
    }

    private sealed class HoboTypeMaterialEntryJson
    {
        [JsonPropertyName("materialTypeID")]
        public int MaterialTypeId { get; set; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }
    }
}
