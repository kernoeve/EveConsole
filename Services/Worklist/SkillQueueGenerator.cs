using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Characters whose skill queue needs attention.
///
/// <para>The same three checks the Overview alerts make — empty, paused, or running dry inside the
/// configured window — and deliberately the same settings, so a check turned off there is off
/// here too. Two places disagreeing about whether a queue is a problem would be worse than either
/// of them alone.</para>
///
/// <para>They belong on the worklist because they are work: a queue is fixed by logging that
/// character in and adding skills, which is precisely what this list is for. An alert says a
/// thing is true; a task says who has to do something about it, and these already know whose
/// queue it is.</para>
///
/// <para>Every authorised character is checked, not only the ones set up to run industry. Training
/// is not an industry activity, and the alt whose queue lapsed unnoticed is usually the one nobody
/// has a job for.</para>
///
/// <para>The exception is a character whose Skill queue box is cleared on the Industry tab. Some
/// alts are not meant to be training — there is nothing left they need — and a permanently
/// empty queue reporting itself every refresh is noise that teaches the reader to skip the
/// section.</para>
/// </summary>
public class SkillQueueGenerator(IDbContextFactory<AppDbContext> dbFactory) : IWorklistGenerator
{
    public string Id          => "skill_queue";
    public string DisplayName => "Skill Queues";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var alerts = await db.AlertSettings.AsNoTracking().FirstOrDefaultAsync(ct)
                     ?? new Models.AlertSettings();

        if (!alerts.SkillQueueEmpty && !alerts.SkillQueuePaused && !alerts.SkillQueueEmptyInDays)
            return [];

        // Characters whose queue is deliberately not kept running — a hauler, a cyno alt, a
        // market alt with nothing left worth training. Cleared on the Industry tab.
        //
        // ⚠️ Only rows that exist AND say false are silenced. A character with no row here is
        // "not configured", not "silence me", and must still be checked; reading the flag as a
        // plain lookup with a false default would mute every character who has never been given
        // an industry setting.
        var muted = (await db.WorklistIndyChars.AsNoTracking()
                .Where(c => !c.SkillQueue)
                .Select(c => c.CharacterId)
                .ToListAsync(ct))
            .ToHashSet();

        var characters = (await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct))
            .Where(c => !muted.Contains(c.Id))
            .ToList();
        if (characters.Count == 0) return [];

        var charIds = characters.Select(c => c.Id).ToList();

        // One query for every queue, grouped here. Eighteen characters is eighteen round trips
        // the database can answer once.
        var queues = (await db.EsiSkillQueue.AsNoTracking()
                .Where(q => charIds.Contains(q.CharacterId) && q.QueuePosition >= 0)
                .Select(q => new { q.CharacterId, q.FinishDate })
                .ToListAsync(ct))
            .GroupBy(q => q.CharacterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.FinishDate).ToList());

        var now      = DateTimeOffset.UtcNow;
        var warnDays = Math.Max(1, alerts.SkillQueueEmptyDays);
        var cutoff   = now.AddDays(warnDays);

        var items = new List<WorklistItem>();

        foreach (var ch in characters.OrderBy(c => c.Name))
        {
            var queue = queues.GetValueOrDefault(ch.Id) ?? [];

            // Empty and paused are the same wound at different depths, so an empty queue is not
            // also reported as paused — matching how the alerts read.
            if (queue.Count == 0)
            {
                if (alerts.SkillQueueEmpty)
                    items.Add(Row(ch.Id, ch.Name, "empty",
                        "Skill queue is empty",
                        "Nothing is training. Every minute is skill points not earned.",
                        WorklistPriority.Missing));
                continue;
            }

            if (alerts.SkillQueuePaused && !queue.Any(f => f is { } d && d > now))
            {
                items.Add(Row(ch.Id, ch.Name, "paused",
                    "Skill queue is paused",
                    "The queue has skills in it but none is training.",
                    WorklistPriority.Missing));
                continue;
            }

            if (!alerts.SkillQueueEmptyInDays) continue;

            var ends = queue.Where(f => f.HasValue).Select(f => f!.Value)
                            .DefaultIfEmpty().Max();
            if (ends == default || ends > cutoff) continue;

            var left = ends - now;
            var when = left.TotalDays >= 1
                ? $"{(int)left.TotalDays}d {left.Hours}h"
                : $"{left.Hours}h {left.Minutes}m";

            items.Add(Row(ch.Id, ch.Name, "ending",
                $"Skill queue ends in {when}",
                $"Runs dry {ends.ToLocalTime():d MMM HH:mm}, inside the {warnDays}-day warning.",
                // Housekeeping rather than Missing: the queue is still training, and a week's
                // notice is not the same kind of problem as one that has already stopped.
                WorklistPriority.Housekeeping));
        }

        return items;
    }

    /// <param name="kind">Part of the key, so the three checks snooze independently — silencing
    /// "ends soon" for a day should not also silence the queue actually running out.</param>
    private WorklistItem Row(long charId, string charName, string kind,
                             string title, string detail, int priority) =>
        new()
        {
            Key           = $"skill_queue:{kind}:{charId}",
            Source        = Id,
            // Its own kind. Filed under Corp Project at first, on the reasoning that the column
            // was about material and a handful of rows did not justify widening it — but that put
            // training under a heading it has nothing to do with, and the column is read to decide
            // what sort of session this is. A queue is fixed by logging a character in, which is
            // not what any corp project asks for.
            Kind          = WorklistKind.SkillQueue,
            Title         = $"{charName} — {title}",
            Detail        = detail,
            Readiness     = WorklistReadiness.Ready,
            CharacterId   = charId,
            CharacterName = charName,
            Priority      = priority,
        };
}
