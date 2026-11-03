using EveConsole.Data;
using EveConsole.Localization;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Item names as the screen shows them, back to their type ids — so a sentence a generator wrote
/// ("50,284 on hand, 15,437 recoverable from Compressed Lime Mykoserocin") can have each item it
/// names drawn with its picture.
///
/// <para>Built once in the background from every published type, in the language on screen, and
/// again when that changes. Until it is ready <see cref="Find"/> finds nothing, and a caller falls
/// back to the names it knows itself.</para>
///
/// <para>⚠️ Matched at word boundaries, longest name first: "Capital Armor Plates" is one item,
/// not "Capital" and something else. That needs spaces between words, so in Japanese, Korean and
/// Chinese text only the names a caller passes are found.</para>
/// </summary>
public static class TypeNameIndex
{
    private static volatile Dictionary<string, int>? _byName;
    private static int _longestWords;
    private static Task? _loading;
    private static IDbContextFactory<AppDbContext>? _factory;

    public static Task EnsureLoadedAsync(IDbContextFactory<AppDbContext> factory)
    {
        if (_factory is null)
        {
            _factory = factory;
            SdeNames.Changed += () => { _byName = null; _loading = null; _ = EnsureLoadedAsync(factory); };
        }
        return _loading ??= Task.Run(LoadAsync);
    }

    private static async Task LoadAsync()
    {
        try
        {
            await using var db = await _factory!.CreateDbContextAsync();
            var types = await db.SdeTypes.AsNoTracking()
                .Where(t => t.Published)
                .Select(t => new { t.TypeId, t.Name })
                .ToListAsync();

            var map = new Dictionary<string, int>(types.Count, StringComparer.Ordinal);
            var longest = 1;
            foreach (var t in types)
            {
                var shown = SdeNames.Type(t.TypeId, t.Name);
                // Single words are left out: "Tritanium" is worth a picture, but a one-word name
                // like "Mercury" or "Station" could as easily be ordinary text, and the item's own
                // row passes its own name anyway.
                if (shown.Length < 4 || !shown.Contains(' ')) continue;
                map.TryAdd(shown, t.TypeId);
                longest = Math.Max(longest, shown.Count(c => c == ' ') + 1);
            }
            _longestWords = longest;
            _byName = map;
        }
        catch { /* no pictures, then; the text is still right */ }
    }

    /// <summary>The item names in <paramref name="text"/> the index knows, as (start, length,
    /// type id), in order and not overlapping.</summary>
    public static List<(int Start, int Length, int TypeId)> Find(string text)
    {
        var found = new List<(int, int, int)>();
        if (_byName is not { } map || text.Length == 0) return found;

        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]) || (i > 0 && char.IsLetterOrDigit(text[i - 1]))) { i++; continue; }

            // Word ends from here, nearest first; the longest one that is a name wins.
            var ends = new List<int>();
            var j = i;
            for (var w = 0; w < _longestWords && j < text.Length; w++)
            {
                while (j < text.Length && text[j] != ' ') j++;
                // A name may end in a word's own letters but not take the comma or stop after it.
                var end = j;
                while (end > i && !char.IsLetterOrDigit(text[end - 1]) && text[end - 1] != ')') end--;
                ends.Add(end);
                j++;
            }
            var hit = ends.AsEnumerable().Reverse().FirstOrDefault(e => e > i && map.ContainsKey(text[i..e]));
            if (hit > 0)
            {
                found.Add((i, hit - i, map[text[i..hit]]));
                i = hit;
            }
            else i++;
        }
        return found;
    }
}
