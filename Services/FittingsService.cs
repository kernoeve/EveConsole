using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

public enum FitSource { Personal, Corp }

public record FitEntry(EsiFittingData Data, FitSource Source, string OwnerName);

public class FittingsService(EsiClient esi, IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<FitEntry>> FetchAllFitsAsync(
        ObservableCollection<Character>   characters,
        ObservableCollection<Corporation> corporations,
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

        // Corporation fittings — requires the character to have Director role in EVE.
        // Uses character auth (not a separate corp token) against the corp endpoint.
        // Deduplication prevents duplicates when multiple chars are directors of the same corp.
        var addedCorpFits = new HashSet<(int CorpId, int FitId)>();

        foreach (var ch in characters)
        {
            if (!ch.HasScope("esi-fittings.read_fittings.v1")) continue;

            var corpId = ch.CorporationId;
            var r = await esi.ExecuteAuthAsync<List<EsiFittingData>>(
                ch.Id, $"corporations/{corpId}/fittings/", ct);

            if (!r.IsSuccess || r.Data == null) continue;

            // Find corp name from DB if not already known
            await using var db = dbFactory.CreateDbContext();
            var corpName = await db.Corporations
                .Where(c => c.Id == corpId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct) ?? $"Corp {corpId}";

            foreach (var f in r.Data)
            {
                if (addedCorpFits.Add((corpId, f.FittingId)))
                    results.Add(new FitEntry(f, FitSource.Corp, corpName));
            }
        }

        return results;
    }
}
