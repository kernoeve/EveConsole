using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>
/// How a standing project reads as one line, and how a set of them reads as a block of text.
///
/// <para>⚠️ The ONE definition of that line. The Overview panel builds it for its single-column
/// grid and a scheduled post prints it; a second copy would drift the first time either changed.</para>
///
/// <para>⚠️ Works on EXPANDED rows. One definition scoped to a region or constellation becomes a
/// row per qualifying system, and that expansion is the point of the report — it is what says
/// which systems actually need something done. Choosing which projects to report on is a separate
/// question, asked against the definitions.</para>
/// </summary>
public static class StandingProjectReport
{
    public const string DeliverItem = "deliver_item";
    public const string DestroyNpc  = "destroy_npc";

    /// <summary>
    /// The whole row in one line, WITH the project type named.
    ///
    /// <para>For the Overview panel, whose one column mixes both types and has no heading to say
    /// which is which. A report is all one type and says so in its title, so it uses Describe.</para>
    /// </summary>
    public static string Summary(StandingProjectGridRow row) =>
        string.Format(CorpText.StandingProjectSummary, row.TypeDisplay, Describe(row));

    /// <summary>
    /// The row without the type prefix.
    ///
    /// <para>The shape follows the type, because the types are identified by different things: a
    /// delivery is an item and where it goes, and a destroy-NPC project is just a place.</para>
    /// </summary>
    public static string Describe(StandingProjectGridRow row) =>
        row.ItemTypeId is > 0
            ? row.TargetDisplay + (row.DestDisplay.Length > 0 ? " → " + row.DestDisplay : "")
            // A destroy-NPC row names the qualifying system in DestDisplay when an ADM rule
            // selected it, and in TargetDisplay when the definition named it outright.
            : row.DestDisplay.Length > 0 ? row.DestDisplay : row.TargetDisplay;

    /// <summary>
    /// What a row's match status says in plain words.
    ///
    /// <para>Title case, matching the column headers above it. The shouted NO PROJECT this
    /// replaced was carrying the emphasis a whole column of statuses cannot all have.</para>
    /// </summary>
    public static string Status(StandingProjectGridRow row) => row.MatchStatus switch
    {
        "matched"     => CorpText.ReportStatusActive,
        "all_healthy" => CorpText.ReportStatusAllHealthy,
        "not_active"  => CorpText.ReportStatusNoProject,
        "no_systems"  => CorpText.ReportStatusNoSystems,
        "no_office"   => CorpText.ReportStatusNoOffice,
        "no_adm"      => CorpText.ReportStatusAdmUnavailable,
        _             => row.MatchStatus,
    };

    /// <summary>
    /// A set of expanded rows as a fenced block.
    ///
    /// <para>Columns are space-padded inside a code fence for the same reason the monthly summary
    /// is: Slack renders proportionally, so nothing but a monospace block lines up.</para>
    ///
    /// <para>⚠️ The shape follows the project type, because the two are identified by different
    /// things. A destroy-NPC row is a place, so it gets Region, System and the system's ADM. A
    /// delivery is an item and where it goes, and has no system of its own to put in a column.</para>
    /// </summary>
    public static string Export(
        IReadOnlyList<StandingProjectGridRow> rows,
        string heading,
        string projectType      = DestroyNpc,
        bool   showHeaders      = false,
        bool   showIskLeft      = true,
        bool   showLastCompleted = true)
    {
        if (rows.Count == 0) return "";

        var byPlace = projectType == DestroyNpc;

        // Region then system, so the list reads as a tour of the map rather than in whatever order
        // the scopes happened to expand. Rows with no system sort last rather than first, since an
        // empty string would otherwise head the list.
        var ordered = byPlace
            ? [.. rows.OrderBy(r => r.RegionName.Length == 0)
                      .ThenBy(r => r.RegionName, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(r => r.SystemName, StringComparer.OrdinalIgnoreCase)]
            : rows.OrderBy(Describe, StringComparer.OrdinalIgnoreCase).ToList();

        // ⚠️ Header, cell and alignment defined together, so a column cannot be switched off or
        // moved in one place and left behind in another. The optional ones are simply not added.
        //
        // Right is for the columns that hold a quantity: digits line up on their last character,
        // so magnitudes are comparable down the column instead of every number starting in the
        // same place and ending wherever it happens to.
        var cols = new List<(string Header, Func<StandingProjectGridRow, string> Cell, bool Right)>();

        if (byPlace)
        {
            cols.Add((CorpText.ColRegion, r => r.RegionName, false));
            // A named system that never expanded still has a name on the row; fall back to it
            // rather than printing a blank where the place should be.
            cols.Add((CorpText.ColSystem, r => r.SystemName.Length > 0 ? r.SystemName : Describe(r), false));
            // WARN One decimal. ADM moves in tenths and never carries a second: 268,305
            // stored readings hold nothing but clean tenths from 1.0 to 6.0, and the game
            // and dotlan both show it that way. F2 was printing a digit that is always zero.
            cols.Add(("ADM",    r => r.Adm is { } a ? a.ToString("F1") : "", false));
        }
        else
        {
            // No "Deliver Item:" prefix — the whole table is one type and the title says which.
            cols.Add((CorpText.ReportColProject, Describe, false));
        }

        cols.Add((CorpText.ReportColStatus,    Status, false));

        // Remaining is the count still to do; ISK Left is what that count is worth at the
        // project's reward per contribution. One answers "how much work", the other "how much is
        // in it", and neither implies the other.
        cols.Add((CorpText.ReportColRemaining, r => r.RemainingText, true));
        if (showIskLeft) cols.Add((CorpText.ReportColIskLeft, r => r.RemainingPayoutText, true));

        cols.Add(("%", r => r.RemainingPercentText, true));
        if (showLastCompleted) cols.Add((CorpText.ReportColLastCompleted, LastDone, false));

        var headers = cols.Select(c => c.Header).ToArray();
        var aligns  = cols.Select(c => c.Right).ToArray();
        var cells   = ordered.Select(r => cols.Select(c => c.Cell(r)).ToArray()).ToList();

        var columns = cols.Count;
        var widths  = new int[columns];

        // ⚠️ Headers are measured into the widths only when they are being printed. Sized in
        // regardless, a hidden "Remaining" header would pad a column of three-digit numbers to
        // nine characters and the block would look wrong for a heading nobody asked for.
        if (showHeaders)
            for (var i = 0; i < columns; i++) widths[i] = MonoColumns.Width(headers[i]);

        foreach (var c in cells)
            for (var i = 0; i < columns; i++)
                widths[i] = Math.Max(widths[i], MonoColumns.Width(c[i]));

        var sb = new StringBuilder();

        // ⚠️ An empty heading prints NOTHING, not an empty bold line. The editor materialises the
        // default into its box, so blank is a choice somebody made rather than a field they left
        // alone — and a stray blank line above a table reads as a rendering fault.
        if (heading.Trim().Length > 0) sb.AppendLine($"*{heading.Trim()}*");

        sb.AppendLine("```");

        // Only with rows to head. Export has already returned on an empty set, so reaching here
        // means there is something for the headers to describe.
        if (showHeaders)
        {
            sb.AppendLine(Row(headers, widths, aligns));

            // A rule under them, each dash run as wide as its own column, so the break lines up
            // with the columns rather than running the width of the widest line.
            sb.AppendLine(Row([.. widths.Select(w => new string('-', w))], widths, aligns));
        }

        foreach (var c in cells) sb.AppendLine(Row(c, widths, aligns));

        sb.AppendLine("```");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// When a project matching this line was last completed, as a date.
    ///
    /// <para>The date rather than a count of days, because the column is read beside a status
    /// that says whether anything is running NOW — "gone since the 3rd" answers both how long
    /// and when, where a bare "12 days" answers only the first.</para>
    ///
    /// <para>⚠️ Blank while a project IS running. The column exists to say how long a gap has
    /// been open, and on a covered line there is no gap — printing a date there would invite
    /// reading it as when the current project started.</para>
    /// </summary>
    private static string LastDone(StandingProjectGridRow row) =>
        row.MatchStatus != "matched" && row.LastDone is { } d
            ? d.UtcDateTime.ToString("yyyy-MM-dd")
            : "";

    /// <summary>
    /// One padded line.
    ///
    /// <para>A right-aligned cell is padded on the left to its column width; the gutter is then
    /// added after it, so the two spaces between columns are the same wherever the text sits.</para>
    ///
    /// <para>⚠️ The last populated cell keeps its alignment but takes no gutter, so no line ends
    /// in whitespace inside the fence. Right-aligning it adds LEADING spaces, which is why that
    /// stays safe.</para>
    /// </summary>
    private static string Row(string[] cells, int[] widths, bool[] right)
    {
        var last = cells.Length - 1;
        while (last > 0 && string.IsNullOrEmpty(cells[last])) last--;

        var line = new StringBuilder();
        for (var i = 0; i <= last; i++)
        {
            var cell = right[i] ? MonoColumns.PadLeft(cells[i], widths[i]) : cells[i];
            line.Append(i == last ? cell : MonoColumns.PadRight(cell, widths[i] + 2));
        }

        return line.ToString();
    }

    /// <summary>
    /// Whether a row belongs in a report filtered to <paramref name="filter"/>.
    ///
    /// <para>"Missing" is defined by what it EXCLUDES: a row is uninteresting only when an active
    /// project is covering it, or when the scope expanded and every system in it is healthy. Those
    /// are the two states that mean nothing to do.</para>
    ///
    /// <para>⚠️ Everything else stays in, including the rows that could not be judged — no
    /// office, no ADM reading, a scope that expanded to nothing. A list of what needs attention
    /// that quietly drops the ones it could not check is worse than one that admits them.</para>
    /// </summary>
    public static bool Wanted(StandingProjectGridRow row, string filter)
    {
        if (filter == ProjectFilters.All) return true;

        var covered = row.MatchStatus is "matched" or "all_healthy";
        if (!covered) return true;

        // Nearly finished, so it is about to need replacing. The same under-10% threshold the
        // grid already flags.
        return filter == ProjectFilters.MissingAndLow
            && row.MatchStatus == "matched"
            && row.RemainingPercentValue >= 0
            && row.RemainingPercentValue < 10.0;
    }

    /// <summary>How a definition reads in the picker: one line per definition, unexpanded.</summary>
    public static string Describe(Models.CorpStandingProject p) =>
        DescribeWith(p, p.ItemTypeName, p.SolarSystemName, p.ScopeEntityName, p.StationName);

    /// <summary>
    /// <see cref="Describe(Models.CorpStandingProject)"/> as a screen shows it: the item, the
    /// system, a rule's region or constellation, and a delivery's NPC station in the interface
    /// language. A player structure and an alliance belong to players and have no other name.
    /// ⚠️ For showing only — a picker keeps the definition by its id.
    /// </summary>
    public static string DescribeShown(Models.CorpStandingProject p) =>
        DescribeWith(p,
            p.ItemTypeId    is int item   ? SdeNames.Type(item, p.ItemTypeName)             : p.ItemTypeName,
            p.SolarSystemId is int system ? SdeNames.SolarSystem(system, p.SolarSystemName) : p.SolarSystemName,
            p.ScopeEntityId is int scope
                ? p.ScopeType switch
                  {
                      "region_adm"        => SdeNames.Region(scope, p.ScopeEntityName),
                      "constellation_adm" => SdeNames.Constellation(scope, p.ScopeEntityName),
                      _                   => p.ScopeEntityName,
                  }
                : p.ScopeEntityName,
            p.StationId is long station && p.StationName.Length > 0
                ? SdeNames.Location(station, p.StationName)
                : p.StationName);

    /// <summary>The one line, with the names it is given.</summary>
    private static string DescribeWith(Models.CorpStandingProject p, string item, string system, string scope,
                                       string station) =>
        p.ProjectType == DeliverItem
            ? $"{item}{(station.Length > 0 ? " → " + station : "")}"
            : p.ScopeType switch
            {
                // ⚠️ Named by its RULE, not by the systems it currently picks. That set changes
                // with sovereignty, and a picker that renamed itself every time ADM moved would
                // be unrecognisable from one week to the next.
                "region_adm"        => string.Format(CorpText.PickerScopeRegionAdm, scope, p.MinAdm ?? 0),
                "constellation_adm" => string.Format(CorpText.PickerScopeConstellationAdm, scope, p.MinAdm ?? 0),
                "alliance_sov"      => string.Format(CorpText.PickerScopeAllianceSov, scope, p.MinAdm ?? 0),
                _                   => system,
            };

    /// <summary>
    /// The heading a section writes for itself when nobody has retitled it, in this run's
    /// interface language.
    ///
    /// <para>Shared with the editor, which shows it as the placeholder in the title box — so
    /// what the box promises and what the post prints cannot drift.</para>
    ///
    /// <para>One whole title per type and filter. It used to be put together from a type label,
    /// " projects" and the filter's name in lower case, and no other language orders or cases
    /// those pieces the way English does.</para>
    /// </summary>
    public static string DefaultTitle(string projectType, string filter) =>
        DefaultTitle(projectType, filter, null);

    /// <summary>
    /// Whether <paramref name="title"/> is the default heading for this type and filter in ANY
    /// of the interface languages.
    ///
    /// <para>⚠️ Not only this run's. The editor fills the default into the title box and the
    /// section saves it as it stands, so a section written in English and reopened in Chinese
    /// holds the English default — and it has to go on following its type and filter rather
    /// than being taken for a title somebody typed.</para>
    /// </summary>
    public static bool IsDefaultTitle(string title, string projectType, string filter) =>
        title == DefaultTitle(projectType, filter)
        || Languages.All.Any(l => title == DefaultTitle(projectType, filter, l.Culture));

    /// <summary>The default heading in <paramref name="language"/>; null is this run's.</summary>
    private static string DefaultTitle(string projectType, string filter, CultureInfo? language)
    {
        var deliver = projectType == DeliverItem;

        // Anything that is not one of the two narrowing filters is titled as All. The editor, the
        // only caller, never passes anything else: it maps a filter it does not know to All.
        var key = filter switch
        {
            ProjectFilters.Missing =>
                deliver ? nameof(CorpText.ProjectsTitleDeliverMissing) : nameof(CorpText.ProjectsTitleDestroyMissing),
            ProjectFilters.MissingAndLow =>
                deliver ? nameof(CorpText.ProjectsTitleDeliverMissingLow) : nameof(CorpText.ProjectsTitleDestroyMissingLow),
            _ =>
                deliver ? nameof(CorpText.ProjectsTitleDeliverAll) : nameof(CorpText.ProjectsTitleDestroyAll),
        };

        // Looked up by name so any language can be asked for, not only this run's.
        return CorpText.ResourceManager.GetString(key, language) ?? key;
    }
}
