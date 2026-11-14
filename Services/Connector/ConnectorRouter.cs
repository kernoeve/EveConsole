using EveConsole.Localization;
using EveConsole.Models;

namespace EveConsole.Services.Connector;

/// <summary>A connector entry without its key: what the screen, the router and later features read.</summary>
public sealed record ConnectorSnapshot(
    int                   Id,
    Uri?                  Url,
    string                ConnectorName,
    bool                  Connected,
    long                  CharacterId,
    string                CharacterName,
    IReadOnlyList<string> Capabilities,
    string                OffersJson,
    IReadOnlyList<int>    Corporations,
    IReadOnlyList<int>    Alliances,
    IReadOnlyList<string> Functions,
    bool                  FunctionsChosen)
{
    /// <summary>Whether any corporation or alliance is chosen. A connector with none is asked nothing but
    /// <c>/v1/info</c>, <c>/v1/me</c> and the sign-in.</summary>
    public bool HasScope => Corporations.Count > 0 || Alliances.Count > 0;

    /// <summary>Whether the connector says it offers <paramref name="function"/>.</summary>
    public bool Offers(string function) => Capabilities.Contains(function);

    /// <summary>Whether the user chose this connector for <paramref name="function"/>.</summary>
    public bool Chose(string function) => Functions.Contains(function);

    /// <summary>
    /// ⚠️ The only test for asking this connector anything about a subject: it is never asked about characters
    /// or corporations outside its scopes.
    /// </summary>
    public bool Covers(int corporationId, int? allianceId) =>
        Corporations.Contains(corporationId) || (allianceId is { } a && Alliances.Contains(a));

    /// <summary>Whether this connector may be asked for <paramref name="function"/> at all now.</summary>
    public bool CanAsk(string function) => Connected && HasScope && Chose(function) && Offers(function);

    /// <summary>The connector's name, or its address while it has none.</summary>
    public string Title => ConnectorName is { Length: > 0 } n ? n : Url?.Authority ?? SettingsText.ConnectorNewEntry;

    /// <summary>The entry as the screen and the router read it: everything but the key.</summary>
    public static ConnectorSnapshot From(ConnectorEntry e) => new(
        e.Id,
        ConnectorService.ParseUrl(e.Url).Url,
        e.Name,
        e.Key.Length > 0,
        e.CharacterId,
        e.CharacterName,
        ConnectorLists.Names(e.Capabilities),
        e.Offers,
        ConnectorLists.Ids(e.Corporations),
        ConnectorLists.Ids(e.Alliances),
        ConnectorLists.Names(e.Functions).Where(ConnectorFunctions.IsKnown).ToList(),
        e.FunctionsChosen);
}

/// <summary>A choice another connector already holds: <see cref="Function"/> for the corporation, or
/// alliance, <see cref="EntityId"/>.</summary>
public sealed record ConnectorConflict(string Function, bool IsAlliance, int EntityId, int OtherId, string OtherTitle);

/// <summary>
/// Which connector to ask for what. ⚠️ Every feature that reads from a connector, or reads what one gave,
/// goes through here, so a corporation's data only ever comes from the connector the user chose for it.
/// </summary>
public static class ConnectorRouter
{
    /// <summary>
    /// The connector whose scope lists the character's corporation, else one whose scope lists its alliance;
    /// null when none.
    ///
    /// <para>Matched on the user's CHOICES (functions and scopes), not on connection state. When the owner is
    /// disconnected or no longer offers <paramref name="function"/>, the caller still gets the owner and checks
    /// <see cref="ConnectorSnapshot.CanAsk"/>. There is no fallback to another connector: the user's choice for
    /// a corporation decides which connector holds its data, so the source never changes silently. Ties cannot
    /// happen (the settings refuse them); as a defence the lowest id wins.</para>
    /// </summary>
    public static ConnectorSnapshot? ForCharacter(IEnumerable<ConnectorSnapshot> entries, string function, int corporationId, int? allianceId)
    {
        var chosen = entries.Where(e => e.Chose(function)).OrderBy(e => e.Id).ToList();
        return chosen.FirstOrDefault(e => e.Corporations.Contains(corporationId))
            ?? (allianceId is { } a ? chosen.FirstOrDefault(e => e.Alliances.Contains(a)) : null);
    }

    /// <summary>
    /// The first (function, corporation) or (function, alliance) pair one of <paramref name="others"/> already
    /// holds, or null. A corporation on one connector and its alliance on another is allowed: the corporation wins.
    /// </summary>
    public static ConnectorConflict? FindConflict(
        IEnumerable<ConnectorSnapshot> others,
        IReadOnlyCollection<int> corporations, IReadOnlyCollection<int> alliances, IReadOnlyCollection<string> functions)
    {
        var sorted = others.OrderBy(o => o.Id).ToList();
        foreach (var f in functions)
            foreach (var o in sorted.Where(o => o.Chose(f)))
            {
                foreach (var corp in corporations)
                    if (o.Corporations.Contains(corp)) return new(f, false, corp, o.Id, o.Title);
                foreach (var ally in alliances)
                    if (o.Alliances.Contains(ally)) return new(f, true, ally, o.Id, o.Title);
            }
        return null;
    }
}
