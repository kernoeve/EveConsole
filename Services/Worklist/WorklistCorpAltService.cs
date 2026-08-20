using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>Who maintains each corporation's standing projects.</summary>
public class WorklistCorpAltService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<WorklistCorpAlt>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorklistCorpAlts.AsNoTracking()
            .OrderBy(a => a.CorporationName)
            .ToListAsync(ct);
    }

    public async Task<Dictionary<long, WorklistCorpAlt>> GetByCorpAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.ToDictionary(a => a.CorporationId);
    }

    /// <summary>Adds, or moves an existing corporation to a different character.</summary>
    public async Task SaveAsync(WorklistCorpAlt alt, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = alt.Id > 0
            ? await db.WorklistCorpAlts.FirstOrDefaultAsync(a => a.Id == alt.Id, ct)
            : await db.WorklistCorpAlts.FirstOrDefaultAsync(a => a.CorporationId == alt.CorporationId, ct);

        if (existing is null)
        {
            db.WorklistCorpAlts.Add(alt);
        }
        else
        {
            existing.CorporationId   = alt.CorporationId;
            existing.CorporationName = alt.CorporationName;
            existing.CharacterId     = alt.CharacterId;
            existing.CharacterName   = alt.CharacterName;
            existing.Note            = alt.Note;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorklistCorpAlts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (row is null) return;
        db.WorklistCorpAlts.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}
