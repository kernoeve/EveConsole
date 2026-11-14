using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>
/// How a pilot stands with a viewing character, in the game's five tiers, plus the user's own
/// characters as a state of their own. Ordered worst first, so "the worst tier" is the smallest.
/// </summary>
public enum StandingTier
{
    Red       = 0,   // below −5: terrible
    Orange    = 1,   // −5 up to 0: bad
    Neutral   = 2,   // 0, or no standing at all
    LightBlue = 3,   // above 0 up to +5: good
    Blue      = 4,   // above +5, and everyone in a corporation or alliance one of the user's characters is in
    Own       = 5,   // one of the user's own characters
}

public static class StandingTiers
{
    /// <summary>The tier a standing value gives, at the game's own steps.</summary>
    public static StandingTier Of(double standing) => standing switch
    {
        < -5 => StandingTier.Red,
        < 0  => StandingTier.Orange,
        0    => StandingTier.Neutral,
        <= 5 => StandingTier.LightBlue,
        _    => StandingTier.Blue,
    };

    /// <summary>Neutral or worse: someone nobody has said is a friend.</summary>
    public static bool IsHostile(StandingTier tier) => tier <= StandingTier.Neutral;

    /// <summary>The tiers shown by default: everyone not blue and not ours.</summary>
    public static readonly IReadOnlySet<StandingTier> Hostile =
        new HashSet<StandingTier> { StandingTier.Red, StandingTier.Orange, StandingTier.Neutral };

    /// <summary>The five standings tiers, worst first; not the user's own.</summary>
    public static readonly IReadOnlyList<StandingTier> Five =
        [StandingTier.Red, StandingTier.Orange, StandingTier.Neutral, StandingTier.LightBlue, StandingTier.Blue];
}

/// <summary>
/// One reading of every contact list the app holds and of the user's own characters: what a tier
/// is worked out from. Immutable and pure, so a projection can use it on any thread and a check can
/// build one by hand.
///
/// <para>An own character is Own; anyone in a corporation or alliance any of the user's characters
/// is in is Blue, whichever character is viewing (the user's rule, 2026-11-13: an alt's corporation
/// and alliance are friends of all of them); otherwise the viewer's personal contacts, then its corporation's, then its
/// alliance's, and in each list a contact on the pilot, else on the pilot's corporation, else on the
/// pilot's alliance. The first one found decides. Nothing found is Neutral. Faction standings are
/// not used.</para>
/// </summary>
public sealed class IntelStandings
{
    /// <summary>One of the user's characters: who it is and whom it flies for.</summary>
    public sealed record OwnCharacter(long CharacterId, string Name, long CorporationId, long AllianceId);

    private readonly Dictionary<long, OwnCharacter> _own;
    private readonly HashSet<long> _ownCorps;
    private readonly HashSet<long> _ownAlliances;

    /// <summary>Owner id (character, corporation or alliance) → contact id → standing.</summary>
    private readonly Dictionary<long, Dictionary<long, double>> _contacts;

    public IntelStandings(IEnumerable<OwnCharacter> own, IEnumerable<long> ownCorporations,
                          IEnumerable<(long OwnerId, long ContactId, double Standing)> contacts,
                          long forcedCharacterId = 0)
    {
        _own = own.GroupBy(o => o.CharacterId).ToDictionary(g => g.Key, g => g.First());
        _ownCorps = [.. ownCorporations.Where(c => c > 0), .. _own.Values.Select(o => o.CorporationId).Where(c => c > 0)];
        _ownAlliances = [.. _own.Values.Select(o => o.AllianceId).Where(a => a > 0)];
        _contacts = [];
        foreach (var (owner, contact, standing) in contacts)
        {
            if (!_contacts.TryGetValue(owner, out var list)) _contacts[owner] = list = [];
            list[contact] = standing;
        }
        ForcedCharacterId = forcedCharacterId > 0 && _own.ContainsKey(forcedCharacterId) ? forcedCharacterId : 0;
    }

    public static readonly IntelStandings Empty = new([], [], []);

    /// <summary>The character the setting forces for everyone, or 0. A character no longer ours
    /// is ignored, rather than colouring everything Neutral.</summary>
    public long ForcedCharacterId { get; }

    public IReadOnlyCollection<OwnCharacter> OwnCharacters => _own.Values;

    public bool IsOwn(long pilotId) => _own.ContainsKey(pilotId);

    public OwnCharacter? Own(long characterId) => _own.GetValueOrDefault(characterId);

    /// <summary>Whose jump bridges a walk from this origin may use: the character's alliance. A system
    /// chosen with no character (0) uses the alliance every one of the user's characters is in, when
    /// they are all in one; otherwise none, since nobody could say whose bridges they would be.</summary>
    public long? BridgeAlliance(long characterId)
    {
        if (characterId > 0) return Own(characterId)?.AllianceId is long a and > 0 ? a : null;
        var alliances = _own.Values.Select(o => o.AllianceId).Distinct().ToList();
        return alliances is [> 0 and var only] ? only : null;
    }

    /// <summary>The character whose standings a view uses: the forced one when set, else the one
    /// asked for (the row's nearest origin), else null for the rule with no viewer.</summary>
    public long? EffectiveViewer(long? requested) =>
        ForcedCharacterId > 0 ? ForcedCharacterId
        : requested is > 0 && _own.ContainsKey(requested.Value) ? requested : null;

    /// <summary>Whether the app holds any personal contacts of the character. Without the contacts
    /// scope it holds none, and a view says "personal contacts unknown".</summary>
    public bool HasPersonalContacts(long characterId) => _contacts.ContainsKey(characterId);

    /// <summary>The pilot's tier as <paramref name="viewerId"/> sees it: the user's own corporations
    /// and alliances first, then the viewer's contacts in the game's order.</summary>
    public StandingTier TierOf(long viewerId, long pilotId, long corporationId, long allianceId)
    {
        if (_own.ContainsKey(pilotId)) return StandingTier.Own;
        // ⚠️ Any of the user's characters, not only the viewer: someone in an alt's corporation or
        // alliance is blue to every character, whatever the viewer's own contacts say of them.
        if (IsOwnGroup(corporationId, allianceId)) return StandingTier.Blue;
        if (!_own.TryGetValue(viewerId, out var viewer)) return StandingTier.Neutral;

        foreach (var owner in new[] { viewer.CharacterId, viewer.CorporationId, viewer.AllianceId })
        {
            if (owner <= 0 || !_contacts.TryGetValue(owner, out var list)) continue;
            if (list.TryGetValue(pilotId, out var s)) return StandingTiers.Of(s);
            if (corporationId > 0 && list.TryGetValue(corporationId, out s)) return StandingTiers.Of(s);
            if (allianceId > 0 && list.TryGetValue(allianceId, out s)) return StandingTiers.Of(s);
        }
        return StandingTier.Neutral;
    }

    /// <summary>
    /// The tier with no single viewer (a map with nobody chosen, an alarm watching systems): the
    /// friendliest any of the user's characters gives. Any of them blue makes the pilot blue, as
    /// the map has always judged it, so a friend is never painted as a threat for want of a viewer.
    /// </summary>
    public StandingTier UnionTier(long pilotId, long corporationId, long allianceId)
    {
        if (_own.ContainsKey(pilotId)) return StandingTier.Own;
        if (IsOwnGroup(corporationId, allianceId)) return StandingTier.Blue;

        var best = StandingTier.Neutral;
        var any = false;
        foreach (var viewer in _own.Values)
        {
            var tier = TierOf(viewer.CharacterId, pilotId, corporationId, allianceId);
            best = any ? (tier > best ? tier : best) : tier;
            any = true;
        }
        return best;
    }

    /// <summary>In a corporation or alliance one of the user's characters is in.</summary>
    public bool IsOwnGroup(long corporationId, long allianceId) =>
        (corporationId > 0 && _ownCorps.Contains(corporationId)) || (allianceId > 0 && _ownAlliances.Contains(allianceId));

    /// <summary>The tier for a viewer, or the union rule when there is none; the forced character
    /// wins over both.</summary>
    public StandingTier TierFor(long? viewerId, long pilotId, long corporationId, long allianceId) =>
        EffectiveViewer(viewerId) is long v ? TierOf(v, pilotId, corporationId, allianceId)
                                            : UnionTier(pilotId, corporationId, allianceId);
}

/// <summary>
/// The contact lists and own characters, read every minute off the UI thread, as one immutable
/// <see cref="IntelStandings"/>. Every intel view and the intel alarms judge standings through it.
/// </summary>
public sealed class IntelStandingService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger? errors = null)
{
    public static readonly TimeSpan ReloadEvery = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private long _forced = -1;

    /// <summary>The last reading; empty until the first load.</summary>
    public IntelStandings Current { get; private set; } = IntelStandings.Empty;

    /// <summary>The reading, loaded again when it is over a minute old or the forced character has
    /// changed. Never throws: a failed read keeps the last one.</summary>
    public async Task<IntelStandings> GetAsync(long forcedCharacterId, CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _loadedAt < ReloadEvery && forcedCharacterId == _forced) return Current;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow - _loadedAt < ReloadEvery && forcedCharacterId == _forced) return Current;
            Current   = await LoadAsync(dbFactory, forcedCharacterId, ct).ConfigureAwait(false);
            _forced   = forcedCharacterId;
            _loadedAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            errors?.Log(nameof(IntelStandingService), "reading contacts", ex);
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally { _gate.Release(); }
        return Current;
    }

    public static async Task<IntelStandings> LoadAsync(
        IDbContextFactory<AppDbContext> dbFactory, long forcedCharacterId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var chars = await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.CorporationId, c.AllianceId }).ToListAsync(ct).ConfigureAwait(false);
        var corps = await db.Corporations.AsNoTracking().Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        var contacts = await db.EsiContacts.AsNoTracking()
            .Select(c => new { c.OwnerId, c.ContactId, c.Standing }).ToListAsync(ct).ConfigureAwait(false);

        return new IntelStandings(
            chars.Select(c => new IntelStandings.OwnCharacter(c.Id, c.Name, c.CorporationId, c.AllianceId ?? 0)),
            corps.Select(c => (long)c),
            contacts.Select(c => (c.OwnerId, c.ContactId, (double)c.Standing)),
            forcedCharacterId);
    }
}
