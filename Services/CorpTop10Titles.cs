using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EveConsole.Services;

/// <summary>
/// The five Top 10 lists, and what each is called.
///
/// <para>⚠️ The ONE definition of the set and of the headings. The scheduler's section picker, the
/// Corp Activity export and the settings tab that overrides them all read this — a list defined
/// twice would let a category exist in one place and not the other.</para>
/// </summary>
public class CorpTop10Titles(AppPreferencesService prefs)
{
    /// <summary>Key and built-in heading, in the order the exports print them.</summary>
    public static readonly (string Key, string Title)[] Categories =
    [
        ("ratting",  "Ratting Tax"),
        ("mining",   "Mining — Reprocessed Value"),
        ("kills",    "Kills"),
        ("projects", "Project Contributors"),
        ("industry", "Industry Tax"),
    ];

    public static string DefaultTitle(string key) =>
        Categories.FirstOrDefault(c => c.Key == key).Title ?? key;

    private static string PrefKey(string key) => $"corp.top10.title.{key}";

    /// <summary>What the reader has asked this list be called, or nothing.</summary>
    public string Override(string key) => (prefs.Get(PrefKey(key)) ?? "").Trim();

    /// <summary>⚠️ Blank clears it rather than storing an empty heading. A list with no name at
    /// all would be a table nobody could identify, so blank means "use the built-in one".</summary>
    public Task SetOverrideAsync(string key, string? title) =>
        prefs.SetAsync(PrefKey(key), string.IsNullOrWhiteSpace(title) ? null : title.Trim());

    /// <summary>The heading to print: the override where there is one, else the built-in.</summary>
    public string Title(string key)
    {
        var custom = Override(key);
        return custom.Length > 0 ? custom : DefaultTitle(key);
    }

    /// <summary>
    /// The heading with the month it covers.
    ///
    /// <para>Every list carries its own month, custom or built-in. In Slack each fenced list is
    /// its own box, so a single date line above them travels with none of them — and a Top 10
    /// pasted somewhere on its own is worth nothing without the month it belongs to.</para>
    /// </summary>
    public string TitleFor(string key, string monthLabel) =>
        monthLabel.Length > 0 ? $"{Title(key)} - {monthLabel}" : Title(key);
}
