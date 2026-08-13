using System.Text.Json;
using EveConsole.Services;

namespace EveConsole.Agent.Tools.Actions;

/// <summary>
/// Opens the universe map on a system or a region.
///
/// One tool rather than two, because a system and a region are resolved by the same search —
/// the capsuleer says "show me Delve" or "show me 1DQ1-A" without labelling which is which,
/// and asking the model to pick the right tool first would just move that guess earlier. The
/// <c>view</c> argument only matters when it disagrees with what the name resolved to: naming
/// a system with <c>view: "region"</c> zooms out to the region containing it.
/// </summary>
public sealed class OpenMapTool : IAgentTool
{
    private readonly UniverseMapService _map;

    public string Name => "open_map";

    public string Description =>
        "Opens the universe map on a solar system or a region. Use when the capsuleer asks to " +
        "'show me', 'pull up the map for', 'where is' or 'zoom to' a place. Naming a system opens " +
        "the system view (its planets, stations, gates and recent activity); naming a region opens " +
        "the region map. Pass view='region' with a system name to zoom out to that system's region " +
        "instead.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            place = new { type = "string", description = "System or region name (partial match accepted)." },
            view  = new
            {
                type = "string",
                @enum = new[] { "auto", "system", "region" },
                description = "'auto' (the default) uses whatever the name resolved to. " +
                              "'region' forces the region map even when a system was named.",
            },
        },
        required = new[] { "place" },
    };

    public OpenMapTool(UniverseMapService map) => _map = map;

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var place = input.TryGetProperty("place", out var p) ? p.GetString() ?? "" : "";
        var view  = input.TryGetProperty("view",  out var v) ? (v.GetString() ?? "auto") : "auto";

        if (string.IsNullOrWhiteSpace(place)) return "No place provided.";

        var matches = await _map.SearchPlacesAsync(place, 10, ct);
        if (matches.Count == 0) return $"No system or region matching '{place}' was found.";

        var best = matches[0];
        var nav  = EntityNavigator.Instance;

        // A region match has no system id, so "system" is only honoured when there is one.
        var wantRegion = view.Equals("region", StringComparison.OrdinalIgnoreCase) || best.SystemId == 0;

        string opened;
        if (wantRegion)
        {
            if (best.RegionId <= 0) return $"'{best.Name}' has no region to open.";
            nav.Region(best.RegionId);
            opened = best.SystemId == 0
                ? $"Opening the region map for '{best.Name}'."
                : $"Opening the region map containing '{best.Name}'.";
        }
        else
        {
            nav.System(best.SystemId);
            opened = $"Opening the system view for '{best.Name}'.";
        }

        if (matches.Count == 1) return opened;

        var others = string.Join(", ", matches.Skip(1).Take(4).Select(m => m.Name));
        return $"{opened} Other matches: {others}"
             + (matches.Count > 5 ? $", and {matches.Count - 5:N0} more." : ".");
    }
}
