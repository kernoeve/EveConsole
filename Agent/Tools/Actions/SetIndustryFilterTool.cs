using System.Text.Json;

namespace EveConsole.Agent.Tools.Actions;

public sealed class SetIndustryFilterTool : IAgentTool
{
    private readonly Action<string?, string?, string?, string?>? _callback;

    public SetIndustryFilterTool(Action<string?, string?, string?, string?>? callback)
        => _callback = callback;

    public string Name        => "set_industry_filter";
    public string Description => "Applies filters to the Industry tab so the capsuleer can see the matching jobs. " +
                                 "Call with no arguments to clear all filters. " +
                                 "activity values: All Activities, Manufacturing, TE Research, ME Research, Copying, Invention, Reverse Eng., Reactions. " +
                                 "status values: All Statuses, active, paused, ready, delivered, cancelled, reverted.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            activity = new { type = "string", description = "Activity type filter. E.g. 'Manufacturing', 'Invention'. Omit for all." },
            status   = new { type = "string", description = "Job status filter. E.g. 'active', 'ready'. Omit for all." },
            search   = new { type = "string", description = "Text search for product or blueprint name. Omit to clear." },
            owner    = new { type = "string", description = "Filter by owner character or corporation name. Omit for all owners." },
        },
    };

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var activity = input.TryGetProperty("activity", out var a) ? a.GetString() : null;
        var status   = input.TryGetProperty("status",   out var s) ? s.GetString() : null;
        var search   = input.TryGetProperty("search",   out var q) ? q.GetString() : null;
        var owner    = input.TryGetProperty("owner",    out var o) ? o.GetString() : null;

        _callback?.Invoke(activity, status, search, owner);

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(activity)) parts.Add($"activity '{activity}'");
        if (!string.IsNullOrEmpty(status))   parts.Add($"status '{status}'");
        if (!string.IsNullOrEmpty(search))   parts.Add($"search '{search}'");
        if (!string.IsNullOrEmpty(owner))    parts.Add($"owner '{owner}'");

        var description = parts.Count > 0
            ? $"Industry filter applied: {string.Join(", ", parts)}."
            : "Industry filters cleared.";

        return Task.FromResult(description);
    }
}
