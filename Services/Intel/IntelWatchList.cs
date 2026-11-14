using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>How the watch list marks a pilot, or a row of them: bold, dimmed, or neither.</summary>
public enum IntelWatchMark { None, Always, Ignored }

/// <summary>
/// The watch list as one reading of it, for matching: pure, so the lens and tools/IntelCheck use it
/// alike. See <see cref="IntelWatchEntry"/> for what each kind matches.
/// </summary>
public sealed class IntelWatchSet
{
    private readonly Dictionary<(string Kind, long Id), IntelWatchEntry> _byKey = [];

    public IntelWatchSet(IEnumerable<IntelWatchEntry> entries)
    {
        foreach (var e in entries)
            if (e.EntityId > 0 && IntelWatchKind.All.Contains(e.Kind)) _byKey[(e.Kind, e.EntityId)] = e;
        Entries = [.. _byKey.Values.OrderBy(e => e.Id)];
        HasGroups = _byKey.Keys.Any(k => k.Kind == IntelWatchKind.Group);
    }

    public static readonly IntelWatchSet Empty = new([]);

    public IReadOnlyList<IntelWatchEntry> Entries { get; }
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>Whether any entry is a ship group, which needs each hull's group looked up.</summary>
    public bool HasGroups { get; }

    /// <summary>The entry for one thing, if it is on the list.</summary>
    public IntelWatchEntry? Find(string kind, long entityId) =>
        entityId > 0 && _byKey.TryGetValue((kind, entityId), out var e) ? e : null;

    /// <summary>The mode one thing has on the list, or null when it is not on it.</summary>
    public int? ModeOf(string kind, long entityId) => Find(kind, entityId)?.Mode;

    /// <summary>
    /// A pilot's mark: the most particular entry that matches decides. The pilot's own, then the
    /// corporation's, the alliance's, the hull flown, its group.
    /// </summary>
    public IntelWatchMark Pilot(long characterId, long corporationId, long allianceId, int? shipTypeId = null, int? groupId = null)
    {
        if (IsEmpty) return IntelWatchMark.None;
        var entry = Find(IntelWatchKind.Pilot, characterId)
                 ?? Find(IntelWatchKind.Corporation, corporationId)
                 ?? Find(IntelWatchKind.Alliance, allianceId)
                 ?? Find(IntelWatchKind.Hull, shipTypeId ?? 0)
                 ?? Find(IntelWatchKind.Group, groupId ?? 0);
        return MarkOf(entry);
    }

    /// <summary>A hull with nobody named to fly it ("8× Muninn"): the hull's entry, then its group's.</summary>
    public IntelWatchMark Hull(int typeId, int? groupId = null)
    {
        if (IsEmpty) return IntelWatchMark.None;
        return MarkOf(Find(IntelWatchKind.Hull, typeId) ?? Find(IntelWatchKind.Group, groupId ?? 0));
    }

    private static IntelWatchMark MarkOf(IntelWatchEntry? e) => e is null ? IntelWatchMark.None
        : e.Mode == IntelWatchMode.Ignore ? IntelWatchMark.Ignored : IntelWatchMark.Always;

    /// <summary>
    /// A row's mark from what it shows: Always when anything on it is; Ignored only when it shows
    /// somebody and everything on it is ignored. A count nobody was named for, or a flag, is
    /// <paramref name="unmarked"/>: something the list says nothing about, so the row is not dimmed.
    /// </summary>
    public static IntelWatchMark Combine(IEnumerable<IntelWatchMark> marks, bool unmarked)
    {
        var any = false; var allIgnored = true;
        foreach (var m in marks)
        {
            if (m == IntelWatchMark.Always) return IntelWatchMark.Always;
            any = true;
            if (m != IntelWatchMark.Ignored) allIgnored = false;
        }
        return any && allIgnored && !unmarked ? IntelWatchMark.Ignored : IntelWatchMark.None;
    }
}

/// <summary>
/// The intel watch list in the shared database: read, and changed from the Intel tool's Watch list tab
/// and the "Always alert" / "Ignore" items on a pilot. Writes happen off the UI thread, by whoever calls.
///
/// <para>Every view on this PC sees a change at once (<see cref="Changed"/>, which the snapshot answers
/// by reading the list again); other PCs read it again within a minute, with the rest of the settings.</para>
/// </summary>
public sealed class IntelWatchList(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Raised after a change written here, on the thread that wrote it.</summary>
    public event Action? Changed;

    public async Task<List<IntelWatchEntry>> ReadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.IntelWatchEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts one thing on the list with this mode, or changes the mode it has: one entry per thing.
    /// The note is kept when null is given.
    /// </summary>
    public async Task SetAsync(string kind, long entityId, string name, int mode, string? note = null, CancellationToken ct = default)
    {
        if (entityId <= 0 || !IntelWatchKind.All.Contains(kind)) return;
        mode = mode == IntelWatchMode.Ignore ? IntelWatchMode.Ignore : IntelWatchMode.Always;
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.IntelWatchEntries.FirstOrDefaultAsync(e => e.Kind == kind && e.EntityId == entityId, ct).ConfigureAwait(false);
            if (row is null)
            {
                db.IntelWatchEntries.Add(new IntelWatchEntry
                {
                    Kind = kind, EntityId = entityId, Name = name, Mode = mode, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    AddedBy = Environment.MachineName, AddedAt = IntelLastSeenWriter.Format(DateTimeOffset.UtcNow),
                });
            }
            else
            {
                row.Mode = mode;
                if (name.Length > 0) row.Name = name;
                if (note is not null) row.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            }
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                break;
            }
            // Added on another PC at the same moment: the unique index refused the second. Once more, as a change.
            catch (DbUpdateException) when (attempt == 0 && row is null) { }
        }
        Changed?.Invoke();
    }

    /// <summary>Changes an entry's mode and note.</summary>
    public async Task UpdateAsync(int id, int mode, string? note, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.IntelWatchEntries.FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (row is null) return;
        row.Mode = mode == IntelWatchMode.Ignore ? IntelWatchMode.Ignore : IntelWatchMode.Always;
        row.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Takes one thing off the list.</summary>
    public async Task RemoveAsync(string kind, long entityId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.IntelWatchEntries.Where(e => e.Kind == kind && e.EntityId == entityId).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0) return;
        db.IntelWatchEntries.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Changed?.Invoke();
    }
}
