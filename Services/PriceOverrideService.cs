using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

// CRUD for user-supplied price overrides plus the type search used to add rows. Overrides are read
// by BuildCostService and ProductionCalculatorService when they recompute, so callers should trigger
// a build-cost recalculation after editing to make changes take effect everywhere.
public class PriceOverrideService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<PriceOverride>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PriceOverrides.AsNoTracking()
            .OrderBy(o => o.TypeName)
            .ToListAsync(ct);
    }

    // Loaded once per recalculation by the cost calculators.
    public async Task<Dictionary<int, PriceOverride>> LoadMapAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PriceOverrides.AsNoTracking().ToDictionaryAsync(o => o.TypeId, ct);
    }

    public async Task UpsertAsync(PriceOverride row, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        var existing = await db.PriceOverrides.FirstOrDefaultAsync(o => o.TypeId == row.TypeId, ct);
        if (existing is null)
            db.PriceOverrides.Add(row);
        else
        {
            existing.TypeName      = row.TypeName;
            existing.BuildCost     = row.BuildCost;
            existing.MarketValue   = row.MarketValue;
            existing.ContractValue = row.ContractValue;
            existing.UpdatedAt     = row.UpdatedAt;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int typeId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PriceOverrides.Where(o => o.TypeId == typeId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<InvTypeResult>> SearchTypesAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SdeTypes.AsNoTracking()
            .Where(t => EF.Functions.Like(t.Name, $"%{text}%") && t.Published)
            .OrderBy(t => t.Name)
            .Take(40)
            .Select(t => new InvTypeResult(t.TypeId, t.Name))
            .ToListAsync(ct);
    }
}
