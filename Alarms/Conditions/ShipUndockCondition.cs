using System.Globalization;
using System.Text;
using System.Text.Json;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// Fires when one of the capsuleer's characters undocks — anywhere, or from named places, in
/// any ship or named hulls and classes — and, if asked, only when the ship left with something
/// missing: nothing fitted, jump fuel short, ammunition short.
///
/// <para>The undock itself comes from the location poll, which stamps <c>UndockedAt</c> on a
/// docked→space transition every ten seconds while a character is online, so the alarm sees
/// it within one poll interval of its own. What was aboard comes from the asset snapshot,
/// which ESI refreshes hourly: the fit, fuel and ammunition checks describe the ship as it was
/// last listed, and every match says when that was.</para>
///
/// <para>Everything narrows: a place AND a hull AND the fit state AND the fuel state AND the
/// ammunition state, each left on "Any" to not care. Each of the three states can be asked for
/// either way round — not fit or fit, fuel low or fuel not low — so two alarms can be made to
/// cover different undocks rather than the same one twice: one for a dreadnought that leaves
/// short of fuel, another for one that leaves with enough.</para>
/// </summary>
public sealed class ShipUndockCondition : IAlarmCondition
{
    /// <summary>
    /// How far back an undock counts. Wider than the alarm's own poll so nothing falls between
    /// two checks, and bounded so an undock hours old — the app was closed, the alarm was off —
    /// is not announced against cargo it no longer has.
    /// </summary>
    private const int MinLookbackSeconds = 600;

    private const int CapsuleGroupId = 29;

    // Dogma attributes. Slot counts decide whether a hull can be unfit at all; the jump drive
    // attribute names the isotope it burns; the charge attributes decide what a turret or
    // launcher can load.
    private const int AttrLowSlots      = 12;
    private const int AttrMedSlots      = 13;
    private const int AttrHiSlots       = 14;
    private const int AttrRigSlots      = 1137;
    private const int AttrChargeSize    = 128;
    private const int AttrJumpFuelType  = 866;
    private static readonly int[] AttrChargeGroups = [604, 605, 606, 609, 610];

    private const int CategoryModule = 7;
    private const int CategoryCharge = 8;

    // The three states, each a choice. The strings are what the editor shows and what the
    // config stores; the reader is lenient about case and a few synonyms.
    private const string Any         = "Any";
    private const string NotFit      = "Not fit";
    private const string Fit         = "Fit";
    private const string LowerThan   = "Lower than";
    private const string NotLower    = "Not lower than";
    private static readonly string[] FitChoices    = [Any, NotFit, Fit];
    private static readonly string[] AmountChoices = [Any, LowerThan, NotLower];

    private const int DefaultFuelUnits = 5000;
    private const int DefaultAmmoUnits = 1000;

    public string TypeKey     => "ship_undock";
    public string DisplayName => "Ship undocks";

    public string Description =>
        "Fires when one of your characters undocks. Optionally only from named stations, " +
        "structures, systems or regions, only in named hulls or ship classes, and only in a " +
        "given state — fit or not fit, jump fuel lower than a number or not, ammunition lower " +
        "than a number or not. Every filter narrows, so two alarms can be set to cover " +
        "different undocks rather than the same one twice. The undock is seen by the location " +
        "poll within about ten seconds; what was aboard is judged from the last asset snapshot, " +
        "which ESI refreshes hourly, and each match says how old that was.";

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            locations = new
            {
                type        = "array",
                items       = new { type = "string" },
                format      = "place-name",
                title       = "Undocking from",
                description = "Optional. Station, structure, solar system or region names, any mix. " +
                              "Matches an undock from any of them; leave empty for anywhere.",
            },
            ships = new
            {
                type        = "array",
                items       = new { type = "string" },
                format      = "ship-name",
                title       = "Flying",
                description = "Optional. Hull names (\"Rifter\"), ship classes (\"Cruiser\", \"Titan\") " +
                              "or \"Pod\", any mix. Matches any of them; leave empty for any ship.",
            },
            fit = new
            {
                type        = "string",
                @enum       = FitChoices,
                @default    = Any,
                title       = "Fit",
                description = "\"Not fit\": nothing at all in any slot. \"Fit\": something is. A pod or " +
                              "shuttle has no slots and matches neither.",
            },
            fuel = new
            {
                type        = "string",
                @enum       = AmountChoices,
                @default    = Any,
                units       = "fuel_units",
                suffix      = "units",
                title       = "Jump fuel",
                description = "Jump-capable hulls only; any other hull matches neither. The units are " +
                              "of the isotope the hull's jump drive burns, fuel bay and cargo hold " +
                              "together.",
            },
            fuel_units = new
            {
                type        = "integer",
                @default    = DefaultFuelUnits,
                description = "The number \"Jump fuel\" compares against.",
            },
            ammo = new
            {
                type        = "string",
                @enum       = AmountChoices,
                @default    = Any,
                units       = "ammo_units",
                suffix      = "units",
                title       = "Ammunition",
                description = "Per fitted turret or launcher: the rounds it can load that are aboard, " +
                              "loaded plus cargo, all compatible types together. \"Lower than\": any " +
                              "weapon short. \"Not lower than\": weapons fitted and none short. An " +
                              "energy turret only needs one crystal. A ship without weapons matches " +
                              "neither.",
            },
            ammo_units = new
            {
                type        = "integer",
                @default    = DefaultAmmoUnits,
                description = "The number \"Ammunition\" compares against.",
            },
        },
    };

    /// <summary>
    /// Rewrites a config from the first shape of this check — <c>unfit</c>, <c>fuel_below</c>,
    /// <c>ammo_below</c>, each a reason to fire — into the choices. Null when there is nothing
    /// to do. Run once at startup over every alarm of this type, so an alarm made yesterday
    /// keeps meaning what it meant.
    /// </summary>
    internal static string? UpgradeConfig(string? json)
    {
        JsonElement config;
        try { config = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch { return null; }
        if (config.ValueKind != JsonValueKind.Object) return null;

        var hasOld = config.TryGetProperty("unfit", out _) || config.TryGetProperty("fuel_below", out _)
                  || config.TryGetProperty("ammo_below", out _);
        if (!hasOld) return null;

        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in config.EnumerateObject())
            if (p.Name is not ("unfit" or "fuel_below" or "ammo_below"))
                o[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());

        if (ReadBool(config, "unfit"))                  o["fit"]  = NotFit;
        if (ReadInt(config, "fuel_below") is { } fuel)  { o["fuel"] = LowerThan; o["fuel_units"] = Math.Max(1, fuel); }
        if (ReadInt(config, "ammo_below") is { } ammo)  { o["ammo"] = LowerThan; o["ammo_units"] = Math.Max(1, ammo); }
        return o.ToJsonString();
    }

    public string Describe(JsonElement config)
    {
        var places = ReadList(config, "locations");
        var ships  = ReadList(config, "ships");
        var checks = new List<string>();
        switch (ReadChoice(config, "fit", FitChoices))
        {
            case NotFit: checks.Add("not fit"); break;
            case Fit:    checks.Add("fit");     break;
        }
        switch (ReadChoice(config, "fuel", AmountChoices))
        {
            case LowerThan: checks.Add($"jump fuel under {Units(config, "fuel_units", DefaultFuelUnits):N0}");    break;
            case NotLower:  checks.Add($"jump fuel at least {Units(config, "fuel_units", DefaultFuelUnits):N0}"); break;
        }
        switch (ReadChoice(config, "ammo", AmountChoices))
        {
            case LowerThan: checks.Add($"ammo under {Units(config, "ammo_units", DefaultAmmoUnits):N0}");    break;
            case NotLower:  checks.Add($"ammo at least {Units(config, "ammo_units", DefaultAmmoUnits):N0}"); break;
        }

        var sb = new StringBuilder("Undock");
        if (places.Count > 0) sb.Append(" from ").Append(Few(places));
        if (ships.Count  > 0) sb.Append(" in ").Append(Few(ships));
        if (places.Count == 0 && ships.Count == 0) sb.Append(" in any ship, anywhere");
        if (checks.Count > 0) sb.Append(" — ").Append(string.Join(", ", checks));
        return sb.ToString();

        static string Few(List<string> items) =>
            items.Count <= 3 ? string.Join(", ", items) : $"{string.Join(", ", items.Take(3))} +{items.Count - 3}";
    }

    public (string Title, string Body) DefaultText(
        string alarmName, JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        var title = matches.Count == 1 && matches[0].Detail is { } d
            ? $"{Str(d, "character")} undocked in {Article(HullWord(d))}"
            : $"{matches.Count} undocks";
        return (title, IAlarmCondition.JoinSummaries(matches));
    }

    /// <summary>
    /// Said as written: who, where, in what — then what is missing. A pilot who has just left
    /// the undock can still dock back, and that window is short.
    /// </summary>
    public string? Announcement(JsonElement config, IReadOnlyList<AlarmMatch> matches)
        => ComposeAnnouncement(matches);

    internal static string? ComposeAnnouncement(IReadOnlyList<AlarmMatch> matches)
    {
        if (matches.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var m in matches.Take(5))
        {
            if (m.Detail is not { } d) continue;
            if (sb.Length > 0) sb.Append(' ');

            sb.Append(Str(d, "character")).Append(" undocked in ").Append(Str(d, "system"))
              .Append(" in ").Append(Article(HullWord(d)));

            var problems = Problems(d);
            sb.Append(problems.Count == 0 ? "." : " with " + string.Join(", ", problems) + ".");
        }
        if (matches.Count > 5) sb.Append($" And {matches.Count - 5} more.");
        return sb.ToString();
    }

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var lookback = TimeSpan.FromSeconds(Math.Max(MinLookbackSeconds, 2 * ctx.Alarm.PollSeconds));
        var cutoff   = ctx.Now - lookback;

        await using var db = await ctx.DbFactory.CreateDbContextAsync(ct);

        // ⚠️ The date filter is applied in memory: a DateTimeOffset in a LINQ Where does not
        // translate on SQLite. The candidate set is one row per character, so this is nothing.
        var recent = (await db.CharacterStatuses.AsNoTracking()
                .Where(s => s.UndockedAt != null && s.ShipTypeId != null)
                .ToListAsync(ct))
            .Where(s => s.UndockedAt >= cutoff)
            .ToList();
        if (recent.Count == 0) return [];

        var wantPlaces = ReadList(config, "locations").Select(Norm).ToHashSet();
        var wantShips  = ReadList(config, "ships").Select(Norm).ToHashSet();
        var fitChoice  = ReadChoice(config, "fit",  FitChoices);
        var fuelChoice = ReadChoice(config, "fuel", AmountChoices);
        var ammoChoice = ReadChoice(config, "ammo", AmountChoices);
        var fuelUnits  = Units(config, "fuel_units", DefaultFuelUnits);
        var ammoUnits  = Units(config, "ammo_units", DefaultAmmoUnits);
        var anyCheck   = fitChoice != Any || fuelChoice != Any || ammoChoice != Any;

        // Names for everything the rows point at, in a few set-based reads.
        var charIds = recent.Select(s => s.CharacterId).ToList();
        var names   = await db.Characters.AsNoTracking()
            .Where(c => charIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var typeIds = recent.Select(s => s.ShipTypeId!.Value).Distinct().ToList();
        var hulls   = await (from t in db.SdeTypes.AsNoTracking()
                             join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                             where typeIds.Contains(t.TypeId)
                             select new { t.TypeId, t.Name, g.GroupId, Group = g.Name })
                            .ToDictionaryAsync(x => x.TypeId, ct);

        var sysIds  = recent.Select(s => s.UndockedSystemId ?? s.SolarSystemId ?? 0).Distinct().ToList();
        var systems = await (from s in db.SdeSolarSystems.AsNoTracking()
                             join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId
                             where sysIds.Contains(s.SolarSystemId)
                             select new { s.SolarSystemId, s.Name, Region = r.Name })
                            .ToDictionaryAsync(x => x.SolarSystemId, ct);

        var fromIds  = recent.Select(s => s.UndockedFromId ?? 0).Where(id => id != 0).Distinct().ToList();
        var stations = await db.SdeStations.AsNoTracking()
            .Where(s => fromIds.Contains((long)s.StationId))
            .ToDictionaryAsync(s => (long)s.StationId, s => s.Name, ct);
        var structures = new Dictionary<long, string>();
        foreach (var s in await db.Structures.AsNoTracking()
                     .Where(s => fromIds.Contains(s.StructureId) && s.Name != "").Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            structures.TryAdd(s.StructureId, s.Name);
        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => fromIds.Contains(s.StructureId) && s.Name != "").Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            structures.TryAdd(s.StructureId, s.Name);
        foreach (var s in await db.EsiCorpStructures.AsNoTracking()
                     .Where(s => fromIds.Contains(s.StructureId) && s.Name != "").Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            structures.TryAdd(s.StructureId, s.Name);

        var matches = new List<AlarmMatch>();
        foreach (var s in recent.OrderByDescending(s => s.UndockedAt))
        {
            if (!hulls.TryGetValue(s.ShipTypeId!.Value, out var hull)) continue;

            var system = systems.GetValueOrDefault(s.UndockedSystemId ?? s.SolarSystemId ?? 0);
            var place  = s.UndockedFromId is { } from
                ? stations.GetValueOrDefault(from) ?? structures.GetValueOrDefault(from)
                : null;

            // ── Filters: where from, and in what ──
            if (wantPlaces.Count > 0
                && !(place is not null && wantPlaces.Contains(Norm(place)))
                && !(system is not null && (wantPlaces.Contains(Norm(system.Name)) || wantPlaces.Contains(Norm(system.Region)))))
                continue;

            var isPod = hull.GroupId == CapsuleGroupId;
            if (wantShips.Count > 0
                && !wantShips.Contains(Norm(hull.Name))
                && !wantShips.Contains(Norm(hull.Group))
                && !(isPod && wantShips.Contains("pod")))
                continue;

            // ── States: fit, fuel, ammunition — each a filter, each either way round ──
            var detail = new Dictionary<string, object?>
            {
                ["character_id"] = s.CharacterId,
                ["character"]    = names.GetValueOrDefault(s.CharacterId) ?? $"Character {s.CharacterId}",
                ["system"]       = system?.Name ?? "an unknown system",
                ["system_id"]    = s.UndockedSystemId ?? s.SolarSystemId,
                ["region"]       = system?.Region,
                ["place"]        = place,
                ["place_id"]     = s.UndockedFromId,
                ["hull"]         = hull.Name,
                ["ship_class"]   = hull.Group,
                ["is_pod"]       = isPod,
                ["ship_name"]    = s.ShipName,
                ["ship_item_id"] = s.ShipItemId,
                ["undocked_at"]  = s.UndockedAt,
            };

            if (anyCheck)
            {
                // A ship not in the snapshot cannot be judged, and a state that cannot be judged
                // is not the state asked for.
                var cargo = s.ShipItemId is { } shipItemId ? await ShipContentsAsync(db, s.CharacterId, shipItemId, ct) : null;
                detail["assets_as_of"] = cargo?.AsOf;
                if (cargo is null) continue;

                if (fitChoice != Any)
                {
                    var fittable = await HasSlotsAsync(db, hull.TypeId, ct);
                    var fitted   = cargo.Items.Any(i => IsSlot(i.Flag));
                    detail["unfit"] = fittable && !fitted;
                    if (!fittable) continue;
                    if (fitChoice == NotFit ? fitted : !fitted) continue;
                }

                if (fuelChoice != Any)
                {
                    if (await JumpFuelAsync(db, hull.TypeId, cargo.Items, ct) is not { } fuel) continue;
                    detail["fuel_type"]   = fuel.Name;
                    detail["fuel_units"]  = fuel.Units;
                    detail["fuel_wanted"] = fuelUnits;
                    detail["fuel_short"]  = fuel.Units < fuelUnits;
                    if (fuelChoice == LowerThan ? fuel.Units >= fuelUnits : fuel.Units < fuelUnits) continue;
                }

                if (ammoChoice != Any)
                {
                    var (weapons, shortWeapons) = await ShortWeaponsAsync(db, cargo.Items, ammoUnits, ct);
                    if (weapons == 0) continue;
                    detail["weapons"]     = weapons;
                    detail["ammo_wanted"] = ammoUnits;
                    if (shortWeapons.Count > 0)
                        detail["ammo_short"] = shortWeapons.Select(w => new Dictionary<string, object?>
                        {
                            ["weapon"] = w.Weapon, ["units"] = w.Units, ["needs"] = w.Needs,
                        }).ToList();
                    if (ammoChoice == LowerThan ? shortWeapons.Count == 0 : shortWeapons.Count > 0) continue;
                }
            }

            var when = s.UndockedAt!.Value.ToUniversalTime();
            matches.Add(new AlarmMatch(
                $"undock:{s.CharacterId}|{when:yyyy-MM-ddTHH:mm:ss}",
                Summary(detail))
            {
                Detail = detail,
            });
        }
        return matches;
    }

    // ── What the ship holds ────────────────────────────────────────────────────

    private sealed record ShipItem(int TypeId, string Flag, int Quantity);
    private sealed record ShipContents(List<ShipItem> Items, DateTimeOffset? AsOf);

    /// <summary>
    /// Everything the asset snapshot lists inside the ship, or null when the ship is not in the
    /// snapshot at all — a hull bought or assembled since the last poll — in which case nothing
    /// can be said about it and the checks stand down rather than call it empty.
    /// </summary>
    private static async Task<ShipContents?> ShipContentsAsync(
        AppDbContext db, long characterId, long shipItemId, CancellationToken ct)
    {
        if (!await db.EsiAssets.AsNoTracking().AnyAsync(a => a.ItemId == shipItemId, ct)) return null;

        var items = await db.EsiAssets.AsNoTracking()
            .Where(a => a.LocationId == shipItemId)
            .Select(a => new ShipItem(a.TypeId, a.LocationFlag, a.Quantity))
            .ToListAsync(ct);

        var asOf = await db.EsiCallRecords.AsNoTracking()
            .Where(r => r.OwnerId == characterId && r.OwnerType == "character" && r.Endpoint == "char.assets")
            .Select(r => (DateTimeOffset?)r.LastCalledAt)
            .FirstOrDefaultAsync(ct);

        return new ShipContents(items, asOf);
    }

    private static bool IsSlot(string flag) =>
        flag.StartsWith("HiSlot",        StringComparison.Ordinal)
     || flag.StartsWith("MedSlot",       StringComparison.Ordinal)
     || flag.StartsWith("LoSlot",        StringComparison.Ordinal)
     || flag.StartsWith("RigSlot",       StringComparison.Ordinal)
     || flag.StartsWith("SubSystemSlot", StringComparison.Ordinal)
     || flag.StartsWith("ServiceSlot",   StringComparison.Ordinal);

    /// <summary>A hull with no slots of any kind — a pod, a shuttle — cannot be unfit.</summary>
    private static async Task<bool> HasSlotsAsync(AppDbContext db, int typeId, CancellationToken ct)
    {
        int[] slotAttrs = [AttrLowSlots, AttrMedSlots, AttrHiSlots, AttrRigSlots];
        var total = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.TypeId == typeId && slotAttrs.Contains(a.AttributeId))
            .SumAsync(a => a.Value, ct);
        return total > 0;
    }

    /// <summary>
    /// The isotope this hull's jump drive burns and how much of it is aboard, or null for a hull
    /// with no jump drive. Fuel bay and cargo hold both count: the number the pilot sees.
    /// </summary>
    private static async Task<(string Name, int Units)?> JumpFuelAsync(
        AppDbContext db, int hullTypeId, List<ShipItem> items, CancellationToken ct)
    {
        var fuelTypeId = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.TypeId == hullTypeId && a.AttributeId == AttrJumpFuelType)
            .Select(a => (double?)a.Value)
            .FirstOrDefaultAsync(ct);
        if (fuelTypeId is null or 0) return null;

        var id   = (int)fuelTypeId.Value;
        var name = await db.SdeTypes.AsNoTracking().Where(t => t.TypeId == id).Select(t => t.Name).FirstOrDefaultAsync(ct)
                   ?? "jump fuel";
        return (name, items.Where(i => i.TypeId == id).Sum(i => i.Quantity));
    }

    private sealed record ShortWeapon(string Weapon, int Units, int Needs);

    /// <summary>
    /// Every fitted turret or launcher with fewer rounds aboard than asked for — loaded plus
    /// cargo, every type it can load added together. Compatibility is the game's own rule: the
    /// charge's group is one the module names, and its size matches when the module has one
    /// (turrets do; launchers tell sizes apart by group). An energy turret needs one crystal
    /// whatever the number, because crystals are not spent the way rounds are.
    /// </summary>
    private static async Task<(int Weapons, List<ShortWeapon> Short)> ShortWeaponsAsync(
        AppDbContext db, List<ShipItem> items, int needs, CancellationToken ct)
    {
        var aboard = items.Select(i => i.TypeId).Distinct().ToList();
        if (aboard.Count == 0) return (0, []);

        var kinds = await (from t in db.SdeTypes.AsNoTracking()
                           join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                           where aboard.Contains(t.TypeId)
                           select new { t.TypeId, t.Name, t.GroupId, Group = g.Name, g.CategoryId })
                          .ToDictionaryAsync(x => x.TypeId, ct);

        var weapons = items
            .Where(i => i.Flag.StartsWith("HiSlot", StringComparison.Ordinal))
            .Select(i => kinds.GetValueOrDefault(i.TypeId))
            .Where(k => k is not null && k.CategoryId == CategoryModule && IsWeaponGroup(k.Group))
            .Select(k => k!)
            .DistinctBy(k => k.TypeId)
            .ToList();
        if (weapons.Count == 0) return (0, []);

        var attrIds  = AttrChargeGroups.Append(AttrChargeSize).ToArray();
        var typeIds  = weapons.Select(w => w.TypeId).Concat(aboard).Distinct().ToList();
        var dogma    = (await db.SdeTypeDogmaAttributes.AsNoTracking()
                .Where(a => typeIds.Contains(a.TypeId) && attrIds.Contains(a.AttributeId))
                .ToListAsync(ct))
            .ToLookup(a => a.TypeId);

        var result = new List<ShortWeapon>();
        foreach (var w in weapons)
        {
            var groups = dogma[w.TypeId].Where(a => AttrChargeGroups.Contains(a.AttributeId))
                                        .Select(a => (int)a.Value).Where(g => g > 0).ToHashSet();
            if (groups.Count == 0) continue;
            var size = dogma[w.TypeId].FirstOrDefault(a => a.AttributeId == AttrChargeSize)?.Value;

            var units = 0;
            foreach (var i in items)
            {
                var k = kinds.GetValueOrDefault(i.TypeId);
                if (k is null || k.CategoryId != CategoryCharge || !groups.Contains(k.GroupId)) continue;
                if (size is { } want && dogma[i.TypeId].FirstOrDefault(a => a.AttributeId == AttrChargeSize)?.Value is { } have && have != want) continue;
                units += i.Quantity;
            }

            var need = w.Group == "Energy Weapon" ? 1 : needs;
            if (units < need) result.Add(new ShortWeapon(w.Name, units, need));
        }
        return (weapons.Count, result.OrderBy(r => r.Units).ToList());
    }

    private static bool IsWeaponGroup(string group) =>
        group is "Energy Weapon" or "Hybrid Weapon" or "Projectile Weapon"
              or "Precursor Weapon" or "Vorton Projector" or "Breacher Pod Launchers"
     || group.StartsWith("Missile Launcher", StringComparison.Ordinal);

    // ── Words ──────────────────────────────────────────────────────────────────

    private static string Summary(IReadOnlyDictionary<string, object?> d)
    {
        var sb = new StringBuilder();
        sb.Append(Str(d, "character")).Append(" undocked");
        if (Str(d, "place") is { Length: > 0 } place) sb.Append(" from ").Append(place);
        sb.Append(" in ").Append(Str(d, "system")).Append(", flying ").Append(Article(HullWord(d)));

        var problems = Problems(d);
        if (problems.Count > 0) sb.Append(" — ").Append(string.Join("; ", problems));
        if (d.TryGetValue("assets_as_of", out var at) && at is DateTimeOffset asOf)
            sb.Append($" (cargo as of {asOf.ToUniversalTime():HH:mm} EVE)");
        return sb.ToString();
    }

    /// <summary>What was missing, in the order it matters: no fit, then fuel, then ammunition.</summary>
    private static List<string> Problems(IReadOnlyDictionary<string, object?> d)
    {
        var list = new List<string>();
        if (d.TryGetValue("unfit", out var u) && u is true) list.Add("nothing fitted");

        // The fuel is worth hearing whichever way the filter was asked: an alarm for "left with
        // enough" is an alarm for the number.
        if (d.TryGetValue("fuel_units", out var fu) && fu is int units)
        {
            var fuel  = Str(d, "fuel_type");
            var short_ = d.TryGetValue("fuel_short", out var fs) && fs is true;
            var wanted = d.TryGetValue("fuel_wanted", out var fw) && fw is int w ? w : 0;
            list.Add(units == 0 ? $"no {fuel}"
                   : short_    ? $"{units:N0} {fuel}, under {wanted:N0}"
                   :             $"{units:N0} {fuel} aboard");
        }

        if (d.TryGetValue("ammo_short", out var a) && a is IEnumerable<Dictionary<string, object?>> weapons)
        {
            var all = weapons.ToList();
            foreach (var w in all.Take(2))
            {
                var name  = Str(w, "weapon");
                var have  = w.TryGetValue("units", out var h) && h is int n ? n : 0;
                var needs = w.TryGetValue("needs", out var x) && x is int y ? y : 0;
                list.Add(needs == 1 ? $"{name} without a crystal"
                       : have == 0 ? $"{name} without ammunition"
                       :             $"{name} down to {have:N0} rounds");
            }
            if (all.Count > 2) list.Add($"{all.Count - 2} more weapons short");
        }
        return list;
    }

    private static string HullWord(IReadOnlyDictionary<string, object?> d)
        => d.TryGetValue("is_pod", out var p) && p is true ? "pod" : Str(d, "hull");

    private static string Article(string noun)
        => noun.Length == 0 ? "a ship"
         : ("aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an " : "a ") + noun;

    private static string Norm(string s) => s.Trim().ToLowerInvariant();

    private static string Str(IReadOnlyDictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is string s ? s : "";

    // ── Config ─────────────────────────────────────────────────────────────────

    /// <summary>A list property, as the editor writes it (an array) or as a hand might (a comma list).</summary>
    private static List<string> ReadList(JsonElement config, string name)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(name, out var p)) return [];
        IEnumerable<string> list = p.ValueKind switch
        {
            JsonValueKind.Array  => p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? ""),
            JsonValueKind.String => (p.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
            _                    => [],
        };
        return list.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    private static int? ReadInt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) return v;
        if (p.ValueKind == JsonValueKind.String
            && int.TryParse(p.GetString()?.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return s;
        return null;
    }

    private static bool ReadBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.True;

    /// <summary>
    /// A choice as the editor stores it, matched leniently: case, spaces and a few synonyms an
    /// agent might send ("unfit", "below", "under", "not below") all land on the choice meant.
    /// Anything else is "Any".
    /// </summary>
    private static string ReadChoice(JsonElement config, string name, string[] choices)
    {
        var raw = ReadStr(config, name)?.Trim().ToLowerInvariant().Replace('_', ' ') ?? "";
        if (raw.Length == 0) return Any;
        foreach (var c in choices)
            if (string.Equals(c, raw, StringComparison.OrdinalIgnoreCase)) return c;
        return raw switch
        {
            "unfit" or "not fitted" or "empty" or "no"                       => choices == FitChoices ? NotFit : Any,
            "fitted" or "yes"                                                => choices == FitChoices ? Fit : Any,
            "below" or "under" or "less than" or "low" or "lower" or "short" => choices == AmountChoices ? LowerThan : Any,
            "not below" or "not under" or "at least" or "not low" or "enough" or "not lower"
                                                                             => choices == AmountChoices ? NotLower : Any,
            _                                                                => Any,
        };
    }

    private static int Units(JsonElement config, string name, int fallback)
        => ReadInt(config, name) is { } n && n > 0 ? n : fallback;

    private static string? ReadStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
