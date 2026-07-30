using System.Collections.Concurrent;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public class AppPreferencesService(IServiceScopeFactory factory)
{
    public const string StructureNameCharKey = "polling.structure_name_char_id";

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
}
