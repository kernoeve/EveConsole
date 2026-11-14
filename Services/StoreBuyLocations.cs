using System.Text.Json;
using EveConsole.Models;

namespace EveConsole.Services;

/// <summary>One place a store asks sellers to create their contracts: an NPC station or a structure.</summary>
/// <param name="Name">As picked: English for an NPC station, a structure's own name.</param>
public sealed record BuyLocation(long Id, string Name);

/// <summary>
/// The places a store asks sellers to create their Buy contracts, any number of them.
///
/// <para>⚠️ A store from before there could be several has its one place in
/// <see cref="Store.BuyLocationId"/> and an empty <see cref="Store.BuyLocations"/>, and that
/// reads as a list of that one place, so an upgrade copies nothing and loses nothing. Writing
/// keeps the first place in the old columns too, for an older app sharing the database.</para>
/// </summary>
public static class StoreBuyLocations
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static List<BuyLocation> Of(Store store)
    {
        if (!string.IsNullOrWhiteSpace(store.BuyLocations))
        {
            try
            {
                if (JsonSerializer.Deserialize<List<BuyLocation>>(store.BuyLocations, Json) is { } list)
                    return [.. list.Where(l => l.Id != 0).DistinctBy(l => l.Id)];
            }
            catch (JsonException) { }   // unreadable: fall back to the one place below
        }
        return store.BuyLocationId == 0 ? [] : [new BuyLocation(store.BuyLocationId, store.BuyLocationName)];
    }

    /// <summary>
    /// Places as one phrase in the current language — "A", "A or B", "A, B, or C" — or "" for none.
    /// Any of them will do, so it is an "or".
    ///
    /// <para>⚠️ Worded as the web site words the same list (the browser's own
    /// <c>Intl.ListFormat</c>, disjunction), so a seller reads the same places the same way in
    /// the mail and on the site: hence the English comma before "or" with three or more, and
    /// Japanese 、または.</para>
    /// </summary>
    public static string Phrase(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => string.Format(EveConsole.Localization.StoreText.PlacesOr, names[0], names[1]),
        _ => string.Format(EveConsole.Localization.StoreText.PlacesOrMany,
                           string.Join(EveConsole.Localization.StoreText.PlacesSeparator, names.Take(names.Count - 1)), names[^1]),
    };

    public static void Set(Store store, IReadOnlyList<BuyLocation> places)
    {
        var kept = places.Where(p => p.Id != 0).DistinctBy(p => p.Id).ToList();
        store.BuyLocations    = kept.Count == 0 ? "" : JsonSerializer.Serialize(kept, Json);
        store.BuyLocationId   = kept.Count == 0 ? 0  : kept[0].Id;
        store.BuyLocationName = kept.Count == 0 ? "" : kept[0].Name;
    }
}
