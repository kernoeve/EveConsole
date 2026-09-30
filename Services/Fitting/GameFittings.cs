using System.Text.Json.Serialization;
using EveConsole.Api;
using EveConsole.Localization;
using EveConsole.Models;

namespace EveConsole.Services.Fitting;

/// <summary>The body ESI takes to create a saved fitting.</summary>
public sealed record EsiFittingCreate(
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("description")]  string Description,
    [property: JsonPropertyName("ship_type_id")] int ShipTypeId,
    [property: JsonPropertyName("items")]        List<EsiFittingItem> Items);

public sealed record EsiFittingCreated([property: JsonPropertyName("fitting_id")] int FittingId);

/// <summary>
/// Fits written to and removed from a character's saved fittings in the game.
/// </summary>
/// <remarks>
/// <para>ESI can create a fitting and delete one, but not change one: updating a fitting is
/// deleting it and creating it again, which gives it a new id. The new fitting is created first
/// and the old one removed only once that has worked, so a failure never leaves the pilot with
/// neither.</para>
///
/// <para>Personal fittings only — ESI offers no way to write a corporation's.</para>
/// </remarks>
public static class GameFittings
{
    public const string WriteScope = "esi-fittings.write_fittings.v1";
    private const int MaxName = 50, MaxDescription = 500;

    /// <summary>
    /// The fit as ESI fitting items: each slot numbered in order, drones and fighters in their
    /// bays, and cargo as listed. The game records no loaded charges, so for each charge type
    /// loaded and not already in the cargo, one full load per module is added to it — which is
    /// where the game keeps them and where importing looks for them.
    /// </summary>
    /// <param name="skipped">The type ids a fitting has no place for — implants, boosters, a
    /// module with no slot — for the caller to name.</param>
    public static List<EsiFittingItem> ToItems(FitDefinition fit, DogmaData data, out IReadOnlyList<int> skipped)
    {
        var items = new List<EsiFittingItem>();
        var skip  = new List<int>();
        var index = new Dictionary<FitSlot, int>();
        foreach (var m in fit.Modules)
        {
            if (!data.TryType(m.TypeId, out var t)) continue;
            var slot = DogmaEngine.SlotOf(data, t);
            var prefix = slot switch
            {
                FitSlot.High => "HiSlot", FitSlot.Mid => "MedSlot", FitSlot.Low => "LoSlot", FitSlot.Rig => "RigSlot",
                FitSlot.Subsystem => "SubSystemSlot", FitSlot.Service => "ServiceSlot", _ => null,
            };
            if (prefix is null) { skip.Add(m.TypeId); continue; }
            var n = index.GetValueOrDefault(slot);
            index[slot] = n + 1;
            items.Add(new EsiFittingItem(m.TypeId, $"{prefix}{n}", 1));
        }

        // One entry per type in each bay: the game keeps fighters by type and count, and splits
        // them into squadrons itself.
        foreach (var g in fit.Drones.GroupBy(d => d.TypeId))
        {
            var bay = data.TryType(g.Key, out var t) && t.CategoryId == DogmaData.CategoryFighter ? "FighterBay" : "DroneBay";
            items.Add(new EsiFittingItem(g.Key, bay, g.Sum(d => d.Count)));
        }

        var cargo = fit.Cargo.GroupBy(c => c.TypeId).ToDictionary(g => g.Key, g => g.Sum(c => c.Quantity));
        foreach (var group in fit.Modules.Where(m => m.ChargeTypeId is not null).GroupBy(m => m.ChargeTypeId!.Value))
        {
            if (cargo.ContainsKey(group.Key)) continue;
            var volume = data.TryType(group.Key, out var ct) ? ct.Attr(DogmaData.AttrVolume) ?? 0 : 0;
            cargo[group.Key] = group.Sum(m =>
            {
                var capacity = data.TryType(m.TypeId, out var mt) ? mt.Attr(DogmaData.AttrCapacity) ?? 0 : 0;
                return volume > 0 && capacity > 0 ? Math.Max(1, (int)Math.Floor(capacity / volume + 1e-9)) : 1;
            });
        }
        foreach (var (typeId, qty) in cargo)
            items.Add(new EsiFittingItem(typeId, "Cargo", qty));

        // Implants and boosters are the pilot's, not the ship's; a saved fitting has no place for them.
        skip.AddRange(fit.Implants.Concat(fit.Boosters));
        skipped = skip;
        return items;
    }

    /// <summary>Saves <paramref name="fit"/> as a new fitting on <paramref name="characterId"/>. The new
    /// fitting's id, or the reason it could not be saved, in a sentence for the status line.</summary>
    /// <param name="description">The fitting's description in the game, written as it is.</param>
    public static async Task<(int? FittingId, string? Error)> CreateAsync(EsiClient esi, long characterId, FitDefinition fit,
        DogmaData data, string description, CancellationToken ct = default)
    {
        // English: the name of a fitting with none, written into the game.
        var name = fit.Name.Trim().Length > 0 ? fit.Name.Trim() : "Fit";
        var body = new EsiFittingCreate(name.Length > MaxName ? name[..MaxName] : name,
            description.Length > MaxDescription ? description[..MaxDescription] : description,
            fit.ShipTypeId, ToItems(fit, data, out _));
        var (status, created) = await esi.PostAuthAsync<EsiFittingCreated>(characterId, $"characters/{characterId}/fittings/", body, ct);
        return status is >= 200 and < 300 && created is not null
            ? (created.FittingId, null)
            : (null, status switch
            {
                0   => FittingText.GameErrUnreachable,
                401 or 403 => FittingText.GameErrNoScope,
                _   => string.Format(FittingText.GameErrRefused, status),
            });
    }

    /// <summary>Deletes fitting <paramref name="fittingId"/> from <paramref name="characterId"/>: null
    /// once it is gone, else the HTTP status the game refused with (0 when it could not be reached).</summary>
    public static async Task<int?> DeleteAsync(EsiClient esi, long characterId, int fittingId, CancellationToken ct = default)
    {
        var status = await esi.DeleteAuthAsync(characterId, $"characters/{characterId}/fittings/{fittingId}/", ct);
        return status is >= 200 and < 300 or 404 ? null : status;
    }
}
