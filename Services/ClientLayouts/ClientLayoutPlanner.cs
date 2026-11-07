using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveConsole.Services.ClientLayouts;

/// <summary>When a monitor's panels appear.</summary>
public enum PanelStart { WithConsole, WithEve }

/// <summary>One monitor's part of what was last applied on a desktop.</summary>
public sealed class AppliedMonitor
{
    public string HardwareKey { get; set; } = "";
    public string Definition { get; set; } = "";
    public bool FullMonitor { get; set; }
    public PanelStart PanelStart { get; set; }

    /// <summary>Client area id to the characters in its group.</summary>
    public Dictionary<string, List<string>> Characters { get; set; } = [];
}

/// <summary>
/// What a desktop last applied, kept as a whole. Edits change the layouts and assignments, but
/// nothing on screen follows them until Apply copies them here, so a new client can't land in a
/// shape the others aren't using yet.
/// </summary>
public sealed class AppliedDesktop
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public List<AppliedMonitor> Monitors { get; set; } = [];

    public string Write() => JsonSerializer.Serialize(this, Options);

    public static AppliedDesktop Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppliedDesktop();
        try
        {
            return JsonSerializer.Deserialize<AppliedDesktop>(json, Options) ?? new AppliedDesktop();
        }
        catch (JsonException)
        {
            return new AppliedDesktop();
        }
    }
}

/// <summary>A monitor's panels and when they show.</summary>
public sealed record MonitorPanels(string Key, LayoutRect Bounds, PanelStart Start, IReadOnlyList<PlacedPanel> Panels);

public sealed record DesktopPlan(PlacementPlan Placement, IReadOnlyList<MonitorPanels> Panels);

/// <summary>Works out where everything goes on the monitors that are plugged in right now.</summary>
public static class ClientLayoutPlanner
{
    /// <summary>
    /// A monitor that isn't plugged in has no cells or panels until it's back, and a character
    /// assigned there simply has no position for now.
    /// </summary>
    public static DesktopPlan Build(AppliedDesktop applied, IReadOnlyList<DesktopMonitor> monitors)
    {
        var placement = new List<PlacementMonitor>();
        var panels = new List<MonitorPanels>();
        var positions = new Dictionary<string, (string MonitorKey, string AreaId)>(StringComparer.OrdinalIgnoreCase);

        foreach (var saved in applied.Monitors)
        {
            var monitor = monitors.FirstOrDefault(m => m.Key == saved.HardwareKey);
            if (monitor == null) continue;

            var space = saved.FullMonitor ? monitor.Bounds : monitor.Work;
            var result = ClientLayoutCalculator.Calculate(ClientLayoutJson.Read(saved.Definition), space);
            placement.Add(new PlacementMonitor(monitor.Key, monitor.Bounds, result.Clients));
            panels.Add(new MonitorPanels(monitor.Key, monitor.Bounds, saved.PanelStart, result.Panels));

            foreach (var cell in result.Clients)
            {
                if (!saved.Characters.TryGetValue(cell.AreaId, out var names)) continue;
                foreach (var name in names) positions.TryAdd(name, (monitor.Key, cell.AreaId));
            }
        }

        return new DesktopPlan(new PlacementPlan { Monitors = placement, Positions = positions }, panels);
    }

    /// <summary>"With EVE" panels show only while a client is on their monitor, hiding at once when the last goes.</summary>
    public static bool PanelsShow(PanelStart start, int clientsOnMonitor) =>
        start == PanelStart.WithConsole || clientsOnMonitor > 0;
}
