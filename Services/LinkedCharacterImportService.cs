using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Reads files of linked characters — which characters are one player's, and which is the main —
/// into <see cref="LinkedAccount"/> and <see cref="LinkedCharacter"/>. EVE Console never fetches
/// these itself: a separate tool, run by whoever has access to where players register their
/// characters, writes the file, and this picks it up.
///
/// <para><b>Where.</b> <c>Imports/Linked Characters</c> under the data folder, every <c>*.json</c>
/// in it, looked at once a minute. Nothing creates the folder: an install that never receives a
/// file never has one, and this does nothing but ask whether it exists.</para>
///
/// <para><b>The file</b>, version 1:</para>
/// <code>
/// {
///   "format": "eveconsole.linked-characters",
///   "version": 1,
///   "source": "example",                       // a label; one file per source
///   "generatedAt": "2026-11-08T12:00:00Z",      // when the tool read it
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
/// <para>⚠️ The file is the WHOLE of what its source says. Importing it replaces every account and
/// character that source gave before, in one transaction, so a reader never sees half of one sync
/// and half of the next. A character left out is gone; an empty <c>accounts</c> list empties the
/// source. Only the current links are kept — moves and their reasons are the source's history.</para>
///
/// <para>⚠️ A file older than what was imported for its source is never imported over it, judged by
/// <c>generatedAt</c>. On a shared database, two machines each holding a copy would otherwise put
/// back whichever they read last.</para>
///
/// <para>⚠️ A file is refused whole, never imported in part: an unknown format, a version newer than
/// this build reads, a character under two accounts, an account with two mains. The reason goes to
/// the error log once for each version of the file, and what was imported before stays as it was.
/// The tool should write to a temporary name and rename it into place; a file still open for writing
/// is simply tried again next time.</para>
///
/// <para>Host-bound, like the game logs: it reads a folder on THIS machine, so every client runs it,
/// the headless worker included — a tool in a container beside the worker writes into its folder.</para>
/// </summary>
public sealed class LinkedCharacterImportService
{
    public const string FileFormat      = "eveconsole.linked-characters";
    public const int    FileVersion     = 1;
    public const int    MaxSourceLength = 64;

    /// <summary>Far beyond any alliance's members; a file larger is not one of these.</summary>
    private const long MaxFileBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public static string DefaultFolder => Path.Combine(AppConfig.AppDataDir, "Imports", "Linked Characters");

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger?                 _errors;

    /// <summary>The folder looked in.</summary>
    public string Folder { get; }

    /// <summary>Raised after an import changed what is stored.</summary>
    public event Action? Changed;

    // Each file as last read: its write time and length. A file is read again only when one of them
    // changes, so a refused file is reported once rather than every minute, and an imported one is
    // not parsed again for nothing.
    private readonly Dictionary<string, (DateTime Written, long Length)> _seen = new(StringComparer.OrdinalIgnoreCase);

    // The last failure reported for each file, so a database that keeps refusing is said once.
    private readonly Dictionary<string, string> _lastFailure = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;

    public LinkedCharacterImportService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger? errors = null, string? folder = null)
    {
        _dbFactory = dbFactory;
        _errors    = errors;
        Folder     = folder ?? DefaultFolder;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts     = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_runTask is not null) { try { await _runTask; } catch { } }
        _cts.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ScanOnceAsync(ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _errors?.Log("LinkedCharacters", "scan", ex); }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Every file in the folder that is new or changed since last time, oldest name first. Nothing at
    /// all, and no error, when the folder does not exist.
    /// </summary>
    public async Task<IReadOnlyList<LinkedImportResult>> ScanOnceAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(Folder)) return [];

        var results = new List<LinkedImportResult>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            var info = new FileInfo(path);
            if (!info.Exists) continue;
            var stamp = (info.LastWriteTimeUtc, info.Length);
            if (_seen.TryGetValue(path, out var was) && was == stamp) continue;

            var result = await ImportFileAsync(path, ct);
            results.Add(result);

            switch (result.Outcome)
            {
                case LinkedImportOutcome.Unreadable:
                    break;      // still being written, most likely: next time

                case LinkedImportOutcome.Failed:
                    // The database, not the file: tried again next time, and said once.
                    if (!_lastFailure.TryGetValue(path, out var said) || said != result.Detail)
                        _errors?.Log("LinkedCharacters", info.Name, result.Detail);
                    _lastFailure[path] = result.Detail;
                    break;

                case LinkedImportOutcome.Rejected:
                    _errors?.Warn("LinkedCharacters", info.Name, "Not imported: " + result.Detail);
                    _seen[path] = stamp;
                    _lastFailure.Remove(path);
                    break;

                default:
                    _seen[path] = stamp;
                    _lastFailure.Remove(path);
                    break;
            }
        }
        return results;
    }

    /// <summary>One file, whatever its name: read, checked, and imported if newer than its source's.</summary>
    public async Task<LinkedImportResult> ImportFileAsync(string path, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        string json;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
                return new(name, LinkedImportOutcome.Rejected, $"{info.Length:N0} bytes, larger than any such file");

            // FileShare.Read: a writer still holding it open makes this throw, which is the answer.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync(ct);
        }
        catch (IOException ex)                  { return new(name, LinkedImportOutcome.Unreadable, ex.Message); }
        catch (UnauthorizedAccessException ex)  { return new(name, LinkedImportOutcome.Unreadable, ex.Message); }

        var parsed = Parse(json);
        if (parsed.Snapshot is not { } snapshot)
            return new(name, LinkedImportOutcome.Rejected, parsed.Error ?? "unreadable");

        try
        {
            var imported = await ImportAsync(snapshot, ct);
            var outcome  = imported ? LinkedImportOutcome.Imported : LinkedImportOutcome.Older;
            if (imported) Changed?.Invoke();
            return new(name, outcome, "", snapshot.Source, snapshot.Accounts.Count, snapshot.Characters.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(name, LinkedImportOutcome.Failed, AppErrorLogger.Line("Import failed", ex), snapshot.Source);
        }
    }

    /// <summary>
    /// Replaces what <paramref name="snapshot"/>'s source said before, unless what is stored is as new
    /// or newer. True when it was imported.
    /// </summary>
    public async Task<bool> ImportAsync(LinkedSnapshot snapshot, CancellationToken ct = default)
    {
        var first = false;
        var imported = await SqliteBusy.RetryAsync(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // ⚠️ Compared here, in memory: SQLite cannot compare a DateTimeOffset in a query.
            var current = await db.LinkedSources.FirstOrDefaultAsync(s => s.Source == snapshot.Source, ct);
            if (current is not null && current.GeneratedAt >= snapshot.GeneratedAt) return false;

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
            return true;
        }, null, ct);

        // Once, the first time a source arrives: afterwards LinkedSources says when it last did.
        if (imported && first)
            _errors?.Note("LinkedCharacters", snapshot.Source,
                $"Linked characters imported: {snapshot.Accounts.Count:N0} accounts, {snapshot.Characters.Count:N0} characters.");
        return imported;
    }

    // ── The file ─────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>A file read and checked, or why it was refused.</summary>
    public static (LinkedSnapshot? Snapshot, string? Error) Parse(string json)
    {
        FileDto? file;
        try { file = JsonSerializer.Deserialize<FileDto>(json, Json); }
        catch (JsonException ex) { return (null, "not readable JSON: " + ex.Message); }
        if (file is null) return (null, "empty");

        if (file.Format != FileFormat)
            return (null, file.Format is null ? "no \"format\": not a linked-characters file"
                                              : $"format \"{file.Format}\" is not \"{FileFormat}\"");
        if (file.Version is not { } version || version < 1)
            return (null, "no valid \"version\"");
        if (version > FileVersion)
            return (null, $"version {version} is for a newer EVE Console; this one reads version {FileVersion}");

        var source = file.Source?.Trim() ?? "";
        if (source.Length == 0)              return (null, "no \"source\"");
        if (source.Length > MaxSourceLength) return (null, $"\"source\" is longer than {MaxSourceLength} characters");

        if (file.GeneratedAt is not { } generatedAt) return (null, "no \"generatedAt\"");
        if (file.Accounts is null)                   return (null, "no \"accounts\" list");

        // UTC, and to the microsecond: PostgreSQL keeps no finer, and a time that came back different
        // from the file's would make the same file look newer than itself.
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

    private sealed class FileDto
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

/// <summary>One source's whole file, checked: the rows it will become.</summary>
public sealed record LinkedSnapshot(
    string                          Source,
    DateTimeOffset                  GeneratedAt,
    IReadOnlyList<LinkedAccount>    Accounts,
    IReadOnlyList<LinkedCharacter>  Characters);

public enum LinkedImportOutcome
{
    /// <summary>Stored, replacing what its source said before.</summary>
    Imported,
    /// <summary>Not newer than what is stored for its source: left alone.</summary>
    Older,
    /// <summary>Not a file this can import: reported, and not tried again until it changes.</summary>
    Rejected,
    /// <summary>Could not be opened, most likely still being written: tried again next time.</summary>
    Unreadable,
    /// <summary>The database refused it: tried again next time.</summary>
    Failed,
}

public sealed record LinkedImportResult(
    string              File,
    LinkedImportOutcome Outcome,
    string              Detail     = "",
    string              Source     = "",
    int                 Accounts   = 0,
    int                 Characters = 0);
