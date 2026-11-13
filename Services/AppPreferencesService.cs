using System.Collections.Concurrent;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public class AppPreferencesService(IServiceScopeFactory factory)
{
    public const string StructureNameCharKey = "polling.structure_name_char_id";

    /// <summary>Unix seconds of the last public-structure sweep. The list moves slowly and
    /// resolving what it turns up is the expensive part, so it is walked daily, not per cycle.</summary>
    public const string PublicStructureSweepKey = "polling.public_structures_swept_at";

    /// <summary>Unix seconds of the last EVE Ref structure snapshot import. Published hourly but
    /// read daily: a structure's name and system change on the scale of months, and the file is
    /// ~855 KB every time.</summary>
    public const string EveRefStructureFetchKey = "polling.everef_structures_fetched_at";

    private readonly ConcurrentDictionary<string, string> _cache = new();

    public Task LoadAsync()
    {
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var pref in db.AppPreferences)
            _cache[pref.Key] = pref.Value;
        return Task.CompletedTask;
    }

    public string? Get(string key)
        => _cache.TryGetValue(key, out var v) ? v : null;

    public long GetLong(string key, long defaultValue = 0)
        => _cache.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : defaultValue;

    public bool GetBool(string key, bool defaultValue = false)
        => _cache.TryGetValue(key, out var v) ? v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                              : defaultValue;

    public async Task SetAsync(string key, string? value)
    {
        if (value is null)
        {
            _cache.TryRemove(key, out _);
        }
        else
        {
            _cache[key] = value;
        }

        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (value is null)
        {
            var existing = await db.AppPreferences.FindAsync(key);
            if (existing is not null) db.AppPreferences.Remove(existing);
        }
        else
        {
            var existing = await db.AppPreferences.FindAsync(key);
            if (existing is null)
                db.AppPreferences.Add(new AppPreference { Key = key, Value = value });
            else
                existing.Value = value;
        }

        await db.SaveChangesAsync();
    }

    public Task SetLongAsync(string key, long? value)
        => SetAsync(key, value.HasValue ? value.Value.ToString() : null);

    public Task SetBoolAsync(string key, bool value)
        => SetAsync(key, value ? "1" : "0");

    /// <summary>
    /// The value as the database holds it now, not as this process loaded it. The cache is refreshed
    /// with what was read.
    ///
    /// <para>⚠️ For the few keys more than one PC writes, where a stale copy does harm: the intel
    /// watermark above all. The cache is loaded once at startup; a PC that becomes the worker an hour
    /// later would otherwise start from where the watermark stood when it opened, and parse the hour
    /// between again.</para>
    /// </summary>
    public async Task<string?> ReadFreshAsync(string key, CancellationToken ct = default)
    {
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var value = await db.AppPreferences.AsNoTracking()
            .Where(p => p.Key == key).Select(p => p.Value).FirstOrDefaultAsync(ct);
        if (value is null) _cache.TryRemove(key, out _);
        else _cache[key] = value;
        return value;
    }

    /// <summary>The long value as the database holds it now; see <see cref="ReadFreshAsync"/>.</summary>
    public async Task<long> ReadFreshLongAsync(string key, long defaultValue = 0, CancellationToken ct = default)
        => long.TryParse(await ReadFreshAsync(key, ct), out var n) ? n : defaultValue;

    /// <summary>
    /// Raises a numeric preference to <paramref name="value"/>, and never lowers it: a value already
    /// higher in the database stays. One statement, so two PCs racing each other cannot move it back.
    /// Returns the value the database holds afterwards.
    ///
    /// <para>⚠️ For a position that only ever moves forward (the intel watermark). A deliberate reset
    /// to a lower value, as a re-parse does, is a plain <see cref="SetLongAsync"/>.</para>
    /// </summary>
    public async Task<long> SetLongIfGreaterAsync(string key, long value, CancellationToken ct = default)
    {
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The same statement on both engines (SQLite 3.24+ and PostgreSQL 9.5+). The value is TEXT, so
        // the comparison is made on the numbers, not on their spelling: "999" < "1000" as text.
        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "AppPreferences" ("Key", "Value") VALUES ({0}, {1})
            ON CONFLICT ("Key") DO UPDATE SET "Value" = excluded."Value"
            WHERE CAST("AppPreferences"."Value" AS BIGINT) < CAST(excluded."Value" AS BIGINT)
            """, [key, text], ct);

        return await ReadFreshLongAsync(key, value, ct);
    }
}
