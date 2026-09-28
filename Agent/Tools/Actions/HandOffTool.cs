using System.Text.Json;

namespace EveConsole.Agent.Tools.Actions;

/// <summary>
/// The conversation model's way to pass a message to the model that can read the capsuleer's
/// data, when it finds it needs to after all.
///
/// <para>The BACKUP, not the plan. The app decides before each turn which model answers — a
/// one-word question the small model gets right far more reliably than it decides, mid-answer,
/// to call a tool; calling tools is exactly where a 7B model failed, inventing figures rather than
/// querying. This catches what the router let through.</para>
///
/// <para>⚠️ Offered only to the conversation model, and only while the data questions go to a
/// model of their own. Calling it ends the conversation model's turn on the spot: the panel stops
/// its stream and gives the same message to the data model, so whatever it might have written
/// next — a guess — is never shown.</para>
/// </summary>
public sealed class HandOffTool(Action<string> handOff) : IAgentTool
{
    public const string ToolName = "hand_off_to_analyst";

    public string Name => ToolName;

    public string Description =>
        "Passes the capsuleer's message to the part of you that can read their database and ESI. " +
        "Call it at once, writing nothing first, whenever answering needs the capsuleer's own data — " +
        "what they have, own, are doing or have done: assets, ships, wallet, industry jobs, market " +
        "orders, contracts, skills, standings, kills — or anything to be looked up, counted, " +
        "totalled or listed. You cannot see that data yourself: never guess at it, and never invent " +
        "a figure, a name or a result. The answer will be given from there.";

    public object InputSchema => new
    {
        type       = "object",
        properties = new
        {
            reason = new { type = "string", description = "In a few words, what data the answer needs." },
        },
        required = new[] { "reason" },
    };

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var reason = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("reason", out var r)
                     && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
        handOff(reason);
        return Task.FromResult("Handed over. Write nothing more; the answer is given from there.");
    }
}
