using System.Text.Json;
using EveConsole.Services;

namespace EveConsole.Agent.Tools.Actions;

/// <summary>
/// Opens a pilot, corporation, alliance, agent, NPC corporation or faction in the entity
/// viewers.
///
/// Resolution goes through the same search the tools themselves use, so the agent finds
/// exactly what a capsuleer typing the name would find — including, for player entities,
/// names that are not in the local cache yet, which that search fetches from ESI.
/// </summary>
public sealed class NavigateToEntityTool : IAgentTool
{
    private readonly EntityBrowserService              _entities;
    private readonly Action<EntityKind, long, string>? _callback;

    public string Name => "navigate_to_entity";

    public string Description =>
        "Opens a pilot, corporation, alliance, NPC agent, NPC corporation or faction in the " +
        "Player Entities or NPC Entities viewer. Use when the capsuleer asks to 'pull up', " +
        "'show', 'look up' or 'who is' a character, corp, alliance, agent or faction. " +
        "Shows portrait, affiliation, kills and losses, corporation history and more. " +
        "Partial name match — prefer the most specific name you know.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            entity_type = new
            {
                type = "string",
                @enum = new[] { "pilot", "corporation", "alliance", "agent", "npc_corporation", "faction" },
                description = "Which kind of entity. Use 'corporation' for player corps and " +
                              "'npc_corporation' for NPC ones such as Lai Dai or Paragon.",
            },
            name = new { type = "string", description = "Name to look up (partial match accepted)." },
        },
        required = new[] { "entity_type", "name" },
    };

    public NavigateToEntityTool(EntityBrowserService entities, Action<EntityKind, long, string>? callback)
    {
        _entities = entities;
        _callback = callback;
    }

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var typeText = input.TryGetProperty("entity_type", out var t) ? t.GetString() ?? "" : "";
        var name     = input.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(name)) return "No name provided.";

        var kind = typeText.ToLowerInvariant() switch
        {
            "pilot" or "character"          => EntityKind.Pilot,
            "corporation" or "corp"         => EntityKind.PlayerCorp,
            "alliance"                      => EntityKind.Alliance,
            "agent"                         => EntityKind.Agent,
            "npc_corporation" or "npc_corp" => EntityKind.NpcCorp,
            "faction"                       => EntityKind.Faction,
            _                               => (EntityKind?)null,
        };
        if (kind is null) return $"Unknown entity_type '{typeText}'.";

        var matches = await _entities.SearchWithEsiAsync(kind.Value, name, ct);
        if (matches.Count == 0)
            return $"No {typeText} matching '{name}' was found.";

        // The search already ranks exact and prefix matches first, so the head of the list
        // is the best answer; the rest are reported so the agent can offer them.
        var best = matches[0];
        _callback?.Invoke(kind.Value, best.Id, best.Name);

        if (matches.Count == 1)
            return $"Opening the {typeText} viewer for '{best.Name}'.";

        var others = string.Join(", ", matches.Skip(1).Take(4).Select(m => m.Name));
        return $"Opening the {typeText} viewer for '{best.Name}'. Other matches: {others}"
             + (matches.Count > 5 ? $", and {matches.Count - 5:N0} more." : ".");
    }
}
