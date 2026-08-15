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
                var (verb, detail) = Diagnose(r);
                if (verb is null) continue;

                items.Add(new WorklistItem
                {
                    // The definition's own id: stable across refreshes, and unique per corp
                    // already, so nothing else needs to go into the key.
                    Key           = $"standing_project:{r.DbId}",
                    Source        = Id,
                    Kind          = WorklistKind.CorpProject,
                    Title         = $"{verb} — {r.TypeDisplay}",
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
    /// What is wrong with one standing definition. Null verb when it is running as intended.
    ///
    /// "no_systems" is kept distinct from "not_active" rather than collapsed into one "missing":
    /// the first cannot be fixed by creating a project, because the scope currently resolves to
    /// nowhere, and telling someone to create a project they cannot create wastes the trip.
    /// </summary>
    private static (string? Verb, string Detail) Diagnose(StandingProjectGridRow r) => r.MatchStatus switch
    {
        "not_active" => ("Create",
                         $"No active project matches this definition. Target {r.TargetDisplay}"
                         + (r.DestDisplay.Length > 0 ? $" to {r.DestDisplay}" : "") + "."),

        "no_systems" => ("Check scope",
                         $"Scope resolves to no systems, so nothing can be created for it. "
                         + $"Target {r.TargetDisplay}."),

        _            => (null, ""),
    };
}
