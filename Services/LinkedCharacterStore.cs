using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Linked characters — which characters are one player's, and which is the main — as a connector last
/// gave them, kept in <see cref="LinkedAccount"/> and <see cref="LinkedCharacter"/>. EVE Console never
/// asks where players registered their characters itself: <see cref="Connector.ConnectorService"/> asks
/// the connector the user runs, and this keeps its answer, so the links are there while it is down.
///
/// <para><b>The data</b>, version 1, as <c>GET /v1/linked-characters</c> answers:</para>
/// <code>
/// {
///   "format": "eveconsole.linked-characters",
///   "version": 1,
///   "source": "example",                       // a label for where the links came from
///   "generatedAt": "2026-11-08T12:00:00Z",      // when the connector read them
///   "accounts": [
///     { "id": 1001, "name": "Example Main",
///       "characters": [
///         { "id": 90000001, "name": "Example Main", "main": true,
///           "corporationId": 98000001, "allianceId": 99000001, "tokenValid": true },
///         { "id": 90000002, "name": "Example Alt",
///           "corporationId": 98000002 } ] } ]
/// }
/// </code>
///
/// <para>⚠️ An answer is the WHOLE of what its source says. Storing it replaces every account and
/// character that source gave before, in one transaction, so a reader never sees half of one sync and
/// half of the next. A character left out is gone; an empty <c>accounts</c> list empties the source.
/// Only the current links are kept — moves and their reasons are the source's history.</para>
///
/// <para>⚠️ An answer older than what is stored for its source is never stored over it, judged by
/// <c>generatedAt</c>. On a shared database, two clients refreshing at once would otherwise put back
/// whichever finished last.</para>
///
/// <para>⚠️ An answer is refused whole, never stored in part: an unknown format, a version newer than
/// this build reads, a character under two accounts, an account with two mains. What was stored
/// before stays as it was.</para>
/// </summary>
public sealed class LinkedCharacterStore(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger? errors = null)
{
    public const string Format          = "eveconsole.linked-characters";
    public const int    Version         = 1;
    public const int    MaxSourceLength = 64;

    /// <summary>Raised after what is stored changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Replaces what <paramref name="snapshot"/>'s source said before, unless what is stored is as new
    /// or newer. True when it was stored.
    ///
    /// <para><paramref name="stillWanted"/> is asked inside the transaction, after the lock: false stores
    /// nothing. <paramref name="replaceOtherSources"/> also removes every other source, for a caller
    /// that is the only one giving links.</para>
    /// </summary>
    public async Task<LinkedStoreResult> ImportAsync(
        LinkedSnapshot snapshot, CancellationToken ct = default,
        Func<AppDbContext, CancellationToken, Task<bool>>? stillWanted = null, bool replaceOtherSources = false)
    {
        var first = false;
        var imported = await SqliteBusy.RetryAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(db, ct);

            if (stillWanted is not null && !await stillWanted(db, ct)) return LinkedStoreResult.NotWanted;

            if (replaceOtherSources)
            {
                await db.LinkedCharacters.Where(c => c.Source != snapshot.Source).ExecuteDeleteAsync(ct);
                await db.LinkedAccounts.Where(a => a.Source != snapshot.Source).ExecuteDeleteAsync(ct);
                await db.LinkedSources.Where(s => s.Source != snapshot.Source).ExecuteDeleteAsync(ct);
            }

            // ⚠️ Compared here, in memory: SQLite cannot compare a DateTimeOffset in a query.
            var current = await db.LinkedSources.FirstOrDefaultAsync(s => s.Source == snapshot.Source, ct);
            if (current is not null && current.GeneratedAt >= snapshot.GeneratedAt)
            {
                await tx.CommitAsync(ct);   // other sources removed, if asked; this one left alone
                return LinkedStoreResult.NotNewer;
            }

            await db.LinkedCharacters.Where(c => c.Source == snapshot.Source).ExecuteDeleteAsync(ct);
            await db.LinkedAccounts.Where(a => a.Source == snapshot.Source).ExecuteDeleteAsync(ct);

            db.LinkedAccounts.AddRange(snapshot.Accounts.Select(a => new LinkedAccount
            {
                Source = a.Source, AccountId = a.AccountId, Name = a.Name, MainCharacterId = a.MainCharacterId,
            }));
            db.LinkedCharacters.AddRange(snapshot.Characters.Select(c => new LinkedCharacter
            {
                Source = c.Source, CharacterId = c.CharacterId, AccountId = c.AccountId, Name = c.Name,
                IsMain = c.IsMain, CorporationId = c.CorporationId, AllianceId = c.AllianceId, TokenValid = c.TokenValid,
            }));

            first = current is null;
            current ??= db.LinkedSources.Add(new LinkedSource { Source = snapshot.Source }).Entity;
            current.GeneratedAt = snapshot.GeneratedAt;
            current.ImportedAt  = DateTimeOffset.UtcNow;
            current.Accounts    = snapshot.Accounts.Count;
            current.Characters  = snapshot.Characters.Count;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return LinkedStoreResult.Stored;
        }, null, ct);

        // Once, the first time a source arrives: afterwards LinkedSources says when it last did.
        if (imported == LinkedStoreResult.Stored && first)
            errors?.Note("LinkedCharacters", snapshot.Source,
                $"Linked characters stored: {snapshot.Accounts.Count:N0} accounts, {snapshot.Characters.Count:N0} characters.");
        if (imported == LinkedStoreResult.Stored) Changed?.Invoke();
        return imported;
    }

    /// <summary>Forgets every source: the connector was disconnected, and what it gave goes with it.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await SqliteBusy.RetryAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(db, ct);
            await ClearAsync(db, ct);
            await tx.CommitAsync(ct);
            return true;
        }, null, ct);
        Changed?.Invoke();
    }

    /// <summary>Every link, inside a transaction the caller holds, with <see cref="LockAsync"/> taken.</summary>
    internal static async Task ClearAsync(AppDbContext db, CancellationToken ct)
    {
        await db.LinkedCharacters.ExecuteDeleteAsync(ct);
        await db.LinkedAccounts.ExecuteDeleteAsync(ct);
        await db.LinkedSources.ExecuteDeleteAsync(ct);
    }

    /// <summary>For a caller that changed what is stored in a transaction of its own.</summary>
    internal void RaiseChanged() => Changed?.Invoke();

    /// <summary>
    /// ⚠️ On PostgreSQL, one import or clear at a time across every client of the database, held to the end
    /// of the transaction; the connector's preference writes take it too. Without it two clients refreshing
    /// together both see no newer row, and one fails on the other's keys or puts the older answer back; and a
    /// clear, or a key ending, can land between another client's check and its write. SQLite needs nothing: this process holds the file alone, and a write transaction there
    /// already excludes every other.
    /// </summary>
    internal static async Task LockAsync(AppDbContext db, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7340117)", ct);
    }

    /// <summary>What is stored, one row per source, for saying how old it is.</summary>
    public async Task<IReadOnlyList<LinkedSource>> SourcesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LinkedSources.AsNoTracking().OrderBy(s => s.Source).ToListAsync(ct);
    }

    // ── The data ─────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>An answer read and checked, or why it was refused.</summary>
    public static (LinkedSnapshot? Snapshot, string? Error) Parse(string json)
    {
        PayloadDto? file;
        try { file = JsonSerializer.Deserialize<PayloadDto>(json, Json); }
        catch (JsonException ex) { return (null, "not readable JSON: " + ex.Message); }
        if (file is null) return (null, "empty");

        if (file.Format != Format)
            return (null, file.Format is null ? "no \"format\": not linked characters"
                                              : $"format \"{file.Format}\" is not \"{Format}\"");
        if (file.Version is not { } version || version < 1)
            return (null, "no valid \"version\"");
        if (version > Version)
            return (null, $"version {version} is for a newer EVE Console; this one reads version {Version}");

        var source = file.Source?.Trim() ?? "";
        if (source.Length == 0)              return (null, "no \"source\"");
        if (source.Length > MaxSourceLength) return (null, $"\"source\" is longer than {MaxSourceLength} characters");

        if (file.GeneratedAt is not { } generatedAt) return (null, "no \"generatedAt\"");
        if (file.Accounts is null)                   return (null, "no \"accounts\" list");

        // UTC, and to the microsecond: PostgreSQL keeps no finer, and a time that came back different
        // from the answer's would make the same answer look newer than itself.
        var utc = generatedAt.ToUniversalTime();
        utc = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);

        var accounts   = new List<LinkedAccount>(file.Accounts.Count);
        var characters = new List<LinkedCharacter>();
        var accountIds = new HashSet<long>();
        var ownerOf    = new Dictionary<long, long>();

        foreach (var a in file.Accounts)
        {
            if (a is null || a.Id <= 0)   return (null, "an account with no \"id\"");
            if (!accountIds.Add(a.Id))    return (null, $"account {a.Id} is listed twice");

            long? main = null;
            foreach (var c in a.Characters ?? [])
            {
                if (c is null || c.Id <= 0) return (null, $"account {a.Id} has a character with no \"id\"");
                if (ownerOf.TryGetValue(c.Id, out var other))
                    return (null, other == a.Id ? $"character {c.Id} is listed twice in account {a.Id}"
                                                : $"character {c.Id} is in both account {other} and account {a.Id}");
                ownerOf[c.Id] = a.Id;

                if (c.CorporationId < 0) return (null, $"character {c.Id} has a negative \"corporationId\"");
                if (c.Main)
                {
                    if (main is not null) return (null, $"account {a.Id} has two mains, {main} and {c.Id}");
                    main = c.Id;
                }

                characters.Add(new LinkedCharacter
                {
                    Source        = source,
                    CharacterId   = c.Id,
                    AccountId     = a.Id,
                    Name          = c.Name?.Trim() ?? "",
                    IsMain        = c.Main,
                    CorporationId = c.CorporationId,
                    AllianceId    = c.AllianceId is > 0 ? c.AllianceId : null,
                    TokenValid    = c.TokenValid,
                });
            }

            accounts.Add(new LinkedAccount { Source = source, AccountId = a.Id, Name = a.Name?.Trim() ?? "", MainCharacterId = main });
        }

        return (new LinkedSnapshot(source, utc, accounts, characters), null);
    }

    private sealed class PayloadDto
    {
        [JsonPropertyName("format")]      public string?            Format      { get; set; }
        [JsonPropertyName("version")]     public int?               Version     { get; set; }
        [JsonPropertyName("source")]      public string?            Source      { get; set; }
        [JsonPropertyName("generatedAt")] public DateTimeOffset?    GeneratedAt { get; set; }
        [JsonPropertyName("accounts")]    public List<AccountDto?>? Accounts    { get; set; }
    }

    private sealed class AccountDto
    {
        [JsonPropertyName("id")]         public long                 Id         { get; set; }
        [JsonPropertyName("name")]       public string?              Name       { get; set; }
        [JsonPropertyName("characters")] public List<CharacterDto?>? Characters { get; set; }
    }

    private sealed class CharacterDto
    {
        [JsonPropertyName("id")]            public long    Id            { get; set; }
        [JsonPropertyName("name")]          public string? Name          { get; set; }
        [JsonPropertyName("main")]          public bool    Main          { get; set; }
        [JsonPropertyName("corporationId")] public int     CorporationId { get; set; }
        [JsonPropertyName("allianceId")]    public int?    AllianceId    { get; set; }
        [JsonPropertyName("tokenValid")]    public bool?   TokenValid    { get; set; }
    }
}

/// <summary>What became of an answer given to <see cref="LinkedCharacterStore.ImportAsync"/>.</summary>
public enum LinkedStoreResult
{
    /// <summary>Stored, replacing what its source said before.</summary>
    Stored,
    /// <summary>Not newer than what is stored for its source: left alone.</summary>
    NotNewer,
    /// <summary>The caller's check, made inside the transaction, said no: nothing was touched.</summary>
    NotWanted,
}

/// <summary>One source's whole answer, checked: the rows it will become.</summary>
public sealed record LinkedSnapshot(
    string                          Source,
    DateTimeOffset                  GeneratedAt,
    IReadOnlyList<LinkedAccount>    Accounts,
    IReadOnlyList<LinkedCharacter>  Characters);
