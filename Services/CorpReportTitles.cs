using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EveConsole.Services;

/// <summary>
/// What the corp reports call their sections, and what a reader has renamed them to.
///
/// <para>⚠️ The ONE definition of both sets and of every heading. The settings tab overrides
/// these, the Corp Activity exports print them and the scheduler posts them — a section defined
/// twice would let one exist in a place the other never heard of.</para>
/// </summary>
public class CorpReportTitles(AppPreferencesService prefs)
{
    /// <summary>The Top 10 lists, in the order the exports print them.</summary>
    public static readonly (string Key, string Title)[] Top10Categories =
    [
        ("ratting",  "Ratting Tax"),
        ("mining",   "Mining — Reprocessed Value"),
        ("kills",    "Kills"),
        ("projects", "Project Contributors"),
        ("industry", "Industry Tax"),
    ];

    /// <summary>
    /// The monthly summary's sections, in the order Build emits them.
    ///
    /// <para>⚠️ The default titles here are what the report writes into its lines, so the two must
    /// agree exactly — a section renamed in one and not the other stops being overridable and
    /// nothing says why.</para>
    /// </summary>
    public static readonly (string Key, string Title)[] SummarySections =
    [
        ("income",   "Income"),
        ("expenses", "Expenses"),
        ("net",      "Net"),
        ("combat",   "Combat"),
        ("mining",   "Mining"),
        ("projects", "Corp Projects"),
        ("members",  "Members"),
    ];

    public const string Top10Group   = "top10";
    public const string SummaryGroup = "summary";

    private const string PrefixKey = "corp.summary.header_prefix";

    private static string PrefKey(string group, string key) => $"corp.{group}.title.{key}";

    private static string DefaultOf((string Key, string Title)[] set, string key) =>
        set.FirstOrDefault(c => c.Key == key).Title ?? key;

    public static string Top10Default(string key)   => DefaultOf(Top10Categories, key);
    public static string SummaryDefault(string key) => DefaultOf(SummarySections, key);

    /// <summary>What the reader has asked a section be called, or nothing.</summary>
    public string Override(string group, string key) => (prefs.Get(PrefKey(group, key)) ?? "").Trim();

    /// <summary>⚠️ Blank clears it rather than storing an empty heading. A section with no name at
    /// all is one nobody can identify, so blank means "use the built-in one".</summary>
    public Task SetOverrideAsync(string group, string key, string? title) =>
        prefs.SetAsync(PrefKey(group, key), string.IsNullOrWhiteSpace(title) ? null : title.Trim());

    private string Resolve(string group, string key, string fallback)
    {
        var custom = Override(group, key);
        return custom.Length > 0 ? custom : fallback;
    }

    public string Top10Title(string key)   => Resolve(Top10Group,   key, Top10Default(key));
    public string SummaryTitle(string key) => Resolve(SummaryGroup, key, SummaryDefault(key));

    /// <summary>
    /// A Top 10 heading with the month it covers.
    ///
    /// <para>Every list carries its own month, custom or built-in. In Slack each fenced list is
    /// its own box, so a single date line above them travels with none of them — and a list pasted
    /// somewhere on its own is worth nothing without the month it belongs to.</para>
    /// </summary>
    public string Top10TitleFor(string key, string monthLabel) =>
        monthLabel.Length > 0 ? $"{Top10Title(key)} - {monthLabel}" : Top10Title(key);

    /// <summary>Put in front of the summary's first line. Empty leaves it as it was.</summary>
    public string HeaderPrefix => (prefs.Get(PrefixKey) ?? "").Trim();

    public Task SetHeaderPrefixAsync(string? value) =>
        prefs.SetAsync(PrefixKey, string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    /// <summary>
    /// The summary section name for a built-in title.
    ///
    /// <para>⚠️ Looked up by the DEFAULT text rather than by key, because the report builds its
    /// lines from those words and does not carry keys through them. Anything unrecognised comes
    /// back untouched, so a section added to the report without being added here still prints.</para>
    /// </summary>
    public string SummaryTitleForDefault(string defaultTitle)
    {
        var match = SummarySections.FirstOrDefault(sec => sec.Title == defaultTitle);
        return match.Key is null ? defaultTitle : SummaryTitle(match.Key);
    }
}
