using System.Text.RegularExpressions;

namespace EveConsole.Monitoring;

/// <summary>
/// Parses intel-channel messages into "who is where, and how many".
///
/// ─── Verified against ~60,000 real messages in east.imperium / west.imperium ───
/// The client does not write link markup to the log, so a system link and a character
/// link both arrive as bare text. What it does write is a SPACE ON EITHER SIDE of every
/// link, which means adjacent links are separated by TWO spaces:
///
///     JK-Q77  kingtut Tut  Nyssa Onzo  Offgrid Booster
///     ZD1-Z2  Sevra  Chiefi nv
///
/// That double space is the only structural signal in the format and it does most of the
/// work here: it splits the line into chunks that are each one entity plus whatever the
/// reporter typed after it. Within a chunk, tokens are matched longest-run-first.
///
/// Observed variation, all of which this handles:
///   system first        ZD1-Z2  Sevra  Chiefi nv
///   name first          Galactiona  CX65-5          Offgrid Booster  JK-Q77*
///   count only          +5  HY-RWO                  GM-0K7 +8
///   count either side   Y-ORBJ 6+                   9-980U*  Alphonse Cruz +13
///   trailing star       KW-OAM*  D2EZ-X*            (reporter convention, not part of the name)
///   trailing period     3L3N-X  jsh666 +3. Naga, Flycatcher...
///   several names       HY-RWO  Al-Punchy  Atlan da Gonozol  Evel Knieve  Jason Aiderona +2
///   non-English text    击杀：kingtut Tut (黑豹级*)   短吻鳄级 10+ GHZ-SJ*
///   bare plus, no digit + stabber / VNI               (ignored — no number to add)
///
/// "clr", "clear" and "nv" lines name a system but report no one, so they fail the
/// "system AND (a name OR a count)" rule and are dropped. That is deliberate: they say a
/// system is empty, which is not a sighting.
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
    /// neither is part of the name. Brackets show up as "Sevra (Loki)".</summary>
    private static readonly char[] Trim = ['*', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '"', '\''];

    /// <summary>
    /// What a line reports. A <see cref="Clear"/> is not a sighting and stores no report of its
    /// own — it says the system has been looked at and is empty, which retires whatever was
    /// standing for that system.
    /// </summary>
    public enum IntelKind { Sighting, Clear }

    /// <summary>Words reporters use for "I looked, there is nobody here". Deliberately does NOT
    /// include "nv" (no visual), which means the opposite — the reporter could not see, so it
    /// says nothing about whether anyone is still there.</summary>
    private static readonly HashSet<string> ClearWords =
        new(StringComparer.OrdinalIgnoreCase) { "clr", "clear", "cleared", "empty" };

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
        "were", "glimpse", "sat", "issues", "where", "nay", "ZD1",
        "entered", "well", "wel", "pipe", "update", "of",

        // Ships, structures and groups named in passing
        "destroyer", "keepstar", "tuskers", "prob", "sabe", "nano", "prot", "grid",
        "W-I", "88A", "yorb",
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

    /// <summary>A pilot named on a line, with the hull they were called in if one was given.</summary>
    public sealed record SightedPilot(string Name, string? Ship);

    public sealed record ParsedIntel(
        IntelKind                    Kind,
        string                       SystemName,
        int                          PlayerCount,
        IReadOnlyList<SightedPilot>  Pilots,
        bool                         NoVisual,
        string                       Note);

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
                          .Select(t => new Token(t, Clean(t)))
                          .Where(t => t.Clean.Length > 0)
                          .ToArray())
            .Where(c => c.Length > 0)];

    /// <summary>
    /// Every token run that could be a character name, for the caller to resolve in one batch.
    /// Runs that already match a system are skipped: a system name is never also asked about as
    /// a character, which is what keeps "C-FD0D Kerno C-FD0D" from being resolved twice.
    /// </summary>
    public static IReadOnlyList<string> NameCandidates(string message, Func<string, bool> isSystem)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in Chunks(message))
            for (var start = 0; start < chunk.Length; start++)
                for (var len = Math.Min(MaxNameTokens, chunk.Length - start); len >= 1; len--)
                {
                    var run = string.Join(' ', chunk.Skip(start).Take(len).Select(t => t.Clean));
                    if (run.Length is 0 or > 37)      continue;   // EVE's own name limit
                    if (IsStopRun(run))              continue;   // never asked about at all
                    if (PlusValue(run) is not null)   continue;
                    if (isSystem(run))                continue;
                    seen.Add(run);
                }

        return [.. seen];
    }

    /// <summary>
    /// Parses one message, or returns null when it is not a usable sighting.
    ///
    /// Requires a system AND either a named character or a count — a system on its own is a
    /// "clr" line, and a name with no system cannot be placed on the map.
    /// </summary>
    public static ParsedIntel? Parse(
        string message, Func<string, bool> isSystem, Func<string, bool> isCharacter,
        Func<string, bool>? isShip = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        // A question is a request for intel, not a report of it — "C-FD0D* Update?" asks whether
        // anyone has eyes on a system, and reads to the parser exactly like a sighting with no
        // one in it. Only a count rescues it: "ZD1-Z2 +3?" is someone unsure of the number, and
        // that is still a sighting.
        var isQuestion = message.TrimEnd().EndsWith('?');

        var chunks = Chunks(message);
        if (chunks.Count == 0) return null;

        isShip ??= _ => false;

        string? system = null;
        var pilots = new List<SightedPilot>();
        var names  = new List<string>();      // parallel, so the count logic reads unchanged
        var ships  = new List<string?>();
        var note   = new List<string>();
        var plus   = 0;

        // A hull named before any pilot — "Loki  Sevra", or a bare list of hulls — waits for the
        // next pilot to attach to.
        string? pendingShip = null;

        var sawClearWord = false;
        var noVisual     = false;

        foreach (var chunk in chunks)
        {
            var i = 0;
            while (i < chunk.Length)
            {
                // A count binds to nothing in particular — reporters put it before the system,
                // after the names, or on its own — so it is simply summed wherever it appears.
                if (PlusValue(chunk[i].Clean) is { } n) { plus += n; i++; continue; }

                // Control words are consumed before any name matching, because several of them
                // are also real character names: "clr" is an actual pilot, and resolving it once
                // put it in the shared name cache, after which every "SYSTEM clr" was read as a
                // sighting of somebody called clr rather than as a system being called clear.
                // A reporter typing one of these means the word, not the person.
                if (ClearWords.Contains(chunk[i].Clean))    { sawClearWord = true; i++; continue; }
                if (NoVisualWords.Contains(chunk[i].Clean)) { noVisual     = true; i++; continue; }

                var matched = false;

                // Longest run first, so "Zulu Delulu" wins over "Zulu", and a multi-word system
                // such as "New Caldari" is not read as its first word alone. Hulls can be three
                // words too — "Scythe Fleet Issue".
                for (var len = Math.Min(MaxNameTokens, chunk.Length - i); len >= 1 && !matched; len--)
                {
                    var run = string.Join(' ', chunk.Skip(i).Take(len).Select(t => t.Clean));

                    // System first: only the first one counts. A later match is a character whose
                    // name happens to be a system, or a second system mentioned in passing — the
                    // reporter's own system is the one they led with.
                    if (system is null && isSystem(run))
                    {
                        system  = run;
                        i      += len;
                        matched = true;
                    }

                    // Hull BEFORE character, which is the opposite of what it looks like it
                    // should be. 232 of the 423 published hulls are also somebody's character
                    // name — Loki, Sabre, Heron, Astero, Buzzard — so checking characters first
                    // reads every ship report as a pilot sighting. That inflated counts and, far
                    // worse, chained the supersede logic across unrelated systems: a "Loki" here
                    // retiring a "Loki" there. Ships are a closed set of 423 names and intel
                    // channels are full of them, whereas a pilot genuinely named after a hull is
                    // rare — so the cheaper mistake is to read that pilot as a ship.
                    else if (isShip(run))
                    {
                        if (pilots.Count > 0 && ships[^1] is null)
                        {
                            ships[^1]  = run;                       // "Sevra (Loki)"
                            pilots[^1] = pilots[^1] with { Ship = run };
                        }
                        else pendingShip ??= run;                   // "Loki  Sevra"

                        i      += len;
                        matched = true;
                    }
                    else if (!IsStopRun(run) && isCharacter(run))
                    {
                        names.Add(run);
                        ships.Add(pendingShip);
                        pilots.Add(new SightedPilot(run, pendingShip));
                        pendingShip = null;
                        i      += len;
                        matched = true;
                    }
                }

                // Nothing matched at this position: drop the token to the note and move on, which
                // is what turns "Naga, Flycatcher, Malediction" and "nv" into free text.
                if (!matched) { note.Add(chunk[i].Raw); i++; }
            }
        }

        if (system is null) return null;

        // Asked, not reported. Checked before the count so a question naming a pilot — "ZD1-Z2
        // Sevra?" — is dropped too; that is somebody wondering whether Sevra is still there.
        if (isQuestion && plus == 0) return null;

        // A named pilot counts as one; "+3" means three more on top of whoever was named, and on
        // its own means three unnamed.
        var count = names.Count + plus;

        // "ZD1-Z2 clr" — a system, nobody in it, and the word for it. Reported rather than
        // dropped so the caller can retire the standing sightings for that system: somebody has
        // looked, and whoever was there has gone.
        if (count == 0)
            return sawClearWord
                ? new ParsedIntel(IntelKind.Clear, system, 0, [], noVisual, string.Join(' ', note).Trim())
                : null;

        return new ParsedIntel(
            IntelKind.Sighting, system, count, pilots, noVisual, string.Join(' ', note).Trim());
    }
}
