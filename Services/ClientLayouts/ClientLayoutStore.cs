using System.Text.Json;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.ClientLayouts;

/// <summary>Reads and writes layouts, desktops, their monitors and who runs where.</summary>
public sealed class ClientLayoutStore(IDbContextFactory<AppDbContext> dbFactory)
{
    // ---- Layouts ----

    /// <summary>All layouts by name. Sorted here: SQLite and PostgreSQL order text differently.</summary>
    public async Task<List<ClientLayout>> ListLayoutsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ClientLayouts.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id)];
    }

    /// <summary>A new layout with no id, otherwise that one. One deleted meanwhile from another desktop is kept anew.</summary>
    public async Task<int> SaveLayoutAsync(int? id, string name, LayoutArea root, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = id is { } existing
            ? await db.ClientLayouts.FirstOrDefaultAsync(l => l.Id == existing, ct).ConfigureAwait(false)
            : null;
        if (row is null) db.ClientLayouts.Add(row = new ClientLayout());
        row.Name = name;
        row.Definition = ClientLayoutJson.Write(root);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>How many monitors, on any desktop, use this layout.</summary>
    public async Task<int> MonitorsUsingAsync(int layoutId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ClientLayoutMonitors.CountAsync(m => m.LayoutId == layoutId, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a layout that no monitor uses. False, and nothing done, if one does.</summary>
    public async Task<bool> DeleteLayoutAsync(int layoutId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.ClientLayoutMonitors.AnyAsync(m => m.LayoutId == layoutId, ct).ConfigureAwait(false)) return false;
        await db.ClientLayouts.Where(l => l.Id == layoutId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ---- Devices ----

    /// <summary>This desktop's row, made on first use with the machine name as its name.</summary>
    public async Task<ClientLayoutDevice> ThisDeviceAsync(CancellationToken ct = default)
    {
        var id = DeviceIdentity.Id;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ClientLayoutDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        if (row != null) return row;
        row = new ClientLayoutDevice { Id = id, Name = Environment.MachineName };
        db.ClientLayoutDevices.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    public async Task<List<ClientLayoutDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ClientLayoutDevices.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id)];
    }

    public async Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ClientLayoutDevices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Name, name), ct).ConfigureAwait(false);
    }

    public async Task SetPlacementOnAsync(string deviceId, bool on, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ClientLayoutDevices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PlacementOn, on), ct).ConfigureAwait(false);
    }

    public async Task SaveSeenCharactersAsync(string deviceId, IEnumerable<string> names, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(names.Order(StringComparer.OrdinalIgnoreCase).ToList());
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ClientLayoutDevices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.SeenCharacters, json), ct).ConfigureAwait(false);
    }

    public static List<string> ReadSeenCharacters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// This desktop becomes an existing device, as after a Windows reinstall, and the row it had
    /// been using goes, along with its monitors and assignments.
    /// </summary>
    public async Task TakeOverDeviceAsync(string oldDeviceId, CancellationToken ct = default)
    {
        var current = DeviceIdentity.Id;
        if (current == oldDeviceId) return;
        await DeleteDeviceRowsAsync(current, ct).ConfigureAwait(false);
        DeviceIdentity.TakeOver(oldDeviceId);
    }

    /// <summary>Forgets a desktop, with its monitors and who ran where. Layouts are shared, so they stay.</summary>
    public async Task DeleteDeviceRowsAsync(string deviceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var monitorIds = await db.ClientLayoutMonitors.Where(m => m.DeviceId == deviceId)
            .Select(m => m.Id).ToListAsync(ct).ConfigureAwait(false);
        await db.ClientLayoutCharacters.Where(c => monitorIds.Contains(c.MonitorId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ClientLayoutMonitors.Where(m => m.DeviceId == deviceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ClientLayoutDevices.Where(d => d.Id == deviceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    // ---- Monitors ----

    public async Task<List<ClientLayoutMonitor>> ListMonitorsAsync(string deviceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ClientLayoutMonitors.AsNoTracking().Where(m => m.DeviceId == deviceId).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Saves a monitor's settings, making its row the first time. Returns the row's id.</summary>
    public async Task<int> SaveMonitorAsync(string deviceId, string hardwareKey, int? layoutId, bool fullMonitor, PanelStart panelStart, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ClientLayoutMonitors
            .FirstOrDefaultAsync(m => m.DeviceId == deviceId && m.HardwareKey == hardwareKey, ct).ConfigureAwait(false);
        if (row is null) db.ClientLayoutMonitors.Add(row = new ClientLayoutMonitor { DeviceId = deviceId, HardwareKey = hardwareKey });
        row.LayoutId = layoutId;
        row.FullMonitor = fullMonitor;
        row.PanelStart = panelStart.ToString();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    // ---- Who runs where ----

    public async Task<List<ClientLayoutCharacter>> ListCharactersAsync(string deviceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var monitorIds = await db.ClientLayoutMonitors.Where(m => m.DeviceId == deviceId)
            .Select(m => m.Id).ToListAsync(ct).ConfigureAwait(false);
        return await db.ClientLayoutCharacters.AsNoTracking().Where(c => monitorIds.Contains(c.MonitorId))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts a character in a client position's group. A character has one position per desktop, so
    /// it leaves any other group it was in here. Matching ignores case, as window titles are matched.
    /// </summary>
    public async Task AssignCharacterAsync(string deviceId, int monitorId, string areaId, string name, CancellationToken ct = default)
    {
        await UnassignCharacterAsync(deviceId, name, ct).ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.ClientLayoutCharacters.Add(new ClientLayoutCharacter { MonitorId = monitorId, AreaId = areaId, CharacterName = name });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UnassignCharacterAsync(string deviceId, string name, CancellationToken ct = default)
    {
        var rows = await ListCharactersAsync(deviceId, ct).ConfigureAwait(false);
        var ids = rows.Where(r => string.Equals(r.CharacterName, name, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ClientLayoutCharacters.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    // ---- Applying ----

    /// <summary>What this desktop would be using if Apply were pressed now.</summary>
    public async Task<AppliedDesktop> BuildAppliedAsync(string deviceId, CancellationToken ct = default)
    {
        var monitors = await ListMonitorsAsync(deviceId, ct).ConfigureAwait(false);
        var characters = await ListCharactersAsync(deviceId, ct).ConfigureAwait(false);
        var layouts = (await ListLayoutsAsync(ct).ConfigureAwait(false)).ToDictionary(l => l.Id);
        return Combine(monitors, characters, layouts);
    }

    /// <summary>
    /// The one place the saved rows become what a desktop runs on. In a fixed order, because
    /// "are there changes to apply?" is answered by comparing the written text. A character
    /// assigned to a position its layout no longer has is left out, so it counts as unassigned.
    /// </summary>
    public static AppliedDesktop Combine(
        IEnumerable<ClientLayoutMonitor> monitors,
        IEnumerable<ClientLayoutCharacter> characters,
        IReadOnlyDictionary<int, ClientLayout> layouts)
    {
        var byMonitor = characters.ToLookup(c => c.MonitorId);
        var applied = new AppliedDesktop();
        foreach (var monitor in monitors.OrderBy(m => m.HardwareKey, StringComparer.Ordinal))
        {
            if (monitor.LayoutId is not { } layoutId || !layouts.TryGetValue(layoutId, out var layout)) continue;
            var areas = ClientLayoutCalculator.ClientAreaIds(ClientLayoutJson.Read(layout.Definition)).ToHashSet();
            var groups = new Dictionary<string, List<string>>();
            foreach (var group in byMonitor[monitor.Id].Where(c => areas.Contains(c.AreaId))
                         .GroupBy(c => c.AreaId).OrderBy(g => g.Key, StringComparer.Ordinal))
                groups[group.Key] = [.. group.Select(c => c.CharacterName).Order(StringComparer.OrdinalIgnoreCase)];

            applied.Monitors.Add(new AppliedMonitor
            {
                HardwareKey = monitor.HardwareKey,
                Definition = layout.Definition,
                FullMonitor = monitor.FullMonitor,
                PanelStart = Enum.TryParse<PanelStart>(monitor.PanelStart, out var start) ? start : PanelStart.WithConsole,
                Characters = groups,
            });
        }
        return applied;
    }

    /// <summary>Makes what would be applied the desktop's current state.</summary>
    public async Task<AppliedDesktop> ApplyAsync(string deviceId, CancellationToken ct = default)
    {
        var applied = await BuildAppliedAsync(deviceId, ct).ConfigureAwait(false);
        var json = applied.Write();
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ClientLayoutDevices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Applied, json), ct).ConfigureAwait(false);
        return applied;
    }
}
