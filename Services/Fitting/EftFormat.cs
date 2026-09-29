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
/// </summary>
public static class EftFormat
{
    public sealed record ParseResult(FitDefinition Fit, IReadOnlyList<string> Unknown);

    public static async Task<ParseResult> ParseAsync(string text, DogmaData data, CancellationToken ct = default)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith('[') || !lines[0].EndsWith(']'))
            throw new FormatException("An EFT fit starts with a line like [Ship, Fit name].");

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
            throw new FormatException($"Unknown ship: {shipName}");

        await data.LoadTypesAsync(ids.Values, ct);
        var fit = new FitDefinition { ShipTypeId = shipId, Name = fitName };

        foreach (var e in entries)
        {
            if (!ids.TryGetValue(e.Name, out var id)) { unknown.Add(e.Name); continue; }
            var type = data.Type(id);
            switch (type.CategoryId)
            {
                case DogmaData.CategoryDrone or DogmaData.CategoryFighter when e.Stack:
                    fit.Drones.Add(new FitDrone(id, e.Qty, 0));
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

    /// <summary>Active if the module can be activated, else online — what a pilot undocks with.</summary>
    public static ModuleState DefaultState(DogmaData data, DogmaTypeInfo type) =>
        type.EffectIds.Any(id => data.Effects.TryGetValue(id, out var e) && e.Category is 1 or 2)
            ? ModuleState.Active : ModuleState.Online;

    private static bool IsBooster(DogmaData data, DogmaTypeInfo type) =>
        data.AttributesByName.TryGetValue("boosterness", out var a) && type.Attributes.ContainsKey(a.Id);
}
