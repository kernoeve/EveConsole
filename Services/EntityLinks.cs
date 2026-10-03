using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>
/// Which browser page an id belongs to, when nothing else says.
///
/// <para>Plenty of rows carry a bare id for "the other party" — a wallet counterparty, a contract
/// acceptor, a market buyer — where the same column holds a character on one row and a
/// corporation on the next. Making those names clickable needs an answer, and the id itself is
/// the only thing on hand.</para>
///
/// <para>⚠️ A guess, and deliberately the last resort. Prefer a stored category
/// (<c>UniverseNames.Category</c>) or a known local list whenever one exists — this is for the
/// rows where neither does. EVE's allocations are stable enough to lean on, but they are a
/// convention rather than a guarantee, and a wrong answer opens the wrong page rather than
/// failing loudly.</para>
/// </summary>
public static class EntityLinks
{
    public static EntityKind KindOf(long id) =>
        id is >= 1_000_000    and < 2_000_000   ? EntityKind.NpcCorp
      : id is >= 98_000_000   and < 99_000_000  ? EntityKind.PlayerCorp
      : id is >= 99_000_000   and < 100_000_000 ? EntityKind.Alliance
      : EntityKind.Pilot;

    /// <summary>The same question where a name lookup already answered it. Falls back to
    /// <see cref="KindOf"/> when the category is blank, which is how bulk-resolved names arrive.
    /// </summary>
    public static EntityKind KindOf(long id, string? category) => category switch
    {
        "character"   => EntityKind.Pilot,
        "corporation" => EntityKind.PlayerCorp,
        "alliance"    => EntityKind.Alliance,
        _             => KindOf(id),
    };

    /// <summary>
    /// A name resolved for a bare id, as a screen shows it: an NPC corporation's or an agent's in
    /// the interface language, anybody else's as it came.
    ///
    /// <para>The same guess as <see cref="KindOf(long)"/>, and harmless where it is wrong: an id
    /// that is not an NPC's is in neither of those tables, and comes back unchanged. ⚠️ Display
    /// only — see <c>EveConsole.Localization.SdeNames</c>.</para>
    /// </summary>
    public static string ShownName(long id, string english) => KindOf(id) switch
    {
        EntityKind.NpcCorp => SdeNames.NpcCorporation(id, english),
        EntityKind.Pilot   => SdeNames.Agent(id, english),
        _                  => english,
    };
}
