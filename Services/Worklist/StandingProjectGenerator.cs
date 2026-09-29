using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using EveConsole.Localization;

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
    public string DisplayName => WorklistText.SourceStandingProjects;

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
            var corpName = corpNames.GetValueOrDefault(corpId, string.Format(WorklistText.CorpWithId, corpId));

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
                    BlockedBy     = blocked ? WorklistText.BlockedNoCharacterForCorp : "",
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
        // ⚠️ By the stored type, never TypeDisplay: that is translated, and a delivery read as
        // "Deliver Item" would have been taken for destroy-NPC in any other language.
        var deliver = r.ProjectType == StandingProjectReport.DeliverItem;

        // ⚠️ Two scopes reach here as different shapes, and the difference matters to the reader.
        // A definition naming one system carries it in TargetDisplay with no dest. An ADM rule
        // carries the RULE in TargetDisplay and one row per qualifying system in DestDisplay —
        // so a system can appear because somebody chose it, or because its ADM dropped under a
        // threshold, and only the second kind goes away again when the ADM recovers.
        var byRule = r.DestDisplay.Length > 0;

        // ⚠️ The row's names are English — the same row feeds the posted report — so its SDE
        // names are put in the interface language here, where they become the task's text, the
        // way the Corp Activity grid words the same row: the item of a delivery, and a system,
        // which a row names by id in ExpandedSystemId. An ADM or alliance rule's label is a
        // sentence around a region or constellation the row carries no id for, so it comes
        // worded already, in TargetShown, by CorpActivityService, which has the id. A delivery's
        // destination is a station or a structure, by id in StationId: an NPC station is named as
        // the screen names it, and a structure as its owner named it.
        var target = r.TargetShown.Length > 0 ? r.TargetShown
                   : r.ItemTypeId is int item ? SdeNames.Type(item, r.TargetDisplay)
                   : !byRule && r.ExpandedSystemId is int named ? SdeNames.SolarSystem(named, r.TargetDisplay)
                   : r.TargetDisplay;
        var dest   = deliver && r.StationId is long station ? SdeNames.Location(station, r.DestDisplay)
                   : !deliver && byRule && r.ExpandedSystemId is int system ? SdeNames.SolarSystem(system, r.DestDisplay)
                   : r.DestDisplay;
        var place  = byRule ? dest : target;

        return r.MatchStatus switch
        {
            "not_active" when deliver => (
                r.DestDisplay.Length > 0
                    ? string.Format(WorklistText.ProjectDeliverTo, r.TypeDisplay, target, dest)
                    : string.Format(WorklistText.ProjectDeliverToAnyOffice, r.TypeDisplay, target),
                WorklistText.ProjectNotActive),

            "not_active" => (
                $"{r.TypeDisplay} — {place}"
              + (byRule ? $" — {target}" : ""),
                byRule ? WorklistText.ProjectNotActiveByRule : WorklistText.ProjectNotActiveNamed),

            // ⚠️ Not a create. There is nothing to create a project against, and saying "create"
            // would send somebody to try. Three separate reasons reach here and they want three
            // different answers — one is a fault, one is a misconfiguration, one is good news.
            "no_adm" => (
                string.Format(WorklistText.ProjectCheckAdm, r.TypeDisplay, target),
                WorklistText.ProjectCheckAdmDetail),

            "no_systems" => (
                string.Format(WorklistText.ProjectCheckScope, r.TypeDisplay, target),
                WorklistText.ProjectCheckScopeDetail),

            "all_healthy" => (
                string.Format(WorklistText.ProjectNothingToRaise, r.TypeDisplay, target),
                WorklistText.ProjectNothingToRaiseDetail),


            _ => (null, ""),
        };
    }
}
