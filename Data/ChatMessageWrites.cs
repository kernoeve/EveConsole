using EveConsole.Services;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Data;

/// <summary>
/// Every write to ChatMessages goes through here, so ids are handed out and committed in the same order.
///
/// <para>⚠️ Why it matters. The intel parser reads ChatMessages by id: everything above its watermark,
/// in id order, and then moves the watermark to the last id it read. On PostgreSQL several writers
/// insert at once (the chat logs of every PC, and the worker's Jabber and IRC rooms), and an id is
/// taken when the row is inserted but becomes visible only when its transaction commits. Writer A takes
/// ids 100–599 and is still committing; writer B takes 600–620 and commits first; the parser reads 600–620
/// and moves past 620. A's 500 lines then commit below the watermark and are never parsed.</para>
///
/// <para>So each insert batch takes one transaction-scoped advisory lock first, and holds it until it
/// commits: the next writer cannot take an id until the one before it is visible. Per batch, never per
/// file or per import: a long re-import of old logs would otherwise hold up a live Jabber line for as long
/// as it runs.</para>
///
/// <para>SQLite needs none of it: one process, and one writer at a time inside it, so an id is always
/// committed before the next one is taken.</para>
/// </summary>
public static class ChatMessageWrites
{
    /// <summary>Fixed and arbitrary: "CHATMSG" + 1. Shared by every writer on the database.</summary>
    public const long LockKey = 0x4348_4154_4D53_4701;

    /// <summary>
    /// Takes the lock inside the caller's transaction, which must already be open. It is released when
    /// that transaction ends. Does nothing on SQLite.
    /// </summary>
    public static async Task SerialiseAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (!DbEngine.IsPostgres) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("ChatMessageWrites.SerialiseAsync needs an open transaction: the lock lasts as long as it does.");

        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [LockKey], ct);
    }

    /// <summary>
    /// Saves what is pending on <paramref name="db"/> (new ChatMessages rows, and anything that rides with
    /// them) in one transaction that holds the lock. On SQLite, a plain save.
    /// </summary>
    public static async Task<int> SaveAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (!DbEngine.IsPostgres || db.Database.CurrentTransaction is not null)
        {
            // Inside a transaction somebody else opened, the lock is taken in it and lasts as long.
            await SerialiseAsync(db, ct);
            return await db.SaveChangesAsync(ct);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SerialiseAsync(db, ct);
        var n = await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return n;
    }
}
