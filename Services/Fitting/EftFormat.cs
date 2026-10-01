using EveConsole.Localization;

namespace EveConsole.Services.Fitting;

/// <summary>
/// The EFT text format — what the client's "copy to clipboard" and fitting tools and sites
/// exchange:
/// <code>
/// [Rifter, My Rifter]
/// Damage Control II
/// 200mm AutoCannon II, EMP S
/// Warrior II x2
/// </code>
/// Blank lines separate sections by convention only; the slot is read from the item, not from
/// where it sits.
///
/// <para>⚠️ Written in English, whatever the interface language: other tools and the game read
/// it. Read in any of the game client's languages — a fit copied from a German client names its
/// items in German (<see cref="DogmaData.FindTypesByNameAsync"/>) — but the format's own words,
/// "[Empty High slot]" and "/OFFLINE", only as English writes them.</para>
/// </summary>
public static class EftFormat
{
    public sealed record ParseResult(FitDefinition Fit, IReadOnlyList<string> Unknown);

    /// <summary>The fit in <paramref name="text"/>. Throws <see cref="FormatException"/> with a
    /// sentence for the status line when the text is no EFT fit, or its hull is not known.</summary>
    public static async Task<ParseResult> ParseAsync(string text, DogmaData data, CancellationToken ct = default)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith('[') || !lines[0].EndsWith(']'))
            throw new FormatException(FittingText.EftErrFirstLine);

        var head     = lines[0][1..^1];
        var comma    = head.IndexOf(',');
        var shipName = (comma < 0 ? head : head[..comma]).Trim();
        var fitName  = comma < 0 ? "" : head[(comma + 1)..].Trim();

        var entries = new List<(string Name, string? Charge, int Qty, bool Offline, bool Stack)>();
        foreach (var raw in lines.Skip(1))
        {
            if (raw.StartsWith("[Empty", StringComparison.OrdinalIgnoreCase)) continue;
            var line    = raw;
            var offline = line.EndsWith("/OFFLINE", StringComparison.OrdinalIgnoreCase);
            if (offline) line = line[..^"/OFFLINE".Length].TrimEnd();

            // "Name xN" is a stack: drones, cargo, or an implant written with a count.
            var x = line.LastIndexOf(" x", StringComparison.Ordinal);
            if (x > 0 && int.TryParse(line[(x + 2)..], out var qty))
            {
                entries.Add((line[..x].Trim(), null, qty, offline, true));
                continue;
            }
            var c = line.IndexOf(',');
            entries.Add(c < 0 ? (line, null, 1, offline, false) : (line[..c].Trim(), line[(c + 1)..].Trim(), 1, offline, false));
        }

        var ids = await data.FindTypesByNameAsync(
            entries.Select(e => e.Name).Concat(entries.Where(e => e.Charge is not null).Select(e => e.Charge!)).Append(shipName), ct);
        var unknown = new List<string>();
        if (!ids.TryGetValue(shipName, out var shipId))
            throw new FormatException(string.Format(FittingText.EftErrUnknownShip, shipName));

        await data.LoadTypesAsync(ids.Values, ct);
        var fit = new FitDefinition { ShipTypeId = shipId, Name = fitName };

        foreach (var e in entries)
        {
            if (!ids.TryGetValue(e.Name, out var id)) { unknown.Add(e.Name); continue; }
            var type = data.Type(id);
            switch (type.CategoryId)
            {
                case DogmaData.CategoryDrone when e.Stack:
                    fit.Drones.Add(new FitDrone(id, e.Qty, 0));
                    break;
                // A fighter line is a count, which may be more than one squadron holds.
                case DogmaData.CategoryFighter when e.Stack:
                    foreach (var size in FighterAbilities.Squadrons(data, id, e.Qty))
                        fit.Drones.Add(new FitDrone(id, size, size));
                    break;
                case DogmaData.CategoryImplant:
                    if (IsBooster(data, type)) fit.Boosters.Add(id); else fit.Implants.Add(id);
                    break;
                case DogmaData.CategoryModule or DogmaData.CategorySubsystem or DogmaData.CategoryStructureModule when !e.Stack:
                    int? charge = null;
                    if (e.Charge is not null)
                    {
                        if (ids.TryGetValue(e.Charge, out var cid)) charge = cid; else unknown.Add(e.Charge);
                    }
                    fit.Modules.Add(new FitModule(id, e.Offline ? ModuleState.Offline : DefaultState(data, type), charge));
                    break;
                default:
                    fit.Cargo.Add((id, e.Qty));
                    break;
            }
        }
        return new ParseResult(fit, unknown);
    }

    /// <summary>
    /// The fit as EFT text: low, mid and high slots, rigs, subsystems, then drones, implants and
    /// boosters, then cargo — each section separated by a blank line, the order the client writes.
    /// English item names in every interface language: this is what tools and the game read.
    /// </summary>
    public static string Write(FitDefinition fit, DogmaData data)
    {
        string Name(int id) => data.TryType(id, out var t) ? t.Name : $"Type {id}";
        var sb = new System.Text.StringBuilder();
        sb.Append('[').Append(Name(fit.ShipTypeId)).Append(", ").Append(fit.Name.Length > 0 ? fit.Name : "New fit").Append("]\n");

        var bySlot = fit.Modules.GroupBy(m => data.TryType(m.TypeId, out var t) ? DogmaEngine.SlotOf(data, t) : FitSlot.None)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var slot in new[] { FitSlot.Low, FitSlot.Mid, FitSlot.High, FitSlot.Rig, FitSlot.Subsystem, FitSlot.Service })
        {
            if (!bySlot.TryGetValue(slot, out var mods)) continue;
            sb.Append('\n');
            foreach (var m in mods)
            {
                sb.Append(Name(m.TypeId));
                if (m.ChargeTypeId is { } c) sb.Append(", ").Append(Name(c));
                if (m.State == ModuleState.Offline) sb.Append(" /OFFLINE");
                sb.Append('\n');
            }
        }
        // Drones, then fighters, each a block of "name xN" — a fighter line per squadron.
        bool IsFighter(FitDrone d) => data.TryType(d.TypeId, out var t) && t.CategoryId == DogmaData.CategoryFighter;
        foreach (var block in new[] { fit.Drones.Where(d => !IsFighter(d)).ToList(), fit.Drones.Where(IsFighter).ToList() })
        {
            if (block.Count == 0) continue;
            sb.Append('\n');
            foreach (var d in block) sb.Append(Name(d.TypeId)).Append(" x").Append(d.Count).Append('\n');
        }
        if (fit.Implants.Count + fit.Boosters.Count > 0)
        {
            sb.Append('\n');
            foreach (var i in fit.Implants.Concat(fit.Boosters)) sb.Append(Name(i)).Append('\n');
        }
        if (fit.Cargo.Count > 0)
        {
            sb.Append('\n');
            foreach (var (id, qty) in fit.Cargo) sb.Append(Name(id)).Append(" x").Append(qty).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// A fitting saved in the game (ESI), as a fit. The game records what sits in each slot and
    /// bay but not which charge a module has loaded, so each module is given the first charge in
    /// the fit's cargo it can take.
    /// </summary>
    public static async Task<FitDefinition> FromEsiAsync(Models.EsiFittingData esi, DogmaData data, FittingCatalog catalog, CancellationToken ct = default)
    {
        await data.LoadTypesAsync(esi.Items.Select(i => i.TypeId).Append(esi.ShipTypeId), ct);
        var fit = new FitDefinition { ShipTypeId = esi.ShipTypeId, Name = esi.Name };
        var cargo = new List<(int TypeId, int Quantity)>();

        static int SlotIndex(string flag) => int.TryParse(new string(flag.SkipWhile(c => !char.IsDigit(c)).ToArray()), out var n) ? n : 0;
        // Racks in the game's order — high, mid, low, rigs, subsystems, services — each by slot number.
        static int Rack(string flag) => flag switch
        {
            _ when flag.StartsWith("HiSlot")        => 0,
            _ when flag.StartsWith("MedSlot")       => 1,
            _ when flag.StartsWith("LoSlot")        => 2,
            _ when flag.StartsWith("RigSlot")       => 3,
            _ when flag.StartsWith("SubSystemSlot") => 4,
            _ when flag.StartsWith("ServiceSlot")   => 5,
            _                                       => 6,
        };
        foreach (var item in esi.Items.OrderBy(i => Rack(i.Flag)).ThenBy(i => SlotIndex(i.Flag)))
        {
            if (!data.TryType(item.TypeId, out var type)) continue;
            var flag = item.Flag;
            if (flag.StartsWith("HiSlot") || flag.StartsWith("MedSlot") || flag.StartsWith("LoSlot") || flag.StartsWith("RigSlot")
                || flag.StartsWith("SubSystemSlot") || flag.StartsWith("ServiceSlot"))
                for (var n = 0; n < Math.Max(1, item.Quantity); n++)
                    fit.Modules.Add(new FitModule(item.TypeId, DefaultState(data, type)));
            else if (flag is "DroneBay")
                fit.Drones.Add(new FitDrone(item.TypeId, item.Quantity, 0));
            // The game lists the fighter bay by type and count, not by squadron.
            else if (flag is "FighterBay")
                foreach (var size in FighterAbilities.Squadrons(data, item.TypeId, item.Quantity))
                    fit.Drones.Add(new FitDrone(item.TypeId, size, size));
            else if (type.CategoryId == DogmaData.CategoryImplant)
                (IsBooster(data, type) ? fit.Boosters : fit.Implants).Add(item.TypeId);
            else
                cargo.Add((item.TypeId, item.Quantity));
        }

        for (var i = 0; i < fit.Modules.Count; i++)
        {
            var valid = (await catalog.ChargesForAsync(fit.Modules[i].TypeId, ct)).Select(c => c.TypeId).ToHashSet();
            if (valid.Count == 0) continue;
            // The charge the hold carries most of: the ammunition actually in use, rather than a
            // spare stack of something else that happens to fit.
            var loaded = cargo.Where(c => valid.Contains(c.TypeId)).OrderByDescending(c => c.Quantity).FirstOrDefault();
            if (loaded.TypeId != 0) fit.Modules[i] = fit.Modules[i] with { ChargeTypeId = loaded.TypeId };
        }
        fit.Cargo.AddRange(cargo);
        return fit;
    }

    /// <summary>
    /// Active if the module can be activated, else online — what a pilot fights with. Cloaks and
    /// cynosural field generators are the exception: running one changes the ship's numbers in
    /// ways nobody reading a fit wants (a cloak halves scan resolution, a cyno stops the ship), so
    /// they start online and are switched on by hand.
    /// </summary>
    public static ModuleState DefaultState(DogmaData data, DogmaTypeInfo type) =>
        CanActivate(data, type) && !StartsOnline.Contains(type.GroupId) && !IsOneOff(data, type) ? ModuleState.Active : ModuleState.Online;

    /// <summary>
    /// A module used once in a while rather than kept running — a micro jump drive or field
    /// generator, a jump portal generator, an Emergency Hull Energizer, a doomsday, Breach Control — whose effect on its
    /// own ship lasts only while it is used. Fitted, it starts on but not running, so the fit is
    /// shown as it flies; switched on, it shows what using it does.
    /// </summary>
    private static bool IsOneOff(DogmaData data, DogmaTypeInfo type) =>
        type.DefaultEffectId is { } id && data.Effects.TryGetValue(id, out var e)
        && (e.Name is "microJumpDrive" or "microJumpPortalDrive" or "microJumpPortalDriveCapital" or "emergencyHullEnergizer" or "jumpPortalGeneration"
                   or "moduleBonusBreacherPodDamageControl" or "debuffLance"
            || e.Name.StartsWith("doomsday", StringComparison.Ordinal) || e.Name.StartsWith("superWeapon", StringComparison.Ordinal));

    /// <summary>Cloaking Device and Cynosural Field Generator (standard, covert and industrial).</summary>
    private static readonly HashSet<int> StartsOnline = [330, 658];

    /// <summary>
    /// Whether the module is switched on and off in space: its own cycling (default) effect is an
    /// active or targeted one. ⚠️ Not "has any active effect" — the generic <c>online</c> effect
    /// every module carries is filed as active, which would make every plate and heat sink
    /// look activatable.
    /// </summary>
    public static bool CanActivate(DogmaData data, DogmaTypeInfo type) =>
        type.DefaultEffectId is { } id && data.Effects.TryGetValue(id, out var e) && e.Category is 1 or 2;

    private static bool IsBooster(DogmaData data, DogmaTypeInfo type) =>
        data.AttributesByName.TryGetValue("boosterness", out var a) && type.Attributes.ContainsKey(a.Id);
}
