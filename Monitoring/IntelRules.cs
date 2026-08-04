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

    /// <summary>A pilot named on a line, with the hull they were called in if one was given.</summary>
    public sealed record SightedPilot(string Name, string? Ship);

    public sealed record ParsedIntel(
        IntelKind                    Kind,
        string                       SystemName,
        int                          PlayerCount,
        IReadOnlyList<SightedPilot>  Pilots,
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

        foreach (var chunk in chunks)
        {
            var i = 0;
            while (i < chunk.Length)
            {
                // A count binds to nothing in particular — reporters put it before the system,
                // after the names, or on its own — so it is simply summed wherever it appears.
                if (PlusValue(chunk[i].Clean) is { } n) { plus += n; i++; continue; }

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
                    else if (isCharacter(run))
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
                if (!matched)
                {
                    if (ClearWords.Contains(chunk[i].Clean)) sawClearWord = true;
                    note.Add(chunk[i].Raw);
                    i++;
                }
            }
        }

        if (system is null) return null;

        // A named pilot counts as one; "+3" means three more on top of whoever was named, and on
        // its own means three unnamed.
        var count = names.Count + plus;

        // "ZD1-Z2 clr" — a system, nobody in it, and the word for it. Reported rather than
        // dropped so the caller can retire the standing sightings for that system: somebody has
        // looked, and whoever was there has gone.
        if (count == 0)
            return sawClearWord
                ? new ParsedIntel(IntelKind.Clear, system, 0, [], string.Join(' ', note).Trim())
                : null;

        return new ParsedIntel(
            IntelKind.Sighting, system, count, pilots, string.Join(' ', note).Trim());
    }
}
