using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Jabber;

/// <summary>What loading a pack came to.</summary>
public sealed record EmoteImportResult(int PackId, string Name, int Codes, int Pictures, int Missing, int Unusable, bool Replaced);

/// <summary>A pack as the Jabber tool lists it.</summary>
public sealed record EmotePackInfo(int Id, string Name, string Description, string Author, int Codes, int Pictures, IReadOnlyList<int> AccountIds);

/// <summary>
/// Emote packs: loading them, giving them to Jabber servers, and the pictures they show.
///
/// <para>The packs are in the database, so every machine that reaches it has them without loading anything. Each
/// machine keeps the pictures it has shown in a folder of its own, by hash, so the database is asked for each only
/// once; a hash never changes what it names, so that copy is never out of date.</para>
///
/// <para>⚠️ Another of the owner's machines can change the packs. That is noticed by looking every couple of
/// minutes, not by a signal: a pack is set up once and then rarely touched.</para>
/// </summary>
public sealed class EmoteService
{
    private static readonly TimeSpan LookAgain = TimeSpan.FromMinutes(2);
    private const long MostDecodedBytes = 160L * 1024 * 1024;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger _errors;
    private readonly Dictionary<int, EmoteSet> _sets = [];
    private readonly Lock _gate = new();
    private string _signature = "";
    private bool _started;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private DispatcherTimer? _timer;

    private readonly Dictionary<string, (EmoteFrames Frames, long Used)> _frames = [];
    private readonly Dictionary<string, Task<EmoteFrames?>> _decoding = [];
    private long _decodedBytes, _use;

    public EmoteService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors)
    {
        _dbFactory = dbFactory;
        _errors = errors;
    }

    /// <summary>Packs were loaded, given, taken off or reordered, here or on another machine.</summary>
    public event Action? PacksChanged;

    private static string CacheDir => Path.Combine(AppConfig.AppDataDir, "EmoteCache");

    // ── Each server's set ─────────────────────────────────────────────────────

    /// <summary>
    /// The set a server's messages are read with. Always the same object for a server, empty until its packs have
    /// been read, so it can be held on to and listened to. <see cref="EmoteSet.None"/> for anything that is not one.
    /// </summary>
    public EmoteSet For(int accountId)
    {
        if (accountId <= 0) return EmoteSet.None;

        EmoteSet set;
        bool first;
        lock (_gate)
        {
            if (!_sets.TryGetValue(accountId, out set!)) _sets[accountId] = set = new EmoteSet(accountId);
            first = !_started;
            _started = true;
        }
        if (first) _ = LoadAsync(force: true);
        StartLooking();
        return set;
    }

    private void StartLooking()
    {
        if (_timer is not null || Avalonia.Application.Current is null || !Dispatcher.UIThread.CheckAccess()) return;
        _timer = new DispatcherTimer { Interval = LookAgain };
        _timer.Tick += (_, _) => _ = LoadAsync(force: false);
        _timer.Start();
    }

    /// <summary>Reads every server's packs again: at once after a change here, or when they look different.</summary>
    public Task ReloadAsync() => LoadAsync(force: true);

    private async Task LoadAsync(bool force)
    {
        // One at a time: a slow read finishing after a newer one would put back what was just changed.
        await _loadGate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var links = await db.JabberEmotePacks.AsNoTracking().OrderBy(l => l.AccountId).ThenBy(l => l.Position).ThenBy(l => l.PackId).ToListAsync();
            var packs = await db.EmotePacks.AsNoTracking().ToDictionaryAsync(p => p.Id);

            var signature = string.Join(";", links.Select(l => $"{l.AccountId}:{l.PackId}:{l.Position}"))
                            + "|" + string.Join(";", packs.Values.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.AddedAt}"));
            if (!force && signature == _signature) return;
            _signature = signature;

            var used = links.Select(l => l.PackId).Distinct().ToList();
            var rows = await (from c in db.EmoteCodes
                              join i in db.EmoteImages on c.ImageHash equals i.Hash
                              where used.Contains(c.PackId)
                              select new { c.PackId, c.Code, c.ImageHash, c.Position, i.Width, i.Height, i.Animated })
                             .AsNoTracking().ToListAsync();
            var byPack = rows.GroupBy(r => r.PackId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Position).ThenBy(r => r.Code, StringComparer.Ordinal).ToList());

            var built = new Dictionary<int, (List<EmoteRef> Codes, List<EmotePickItem> Pictures)>();
            foreach (var account in links.GroupBy(l => l.AccountId))
            {
                var codes = new List<EmoteRef>();
                var pictures = new List<EmotePickItem>();
                var pictured = new HashSet<string>(StringComparer.Ordinal);

                foreach (var link in account)
                {
                    if (!byPack.TryGetValue(link.PackId, out var packRows)) continue;
                    var packName = packs.TryGetValue(link.PackId, out var p) ? p.Name : "";

                    foreach (var r in packRows) codes.Add(new EmoteRef(r.Code, r.ImageHash, r.Width, r.Height, r.Animated));

                    // One picture each, under the codes this pack gives it. A picture in two packs shows once.
                    foreach (var picture in packRows.GroupBy(r => r.ImageHash).OrderBy(g => g.Min(r => r.Position)))
                    {
                        if (!pictured.Add(picture.Key)) continue;
                        var first = picture.First();
                        pictures.Add(new EmotePickItem(new EmoteRef(first.Code, first.ImageHash, first.Width, first.Height, first.Animated),
                            [.. picture.Select(r => r.Code)], packName));
                    }
                }
                built[account.Key] = (codes, pictures);
            }

            void Apply()
            {
                List<EmoteSet> sets;
                lock (_gate)
                {
                    foreach (var id in built.Keys) if (!_sets.ContainsKey(id)) _sets[id] = new EmoteSet(id);
                    sets = [.. _sets.Values];
                }
                foreach (var set in sets)
                    if (built.TryGetValue(set.AccountId, out var b)) set.Fill(b.Codes, b.Pictures);
                    else if (!set.IsEmpty) set.Fill([], []);
                PacksChanged?.Invoke();
            }

            // The sets are drawn from, so they change on the UI thread. A check run with no app has none to wait for.
            if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess()) Apply();
            else await Dispatcher.UIThread.InvokeAsync(Apply);
        }
        catch (Exception e)
        {
            _errors.Log(nameof(EmoteService), "read emote packs", e);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    // ── Packs ─────────────────────────────────────────────────────────────────

    public async Task<List<EmotePackInfo>> PacksAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var packs = await db.EmotePacks.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var counts = await db.EmoteCodes.GroupBy(c => c.PackId)
            .Select(g => new { g.Key, Codes = g.Count(), Pictures = g.Select(c => c.ImageHash).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, ct);
        var links = await db.JabberEmotePacks.AsNoTracking().ToListAsync(ct);

        return [.. packs.Select(p => new EmotePackInfo(p.Id, p.Name, p.Description, p.Author,
            counts.TryGetValue(p.Id, out var c) ? c.Codes : 0, c?.Pictures ?? 0,
            [.. links.Where(l => l.PackId == p.Id).Select(l => l.AccountId)]))];
    }

    /// <summary>A server's packs, best first.</summary>
    public async Task<List<int>> PackIdsOfAsync(int accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.JabberEmotePacks.AsNoTracking().Where(l => l.AccountId == accountId)
            .OrderBy(l => l.Position).ThenBy(l => l.PackId).Select(l => l.PackId).ToListAsync(ct);
    }

    /// <summary>
    /// Loads a pack and gives it to a server, last in its list. A pack of the same name already loaded is replaced by
    /// this one, for every server that has it: that is how a newer copy of a pack goes in.
    /// </summary>
    public async Task<EmoteImportResult> ImportAsync(string path, int accountId, CancellationToken ct = default)
    {
        var file = await Task.Run(() => EmotePackReader.Read(path), ct);
        if (file.Pictures.Count == 0) throw new InvalidDataException(Localization.JabberText.EmoteNoPictures);

        var hashes = file.Pictures.ToDictionary(p => p.Key, p => Convert.ToHexStringLower(SHA256.HashData(p.Value)), StringComparer.OrdinalIgnoreCase);
        var name = file.Theme.Name.Length > 0 ? file.Theme.Name : Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\'));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var wanted = hashes.Values.Distinct().ToList();
        var have = (await db.EmoteImages.Where(i => wanted.Contains(i.Hash)).Select(i => i.Hash).ToListAsync(ct)).ToHashSet();
        foreach (var (picture, hash) in hashes)
        {
            if (!have.Add(hash)) continue;
            var info = file.Info[picture];
            db.EmoteImages.Add(new EmoteImage { Hash = hash, Bytes = file.Pictures[picture], Width = info.Width, Height = info.Height, Animated = info.Frames > 1 });
        }

        var pack = await db.EmotePacks.FirstOrDefaultAsync(p => p.Name.ToLower() == name.ToLower(), ct);
        var replaced = pack is not null;
        if (pack is null) db.EmotePacks.Add(pack = new EmotePack { Name = name });
        pack.Description = file.Theme.Description;
        pack.Author = file.Theme.Author;
        pack.AddedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        await db.SaveChangesAsync(ct);

        if (replaced) await db.EmoteCodes.Where(c => c.PackId == pack.Id).ExecuteDeleteAsync(ct);

        var codes = 0;
        var position = 0;
        foreach (var line in file.Theme.Lines)
        {
            position++;
            if (!hashes.TryGetValue(line.File, out var hash)) continue;
            foreach (var code in line.Codes)
            {
                db.EmoteCodes.Add(new EmoteCode { PackId = pack.Id, Code = code, ImageHash = hash, Position = position });
                codes++;
            }
        }
        pack.IconCode = file.Theme.Lines.FirstOrDefault(l => string.Equals(l.File, file.Theme.Icon, StringComparison.OrdinalIgnoreCase) && hashes.ContainsKey(l.File))?.Codes[0] ?? "";

        if (accountId > 0 && !await db.JabberEmotePacks.AnyAsync(l => l.AccountId == accountId && l.PackId == pack.Id, ct))
        {
            var last = await db.JabberEmotePacks.Where(l => l.AccountId == accountId).Select(l => (int?)l.Position).MaxAsync(ct) ?? -1;
            db.JabberEmotePacks.Add(new JabberEmotePack { AccountId = accountId, PackId = pack.Id, Position = last + 1 });
        }
        await db.SaveChangesAsync(ct);
        await DeleteUnusedAsync(db, ct);
        await tx.CommitAsync(ct);

        await ReloadAsync();
        return new EmoteImportResult(pack.Id, name, codes, hashes.Values.Distinct().Count(), file.Missing.Count, file.Unusable.Count, replaced);
    }

    /// <summary>Gives a pack already loaded to a server, last in its list.</summary>
    public async Task AddToAsync(int accountId, int packId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.JabberEmotePacks.AnyAsync(l => l.AccountId == accountId && l.PackId == packId, ct)) return;

        var last = await db.JabberEmotePacks.Where(l => l.AccountId == accountId).Select(l => (int?)l.Position).MaxAsync(ct) ?? -1;
        db.JabberEmotePacks.Add(new JabberEmotePack { AccountId = accountId, PackId = packId, Position = last + 1 });
        await db.SaveChangesAsync(ct);
        await ReloadAsync();
    }

    /// <summary>Takes a pack off a server. True when no server had it any more, so it was deleted with its emotes.</summary>
    public async Task<bool> RemoveFromAsync(int accountId, int packId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.JabberEmotePacks.Where(l => l.AccountId == accountId && l.PackId == packId).ExecuteDeleteAsync(ct);
        var gone = !await db.JabberEmotePacks.AnyAsync(l => l.PackId == packId, ct);
        await DeleteUnusedAsync(db, ct);
        await tx.CommitAsync(ct);

        await ReloadAsync();
        return gone;
    }

    /// <summary>Moves a pack up (negative) or down a server's list. The higher one wins a code both have.</summary>
    public async Task MoveAsync(int accountId, int packId, int by, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var links = await db.JabberEmotePacks.Where(l => l.AccountId == accountId).OrderBy(l => l.Position).ThenBy(l => l.PackId).ToListAsync(ct);
        var at = links.FindIndex(l => l.PackId == packId);
        var to = Math.Clamp(at + by, 0, links.Count - 1);
        if (at < 0 || to == at) return;

        var link = links[at];
        links.RemoveAt(at);
        links.Insert(to, link);
        for (var i = 0; i < links.Count; i++) links[i].Position = i;
        await db.SaveChangesAsync(ct);
        await ReloadAsync();
    }

    /// <summary>Deletes the packs no server has, and the pictures no pack has. Part of whatever change made them so.</summary>
    public static async Task DeleteUnusedAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.EmoteCodes.Where(c => !db.JabberEmotePacks.Any(l => l.PackId == c.PackId)).ExecuteDeleteAsync(ct);
        await db.EmotePacks.Where(p => !db.JabberEmotePacks.Any(l => l.PackId == p.Id)).ExecuteDeleteAsync(ct);
        await db.EmoteImages.Where(i => !db.EmoteCodes.Any(c => c.ImageHash == i.Hash)).ExecuteDeleteAsync(ct);
    }

    // ── Pictures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A picture ready to draw: every frame, or with <paramref name="still"/> only the first, as the picker shows them.
    /// Null when it cannot be had. Decoded once, and kept while there is room.
    /// </summary>
    public Task<EmoteFrames?> FramesAsync(string hash, bool still = false)
    {
        var key = still ? hash + "#still" : hash;
        lock (_gate)
        {
            if (_frames.TryGetValue(key, out var kept))
            {
                _frames[key] = (kept.Frames, ++_use);
                return Task.FromResult<EmoteFrames?>(kept.Frames);
            }
            if (_decoding.TryGetValue(key, out var running)) return running;

            var task = Task.Run(async () =>
            {
                try
                {
                    var bytes = await BytesAsync(hash);
                    var frames = bytes is null ? null : EmoteFrames.Decode(bytes, still);
                    if (frames is not null) Keep(key, frames);
                    return frames;
                }
                catch (Exception e)
                {
                    _errors.Log(nameof(EmoteService), "show an emote", e);
                    return null;
                }
                finally
                {
                    lock (_gate) _decoding.Remove(key);
                }
            });
            _decoding[key] = task;
            return task;
        }
    }

    private void Keep(string key, EmoteFrames frames)
    {
        lock (_gate)
        {
            _frames[key] = (frames, ++_use);
            _decodedBytes += frames.Bytes;

            // The ones shown longest ago go first. Anything still on screen holds its own and carries on.
            while (_decodedBytes > MostDecodedBytes && _frames.Count > 1)
            {
                var oldest = _frames.MinBy(f => f.Value.Used);
                _frames.Remove(oldest.Key);
                _decodedBytes -= oldest.Value.Frames.Bytes;
            }
        }
    }

    private async Task<byte[]?> BytesAsync(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return null;

        var path = Path.Combine(CacheDir, hash);
        try
        {
            if (File.Exists(path))
            {
                var kept = await File.ReadAllBytesAsync(path);
                if (Convert.ToHexStringLower(SHA256.HashData(kept)) == hash) return kept;
            }
        }
        catch (IOException) { /* read again from the database */ }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var bytes = await db.EmoteImages.AsNoTracking().Where(i => i.Hash == hash).Select(i => i.Bytes).FirstOrDefaultAsync();
        if (bytes is null) return null;

        try
        {
            Directory.CreateDirectory(CacheDir);
            var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { /* shown anyway; kept next time */ }
        catch (UnauthorizedAccessException) { /* the same */ }
        return bytes;
    }
}
