using System.Globalization;

namespace EveConsole.Models;

// ─────────────────────────────────────────────────────────────────────────────
//  Connectors the user set up: each one a separate program answering EVE Console's questions
//  about a corporation's or alliance's services (see ConnectorService). Several may be set up,
//  each used only for the corporations and alliances the user chose for it ("scopes"), and only
//  for the functions the user ticked.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One connector the user connected, or is setting up.
/// ⚠️ <see cref="Key"/> is a secret: the table is hidden from the AI agent (AgentSchema, ReadOnlySql).
/// </summary>
public class ConnectorEntry
{
    public int    Id              { get; set; }

    /// <summary>The parsed base (ConnectorService.ParseUrl), written only with a key; "" until the first connect.</summary>
    public string Url             { get; set; } = "";

    /// <summary>What the connector calls itself, from <c>/v1/info</c>.</summary>
    public string Name            { get; set; } = "";

    /// <summary>The key the connector issued; "" = not connected.</summary>
    public string Key             { get; set; } = "";

    /// <summary>The signed-in character, 0 when none.</summary>
    public long   CharacterId     { get; set; }
    public string CharacterName   { get; set; } = "";

    /// <summary>Comma list from <c>/v1/me</c>, as sent (trimmed, only a-z, 0-9 and '-').</summary>
    public string Capabilities    { get; set; } = "";

    /// <summary>The raw JSON array of <c>/v1/me</c>'s "offers", "" when absent or too long. Kept for later features.</summary>
    public string Offers          { get; set; } = "";

    /// <summary>Comma list of corporation ids the user chose this connector for.</summary>
    public string Corporations    { get; set; } = "";

    /// <summary>Comma list of alliance ids the user chose this connector for.</summary>
    public string Alliances       { get; set; } = "";

    /// <summary>Comma list of the functions (capability names this build knows) the user chose.</summary>
    public string Functions       { get; set; } = "";

    /// <summary>False until the defaults were applied after a connect, or the user saved functions while connected to a
    /// connector offering something this build knows.</summary>
    public bool   FunctionsChosen { get; set; }
}

/// <summary>The comma lists <see cref="ConnectorEntry"/> keeps, read and written. Invalid tokens are ignored.</summary>
public static class ConnectorLists
{
    /// <summary>The positive ids in a comma list, each once, in order.</summary>
    public static IReadOnlyList<int> Ids(string? list)
    {
        var ids = new List<int>();
        foreach (var token in (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && !ids.Contains(id))
                ids.Add(id);
        return ids;
    }

    /// <summary>The names in a comma list, trimmed, each once, in order.</summary>
    public static IReadOnlyList<string> Names(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    public static string Join(IEnumerable<int> ids) =>
        string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    public static string Join(IEnumerable<string> names) => string.Join(",", names);
}
