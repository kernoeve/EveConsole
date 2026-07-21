using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public record HoboImportProgress(string Stage, string Detail, double Fraction);

public class HoboImportService
{
    private const string BaseUrl = "https://sde.hoboleaks.space/tq/";
    private const int    Batch   = 2000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory   _httpFactory;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public HoboImportService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory)
    {
        _scopeFactory = scopeFactory;
        _httpFactory  = httpFactory;
    }

    public async Task ImportAsync(IProgress<HoboImportProgress> progress, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL",   ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA cache_size=20000",   ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA temp_store=MEMORY",  ct);

        Report(progress, "Preparing", "Creating Hobo schema…", 0.01);
        await EnsureHoboSchemaAsync(db, ct);

        Report(progress, "Preparing", "Clearing existing Hobo data…", 0.02);
        await ClearHoboTablesAsync(db, ct);

        db.ChangeTracker.AutoDetectChangesEnabled = false;

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        await ImportBlueprintsAsync(db, http, progress, ct);
        await ImportTypeMaterialsAsync(db, http, progress, ct);
        await ImportRepackagedVolumesAsync(db, http, progress, ct);
        await ImportCompressibleTypesAsync(db, http, progress, ct);

        // Save import timestamp
        db.ChangeTracker.AutoDetectChangesEnabled = true;
        var existing = await db.HoboBuildInfos.FindAsync([1], ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
            db.HoboBuildInfos.Add(new HoboBuildInfo { Id = 1, ImportedAt = now });
        else
            existing.ImportedAt = now;
        await db.SaveChangesAsync(ct);

        Report(progress, "Done", "Hoboleaks import complete.", 1.0);
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
            """CREATE TABLE IF NOT EXISTS "HoboBuildInfos" ("Id" INTEGER NOT NULL PRIMARY KEY, "ImportedAt" TEXT NOT NULL)""",
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
    }

    private static async Task ClearHoboTablesAsync(AppDbContext db, CancellationToken ct)
    {
        var deletes = new[]
        {
            "DELETE FROM \"HoboBlueprintSkills\"",    "DELETE FROM \"HoboBlueprintProducts\"",
            "DELETE FROM \"HoboBlueprintMaterials\"", "DELETE FROM \"HoboBlueprintActivities\"",
            "DELETE FROM \"HoboBlueprints\"",
            "DELETE FROM \"HoboTypeMaterials\"",      "DELETE FROM \"HoboRepackagedVolumes\"",
            "DELETE FROM \"HoboCompressibleTypes\"",
        };
        foreach (var sql in deletes)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    // -----------------------------------------------------------------------
    // Section importers
    // -----------------------------------------------------------------------

    private async Task ImportBlueprintsAsync(AppDbContext db, HttpClient http,
        IProgress<HoboImportProgress> p, CancellationToken ct)
    {
        Report(p, "Blueprints", "Downloading blueprints.json…", 0.03);
        await using var stream = await http.GetStreamAsync($"{BaseUrl}blueprints.json", ct);

        var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, HoboBpJson>>(stream, _json, ct)
                  ?? [];

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

    private async Task ImportTypeMaterialsAsync(AppDbContext db, HttpClient http,
        IProgress<HoboImportProgress> p, CancellationToken ct)
    {
        Report(p, "Type Materials", "Downloading typematerials.json…", 0.83);
        await using var stream = await http.GetStreamAsync($"{BaseUrl}typematerials.json", ct);

        var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, HoboTypeMatsJson>>(stream, _json, ct)
                  ?? [];

        var rows = raw.SelectMany(kv =>
            (kv.Value.Materials ?? []).Select(m => new HoboTypeMaterial
                { TypeId = int.Parse(kv.Key), MaterialTypeId = m.MaterialTypeId, Quantity = m.Quantity }))
            .DistinctBy(x => (x.TypeId, x.MaterialTypeId));

        await SaveBatchesAsync(db, db.HoboTypeMaterials, rows, "Type Materials", -1, p, 0.83, 0.90, ct);
    }

    private async Task ImportRepackagedVolumesAsync(AppDbContext db, HttpClient http,
        IProgress<HoboImportProgress> p, CancellationToken ct)
    {
        Report(p, "Repackaged Volumes", "Downloading repackagedvolumes.json…", 0.90);
        await using var stream = await http.GetStreamAsync($"{BaseUrl}repackagedvolumes.json", ct);

        var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, double>>(stream, _json, ct)
                  ?? [];

        var rows = raw.Select(kv => new HoboRepackagedVolume
            { TypeId = int.Parse(kv.Key), Volume = kv.Value });

        await SaveBatchesAsync(db, db.HoboRepackagedVolumes, rows, "Repackaged Volumes", raw.Count, p, 0.90, 0.95, ct);
    }

    private async Task ImportCompressibleTypesAsync(AppDbContext db, HttpClient http,
        IProgress<HoboImportProgress> p, CancellationToken ct)
    {
        Report(p, "Compressible Types", "Downloading compressibletypes.json…", 0.95);
        await using var stream = await http.GetStreamAsync($"{BaseUrl}compressibletypes.json", ct);

        var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, int>>(stream, _json, ct)
                  ?? [];

        var rows = raw.Select(kv => new HoboCompressibleType
            { SourceTypeId = int.Parse(kv.Key), CompressedTypeId = kv.Value });

        await SaveBatchesAsync(db, db.HoboCompressibleTypes, rows, "Compressible Types", raw.Count, p, 0.95, 0.99, ct);
    }

    // -----------------------------------------------------------------------
    // Batch save helper
    // -----------------------------------------------------------------------

    private static async Task SaveBatchesAsync<T>(
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
    }

    private static void Report(IProgress<HoboImportProgress> p, string stage, string detail, double frac)
        => p.Report(new HoboImportProgress(stage, detail, frac));

    // -----------------------------------------------------------------------
    // JSON DTOs
    // -----------------------------------------------------------------------

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
