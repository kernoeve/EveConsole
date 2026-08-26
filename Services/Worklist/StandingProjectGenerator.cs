using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Corp projects the player has said should always be running, that currently are not.
///
/// The same shape as the standing buy order generator, and thin for the same reason:
/// <see cref="CorpActivityService.BuildMaintainGridRowsAsync"/> already decides what counts as
/// missing, including the awkward parts — expanding ADM scopes into systems, and matching a
/// definition against the live project list. Re-deriving any of that would produce a second
/// opinion on a question the Corp Activity tool already answers.
///
/// Routing is by corporation rather than station: a standing project belongs to a corp, and
/// whoever can create projects for that corp is the one who can act on it.
/// </summary>
public class StandingProjectGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    CorpActivityService             corpActivity,
    WorklistCorpAltService          corpAlts) : IWorklistGenerator
{
    public string Id          => "standing_projects";
    public string DisplayName => "Standing Projects";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Only corporations that actually have definitions — asking the rest costs queries and
        // can only ever return nothing.
        var corpIds = await db.CorpStandingProjects.AsNoTracking()
            .Select(p => p.CorporationId)
            .Distinct()
            .ToListAsync(ct);
        if (corpIds.Count == 0) return [];

        var corpNames = await db.Corporations.AsNoTracking()
            .Where(c => corpIds.Contains(c.Id))
            .ToDictionaryAsync(c => (long)c.Id, c => c.Name, ct);

        var altMap = await corpAlts.GetByCorpAsync(ct);
        var items  = new List<WorklistItem>();

        foreach (var corpId in corpIds)
        {
            var rows = await corpActivity.BuildMaintainGridRowsAsync(corpId, ct);

            altMap.TryGetValue(corpId, out var alt);
            var blocked  = alt is null;
            var corpName = corpNames.GetValueOrDefault(corpId, $"Corp {corpId}");

            foreach (var r in rows)
            {
                var (title, detail) = Describe(r);
                if (title is null) continue;

                items.Add(new WorklistItem
                {
                    // The definition's own id: stable across refreshes, and unique per corp
                    // already, so nothing else needs to go into the key.
                    Key           = $"standing_project:{r.DbId}",
                    Source        = Id,
                    Kind          = WorklistKind.CorpProject,
                    Title         = title,
                    Detail        = $"{corpName} · {detail}",
                    Readiness     = blocked ? WorklistReadiness.Blocked : WorklistReadiness.Ready,
                    BlockedBy     = blocked ? "No character assigned to this corporation" : "",
                    CharacterId   = alt?.CharacterId   ?? 0,
                    CharacterName = alt?.CharacterName ?? "",
                    TypeId        = r.ItemTypeId ?? 0,
                    TypeName      = r.ItemTypeName,
                    Priority      = WorklistPriority.StandingProject,
                });
            }
        }

        return items;
    }

    /// <summary>
    /// What one standing definition is asking for. Null title when it is running as intended.
    ///
    /// <para>⚠️ The title names the project, not the act. Every row here is a project that does
    /// not exist, so "Create" was on all of them and distinguished nothing — it cost the width
    /// of a word on every line and left the actual project type, item and place to the detail.
    /// The one row that is NOT a create still says so, because the answer there is different:
    /// the scope resolves to nowhere, and no amount of creating fixes that.</para>
    ///
    /// <para>What follows the type varies by type, because the two kinds are identified by
    /// different things. A delivery is an item and a place to put it. A destroy-NPC project is
    /// a place and the scope that picked it — a system named directly, or one of many systems
    /// an ADM rule resolved to, which is worth saying because the second kind reappears as the
    /// ADM moves.</para>
    /// </summary>
    private static (string? Title, string Detail) Describe(StandingProjectGridRow r)
    {
        var deliver = r.TypeDisplay == "Deliver Item";

        // ADM-scoped rows carry the qualifying system in DestDisplay and the rule in
        // TargetDisplay; a directly named system carries it in TargetDisplay with no dest.
        var place = r.DestDisplay.Length > 0 ? r.DestDisplay : r.TargetDisplay;

        return r.MatchStatus switch
        {
            "not_active" when deliver => (
                $"{r.TypeDisplay} — {r.TargetDisplay} to "
              + (r.DestDisplay.Length > 0 ? r.DestDisplay : "any corp office"),
                "No active project matches this definition."),

            "not_active" => (
                $"{r.TypeDisplay} — {place} (system)",
                "No active project matches this definition."
              + (r.DestDisplay.Length > 0 ? $" Scope: {r.TargetDisplay}." : "")),

            // ⚠️ Not a create. The scope currently resolves to no systems, so there is nothing to
            // create a project against, and saying "create" would send somebody to try.
            "no_systems" => (
                $"Check scope — {r.TypeDisplay}: {r.TargetDisplay}",
                "Scope resolves to no systems, so nothing can be created for it."),

            _ => (null, ""),
        };
    }
}
