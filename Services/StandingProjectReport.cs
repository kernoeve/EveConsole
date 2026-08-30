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
    /// The whole row in one line.
    ///
    /// <para>The shape follows the type, because the types are identified by different things: a
    /// delivery is an item and where it goes, and a destroy-NPC project is just a place.</para>
    /// </summary>
    public static string Summary(StandingProjectGridRow row) =>
        row.TypeDisplay + ": " + (row.ItemTypeId is > 0
            ? row.TargetDisplay + (row.DestDisplay.Length > 0 ? " → " + row.DestDisplay : "")
            // A destroy-NPC row names the qualifying system in DestDisplay when an ADM rule
            // selected it, and in TargetDisplay when the definition named it outright.
            : row.DestDisplay.Length > 0 ? row.DestDisplay : row.TargetDisplay);

    /// <summary>What a row's match status says in plain words.</summary>
    public static string Status(StandingProjectGridRow row) => row.MatchStatus switch
    {
        "matched"     => "active",
        "all_healthy" => "all healthy",
        "not_active"  => "NO PROJECT",
        "no_systems"  => "no systems in scope",
        "no_office"   => "no office",
        "no_adm"      => "ADM unavailable",
        _             => row.MatchStatus,
    };

    /// <summary>
    /// A set of expanded rows as a fenced block.
    ///
    /// <para>Columns are space-padded inside a code fence for the same reason the monthly summary
    /// is: Slack renders proportionally, so nothing but a monospace block lines up.</para>
    /// </summary>
    public static string Export(IReadOnlyList<StandingProjectGridRow> rows, string heading)
    {
        if (rows.Count == 0) return "";

        var cells = rows.Select(r => new[]
        {
            Summary(r),
            Status(r),
            r.RemainingText,
            r.RemainingPercentText,
        }).ToList();

        var widths = new int[4];
        for (var i = 0; i < 4; i++) widths[i] = cells.Max(c => c[i].Length);

        var sb = new StringBuilder();
        sb.AppendLine($"*{heading}*");
        sb.AppendLine("```");

        foreach (var c in cells)
        {
            // The last populated cell is not padded, so there is no trailing whitespace.
            var last = c.Length - 1;
            while (last > 0 && string.IsNullOrEmpty(c[last])) last--;

            var line = new StringBuilder();
            for (var i = 0; i <= last; i++)
                line.Append(i == last ? c[i] : c[i].PadRight(widths[i] + 2));

            sb.AppendLine(line.ToString());
        }

        sb.AppendLine("```");
        return sb.ToString().TrimEnd();
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
}
