using System.Text.Json;
using EveConsole.Services;

namespace EveConsole.Agent.Tools.Actions;

/// <summary>
/// Switches what the universe map colours its systems by — security, sovereignty, ADM,
/// industry activity and so on.
///
/// The list of overlays is not hard-coded here. It lives on the map view model, which is
/// where new ones get added, and a copy in this file would silently go stale the first time
/// one was; the callback resolves the name and reports back what it actually selected.
/// </summary>
public sealed class SetMapOverlayTool : IAgentTool
{
    public string Name => "set_map_overlay";

    public string Description =>
        "Changes the universe map's overlay — what its systems are coloured by. Use when the " +
        "capsuleer asks to 'colour by', 'show sovereignty on the map', 'switch the map to ADM', " +
        "'show manufacturing activity' and the like. Names available today include Security, " +
        "Constellation, Sovereignty, Sovereignty ADM, and Industry — manufacturing / reactions / " +
        "ME research / TE research. Partial name match; call with an unknown name to be told what " +
        "is available.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            overlay = new
            {
                type = "string",
                description = "Overlay name, e.g. 'Sovereignty' or 'Industry — manufacturing'. " +
                              "Partial match accepted.",
            },
        },
        required = new[] { "overlay" },
    };

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var overlay = input.TryGetProperty("overlay", out var o) ? o.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(overlay)) return Task.FromResult("No overlay provided.");

        var set = EntityNavigator.Instance.SetOverlay;
        if (set is null) return Task.FromResult("The map is not available right now.");

        var result = set(overlay);
        return Task.FromResult(string.IsNullOrEmpty(result)
            ? $"No overlay matching '{overlay}'."
            : result);
    }
}
