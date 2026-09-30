using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using EveConsole.Localization;

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
        string.Format(SettingsText.SdeIncompatible, string.Join(", ", missing));
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
    private readonly ClientSignals        _signals;
    private readonly IDeserializer        _yaml;
    private readonly AppErrorLogger       _errors;

    public SdeImportService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory, ClientSignals signals)
    {
        _scopeFactory = scopeFactory;
        _httpFactory  = httpFactory;
        _signals      = signals;
        _errors = new AppErrorLogger(scopeFactory);
        _yaml = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            // Every localised field — see LocalizedTextConverter for why not YamlDotNet's own.
            .WithTypeConverter(new LocalizedTextConverter())
            .Build();
    }

    /// <summary>
    /// How many names the last import that committed stored in each of the client's other
    /// languages, by the SDE's key (de … zh). Null before one has, and when the count itself
    /// failed. Shown with the import's result: a language at 0 is one the SDE did not carry.
    /// </summary>
    public IReadOnlyDictionary<string, int>? LastNamesByLanguage { get; private set; }

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

    /// <summary>
    /// Downloads the current SDE and replaces every table the app derives from it.
    /// </summary>
    /// <returns>
    /// Warnings about tables that imported oddly but not badly enough to reject the run. Empty
    /// after a clean import.
    /// </returns>
    /// <exception cref="SdeCompatibilityException">A required file is missing from the archive.</exception>
    /// <exception cref="ImportVerificationException">
    /// The import ran to the end but lost a table that previously held data. Nothing was committed.
    /// </exception>
    public async Task<IReadOnlyList<string>> ImportAsync(IProgress<SdeImportProgress> progress, CancellationToken ct)
    {
        Report(progress, SettingsText.ImportStagePreparing, SettingsText.SdeFetchingBuildInfo, 0.01);
        var buildInfo = await GetLatestBuildInfoAsync(ct);

        var tempPath = await DownloadAsync(progress, ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // SQLite PRAGMAs, and journal_mode cannot be set inside a transaction — so this stays
            // above the one opened below.
            await AppDb.TuneForBulkImportAsync(db.Database, ct);

            // Open archive and validate BEFORE touching any existing data.
            // Throws SdeCompatibilityException if required files are missing,
            // leaving existing SDE tables intact so the app stays functional.
            using var archive = ZipFile.OpenRead(tempPath);
            var fsdRoot = DetectRoot(archive, progress);
            ValidateArchive(archive, fsdRoot, progress);

            // Schema first, and outside the transaction: the statements are all IF NOT EXISTS, they
            // describe the shape rather than the contents, and a new column is wanted whether or
            // not the rows that follow survive.
            Report(progress, SettingsText.ImportStagePreparing, SettingsText.SdeCreatingSchema, 0.30);
            EnsureSdeSchema(db);

            // Read before the wipe destroys it. This is what the verification at the end compares
            // against, so a table that quietly stops being filled reads as "races held 11 rows and
            // now holds none" rather than merely "races is empty" — which on a first import is the
            // plain truth and no cause for alarm.
            Report(progress, SettingsText.ImportStagePreparing, SettingsText.SdeReadingRowCounts, 0.305);
            var tables = BulkImport.TablesFor(db, "Sde", "SdeBuildInfos");
            var before = await BulkImport.CountAsync(db, tables, ct);

            // ⚠️ The wipe and the refill are undoable as one unit. A failure anywhere — a changed
            // file format, a duplicate key, a cancelled run — now leaves the previous SDE exactly
            // as it was.
            //
            // Before this, a stage that threw left every table after it empty, and since the whole
            // import is a wipe followed by a refill there was no way to tell that from "CCP removed
            // it". One duplicate key cost ten tables, reprocessing among them.
            //
            // ⚠️ HOW it is undoable differs by engine, and the difference is not cosmetic: on
            // PostgreSQL this is one transaction, on SQLite a copy of the tables in an attached
            // file, because a transaction held for the length of an import blocks every other
            // writer in the app. SdeUndo has the measurements.
            await using var undo = await BulkImportUndo.CreateAsync(db, "sde", tables,
                (stage, detail, frac) => progress.Report(new SdeImportProgress(stage, detail, frac)), ct);

            Report(progress, SettingsText.ImportStagePreparing, SettingsText.SdeClearing, 0.31);
            await BulkImport.ClearAsync(db, tables, ct);

            db.ChangeTracker.AutoDetectChangesEnabled = false;

            await ImportCategoriesAsync(archive, fsdRoot, db, progress, ct);
            await ImportGroupsAsync(archive, fsdRoot, db, progress, ct);
            await ImportMarketGroupsAsync(archive, fsdRoot, db, progress, ct);
            await ImportTypesAsync(archive, fsdRoot, db, progress, ct);
            await ImportTypeDescriptionsAsync(archive, fsdRoot, db, progress, ct);
            await ImportDogmaAttributeCategoriesAsync(archive, fsdRoot, db, progress, ct);
            await ImportDogmaAttributesAsync(archive, fsdRoot, db, progress, ct);
            await ImportDogmaEffectsAsync(archive, fsdRoot, db, progress, ct);
            await ImportTypeDogmaAsync(archive, fsdRoot, db, progress, ct);
            await ImportBlueprintsAsync(archive, fsdRoot, db, progress, ct);
            var customNames = await ImportUniverseAsync(archive, fsdRoot, db, progress, ct);
            await ImportStationsAsync(archive, fsdRoot, db, progress, ct);
            await ImportAgentsAsync(archive, fsdRoot, db, progress, ct);
            await ImportFactionsAsync(archive, fsdRoot, db, progress, ct);
            await ImportNpcCorporationsAsync(archive, fsdRoot, db, progress, ct);
            await ImportIndustryModifierSourcesAsync(archive, fsdRoot, db, progress, ct);
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

            // Last of the stages, because it is built from theirs: see the method.
            var stationNames = await ImportStationNamesAsync(db, customNames, progress, ct);

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

            // How many names each language got: the one place that says whether the SDE carried all
            // eight. Counted from the table rather than tallied on the way in, so it reports what
            // was stored rather than what was meant to be. The descriptions likewise, on a line of
            // their own — and only in the log: the result on the Settings screen stays names.
            var namesStored = await CountNamesAsync(db, ct);
            var textsStored = await CountTextsAsync(db, ct);

            // Inside the transaction, so "this import lost a table" is still a decision and not
            // merely a note about something that has already happened.
            Report(progress, SettingsText.ImportStageVerifying, SettingsText.ImportCheckingRowCounts, 0.99);
            var (lost, warnings) = BulkImport.Compare(
                before, await BulkImport.CountAsync(db, tables, ct));

            if (lost.Count > 0)
            {
                // CancellationToken.None: whatever else has gone wrong, undoing this is exactly
                // what still needs to happen.
                await undo.RollbackAsync(CancellationToken.None);
                foreach (var line in lost)
                    _errors.Log("SdeImport", "Verification", line);
                throw new ImportVerificationException("SDE", lost);
            }

            await undo.CommitAsync(ct);

            foreach (var line in warnings)
                _errors.Log("SdeImport", "Verification", line);

            // A result, not a fault — logged all the same, because the log is what stays: it is how
            // anyone can see which languages the SDE really carried, and how many names came of it.
            _errors.Log("SdeImport", "Names", namesStored.Summary, namesStored.ByKind);
            _errors.Log("SdeImport", "Descriptions", textsStored.Summary, textsStored.ByKind);
            if (stationNames is not null)
                _errors.Log("SdeImport", "Station names", stationNames.Summary, stationNames.Detail);
            LastNamesByLanguage = namesStored.ByLanguage;

            // The names on screen come from what was just replaced: this client's, and on a shared
            // server every other client's too, each in its own language. See SdeNames. On SQLite
            // there is no other client to tell.
            SdeNames.Reload();
            if (DbEngine.IsPostgres)
                await _signals.PublishAsync(SdeNames.ImportedSignal, CancellationToken.None);

            Report(progress, SettingsText.ImportStageDone, SettingsText.SdeImportComplete, 1.0);
            return warnings;
        }
        finally
        {
            // The transaction is disposed on the way out of the block above, which rolls it back
            // unless it was committed. Nothing here has to undo anything.
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
        const string regionsFile = "mapRegions.yaml";
        bool hasUniverse = archive.GetEntry(fsdRoot + regionsFile) is not null
            || archive.Entries.Any(e => e.FullName.Contains("/universe/", StringComparison.Ordinal));
        if (!hasUniverse)
            missing.Add(string.Format(SettingsText.SdeUniverseDataFile, regionsFile));

        if (missing.Count > 0)
            throw new SdeCompatibilityException(missing);

        // Optional files — missing means we silently skip that importer, not a hard failure.
        var optional = new[]
        {
            "dogmaAttributeCategories.yaml", "races.yaml", "certificates.yaml",
            "typeMaterials.yaml", "planetSchematics.yaml",
            "industryModifierSources.yaml",
            "dogmaUnits.yaml", "icons.yaml", "graphics.yaml", "skins.yaml", "skinLicenses.yaml",
            "npcStations.yaml", "stationServices.yaml", "stationOperations.yaml",
        };
        var missingOptional = optional.Where(f => archive.GetEntry($"{fsdRoot}{f}") is null).ToList();
        if (missingOptional.Count > 0)
            p.Report(new SdeImportProgress(SettingsText.ImportStagePreparing,
                string.Format(SettingsText.SdeOptionalMissing, string.Join(", ", missingOptional)), 0.315));
        else
            p.Report(new SdeImportProgress(SettingsText.ImportStagePreparing, SettingsText.SdeAllFilesPresent, 0.315));
    }

    // Returns the prefix to prepend before a filename. New flat SDE returns ""; old nested returns "fsd/" or "sde/fsd/".
    private static string DetectRoot(ZipArchive archive, IProgress<SdeImportProgress> p)
    {
        // New SDE format: flat — files at root level (e.g. "categories.yaml")
        if (archive.GetEntry("categories.yaml") != null)
        {
            p.Report(new SdeImportProgress(SettingsText.ImportStagePreparing, SettingsText.SdeFlatFormat, 0.31));
            return "";
        }

        // Old format: look for fsd/categories.yaml under an optional root prefix
        const string probe = "fsd/categories.yaml";
        foreach (var e in archive.Entries)
        {
            if (!e.FullName.EndsWith(probe, StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = e.FullName[..^"categories.yaml".Length];  // includes "fsd/"
            p.Report(new SdeImportProgress(SettingsText.ImportStagePreparing, string.Format(SettingsText.SdeNestedFormat, prefix), 0.31));
            return prefix;
        }

        p.Report(new SdeImportProgress(SettingsText.ImportStageWarning, SettingsText.SdeRootNotDetected, 0.31));
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
            Report(progress, SettingsText.ImportStageDownloadingSde, detail, frac * 0.30);
        }

        return tempPath;
    }

    // -----------------------------------------------------------------------
    // Schema + clear
    // -----------------------------------------------------------------------

    /// <summary>
    /// Brings the SDE tables up to the shape the entity model expects.
    /// </summary>
    /// <remarks>
    /// ⚠️ Called from App startup as well as from the import, and startup is what matters. This
    /// used to run ONLY while an import ran, which meant a database imported before a column
    /// existed had EF querying a column the table lacked from the moment the app opened — and EF
    /// throws on the whole entity, not just the missing value. v0.9.13 added 81 columns and two
    /// tables to the model; on an existing install every one of them was absent, so Wallet, Sales
    /// Tracker, Order Tracker and the Worklist all threw on their first query, and Update SDE —
    /// the one action that would have repaired the schema — died on the missing table before it
    /// got there. Running this at startup is what breaks that circle.
    ///
    /// <para>Synchronous because the startup path is inside a Task.Run and cannot await; this is
    /// a few dozen DDL statements against a local file, so there is nothing to gain by splitting
    /// it into two versions that then have to be kept in step.</para>
    ///
    /// <para>⚠️ Every statement here is idempotent — CREATE TABLE IF NOT EXISTS, or an ALTER whose
    /// duplicate-column error is swallowed — so it is safe to run on every start, on any vintage
    /// of database, in either order relative to EnsureCreated.</para>
    /// </remarks>
    internal static void EnsureSdeSchema(AppDbContext db)
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
            """CREATE TABLE IF NOT EXISTS "SdeDogmaEffects" ("EffectId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "DisplayName" TEXT NOT NULL, "Description" TEXT NOT NULL, "IsOffensive" INTEGER NOT NULL, "IsAssistance" INTEGER NOT NULL, "Published" INTEGER NOT NULL, "EffectCategory" INTEGER NOT NULL DEFAULT 0, "IsWarpSafe" INTEGER NOT NULL DEFAULT 0, "DisallowAutoRepeat" INTEGER NOT NULL DEFAULT 0, "DurationAttributeId" INTEGER, "DischargeAttributeId" INTEGER, "RangeAttributeId" INTEGER, "FalloffAttributeId" INTEGER, "TrackingSpeedAttributeId" INTEGER, "ResistanceAttributeId" INTEGER, "FittingUsageChanceAttributeId" INTEGER)""",
            // The rules the fitting engine runs: one row per modifierInfo entry of each effect.
            """CREATE TABLE IF NOT EXISTS "SdeDogmaEffectModifiers" ("EffectId" INTEGER NOT NULL, "Ordinal" INTEGER NOT NULL, "Func" TEXT NOT NULL, "Domain" TEXT NOT NULL, "Operation" INTEGER, "ModifiedAttributeId" INTEGER, "ModifyingAttributeId" INTEGER, "GroupId" INTEGER, "SkillTypeId" INTEGER, "StoppedEffectId" INTEGER, CONSTRAINT "PK_SdeDogmaEffectModifiers" PRIMARY KEY ("EffectId", "Ordinal"))""",
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
            """CREATE TABLE IF NOT EXISTS "SdeStationServices" ("ServiceId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '')""",
            """CREATE TABLE IF NOT EXISTS "SdeStationOperations" ("OperationId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '')""",
            """CREATE TABLE IF NOT EXISTS "SdeStationOperationServices" ("OperationId" INTEGER NOT NULL, "ServiceId" INTEGER NOT NULL, PRIMARY KEY ("OperationId", "ServiceId"))""",
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

            // ── Tables added in 0.9.13 ──────────────────────────────────────────
            // The import writes SdeIndustryModifierSources at stage 0.93, and on any database
            // that predates it the import died there — after the wipe, so the rollback fired and
            // the SDE could never move forward. EsiNpcCorpProfiles is filled from ESI rather than
            // the SDE and is here only because it shares the fault: added to the model, and to
            // PostgresSchema, and to no list SQLite reads.
            """CREATE TABLE IF NOT EXISTS "SdeIndustryModifierSources" ("TypeId" INTEGER NOT NULL, "Activity" TEXT NOT NULL, "BonusKind" TEXT NOT NULL, "DogmaAttributeId" INTEGER NOT NULL, "FilterId" INTEGER NULL, CONSTRAINT "PK_SdeIndustryModifierSources" PRIMARY KEY ("TypeId", "Activity", "BonusKind", "DogmaAttributeId"))""",
            """CREATE TABLE IF NOT EXISTS "EsiNpcCorpProfiles" ("CorporationId" INTEGER NOT NULL CONSTRAINT "PK_EsiNpcCorpProfiles" PRIMARY KEY, "Ticker" TEXT NOT NULL, "Description" TEXT NOT NULL, "Url" TEXT NOT NULL, "CeoId" INTEGER NOT NULL, "HomeStationId" INTEGER NOT NULL, "MemberCount" INTEGER NOT NULL, "TaxRate" REAL NOT NULL, "FetchedUtc" TEXT NOT NULL)""",

            // ── Names in the client's other languages ───────────────────────────
            // Display only: the English stays in every Name column. See SdeName. A new table, so
            // on an existing database the fingerprint grows and the import that fills it starts on
            // its own after the upgrade.
            """CREATE TABLE IF NOT EXISTS "SdeNames" ("Kind" INTEGER NOT NULL, "Id" INTEGER NOT NULL, "Lang" TEXT NOT NULL, "Name" TEXT NOT NULL DEFAULT '', CONSTRAINT "PK_SdeNames" PRIMARY KEY ("Kind", "Id", "Lang"))""",
            // And the descriptions, which are read a row at a time rather than loaded: see SdeText.
            // A new table too, with the same consequence.
            """CREATE TABLE IF NOT EXISTS "SdeTexts" ("Kind" INTEGER NOT NULL, "Id" INTEGER NOT NULL, "Lang" TEXT NOT NULL, "Text" TEXT NOT NULL DEFAULT '', CONSTRAINT "PK_SdeTexts" PRIMARY KEY ("Kind", "Id", "Lang"))""",
        };
        foreach (var sql in creates)
            db.Database.ExecuteSqlRaw(sql);

        // ALTER TABLE ADD COLUMN for tables that existed before these columns were added.
        // SQLite ALTER TABLE does not support IF NOT EXISTS, so we catch the duplicate-column error.
        var alters = new[]
        {
            """ALTER TABLE "SdeStations" ADD COLUMN "OperationId" INTEGER""",
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

            // ── Columns added in 0.9.13 ─────────────────────────────────────────
            // Generated from the entity model rather than transcribed: a type written by
            // hand that differs from the one EnsureCreated emits gives upgraded installs a
            // different column type from fresh ones, and nothing throws to say so.
            // -- SdeCategories (1)
            """ALTER TABLE "SdeCategories" ADD COLUMN "IconId" INTEGER""",
            // -- SdeConstellations (1)
            """ALTER TABLE "SdeConstellations" ADD COLUMN "WormholeClassId" INTEGER""",
            // -- SdeDogmaAttributes (9)
            """ALTER TABLE "SdeDogmaAttributes" ADD COLUMN "Description" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeDogmaAttributes" ADD COLUMN "IconId" INTEGER""",
            """ALTER TABLE "SdeDogmaAttributes" ADD COLUMN "MinAttributeId" INTEGER""",
            """ALTER TABLE "SdeDogmaAttributes" ADD COLUMN "MaxAttributeId" INTEGER""",
            """ALTER TABLE "SdeDogmaAttributes" ADD COLUMN "TooltipTitle" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeDogmaAttributes" ADD COLUMN "TooltipDescription" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeDogmaAttributes" ADD COLUMN "DataType" INTEGER""",
            """ALTER TABLE "SdeDogmaAttributes" ADD COLUMN "DisplayWhenZero" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeDogmaAttributes" ADD COLUMN "ChargeRechargeTimeId" INTEGER""",
            // -- SdeDogmaEffects: what the fitting engine runs on (10)
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "EffectCategory" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "IsWarpSafe" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "DisallowAutoRepeat" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "DurationAttributeId" INTEGER""",
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "DischargeAttributeId" INTEGER""",
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "RangeAttributeId" INTEGER""",
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "FalloffAttributeId" INTEGER""",
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "TrackingSpeedAttributeId" INTEGER""",
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "ResistanceAttributeId" INTEGER""",
            """ALTER TABLE "SdeDogmaEffects" ADD COLUMN "FittingUsageChanceAttributeId" INTEGER""",
            // -- SdeFactions (4)
            """ALTER TABLE "SdeFactions" ADD COLUMN "IconId" INTEGER""",
            """ALTER TABLE "SdeFactions" ADD COLUMN "ShortDescription" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeFactions" ADD COLUMN "SizeFactor" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeFactions" ADD COLUMN "UniqueName" INTEGER NOT NULL DEFAULT 0""",
            // -- SdeGroups (3)
            """ALTER TABLE "SdeGroups" ADD COLUMN "IconId" INTEGER""",
            """ALTER TABLE "SdeGroups" ADD COLUMN "FittableNonSingleton" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeGroups" ADD COLUMN "UseBasePrice" INTEGER NOT NULL DEFAULT 0""",
            // -- SdeMetaGroups (4)
            """ALTER TABLE "SdeMetaGroups" ADD COLUMN "Description" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeMetaGroups" ADD COLUMN "IconId" INTEGER""",
            """ALTER TABLE "SdeMetaGroups" ADD COLUMN "IconSuffix" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeMetaGroups" ADD COLUMN "ColorHex" TEXT NOT NULL DEFAULT ''""",
            // -- SdeNpcCorporations (18)
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "StationId" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "SolarSystemId" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "Ticker" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "Description" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "CeoId" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "TaxRate" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "Size" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "Extent" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "MemberLimit" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "MinSecurity" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "MinimumJoinStanding" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "EnemyId" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "FriendId" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "RaceId" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "IconId" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "MainActivityId" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "SecondaryActivityId" INTEGER""",
            """ALTER TABLE "SdeNpcCorporations" ADD COLUMN "Deleted" INTEGER NOT NULL DEFAULT 0""",
            // -- SdeRaces (2)
            """ALTER TABLE "SdeRaces" ADD COLUMN "IconId" INTEGER""",
            """ALTER TABLE "SdeRaces" ADD COLUMN "ShipTypeId" INTEGER""",
            // -- SdeRegions (3)
            """ALTER TABLE "SdeRegions" ADD COLUMN "Description" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeRegions" ADD COLUMN "NebulaId" INTEGER""",
            """ALTER TABLE "SdeRegions" ADD COLUMN "WormholeClassId" INTEGER""",
            // -- SdeSolarSystems (10)
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "WormholeClassId" INTEGER""",
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "Border" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "Corridor" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "Fringe" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "Hub" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "International" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "Regional" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "Luminosity" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "VisualEffect" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeSolarSystems" ADD COLUMN "StarId" INTEGER""",
            // -- SdeStationOperations (9)
            """ALTER TABLE "SdeStationOperations" ADD COLUMN "ActivityId" INTEGER""",
            """ALTER TABLE "SdeStationOperations" ADD COLUMN "Description" TEXT NOT NULL DEFAULT ''""",
            """ALTER TABLE "SdeStationOperations" ADD COLUMN "ManufacturingFactor" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeStationOperations" ADD COLUMN "ResearchFactor" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeStationOperations" ADD COLUMN "Ratio" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeStationOperations" ADD COLUMN "Border" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeStationOperations" ADD COLUMN "Corridor" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeStationOperations" ADD COLUMN "Fringe" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeStationOperations" ADD COLUMN "Hub" REAL NOT NULL DEFAULT 0""",
            // -- SdeStations (8)
            """ALTER TABLE "SdeStations" ADD COLUMN "CelestialIndex" INTEGER""",
            """ALTER TABLE "SdeStations" ADD COLUMN "OrbitId" INTEGER""",
            """ALTER TABLE "SdeStations" ADD COLUMN "OrbitIndex" INTEGER""",
            """ALTER TABLE "SdeStations" ADD COLUMN "ReprocessingHangarFlag" INTEGER""",
            """ALTER TABLE "SdeStations" ADD COLUMN "UseOperationName" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeStations" ADD COLUMN "X" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeStations" ADD COLUMN "Y" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeStations" ADD COLUMN "Z" REAL NOT NULL DEFAULT 0""",
            // -- SdeTypes (9)
            """ALTER TABLE "SdeTypes" ADD COLUMN "PackagedVolume" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeTypes" ADD COLUMN "MetaLevel" INTEGER""",
            """ALTER TABLE "SdeTypes" ADD COLUMN "TechLevel" INTEGER""",
            """ALTER TABLE "SdeTypes" ADD COLUMN "IsRepackable" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeTypes" ADD COLUMN "IsDynamicType" INTEGER NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeTypes" ADD COLUMN "Radius" REAL NOT NULL DEFAULT 0""",
            """ALTER TABLE "SdeTypes" ADD COLUMN "VariationParentTypeId" INTEGER""",
            """ALTER TABLE "SdeTypes" ADD COLUMN "SoundId" INTEGER""",
            """ALTER TABLE "SdeTypes" ADD COLUMN "ShipTreeGroupId" INTEGER""",
        };
        foreach (var sql in alters)
        {
            try { db.Database.ExecuteSqlRaw(sql); }
            catch { /* column already exists — idempotent */ }
        }
    }

    /// <summary>
    /// Empties every table this import refills, immediately before it refills them.
    /// </summary>
    /// <remarks>
    /// ⚠️ Derived from the model rather than hand-listed, because a hand-list is one line that
    /// gets forgotten. When SdeIndustryModifierSources was added to the import and not added
    /// here, the next import inserted its rows on top of the previous run's, violated the primary
    /// key and threw — and because this whole import is a WIPE followed by a refill, every stage
    /// after that one never ran and its table stayed empty. Ten tables came back blank from one
    /// missing line, type materials among them, which is reprocessing.
    ///
    /// <para>No foreign key is configured between any two of these, so no delete order is
    /// required. If one is ever added this needs a topological sort, not a hand-written order
    /// that the next new table silently falls out of.</para>
    /// </remarks>
    // -----------------------------------------------------------------------
    // Section importers
    // -----------------------------------------------------------------------

    private async Task ImportCategoriesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}categories.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageCategories, SettingsText.SdeNotFoundInZip, 0.32); return; }
        Report(p, SettingsText.ImportStageCategories, SettingsText.ImportParsing, 0.32);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, CategoryYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeCategory { CategoryId = kv.Key, Name = kv.Value.name?.en ?? "", Published = kv.Value.published, IconId = kv.Value.iconID });
        await SaveBatchesAsync(db, db.SdeCategories, rows, SettingsText.ImportStageCategories, raw.Count, p, 0.32, 0.33, ct);
        await SaveNamesAsync(db, SdeNameKind.Category, raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.33, ct);
    }

    private async Task ImportGroupsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}groups.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageGroups, SettingsText.SdeNotFoundInZip, 0.33); return; }
        Report(p, SettingsText.ImportStageGroups, SettingsText.ImportParsing, 0.33);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, GroupYaml>>(reader) ?? [];
        Report(p, SettingsText.ImportStageGroups, string.Format(SettingsText.SdeParsedGroups, raw.Count), 0.335);
        var rows = raw.Select(kv => new SdeGroup
        {
            GroupId    = kv.Key,
            CategoryId = kv.Value.categoryID,
            Name       = kv.Value.name?.en ?? "",
            Published  = kv.Value.published,
            Anchorable = kv.Value.anchorable,
            Anchored   = kv.Value.anchored,
            IconId     = kv.Value.iconID,
            FittableNonSingleton = kv.Value.fittableNonSingleton,
            UseBasePrice         = kv.Value.useBasePrice,
        });
        await SaveBatchesAsync(db, db.SdeGroups, rows, SettingsText.ImportStageGroups, raw.Count, p, 0.335, 0.35, ct);
        await SaveNamesAsync(db, SdeNameKind.Group, raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.35, ct);
    }

    private async Task ImportMarketGroupsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}marketGroups.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageMarketGroups, SettingsText.SdeNotFoundInZip, 0.35); return; }
        Report(p, SettingsText.ImportStageMarketGroups, SettingsText.ImportParsing, 0.35);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, MarketGroupYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeMarketGroup
        {
            MarketGroupId = kv.Key, ParentGroupId = kv.Value.parentGroupID,
            Name        = kv.Value.nameID?.en        ?? kv.Value.name?.en        ?? "",
            Description = kv.Value.descriptionID?.en ?? kv.Value.description?.en ?? "",
            IconId = kv.Value.iconID, HasTypes = kv.Value.hasTypes,
        });
        await SaveBatchesAsync(db, db.SdeMarketGroups, rows, SettingsText.ImportStageMarketGroups, raw.Count, p, 0.35, 0.36, ct);
        await SaveNamesAsync(db, SdeNameKind.MarketGroup, raw.Select(kv => ((long)kv.Key, kv.Value.nameID ?? kv.Value.name)), p, 0.36, ct);
        await SaveTextsAsync(db, SdeTextKind.MarketGroupDescription,
            raw.Select(kv => ((long)kv.Key, kv.Value.descriptionID ?? kv.Value.description)), p, 0.36, ct);
    }

    private async Task ImportTypesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}types.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageTypes, SettingsText.SdeNotFoundInZip, 0.36); return; }
        Report(p, SettingsText.ImportStageTypes, ParsingLarge("types.yaml"), 0.36);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, TypeYaml>>(reader) ?? [];
        Report(p, SettingsText.ImportStageTypes, string.Format(SettingsText.SdeParsedTypes, raw.Count), 0.37);
        var rows = raw.Select(kv => new SdeType
        {
            TypeId        = kv.Key,
            GroupId       = kv.Value.groupID,
            Name          = kv.Value.nameID?.en        ?? kv.Value.name?.en        ?? "",
            Description   = kv.Value.descriptionID?.en ?? kv.Value.description?.en ?? "",
            Volume        = kv.Value.volume,
            PackagedVolume = kv.Value.packagedVolume ?? 0,
            MetaLevel      = kv.Value.metaLevel,
            TechLevel      = kv.Value.techLevel,
            IsRepackable   = kv.Value.isRepackable  ?? false,
            IsDynamicType  = kv.Value.isDynamicType ?? false,
            Radius         = kv.Value.radius ?? 0,
            VariationParentTypeId = kv.Value.variationParentTypeID,
            SoundId        = kv.Value.soundID,
            ShipTreeGroupId = kv.Value.shipTreeGroupID,
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
        await SaveBatchesAsync(db, db.SdeTypes, rows, SettingsText.ImportStageTypes, raw.Count, p, 0.37, 0.50, ct);
        await SaveNamesAsync(db, SdeNameKind.Type, raw.Select(kv => ((long)kv.Key, kv.Value.nameID ?? kv.Value.name)), p, 0.50, ct);
    }

    /// <summary>
    /// The published types' descriptions in the client's other languages, in a pass of their own
    /// over types.yaml.
    /// </summary>
    /// <remarks>
    /// ⚠️ A second pass rather than more fields in the first, for memory. The first deserializes the
    /// whole file into one dictionary, and the descriptions are most of the file: all eight languages
    /// of them would be a hundred and more megabytes of strings held at once, beside everything else
    /// the first pass holds. This walks the file's top-level mapping instead, deserializing one type
    /// at a time and keeping only the batch of rows on its way to the database. It reads the archive
    /// as a stream, too, where every other stage buffers its whole file before parsing it.
    ///
    /// <para>Published types only: nothing shows the others, and they are a third of the text.</para>
    /// </remarks>
    private async Task ImportTypeDescriptionsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}types.yaml");
        if (entry is null) return;   // the types stage has said so already
        Report(p, SettingsText.ImportStageDescriptions, ParsingLarge("types.yaml"), 0.50);

        using var reader = OpenEntryStreaming(entry);
        await SaveTextsAsync(db, SdeTextKind.TypeDescription, PublishedTypeDescriptions(reader), p, 0.50, ct);
    }

    /// <summary>Each published type's description, one type at a time, as the parser reaches it.</summary>
    private IEnumerable<(long Id, LocalizedName? Text)> PublishedTypeDescriptions(TextReader reader)
    {
        var parser = new Parser(reader);
        parser.Consume<StreamStart>();
        // An empty file, or one that is not a mapping of types: the first pass has read the same
        // file and said what was wrong with it.
        if (!parser.TryConsume<DocumentStart>(out _) || !parser.TryConsume<MappingStart>(out _)) yield break;

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            if (!parser.TryConsume<Scalar>(out var key))
            {
                parser.SkipThisAndNestedEvents();   // the key
                parser.SkipThisAndNestedEvents();   // its value
                continue;
            }

            // Deserializes the one node the parser is at — this type — and leaves it at the next key.
            var type = _yaml.Deserialize<TypeDescriptionYaml?>(parser);
            if (type is null || !type.published
                || !long.TryParse(key.Value, System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out var typeId))
                continue;

            yield return (typeId, type.descriptionID ?? type.description);
        }
    }

    private async Task ImportDogmaAttributeCategoriesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        // Try FSD dict format first (new flat SDE), then BSD list format (classic SDE).
        var bsdRoot = fsdRoot.Length == 0 ? "" : fsdRoot.Replace("fsd/", "bsd/");
        var entry = zip.GetEntry($"{fsdRoot}dogmaAttributeCategories.yaml")
                 ?? zip.GetEntry($"{bsdRoot}dgmAttributeCategories.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageAttrCategories, SettingsText.SdeNotFoundSkipped, 0.495); return; }

        Report(p, SettingsText.ImportStageAttrCategories, SettingsText.ImportParsing, 0.495);
        List<SdeDogmaAttributeCategory> rows;
        List<(long, LocalizedName?)>    names;
        try
        {
            using var reader = OpenEntry(entry);
            var raw = _yaml.Deserialize<Dictionary<int, DogmaAttrCategoryYaml>>(reader) ?? [];
            rows = raw.Select(kv => new SdeDogmaAttributeCategory
            {
                CategoryId = kv.Key,
                Name       = kv.Value.nameID?.en ?? kv.Value.name?.en ?? ""
            }).ToList();
            names = raw.Select(kv => ((long)kv.Key, kv.Value.nameID ?? kv.Value.name)).ToList();
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
                    Name       = x.nameID?.en ?? x.name?.en ?? ""
                }).ToList();
                names = raw.Where(x => x.categoryID.HasValue)
                    .Select(x => ((long)x.categoryID!.Value, x.nameID ?? x.name)).ToList();
            }
            catch { return; }
        }

        await SaveBatchesAsync(db, db.SdeDogmaAttributeCategories, rows, SettingsText.ImportStageAttrCategories, rows.Count, p, 0.495, 0.50, ct);
        await SaveNamesAsync(db, SdeNameKind.DogmaAttributeCategory, names, p, 0.50, ct);
    }

    private async Task ImportDogmaAttributesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}dogmaAttributes.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageDogmaAttributes, SettingsText.SdeNotFoundInZip, 0.50); return; }
        Report(p, SettingsText.ImportStageDogmaAttributes, SettingsText.ImportParsing, 0.50);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, DogmaAttributeYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeDogmaAttribute
        {
            AttributeId  = kv.Key,
            Name         = kv.Value.name ?? "",
            // New SDE: displayName is a localised mapping; old: a plain string. The converter reads
            // either as the English, and an attribute with neither falls back to its internal name.
            DisplayName  = kv.Value.displayName?.en ?? kv.Value.displayNameID?.en ?? kv.Value.name ?? "",
            // New SDE uses attributeCategoryID; old used categoryID
            CategoryId   = kv.Value.attributeCategoryID ?? kv.Value.categoryID,
            DefaultValue = kv.Value.defaultValue,
            HighIsGood   = kv.Value.highIsGood,
            Stackable    = kv.Value.stackable,
            UnitId       = kv.Value.unitID,
            Published    = kv.Value.published,
            Description  = kv.Value.description?.en ?? "",
            IconId       = kv.Value.iconID,
            MinAttributeId = kv.Value.minAttributeID,
            MaxAttributeId = kv.Value.maxAttributeID,
            // ⚠️ Both spellings, whichever the file has. Only the ...ID keys used to be read, the
            // current SDE writes tooltipTitle / tooltipDescription, and every one of these came out
            // empty. English only: the other languages are kept for names, not for text.
            TooltipTitle       = kv.Value.tooltipTitle?.en       ?? kv.Value.tooltipTitleID?.en       ?? "",
            TooltipDescription = kv.Value.tooltipDescription?.en ?? kv.Value.tooltipDescriptionID?.en ?? "",
            DataType     = kv.Value.dataType,
            DisplayWhenZero = kv.Value.displayWhenZero,
            ChargeRechargeTimeId = kv.Value.chargeRechargeTimeID,
        });
        await SaveBatchesAsync(db, db.SdeDogmaAttributes, rows, SettingsText.ImportStageDogmaAttributes, raw.Count, p, 0.50, 0.52, ct);
        await SaveNamesAsync(db, SdeNameKind.DogmaAttribute,
            raw.Select(kv => ((long)kv.Key, kv.Value.displayName ?? kv.Value.displayNameID)), p, 0.52, ct);
    }

    private async Task ImportDogmaEffectsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}dogmaEffects.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageDogmaEffects, SettingsText.SdeNotFoundInZip, 0.52); return; }
        Report(p, SettingsText.ImportStageDogmaEffects, SettingsText.ImportParsing, 0.52);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, DogmaEffectYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeDogmaEffect
        {
            EffectId    = kv.Key,
            // New SDE uses "name"; old SDE used "effectName"
            Name        = kv.Value.name ?? kv.Value.effectName ?? "",
            // ⚠️ Both spellings of each, whichever the file has — as for the attributes' tooltips.
            // Only displayNameID and descriptionID used to be read: every Description came out
            // empty, and every DisplayName fell through to the internal name.
            DisplayName = kv.Value.displayName?.en ?? kv.Value.displayNameID?.en ?? kv.Value.name ?? kv.Value.effectName ?? "",
            Description = kv.Value.description?.en ?? kv.Value.descriptionID?.en ?? "",
            IsOffensive = kv.Value.isOffensive,
            IsAssistance = kv.Value.isAssistance,
            Published   = kv.Value.published,
            EffectCategory           = kv.Value.effectCategoryID ?? kv.Value.effectCategory ?? 0,
            IsWarpSafe               = kv.Value.isWarpSafe,
            DisallowAutoRepeat       = kv.Value.disallowAutoRepeat,
            DurationAttributeId      = kv.Value.durationAttributeID,
            DischargeAttributeId     = kv.Value.dischargeAttributeID,
            RangeAttributeId         = kv.Value.rangeAttributeID,
            FalloffAttributeId       = kv.Value.falloffAttributeID,
            TrackingSpeedAttributeId = kv.Value.trackingSpeedAttributeID,
            ResistanceAttributeId    = kv.Value.resistanceAttributeID,
            FittingUsageChanceAttributeId = kv.Value.fittingUsageChanceAttributeID,
        });
        await SaveBatchesAsync(db, db.SdeDogmaEffects, rows, SettingsText.ImportStageDogmaEffects, raw.Count, p, 0.52, 0.53, ct);
        await SaveNamesAsync(db, SdeNameKind.DogmaEffect,
            raw.Select(kv => ((long)kv.Key, kv.Value.displayName ?? kv.Value.displayNameID)), p, 0.53, ct);

        // modifierInfo, one row per entry. The order within an effect is kept as the key, which
        // is all it is: the engine applies modifiers by operation, not by position.
        var mods = raw.SelectMany(kv => (kv.Value.modifierInfo ?? []).Select((m, i) => new SdeDogmaEffectModifier
        {
            EffectId             = kv.Key,
            Ordinal              = i,
            Func                 = m.func   ?? "",
            Domain               = m.domain ?? "",
            Operation            = m.operation,
            ModifiedAttributeId  = m.modifiedAttributeID,
            ModifyingAttributeId = m.modifyingAttributeID,
            GroupId              = m.groupID,
            SkillTypeId          = m.skillTypeID,
            StoppedEffectId      = m.effectID,
        }));
        await SaveBatchesAsync(db, db.SdeDogmaEffectModifiers, mods, SettingsText.ImportStageDogmaEffectModifiers, -1, p, 0.53, 0.54, ct);
    }

    private async Task ImportTypeDogmaAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}typeDogma.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageTypeDogma, SettingsText.SdeNotFoundInZip, 0.54); return; }
        Report(p, SettingsText.ImportStageTypeDogma, ParsingLarge("typeDogma.yaml"), 0.54);
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

        await SaveBatchesAsync(db, db.SdeTypeDogmaAttributes, attrs, SettingsText.ImportStageTypeDogmaAttributes, -1, p, 0.54, 0.63, ct);
        await SaveBatchesAsync(db, db.SdeTypeDogmaEffects,    effs,  SettingsText.ImportStageTypeDogmaEffects,    -1, p, 0.63, 0.67, ct);
    }

    private async Task ImportBlueprintsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}blueprints.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageBlueprints, SettingsText.SdeNotFoundInZip, 0.67); return; }
        Report(p, SettingsText.ImportStageBlueprints, Parsing("blueprints.yaml"), 0.67);
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

        await SaveBatchesAsync(db, db.SdeBlueprints,         bps,    SettingsText.ImportStageBlueprints,         raw.Count, p, 0.67, 0.69, ct);
        await SaveBatchesAsync(db, db.SdeBlueprintMaterials, mats,   SettingsText.ImportStageBlueprintMaterials, -1,        p, 0.69, 0.72, ct);
        await SaveBatchesAsync(db, db.SdeBlueprintProducts,  prods,  SettingsText.ImportStageBlueprintProducts,  -1,        p, 0.72, 0.74, ct);
        await SaveBatchesAsync(db, db.SdeBlueprintSkills,    skills, SettingsText.ImportStageBlueprintSkills,    -1,        p, 0.74, 0.76, ct);
    }

    // The client's numerals, and the same ones the station names are built with.
    private static string RomanNumeral(int n) => LocationNames.Roman(n);

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

    /// <returns>
    /// The planets, moons and asteroid belts that have a name of their own, by id, in every
    /// language the SDE gives it — for the station names built at the end of the import, which is
    /// the only thing that needs them. Empty for the old nested SDE, which carries none.
    /// </returns>
    private async Task<Dictionary<long, LocalizedName>> ImportUniverseAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        // New SDE: flat files mapRegions.yaml, mapConstellations.yaml, mapSolarSystems.yaml, mapStargates.yaml
        // Old SDE: nested universe/ directory walk
        var regEntry = zip.GetEntry($"{fsdRoot}mapRegions.yaml");
        if (regEntry != null)
            return await ImportUniverseFlatAsync(zip, fsdRoot, db, p, ct);

        // Old nested-directory format
        await ImportUniverseNestedAsync(zip, fsdRoot, db, p, ct);
        return [];
    }

    private async Task<Dictionary<long, LocalizedName>> ImportUniverseFlatAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        // A few hundred bodies have a name of their own — "Amarr VIII (Oris)" — which the SDE gives
        // in every language (see NamedCelestialYaml). The English goes in the celestial's Name
        // column; all of them are kept for the station names. Only a name that differs from the
        // one the body would be given anyway counts, which also bounds this should the SDE ever
        // name every body: there are some 450,000.
        var customNames = new Dictionary<long, LocalizedName>();
        string Named(long id, NamedCelestialYaml body, string generated)
        {
            var own = body.CustomName();
            var english = own?.en?.Trim();
            if (own is null || string.IsNullOrEmpty(english) || english == generated) return generated;
            customNames[id] = own;
            return english;
        }

        Report(p, SettingsText.ImportStageUniverse, Parsing("mapRegions.yaml"), 0.76);
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
                Description = kv.Value.description?.en ?? "",
                NebulaId    = kv.Value.nebulaID,
                WormholeClassId = kv.Value.wormholeClassID,
                X = kv.Value.position?.x ?? 0,
                Y = kv.Value.position?.y ?? 0,
                Z = kv.Value.position?.z ?? 0,
            });
            await SaveBatchesAsync(db, db.SdeRegions, rows, SettingsText.ImportStageRegions, raw.Count, p, 0.76, 0.78, ct);
            await SaveNamesAsync(db, SdeNameKind.Region, raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.78, ct);
        }

        Report(p, SettingsText.ImportStageUniverse, Parsing("mapConstellations.yaml"), 0.78);
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
            await SaveBatchesAsync(db, db.SdeConstellations, rows, SettingsText.ImportStageConstellations, raw.Count, p, 0.78, 0.80, ct);
            await SaveNamesAsync(db, SdeNameKind.Constellation, raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.80, ct);
        }

        Report(p, SettingsText.ImportStageUniverse, Parsing("mapSolarSystems.yaml"), 0.80);
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
                WormholeClassId = kv.Value.wormholeClassID,
                Border        = kv.Value.border,
                Corridor      = kv.Value.corridor,
                Fringe        = kv.Value.fringe,
                Hub           = kv.Value.hub,
                International = kv.Value.international,
                Regional      = kv.Value.regional,
                Luminosity    = kv.Value.luminosity,
                VisualEffect  = kv.Value.visualEffect ?? "",
                StarId        = kv.Value.starID,
            });
            await SaveBatchesAsync(db, db.SdeSolarSystems, rows, SettingsText.ImportStageSolarSystems, raw.Count, p, 0.80, 0.82, ct);
            await SaveNamesAsync(db, SdeNameKind.SolarSystem, raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.82, ct);
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

        Report(p, SettingsText.ImportStageUniverse, Parsing("mapStargates.yaml"), 0.82);
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
            await SaveBatchesAsync(db, db.SdeStargates, rows, SettingsText.ImportStageStargates, raw.Count, p, 0.82, 0.83, ct);
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
        Report(p, SettingsText.ImportStageUniverse, Parsing("mapPlanets.yaml"), 0.83);
        var planetEntry = zip.GetEntry($"{fsdRoot}mapPlanets.yaml");
        if (planetEntry != null)
        {
            using var r = OpenEntry(planetEntry);
            var raw = _yaml.Deserialize<Dictionary<int, MapPlanetYaml>>(r) ?? [];
            foreach (var (pid, pl) in raw)
                if (pl.position is { } pp)
                    celestials.Add(new SdeCelestial { ItemId = pid, SolarSystemId = pl.solarSystemID,
                        TypeId = pl.typeID, Kind = 0, X = pp.x, Y = pp.y, Z = pp.z,
                        Name = Named(pid, pl, $"{sysNames.GetValueOrDefault(pl.solarSystemID, "")} {RomanNumeral(pl.celestialIndex)}".Trim()) });
        }

        Report(p, SettingsText.ImportStageUniverse, Parsing("mapMoons.yaml"), 0.84);
        var moonEntry = zip.GetEntry($"{fsdRoot}mapMoons.yaml");
        if (moonEntry != null)
        {
            using var r = OpenEntry(moonEntry);
            var raw = _yaml.Deserialize<Dictionary<int, MapMoonYaml>>(r) ?? [];
            foreach (var (mid, mo) in raw)
                if (mo.position is { } mp)
                    celestials.Add(new SdeCelestial { ItemId = mid, SolarSystemId = mo.solarSystemID,
                        TypeId = mo.typeID, Kind = 1, X = mp.x, Y = mp.y, Z = mp.z,
                        Name = Named(mid, mo, $"{sysNames.GetValueOrDefault(mo.solarSystemID, "")} {RomanNumeral(mo.celestialIndex)} - Moon {mo.orbitIndex}".Trim()) });
        }

        // Asteroid belts. CCP has shipped these under more than one name across SDE revisions,
        // so the candidates are tried in turn rather than assuming one — a missing file simply
        // means no belts, which is also the correct outcome for an SDE that omits them.
        Report(p, SettingsText.ImportStageUniverse, SettingsText.SdeParsingBelts, 0.845);
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
                        Name = Named(bid, b, $"{sysNames.GetValueOrDefault(b.solarSystemID, "")} " +
                                             $"{RomanNumeral(b.celestialIndex)} - Asteroid Belt {b.orbitIndex}".Trim()),
                    });
            break;
        }

        await SaveBatchesAsync(db, db.SdeCelestials, celestials, SettingsText.ImportStageCelestials, celestials.Count, p, 0.85, 0.87, ct);

        // Equinox planetary production. The reagent is unnamed here — it is decided by the
        // planet's type, Lava yielding Magmatic Gas and Ice yielding Superionic Ice.
        Report(p, SettingsText.ImportStageUniverse, Parsing("planetResources.yaml"), 0.868);
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
            await SaveBatchesAsync(db, db.SdePlanetResources, rows, SettingsText.ImportStagePlanetResources,
                raw.Count, p, 0.868, 0.87, ct);
        }

        return customNames;
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
        Report(p, SettingsText.ImportStageAgents, Parsing("agentTypes.yaml"), 0.872);
        var typeEntry = zip.GetEntry($"{fsdRoot}agentTypes.yaml");
        if (typeEntry != null)
        {
            using var r = OpenEntry(typeEntry);
            var raw = _yaml.Deserialize<Dictionary<int, AgentTypeYaml>>(r) ?? [];
            await SaveBatchesAsync(db, db.SdeAgentTypes,
                raw.Select(kv => new SdeAgentType { AgentTypeId = kv.Key, Name = kv.Value.name ?? "" }),
                SettingsText.ImportStageAgentTypes, raw.Count, p, 0.872, 0.873, ct);
        }

        Report(p, SettingsText.ImportStageAgents, Parsing("npcCorporationDivisions.yaml"), 0.873);
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
                SettingsText.ImportStageCorpDivisions, raw.Count, p, 0.873, 0.874, ct);
            await SaveNamesAsync(db, SdeNameKind.NpcCorporationDivision,
                raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.874, ct);
        }

        Report(p, SettingsText.ImportStageAgents, Parsing("npcCharacters.yaml"), 0.874);
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

        await SaveBatchesAsync(db, db.SdeAgents, agents, SettingsText.ImportStageAgents, agents.Count, p, 0.874, 0.88, ct);

        // The agents' names only: the rest of the file's characters have no table to name.
        await SaveNamesAsync(db, SdeNameKind.Agent,
            chars.Where(kv => kv.Value.agent is not null).Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.88, ct);
    }

    private class AgentTypeYaml
    {
        public string? name { get; set; }
    }

    private class CorpDivisionYaml
    {
        public string?          internalName { get; set; }
        public LocalizedName?   name         { get; set; }
    }

    private class NpcCharacterYaml
    {
        public int              corporationID { get; set; }
        public long             locationID    { get; set; }
        public LocalizedName?   name          { get; set; }
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
        Report(p, SettingsText.ImportStageUniverse, SettingsText.SdeScanningEntries, 0.76);
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

        Report(p, SettingsText.ImportStageUniverse, string.Format(SettingsText.SdeFoundUniverse, regionEntries.Count, constellationEntries.Count, systemEntries.Count), 0.76);

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
        await SaveBatchesAsync(db, db.SdeRegions, regions, SettingsText.ImportStageRegions, regions.Count, p, 0.76, 0.78, ct);

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
        await SaveBatchesAsync(db, db.SdeConstellations, constellations, SettingsText.ImportStageConstellations, constellations.Count, p, 0.78, 0.80, ct);

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
        await SaveBatchesAsync(db, db.SdeSolarSystems, systems,    SettingsText.ImportStageSolarSystems, systems.Count,    p, 0.80, 0.83, ct);
        await SaveBatchesAsync(db, db.SdeStargates,    stargates,  SettingsText.ImportStageStargates,     stargates.Count,  p, 0.83, 0.85, ct);
        await SaveBatchesAsync(db, db.SdeCelestials,   celestials, SettingsText.ImportStageCelestials,    celestials.Count, p, 0.85, 0.87, ct);
    }

    private async Task ImportStationsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        // New SDE: npcStations.yaml (dict, no names) — use ESI bulk names endpoint to populate
        var newEntry = zip.GetEntry($"{fsdRoot}npcStations.yaml");
        if (newEntry != null)
        {
            Report(p, SettingsText.ImportStageStations, Parsing("npcStations.yaml"), 0.87);
            using var reader = OpenEntry(newEntry);
            var raw = _yaml.Deserialize<Dictionary<int, NpcStationYaml>>(reader) ?? [];

            // npcStations.yaml carries only the solar system, unlike the old staStations.yaml
            // which also gave constellation, region and security. Those three are resolved from
            // the systems imported moments ago by ImportUniverseAsync rather than left at zero:
            // a zero here is indistinguishable from a real id, so anything grouping or filtering
            // on the column silently returns nothing instead of failing.
            var systems = await db.SdeSolarSystems.AsNoTracking()
                .Select(s => new { s.SolarSystemId, s.ConstellationId, s.RegionId, s.Security })
                .ToDictionaryAsync(s => s.SolarSystemId, ct);

            Report(p, SettingsText.ImportStageStations, string.Format(SettingsText.SdeFetchingStationNames, raw.Count), 0.875);
            var names = await FetchEsiNamesAsync(raw.Keys.ToList(), "station", ct);
            var rows = raw.Select(kv =>
            {
                systems.TryGetValue(kv.Value.solarSystemID, out var sys);
                return new SdeStation
                {
                    StationId              = kv.Key,
                    Name                   = names.GetValueOrDefault(kv.Key, ""),
                    SolarSystemId          = kv.Value.solarSystemID,
                    ConstellationId        = sys?.ConstellationId ?? 0,
                    RegionId               = sys?.RegionId ?? 0,
                    CorporationId          = kv.Value.ownerID,
                    StationTypeId          = kv.Value.typeID,
                    Security               = sys?.Security ?? 0,
                    ReprocessingEfficiency = kv.Value.reprocessingEfficiency,
                    ReprocessingTax        = kv.Value.reprocessingStationsTake,
                    OperationId            = kv.Value.operationID,
                    CelestialIndex         = kv.Value.celestialIndex,
                    OrbitId                = kv.Value.orbitID,
                    OrbitIndex             = kv.Value.orbitIndex,
                    ReprocessingHangarFlag = kv.Value.reprocessingHangarFlag,
                    UseOperationName       = kv.Value.useOperationName,
                    X = kv.Value.position?.x ?? 0,
                    Y = kv.Value.position?.y ?? 0,
                    Z = kv.Value.position?.z ?? 0,
                };
            });
            await SaveBatchesAsync(db, db.SdeStations, rows, SettingsText.ImportStageStations, raw.Count, p, 0.875, 0.89, ct);
            await ImportStationServicesAsync(zip, fsdRoot, db, p, ct);
            return;
        }

        // Old SDE: bsd/staStations.yaml (list, has names)
        var bsdRoot  = fsdRoot.Length == 0 ? "" : fsdRoot.Replace("fsd/", "bsd/");
        var oldEntry = zip.GetEntry($"{bsdRoot}staStations.yaml");
        if (oldEntry is null) { Report(p, SettingsText.ImportStageStations, SettingsText.SdeNotFoundInZip, 0.87); return; }
        Report(p, SettingsText.ImportStageStations, Parsing("staStations.yaml"), 0.87);
        using var oldReader = OpenEntry(oldEntry);
        var oldRaw = _yaml.Deserialize<List<StationYaml>>(oldReader) ?? [];
        var oldRows = oldRaw.Select(s => new SdeStation
        {
            StationId = s.stationID, Name = s.stationName ?? "",
            SolarSystemId = s.solarSystemID, ConstellationId = s.constellationID, RegionId = s.regionID,
            CorporationId = s.corporationID, StationTypeId = s.stationTypeID,
            Security = s.security, ReprocessingEfficiency = s.reprocessingEfficiency, ReprocessingTax = s.reprocessingStationsTake,
        });
        await SaveBatchesAsync(db, db.SdeStations, oldRows, SettingsText.ImportStageStations, oldRaw.Count, p, 0.87, 0.89, ct);
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
        if (entry is null) { Report(p, SettingsText.ImportStageFactions, SettingsText.SdeNotFoundInZip, 0.89); return; }
        Report(p, SettingsText.ImportStageFactions, SettingsText.ImportParsing, 0.89);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, FactionYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeFaction
        {
            FactionId = kv.Key,
            Name = kv.Value.nameID?.en ?? kv.Value.name?.en ?? "",
            Description = kv.Value.descriptionID?.en ?? kv.Value.description?.en ?? "",
            CorporationId = kv.Value.corporationID, MilitiaCorporationId = kv.Value.militiaCorporationID, SolarSystemId = kv.Value.solarSystemID,
            IconId           = kv.Value.iconID,
            ShortDescription = kv.Value.shortDescriptionID?.en ?? kv.Value.shortDescription?.en ?? "",
            SizeFactor       = kv.Value.sizeFactor ?? 0,
            UniqueName       = kv.Value.uniqueName,
        });
        await SaveBatchesAsync(db, db.SdeFactions, rows, SettingsText.ImportStageFactions, raw.Count, p, 0.89, 0.91, ct);
        await SaveNamesAsync(db, SdeNameKind.Faction, raw.Select(kv => ((long)kv.Key, kv.Value.nameID ?? kv.Value.name)), p, 0.91, ct);
        await SaveTextsAsync(db, SdeTextKind.FactionDescription,
            raw.Select(kv => ((long)kv.Key, kv.Value.descriptionID ?? kv.Value.description)), p, 0.91, ct);
    }

    private async Task ImportNpcCorporationsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}npcCorporations.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageNpcCorporations, SettingsText.SdeNotFoundInZip, 0.91); return; }
        Report(p, SettingsText.ImportStageNpcCorporations, SettingsText.ImportParsing, 0.91);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, NpcCorpYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeNpcCorporation
        {
            CorporationId = kv.Key,
            Name          = kv.Value.name?.en ?? "",
            FactionId     = kv.Value.factionID,
            StationId     = kv.Value.stationID,
            SolarSystemId = kv.Value.solarSystemID,
            Ticker        = kv.Value.tickerName ?? "",
            Description   = kv.Value.description?.en ?? "",
            CeoId         = kv.Value.ceoID,
            TaxRate       = kv.Value.taxRate ?? 0,
            Size          = kv.Value.size   ?? "",
            Extent        = kv.Value.extent ?? "",
            MemberLimit   = kv.Value.memberLimit,
            MinSecurity   = kv.Value.minSecurity ?? 0,
            MinimumJoinStanding = kv.Value.minimumJoinStanding,
            EnemyId       = kv.Value.enemyID,
            FriendId      = kv.Value.friendID,
            RaceId        = kv.Value.raceID,
            IconId        = kv.Value.iconID,
            MainActivityId      = kv.Value.mainActivityID,
            SecondaryActivityId = kv.Value.secondaryActivityID,
            Deleted       = kv.Value.deleted,
        });
        await SaveBatchesAsync(db, db.SdeNpcCorporations, rows, SettingsText.ImportStageNpcCorporations, raw.Count, p, 0.91, 0.93, ct);
        await SaveNamesAsync(db, SdeNameKind.NpcCorporation, raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.93, ct);
        await SaveTextsAsync(db, SdeTextKind.NpcCorporationDescription,
            raw.Select(kv => ((long)kv.Key, kv.Value.description)), p, 0.93, ct);
    }

    /// <summary>
    /// Which dogma attribute carries each structure or rig industry bonus.
    ///
    /// <para>⚠️ Flattened on the way in. The file nests type → activity → kind → a list of
    /// attributes, which is four levels of dictionary and awkward to query; one row per attribute
    /// is the same information and joins to typeDogma directly.</para>
    /// </summary>
    private async Task ImportIndustryModifierSourcesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}industryModifierSources.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageIndustryModifiers, SettingsText.SdeNotFoundInZip, 0.93); return; }
        Report(p, SettingsText.ImportStageIndustryModifiers, SettingsText.ImportParsing, 0.93);

        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, Dictionary<string, Dictionary<string, List<IndustryModifierYaml>>>>>(reader) ?? [];

        var rows = raw.SelectMany(type => type.Value
            .SelectMany(activity => activity.Value
                .SelectMany(kind => kind.Value.Select(mod => new SdeIndustryModifierSource
                {
                    TypeId           = type.Key,
                    Activity         = activity.Key,
                    BonusKind        = kind.Key,
                    DogmaAttributeId = mod.dogmaAttributeID,
                    FilterId         = mod.filterID,
                }))))
            // One type can name the same attribute twice under different filters; the key cannot
            // carry both, and the narrower one is the one worth keeping.
            .GroupBy(r => (r.TypeId, r.Activity, r.BonusKind, r.DogmaAttributeId))
            .Select(g => g.OrderByDescending(r => r.FilterId ?? 0).First());

        await SaveBatchesAsync(db, db.SdeIndustryModifierSources, rows, SettingsText.ImportStageIndustryModifiers, raw.Count, p, 0.93, 0.94, ct);
    }

    private async Task ImportRacesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}races.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageRaces, SettingsText.SdeNotFoundInZip, 0.93); return; }
        Report(p, SettingsText.ImportStageRaces, SettingsText.ImportParsing, 0.93);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, RaceYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeRace
        {
            RaceId      = kv.Key,
            Name        = kv.Value.name?.en ?? "",
            Description = kv.Value.description?.en ?? "",
            IconId      = kv.Value.iconID,
            ShipTypeId  = kv.Value.shipTypeID,
        });
        await SaveBatchesAsync(db, db.SdeRaces, rows, SettingsText.ImportStageRaces, raw.Count, p, 0.93, 0.94, ct);
        await SaveNamesAsync(db, SdeNameKind.Race, raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.94, ct);
        await SaveTextsAsync(db, SdeTextKind.RaceDescription, raw.Select(kv => ((long)kv.Key, kv.Value.description)), p, 0.94, ct);
    }

    private async Task ImportMetaGroupsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}metaGroups.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageMetaGroups, SettingsText.SdeNotFoundInZip, 0.94); return; }
        Report(p, SettingsText.ImportStageMetaGroups, SettingsText.ImportParsing, 0.94);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, MetaGroupYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeMetaGroup
        {
            MetaGroupId = kv.Key,
            Name        = kv.Value.name?.en ?? "",
            Description = kv.Value.description?.en ?? "",
            IconId      = kv.Value.iconID,
            IconSuffix  = kv.Value.iconSuffix ?? "",
            ColorHex    = kv.Value.color?.Hex ?? "",
        });
        await SaveBatchesAsync(db, db.SdeMetaGroups, rows, SettingsText.ImportStageMetaGroups, raw.Count, p, 0.94, 0.96, ct);
        await SaveNamesAsync(db, SdeNameKind.MetaGroup, raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.96, ct);
        await SaveTextsAsync(db, SdeTextKind.MetaGroupDescription, raw.Select(kv => ((long)kv.Key, kv.Value.description)), p, 0.96, ct);
    }

    private async Task ImportCertificatesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}certificates.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageCertificates, SettingsText.SdeNotFoundInZip, 0.96); return; }
        Report(p, SettingsText.ImportStageCertificates, SettingsText.ImportParsing, 0.96);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, CertificateYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeCertificate
        {
            CertificateId = kv.Key,
            GroupId       = kv.Value.groupID,
            Name        = kv.Value.name?.en        ?? "",
            Description = kv.Value.description?.en ?? "",
        });
        await SaveBatchesAsync(db, db.SdeCertificates, rows, SettingsText.ImportStageCertificates, raw.Count, p, 0.96, 0.97, ct);
        await SaveNamesAsync(db, SdeNameKind.Certificate, raw.Select(kv => ((long)kv.Key, kv.Value.name)), p, 0.97, ct);
        await SaveTextsAsync(db, SdeTextKind.CertificateDescription, raw.Select(kv => ((long)kv.Key, kv.Value.description)), p, 0.97, ct);
    }

    private async Task ImportTypeMaterialsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}typeMaterials.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageTypeMaterials, SettingsText.SdeNotFoundInZip, 0.97); return; }
        Report(p, SettingsText.ImportStageTypeMaterials, SettingsText.ImportParsing, 0.97);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, TypeMaterialsYaml>>(reader) ?? [];
        var rows = raw.SelectMany(kv =>
            (kv.Value.materials ?? []).Select(m => new SdeTypeMaterial
                { TypeId = kv.Key, MaterialTypeId = m.materialTypeID, Quantity = m.quantity }))
            .DistinctBy(x => (x.TypeId, x.MaterialTypeId));
        await SaveBatchesAsync(db, db.SdeTypeMaterials, rows, SettingsText.ImportStageTypeMaterials, -1, p, 0.97, 0.975, ct);
    }

    private async Task ImportPlanetSchematicsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}planetSchematics.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStagePiSchematics, SettingsText.SdeNotFoundInZip, 0.975); return; }
        Report(p, SettingsText.ImportStagePiSchematics, SettingsText.ImportParsing, 0.975);
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
        await SaveBatchesAsync(db, db.SdePlanetSchematics,     schematics, SettingsText.ImportStagePiSchematics,      raw.Count, p, 0.975, 0.985, ct);
        await SaveBatchesAsync(db, db.SdePlanetSchematicTypes, types,      SettingsText.ImportStagePiSchematicTypes,  -1,        p, 0.985, 0.987, ct);
        await SaveNamesAsync(db, SdeNameKind.PlanetSchematic, raw.Select(kv => ((long)kv.Key, kv.Value.name ?? kv.Value.nameID)), p, 0.987, ct);
    }

    private async Task ImportDogmaUnitsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}dogmaUnits.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageDogmaUnits, SettingsText.SdeNotFoundInZip, 0.987); return; }
        Report(p, SettingsText.ImportStageDogmaUnits, SettingsText.ImportParsing, 0.987);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, DogmaUnitYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeDogmaUnit
        {
            UnitId      = kv.Key,
            Name        = kv.Value.name ?? "",
            DisplayName = kv.Value.displayName?.en ?? "",
        });
        await SaveBatchesAsync(db, db.SdeDogmaUnits, rows, SettingsText.ImportStageDogmaUnits, raw.Count, p, 0.987, 0.989, ct);
        await SaveNamesAsync(db, SdeNameKind.DogmaUnit, raw.Select(kv => ((long)kv.Key, kv.Value.displayName)), p, 0.989, ct);
    }

    private async Task ImportIconsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}icons.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageIcons, SettingsText.SdeNotFoundInZip, 0.989); return; }
        Report(p, SettingsText.ImportStageIcons, ParsingLarge("icons.yaml"), 0.989);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, IconYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeIcon
        {
            IconId   = kv.Key,
            IconFile = kv.Value.iconFile ?? "",
        });
        await SaveBatchesAsync(db, db.SdeIcons, rows, SettingsText.ImportStageIcons, raw.Count, p, 0.989, 0.992, ct);
    }

    private async Task ImportGraphicsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}graphics.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageGraphics, SettingsText.SdeNotFoundInZip, 0.992); return; }
        Report(p, SettingsText.ImportStageGraphics, ParsingLarge("graphics.yaml"), 0.992);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, GraphicYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeGraphic
        {
            GraphicId   = kv.Key,
            GraphicFile = kv.Value.graphicFile,
        });
        await SaveBatchesAsync(db, db.SdeGraphics, rows, SettingsText.ImportStageGraphics, raw.Count, p, 0.992, 0.995, ct);
    }

    private async Task ImportSkinsAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}skins.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageSkins, SettingsText.SdeNotFoundInZip, 0.995); return; }
        Report(p, SettingsText.ImportStageSkins, SettingsText.ImportParsing, 0.995);
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
        await SaveBatchesAsync(db, db.SdeSkins,     skinRows, SettingsText.ImportStageSkins,      raw.Count, p, 0.995, 0.997, ct);
        await SaveBatchesAsync(db, db.SdeSkinTypes, typeRows, SettingsText.ImportStageSkinTypes, -1,        p, 0.997, 0.999, ct);
    }

    private async Task ImportSkinLicensesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
        IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var entry = zip.GetEntry($"{fsdRoot}skinLicenses.yaml");
        if (entry is null) { Report(p, SettingsText.ImportStageSkinLicenses, SettingsText.SdeNotFoundInZip, 0.999); return; }
        Report(p, SettingsText.ImportStageSkinLicenses, SettingsText.ImportParsing, 0.999);
        using var reader = OpenEntry(entry);
        var raw = _yaml.Deserialize<Dictionary<int, SkinLicenseYaml>>(reader) ?? [];
        var rows = raw.Select(kv => new SdeSkinLicense
        {
            LicenseTypeId = kv.Key,
            SkinId        = kv.Value.skinID,
            Duration      = kv.Value.duration,
        });
        await SaveBatchesAsync(db, db.SdeSkinLicenses, rows, SettingsText.ImportStageSkinLicenses, raw.Count, p, 0.999, 1.0, ct);
    }

    // -----------------------------------------------------------------------
    // NPC station names, built
    // -----------------------------------------------------------------------

    /// <summary>What <see cref="ImportStationNamesAsync"/> found, for its line in the log.</summary>
    private sealed record StationNamesCheck(string Summary, string? Detail);

    private sealed record StationParts(int StationId, string Name, int SolarSystemId, int? CorporationId,
        int? OperationId, bool UseOperationName, long? OrbitId, int? CelestialIndex, int? OrbitIndex);

    private sealed record OrbitParts(long ItemId, int Kind, int SolarSystemId, int TypeId);

    /// <summary>
    /// NPC station names in the client's other languages. Nothing ships them, so they are built the
    /// way the game client builds them: what the station orbits, its owner and its operation, each in
    /// that language, in the client's own format for it — see <see cref="LocationNames"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ After every stage, because it needs their work: the owners are NPC corporations, imported
    /// after the stations. Everything is read back from what the import has written — the parts'
    /// English columns and their SdeNames rows — except the names some planets, moons and belts have
    /// of their own, which have no table and are handed on from the universe stage.
    ///
    /// <para>A station's own celestialIndex and orbitIndex are its orbit's — the planet's place and
    /// the moon's number. Measured on the live database: every one of the 5,209 stations at a planet
    /// or a moon agrees with the name of its celestial. What it orbits decides the format. One
    /// station orbits its system's star; ESI names that orbit after the system, and so does this.</para>
    ///
    /// <para>The English is built too, by the same code, and compared with ESI's. That count is the
    /// one check there is on the formats and the parts — nothing publishes the other languages to
    /// compare them with — so it is logged whatever it says. Only a name that differs from ESI's
    /// English is stored, as for every other name.</para>
    /// </remarks>
    /// <returns>The check, for the log; null when there are no stations to name.</returns>
    private async Task<StationNamesCheck?> ImportStationNamesAsync(AppDbContext db,
        IReadOnlyDictionary<long, LocalizedName> customNames, IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var stations = await db.SdeStations.AsNoTracking()
            .OrderBy(s => s.StationId)
            .Select(s => new StationParts(s.StationId, s.Name, s.SolarSystemId, s.CorporationId, s.OperationId,
                                          s.UseOperationName, s.OrbitId, s.CelestialIndex, s.OrbitIndex))
            .ToListAsync(ct);
        if (stations.Count == 0) return null;

        // What they orbit. Kind is SdeCelestial's: 0 planet, 1 moon, 3 asteroid belt, 4 star.
        var orbitIds = stations.Where(s => s.OrbitId is not null).Select(s => s.OrbitId!.Value).Distinct().ToList();
        var orbits = await db.SdeCelestials.AsNoTracking()
            .Where(c => orbitIds.Contains(c.ItemId))
            .Select(c => new OrbitParts(c.ItemId, c.Kind, c.SolarSystemId, c.TypeId))
            .ToDictionaryAsync(c => c.ItemId, ct);

        // Each part's English: what each language falls back to where the SDE gives it no other.
        var systemIds   = stations.Select(s => s.SolarSystemId).Concat(orbits.Values.Select(o => o.SolarSystemId)).Distinct().ToList();
        var beltTypeIds = orbits.Values.Where(o => o.Kind == 3).Select(o => o.TypeId).Distinct().ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking().Where(s => systemIds.Contains(s.SolarSystemId))
            .Select(s => new { s.SolarSystemId, s.Name }).ToDictionaryAsync(s => s.SolarSystemId, s => s.Name, ct);
        var corps = await db.SdeNpcCorporations.AsNoTracking()
            .Select(c => new { c.CorporationId, c.Name }).ToDictionaryAsync(c => c.CorporationId, c => c.Name, ct);
        var operations = await db.SdeStationOperations.AsNoTracking()
            .Select(o => new { o.OperationId, o.Name }).ToDictionaryAsync(o => o.OperationId, o => o.Name, ct);
        var beltTypes = await db.SdeTypes.AsNoTracking().Where(t => beltTypeIds.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.Name }).ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        // And every other language of each, as the stages above stored them.
        var systemKeys = systemIds.Select(id => (long)id).ToList();
        var beltKeys   = beltTypeIds.Select(id => (long)id).ToList();
        var translated = (await db.SdeNames.AsNoTracking()
                .Where(n => (n.Kind == SdeNameKind.SolarSystem && systemKeys.Contains(n.Id))
                         || n.Kind == SdeNameKind.NpcCorporation
                         || n.Kind == SdeNameKind.StationOperation
                         || (n.Kind == SdeNameKind.Type && beltKeys.Contains(n.Id)))
                .Select(n => new { n.Kind, n.Id, n.Lang, n.Name })
                .ToListAsync(ct))
            .ToDictionary(n => (n.Kind, n.Id, n.Lang), n => n.Name);

        string Part(SdeNameKind kind, long id, string lang, string english) =>
            lang != "en" && translated.TryGetValue((kind, id, lang), out var name) ? name : english;

        // One station's name in one language; null when a part it needs is missing.
        string? Build(StationParts s, string lang)
        {
            if (s.OrbitId is not { } orbitId || !orbits.TryGetValue(orbitId, out var orbit)) return null;
            if (s.CorporationId is not { } corpId || !corps.TryGetValue(corpId, out var corpEnglish)) return null;
            if (!systems.TryGetValue(orbit.SolarSystemId, out var systemEnglish)) return null;

            string? operation = null;
            if (s.UseOperationName)
            {
                if (s.OperationId is not { } opId || !operations.TryGetValue(opId, out var opEnglish)) return null;
                operation = Part(SdeNameKind.StationOperation, opId, lang, opEnglish);
            }

            // A body's name of its own, in this language, is its name — "Amarr VIII (Oris)" — and
            // otherwise the client's format for what it is.
            var system = Part(SdeNameKind.SolarSystem, orbit.SolarSystemId, lang, systemEnglish);
            var own    = customNames.TryGetValue(orbitId, out var custom) ? custom.Get(lang)?.Trim() : null;
            var orbitName = !string.IsNullOrEmpty(own) ? own : (orbit.Kind, s.CelestialIndex, s.OrbitIndex) switch
            {
                (0, { } planet, _)        => LocationNames.Planet(lang, system, planet),
                (1, { } planet, { } moon) => LocationNames.Moon(lang, system, planet, moon),
                (3, { } planet, { } belt) when beltTypes.TryGetValue(orbit.TypeId, out var beltEnglish)
                                          => LocationNames.Belt(lang, system, planet,
                                                 Part(SdeNameKind.Type, orbit.TypeId, lang, beltEnglish), belt),
                (4, _, _)                 => system,
                _                         => null,
            };
            if (orbitName is null) return null;

            return LocationNames.Station(lang, orbitName, Part(SdeNameKind.NpcCorporation, corpId, lang, corpEnglish), operation);
        }

        int matched = 0, unbuilt = 0, withoutEsiName = 0;
        var examples = new List<string>();
        var names    = new List<(long, LocalizedName?)>(stations.Count);
        var filled   = new Dictionary<int, string>();
        foreach (var s in stations)
        {
            var english = Build(s, "en");
            if (english is not null && english == s.Name) matched++;
            else
            {
                if (english is null)    unbuilt++;
                if (s.Name.Length == 0) withoutEsiName++;
                if (examples.Count < 5)
                    examples.Add(english is null
                        ? $"{s.StationId} \"{s.Name}\": could not be built — what it orbits, its owner or its operation is missing."
                        : $"{s.StationId}: built \"{english}\", ESI has \"{s.Name}\".");
            }

            // ESI gave this one no English — it was down during the import, or left the name out — so
            // the built English stands in rather than no name at all: where both exist it IS ESI's,
            // which is what the count above measures.
            if (s.Name.Length == 0 && english is not null) filled[s.StationId] = english;

            // The English is the one each language is stored only if it differs from.
            var name = new LocalizedName { en = s.Name.Length > 0 ? s.Name : english ?? "" };
            foreach (var lang in SdeNames.OtherLanguages)
                if (Build(s, lang) is { } built) name.Set(lang, built);
            names.Add((s.StationId, name));
        }

        if (filled.Count > 0)
        {
            var ids = filled.Keys.ToList();
            foreach (var station in await db.SdeStations.Where(s => ids.Contains(s.StationId)).ToListAsync(ct))
                station.Name = filled[station.StationId];
            // ⚠️ The import turns automatic change detection off for its bulk inserts.
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        await SaveNamesAsync(db, SdeNameKind.Station, names, p, 0.999, ct);

        static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var detail = new List<string>();
        if (unbuilt > 0)        detail.Add($"{N(unbuilt)} could not be built from their parts.");
        if (withoutEsiName > 0) detail.Add($"ESI gave no English name for {N(withoutEsiName)}, so there was nothing to compare those with; {N(filled.Count)} of them now carry the built English.");
        detail.AddRange(examples);

        return new StationNamesCheck(
            $"Station names rebuilt from their parts: {N(matched)} of {N(stations.Count)} match ESI's English. "
            + "The other languages are built the same way and nothing else checks them: a low number means they are wrong too.",
            detail.Count == 0 ? null : string.Join("\n", detail));
    }

    // -----------------------------------------------------------------------
    // Batch save helper
    // -----------------------------------------------------------------------

    private async Task SaveBatchesAsync<T>(
        AppDbContext db,
        DbSet<T> set,
        IEnumerable<T> source,
        string stage,
        int estimatedTotal,
        IProgress<SdeImportProgress> p,
        double fracStart,
        double fracEnd,
        CancellationToken ct,
        bool emptyIsNormal = false) where T : class
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
                var detail = estimatedTotal > 0 ? $"{saved:N0} / {estimatedTotal:N0}" : string.Format(SettingsText.ImportRowsProgress, saved);
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

        p.Report(new SdeImportProgress(stage, string.Format(SettingsText.ImportRowsSaved, saved), fracEnd));

        // ⚠️ A stage that stores NOTHING from a file that exists is a silent failure, and this
        // import is a wipe followed by a refill: whatever it fails to store is simply gone. Ten
        // tables came back empty after a clean import — races, meta groups, certificates, type
        // materials, planet schematics, dogma units, icons, graphics, skins and skin licences —
        // and nothing anywhere said so. Type materials alone is reprocessing.
        //
        // Logged under the table's name rather than the stage's: the stage is shown to the person
        // importing, in their language, and the error log stays in English.
        //
        // emptyIsNormal is for a stage where nothing IS a possible right answer — the names of a
        // kind that reads the same in every language — and which reports in some other way.
        if (saved == 0 && !emptyIsNormal)
            _errors.Log("SdeImport", db.Model.FindEntityType(typeof(T))?.GetTableName() ?? typeof(T).Name,
                estimatedTotal > 0
                    ? $"Stored 0 of {estimatedTotal:N0} row(s) parsed. The file was read and nothing reached the database."
                    : "Stored 0 rows: the file was found but parsed to nothing.");
    }

    /// <summary>
    /// Stores one kind's names in the client's other languages: each language only where it differs
    /// from the English, which stays in the entity's own Name column. See <see cref="SdeName"/>.
    /// </summary>
    /// <remarks>
    /// Straight after the stage that read them, so each file's names are let go with the file
    /// rather than all held to the end — the types alone come to a few hundred thousand. A kind
    /// that stores none is not a fault: solar systems, say, are largely called the same in every
    /// language. Whether a whole LANGUAGE is missing is what the count at the end of the import
    /// says, which is where to look.
    /// </remarks>
    private Task SaveNamesAsync(AppDbContext db, SdeNameKind kind,
        IEnumerable<(long Id, LocalizedName? Name)> names,
        IProgress<SdeImportProgress> p, double frac, CancellationToken ct)
    {
        return SaveBatchesAsync(db, db.SdeNames, Rows(), SettingsText.ImportStageNames, -1, p, frac, frac, ct,
            emptyIsNormal: true);

        IEnumerable<SdeName> Rows()
        {
            // One row per id and language, which the key insists on — and a list-shaped file
            // (the old attribute categories) could name an id twice.
            var seen = new HashSet<long>();
            foreach (var (id, name) in names)
            {
                if (name is null || !seen.Add(id)) continue;
                foreach (var (lang, text) in name.Translations())
                    yield return new SdeName { Kind = kind, Id = id, Lang = lang, Name = text };
            }
        }
    }

    /// <summary>
    /// Stores one kind's descriptions in the client's other languages, as <see cref="SaveNamesAsync"/>
    /// does names: each language only where it differs from the English, which stays in the entity's
    /// own Description column. See <see cref="SdeText"/>.
    /// </summary>
    /// <remarks>
    /// Pulls <paramref name="texts"/> a batch at a time, so a source that parses as it is read — the
    /// types' — is never held whole.
    /// </remarks>
    private Task SaveTextsAsync(AppDbContext db, SdeTextKind kind,
        IEnumerable<(long Id, LocalizedName? Text)> texts,
        IProgress<SdeImportProgress> p, double frac, CancellationToken ct)
    {
        return SaveBatchesAsync(db, db.SdeTexts, Rows(), SettingsText.ImportStageDescriptions, -1, p, frac, frac, ct,
            emptyIsNormal: true);

        IEnumerable<SdeText> Rows()
        {
            var seen = new HashSet<long>();
            foreach (var (id, text) in texts)
            {
                if (text is null || !seen.Add(id)) continue;
                foreach (var (lang, translation) in text.Translations())
                    yield return new SdeText { Kind = kind, Id = id, Lang = lang, Text = translation };
            }
        }
    }

    /// <summary>
    /// What this import stored in SdeNames, by language and by kind, as the log line says it.
    /// </summary>
    /// <remarks>
    /// ⚠️ Throws only when cancelled. It reports; it is not part of what makes the import succeed,
    /// and a count that failed must not roll back an import that did not.
    /// </remarks>
    private static async Task<(string Summary, string? ByKind, IReadOnlyDictionary<string, int>? ByLanguage)> CountNamesAsync(
        AppDbContext db, CancellationToken ct)
    {
        try
        {
            var byLang = await db.SdeNames.AsNoTracking()
                .GroupBy(n => n.Lang)
                .Select(g => new { Lang = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Lang, x => x.Count, ct);
            var byKind = await db.SdeNames.AsNoTracking()
                .GroupBy(n => n.Kind)
                .Select(g => new { Kind = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            // Every language named, a missing one as 0: an absent language is the finding.
            static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            var languages = string.Join(" · ", SdeNames.OtherLanguages.Select(l => $"{l} {N(byLang.GetValueOrDefault(l))}"));

            return ($"Names stored in the client's other languages: {languages} — {N(byLang.Values.Sum())} in all. "
                    + "Only a name that differs from the English is stored; a language at 0 is one this SDE did not carry.",
                    "By kind: " + string.Join(" · ", byKind.OrderBy(k => k.Kind).Select(k => $"{k.Kind} {N(k.Count)}")),
                    byLang);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (AppErrorLogger.Line("Names stored in the client's other languages could not be counted", ex), null, null);
        }
    }

    /// <summary>
    /// What this import stored in SdeTexts, by language and by kind, as its log line says it — the
    /// descriptions' counterpart of <see cref="CountNamesAsync"/>, and like it, it reports and never
    /// fails the import.
    /// </summary>
    private static async Task<(string Summary, string? ByKind)> CountTextsAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            var byLang = await db.SdeTexts.AsNoTracking()
                .GroupBy(t => t.Lang)
                .Select(g => new { Lang = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Lang, x => x.Count, ct);
            var byKind = await db.SdeTexts.AsNoTracking()
                .GroupBy(t => t.Kind)
                .Select(g => new { Kind = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            var languages = string.Join(" · ", SdeNames.OtherLanguages.Select(l => $"{l} {N(byLang.GetValueOrDefault(l))}"));

            return ($"Descriptions stored in the client's other languages: {languages} — {N(byLang.Values.Sum())} in all. "
                    + "Only a description that differs from the English is stored, and an item's only if it is published.",
                    "By kind: " + string.Join(" · ", byKind.OrderBy(k => k.Kind).Select(k => $"{k.Kind} {N(k.Count)}")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (AppErrorLogger.Line("Descriptions stored in the client's other languages could not be counted", ex), null);
        }
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

    /// <summary>
    /// An entry read as it is decompressed, for a pass that must not hold its whole file — the types'
    /// descriptions, where the file with every language in it runs past a hundred megabytes, and
    /// <see cref="OpenEntry"/> would hold all of it for as long as the pass takes. Every read is
    /// filled, as the buffered copy's are, so the parser sees the same stream it always has.
    /// </summary>
    private static StreamReader OpenEntryStreaming(ZipArchiveEntry e) =>
        new(new FilledReads(e.Open()), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);

    /// <summary>
    /// Returns as many bytes as each read asks for, and fewer only at the end — as a MemoryStream
    /// does. A stream straight off the archive hands back whatever it has decompressed so far, which
    /// a reader is entitled to take for the end of what is available.
    /// </summary>
    private sealed class FilledReads(Stream inner) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var filled = 0;
            while (filled < buffer.Length)
            {
                var n = inner.Read(buffer[filled..]);
                if (n == 0) break;
                filled += n;
            }
            return filled;
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static void Report(IProgress<SdeImportProgress> p, string stage, string detail, double frac)
        => p.Report(new SdeImportProgress(stage, detail, frac));

    // Progress details that name a file: "Parsing mapRegions.yaml…".
    private static string Parsing(string file)         => string.Format(SettingsText.ImportParsingFile, file);
    private static string ParsingLarge(string file)    => string.Format(SettingsText.ImportParsingFileLarge, file);
    private static string NotFoundSkipped(string file) => string.Format(SettingsText.SdeFileNotFoundSkipped, file);

    // -----------------------------------------------------------------------
    // YAML DTOs — property names match SDE YAML keys exactly (case-sensitive)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Localised TEXT — a description, a tooltip — of which only the English is kept. The SDE writes
    /// every language (de, en, es, fr, ja, ko, ru, zh); the other seven are read past, not held, so
    /// fifty thousand type descriptions are not in memory eight times over. The descriptions that
    /// ARE kept in every language are read as <see cref="LocalizedName"/>: the small files' in
    /// their own stage, the types' in a pass of their own (ImportTypeDescriptionsAsync).
    /// </summary>
    private sealed class LocalizedString { public string? en { get; set; } }

    /// <summary>
    /// A localised NAME, in every language the SDE writes — the English for the entity's own Name
    /// column, the other seven for SdeNames. Also a description kept in every language, for
    /// SdeTexts, and a celestial's name of its own, for the station names.
    /// </summary>
    private sealed class LocalizedName
    {
        public string? de { get; set; }
        public string? en { get; set; }
        public string? es { get; set; }
        public string? fr { get; set; }
        public string? ja { get; set; }
        public string? ko { get; set; }
        public string? ru { get; set; }
        public string? zh { get; set; }

        /// <summary>Sets one language by the SDE's key; a key it does not know is ignored.</summary>
        public void Set(string lang, string value)
        {
            switch (lang)
            {
                case "de": de = value; break;
                case "en": en = value; break;
                case "es": es = value; break;
                case "fr": fr = value; break;
                case "ja": ja = value; break;
                case "ko": ko = value; break;
                case "ru": ru = value; break;
                case "zh": zh = value; break;
            }
        }

        /// <summary>One language by the SDE's key, English included; null for a key it does not know.</summary>
        public string? Get(string lang) => lang switch
        {
            "de" => de, "en" => en, "es" => es, "fr" => fr, "ja" => ja, "ko" => ko, "ru" => ru, "zh" => zh,
            _    => null,
        };

        /// <summary>
        /// The other languages worth a row: present, and different from the English. A name the
        /// same in every language — most ship hulls in the European ones — stores nothing.
        /// </summary>
        public IEnumerable<(string Lang, string Name)> Translations()
        {
            var english = en?.Trim() ?? "";
            foreach (var lang in SdeNames.OtherLanguages)
            {
                var name = Get(lang)?.Trim();
                if (string.IsNullOrEmpty(name) || string.Equals(name, english, StringComparison.Ordinal)) continue;
                yield return (lang, name);
            }
        }
    }

    /// <summary>
    /// Reads <see cref="LocalizedString"/> and <see cref="LocalizedName"/> from either shape the
    /// SDE has used: a mapping of language to text (the current SDE), or a plain string (older ones,
    /// read as the English).
    /// </summary>
    /// <remarks>
    /// ⚠️ Instead of YamlDotNet's own mapping of a class, which THROWS on a plain string — and one
    /// throw fails the whole file, and with it the import, which is then rolled back. A field that
    /// changed shape between SDE releases (displayName did) would cost every table, not one column.
    /// Accepting both is also what lets a DTO offer the old key and the new one side by side and
    /// take whichever the file has.
    /// </remarks>
    private sealed class LocalizedTextConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(LocalizedString) || type == typeof(LocalizedName);

        public object? ReadYaml(IParser parser, Type type)
        {
            var isName = type == typeof(LocalizedName);

            if (parser.TryConsume<Scalar>(out var scalar))
            {
                if (IsNull(scalar)) return null;
                return isName ? new LocalizedName { en = scalar.Value } : new LocalizedString { en = scalar.Value };
            }

            // Neither a string nor a mapping: not text at all. Stepped over rather than failing the file.
            if (!parser.TryConsume<MappingStart>(out _))
            {
                parser.SkipThisAndNestedEvents();
                return null;
            }

            var name = isName ? new LocalizedName() : null;
            var text = isName ? null : new LocalizedString();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (!parser.TryConsume<Scalar>(out var key))
                {
                    parser.SkipThisAndNestedEvents();   // the key
                    parser.SkipThisAndNestedEvents();   // its value
                    continue;
                }
                if (!parser.TryConsume<Scalar>(out var value))
                {
                    parser.SkipThisAndNestedEvents();
                    continue;
                }
                if (IsNull(value)) continue;

                if (name is not null) name.Set(key.Value, value.Value);
                else if (key.Value == "en") text!.en = value.Value;
            }
            return (object?)name ?? text;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type) =>
            throw new NotSupportedException("The SDE import only reads YAML.");

        /// <summary>YAML's null: <c>~</c>, <c>null</c> or nothing at all, unquoted.</summary>
        private static bool IsNull(Scalar s) =>
            s.Style == ScalarStyle.Plain && s.Value is "" or "~" or "null" or "Null" or "NULL";
    }

    private class CategoryYaml { public LocalizedName? name { get; set; } public bool published { get; set; } public int? iconID { get; set; } }
    private class GroupYaml
    {
        public int              categoryID  { get; set; }
        public LocalizedName?   name        { get; set; }
        public bool             published   { get; set; }
        public bool             anchorable  { get; set; }
        public bool             anchored    { get; set; }
        public int?             iconID      { get; set; }
        public bool             fittableNonSingleton { get; set; }
        public bool             useBasePrice { get; set; }
    }

    private class MarketGroupYaml
    {
        public LocalizedName?   name          { get; set; }
        public LocalizedName?   nameID        { get; set; }
        // Every language, for SdeTexts: a small file. The types' are read in a pass of their own.
        public LocalizedName?   description   { get; set; }
        public LocalizedName?   descriptionID { get; set; }
        public int?             parentGroupID { get; set; }
        public bool             hasTypes      { get; set; }
        public int?             iconID        { get; set; }
    }

    private class TypeYaml
    {
        public int              groupID       { get; set; }
        public LocalizedName?   name          { get; set; }
        public LocalizedName?   nameID        { get; set; }
        public LocalizedString? description   { get; set; }
        public LocalizedString? descriptionID { get; set; }
        public double           volume        { get; set; }
        public double?          packagedVolume { get; set; }
        public int?             metaLevel      { get; set; }
        public int?             techLevel      { get; set; }
        public bool?            isRepackable   { get; set; }
        public bool?            isDynamicType  { get; set; }
        public double?          radius         { get; set; }
        public int?             variationParentTypeID { get; set; }
        public int?             soundID        { get; set; }
        public int?             shipTreeGroupID { get; set; }
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

    /// <summary>What the descriptions pass reads of a type, in every language — see
    /// ImportTypeDescriptionsAsync. Every other field of the type is stepped over.</summary>
    private class TypeDescriptionYaml
    {
        public bool             published     { get; set; }
        public LocalizedName?   description   { get; set; }
        public LocalizedName?   descriptionID { get; set; }
    }

    private class DogmaAttributeYaml
    {
        public string?          name                { get; set; }
        // New SDE: displayName is a localised mapping {de: ..., en: ..., …}
        // Old SDE: displayName was a plain scalar string — which LocalizedTextConverter reads as
        // the English, where YamlDotNet on its own would have thrown.
        public LocalizedName?   displayName         { get; set; }
        public LocalizedName?   displayNameID       { get; set; }
        // New SDE uses attributeCategoryID; old SDE used categoryID
        public int?             attributeCategoryID { get; set; }
        public int?             categoryID          { get; set; }
        public double           defaultValue        { get; set; }
        public bool             highIsGood          { get; set; }
        public bool             stackable           { get; set; }
        public int?             unitID              { get; set; }
        // A plain string so far; read through the converter all the same, so that the day CCP
        // localises it costs nothing rather than the whole file.
        public LocalizedString? description         { get; set; }
        public int?             iconID              { get; set; }
        public int?             minAttributeID      { get; set; }
        public int?             maxAttributeID      { get; set; }
        // Both spellings, and the import takes whichever is there: the current SDE writes the
        // plain keys, and only the ...ID ones were read — so every tooltip came out empty.
        public LocalizedString? tooltipTitle         { get; set; }
        public LocalizedString? tooltipTitleID       { get; set; }
        public LocalizedString? tooltipDescription   { get; set; }
        public LocalizedString? tooltipDescriptionID { get; set; }
        public int?             dataType            { get; set; }
        public bool             displayWhenZero     { get; set; }
        public int?             chargeRechargeTimeID { get; set; }
        public bool             published           { get; set; }
    }

    private class DogmaAttrCategoryYaml
    {
        public int?             categoryID { get; set; }
        // A plain string in the files seen so far, which the converter reads as the English; a
        // localised mapping reads as well, with its other languages.
        public LocalizedName?   name       { get; set; }
        public LocalizedName?   nameID     { get; set; }
    }

    private class DogmaEffectYaml
    {
        // New SDE: "name" field holds the effect name
        public string?          name          { get; set; }
        // Old SDE: "effectName" field
        public string?          effectName    { get; set; }
        // Both spellings of each, as for the attributes' tooltips: only the ...ID keys were read,
        // so every Description came out empty and every DisplayName fell back to the internal name.
        public LocalizedName?   displayName   { get; set; }
        public LocalizedName?   displayNameID { get; set; }
        public LocalizedString? description   { get; set; }
        public LocalizedString? descriptionID { get; set; }
        public bool             isOffensive   { get; set; }
        public bool             isAssistance  { get; set; }
        public bool             published     { get; set; }

        // New SDE "effectCategoryID"; old SDE "effectCategory".
        public int?             effectCategoryID { get; set; }
        public int?             effectCategory   { get; set; }
        public bool             isWarpSafe         { get; set; }
        public bool             disallowAutoRepeat { get; set; }
        public int?             durationAttributeID      { get; set; }
        public int?             dischargeAttributeID     { get; set; }
        public int?             rangeAttributeID         { get; set; }
        public int?             falloffAttributeID       { get; set; }
        public int?             trackingSpeedAttributeID { get; set; }
        public int?             resistanceAttributeID    { get; set; }
        public int?             fittingUsageChanceAttributeID { get; set; }
        public List<ModifierInfoYaml>? modifierInfo { get; set; }
    }

    private class ModifierInfoYaml
    {
        public string? func                 { get; set; }
        public string? domain               { get; set; }
        public int?    operation            { get; set; }
        public int?    modifiedAttributeID  { get; set; }
        public int?    modifyingAttributeID { get; set; }
        public int?    groupID              { get; set; }
        public int?    skillTypeID          { get; set; }
        public int?    effectID             { get; set; }
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

    /// <summary>
    /// A planet, moon or asteroid belt of the flat SDE, and the name it has of its own where it has
    /// one — "Amarr VIII (Oris)" rather than "Amarr VIII" — in every language. A few hundred do;
    /// every other is named from its system and indices.
    /// </summary>
    /// <remarks>
    /// The field arrived in the SDE's schema on 2025-09-22 as <c>name</c>, and the belts' has shipped
    /// as <c>uniqueName</c>, so both are read on all three. As a localised mapping or a plain string,
    /// like every other name — see LocalizedTextConverter.
    /// </remarks>
    private abstract class NamedCelestialYaml
    {
        public LocalizedName?  name       { get; set; }
        public LocalizedName?  uniqueName { get; set; }

        // A method rather than a property, so that the deserializer never sees it as a field.
        public LocalizedName?  CustomName() => name ?? uniqueName;
    }

    // Flat universe: asteroid belts alongside mapPlanets/mapMoons, same shape as a moon.
    private class MapAsteroidBeltYaml : NamedCelestialYaml
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
    private class MapPlanetYaml : NamedCelestialYaml
    {
        public int           celestialIndex { get; set; }
        public int           solarSystemID  { get; set; }
        public int           typeID         { get; set; }
        public PositionYaml? position       { get; set; }
    }
    private class MapMoonYaml : NamedCelestialYaml
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
        public LocalizedString? description     { get; set; }
        public int?             nebulaID        { get; set; }
        public int?             wormholeClassID { get; set; }
        public LocalizedName?   name      { get; set; }
        public int?             factionID { get; set; }
        public PositionYaml?    position  { get; set; }
    }
    private class MapConstellationYaml
    {
        public int?             wormholeClassID { get; set; }
        public LocalizedName?   name      { get; set; }
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
        public int?             wormholeClassID { get; set; }
        public bool             border          { get; set; }
        public bool             corridor        { get; set; }
        public bool             fringe          { get; set; }
        public bool             hub             { get; set; }
        public bool             international   { get; set; }
        public bool             regional        { get; set; }
        public double           luminosity      { get; set; }
        public string?          visualEffect    { get; set; }
        public int?             starID          { get; set; }
        public LocalizedName?   name           { get; set; }
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

    /// <summary>
    /// Station services, and which operation provides which.
    ///
    /// Services hang off the operation, not the station: a station names an operationID, and
    /// the operation lists its service ids. That indirection is the whole reason this import
    /// exists — without it "does this station have a market / an LP store / a factory" can
    /// only be guessed at from the owning corporation, which is an approximation.
    /// </summary>
    private async Task ImportStationServicesAsync(ZipArchive zip, string fsdRoot, AppDbContext db,
                                                  IProgress<SdeImportProgress> p, CancellationToken ct)
    {
        var svcEntry = zip.GetEntry($"{fsdRoot}stationServices.yaml");
        if (svcEntry is not null)
        {
            Report(p, SettingsText.ImportStageStationServices, Parsing("stationServices.yaml"), 0.89);
            using var reader = OpenEntry(svcEntry);
            var raw = _yaml.Deserialize<Dictionary<int, StationServiceYaml>>(reader) ?? [];
            db.SdeStationServices.AddRange(raw.Select(kv => new SdeStationService
            {
                ServiceId = kv.Key,
                Name      = kv.Value.serviceName?.en ?? $"Service {kv.Key}",
            }));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            Report(p, SettingsText.ImportStageStationServices, string.Format(SettingsText.SdeServicesCount, raw.Count), 0.892);
            await SaveNamesAsync(db, SdeNameKind.StationService, raw.Select(kv => ((long)kv.Key, kv.Value.serviceName)), p, 0.892, ct);
        }
        else Report(p, SettingsText.ImportStageStationServices, NotFoundSkipped("stationServices.yaml"), 0.892);

        var opEntry = zip.GetEntry($"{fsdRoot}stationOperations.yaml");
        if (opEntry is null)
        {
            Report(p, SettingsText.ImportStageStationOperations, NotFoundSkipped("stationOperations.yaml"), 0.895);
            return;
        }

        Report(p, SettingsText.ImportStageStationOperations, Parsing("stationOperations.yaml"), 0.893);
        using var opReader = OpenEntry(opEntry);
        var ops = _yaml.Deserialize<Dictionary<int, StationOperationYaml>>(opReader) ?? [];

        db.SdeStationOperations.AddRange(ops.Select(kv => new SdeStationOperation
        {
            OperationId = kv.Key,
            Name        = kv.Value.operationName?.en ?? $"Operation {kv.Key}",
            ActivityId  = kv.Value.activityID,
            Description = kv.Value.description?.en ?? "",
            ManufacturingFactor = kv.Value.manufacturingFactor,
            ResearchFactor      = kv.Value.researchFactor,
            Ratio               = kv.Value.ratio,
            Border   = kv.Value.border,
            Corridor = kv.Value.corridor,
            Fringe   = kv.Value.fringe,
            Hub      = kv.Value.hub,
        }));

        // Distinct guards against an operation listing the same service twice, which the
        // composite key would reject for the whole batch.
        db.SdeStationOperationServices.AddRange(ops
            .SelectMany(kv => (kv.Value.services ?? []).Distinct()
                .Select(sid => new SdeStationOperationService { OperationId = kv.Key, ServiceId = sid })));

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        Report(p, SettingsText.ImportStageStationOperations, string.Format(SettingsText.SdeOperationsCount, ops.Count), 0.895);
        await SaveNamesAsync(db, SdeNameKind.StationOperation, ops.Select(kv => ((long)kv.Key, kv.Value.operationName)), p, 0.895, ct);
    }

    private class StationServiceYaml   { public LocalizedName? serviceName   { get; set; } }
    private class StationOperationYaml
    {
        public int?             activityID          { get; set; }
        public LocalizedString? description         { get; set; }
        public double           manufacturingFactor { get; set; }
        public double           researchFactor      { get; set; }
        public double           ratio               { get; set; }
        public double           border              { get; set; }
        public double           corridor            { get; set; }
        public double           fringe              { get; set; }
        public double           hub                 { get; set; }
        public LocalizedName?   operationName { get; set; }
        public List<int>?       services      { get; set; }
    }

    // New npcStations.yaml DTO (dict format, no station name)
    private class NpcStationYaml
    {
        public int?          celestialIndex         { get; set; }
        public long?         orbitID                { get; set; }
        public int?          orbitIndex             { get; set; }
        public int?          reprocessingHangarFlag { get; set; }
        public bool          useOperationName       { get; set; }
        public PositionYaml? position               { get; set; }
        public int    solarSystemID            { get; set; }
        public int    typeID                   { get; set; }
        public int    ownerID                  { get; set; }
        public int    operationID              { get; set; }
        public double reprocessingEfficiency   { get; set; }
        public double reprocessingStationsTake { get; set; }
    }

    private class FactionYaml
    {
        public LocalizedName?   name                 { get; set; }
        public LocalizedName?   nameID               { get; set; }
        public LocalizedName?   description          { get; set; }   // every language, for SdeTexts
        public LocalizedName?   descriptionID        { get; set; }
        public int?             corporationID        { get; set; }
        public int?             militiaCorporationID { get; set; }
        public int?             solarSystemID        { get; set; }
        public int?             iconID               { get; set; }
        public LocalizedString? shortDescription     { get; set; }
        public LocalizedString? shortDescriptionID   { get; set; }
        public double?          sizeFactor           { get; set; }
        public bool             uniqueName           { get; set; }
    }

    private class IndustryModifierYaml
    {
        public int  dogmaAttributeID { get; set; }
        public int? filterID         { get; set; }
    }

    private class NpcCorpYaml
    {
        public LocalizedName?   name        { get; set; }
        public LocalizedName?   description { get; set; }   // every language, for SdeTexts
        public int?             factionID   { get; set; }
        public int?             stationID   { get; set; }
        public int?             solarSystemID { get; set; }
        public string?          tickerName  { get; set; }
        public int?             ceoID       { get; set; }
        public double?          taxRate     { get; set; }
        public string?          size        { get; set; }
        public string?          extent      { get; set; }
        public int?             memberLimit { get; set; }
        public double?          minSecurity { get; set; }
        public int?             minimumJoinStanding { get; set; }
        public int?             enemyID     { get; set; }
        public int?             friendID    { get; set; }
        public int?             raceID      { get; set; }
        public int?             iconID      { get; set; }
        public int?             mainActivityID      { get; set; }
        public int?             secondaryActivityID { get; set; }
        public bool             deleted     { get; set; }
    }
    private class RaceYaml
    {
        public LocalizedName?   name        { get; set; }
        public LocalizedName?   description { get; set; }   // every language, for SdeTexts
        public int?             iconID      { get; set; }
        public int?             shipTypeID  { get; set; }
    }
    private class MetaGroupYaml
    {
        public LocalizedName?   name        { get; set; }
        public LocalizedName?   description { get; set; }   // every language, for SdeTexts
        public int?             iconID      { get; set; }
        public string?          iconSuffix  { get; set; }
        public MetaColorYaml?   color       { get; set; }
    }

    private class MetaColorYaml
    {
        public double r { get; set; }
        public double g { get; set; }
        public double b { get; set; }
        public string Hex => $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
    }

    private class CertificateYaml
    {
        public int              groupID     { get; set; }
        // New SDE uses a localised mapping {de: ..., en: ..., …}; old SDE used a plain scalar,
        // which LocalizedTextConverter reads as the English.
        public LocalizedName?   name        { get; set; }
        public LocalizedName?   description { get; set; }   // every language, for SdeTexts
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
        public LocalizedName?                         name      { get; set; }
        public LocalizedName?                         nameID    { get; set; }
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
        public LocalizedName?   displayName { get; set; }
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
