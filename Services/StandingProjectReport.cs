using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
        row.TypeDisplay + ": " + Describe(row);

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
        "matched"     => "Active",
        "all_healthy" => "All Healthy",
        "not_active"  => "No Project",
        "no_systems"  => "No Systems In Scope",
        "no_office"   => "No Office",
        "no_adm"      => "ADM Unavailable",
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
        string projectType   = DestroyNpc,
        bool   showHeaders   = false)
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

        // Remaining is the count still to do; ISK Left is what that count is still worth at the
        // project's reward per contribution. Both, because one answers "how much work" and the
        // other "how much is in it", and neither implies the other.
        string[] headers = byPlace
            ? ["Region", "System", "ADM", "Status", "Remaining", "ISK Left", "%", "Last Done"]
            : ["Project", "Status", "Remaining", "ISK Left", "%", "Last Done"];

        var cells = ordered.Select(r => byPlace
            ? new[]
              {
                  r.RegionName,
                  // A named system that never expanded still has a name on the row; fall back to
                  // it rather than printing a blank where the place should be.
                  r.SystemName.Length > 0 ? r.SystemName : Describe(r),
                  r.Adm is { } a ? a.ToString("F2") : "",
                  Status(r),
                  r.RemainingText,
                  r.RemainingPayoutText,
                  r.RemainingPercentText,
                  LastDone(r),
              }
            : new[]
              {
                  // No "Deliver Item:" prefix — the whole table is one type and the title
                  // already says which.
                  Describe(r), Status(r), r.RemainingText,
                  r.RemainingPayoutText, r.RemainingPercentText, LastDone(r),
              })
            .ToList();

        var columns = headers.Length;
        var widths  = new int[columns];

        // ⚠️ Headers are measured into the widths only when they are being printed. Sized in
        // regardless, a hidden "Remaining" header would pad a column of three-digit numbers to
        // nine characters and the block would look wrong for a heading nobody asked for.
        if (showHeaders)
            for (var i = 0; i < columns; i++) widths[i] = headers[i].Length;

        foreach (var c in cells)
            for (var i = 0; i < columns; i++)
                widths[i] = Math.Max(widths[i], c[i].Length);

        var sb = new StringBuilder();
        sb.AppendLine($"*{heading}*");
        sb.AppendLine("```");

        // Only with rows to head. Export has already returned on an empty set, so reaching here
        // means there is something for the headers to describe.
        if (showHeaders)
        {
            sb.AppendLine(Row(headers, widths));

            // A rule under them, each dash run as wide as its own column, so the break lines up
            // with the columns rather than running the width of the widest line.
            sb.AppendLine(Row([.. widths.Select(w => new string('-', w))], widths));
        }

        foreach (var c in cells) sb.AppendLine(Row(c, widths));

        sb.AppendLine("```");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// When a project matching this line was last completed, as a date.
    ///
    /// <para>The date rather than a count of days, because the column is read beside a status
    /// that says whether anything is running NOW — "gone since the 3rd" answers both how long
    /// and when, where a bare "12 days" answers only the first.</para>
    /// </summary>
    private static string LastDone(StandingProjectGridRow row) =>
        row.LastDone is { } d ? d.UtcDateTime.ToString("yyyy-MM-dd") : "";

    /// <summary>One padded line. The last populated cell is not padded, so no line ends in
    /// whitespace inside the fence.</summary>
    private static string Row(string[] cells, int[] widths)
    {
        var last = cells.Length - 1;
        while (last > 0 && string.IsNullOrEmpty(cells[last])) last--;

        var line = new StringBuilder();
        for (var i = 0; i <= last; i++)
            line.Append(i == last ? cells[i] : cells[i].PadRight(widths[i] + 2));

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
        p.ProjectType == DeliverItem
            ? $"{p.ItemTypeName}{(p.StationName.Length > 0 ? " → " + p.StationName : "")}"
            : p.ScopeType switch
            {
                // ⚠️ Named by its RULE, not by the systems it currently picks. That set changes
                // with sovereignty, and a picker that renamed itself every time ADM moved would
                // be unrecognisable from one week to the next.
                "region_adm"        => $"{p.ScopeEntityName} — region, ADM below {p.MinAdm ?? 0:0.##}",
                "constellation_adm" => $"{p.ScopeEntityName} — constellation, ADM below {p.MinAdm ?? 0:0.##}",
                "alliance_sov"      => $"{p.ScopeEntityName} — sov, ADM below {p.MinAdm ?? 0:0.##}",
                _                   => p.SolarSystemName,
            };

    public static string TypeLabel(string projectType) =>
        projectType == DeliverItem ? "Deliver item" : "Destroy NPC";

    /// <summary>
    /// The heading a section writes for itself when nobody has retitled it.
    ///
    /// <para>Shared with the editor, which shows it as the placeholder in the title box — so
    /// what the box promises and what the post prints cannot drift.</para>
    /// </summary>
    public static string DefaultTitle(string projectType, string filter)
    {
        var title = TypeLabel(projectType) + " projects";

        return filter == ProjectFilters.All
            ? title
            : title + " — " + ProjectFilters.Label(filter).ToLowerInvariant();
    }
}
