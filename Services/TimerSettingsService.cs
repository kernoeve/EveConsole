using System.Collections.Concurrent;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public class TimerSettingsService
{
    private readonly IServiceScopeFactory            _factory;
    private readonly ConcurrentDictionary<string,int> _cache = new();

    public TimerSettingsService(IServiceScopeFactory factory) => _factory = factory;

    public async Task LoadAsync()
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.ApiTimerSettings.AsNoTracking().ToListAsync();
        foreach (var r in rows)
            _cache[r.Key] = r.IntervalSeconds;
    }

    public int GetInterval(string key, int defaultSeconds)
        => _cache.TryGetValue(key, out var v) ? v : defaultSeconds;

    /// <summary>
    /// Undoes what the Timers page did to second-scale endpoints while it could only speak in
    /// minutes: a ten-second poll showed as "1 min", and saving the page wrote sixty seconds —
    /// the location and ship polls on this database were found running at a minute. A stored
    /// sixty on an endpoint whose default is under a minute can only have come from that, since
    /// the page offered nothing else, so it is dropped and the default is back.
    /// </summary>
    public async Task ForgetMinuteRoundingAsync(IEnumerable<EndpointInfo> endpoints)
    {
        var suspect = endpoints
            .Where(e => e.DefaultSeconds < 60 && _cache.TryGetValue(e.Key, out var v) && v == 60)
            .Select(e => e.Key)
            .ToList();
        if (suspect.Count == 0) return;

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var key in suspect)
        {
            _cache.TryRemove(key, out _);
            if (await db.ApiTimerSettings.FindAsync(key) is { } row) db.ApiTimerSettings.Remove(row);
        }
        await db.SaveChangesAsync();
    }

    public async Task SetIntervalAsync(string key, int intervalSeconds)
    {
        _cache[key] = intervalSeconds;
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.ApiTimerSettings.FindAsync(key);
        if (existing is null)
            db.ApiTimerSettings.Add(new ApiTimerSetting { Key = key, IntervalSeconds = intervalSeconds });
        else
            existing.IntervalSeconds = intervalSeconds;
        await db.SaveChangesAsync();
    }
}
