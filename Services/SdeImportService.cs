using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace EveConsole.Services;

public record SdeImportProgress(string Stage, string Detail, double Fraction);

// Thrown when the SDE archive is missing files this version of EVE Console requires.
// The existing SDE data is left intact so the app remains functional.
public class SdeCompatibilityException : Exception
{
    public IReadOnlyList<string> MissingFiles { get; }
    public SdeCompatibilityException(IReadOnlyList<string> missing)
        : base(BuildMessage(missing))
    {
        MissingFiles = missing;
    }
    private static string BuildMessage(IReadOnlyList<string> missing) =>
        $"EVE Console needs to be updated before it can refresh the SDE. " +
        $"The following required file(s) were not found in the archive " +
        $"(CCP may have restructured the SDE format): {string.Join(", ", missing)}. " +
        $"Your existing SDE data has NOT been cleared.";
}

public class SdeImportService
{
    // CCP moved to a new URL and flat file structure (no fsd/ or bsd/ subdirectories).
    // The old amazonaws URL served a stale July 2025 file.
    private const string SdeUrl      = "https://developers.eveonline.com/static-data/eve-online-static-data-latest-yaml.zip";
    private const string SdeBuildUrl = "https://developers.eveonline.com/static-data/tranquility/latest.jsonl";
    private const int    Batch       = 2000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory   _httpFactory;
    private readonly IDeserializer        _yaml;

    public SdeImportService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory)
    {
        _scopeFactory = scopeFactory;
        _httpFactory  = httpFactory;
        _yaml = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
    }

    // -----------------------------------------------------------------------
    // Entry point
    // -----------------------------------------------------------------------

    // Fetch the latest build metadata from CCP's index without running a full import.
    public async Task<SdeBuildInfo?> GetLatestBuildInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            var json = await http.GetStringAsync(SdeBuildUrl, ct);
            var dto  = JsonSerializer.Deserialize<BuildInfoDto>(json);
            if (dto is null) return null;
            return new SdeBuildInfo { Id = 1, BuildNumber = dto.BuildNumber, ReleaseDate = dto.ReleaseDate };
        }
        catch { return null; }
    }

    public async Task ImportAsync(IProgress<SdeImportProgress> progress, CancellationToken ct)
    {
        Report(progress, "Preparing", "Fetching build info…", 0.01);
        var buildInfo = await GetLatestBuildInfoAsync(ct);

        var tempPath = await DownloadAsync(progress, ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL",   ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA cache_size=20000",   ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA temp_store=MEMORY",  ct);

            // Open archive and validate BEFORE touching any existing data.
            // Throws SdeCompatibilityException if required files are missing,
            // leaving existing SDE tables intact so the app stays functional.
            using var archive = ZipFile.OpenRead(tempPath);
            var fsdRoot = DetectRoot(archive, progress);
            ValidateArchive(archive, fsdRoot, progress);

            Report(progress, "Preparing", "Creating schema…",            0.30);
            await EnsureSdeSchemaAsync(db, ct);

            Report(progress, "Preparing", "Clearing existing SDE data…", 0.31);
            await ClearSdeTablesAsync(db, ct);

            db.ChangeTracker.AutoDetectChangesEnabled = false;

            await ImportCategoriesAsync(archive, fsdRoot, db, progress, ct);
            await ImportGroupsAsync(archive, fsdRoot, db, progress, ct);
            await ImportMarketGroupsAsync(archive, fsdRoot, db, progress, ct);
            await ImportTypesAsync(archive, fsdRoot, db, progress, ct);
            await ImportDogmaAttributeCategoriesAsync(archive, fsdRoot, db, progress, ct);
            await ImportDogmaAttributesAsync(archive, fsdRoot, db, progress, ct);
            await ImportDogmaEffectsAsync(archive, fsdRoot, db, progress, ct);
            await ImportTypeDogmaAsync(archive, fsdRoot, db, progress, ct);
            await ImportBlueprintsAsync(archive, fsdRoot, db, progress, ct);
            await ImportUniverseAsync(archive, fsdRoot, db, progress, ct);
            await ImportStationsAsync(archive, fsdRoot, db, progress, ct);
            await ImportAgentsAsync(archive, fsdRoot, db, progress, ct);
            await ImportFactionsAsync(archive, fsdRoot, db, progress, ct);
            await ImportNpcCorporationsAsync(archive, fsdRoot, db, progress, ct);
            await ImportRacesAsync(archive, fsdRoot, db, progress, ct);
            await ImportMetaGroupsAsync(archive, fsdRoot, db, progress, ct);
            await ImportCertificatesAsync(archive, fsdRoot, db, progress, ct);
            await ImportTypeMaterialsAsync(archive, fsdRoot, db, progress, ct);
            await ImportPlanetSchematicsAsync(archive, fsdRoot, db, progress, ct);
            await ImportDogmaUnitsAsync(archive, fsdRoot, db, progress, ct);
            await ImportIconsAsync(archive, fsdRoot, db, progress, ct);
            await ImportGraphicsAsync(archive, fsdRoot, db, progress, ct);
            await ImportSkinsAsync(archive, fsdRoot, db, progress, ct);
            await ImportSkinLicensesAsync(archive, fsdRoot, db, progress, ct);

            // Save build metadata (upsert the single row).
            if (buildInfo is not null)
            {
                db.ChangeTracker.AutoDetectChangesEnabled = true;
                buildInfo.ImportedAt = DateTimeOffset.UtcNow;
                var existing = await db.SdeBuildInfos.FindAsync([1], ct);
                if (existing is null)
                    db.SdeBuildInfos.Add(buildInfo);
                else
                {
                    existing.BuildNumber = buildInfo.BuildNumber;
                    existing.ReleaseDate = buildInfo.ReleaseDate;
                    existing.ImportedAt  = buildInfo.ImportedAt;
                }
                await db.SaveChangesAsync(ct);
            }

            Report(progress, "Done", "SDE import complete.", 1.0);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private record BuildInfoDto(
        [property: JsonPropertyName("buildNumber")] int            BuildNumber,
        [property: JsonPropertyName("releaseDate")]  DateTimeOffset ReleaseDate);

    // Checks that every file this importer requires is present in the archive.
    // Must be called BEFORE ClearSdeTablesAsync so existing data stays intact on failure.
    // Optional files (new tables, supplemental data) are checked separately and only logged as warnings.
    private static void ValidateArchive(ZipArchive archive, string fsdRoot, IProgress<SdeImportProgress> p)
    {
        // Files we actively import and whose absence leaves a core table empty.
        var required = new[]
        {
            "types.yaml",
            "groups.yaml",
            "categories.yaml",
            "marketGroups.yaml",
            "blueprints.yaml",
            "typeDogma.yaml",
            "dogmaAttributes.yaml",
            "dogmaEffects.yaml",
            "factions.yaml",
            "npcCorporations.yaml",
            "metaGroups.yaml",
        };

        var missing = required
            .Where(f => archive.GetEntry($"{fsdRoot}{f}") is null)
            .ToList();

        // Universe: new flat map files OR old nested universe/ directory — either is fine.
        bool hasUniverse = archive.GetEntry($"{fsdRoot}mapRegions.yaml") is not null
            || archive.Entries.Any(e => e.FullName.Contains("/universe/", StringComparison.Ordinal));
        if (!hasUniverse)
            missing.Add("mapRegions.yaml (universe data)");

        if (missing.Count > 0)
            throw new SdeCompatibilityException(missing);

        // Optional files — missing means we silently skip that importer, not a hard failure.
        var optional = new[]
        {
            "dogmaAttributeCategories.yaml", "races.yaml", "certificates.yaml",
            "typeMaterials.yaml", "planetSchematics.yaml",
            "dogmaUnits.yaml", "icons.yaml", "graphics.yaml", "skins.yaml", "skinLicenses.yaml",
            "npcStations.yaml",
        };
        var missingOptional = optional.Where(f => archive.GetEntry($"{fsdRoot}{f}") is null).ToList();
        if (missingOptional.Count > 0)
            p.Report(new SdeImportProgress("Preparing",
                $"Optional files not found (will be skipped): {string.Join(", ", missingOptional)}", 0.315));
        else
            p.Report(new SdeImportProgress("Preparing", "All SDE files present.", 0.315));
    }

    // Returns the prefix to prepend before a filename. New flat SDE returns ""; old nested returns "fsd/" or "sde/fsd/".
    private static string DetectRoot(ZipArchive archive, IProgress<SdeImportProgress> p)
    {
        // New SDE format: flat — files at root level (e.g. "categories.yaml")
        if (archive.GetEntry("categories.yaml") != null)
        {
            p.Report(new SdeImportProgress("Preparing", "New flat SDE format detected", 0.31));
            return "";
        }

        // Old format: look for fsd/categories.yaml under an optional root prefix
        const string probe = "fsd/categories.yaml";
        foreach (var e in archive.Entries)
        {
            if (!e.FullName.EndsWith(probe, StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = e.FullName[..^"categories.yaml".Length];  // includes "fsd/"
            p.Report(new SdeImportProgress("Preparing", $"Old nested SDE format, prefix: \"{prefix}\"", 0.31));
            return prefix;
        }

        p.Report(new SdeImportProgress("Warning", "Could not detect ZIP root — assuming flat", 0.31));
        return "";
    }

    // -----------------------------------------------------------------------
    // Download
    // -----------------------------------------------------------------------

    private async Task<string> DownloadAsync(IProgress<SdeImportProgress> progress, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "eve-sde.zip");
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromHours(2);

        using var response = await http.GetAsync(SdeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file   = File.Create(tempPath);

        var buffer = new byte[131_072];
        long downloaded = 0;
        int  read;

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            var frac   = total > 0 ? (double)downloaded / total : 0;
            var detail = total > 0
                ? $"{downloaded / 1_048_576:N0} MB / {total / 1_048_576:N0} MB"
                : $"{downloaded / 1_048_576:N0} MB";
            Report(progress, "Downloading SDE", detail, frac * 0.30);
        }

        return tempPath;
    }

    // -----------------------------------------------------------------------
    // Schema + clear
    // -----------------------------------------------------------------------

    private static async Task EnsureSdeSchemaAsync(AppDbContext db, CancellationToken ct)
    {
        // CREATE TABLE IF NOT EXISTS — idempotent for the full table definition
        var creates = new[]
        {
            """CREATE TABLE IF NOT EXISTS "SdeBuildInfos" ("Id" INTEGER NOT NULL PRIMARY KEY, "BuildNumber" INTEGER NOT NULL, "ReleaseDate" TEXT NOT NULL, "ImportedAt" TEXT NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeCategories" ("CategoryId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "Published" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeGroups" ("GroupId" INTEGER NOT NULL PRIMARY KEY, "CategoryId" INTEGER NOT NULL, "Name" TEXT NOT NULL, "Published" INTEGER NOT NULL, "Anchorable" INTEGER NOT NULL DEFAULT 0, "Anchored" INTEGER NOT NULL DEFAULT 0)""",
            """CREATE TABLE IF NOT EXISTS "SdeMarketGroups" ("MarketGroupId" INTEGER NOT NULL PRIMARY KEY, "ParentGroupId" INTEGER, "Name" TEXT NOT NULL, "Description" TEXT NOT NULL, "IconId" INTEGER, "HasTypes" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeTypes" ("TypeId" INTEGER NOT NULL PRIMARY KEY, "GroupId" INTEGER NOT NULL, "Name" TEXT NOT NULL, "Description" TEXT NOT NULL, "Volume" REAL NOT NULL, "Mass" REAL NOT NULL, "Capacity" REAL NOT NULL, "PortionSize" INTEGER NOT NULL, "BasePrice" REAL, "MarketGroupId" INTEGER, "IconId" INTEGER, "GraphicId" INTEGER, "FactionId" INTEGER, "RaceId" INTEGER, "MetaGroupId" INTEGER, "Published" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeDogmaAttributeCategories" ("CategoryId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeDogmaAttributes" ("AttributeId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "DisplayName" TEXT NOT NULL, "CategoryId" INTEGER, "DefaultValue" REAL NOT NULL, "HighIsGood" INTEGER NOT NULL, "Stackable" INTEGER NOT NULL, "UnitId" INTEGER, "Published" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeDogmaEffects" ("EffectId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "DisplayName" TEXT NOT NULL, "Description" TEXT NOT NULL, "IsOffensive" INTEGER NOT NULL, "IsAssistance" INTEGER NOT NULL, "Published" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeTypeDogmaAttributes" ("TypeId" INTEGER NOT NULL, "AttributeId" INTEGER NOT NULL, "Value" REAL NOT NULL, PRIMARY KEY ("TypeId", "AttributeId"))""",
            """CREATE TABLE IF NOT EXISTS "SdeTypeDogmaEffects" ("TypeId" INTEGER NOT NULL, "EffectId" INTEGER NOT NULL, "IsDefault" INTEGER NOT NULL, PRIMARY KEY ("TypeId", "EffectId"))""",
            """CREATE TABLE IF NOT EXISTS "SdeBlueprints" ("TypeId" INTEGER NOT NULL PRIMARY KEY, "MaxProductionLimit" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeBlueprintMaterials" ("TypeId" INTEGER NOT NULL, "Activity" TEXT NOT NULL, "MaterialTypeId" INTEGER NOT NULL, "Quantity" INTEGER NOT NULL, PRIMARY KEY ("TypeId", "Activity", "MaterialTypeId"))""",
            """CREATE TABLE IF NOT EXISTS "SdeBlueprintProducts" ("TypeId" INTEGER NOT NULL, "Activity" TEXT NOT NULL, "ProductTypeId" INTEGER NOT NULL, "Quantity" INTEGER NOT NULL, "Probability" REAL NOT NULL, PRIMARY KEY ("TypeId", "Activity", "ProductTypeId"))""",
            """CREATE TABLE IF NOT EXISTS "SdeBlueprintSkills" ("TypeId" INTEGER NOT NULL, "Activity" TEXT NOT NULL, "SkillTypeId" INTEGER NOT NULL, "Level" INTEGER NOT NULL, PRIMARY KEY ("TypeId", "Activity", "SkillTypeId"))""",
            // Map geometry: X/Y/Z are galactic metres in CCP's left-handed frame (+X east,
            // +Y up, +Z north). X2D/Y2D is CCP's own published 2D map layout and is NULL
            // outside New Eden — only systems 30000000-30999999 carry it, which is exactly
            // the set the in-game map draws.
            """CREATE TABLE IF NOT EXISTS "SdeRegions" ("RegionId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "FactionId" INTEGER, "IsWormhole" INTEGER NOT NULL, "X" REAL NOT NULL DEFAULT 0, "Y" REAL NOT NULL DEFAULT 0, "Z" REAL NOT NULL DEFAULT 0)""",
            """CREATE TABLE IF NOT EXISTS "SdeConstellations" ("ConstellationId" INTEGER NOT NULL PRIMARY KEY, "RegionId" INTEGER NOT NULL, "Name" TEXT NOT NULL, "IsWormhole" INTEGER NOT NULL, "X" REAL NOT NULL DEFAULT 0, "Y" REAL NOT NULL DEFAULT 0, "Z" REAL NOT NULL DEFAULT 0)""",
            """CREATE TABLE IF NOT EXISTS "SdeSolarSystems" ("SolarSystemId" INTEGER NOT NULL PRIMARY KEY, "ConstellationId" INTEGER NOT NULL, "RegionId" INTEGER NOT NULL, "Name" TEXT NOT NULL, "Security" REAL NOT NULL, "FactionId" INTEGER, "IsWormhole" INTEGER NOT NULL, "X" REAL NOT NULL DEFAULT 0, "Y" REAL NOT NULL DEFAULT 0, "Z" REAL NOT NULL DEFAULT 0, "X2D" REAL, "Y2D" REAL, "SecurityClass" TEXT NOT NULL DEFAULT '', "Radius" REAL NOT NULL DEFAULT 0)""",
            """CREATE TABLE IF NOT EXISTS "SdeStargates" ("StargateId" INTEGER NOT NULL PRIMARY KEY, "SolarSystemId" INTEGER NOT NULL, "DestinationStargateId" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeCelestials" ("ItemId" INTEGER NOT NULL PRIMARY KEY, "SolarSystemId" INTEGER NOT NULL, "TypeId" INTEGER NOT NULL, "Kind" INTEGER NOT NULL, "X" REAL NOT NULL, "Y" REAL NOT NULL, "Z" REAL NOT NULL, "Name" TEXT NOT NULL)""",
            """CREATE INDEX IF NOT EXISTS "IX_SdeCelestials_System" ON "SdeCelestials" ("SolarSystemId")""",
            """CREATE TABLE IF NOT EXISTS "SdeAgents" ("AgentId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '', "CorporationId" INTEGER NOT NULL DEFAULT 0, "LocationId" INTEGER NOT NULL DEFAULT 0, "AgentTypeId" INTEGER NOT NULL DEFAULT 0, "DivisionId" INTEGER NOT NULL DEFAULT 0, "Level" INTEGER NOT NULL DEFAULT 0, "IsLocator" INTEGER NOT NULL DEFAULT 0)""",
            """CREATE INDEX IF NOT EXISTS "IX_SdeAgents_Location" ON "SdeAgents" ("LocationId")""",
            """CREATE TABLE IF NOT EXISTS "SdeAgentTypes" ("AgentTypeId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '')""",
            """CREATE TABLE IF NOT EXISTS "SdeCorpDivisions" ("DivisionId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '')""",
            """CREATE TABLE IF NOT EXISTS "SdePlanetResources" ("PlanetId" INTEGER NOT NULL PRIMARY KEY, "Power" INTEGER NOT NULL DEFAULT 0, "Workforce" INTEGER NOT NULL DEFAULT 0, "ReagentPerCycle" INTEGER NOT NULL DEFAULT 0, "ReagentCycleTime" INTEGER NOT NULL DEFAULT 0, "SecuredCapacity" INTEGER NOT NULL DEFAULT 0)""",
            """CREATE TABLE IF NOT EXISTS "SdeStations" ("StationId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "SolarSystemId" INTEGER NOT NULL, "ConstellationId" INTEGER NOT NULL, "RegionId" INTEGER NOT NULL, "CorporationId" INTEGER, "StationTypeId" INTEGER, "Security" REAL NOT NULL, "ReprocessingEfficiency" REAL NOT NULL, "ReprocessingTax" REAL NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeFactions" ("FactionId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "Description" TEXT NOT NULL, "CorporationId" INTEGER, "MilitiaCorporationId" INTEGER, "SolarSystemId" INTEGER)""",
            """CREATE TABLE IF NOT EXISTS "SdeNpcCorporations" ("CorporationId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "FactionId" INTEGER)""",
            """CREATE TABLE IF NOT EXISTS "SdeRaces" ("RaceId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "Description" TEXT NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeMetaGroups" ("MetaGroupId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeCertificates" ("CertificateId" INTEGER NOT NULL PRIMARY KEY, "GroupId" INTEGER NOT NULL, "Name" TEXT NOT NULL, "Description" TEXT NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeTypeMaterials" ("TypeId" INTEGER NOT NULL, "MaterialTypeId" INTEGER NOT NULL, "Quantity" INTEGER NOT NULL, PRIMARY KEY ("TypeId", "MaterialTypeId"))""",
            """CREATE TABLE IF NOT EXISTS "SdePlanetSchematics" ("SchematicId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "CycleTime" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdePlanetSchematicTypes" ("SchematicId" INTEGER NOT NULL, "TypeId" INTEGER NOT NULL, "IsInput" INTEGER NOT NULL, "Quantity" INTEGER NOT NULL, PRIMARY KEY ("SchematicId", "TypeId"))""",
            // New tables added in 2026 SDE
            """CREATE TABLE IF NOT EXISTS "SdeDogmaUnits" ("UnitId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "DisplayName" TEXT NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeIcons" ("IconId" INTEGER NOT NULL PRIMARY KEY, "IconFile" TEXT NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeGraphics" ("GraphicId" INTEGER NOT NULL PRIMARY KEY, "GraphicFile" TEXT)""",
            """CREATE TABLE IF NOT EXISTS "SdeSkins" ("SkinId" INTEGER NOT NULL PRIMARY KEY, "InternalName" TEXT NOT NULL, "SkinMaterialId" INTEGER, "VisibleTranquility" INTEGER NOT NULL)""",
            """CREATE TABLE IF NOT EXISTS "SdeSkinTypes" ("SkinId" INTEGER NOT NULL, "TypeId" INTEGER NOT NULL, PRIMARY KEY ("SkinId", "TypeId"))""",
            """CREATE TABLE IF NOT EXISTS "SdeSkinLicenses" ("LicenseTypeId" INTEGER NOT NULL PRIMARY KEY, "SkinId" INTEGER NOT NULL, "Duration" INTEGER NOT NULL)""",
        };
        foreach (var sql in creates)
            await db.Database.ExecuteSqlRawAsync(sql, ct);

        // ALTER TABLE ADD COLUMN for tables that existed before these columns were added.
        // SQLite ALTER TABLE does not support IF NOT EXISTS, so we catch the duplicate-column error.
        var alters = new[]
        {
            """ALTER TABLE "SdeGroups" ADD COLUMN "Anchorable" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeGroups" ADD COLUMN "Anchored"   INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeTypes"  ADD COLUMN "GraphicId"  INTEGER""",
            """ALTER TABLE "SdeTypes"  ADD COLUMN "FactionId"  INTEGER""",
            """ALTER TABLE "SdeTypes"  ADD COLUMN "RaceId"     INTEGER""",
            """ALTER TABLE "SdeTypes"  ADD COLUMN "MetaGroupId" INTEGER""",
            // Map geometry, added for the Universe tool.
            """ALTER TABLE "SdeRegions"        ADD COLUMN "X" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeRegions"        ADD COLUMN "Y" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeRegions"        ADD COLUMN "Z" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeConstellations" ADD COLUMN "X" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeConstellations" ADD COLUMN "Y" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeConstellations" ADD COLUMN "Z" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "X" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "Y" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "Z" REAL NOT NULL DEFAULT 0""",
            // Nullable on purpose: CCP publishes a 2D layout only for New Eden, so a NULL
            // here means "not on the in-game map" (wormhole, abyssal, Zarzakh) rather than
            // "at the origin".
            """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "X2D" REAL""",
            """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "Y2D" REAL""",
            """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "SecurityClass" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "Radius" REAL NOT NULL DEFAULT 0""",
        };
        foreach (var sql in alters)
        {
            try { await db.Database.ExecuteSqlRawAsync(sql, ct); }
            catch { /* column already exists — idempotent */ }
        }
    }

    private static async Task ClearSdeTablesAsync(AppDbContext db, CancellationToken ct)
    {
        // Delete in leaf-first order so FK constraints (if any ever get added) don't block.
        var deletes = new[]
        {
            "DELETE FROM \"SdeTypeDogmaAttributes\"", "DELETE FROM \"SdeTypeDogmaEffects\"",
            "DELETE FROM \"SdeBlueprintMaterials\"",  "DELETE FROM \"SdeBlueprintProducts\"",
            "DELETE FROM \"SdeBlueprintSkills\"",     "DELETE FROM \"SdeBlueprints\"",
            "DELETE FROM \"SdeStargates\"",           "DELETE FROM \"SdeStations\"",
            "DELETE FROM \"SdePlanetResources\"",
            "DELETE FROM \"SdeAgents\"", "DELETE FROM \"SdeAgentTypes\"",
            "DELETE FROM \"SdeCorpDivisions\"",
            "DELETE FROM \"SdeCelestials\"",
            "DELETE FROM \"SdeSolarSystems\"",        "DELETE FROM \"SdeConstellations\"",
            "DELETE FROM \"SdeRegions\"",             "DELETE FROM \"SdeTypes\"",
            "DELETE FROM \"SdeGroups\"",              "DELETE FROM \"SdeCategories\"",
            "DELETE FROM \"SdeMarketGroups\"",        "DELETE FROM \"SdeDogmaAttributeCategories\"",
            "DELETE FROM \"SdeDogmaAttributes\"",
            "DELETE FROM \"SdeDogmaEffects\"",        "DELETE FROM \"SdeFactions\"",
            "DELETE FROM \"SdeNpcCorporations\"",     "DELETE FROM \"SdeRaces\"",
            "DELETE FROM \"SdeMetaGroups\"",          "DELETE FROM \"SdeCertificates\"",
            "DELETE FROM \"SdeTypeMaterials\"",       "DELETE FROM \"SdePlanetSchematicTypes\"",
            "DELETE FROM \"SdePlanetSchematics\"",    "DELETE FROM \"SdeDogmaUnits\"",
            "DELETE FROM \"SdeIcons\"",               "DELETE FROM \"SdeGraphics\"",
            "DELETE FROM \"SdeSkinTypes\"",           "DELETE FROM \"SdeSkins\"",
            "DELETE FROM \"SdeSkinLicenses\"",
        };
        foreach (var sql in deletes)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    // -----------------------------------------------------------------------
    // Section importers
    // -----------------------------------------------------------------------

    private async Task ImportCategoriesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}categories.yaml");
        if (entry is null) { Report(p, "Categories", "NOT FOUND in ZIP — skipped", 0.32); return; }
        Report(p, "Categories", "Parsing…", 0.32);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, CategoryYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeCategory { CategoryId = kv.Key, Name = kv.Value.name?.en ?? "", Published = kv.Value.published });
        await SaveBatchesAsync(db, db.SdeCategories, rows, "Categories", raw.Count, p, 0.32, 0.33, ct);
    }

    private async Task ImportGroupsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}groups.yaml");
        if (entry is null) { Report(p, "Groups", "NOT FOUND in ZIP — skipped", 0.33); return; }
        Report(p, "Groups", "Parsing…", 0.33);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, GroupYaml>>(reader) ?? [];
        Report(p, "Groups", $"Parsed {raw.Count:N0} groups from YAML — saving…", 0.335);
        var rows = raw.Select(kv => new SdeGroup
        {
            GroupId    = kv.Key,
            CategoryId = kv.Value.categoryID,
            Name       = kv.Value.name?.en ?? "",
            Published  = kv.Value.published,
            Anchorable = kv.Value.anchorable,
            Anchored   = kv.Value.anchored,
        });
        await SaveBatchesAsync(db, db.SdeGroups, rows, "Groups", raw.Count, p, 0.335, 0.35, ct);
    }

    private async Task ImportMarketGroupsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}marketGroups.yaml");
        if (entry is null) { Report(p, "Market Groups", "NOT FOUND in ZIP — skipped", 0.35); return; }
        Report(p, "Market Groups", "Parsing…", 0.35);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, MarketGroupYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeMarketGroup
        {
            MarketGroupId = kv.Key, ParentGroupId = kv.Value.parentGroupID,
            Name        = kv.Value.nameID?.en        ?? kv.Value.name?.en        ?? "",
            Description = kv.Value.descriptionID?.en ?? kv.Value.description?.en ?? "",
            IconId = kv.Value.iconID, HasTypes = kv.Value.hasTypes,
        });
        await SaveBatchesAsync(db, db.SdeMarketGroups, rows, "Market Groups", raw.Count, p, 0.35, 0.36, ct);
    }

    private async Task ImportTypesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}types.yaml");
        if (entry is null) { Report(p, "Types", "NOT FOUND in ZIP — skipped", 0.36); return; }
        Report(p, "Types", "Parsing types.yaml (large)…", 0.36);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, TypeYaml>>(reader) ?? [];
        Report(p, "Types", $"Parsed {raw.Count:N0} types from YAML — saving…", 0.37);
        var rows = raw.Select(kv => new SdeType
        {
            TypeId        = kv.Key,
            GroupId       = kv.Value.groupID,
            Name          = kv.Value.nameID?.en        ?? kv.Value.name?.en        ?? "",
            Description   = kv.Value.descriptionID?.en ?? kv.Value.description?.en ?? "",
            Volume        = kv.Value.volume,
            Mass          = kv.Value.mass,
            Capacity      = kv.Value.capacity,
            PortionSize   = kv.Value.portionSize,
            BasePrice     = kv.Value.basePrice,
            MarketGroupId = kv.Value.marketGroupID,
            IconId        = kv.Value.iconID,
            GraphicId     = kv.Value.graphicID,
            FactionId     = kv.Value.factionID,
            RaceId        = kv.Value.raceID,
            MetaGroupId   = kv.Value.metaGroupID,
            Published     = kv.Value.published,
        });
        await SaveBatchesAsync(db, db.SdeTypes, rows, "Types", raw.Count, p, 0.37, 0.50, ct);
    }

    private async Task ImportDogmaAttributeCategoriesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        // Try FSD dict format first (new flat SDE), then BSD list format (classic SDE).
        var bsdRoot = fsdRoot.Length == 0 ? "" : fsdRoot.Replace("fsd/", "bsd/");
        var entry = zip.GetEntry($"{fsdRoot}dogmaAttributeCategories.yaml")
                 ?? zip.GetEntry($"{bsdRoot}dgmAttributeCategories.yaml");
        if (entry is null) { Report(p, "Attr Categories", "NOT FOUND — skipped", 0.495); return; }

        Report(p, "Attr Categories", "Parsing…", 0.495);
        List<SdeDogmaAttributeCategory> rows;
        try
        {
            using var reader = OpenEntry(entry);
            var raw = _yaml.Deserialize<Dictionary<int, DogmaAttrCategoryYaml>>(reader) ?? [];
            rows = raw.Select(kv => new SdeDogmaAttributeCategory
            {
                CategoryId = kv.Key,
                Name       = kv.Value.nameID?.en ?? kv.Value.name ?? ""
            }).ToList();
        }
        catch
        {
            // Fallback: BSD list format  [{categoryID: 1, name: 'Fitting'}, ...]
            entry = zip.GetEntry($"{bsdRoot}dgmAttributeCategories.yaml");
            if (entry is null) return;
            try
            {
                using var reader = OpenEntry(entry);
                var raw = _yaml.Deserialize<List<DogmaAttrCategoryYaml>>(reader) ?? [];
                rows = raw.Where(x => x.categoryID.HasValue).Select(x => new SdeDogmaAttributeCategory
                {
                    CategoryId = x.categoryID!.Value,
                    Name       = x.nameID?.en ?? x.name ?? ""
                }).ToList();
            }
            catch { return; }
        }

        await SaveBatchesAsync(db, db.SdeDogmaAttributeCategories, rows, "Attr Categories", rows.Count, p, 0.495, 0.50, ct);
    }

    private async Task ImportDogmaAttributesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}dogmaAttributes.yaml");
        if (entry is null) { Report(p, "Dogma Attributes", "NOT FOUND in ZIP — skipped", 0.50); return; }
        Report(p, "Dogma Attributes", "Parsing…", 0.50);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, DogmaAttributeYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeDogmaAttribute
        {
            AttributeId  = kv.Key,
            Name         = kv.Value.name ?? "",
            // New SDE uses displayName as localized string; old used displayName as scalar (→null here, falls back to name)
            DisplayName  = kv.Value.displayName?.en ?? kv.Value.displayNameID?.en ?? kv.Value.name ?? "",
            // New SDE uses attributeCategoryID; old used categoryID
            CategoryId   = kv.Value.attributeCategoryID ?? kv.Value.categoryID,
            DefaultValue = kv.Value.defaultValue,
            HighIsGood   = kv.Value.highIsGood,
            Stackable    = kv.Value.stackable,
            UnitId       = kv.Value.unitID,
            Published    = kv.Value.published,
        });
        await SaveBatchesAsync(db, db.SdeDogmaAttributes, rows, "Dogma Attributes", raw.Count, p, 0.50, 0.52, ct);
    }

    private async Task ImportDogmaEffectsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}dogmaEffects.yaml");
        if (entry is null) { Report(p, "Dogma Effects", "NOT FOUND in ZIP — skipped", 0.52); return; }
        Report(p, "Dogma Effects", "Parsing…", 0.52);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, DogmaEffectYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeDogmaEffect
        {
            EffectId    = kv.Key,
            // New SDE uses "name"; old SDE used "effectName"
            Name        = kv.Value.name ?? kv.Value.effectName ?? "",
            DisplayName = kv.Value.displayNameID?.en ?? kv.Value.name ?? kv.Value.effectName ?? "",
            Description = kv.Value.descriptionID?.en ?? "",
            IsOffensive = kv.Value.isOffensive,
            IsAssistance = kv.Value.isAssistance,
            Published   = kv.Value.published,
        });
        await SaveBatchesAsync(db, db.SdeDogmaEffects, rows, "Dogma Effects", raw.Count, p, 0.52, 0.54, ct);
    }

    private async Task ImportTypeDogmaAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}typeDogma.yaml");
        if (entry is null) { Report(p, "Type Dogma", "NOT FOUND in ZIP — skipped", 0.54); return; }
        Report(p, "Type Dogma", "Parsing typeDogma.yaml (large)…", 0.54);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, TypeDogmaYaml>>(reader) ?? [];

        var attrs = raw.SelectMany(kv =>
            (kv.Value.dogmaAttributes ?? []).Select(a => new SdeTypeDogmaAttribute
                { TypeId = kv.Key, AttributeId = a.attributeID, Value = a.value }))
            .DistinctBy(x => (x.TypeId, x.AttributeId));
        var effs = raw.SelectMany(kv =>
            (kv.Value.dogmaEffects ?? []).Select(e => new SdeTypeDogmaEffect
                { TypeId = kv.Key, EffectId = e.effectID, IsDefault = e.isDefault }))
            .DistinctBy(x => (x.TypeId, x.EffectId));

        await SaveBatchesAsync(db, db.SdeTypeDogmaAttributes, attrs, "Type Dogma Attributes", -1, p, 0.54, 0.63, ct);
        await SaveBatchesAsync(db, db.SdeTypeDogmaEffects,    effs,  "Type Dogma Effects",    -1, p, 0.63, 0.67, ct);
    }

    private async Task ImportBlueprintsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}blueprints.yaml");
        if (entry is null) { Report(p, "Blueprints", "NOT FOUND in ZIP — skipped", 0.67); return; }
        Report(p, "Blueprints", "Parsing blueprints.yaml…", 0.67);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, BlueprintYaml>>(reader) ?? [];

        var bps = raw.Select(kv =>
            new SdeBlueprint { TypeId = kv.Key, MaxProductionLimit = kv.Value.maxProductionLimit });
        var mats = raw.SelectMany(kv =>
            (kv.Value.activities ?? []).SelectMany(act =>
                (act.Value.materials ?? []).Select(m =>
                    new SdeBlueprintMaterial { TypeId = kv.Key, Activity = act.Key, MaterialTypeId = m.typeID, Quantity = m.quantity })))
            .DistinctBy(x => (x.TypeId, x.Activity, x.MaterialTypeId));
        var prods = raw.SelectMany(kv =>
            (kv.Value.activities ?? []).SelectMany(act =>
                (act.Value.products ?? []).Select(pr =>
                    new SdeBlueprintProduct { TypeId = kv.Key, Activity = act.Key, ProductTypeId = pr.typeID, Quantity = pr.quantity, Probability = pr.probability })))
            .DistinctBy(x => (x.TypeId, x.Activity, x.ProductTypeId));
        var skills = raw.SelectMany(kv =>
            (kv.Value.activities ?? []).SelectMany(act =>
                (act.Value.skills ?? []).Select(sk =>
                    new SdeBlueprintSkill { TypeId = kv.Key, Activity = act.Key, SkillTypeId = sk.typeID, Level = sk.level })))
            .DistinctBy(x => (x.TypeId, x.Activity, x.SkillTypeId));

        await SaveBatchesAsync(db, db.SdeBlueprints,         bps,    "Blueprints",         raw.Count, p, 0.67, 0.69, ct);
        await SaveBatchesAsync(db, db.SdeBlueprintMaterials, mats,   "Blueprint Materials", -1,        p, 0.69, 0.72, ct);
        await SaveBatchesAsync(db, db.SdeBlueprintProducts,  prods,  "Blueprint Products",  -1,        p, 0.72, 0.74, ct);
        await SaveBatchesAsync(db, db.SdeBlueprintSkills,    skills, "Blueprint Skills",    -1,        p, 0.74, 0.76, ct);
    }

    private static string RomanNumeral(int n)
    {
        if (n <= 0) return n.ToString();
        var map = new (int v, string s)[] { (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var sb = new System.Text.StringBuilder();
        foreach (var (v, s) in map) while (n >= v) { sb.Append(s); n -= v; }
        return sb.ToString();
    }

    // Nested universe (old SDE): flattens a system's inline planets+moons into celestial rows.
    private static void AddPlanetCelestials(List<SdeCelestial> list, int systemId, string systemName,
        Dictionary<int, PlanetYaml>? planets)
    {
        if (planets is null) return;
        foreach (var (pid, planet) in planets.OrderBy(kv => kv.Value.celestialIndex))
        {
            string pName = $"{systemName} {RomanNumeral(planet.celestialIndex)}";
            if (planet.position is { } pp)
                list.Add(new SdeCelestial { ItemId = pid, SolarSystemId = systemId, TypeId = planet.typeID,
                    Kind = 0, X = pp.x, Y = pp.y, Z = pp.z, Name = pName });
            if (planet.asteroidBelts is not null)
            {
                var bi = 0;
                foreach (var (bid, belt) in planet.asteroidBelts.OrderBy(kv => kv.Key))
                {
                    bi++;
                    if (belt.position is { } bp)
                        list.Add(new SdeCelestial
                        {
                            ItemId = bid, SolarSystemId = systemId, TypeId = belt.typeID,
                            Kind = 3, X = bp.x, Y = bp.y, Z = bp.z,
                            Name = $"{pName} - Asteroid Belt {bi}",
                        });
                }
            }

            if (planet.moons is null) continue;
            int mi = 0;
            foreach (var (mid, moon) in planet.moons.OrderBy(kv => kv.Key))
            {
                mi++;
                if (moon.position is { } mp)
                    list.Add(new SdeCelestial { ItemId = mid, SolarSystemId = systemId, TypeId = moon.typeID,
                        Kind = 1, X = mp.x, Y = mp.y, Z = mp.z, Name = $"{pName} - Moon {mi}" });
            }
        }
    }

    private async Task ImportUniverseAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        // New SDE: flat files mapRegions.yaml, mapConstellations.yaml, mapSolarSystems.yaml, mapStargates.yaml
        // Old SDE: nested universe/ directory walk
        var regEntry = zip.GetEntry($"{fsdRoot}mapRegions.yaml");
        if (regEntry != null)
        {
            await ImportUniverseFlatAsync(zip, fsdRoot, db, p, ct);
            return;
        }

        // Old nested-directory format
        await ImportUniverseNestedAsync(zip, fsdRoot, db, p, ct);
    }

    private async Task ImportUniverseFlatAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        Report(p, "Universe", "Parsing mapRegions.yaml…", 0.76);
        var regEntry = zip.GetEntry($"{fsdRoot}mapRegions.yaml")!;
        using (var r = OpenEntry(regEntry))
        {
            var raw = _yaml.Deserialize<Dictionary<int, MapRegionYaml>>(r) ?? [];
            // Wormhole regions have IDs in the 11000000 range
            var rows = raw.Select(kv => new SdeRegion
            {
                RegionId  = kv.Key,
                Name      = kv.Value.name?.en ?? "",
                FactionId = kv.Value.factionID,
                IsWormhole = kv.Key >= 11000000 && kv.Key < 12000000,
                X = kv.Value.position?.x ?? 0,
                Y = kv.Value.position?.y ?? 0,
                Z = kv.Value.position?.z ?? 0,
            });
            await SaveBatchesAsync(db, db.SdeRegions, rows, "Regions", raw.Count, p, 0.76, 0.78, ct);
        }

        Report(p, "Universe", "Parsing mapConstellations.yaml…", 0.78);
        var constEntry = zip.GetEntry($"{fsdRoot}mapConstellations.yaml");
        if (constEntry != null)
        {
            using var r = OpenEntry(constEntry);
            var raw = _yaml.Deserialize<Dictionary<int, MapConstellationYaml>>(r) ?? [];
            var rows = raw.Select(kv => new SdeConstellation
            {
                ConstellationId = kv.Key,
                RegionId        = kv.Value.regionID,
                Name            = kv.Value.name?.en ?? "",
                IsWormhole      = kv.Value.regionID >= 11000000 && kv.Value.regionID < 12000000,
                X = kv.Value.position?.x ?? 0,
                Y = kv.Value.position?.y ?? 0,
                Z = kv.Value.position?.z ?? 0,
            });
            await SaveBatchesAsync(db, db.SdeConstellations, rows, "Constellations", raw.Count, p, 0.78, 0.80, ct);
        }

        Report(p, "Universe", "Parsing mapSolarSystems.yaml…", 0.80);
        var sysNames = new Dictionary<int, string>();
        // Collected while the systems are parsed, but merged into the celestial list further
        // down, which is where that list comes into existence.
        var stars    = new List<SdeCelestial>();
        var sysEntry = zip.GetEntry($"{fsdRoot}mapSolarSystems.yaml");
        if (sysEntry != null)
        {
            using var r = OpenEntry(sysEntry);
            var raw = _yaml.Deserialize<Dictionary<int, MapSolarSystemYaml>>(r) ?? [];
            var rows = raw.Select(kv => new SdeSolarSystem
            {
                SolarSystemId   = kv.Key,
                ConstellationId = kv.Value.constellationID,
                RegionId        = kv.Value.regionID,
                Name            = kv.Value.name?.en ?? "",
                Security        = kv.Value.securityStatus,
                FactionId       = kv.Value.factionID,
                IsWormhole      = kv.Value.regionID >= 11000000 && kv.Value.regionID < 12000000,
                X = kv.Value.position?.x ?? 0,
                Y = kv.Value.position?.y ?? 0,
                Z = kv.Value.position?.z ?? 0,
                // Left null where CCP publishes none — see SdeSolarSystem.X2D.
                X2D           = kv.Value.position2D?.x,
                Y2D           = kv.Value.position2D?.y,
                SecurityClass = kv.Value.securityClass ?? "",
                Radius        = kv.Value.radius,
            });
            await SaveBatchesAsync(db, db.SdeSolarSystems, rows, "Solar Systems", raw.Count, p, 0.80, 0.82, ct);
            foreach (var (sysId, sys) in raw) sysNames[sysId] = sys.name?.en ?? "";

            // Stars are their own top-level file, mapStars.yaml — not a field on the system,
            // which is what an earlier attempt assumed and why no star was ever imported.
            var starEntry = zip.GetEntry($"{fsdRoot}mapStars.yaml");
            if (starEntry != null)
            {
                using var sr = OpenEntry(starEntry);
                var starRaw = _yaml.Deserialize<Dictionary<long, MapStarYaml>>(sr) ?? [];
                foreach (var (starId, st) in starRaw)
                    if (st.typeID > 0)
                        stars.Add(new SdeCelestial
                        {
                            ItemId = starId, SolarSystemId = st.solarSystemID, TypeId = st.typeID,
                            // The star sits at the origin of its system's coordinates, which is
                            // what every other celestial's orbital radius is measured from.
                            Kind = 4, X = 0, Y = 0, Z = 0,
                            Name = sysNames.GetValueOrDefault(st.solarSystemID, ""),
                        });
            }
        }

        Report(p, "Universe", "Parsing mapStargates.yaml…", 0.82);
        var celestials = new List<SdeCelestial>();
        celestials.AddRange(stars);
        var sgEntry = zip.GetEntry($"{fsdRoot}mapStargates.yaml");
        if (sgEntry != null)
        {
            using var r = OpenEntry(sgEntry);
            var raw = _yaml.Deserialize<Dictionary<int, MapStargateYaml>>(r) ?? [];
            var rows = raw.Where(kv => kv.Value.destination != null)
                .Select(kv => new SdeStargate
                {
                    StargateId            = kv.Key,
                    SolarSystemId         = kv.Value.solarSystemID,
                    DestinationStargateId = kv.Value.destination!.stargateID,
                });
            await SaveBatchesAsync(db, db.SdeStargates, rows, "Stargates", raw.Count, p, 0.82, 0.83, ct);
            foreach (var (gid, g) in raw)
                if (g.position is { } gp)
                {
                    string dest = g.destination != null
                                  && sysNames.TryGetValue(g.destination.solarSystemID, out var dn) && dn.Length > 0
                        ? $"Stargate to {dn}" : "Stargate";
                    celestials.Add(new SdeCelestial { ItemId = gid, SolarSystemId = g.solarSystemID,
                        TypeId = g.typeID, Kind = 2, X = gp.x, Y = gp.y, Z = gp.z, Name = dest });
                }
        }

        // Planets and moons are separate top-level files in the flat SDE. Moons carry celestialIndex
        // (of their planet) + orbitIndex (moon number), so both can be named from the system name.
        Report(p, "Universe", "Parsing mapPlanets.yaml…", 0.83);
        var planetEntry = zip.GetEntry($"{fsdRoot}mapPlanets.yaml");
        if (planetEntry != null)
        {
            using var r = OpenEntry(planetEntry);
            var raw = _yaml.Deserialize<Dictionary<int, MapPlanetYaml>>(r) ?? [];
            foreach (var (pid, pl) in raw)
                if (pl.position is { } pp)
                    celestials.Add(new SdeCelestial { ItemId = pid, SolarSystemId = pl.solarSystemID,
                        TypeId = pl.typeID, Kind = 0, X = pp.x, Y = pp.y, Z = pp.z,
                        Name = $"{sysNames.GetValueOrDefault(pl.solarSystemID, "")} {RomanNumeral(pl.celestialIndex)}".Trim() });
        }

        Report(p, "Universe", "Parsing mapMoons.yaml…", 0.84);
        var moonEntry = zip.GetEntry($"{fsdRoot}mapMoons.yaml");
        if (moonEntry != null)
        {
            using var r = OpenEntry(moonEntry);
            var raw = _yaml.Deserialize<Dictionary<int, MapMoonYaml>>(r) ?? [];
            foreach (var (mid, mo) in raw)
                if (mo.position is { } mp)
                    celestials.Add(new SdeCelestial { ItemId = mid, SolarSystemId = mo.solarSystemID,
                        TypeId = mo.typeID, Kind = 1, X = mp.x, Y = mp.y, Z = mp.z,
                        Name = $"{sysNames.GetValueOrDefault(mo.solarSystemID, "")} {RomanNumeral(mo.celestialIndex)} - Moon {mo.orbitIndex}".Trim() });
        }

        // Asteroid belts. CCP has shipped these under more than one name across SDE revisions,
        // so the candidates are tried in turn rather than assuming one — a missing file simply
        // means no belts, which is also the correct outcome for an SDE that omits them.
        Report(p, "Universe", "Parsing asteroid belts…", 0.845);
        foreach (var candidate in AsteroidBeltFiles)
        {
            var beltEntry = zip.GetEntry($"{fsdRoot}{candidate}");
            if (beltEntry is null) continue;

            using var r = OpenEntry(beltEntry);
            var raw = _yaml.Deserialize<Dictionary<int, MapAsteroidBeltYaml>>(r) ?? [];
            foreach (var (bid, b) in raw)
                if (b.position is { } bp)
                    celestials.Add(new SdeCelestial
                    {
                        ItemId = bid, SolarSystemId = b.solarSystemID, TypeId = b.typeID,
                        Kind = 3, X = bp.x, Y = bp.y, Z = bp.z,
                        Name = $"{sysNames.GetValueOrDefault(b.solarSystemID, "")} " +
                               $"{RomanNumeral(b.celestialIndex)} - Asteroid Belt {b.orbitIndex}".Trim(),
                    });
            break;
        }

        await SaveBatchesAsync(db, db.SdeCelestials, celestials, "Celestials", celestials.Count, p, 0.85, 0.87, ct);

        // Equinox planetary production. The reagent is unnamed here — it is decided by the
        // planet's type, Lava yielding Magmatic Gas and Ice yielding Sublimated Ice.
        Report(p, "Universe", "Parsing planetResources.yaml…", 0.868);
        var resEntry = zip.GetEntry($"{fsdRoot}planetResources.yaml");
        if (resEntry != null)
        {
            using var r = OpenEntry(resEntry);
            var raw = _yaml.Deserialize<Dictionary<long, PlanetResourceYaml>>(r) ?? [];
            var rows = raw.Select(kv => new SdePlanetResource
            {
                PlanetId         = kv.Key,
                Power            = kv.Value.power,
                Workforce        = kv.Value.workforce,
                ReagentPerCycle  = kv.Value.reagent?.amount_per_cycle  ?? 0,
                ReagentCycleTime = kv.Value.reagent?.cycle_period      ?? 0,
                SecuredCapacity  = kv.Value.reagent?.secured_capacity  ?? 0,
            });
            await SaveBatchesAsync(db, db.SdePlanetResources, rows, "Planet Resources",
                raw.Count, p, 0.868, 0.87, ct);
        }
    }

    /// <summary>
    /// Agents, their types, and the corporation divisions they work in.
    ///
    /// There is no agents file: an agent is an entry in npcCharacters.yaml carrying a nested
    /// "agent" block, so the whole character file is read and everything without one is
    /// discarded — roughly eleven thousand agents out of far more characters.
    /// </summary>
    private async Task ImportAgentsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        Report(p, "Agents", "Parsing agentTypes.yaml…", 0.872);
        var typeEntry = zip.GetEntry($"{fsdRoot}agentTypes.yaml");
        if (typeEntry != null)
        {
            using var r = OpenEntry(typeEntry);
            var raw = _yaml.Deserialize<Dictionary<int, AgentTypeYaml>>(r) ?? [];
            await SaveBatchesAsync(db, db.SdeAgentTypes,
                raw.Select(kv => new SdeAgentType { AgentTypeId = kv.Key, Name = kv.Value.name ?? "" }),
                "Agent Types", raw.Count, p, 0.872, 0.873, ct);
        }

        Report(p, "Agents", "Parsing npcCorporationDivisions.yaml…", 0.873);
        var divEntry = zip.GetEntry($"{fsdRoot}npcCorporationDivisions.yaml");
        if (divEntry != null)
        {
            using var r = OpenEntry(divEntry);
            var raw = _yaml.Deserialize<Dictionary<int, CorpDivisionYaml>>(r) ?? [];
            await SaveBatchesAsync(db, db.SdeCorpDivisions,
                raw.Select(kv => new SdeCorpDivision
                {
                    DivisionId = kv.Key,
                    // internalName is CCP's short form ("R&D"); the localised name reads better
                    // where it exists.
                    Name = kv.Value.name?.en ?? kv.Value.internalName ?? "",
                }),
                "Corp Divisions", raw.Count, p, 0.873, 0.874, ct);
        }

        Report(p, "Agents", "Parsing npcCharacters.yaml…", 0.874);
        var charEntry = zip.GetEntry($"{fsdRoot}npcCharacters.yaml");
        if (charEntry is null) return;

        using var cr = OpenEntry(charEntry);
        var chars = _yaml.Deserialize<Dictionary<int, NpcCharacterYaml>>(cr) ?? [];

        var agents = chars
            .Where(kv => kv.Value.agent is not null)
            .Select(kv => new SdeAgent
            {
                AgentId       = kv.Key,
                Name          = kv.Value.name?.en ?? "",
                CorporationId = kv.Value.corporationID,
                LocationId    = kv.Value.locationID,
                AgentTypeId   = kv.Value.agent!.agentTypeID,
                DivisionId    = kv.Value.agent.divisionID,
                Level         = kv.Value.agent.level,
                IsLocator     = kv.Value.agent.isLocator,
            })
            .ToList();

        await SaveBatchesAsync(db, db.SdeAgents, agents, "Agents", agents.Count, p, 0.874, 0.88, ct);
    }

    private class AgentTypeYaml
    {
        public string? name { get; set; }
    }

    private class CorpDivisionYaml
    {
        public string?          internalName { get; set; }
        public LocalizedString? name         { get; set; }
    }

    private class NpcCharacterYaml
    {
        public int              corporationID { get; set; }
        public long             locationID    { get; set; }
        public LocalizedString? name          { get; set; }
        public NpcAgentYaml?    agent         { get; set; }
    }

    private class NpcAgentYaml
    {
        public int  agentTypeID { get; set; }
        public int  divisionID  { get; set; }
        public int  level       { get; set; }
        public bool isLocator   { get; set; }
    }

    private class PlanetResourceYaml
    {
        public int             power     { get; set; }
        public int             workforce { get; set; }
        public PlanetReagentYaml? reagent { get; set; }
    }

    private class PlanetReagentYaml
    {
        public int  amount_per_cycle { get; set; }
        public int  cycle_period     { get; set; }
        public long secured_capacity { get; set; }
    }

    /// <summary>Names CCP has used for the asteroid-belt file across SDE revisions.</summary>
    private static readonly string[] AsteroidBeltFiles =
        ["mapAsteroidBelts.yaml", "mapAsteroidbelts.yaml", "asteroidBelts.yaml"];

    private async Task ImportUniverseNestedAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        Report(p, "Universe", "Scanning entries…", 0.76);
        var uniMarker = $"{fsdRoot}universe/";
        int rootDepth = fsdRoot.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        int regionDepth = rootDepth + 4;
        int constDepth  = rootDepth + 5;
        int sysDepth    = rootDepth + 6;

        var regionEntries        = new List<ZipArchiveEntry>();
        var constellationEntries = new List<ZipArchiveEntry>();
        var systemEntries        = new List<ZipArchiveEntry>();

        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith(uniMarker) || !e.Name.EndsWith(".yaml")) continue;
            var parts = e.FullName.Split('/');
            switch (parts.Length)
            {
                case var n when n == regionDepth && e.Name == "region.yaml":        regionEntries.Add(e);        break;
                case var n when n == constDepth  && e.Name == "constellation.yaml": constellationEntries.Add(e); break;
                case var n when n == sysDepth    && e.Name == "solarsystem.yaml":   systemEntries.Add(e);        break;
            }
        }

        Report(p, "Universe", $"Found {regionEntries.Count} regions, {constellationEntries.Count} constellations, {systemEntries.Count} systems", 0.76);

        int typeIdx   = rootDepth + 1;
        int regionIdx = rootDepth + 2;
        int constIdx  = rootDepth + 3;
        int sysIdx    = rootDepth + 4;

        var regionIdByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var regions = new List<SdeRegion>(regionEntries.Count);
        foreach (var e in regionEntries)
        {
            var parts = e.FullName.Split('/');
            using var r = OpenEntry(e);
            var y = _yaml.Deserialize<RegionYaml>(r);
            if (y is null) continue;
            var rName = parts[regionIdx];
            regionIdByName[rName] = y.regionID;
            regions.Add(new SdeRegion { RegionId = y.regionID, Name = rName, FactionId = y.factionID, IsWormhole = parts[typeIdx] == "wormhole" });
        }
        await SaveBatchesAsync(db, db.SdeRegions, regions, "Regions", regions.Count, p, 0.76, 0.78, ct);

        var constIdByKey = new Dictionary<(string, string), int>();
        var constellations = new List<SdeConstellation>(constellationEntries.Count);
        foreach (var e in constellationEntries)
        {
            var parts = e.FullName.Split('/');
            var rName = parts[regionIdx]; var cName = parts[constIdx];
            if (!regionIdByName.TryGetValue(rName, out var regionId)) continue;
            using var r = OpenEntry(e);
            var y = _yaml.Deserialize<ConstellationYaml>(r);
            if (y is null) continue;
            constIdByKey[(rName, cName)] = y.constellationID;
            constellations.Add(new SdeConstellation { ConstellationId = y.constellationID, RegionId = regionId, Name = cName, IsWormhole = parts[typeIdx] == "wormhole" });
        }
        await SaveBatchesAsync(db, db.SdeConstellations, constellations, "Constellations", constellations.Count, p, 0.78, 0.80, ct);

        var systems    = new List<SdeSolarSystem>(systemEntries.Count);
        var stargates  = new List<SdeStargate>();
        var celestials = new List<SdeCelestial>();
        foreach (var e in systemEntries)
        {
            var parts = e.FullName.Split('/');
            var rName = parts[regionIdx]; var cName = parts[constIdx];
            if (!regionIdByName.TryGetValue(rName, out var regionId)) continue;
            if (!constIdByKey.TryGetValue((rName, cName), out var constId)) continue;
            using var r = OpenEntry(e);
            var y = _yaml.Deserialize<SolarSystemYaml>(r);
            if (y is null) continue;
            var sysName = parts[sysIdx];
            systems.Add(new SdeSolarSystem
            {
                SolarSystemId = y.solarSystemID, ConstellationId = constId, RegionId = regionId,
                Name = sysName, Security = y.security, FactionId = y.factionID, IsWormhole = parts[typeIdx] == "wormhole",
            });
            foreach (var (sgId, sg) in (y.stargates ?? []))
            {
                stargates.Add(new SdeStargate { StargateId = sgId, SolarSystemId = y.solarSystemID, DestinationStargateId = sg.destination });
                if (sg.position is { } gp)
                    celestials.Add(new SdeCelestial { ItemId = sgId, SolarSystemId = y.solarSystemID,
                        TypeId = sg.typeID, Kind = 2, X = gp.x, Y = gp.y, Z = gp.z, Name = "Stargate" });
            }
            AddPlanetCelestials(celestials, y.solarSystemID, sysName, y.planets);
        }
        await SaveBatchesAsync(db, db.SdeSolarSystems, systems,    "Solar Systems", systems.Count,    p, 0.80, 0.83, ct);
        await SaveBatchesAsync(db, db.SdeStargates,    stargates,  "Stargates",     stargates.Count,  p, 0.83, 0.85, ct);
        await SaveBatchesAsync(db, db.SdeCelestials,   celestials, "Celestials",    celestials.Count, p, 0.85, 0.87, ct);
    }

    private async Task ImportStationsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        // New SDE: npcStations.yaml (dict, no names) — use ESI bulk names endpoint to populate
        var newEntry = zip.GetEntry($"{fsdRoot}npcStations.yaml");
        if (newEntry != null)
        {
            Report(p, "Stations", "Parsing npcStations.yaml…", 0.87);
            using var reader = OpenEntry(newEntry);
            var raw = _yaml.Deserialize<Dictionary<int, NpcStationYaml>>(reader) ?? [];
            Report(p, "Stations", $"Fetching {raw.Count:N0} station names from ESI…", 0.875);
            var names = await FetchEsiNamesAsync(raw.Keys.ToList(), "station", ct);
            var rows = raw.Select(kv => new SdeStation
            {
                StationId              = kv.Key,
                Name                   = names.GetValueOrDefault(kv.Key, ""),
                SolarSystemId          = kv.Value.solarSystemID,
                ConstellationId        = 0,   // not in npcStations.yaml; resolvable via join to SdeSolarSystems
                RegionId               = 0,
                CorporationId          = kv.Value.ownerID,
                StationTypeId          = kv.Value.typeID,
                Security               = 0,
                ReprocessingEfficiency = kv.Value.reprocessingEfficiency,
                ReprocessingTax        = kv.Value.reprocessingStationsTake,
            });
            await SaveBatchesAsync(db, db.SdeStations, rows, "Stations", raw.Count, p, 0.875, 0.89, ct);
            return;
        }

        // Old SDE: bsd/staStations.yaml (list, has names)
        var bsdRoot  = fsdRoot.Length == 0 ? "" : fsdRoot.Replace("fsd/", "bsd/");
        var oldEntry = zip.GetEntry($"{bsdRoot}staStations.yaml");
        if (oldEntry is null) { Report(p, "Stations", "NOT FOUND in ZIP — skipped", 0.87); return; }
        Report(p, "Stations", "Parsing staStations.yaml…", 0.87);
        using var oldReader = OpenEntry(oldEntry);
        var oldRaw = _yaml.Deserialize<List<StationYaml>>(oldReader) ?? [];
        var oldRows = oldRaw.Select(s => new SdeStation
        {
            StationId = s.stationID, Name = s.stationName ?? "",
            SolarSystemId = s.solarSystemID, ConstellationId = s.constellationID, RegionId = s.regionID,
            CorporationId = s.corporationID, StationTypeId = s.stationTypeID,
            Security = s.security, ReprocessingEfficiency = s.reprocessingEfficiency, ReprocessingTax = s.reprocessingStationsTake,
        });
        await SaveBatchesAsync(db, db.SdeStations, oldRows, "Stations", oldRaw.Count, p, 0.87, 0.89, ct);
    }

    // Calls POST /universe/names/ in batches of 1000 to resolve entity names from IDs.
    private async Task<Dictionary<int, string>> FetchEsiNamesAsync(List<int> ids, string category, CancellationToken ct)
    {
        var names = new Dictionary<int, string>(ids.Count);
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            for (int i = 0; i < ids.Count; i += 1000)
            {
                var batch   = ids.GetRange(i, Math.Min(1000, ids.Count - i));
                var json    = JsonSerializer.Serialize(batch);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var resp    = await http.PostAsync("https://esi.evetech.net/latest/universe/names/", content, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var body  = await resp.Content.ReadAsStringAsync(ct);
                var items = JsonSerializer.Deserialize<List<EsiNameItem>>(body);
                if (items is null) continue;
                foreach (var item in items)
                    if (item.Category == category || string.IsNullOrEmpty(category))
                        names[item.Id] = item.Name;
            }
        }
        catch { /* best-effort; stations will have empty names if ESI is unreachable */ }
        return names;
    }

    private record EsiNameItem(
        [property: JsonPropertyName("id")]       int    Id,
        [property: JsonPropertyName("name")]     string Name,
        [property: JsonPropertyName("category")] string Category);

    private async Task ImportFactionsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}factions.yaml");
        if (entry is null) { Report(p, "Factions", "NOT FOUND in ZIP — skipped", 0.89); return; }
        Report(p, "Factions", "Parsing…", 0.89);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, FactionYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeFaction
        {
            FactionId = kv.Key,
            Name = kv.Value.nameID?.en ?? kv.Value.name?.en ?? "",
            Description = kv.Value.descriptionID?.en ?? kv.Value.description?.en ?? "",
            CorporationId = kv.Value.corporationID, MilitiaCorporationId = kv.Value.militiaCorporationID, SolarSystemId = kv.Value.solarSystemID,
        });
        await SaveBatchesAsync(db, db.SdeFactions, rows, "Factions", raw.Count, p, 0.89, 0.91, ct);
    }

    private async Task ImportNpcCorporationsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}npcCorporations.yaml");
        if (entry is null) { Report(p, "NPC Corporations", "NOT FOUND in ZIP — skipped", 0.91); return; }
        Report(p, "NPC Corporations", "Parsing…", 0.91);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, NpcCorpYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeNpcCorporation { CorporationId = kv.Key, Name = kv.Value.nameID?.en ?? kv.Value.name?.en ?? "", FactionId = kv.Value.factionID });
        await SaveBatchesAsync(db, db.SdeNpcCorporations, rows, "NPC Corporations", raw.Count, p, 0.91, 0.93, ct);
    }

    private async Task ImportRacesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}races.yaml");
        if (entry is null) { Report(p, "Races", "NOT FOUND in ZIP — skipped", 0.93); return; }
        Report(p, "Races", "Parsing…", 0.93);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, RaceYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeRace { RaceId = kv.Key, Name = kv.Value.name?.en ?? "", Description = kv.Value.description?.en ?? "" });
        await SaveBatchesAsync(db, db.SdeRaces, rows, "Races", raw.Count, p, 0.93, 0.94, ct);
    }

    private async Task ImportMetaGroupsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}metaGroups.yaml");
        if (entry is null) { Report(p, "Meta Groups", "NOT FOUND in ZIP — skipped", 0.94); return; }
        Report(p, "Meta Groups", "Parsing…", 0.94);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, MetaGroupYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeMetaGroup { MetaGroupId = kv.Key, Name = kv.Value.name?.en ?? "" });
        await SaveBatchesAsync(db, db.SdeMetaGroups, rows, "Meta Groups", raw.Count, p, 0.94, 0.96, ct);
    }

    private async Task ImportCertificatesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}certificates.yaml");
        if (entry is null) { Report(p, "Certificates", "NOT FOUND in ZIP — skipped", 0.96); return; }
        Report(p, "Certificates", "Parsing…", 0.96);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, CertificateYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeCertificate
        {
            CertificateId = kv.Key,
            GroupId       = kv.Value.groupID,
            Name        = kv.Value.name?.en        ?? "",
            Description = kv.Value.description?.en ?? "",
        });
        await SaveBatchesAsync(db, db.SdeCertificates, rows, "Certificates", raw.Count, p, 0.96, 0.97, ct);
    }

    private async Task ImportTypeMaterialsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}typeMaterials.yaml");
        if (entry is null) { Report(p, "Type Materials", "NOT FOUND in ZIP — skipped", 0.97); return; }
        Report(p, "Type Materials", "Parsing…", 0.97);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, TypeMaterialsYaml>>(reader) ?? [];
        var rows = raw.SelectMany(kv =>
            (kv.Value.materials ?? []).Select(m => new SdeTypeMaterial
                { TypeId = kv.Key, MaterialTypeId = m.materialTypeID, Quantity = m.quantity }))
            .DistinctBy(x => (x.TypeId, x.MaterialTypeId));
        await SaveBatchesAsync(db, db.SdeTypeMaterials, rows, "Type Materials", -1, p, 0.97, 0.975, ct);
    }

    private async Task ImportPlanetSchematicsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}planetSchematics.yaml");
        if (entry is null) { Report(p, "PI Schematics", "NOT FOUND in ZIP — skipped", 0.975); return; }
        Report(p, "PI Schematics", "Parsing…", 0.975);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, PlanetSchematicYaml>>(reader) ?? [];
        var schematics = raw.Select(kv => new SdePlanetSchematic
        {
            SchematicId = kv.Key,
            // New SDE uses "name" (localized); old SDE used "nameID" (localized)
            Name      = kv.Value.name?.en ?? kv.Value.nameID?.en ?? "",
            CycleTime = kv.Value.cycleTime,
        });
        var types = raw.SelectMany(kv =>
            (kv.Value.types ?? []).Select(t => new SdePlanetSchematicType
                { SchematicId = kv.Key, TypeId = t.Key, IsInput = t.Value.isInput, Quantity = t.Value.quantity }))
            .DistinctBy(x => (x.SchematicId, x.TypeId));
        await SaveBatchesAsync(db, db.SdePlanetSchematics,     schematics, "PI Schematics",      raw.Count, p, 0.975, 0.985, ct);
        await SaveBatchesAsync(db, db.SdePlanetSchematicTypes, types,      "PI Schematic Types",  -1,        p, 0.985, 0.987, ct);
    }

    private async Task ImportDogmaUnitsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}dogmaUnits.yaml");
        if (entry is null) { Report(p, "Dogma Units", "NOT FOUND in ZIP — skipped", 0.987); return; }
        Report(p, "Dogma Units", "Parsing…", 0.987);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, DogmaUnitYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeDogmaUnit
        {
            UnitId      = kv.Key,
            Name        = kv.Value.name ?? "",
            DisplayName = kv.Value.displayName?.en ?? "",
        });
        await SaveBatchesAsync(db, db.SdeDogmaUnits, rows, "Dogma Units", raw.Count, p, 0.987, 0.989, ct);
    }

    private async Task ImportIconsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}icons.yaml");
        if (entry is null) { Report(p, "Icons", "NOT FOUND in ZIP — skipped", 0.989); return; }
        Report(p, "Icons", "Parsing icons.yaml (large)…", 0.989);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, IconYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeIcon
        {
            IconId   = kv.Key,
            IconFile = kv.Value.iconFile ?? "",
        });
        await SaveBatchesAsync(db, db.SdeIcons, rows, "Icons", raw.Count, p, 0.989, 0.992, ct);
    }

    private async Task ImportGraphicsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}graphics.yaml");
        if (entry is null) { Report(p, "Graphics", "NOT FOUND in ZIP — skipped", 0.992); return; }
        Report(p, "Graphics", "Parsing graphics.yaml (large)…", 0.992);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, GraphicYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeGraphic
        {
            GraphicId   = kv.Key,
            GraphicFile = kv.Value.graphicFile,
        });
        await SaveBatchesAsync(db, db.SdeGraphics, rows, "Graphics", raw.Count, p, 0.992, 0.995, ct);
    }

    private async Task ImportSkinsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}skins.yaml");
        if (entry is null) { Report(p, "Skins", "NOT FOUND in ZIP — skipped", 0.995); return; }
        Report(p, "Skins", "Parsing…", 0.995);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, SkinYaml>>(reader) ?? [];
        var skinRows = raw.Select(kv => new SdeSkin
        {
            SkinId             = kv.Key,
            InternalName       = kv.Value.internalName ?? "",
            SkinMaterialId     = kv.Value.skinMaterialID,
            VisibleTranquility = kv.Value.visibleTranquility,
        });
        var typeRows = raw.SelectMany(kv =>
            (kv.Value.types ?? []).Select(typeId => new SdeSkinType { SkinId = kv.Key, TypeId = typeId }))
            .DistinctBy(x => (x.SkinId, x.TypeId));
        await SaveBatchesAsync(db, db.SdeSkins,     skinRows, "Skins",      raw.Count, p, 0.995, 0.997, ct);
        await SaveBatchesAsync(db, db.SdeSkinTypes, typeRows, "Skin Types", -1,        p, 0.997, 0.999, ct);
    }

    private async Task ImportSkinLicensesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}skinLicenses.yaml");
        if (entry is null) { Report(p, "Skin Licenses", "NOT FOUND in ZIP — skipped", 0.999); return; }
        Report(p, "Skin Licenses", "Parsing…", 0.999);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, SkinLicenseYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeSkinLicense
        {
            LicenseTypeId = kv.Key,
            SkinId        = kv.Value.skinID,
            Duration      = kv.Value.duration,
        });
        await SaveBatchesAsync(db, db.SdeSkinLicenses, rows, "Skin Licenses", raw.Count, p, 0.999, 1.0, ct);
    }

    // -----------------------------------------------------------------------
    // Batch save helper
    // -----------------------------------------------------------------------

    private static async Task SaveBatchesAsync<T>(
        AppDbContext db,
        DbSet<T> set,
        IEnumerable<T> source,
        string stage,
        int estimatedTotal,
        IProgress<SdeImportProgress> p,
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
                p.Report(new SdeImportProgress(stage, detail, frac));
            }
        }

        if (buffer.Count > 0)
        {
            await set.AddRangeAsync(buffer, ct);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            saved += buffer.Count;
        }

        p.Report(new SdeImportProgress(stage, $"{saved:N0} rows saved", fracEnd));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Buffer the entire zip entry into a MemoryStream before handing it to
    // YamlDotNet. ZipArchiveEntry.Open() returns a non-seekable DeflateStream
    // that can legally return 0 bytes on a Read() call before reaching true EOF.
    private static StreamReader OpenEntry(ZipArchiveEntry e)
    {
        var ms = new MemoryStream();
        using (var src = e.Open())
            src.CopyTo(ms);
        ms.Position = 0;
        return new StreamReader(ms, System.Text.Encoding.UTF8, leaveOpen: false);
    }

    private static void Report(IProgress<SdeImportProgress> p, string stage, string detail, double frac)
        => p.Report(new SdeImportProgress(stage, detail, frac));

    // -----------------------------------------------------------------------
    // YAML DTOs — property names match SDE YAML keys exactly (case-sensitive)
    // -----------------------------------------------------------------------

    private class LocalizedString { public string? en { get; set; } }

    private class CategoryYaml { public LocalizedString? name { get; set; } public bool published { get; set; } }
    private class GroupYaml
    {
        public int              categoryID  { get; set; }
        public LocalizedString? name        { get; set; }
        public bool             published   { get; set; }
        public bool             anchorable  { get; set; }
        public bool             anchored    { get; set; }
    }

    private class MarketGroupYaml
    {
        public LocalizedString? name          { get; set; }
        public LocalizedString? nameID        { get; set; }
        public LocalizedString? description   { get; set; }
        public LocalizedString? descriptionID { get; set; }
        public int?             parentGroupID { get; set; }
        public bool             hasTypes      { get; set; }
        public int?             iconID        { get; set; }
    }

    private class TypeYaml
    {
        public int              groupID       { get; set; }
        public LocalizedString? name          { get; set; }
        public LocalizedString? nameID        { get; set; }
        public LocalizedString? description   { get; set; }
        public LocalizedString? descriptionID { get; set; }
        public double           volume        { get; set; }
        public double           mass          { get; set; }
        public double           capacity      { get; set; }
        public int              portionSize   { get; set; }
        public double?          basePrice     { get; set; }
        public int?             marketGroupID { get; set; }
        public int?             iconID        { get; set; }
        public int?             graphicID     { get; set; }
        public int?             factionID     { get; set; }
        public int?             raceID        { get; set; }
        public int?             metaGroupID   { get; set; }
        public bool             published     { get; set; }
    }

    private class DogmaAttributeYaml
    {
        public string?          name                { get; set; }
        // New SDE: displayName is a localized string {en: ...}
        // Old SDE: displayName was a plain scalar string
        // YamlDotNet will give null when scalar→LocalizedString; fallback to name covers that.
        public LocalizedString? displayName         { get; set; }
        public LocalizedString? displayNameID       { get; set; }
        // New SDE uses attributeCategoryID; old SDE used categoryID
        public int?             attributeCategoryID { get; set; }
        public int?             categoryID          { get; set; }
        public double           defaultValue        { get; set; }
        public bool             highIsGood          { get; set; }
        public bool             stackable           { get; set; }
        public int?             unitID              { get; set; }
        public bool             published           { get; set; }
    }

    private class DogmaAttrCategoryYaml
    {
        public int?             categoryID { get; set; }
        public string?          name       { get; set; }
        public LocalizedString? nameID     { get; set; }
    }

    private class DogmaEffectYaml
    {
        // New SDE: "name" field holds the effect name
        public string?          name          { get; set; }
        // Old SDE: "effectName" field
        public string?          effectName    { get; set; }
        public LocalizedString? displayNameID { get; set; }
        public LocalizedString? descriptionID { get; set; }
        public bool             isOffensive   { get; set; }
        public bool             isAssistance  { get; set; }
        public bool             published     { get; set; }
    }

    private class TypeDogmaYaml
    {
        public List<TdAttrYaml>?   dogmaAttributes { get; set; }
        public List<TdEffectYaml>? dogmaEffects    { get; set; }
    }
    private class TdAttrYaml   { public int attributeID { get; set; } public double value { get; set; } }
    private class TdEffectYaml { public int effectID { get; set; } public bool isDefault { get; set; } }

    private class BlueprintYaml
    {
        public int                                        maxProductionLimit { get; set; }
        public Dictionary<string, BlueprintActivityYaml>? activities        { get; set; }
    }
    private class BlueprintActivityYaml
    {
        public List<BpMaterialYaml>? materials { get; set; }
        public List<BpProductYaml>?  products  { get; set; }
        public List<BpSkillYaml>?    skills    { get; set; }
    }
    private class BpMaterialYaml { public int typeID { get; set; } public int quantity { get; set; } }
    private class BpProductYaml  { public int typeID { get; set; } public int quantity { get; set; } public double probability { get; set; } }
    private class BpSkillYaml    { public int typeID { get; set; } public int level { get; set; } }

    // Old nested-universe DTOs
    private class RegionYaml        { public int regionID        { get; set; } public int? factionID { get; set; } }
    private class ConstellationYaml { public int constellationID { get; set; } }
    private class SolarSystemYaml
    {
        public int    solarSystemID { get; set; }
        public double security      { get; set; }
        public int?   factionID     { get; set; }
        public Dictionary<int, StargateYaml>? stargates { get; set; }
        public Dictionary<int, PlanetYaml>?   planets   { get; set; }
    }
    // SDE positions are objects {x, y, z}, not arrays.
    private class PositionYaml { public double x { get; set; } public double y { get; set; } public double z { get; set; } }
    private class StargateYaml
    {
        public int           destination { get; set; }
        public int           typeID      { get; set; }
        public PositionYaml? position    { get; set; }
    }
    // Nested universe (old SDE): planets carry their moons and belts inline.
    private class PlanetYaml
    {
        public int           celestialIndex { get; set; }
        public int           typeID         { get; set; }
        public PositionYaml? position       { get; set; }
        public Dictionary<int, MoonYaml>? moons         { get; set; }
        public Dictionary<int, MoonYaml>? asteroidBelts { get; set; }
    }

    // Flat universe: asteroid belts alongside mapPlanets/mapMoons, same shape as a moon.
    private class MapAsteroidBeltYaml
    {
        public int           celestialIndex { get; set; }
        public int           orbitIndex     { get; set; }
        public int           solarSystemID  { get; set; }
        public int           typeID         { get; set; }
        public PositionYaml? position       { get; set; }
    }
    private class MoonYaml
    {
        public int           typeID   { get; set; }
        public PositionYaml? position { get; set; }
    }
    // Flat universe (current SDE): mapPlanets.yaml / mapMoons.yaml are separate top-level files.
    // Moons carry celestialIndex (of their planet) + orbitIndex (moon number), so a moon can be
    // named without joining back to its planet.
    private class MapPlanetYaml
    {
        public int           celestialIndex { get; set; }
        public int           solarSystemID  { get; set; }
        public int           typeID         { get; set; }
        public PositionYaml? position       { get; set; }
    }
    private class MapMoonYaml
    {
        public int           celestialIndex { get; set; }
        public int           orbitIndex     { get; set; }
        public int           solarSystemID  { get; set; }
        public int           typeID         { get; set; }
        public PositionYaml? position       { get; set; }
    }

    // New flat-universe DTOs
    private class MapRegionYaml
    {
        public LocalizedString? name      { get; set; }
        public int?             factionID { get; set; }
        public PositionYaml?    position  { get; set; }
    }
    private class MapConstellationYaml
    {
        public LocalizedString? name      { get; set; }
        public int              regionID  { get; set; }
        public int?             factionID { get; set; }
        public PositionYaml?    position  { get; set; }
    }
    /// <summary>position2D is CCP's own published map layout — the one the in-game map
    /// draws — and is present only for New Eden systems (30000000-30999999). Wormhole,
    /// abyssal and Zarzakh systems have position but no position2D.</summary>
    private class Position2DYaml { public double x { get; set; } public double y { get; set; } }

    private class MapSolarSystemYaml
    {
        public LocalizedString? name           { get; set; }
        public int              constellationID { get; set; }
        public int              regionID        { get; set; }
        public double           securityStatus  { get; set; }
        public int?             factionID       { get; set; }
        public PositionYaml?    position        { get; set; }
        public Position2DYaml?  position2D      { get; set; }
        public string?          securityClass   { get; set; }
        public double           radius          { get; set; }
        public Dictionary<int, PlanetYaml>? planets { get; set; }
    }

    /// <summary>mapStars.yaml — keyed by star id, one per system. The type name carries the
    /// spectral class the system view shows ("Sun K5 (Orange Bright)").</summary>
    private class MapStarYaml
    {
        public int solarSystemID { get; set; }
        public int typeID        { get; set; }
    }
    private class MapStargateYaml
    {
        public int              solarSystemID { get; set; }
        public int              typeID        { get; set; }
        public PositionYaml?    position      { get; set; }
        public MapStargateDestYaml? destination   { get; set; }
    }
    private class MapStargateDestYaml { public int stargateID { get; set; } public int solarSystemID { get; set; } }

    // Old BSD station list DTO
    private class StationYaml
    {
        public int     stationID                { get; set; }
        public string? stationName              { get; set; }
        public int     solarSystemID            { get; set; }
        public int     constellationID          { get; set; }
        public int     regionID                 { get; set; }
        public int?    corporationID            { get; set; }
        public int?    stationTypeID            { get; set; }
        public double  security                 { get; set; }
        public double  reprocessingEfficiency   { get; set; }
        public double  reprocessingStationsTake { get; set; }
    }

    // New npcStations.yaml DTO (dict format, no station name)
    private class NpcStationYaml
    {
        public int    solarSystemID            { get; set; }
        public int    typeID                   { get; set; }
        public int    ownerID                  { get; set; }
        public int    operationID              { get; set; }
        public double reprocessingEfficiency   { get; set; }
        public double reprocessingStationsTake { get; set; }
    }

    private class FactionYaml
    {
        public LocalizedString? name                 { get; set; }
        public LocalizedString? nameID               { get; set; }
        public LocalizedString? description          { get; set; }
        public LocalizedString? descriptionID        { get; set; }
        public int?             corporationID        { get; set; }
        public int?             militiaCorporationID { get; set; }
        public int?             solarSystemID        { get; set; }
    }

    private class NpcCorpYaml
    {
        public LocalizedString? name      { get; set; }
        public LocalizedString? nameID    { get; set; }
        public int?             factionID { get; set; }
    }
    private class RaceYaml      { public LocalizedString? name { get; set; } public LocalizedString? description { get; set; } }
    private class MetaGroupYaml { public LocalizedString? name { get; set; } }

    private class CertificateYaml
    {
        public int              groupID     { get; set; }
        // New SDE uses localized string {en: ...}; old SDE used plain scalar
        // YamlDotNet gives null on scalar→LocalizedString mismatch; acceptable since we use the new SDE URL
        public LocalizedString? name        { get; set; }
        public LocalizedString? description { get; set; }
    }

    private class TypeMaterialsYaml
    {
        public List<TypeMaterialEntryYaml>? materials { get; set; }
    }
    private class TypeMaterialEntryYaml
    {
        public int materialTypeID { get; set; }
        public int quantity       { get; set; }
    }

    private class PlanetSchematicYaml
    {
        public int                                    cycleTime { get; set; }
        // New SDE uses "name" (localized); old used "nameID" (localized)
        public LocalizedString?                       name      { get; set; }
        public LocalizedString?                       nameID    { get; set; }
        public Dictionary<int, PiSchematicTypeYaml>? types     { get; set; }
    }
    private class PiSchematicTypeYaml
    {
        public bool isInput  { get; set; }
        public int  quantity { get; set; }
    }

    // New SDE DTOs
    private class DogmaUnitYaml
    {
        public string?          name        { get; set; }
        public LocalizedString? displayName { get; set; }
    }

    private class IconYaml
    {
        public string? iconFile { get; set; }
    }

    private class GraphicYaml
    {
        public string? graphicFile { get; set; }
    }

    private class SkinYaml
    {
        public string?     internalName       { get; set; }
        public int?        skinMaterialID     { get; set; }
        public bool        visibleTranquility { get; set; }
        public List<int>?  types              { get; set; }
    }

    private class SkinLicenseYaml
    {
        public int skinID   { get; set; }
        public int duration { get; set; }
    }
}
