using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Api;
using EveConsole.Models;

namespace EveConsole.Services;

/// <summary>Where a fit is kept: a character's fittings in the game, or EVE Console's own saved fits.
/// (ESI reads and writes only a character's fittings; a corporation's are the game client's alone.)</summary>
public enum FitSource { Personal, App }

/// <param name="SavedFitId">For a fit saved in EVE Console: its row.</param>
public record FitEntry(EsiFittingData Data, FitSource Source, string OwnerName, long? SavedFitId = null)
{
    /// <summary>The owner shown for a fit saved in EVE Console: the app's name, not translated.</summary>
    public const string AppOwner = "EVE Console";
}

public class FittingsService(EsiClient esi)
{
    /// <summary>Every fitting of every character that has granted the fittings read scope.</summary>
    public async Task<List<FitEntry>> FetchAllFitsAsync(
        ObservableCollection<Character>   characters,
        CancellationToken                 ct = default)
    {
        var results = new List<FitEntry>();

        // Personal fittings — one ESI call per authenticated character
        foreach (var ch in characters)
        {
            if (!ch.HasScope("esi-fittings.read_fittings.v1")) continue;

            var r = await esi.ExecuteAuthAsync<List<EsiFittingData>>(
                ch.Id, $"characters/{ch.Id}/fittings/", ct);

            if (r.IsSuccess && r.Data != null)
                foreach (var f in r.Data)
                    results.Add(new FitEntry(f, FitSource.Personal, ch.Name));
        }

        // ⚠️ There are no corporation fittings to add: ESI has never had an endpoint for them
        // (esi/esi-issues#234, open since 2017). An earlier version called a guessed
        // corporations/{id}/fittings/ path, which failed silently on every open.
        return results;
    }
}
