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
