using System.Text.RegularExpressions;

namespace EveConsole.Monitoring;

/// <summary>
/// Parses intel-channel messages into "who is where, and how many".
///
/// ─── Verified against ~60,000 real intel-channel messages ───
/// The client does not write link markup to the log, so a system link and a character
/// link both arrive as bare text. What it does write is a SPACE ON EITHER SIDE of every
/// link, which means adjacent links are separated by TWO spaces:
///
///     QZ-X77  ann Example  Bo Sample  Offgrid Booster
///     XQ1-Z2  Tester  Sampler nv
///
/// That double space is the only structural signal in the format and it does most of the
/// work here: it splits the line into chunks that are each one entity plus whatever the
/// reporter typed after it. Within a chunk, tokens are matched longest-run-first.
///
/// Observed variation, all of which this handles (systems and pilots invented; the shapes
/// are the real ones):
///   system first        XQ1-Z2  Tester  Sampler nv
///   name first          Examplia  QX65-9            Offgrid Booster  QZ-X77*
///   count only          +5  ZQ-XWZ                  XZ-0Q7 +8
///   count either side   Q-XZWQ 6+                   9-97XQ*  Ann Other +13
///   trailing star       ZX-QQW*  Q2XZ-X*            (reporter convention, not part of the name)
///   trailing period     3Q3X-Z  xyz404 +3. Naga, Flycatcher...
///   several names       ZQ-XWZ  Al-Example  Bo da Sample  Cy Tester  Dee Placeholder +2
///   non-English text    击杀：ann Example (黑豹级*)   短吻鳄级 10+ QXZ-ZW*
///   bare plus, no digit + stabber / VNI               (ignored — no number to add)
///
/// A "clr" line names a system and no one, and comes back as a Clear: it retires what was
/// standing there. A line naming a system and only a flag — "QZ-X77 spike", "XQ1-Z2 bubbles"
/// — is a sighting of no one in particular.
///
/// Shortened system names ("QZ-X" for QZ-X77), ship slang, plurals and counts tied to ships
/// ("3 lokis", "Sabre x2") are resolved through an ILexicon the caller builds per channel; the
/// slang list is our own, taken from this app's stored intel history.
/// ─────────────────────────────────────────────────────────────────────────────────────
///
/// Resolution is deliberately split in two so the caller can batch its lookups: ask for
/// <see cref="NameCandidates"/> first, resolve them all at once, then call
/// <see cref="Parse"/> with a lookup that already knows the answers.
/// </summary>
public static class IntelRules
{
    private const RegexOptions Opts =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>Two or more spaces — the client's link padding, and our entity boundary.</summary>
    private static readonly Regex ChunkSplitRx = new(@"\s{2,}", Opts);

    /// <summary>"+6" and "6+" both occur, roughly equally often.</summary>
    private static readonly Regex PlusCountRx = new(@"^\+(?<n>\d{1,4})$|^(?<n2>\d{1,4})\+$", Opts);

    /// <summary>
    /// Longest character name EVE permits is 37 characters across at most three words, so a
    /// run longer than this can never be one and is not worth asking about.
    /// </summary>
    private const int MaxNameTokens = 3;

    /// <summary>Reporters mark systems with a trailing star and end sentences with punctuation;
    /// neither is part of the name. Brackets show up as "Tester (Loki)".</summary>
    private static readonly char[] Trim = ['*', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '"', '\''];

    /// <summary>
    /// What a line reports. A <see cref="Clear"/> is not a sighting and stores no report of its
    /// own — it says the system has been looked at and is empty, which retires whatever was
    /// standing for that system.
    /// </summary>
    public enum IntelKind { Sighting, Clear }

    /// <summary>Words reporters use for "I looked, there is nobody here", with the typos of them
    /// the stored history shows. Deliberately does NOT include "nv" (no visual), which means the
    /// opposite — the reporter could not see, so it says nothing about whether anyone is still
    /// there.</summary>
    private static readonly HashSet<string> ClearWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "clr", "clear", "cleared", "empty", "clrr", "crl", "cir", "cls", "clea",
    };

    /// <summary>"No visual" — the reporter knows someone is there but cannot see them, usually
    /// because they are cloaked or off grid. Common enough to be worth its own field rather than
    /// being left as noise in the note.</summary>
    private static readonly HashSet<string> NoVisualWords =
        new(StringComparer.OrdinalIgnoreCase) { "nv", "n/v", "novis" };

    /// <summary>
    /// Words never treated as a pilot, however well they match a character name.
    ///
    /// Real players are called things like "gate", "status", "and", "hole" and "Kill", and once
    /// ESI confirms such a name it goes into the shared entity-name cache — after which every
    /// reporter who types that ordinary word is recorded as having seen that person. Across the
    /// stored history this produced ~6,900 phantom pilot rows, inflating headcounts and dragging
    /// unrelated systems into one pilot's supersede chain.
    ///
    /// Chosen from this user's own channels, by taking every single-token match that appears in
    /// 8 or more systems and has NEVER appeared on a killmail — vocabulary scatters across the
    /// map and never dies, whereas a real pilot concentrates and eventually shows up on a kill.
    /// That candidate set was then filtered: the English entries are those matching the 10,000
    /// most common English words, and the EVE entries were picked by hand, because a plain
    /// dictionary knows nothing of "dscan" or "ansiblex".
    ///
    /// ⚠ Deliberately conservative. A missed stop word costs one bogus row; a wrongly listed one
    /// means a genuine hostile stops being tracked. Anything that reads like a name was left off
    /// even where the numbers looked suspicious — "cyberanarchist" and "Niceee" among them.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Common English ────────────────────────────────────────────────────
        "about", "active", "again", "all", "also", "and", "anyone", "are", "around", "atm",
        "back", "bank", "being", "blue", "bridge", "bubble", "but", "came", "camp", "camping",
        "clearing", "core", "enemy", "eyes", "fleet", "for", "fort", "from", "gang", "gate",
        "gates", "get", "going", "gone", "got", "group", "has", "have", "heading", "here",
        "him", "hole", "hot", "hunting", "into", "issue", "jump", "jumping", "just", "keep",
        "kill", "killing", "large", "last", "left", "likely", "linked", "logged", "male",
        "max", "maybe", "might", "min", "mobile", "more", "mostly", "navy", "near", "not",
        "off", "only", "other", "out", "please", "plus", "pod", "possible", "probably", "red",
        "rest", "saw", "scan", "ship", "ships", "shuttle", "sitting", "small", "solar",
        "sorry", "status", "still", "system", "test", "that", "the", "theft", "them", "there",
        "they", "this", "through", "was", "went", "what", "with",

        // ── EVE vocabulary a dictionary does not know ─────────────────────────
        // Ships, by slang or abbreviation
        "retri", "kiki", "stilleto", "stilletto", "saber", "lokis", "hecates", "dictor",
        "VNI", "CNI", "SFI", "ENI", "ONI", "shuttles",
        // Mechanics and structures
        "ESS", "ANSI", "ansiblex", "spike", "neut", "neuts", "blops", "dscan", "cyno",
        "filament", "skyhook", "gatecamp", "wormhole", "probes",
        // Bubbles, including the common misspelling
        "bubbled", "bubbles", "bubbling", "buble",
        // States and actions
        "camped", "jumped", "cloaked", "anchored", "docked", "robbing", "stealing",
        "hostile", "hostiles", "reds", "dropper",
        // Alliance tickers reporters type as words
        "Horde", "init", "FRT",
        // Chat shorthand
        "pls", "5min", "ved",

        // Added from review of the parsed output
        "were", "glimpse", "sat", "issues", "where", "nay",
        "entered", "well", "wel", "pipe", "update", "of",

        // Words that say which way someone went. They decide which system is the place, so a
        // character who happens to be called one must never swallow it.
        "headed", "moving", "towards", "toward", "entering", "enters", "jumps",

        // Ships, structures and groups named in passing
        "destroyer", "keepstar", "tuskers", "prob", "sabe", "nano", "prot", "grid",
        // Shortened hull names the SDE does not carry: it lists "Imperial Navy Slicer", so
        // "navy slicer" fails the ship match and the second word falls through to pilot names.
        "slicer",

        // Chat and commentary
        "fighting", "info", "tea", "meme", "bunch", "sos", "guys", "plz", "getting",
        "ambushed", "menny", "strip", "outside", "established", "currently", "stufff",
        "reported", "which", "200", "safe", "intel",

        // Connectives, so the all-words rule covers combinations of listed words without
        // every pairing having to be written out — "in the", "gang on", "a hole", "did not"
        // and "gate is camped" all fall out of these plus words already above.
        "in", "is", "a", "on", "up", "did", "coming", "big", "under", "attack",
        "moon", "planet", "x", "how",

        // ── Phrases ───────────────────────────────────────────────────────────
        // Multi-token matches were overwhelmingly REAL names — one player runs an Expanse-themed
        // alt fleet, so "Capt Amos Burton" and "Naomi Nagata" look like chatter and are not.
        // Only these six were actually phrases.
        "on the", "gate in", "gate camp", "drag bubble", "still here", "they are",
        "all in", "look in", "how do", "combat probes out",
        // Phrases whose parts are not all listed on their own
        "big spike", "navy slicer", "eni on", "moon 1", "planet V", "15 x", "bubble up",
    };


    /// <summary>
    /// Whether a run should never be taken as a pilot: either it is listed outright, or every
    /// one of its words is.
    ///
    /// The second test is what catches combinations nobody enumerated. "the" and "gate" were
    /// both listed, yet "the gate" still matched a character and was recorded as a sighting of
    /// them — and the same would hold for any other pairing of listed words.
    /// </summary>
    private static bool IsStopRun(string run) =>
        StopWords.Contains(run) ||
        run.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts
            && parts.All(StopWords.Contains);

    // ── What the parser asks the outside world ───────────────────────────────

    /// <summary>A system a run of words names: <paramref name="Name"/> as the SDE spells it, and
    /// whether it was a shortened form ("UALX", "5-C") rather than the full name.</summary>
    public sealed record SystemMatch(string Name, bool Short);

    /// <summary>A ship a run of words names: a hull, with its type id, or a whole class
    /// ("Interdictor", from "dictors"), which has none.</summary>
    public sealed record ShipMatch(string Name, int? TypeId, bool IsClass);

    /// <summary>
    /// What the parser looks words up in. The parser stays pure; the caller decides what a
    /// system, a ship or a character is — per channel, because a shortened system name means one
    /// system in one region's channel and another elsewhere.
    /// </summary>
    public interface ILexicon
    {
        SystemMatch? System(string run);
        ShipMatch?   Ship(string run);
        /// <param name="startsChunk">The run begins a chunk — where a pasted link begins, the
        /// client padding it with spaces; typed chatter mostly runs on mid-chunk.</param>
        bool         Character(string run, bool startsChunk);
    }

    /// <summary>What a reporter says about a system besides who is in it. Stored as a bit set,
    /// so these values never change.</summary>
    [Flags]
    public enum IntelFlags
    {
        None     = 0,
        Spike    = 1 << 0,   // local spike
        Camp     = 1 << 1,   // gate camp
        Bubbles  = 1 << 2,   // warp disruption bubbles
        Wormhole = 1 << 3,   // a wormhole in the system
        Ess      = 1 << 4,   // someone at the ESS
        Cyno     = 1 << 5,   // a cyno lit
        Skyhook  = 1 << 6,   // someone at a skyhook
        Probes   = 1 << 7,   // combat probes out
        Hotdrop  = 1 << 8,   // a hot drop, or a pilot set up for one
    }

    /// <summary>Words that set a flag. Two-word phrases are tried first.</summary>
    private static readonly Dictionary<string, IntelFlags> FlagWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gate camp"] = IntelFlags.Camp, ["local spike"] = IntelFlags.Spike, ["drag bubble"] = IntelFlags.Bubbles,
        ["combat probes"] = IntelFlags.Probes, ["combat probing"] = IntelFlags.Probes,

        ["spike"] = IntelFlags.Spike, ["spiked"] = IntelFlags.Spike,
        ["camp"] = IntelFlags.Camp, ["camped"] = IntelFlags.Camp, ["camping"] = IntelFlags.Camp,
        ["gatecamp"] = IntelFlags.Camp,
        ["bubble"] = IntelFlags.Bubbles, ["bubbles"] = IntelFlags.Bubbles, ["bubbled"] = IntelFlags.Bubbles,
        ["bubbling"] = IntelFlags.Bubbles, ["buble"] = IntelFlags.Bubbles,
        ["wh"] = IntelFlags.Wormhole, ["wormhole"] = IntelFlags.Wormhole, ["k162"] = IntelFlags.Wormhole,
        ["ess"] = IntelFlags.Ess,
        ["cyno"] = IntelFlags.Cyno,
        ["skyhook"] = IntelFlags.Skyhook,
        ["probes"] = IntelFlags.Probes,
        ["hotdrop"] = IntelFlags.Hotdrop, ["hotdropped"] = IntelFlags.Hotdrop, ["hotdropper"] = IntelFlags.Hotdrop,
        ["dropper"] = IntelFlags.Hotdrop, ["droppers"] = IntelFlags.Hotdrop,
    };

    /// <summary>Words that make the system before them a gate: "QZ-X77 gate", "XQ1-Z2 ansi".</summary>
    private static readonly HashSet<string> GateWords =
        new(StringComparer.OrdinalIgnoreCase) { "gate", "ansi", "ansiblex", "jb" };

    /// <summary>Words that make the system after them where the hostiles came FROM: "left QZ-X77",
    /// "jumped in from XQ1-Z2". Such a system is never the place; it is where they were before.</summary>
    private static readonly HashSet<string> FromWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "left", "leaving", "from",
    };

    /// <summary>Words that make the system after them somewhere the hostiles did NOT go: "did not
    /// come QZ-X77". Never the place, and never where they came from either.</summary>
    private static readonly HashSet<string> NotWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "didnt", "didn't", "never",
    };

    // ── Direction ────────────────────────────────────────────────────────────
    //
    // Reporters say where a gang went in a handful of fixed ways, and each means one thing:
    //   QZ-X77 -> XQ1-Z2   (also ">", "=>", "→")   now in XQ1-Z2, came from QZ-X77
    //   jumped into XQ1-Z2 / entered XQ1-Z2           now in XQ1-Z2
    //   heading XQ1-Z2 / going to / moving to / towards  still where reported; XQ1-Z2 is only intended
    // A bare "to" is far too common in chatter ("going to bed", "talk to him") to mean anything on
    // its own, so it counts only right after one of the heading words. Every one of these needs a
    // system right after it: a direction word followed by anything else is chatter.

    /// <summary>An arrow typed between two systems.</summary>
    private static readonly Regex ArrowRx = new(@"^(?:-+>|=+>|>+|→)$", Opts);

    /// <summary>The same arrows inside a word ("QZ-X77->XQ1-Z2"), to split them out. No system or
    /// character name can contain one. Without ExplicitCapture, so Split keeps what it split on.</summary>
    private static readonly Regex ArrowSplitRx = new(@"(-+>|=+>|>+|→)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Words that put them IN the system after them: "entered XQ1-Z2", "into XQ1-Z2", and
    /// "jumped XQ1-Z2". Such a system beats every other place named on the line.</summary>
    private static readonly HashSet<string> IntoWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "into", "entered", "enters", "entering", "jumped", "jumping", "jumps",
    };

    /// <summary>The jump words, which may take one of <see cref="IntoLinks"/> before the system:
    /// "jumped in XQ1-Z2", "jumped to XQ1-Z2".</summary>
    private static readonly HashSet<string> JumpWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "jumped", "jumping", "jumps",
    };

    private static readonly HashSet<string> IntoLinks = new(StringComparer.OrdinalIgnoreCase) { "in", "into", "to" };

    /// <summary>Words that make the system after them where they are GOING, never where they are:
    /// "heading XQ1-Z2", "moving to XQ1-Z2", "towards XQ1-Z2".</summary>
    private static readonly HashSet<string> HeadingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "heading", "headed", "going", "moving", "towards", "toward",
    };

    private static bool IsArrow(string token) => ArrowRx.IsMatch(token);

    /// <summary>Words that make the number before them a headcount: "+2 neuts", "14 man".
    /// After a bare "+N" they add nothing: "+4 more" is already four.</summary>
    private static readonly HashSet<string> HeadWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "neut", "neuts", "neutral", "neutrals", "hostile", "hostiles", "red", "reds",
        "man", "players", "pilots", "more", "others",
    };

    /// <summary>"x5" or "5x": a multiplier on the ship beside it.</summary>
    private static readonly Regex TimesRx = new(@"^(?:x(?<n>\d{1,3})|(?<n>\d{1,3})x)$", Opts | RegexOptions.IgnoreCase);

    /// <summary>A plain number. Only a count when a ship or a headcount word follows it —
    /// plenty of pilots end their names in a number, so a number on its own is never one.</summary>
    private static readonly Regex BareRx = new(@"^(?<n>\d{1,3})$", Opts);

    /// <summary>"=15": a running total.</summary>
    private static readonly Regex TotalRx = new(@"^=(?<n>\d{1,4})$", Opts);

    private static int? Num(Regex rx, string token) =>
        rx.Match(token) is { Success: true } m && int.TryParse(m.Groups["n"].Value, out var n) ? n : null;

    /// <summary>A pilot named on a line, with the hull they were called in if one was given.</summary>
    public sealed record SightedPilot(string Name, string? Ship, int? ShipTypeId = null);

    /// <summary>A hull or class named with nobody to fly it: hostiles all the same,
    /// <see cref="Count"/> of them.</summary>
    public sealed record SightedShip(string Name, int? TypeId, bool IsClass, int Count);

    public sealed record ParsedIntel(
        IntelKind                    Kind,
        string                       SystemName,
        int                          PlayerCount,
        IReadOnlyList<SightedPilot>  Pilots,
        bool                         NoVisual,
        string                       Note)
    {
        /// <summary>Spike, gate camp, bubbles and the rest.</summary>
        public IntelFlags Flags { get; init; }

        /// <summary>The system whose gate they are on — "on the QZ-X77 gate" in the reported
        /// system — or null.</summary>
        public string? Gate { get; init; }

        /// <summary>Hulls and classes with no pilot named for them; already in the count.</summary>
        public IReadOnlyList<SightedShip> Ships { get; init; } = [];

        /// <summary>The system was named by a shortened form, resolved with the channel's help.</summary>
        public bool ShortSystem { get; init; }

        /// <summary>Where they came from — "QZ-X77 -> XQ1-Z2", "XQ1-Z2 ... from QZ-X77" — or null.</summary>
        public string? FromSystem { get; init; }

        /// <summary>Where they are said to be going — "heading XQ1-Z2" — or null. Only intended:
        /// they are still in <see cref="SystemName"/>.</summary>
        public string? HeadingSystem { get; init; }
    }

    /// <summary>
    /// A line that names no place but adds to a report already made: "+3", "=12", "nv", "Tester
    /// Loki", "heading XQ1-Z2", "clr". The caller decides which report it belongs to.
    /// </summary>
    /// <param name="Plus">Added to the unnamed count ("+3").</param>
    /// <param name="Total">The whole count ("=12", "12 total"), or 0 when none was given.</param>
    /// <param name="Clear">The line is a clear word and nothing else: the system is now empty.</param>
    /// <param name="HeadingSystem">Where they are going, or null.</param>
    public sealed record ParsedFollowUp(
        IReadOnlyList<SightedPilot> Pilots,
        int                         Plus,
        int                         Total,
        IReadOnlyList<SightedShip>  Ships,
        IntelFlags                  Flags,
        bool                        NoVisual,
        bool                        Clear,
        string?                     HeadingSystem,
        string                      Note);

    private static string Clean(string token) => token.Trim().Trim(Trim).Trim();

    /// <summary>The "+3" / "3+" value, or null when the token is not a count. A bare "+" has
    /// no number and is not one.</summary>
    private static int? PlusValue(string token)
    {
        var m = PlusCountRx.Match(token);
        if (!m.Success) return null;
        var g = m.Groups["n"].Success ? m.Groups["n"] : m.Groups["n2"];
        return int.TryParse(g.Value, out var n) ? n : null;
    }

    /// <summary>A token keeps both forms: the cleaned one is what names are matched against,
    /// the raw one is what goes into the note, so "Naga, Flycatcher, Malediction" reads back
    /// with its punctuation instead of as three bare words.</summary>
    private readonly record struct Token(string Raw, string Clean);

    private static List<Token[]> Chunks(string message) =>
        [.. ChunkSplitRx.Split(message.Trim())
            .Select(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .SelectMany(SplitArrows)
                          .Select(t => new Token(t, Clean(t)))
                          .Where(t => t.Clean.Length > 0)
                          .ToArray())
            .Where(c => c.Length > 0)];

    /// <summary>"QZ-X77->XQ1-Z2" as three words; any other word as itself.</summary>
    private static IEnumerable<string> SplitArrows(string word) =>
        word.Length > 1 && !IsArrow(word) && ArrowSplitRx.IsMatch(word)
            ? ArrowSplitRx.Split(word).Where(p => p.Length > 0)
            : [word];

    private static string Run(Token[] chunk, int start, int len) =>
        string.Join(' ', chunk.Skip(start).Take(len).Select(t => t.Clean));

    /// <summary>Whether a run is never worth asking about as a character: a control word, a
    /// count, a flag, a gate word or a listed stop word.</summary>
    private static bool NotAName(string run) =>
        IsStopRun(run) || PlusValue(run) is not null || ClearWords.Contains(run) || NoVisualWords.Contains(run)
        || FlagWords.ContainsKey(run) || GateWords.Contains(run) || BareRx.IsMatch(run)
        || run.Contains('>') || run.Contains('→');   // an arrow, or a run across one: no name holds either

    /// <summary>
    /// Every token run that could be a character name, for the caller to resolve in one batch.
    /// Runs that already match a system are skipped: a system name is never also asked about as
    /// a character, which is what keeps "QZ-X77 Some Pilot QZ-X77" from being resolved twice.
    /// Ship names are asked about: a pilot can be called "Loki", and only knowing so lets the
    /// parser tell "Loki (Sabre)" from a Loki.
    /// </summary>
    public static IReadOnlyList<string> NameCandidates(string message, Func<string, bool> isSystem)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in Chunks(message))
            for (var start = 0; start < chunk.Length; start++)
                for (var len = Math.Min(MaxNameTokens, chunk.Length - start); len >= 1; len--)
                {
                    var run = Run(chunk, start, len);
                    if (run.Length is 0 or > 37) continue;   // EVE's own name limit
                    if (NotAName(run))           continue;   // never asked about at all
                    if (isSystem(run))           continue;
                    seen.Add(run);
                }

        return [.. seen];
    }

    /// <summary>
    /// Parses one message, or returns null when it is not a usable report.
    ///
    /// <para>One pass, left to right, longest run first at each position. Before any name is
    /// tried, a token is checked as a count, a control word ("clr", "nv") or a flag ("spike",
    /// "bubbles"), because several of those are also real character names. Then a run is tried
    /// as a system, a ship, then a character.</para>
    ///
    /// <para>Needs a system, and then someone — a named pilot, a count, a ship — or a flag. A
    /// system with a clear word and nothing else is a <see cref="IntelKind.Clear"/>.</para>
    ///
    /// <para>The place is a system they went into ("-> XQ1-Z2", "jumped into XQ1-Z2") when there is
    /// one, else the first system named that is not where they came from, did not go, or are
    /// heading. A line whose only system is one of those has no place: see <see cref="ParseFollowUp"/>.</para>
    /// </summary>
    public static ParsedIntel? Parse(string message, ILexicon lexicon)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        if (Scan(message, lexicon) is not { } s) return null;

        var systems = s.Systems;

        // The place: a system they went into, then the first full system name that is not a gate,
        // else a shortened one, else a gate on its own — "QZ-X77 gate" alone has been named as where
        // they are. Never a system they left, did not go to, or are only heading for.
        var at = systems.FindIndex(x => x.Into && x.Placeable && !x.Match.Short);
        if (at < 0) at = systems.FindIndex(x => x.Into && x.Placeable);
        if (at < 0) at = systems.FindIndex(x => !x.Gate && x.Placeable && !x.Match.Short);
        if (at < 0) at = systems.FindIndex(x => !x.Gate && x.Placeable);
        if (at < 0) at = systems.FindIndex(x => x.Placeable);
        if (at < 0) return null;
        var location = systems[at].Match;

        var gateIx = systems.FindIndex(x => x.Gate);
        if (gateIx == at) gateIx = systems.FindIndex(at + 1, x => x.Gate);

        // Where they came from: the system an arrow led from into the place, else one they left.
        var fromIx = systems[at].CameFrom >= 0 ? systems[at].CameFrom : systems.FindIndex(x => x.From);
        if (fromIx >= 0 && SameSystem(systems[fromIx], location)) fromIx = -1;

        var headingIx = systems.FindIndex(x => x.Heading);
        if (headingIx >= 0 && SameSystem(systems[headingIx], location)) headingIx = -1;

        // The place, where they came from and where they are going are fields of their own, so neither
        // they nor the words that said which was which are left in the note. Any other system named
        // goes there as written.
        var drop = new HashSet<int>();
        foreach (var ix in new[] { at, fromIx, headingIx })
            if (ix >= 0) drop.UnionWith(systems[ix].Words.Select(w => s.NoteAt.GetValueOrDefault(w, -1)));
        var note = s.Note.Where((_, n) => !drop.Contains(n)).ToList();
        for (var x = 0; x < systems.Count; x++)
            if (x != at && x != gateIx && x != fromIx && x != headingIx) note.Add(systems[x].Raw);

        // Asked, not reported. A question naming a pilot — "XQ1-Z2 Tester?" — is somebody
        // wondering whether Tester is still there.
        if (s.IsQuestion && s.Plus == 0 && s.Total == 0) return null;

        // Named pilots and "+N" on one side, the ships listed on the other, and the larger of the
        // two. Ships listed beside pilots or a "+N" are nearly always what those people fly —
        // "Tester  Bo Sample  2x sabre", "+15  7 Keres  Kitsune" — not more people; ships with
        // nobody named are people in their own right — "QZ-X77 3 lokis".
        var count = Math.Max(s.Total, Math.Max(s.Pilots.Count + s.Plus, s.Ships.Sum(x => x.Count)));

        // "XQ1-Z2 clr" — a system, nobody in it, and the word for it. Reported rather than
        // dropped so the caller can retire the standing sightings for that system: somebody has
        // looked, and whoever was there has gone.
        if (count == 0 && (s.SawClearWord || s.Flags == IntelFlags.None))
            return s.SawClearWord
                ? new ParsedIntel(IntelKind.Clear, location.Name, 0, [], s.NoVisual, string.Join(' ', note).Trim())
                : null;

        return new ParsedIntel(IntelKind.Sighting, location.Name, count, s.Pilots, s.NoVisual, string.Join(' ', note).Trim())
        {
            Flags         = s.Flags,
            Gate          = gateIx >= 0 ? systems[gateIx].Match.Name : null,
            Ships         = s.Ships,
            ShortSystem   = location.Short,
            FromSystem    = fromIx    >= 0 ? systems[fromIx].Match.Name    : null,
            HeadingSystem = headingIx >= 0 ? systems[headingIx].Match.Name : null,
        };
    }

    /// <summary>
    /// Reads a line that names no place — what <see cref="Parse"/> returned null for — as an
    /// addition to a report already made: "+3", "=12", "nv", "Tester Loki", "heading XQ1-Z2", "clr".
    ///
    /// <para>Null when the line adds nothing, when it is a question (unless it carries a count, as
    /// for <see cref="Parse"/>), and when it names any system other than where they are heading: a
    /// line about a system they left or did not go to says nothing about where they are now, and
    /// restating a report with it would put them back where they were.</para>
    /// </summary>
    public static ParsedFollowUp? ParseFollowUp(string message, ILexicon lexicon)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        if (Scan(message, lexicon) is not { } s) return null;

        if (s.Systems.Any(x => !x.Heading)) return null;
        if (s.IsQuestion && s.Plus == 0 && s.Total == 0) return null;

        var heading = s.Systems.Count > 0 ? s.Systems[0] : null;

        // A clear word with nobody counted: the system has emptied. With people named or counted it
        // is something else ("Tester cleared the gate"), as it is for Parse.
        var counted = s.Pilots.Count > 0 || s.Plus > 0 || s.Total > 0 || s.Ships.Count > 0;
        var clear   = s.SawClearWord && !counted;

        if (!counted && !clear && s.Flags == IntelFlags.None && !s.NoVisual && heading is null) return null;

        var drop = heading is null ? new HashSet<int>() : heading.Words.Select(w => s.NoteAt.GetValueOrDefault(w, -1)).ToHashSet();
        var note = s.Note.Where((_, n) => !drop.Contains(n));

        return new ParsedFollowUp(s.Pilots, s.Plus, s.Total, s.Ships, s.Flags, s.NoVisual, clear,
                                  heading?.Match.Name, string.Join(' ', note).Trim());
    }

    private static bool SameSystem(SystemSeen a, SystemMatch b) =>
        string.Equals(a.Match.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A system named on a line, and what the words around it said about it.</summary>
    private sealed class SystemSeen(SystemMatch match, bool gate, string raw)
    {
        public SystemMatch Match { get; } = match;
        public bool        Gate  { get; } = gate;
        public string      Raw   { get; } = raw;

        /// <summary>Somewhere they are not: a system they left or came from, or did not go to.</summary>
        public bool Away    { get; set; }
        /// <summary>Where they came from: after "from", "left", or before an arrow.</summary>
        public bool From    { get; set; }
        /// <summary>Where they went: after an arrow, "into", "entered" or "jumped".</summary>
        public bool Into    { get; set; }
        /// <summary>Where they are going, and so not where they are.</summary>
        public bool Heading { get; set; }

        /// <summary>The system an arrow led from into this one, by index; -1 when none.</summary>
        public int CameFrom { get; set; } = -1;

        /// <summary>The direction words that said so, by position, to leave out of the note when the
        /// direction is used.</summary>
        public List<(int Chunk, int Token)> Words { get; } = [];

        public bool Placeable => !Away && !Heading;
    }

    /// <summary>Everything one left-to-right pass over a line found.</summary>
    private sealed class Scanned
    {
        public List<SystemSeen>   Systems { get; } = [];
        public List<SightedPilot> Pilots  { get; } = [];
        public List<SightedShip>  Ships   { get; } = [];
        public List<string>       Note    { get; } = [];

        /// <summary>Where each token that went to the note went, by its position.</summary>
        public Dictionary<(int Chunk, int Token), int> NoteAt { get; } = [];

        public int        Plus;
        public int        Total;
        public IntelFlags Flags;
        public bool       SawClearWord;
        public bool       NoVisual;
        public bool       IsQuestion;
    }

    /// <summary>The token <paramref name="back"/> places before (chunk, token), across chunks; null at
    /// the start of the line.</summary>
    private static (int Chunk, int Token)? Before(List<Token[]> chunks, int ci, int i, int back)
    {
        while (back-- > 0)
        {
            if (i > 0) { i--; continue; }
            do { if (--ci < 0) return null; } while (chunks[ci].Length == 0);
            i = chunks[ci].Length - 1;
        }
        return (ci, i);
    }

    /// <summary>The one pass over a line that <see cref="Parse"/> and <see cref="ParseFollowUp"/>
    /// both read; null when the line has no words.</summary>
    private static Scanned? Scan(string message, ILexicon lexicon)
    {
        var chunks = Chunks(message);
        if (chunks.Count == 0) return null;

        // A question is a request for intel, not a report of it — "QZ-X77* Update?" asks whether
        // anyone has eyes on a system, and reads to the parser exactly like a sighting with no
        // one in it. Only a count rescues it: "XQ1-Z2 +3?" is someone unsure of the number, and
        // that is still a sighting.
        var s = new Scanned { IsQuestion = message.TrimEnd().EndsWith('?') };

        var systems = s.Systems;
        var pilots  = s.Pilots;
        var ships   = s.Ships;

        // What the previous item was, for binding a "x5" to the ship before it and a hull to the
        // pilot right after it.
        var last = Last.None;

        // The latest pilot still without a hull, who takes the next single one named — "Tester
        // QZ-X77 Loki" is Tester in a Loki, the system in between notwithstanding. A hull left
        // unflown in between closes it: what follows that is a list of ships.
        var open = -1;

        // A number waiting for what follows: "3 lokis", "2 dictors".
        int? number = null;

        // An arrow just read, and the system before it (by index, -1 for none). Only the very next
        // item can be where the arrow points; anything else in between and the arrow was not one.
        var arrow     = false;
        var arrowFrom = -1;

        for (var ci = 0; ci < chunks.Count; ci++)
        {
            var chunk = chunks[ci];
            var i = 0;
            while (i < chunk.Length)
            {
                var tok = chunk[i].Clean;
                var arrowBefore = arrow;
                arrow = false;

                // ── Direction ────────────────────────────────────────────────────────
                // An arrow says nothing a reader needs once the systems either side of it are read,
                // so it never goes to the note.
                if (IsArrow(tok))
                {
                    arrow     = true;
                    arrowFrom = systems.Count - 1;
                    i++; last = Last.Other; continue;
                }

                // ── Counts ───────────────────────────────────────────────────────────
                // "+N" binds to nothing in particular — reporters put it before the system,
                // after the names, or on its own — so it is simply summed wherever it appears.
                if (PlusValue(tok) is { } n) { s.Plus += n; i++; last = Last.Plus; continue; }
                if (Num(TotalRx, tok) is { } t) { s.Total = Math.Max(s.Total, t); i++; last = Last.Other; continue; }

                // "x5", "5x", "x 5": a multiplier on the ship just named, or on the next one.
                var times = Num(TimesRx, tok);
                var step  = 1;
                if (times is null && tok.Equals("x", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < chunk.Length && Num(BareRx, chunk[i + 1].Clean) is { } spaced)
                    (times, step) = (spaced, 2);
                if (times is { } k)
                {
                    if (last == Last.Ship) ships[^1] = ships[^1] with { Count = k };
                    else number = k;
                    i += step; last = Last.Other; continue;
                }

                // A plain number is a count only before a headcount word or a ship.
                if (Num(BareRx, tok) is { } bare && i + 1 < chunk.Length && !StartsCharacter(chunk, i, lexicon))
                {
                    var next = chunk[i + 1].Clean;
                    if (HeadWords.Contains(next)) { s.Plus += bare; i += 2; last = Last.Other; continue; }
                    if (next.Equals("total", StringComparison.OrdinalIgnoreCase)) { s.Total = Math.Max(s.Total, bare); i += 2; last = Last.Other; continue; }
                    // "3 camping": three people, and the camp, which the flag word sets next.
                    if (FlagWords.TryGetValue(next, out var what) && what == IntelFlags.Camp) { s.Plus += bare; i++; last = Last.Plus; continue; }
                    if (ShipRunAt(chunk, i + 1, lexicon) > 0) { number = bare; i++; last = Last.Other; continue; }
                }

                // ── Control words and flags ──────────────────────────────────────────
                // Consumed before any name matching, because several of them are also real
                // character names: "clr" is an actual pilot, and resolving it once put it in the
                // shared name cache, after which every "SYSTEM clr" was read as a sighting of
                // somebody called clr rather than as a system being called clear.
                if (ClearWords.Contains(tok))    { s.SawClearWord = true; i++; last = Last.Other; continue; }
                if (NoVisualWords.Contains(tok)) { s.NoVisual     = true; i++; last = Last.Other; continue; }

                if (i + 1 < chunk.Length && FlagWords.TryGetValue(Run(chunk, i, 2), out var phrase))
                { s.Flags |= phrase; i += 2; last = Last.Other; continue; }
                if (FlagWords.TryGetValue(tok, out var flag)) { s.Flags |= flag; i++; last = Last.Other; continue; }

                // A headcount word with no number before it ("+4 more", "neuts") says nothing new.
                if (HeadWords.Contains(tok) && last is Last.Plus or Last.Other) { i++; continue; }

                // ── Names ────────────────────────────────────────────────────────────
                var matched = false;

                // Longest run first, so "Zulu Delulu" wins over "Zulu", and a multi-word system
                // such as "New Caldari" is not read as its first word alone. Hulls can be three
                // words too — "Scythe Fleet Issue".
                for (var len = Math.Min(MaxNameTokens, chunk.Length - i); len >= 1 && !matched; len--)
                {
                    var run  = Run(chunk, i, len);
                    var gate = i + len < chunk.Length && GateWords.Contains(chunk[i + len].Clean);

                    // A system. Once one has been named, a later run that is also a ship is the
                    // ship — "QZ-X77 Naga" — unless a gate word says it is a place.
                    if (lexicon.System(run) is { } sys
                        && (gate || systems.Count == 0 || lexicon.Ship(run) is null))
                    {
                        var seen = new SystemSeen(sys, gate, string.Join(' ', chunk.Skip(i).Take(len).Select(x => x.Raw)));
                        Direct(seen, chunks, ci, i, arrowBefore, arrowFrom, systems);
                        systems.Add(seen);
                        i      += len + (gate ? 1 : 0);
                        matched = true;
                        last    = Last.Other;
                        continue;
                    }

                    // Hull BEFORE character, which is the opposite of what it looks like it
                    // should be. 232 of the 423 published hulls are also somebody's character
                    // name — Loki, Sabre, Heron, Astero, Buzzard — so checking characters first
                    // reads every ship report as a pilot sighting. That inflated counts and, far
                    // worse, chained the supersede logic across unrelated systems: a "Loki" here
                    // retiring a "Loki" there. The one exception is the form "Loki (Sabre)": a
                    // hull-named character followed by a bracketed hull is a pilot and the ship
                    // they fly. Unbracketed, two hulls in a row are a list of ships.
                    if (lexicon.Ship(run) is { } ship
                        && !(!ship.IsClass && lexicon.Character(run, i == 0) && NextIsBracketedHull(chunks, ci, i + len, lexicon)))
                    {
                        var each = number ?? 1;
                        number = null;

                        // One hull after a pilot without one is theirs: "Tester (Loki)".
                        if (each == 1 && !ship.IsClass && open >= 0)
                        {
                            pilots[open] = pilots[open] with { Ship = ship.Name, ShipTypeId = ship.TypeId };
                            open = -1;
                            last = Last.Other;
                        }
                        else
                        {
                            ships.Add(new SightedShip(ship.Name, ship.TypeId, ship.IsClass, each));
                            open = -1;
                            last = Last.Ship;
                        }
                        i      += len;
                        matched = true;
                        continue;
                    }

                    if (!IsStopRun(run) && lexicon.Character(run, i == 0))
                    {
                        // One hull right before a pilot is theirs: "Loki  Tester".
                        string? hull = null; int? hullId = null;
                        if (last == Last.Ship && ships[^1] is { IsClass: false, Count: 1 } waiting)
                        {
                            (hull, hullId) = (waiting.Name, waiting.TypeId);
                            ships.RemoveAt(ships.Count - 1);
                        }
                        pilots.Add(new SightedPilot(run, hull, hullId));
                        open    = hull is null ? pilots.Count - 1 : -1;
                        number  = null;
                        i      += len;
                        matched = true;
                        last    = Last.Pilot;
                    }
                }

                // Nothing matched at this position: drop the token to the note and move on, which
                // is what turns commentary into free text.
                if (!matched)
                {
                    s.NoteAt[(ci, i)] = s.Note.Count;
                    s.Note.Add(chunk[i].Raw);
                    i++; last = Last.Other; number = null;
                }
            }
        }

        return s;
    }

    /// <summary>
    /// What the words just before a system say about it: where an arrow points, a system gone into,
    /// one they are heading for, one they came from, or one they did not go to. Only the word or
    /// two right before it count — "jumped in from XQ1-Z2" is a system they came from, not one they
    /// jumped into.
    /// </summary>
    private static void Direct(SystemSeen seen, List<Token[]> chunks, int ci, int i,
                               bool arrow, int arrowFrom, List<SystemSeen> systems)
    {
        if (arrow)
        {
            seen.Into = true;
            if (arrowFrom >= 0)
            {
                seen.CameFrom = arrowFrom;
                systems[arrowFrom].From = systems[arrowFrom].Away = true;
            }
            return;
        }

        var p1 = Before(chunks, ci, i, 1);
        var p2 = Before(chunks, ci, i, 2);
        var w1 = p1 is { } a ? chunks[a.Chunk][a.Token].Clean : "";
        var w2 = p2 is { } b ? chunks[b.Chunk][b.Token].Clean : "";

        if (IntoLinks.Contains(w1) && JumpWords.Contains(w2))
        {
            seen.Into = true;
            seen.Words.AddRange([p1!.Value, p2!.Value]);
        }
        else if ((w1.Equals("to", StringComparison.OrdinalIgnoreCase) || w1.Equals("into", StringComparison.OrdinalIgnoreCase))
                 && HeadingWords.Contains(w2))
        {
            // "heading to XQ1-Z2", "heading into XQ1-Z2": still only where they are going.
            seen.Heading = true;
            seen.Words.AddRange([p1!.Value, p2!.Value]);
        }
        else if (IntoWords.Contains(w1))
        {
            seen.Into = true;
            seen.Words.Add(p1!.Value);
        }
        else if (HeadingWords.Contains(w1))
        {
            seen.Heading = true;
            seen.Words.Add(p1!.Value);
        }
        else if (FromWords.Contains(w1))
        {
            seen.From = seen.Away = true;
            seen.Words.Add(p1!.Value);
        }
        else if (NotWords.Contains(w1))
        {
            seen.Away = true;
        }
    }
    private enum Last { None, Pilot, Ship, Plus, Other }

    /// <summary>How many tokens from <paramref name="start"/> name a ship, longest first; 0 when
    /// none do.</summary>
    private static int ShipRunAt(Token[] chunk, int start, ILexicon lexicon)
    {
        for (var len = Math.Min(MaxNameTokens, chunk.Length - start); len >= 1; len--)
            if (lexicon.Ship(Run(chunk, start, len)) is not null) return len;
        return 0;
    }

    /// <summary>Whether a bracketed hull — "(Sabre)" — comes next, in this chunk or the next one.</summary>
    private static bool NextIsBracketedHull(List<Token[]> chunks, int ci, int at, ILexicon lexicon)
    {
        var (chunk, start) = at < chunks[ci].Length ? (chunks[ci], at)
                           : ci + 1 < chunks.Count ? (chunks[ci + 1], 0) : (null, 0);
        if (chunk is null || !chunk[start].Raw.StartsWith('(') && !chunk[start].Raw.StartsWith('[')) return false;
        for (var len = Math.Min(MaxNameTokens, chunk.Length - start); len >= 1; len--)
            if (lexicon.Ship(Run(chunk, start, len)) is { IsClass: false }) return true;
        return false;
    }

    /// <summary>Whether two or three words from <paramref name="start"/> are a character's name —
    /// a pilot can be called "007 Example".</summary>
    private static bool StartsCharacter(Token[] chunk, int start, ILexicon lexicon)
    {
        for (var len = Math.Min(MaxNameTokens, chunk.Length - start); len >= 2; len--)
            if (lexicon.Character(Run(chunk, start, len), start == 0)) return true;
        return false;
    }
}
