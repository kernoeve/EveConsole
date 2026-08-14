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
public class WorklistDeskService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<WorklistDesk>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorklistDesks.AsNoTracking()
            .OrderBy(d => d.LocationName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Location id to desk. Generators resolve in bulk rather than per item — a worklist run
    /// touches the same handful of stations over and over.
    /// </summary>
    public async Task<Dictionary<long, WorklistDesk>> GetByLocationAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.ToDictionary(d => d.LocationId);
    }

    /// <summary>Adds, or moves an existing desk to a different character. The unique index on
    /// LocationId makes "one character per station" a schema guarantee, not a convention.</summary>
    public async Task SaveAsync(WorklistDesk desk, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = desk.Id > 0
            ? await db.WorklistDesks.FirstOrDefaultAsync(d => d.Id == desk.Id, ct)
            : await db.WorklistDesks.FirstOrDefaultAsync(d => d.LocationId == desk.LocationId, ct);

        if (existing is null)
        {
            db.WorklistDesks.Add(desk);
        }
        else
        {
            existing.LocationId    = desk.LocationId;
            existing.LocationName  = desk.LocationName;
            existing.CharacterId   = desk.CharacterId;
            existing.CharacterName = desk.CharacterName;
            existing.Note          = desk.Note;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorklistDesks.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return;
        db.WorklistDesks.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}
