using System.Text.RegularExpressions;

namespace EveConsole.Monitoring;

/// <summary>
/// The words intel is written in: every system and hull by its full name in any of the client's
/// languages, ship classes, the slang reporters use for hulls, and the shortened system names
/// of null-sec. Built once from the SDE; shared by every channel's <see cref="IntelLexicon"/>.
/// </summary>
public sealed class IntelVocabulary
{
    /// <summary>
    /// Hull and class slang seen in this app's own stored intel, mapped to the English hull or
    /// class name. Built by ranking the words intel channels use that match nothing, and keeping
    /// those that plainly stand for a ship — shorthand, initials, common misspellings. Plurals
    /// are not listed: "lokis" and "dictors" fall out of the singular.
    /// </summary>
    internal static readonly Dictionary<string, string> Slang = new(StringComparer.OrdinalIgnoreCase)
    {
        // Shorthand
        ["kiki"]   = "Kikimora",
        ["retri"]  = "Retribution",
        ["vaga"]   = "Vagabond",
        ["drek"]   = "Drekavac",
        ["squal"]  = "Squall",
        ["jag"]    = "Jaguar",
        ["male"]   = "Malediction",
        ["cerb"]   = "Cerberus",
        ["maled"]  = "Malediction",
        // Initials of the navy and fleet hulls
        ["eni"]    = "Exequror Navy Issue",
        ["vni"]    = "Vexor Navy Issue",
        ["cni"]    = "Caracal Navy Issue",
        ["oni"]    = "Osprey Navy Issue",
        ["sfi"]    = "Stabber Fleet Issue",
        ["navy slicer"]   = "Imperial Navy Slicer",
        ["navy hookbill"] = "Caldari Navy Hookbill",
        // Misspellings
        ["stilleto"]  = "Stiletto",
        ["stilletto"] = "Stiletto",
        ["saber"]     = "Sabre",
        ["flycather"] = "Flycatcher",
        ["fly catcher"] = "Flycatcher",
        ["cynabul"]   = "Cynabal",
        // Classes
        ["dictor"] = "Interdictor",
        ["cepter"] = "Interceptor",
        ["inty"]   = "Interceptor",
        ["blops"]  = "Black Ops",
        ["bomber"] = "Stealth Bomber",
    };

    private readonly Dictionary<string, (int Id, string Name)> _systems;
    private readonly Dictionary<int, int>                      _regionOf;
    private readonly Dictionary<string, List<int>>             _short;
    private readonly HashSet<string>                           _beforeDash;
    private readonly Dictionary<int, string>                   _systemName;
    private readonly Dictionary<string, (int TypeId, string Name)> _hulls;
    private readonly Dictionary<string, string>                _classes;

    /// <param name="systems">Every system: id, English name, region.</param>
    /// <param name="systemNames">Systems' names in the client's other languages: id, name.</param>
    /// <param name="hulls">Published hulls: type id, English name.</param>
    /// <param name="hullNames">Hulls' names in the other languages: type id, name.</param>
    /// <param name="classes">Ship classes (groups): English name, and its names in the other languages.</param>
    public IntelVocabulary(
        IEnumerable<(int Id, string Name, int RegionId)> systems,
        IEnumerable<(int Id, string Name)>               systemNames,
        IEnumerable<(int TypeId, string Name)>           hulls,
        IEnumerable<(int TypeId, string Name)>           hullNames,
        IEnumerable<(string English, string Name)>       classes)
    {
        _systems    = new(StringComparer.OrdinalIgnoreCase);
        _regionOf   = [];
        _systemName = [];
        _short      = new(StringComparer.Ordinal);
        _beforeDash = new(StringComparer.Ordinal);

        foreach (var (id, name, region) in systems)
        {
            _systems[name]  = (id, name);
            _regionOf[id]   = region;
            _systemName[id] = name;

            // Null-sec names are codes with a dash — "QZ-X77", "9-97XQ". Every leading part of
            // one of at least two characters is a possible shortened form; so is the part before
            // the dash on its own.
            var dash = name.IndexOf('-');
            if (dash < 0) continue;
            var lower = name.ToLowerInvariant();
            for (var len = 2; len < lower.Length; len++)
                Add(_short, lower[..len], id);
            if (dash >= 2) _beforeDash.Add(lower[..dash]);
        }
        foreach (var (id, name) in systemNames)
            if (_systemName.TryGetValue(id, out var english)) _systems.TryAdd(name, (id, english));

        _hulls = new(StringComparer.OrdinalIgnoreCase);
        var hullEnglish = new Dictionary<int, string>();
        foreach (var (typeId, name) in hulls) { _hulls[name] = (typeId, name); hullEnglish[typeId] = name; }
        foreach (var (typeId, name) in hullNames)
            if (hullEnglish.TryGetValue(typeId, out var english)) _hulls.TryAdd(name, (typeId, english));

        _classes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (english, name) in classes) _classes.TryAdd(name, english);

        static void Add(Dictionary<string, List<int>> map, string key, int id)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            if (!list.Contains(id)) list.Add(id);
        }
    }

    public int RegionOf(int systemId) => _regionOf.GetValueOrDefault(systemId);

    /// <summary>A system by its full name, in any language, as id and English name.</summary>
    public (int Id, string Name)? Exact(string run) => _systems.TryGetValue(run, out var s) ? s : null;

    public string? NameOf(int systemId) => _systemName.GetValueOrDefault(systemId);

    /// <summary>The systems a shortened form could be, by id; empty when it is no such form.</summary>
    public IReadOnlyList<int> ShortCandidates(string lower) =>
        _short.TryGetValue(lower, out var list) ? list : [];

    /// <summary>Whether a letters-only word is the whole part before the dash of some system —
    /// "xqz" for a system called XQZ-77. Only those are taken as shortened forms without a digit or a dash.</summary>
    public bool IsBeforeDash(string lower) => _beforeDash.Contains(lower);

    /// <summary>A hull or class by name, slang or plural; null when it is neither.</summary>
    public IntelRules.ShipMatch? Ship(string run)
    {
        if (Direct(run) is { } direct) return direct;

        // Plurals: "lokis", "kiki's", "dictors", "redeemers", "harpies".
        foreach (var (suffix, singular) in new[] { ("'s", ""), ("’s", ""), ("ies", "y"), ("es", ""), ("s", "") })
            if (run.Length > suffix.Length + 2 && run.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && Direct(run[..^suffix.Length] + singular) is { } stem)
                return stem;
        return null;
    }

    private IntelRules.ShipMatch? Direct(string run)
    {
        if (_hulls.TryGetValue(run, out var hull)) return new(hull.Name, hull.TypeId, false);
        if (Slang.TryGetValue(run, out var slang))
            return _hulls.TryGetValue(slang, out var named) ? new(named.Name, named.TypeId, false)
                 : _classes.TryGetValue(slang, out var cls) ? new(cls, null, true) : null;
        if (_classes.TryGetValue(run, out var group)) return new(group, null, true);

        // "omen navy", "vexor navy": the hull's Navy Issue, where there is one.
        if (run.EndsWith(" navy", StringComparison.OrdinalIgnoreCase)
            && _hulls.TryGetValue(run[..^5] + " Navy Issue", out var navy))
            return new(navy.Name, navy.TypeId, false);
        return null;
    }
}

/// <summary>
/// One channel's view of the vocabulary: which regions it reports on, and how often it has named
/// each system — what decides which system a shortened name means there.
/// </summary>
/// <param name="Regions">The channel's regions: the ones set for it, or those it has been seen
/// to report on. Empty when neither is known.</param>
/// <param name="Frequency">How many reports the channel has made in each system.</param>
public sealed record IntelChannelHints(IReadOnlySet<int> Regions, IReadOnlyDictionary<int, int> Frequency)
{
    public static readonly IntelChannelHints None = new(new HashSet<int>(), new Dictionary<int, int>());
}

/// <summary>The lexicon <see cref="IntelRules.Parse"/> reads one channel's messages with.</summary>
public sealed class IntelLexicon(IntelVocabulary vocabulary, IntelChannelHints hints, Func<string, bool, bool> isCharacter)
    : IntelRules.ILexicon
{
    /// <summary>Counts and salutes that look like the start of a system name: "x2", "5x", "o7".</summary>
    private static readonly Regex NotShortRx = new(@"^(?:x\d+|\d+x?|o7|07|7o)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>How many more reports a channel must have made in one candidate than in the next
    /// for that to settle a shortened name the regions did not.</summary>
    private const int FrequencyMargin = 3;

    public IntelRules.ShipMatch? Ship(string run) => vocabulary.Ship(run);

    public bool Character(string run, bool startsChunk) => isCharacter(run, startsChunk);

    public IntelRules.SystemMatch? System(string run)
    {
        if (vocabulary.Exact(run) is { } exact) return new(exact.Name, false);

        // A zero typed for an O, or the other way round — "Q0-X77" for QO-X77.
        if (run.Contains('-') && (run.Contains('0') || run.Contains('O') || run.Contains('o')))
            foreach (var swapped in Swaps(run))
                if (vocabulary.Exact(swapped) is { } fixedUp) return new(fixedUp.Name, false);

        return Short(run) is { } id && vocabulary.NameOf(id) is { } name ? new(name, true) : null;
    }

    /// <summary>
    /// A shortened system name: "QZ-X", "XQ1", "xq1z". Two to five characters, with a digit
    /// or a dash in it — or, letters only, the whole part before some system's dash. Several
    /// systems often share a beginning; the channel's regions narrow them, and where that leaves
    /// more than one, the system the channel has reported far more often wins. Otherwise it is
    /// not taken: a wrong system is worse than none.
    /// </summary>
    private int? Short(string run)
    {
        var t = run.TrimEnd('-').ToLowerInvariant();
        if (t.Length is < 2 or > 5 || t.Contains(' ') || NotShortRx.IsMatch(t)) return null;

        var shaped = t.Any(char.IsDigit) || t.Contains('-');
        if (!shaped && !(t.Length >= 3 && vocabulary.IsBeforeDash(t))) return null;

        var all = vocabulary.ShortCandidates(t);
        if (all.Count == 0)
            foreach (var swapped in Swaps(t))
                if ((all = vocabulary.ShortCandidates(swapped.ToLowerInvariant())).Count > 0) break;
        if (all.Count == 0) return null;

        if (hints.Regions.Count > 0)
        {
            var local = all.Where(id => hints.Regions.Contains(vocabulary.RegionOf(id))).ToList();
            if (local.Count > 0) return Pick(local);
            // Nothing in the channel's regions: only a form that can mean one system anywhere.
            return all.Count == 1 && shaped ? all[0] : null;
        }
        return Pick(all);
    }

    private int? Pick(IReadOnlyList<int> candidates)
    {
        if (candidates.Count == 1) return candidates[0];
        var ranked = candidates.Select(id => (Id: id, N: hints.Frequency.GetValueOrDefault(id)))
                               .OrderByDescending(x => x.N).ToList();
        return ranked[0].N >= 5 && ranked[0].N >= FrequencyMargin * ranked[1].N ? ranked[0].Id : null;
    }

    private static IEnumerable<string> Swaps(string s)
    {
        yield return s.Replace('0', 'O');
        yield return s.Replace('O', '0').Replace('o', '0');
    }
}
