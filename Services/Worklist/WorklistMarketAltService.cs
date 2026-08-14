using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Who works which station.
///
/// Every generator needs this and none of them owns it, so it lives on its own: a standing buy
/// order knows its location but not its owner, inventory-level rules name a station, and the
/// order-driven generator routes builds by station. One mapping, edited once when an alt
/// changes hands.
/// </summary>
public class WorklistMarketAltService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<WorklistMarketAlt>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorklistMarketAlts.AsNoTracking()
            .OrderBy(d => d.LocationName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Location id to market alt. Generators resolve in bulk rather than per item — a worklist run
    /// touches the same handful of stations over and over.
    /// </summary>
    public async Task<Dictionary<long, WorklistMarketAlt>> GetByLocationAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.ToDictionary(d => d.LocationId);
    }

    /// <summary>Adds, or moves an existing alt to a different character. The unique index on
    /// LocationId makes "one character per station" a schema guarantee, not a convention.</summary>
    public async Task SaveAsync(WorklistMarketAlt alt, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = alt.Id > 0
            ? await db.WorklistMarketAlts.FirstOrDefaultAsync(d => d.Id == alt.Id, ct)
            : await db.WorklistMarketAlts.FirstOrDefaultAsync(d => d.LocationId == alt.LocationId, ct);

        if (existing is null)
        {
            db.WorklistMarketAlts.Add(alt);
        }
        else
        {
            existing.LocationId    = alt.LocationId;
            existing.LocationName  = alt.LocationName;
            existing.CharacterId   = alt.CharacterId;
            existing.CharacterName = alt.CharacterName;
            existing.Note          = alt.Note;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorklistMarketAlts.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return;
        db.WorklistMarketAlts.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}
