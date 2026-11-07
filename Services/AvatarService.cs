using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace EveConsole.Services;

/// <summary>Whose picture to show: where they were seen, who they are there, and the name to take an initial from.</summary>
public sealed record AvatarRef(string Service, int ServerId, string Person, string Name);

/// <summary>
/// People's pictures, for both Jabber and Mumble.
///
/// <para>Saved by whichever process holds the connection, as people's presence names a new picture, into the database
/// every machine of the owner's reaches. Shown by every client from there. Someone with none is shown by a coloured
/// first initial, the colour always the same for the same name.</para>
/// </summary>
public sealed class AvatarService
{
    private static readonly TimeSpan LookAgain = TimeSpan.FromSeconds(30);
    public const int MostBytes = 512 * 1024;
    public const int MostSide = 2048;
    private const int ShownSize = 96;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger _errors;
    private readonly Lock _gate = new();
    private Dictionary<(string, int, string), string> _owners = [];
    private readonly Dictionary<string, Bitmap?> _images = [];
    private readonly HashSet<string> _loading = [];
    private readonly HashSet<string> _fetching = [];
    private readonly HashSet<(string, int, string)> _asked = [];
    private readonly Dictionary<(string, int, string), string> _announced = [];
    private string _signature = "";
    private bool _started;
    private DispatcherTimer? _timer;

    public AvatarService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors)
    {
        _dbFactory = dbFactory;
        _errors = errors;
    }

    /// <summary>Where pictures are drawn from. Set once when the app starts.</summary>
    public static AvatarService? Shared { get; set; }

    /// <summary>Someone's picture changed, or one became ready to draw. Raised on the UI thread.</summary>
    public event Action? Changed;

    // ── Showing ───────────────────────────────────────────────────────────────

    /// <summary>The picture to draw for someone, or null for an initial: none known, or not read yet.</summary>
    public Bitmap? ImageFor(AvatarRef who)
    {
        Start();
        string? hash;
        lock (_gate)
        {
            if (!_owners.TryGetValue((who.Service, who.ServerId, who.Person), out hash)) return null;
            if (_images.TryGetValue(hash, out var image)) return image;
            if (!_loading.Add(hash)) return null;
        }
        _ = LoadImageAsync(hash);
        return null;
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        _ = ReadOwnersAsync(force: true);
        if (Avalonia.Application.Current is null || !Dispatcher.UIThread.CheckAccess()) return;
        _timer = new DispatcherTimer { Interval = LookAgain };
        _timer.Tick += (_, _) => _ = ReadOwnersAsync(force: false);
        _timer.Start();
    }

    private async Task ReadOwnersAsync(bool force)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var count = await db.ChatAvatarOwners.CountAsync();
            var newest = await db.ChatAvatarOwners.MaxAsync(o => (string?)o.UpdatedAt) ?? "";
            var signature = $"{count}|{newest}";
            if (!force && signature == _signature) return;
            _signature = signature;

            var rows = await db.ChatAvatarOwners.AsNoTracking().ToListAsync();
            lock (_gate) _owners = rows.ToDictionary(o => (o.Service, o.ServerId, o.Person), o => o.Hash);
            Tell();
        }
        catch (Exception e)
        {
            _errors.Log(nameof(AvatarService), "read who has which picture", e);
        }
    }

    private async Task LoadImageAsync(string hash)
    {
        Bitmap? image = null;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var bytes = await db.ChatAvatars.AsNoTracking().Where(a => a.Hash == hash).Select(a => a.Bytes).FirstOrDefaultAsync();
            if (bytes is { Length: > 0 })
                image = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(stream, ShownSize, BitmapInterpolationMode.HighQuality);
                });
        }
        catch (Exception e)
        {
            _errors.Log(nameof(AvatarService), "show a picture", e);
        }

        lock (_gate)
        {
            _images[hash] = image;
            _loading.Remove(hash);
        }
        if (image is not null) Tell();
    }

    private void Tell()
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess()) Changed?.Invoke();
        else Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// <summary>What to show for someone with no picture: the first letter or digit of their name, in capitals.</summary>
    public static string Initial(string name)
    {
        foreach (var c in name.Trim())
            if (char.IsLetterOrDigit(c)) return char.ToUpper(c, CultureInfo.CurrentCulture).ToString();
        return "?";
    }

    /// <summary>The same colour every time for the same name, from a set that reads with white on it in either theme.</summary>
    public static int ColorIndex(string name, int colors)
    {
        var hash = 0u;
        foreach (var c in name.Trim().ToLowerInvariant()) hash = hash * 31 + c;
        return (int)(hash % (uint)colors);
    }

    // ── Saving ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a connection learned of someone's picture: a hash, "" for none, or null for not said. Their picture is
    /// fetched only when it is one not saved yet; with no hash, once, unless <paramref name="refresh"/>.
    /// </summary>
    /// <param name="persons">Every key they go by here. The first decides whether anything has changed.</param>
    public async Task NoteAsync(string service, int serverId, IReadOnlyList<string> persons, string? hash,
        Func<Task<byte[]?>> fetch, bool refresh = false, CancellationToken ct = default)
    {
        if (persons.Count == 0) return;
        var key = (service, serverId, persons[0]);

        try
        {
            if (hash is { Length: 0 })
            {
                bool had;
                lock (_gate) had = persons.Any(p => _owners.ContainsKey((service, serverId, p)));
                if (had) await ForgetAsync(service, serverId, persons, ct);
                return;
            }

            lock (_gate)
            {
                if (hash is null)
                {
                    // Nothing said: asked once, unless it is to be read again, as this account's own is on each connect.
                    if (!refresh && (_owners.ContainsKey(key) || !_asked.Add(key))) return;
                }
                else
                {
                    // ⚠️ By what they said last, not only what is stored: a picture is kept under the hash worked out
                    // here, so one announced wrongly would otherwise be fetched again with every presence.
                    if (_announced.GetValueOrDefault(key) == hash || persons.All(p => _owners.GetValueOrDefault((service, serverId, p)) == hash)) return;
                    if (!_fetching.Add(hash)) return;
                }
            }

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var stored = hash is not null && await db.ChatAvatars.AnyAsync(a => a.Hash == hash, ct) ? hash : null;

                if (stored is null)
                {
                    var bytes = await fetch();
                    if (bytes is null || Probe(bytes) is not (int width, int height)) return;

                    var own = Convert.ToHexStringLower(SHA1.HashData(bytes));
                    stored = own;
                    if (!await db.ChatAvatars.AnyAsync(a => a.Hash == own, ct))
                    {
                        db.ChatAvatars.Add(new ChatAvatar { Hash = own, Bytes = bytes, Width = width, Height = height });
                        await db.SaveChangesAsync(ct);
                    }
                }

                await OwnAsync(db, service, serverId, persons, stored, ct);
                if (hash is not null) lock (_gate) _announced[key] = hash;
            }
            finally
            {
                if (hash is not null) lock (_gate) _fetching.Remove(hash);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _errors.Log(nameof(AvatarService), "save someone's picture", e);
        }
    }

    /// <summary>A picture small enough to be one, and its size. Null for anything else.</summary>
    public static (int Width, int Height)? Probe(byte[] bytes)
    {
        if (bytes.Length is 0 or > MostBytes) return null;
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;
            var (w, h) = (codec.Info.Width, codec.Info.Height);
            return w is > 0 and <= MostSide && h is > 0 and <= MostSide ? (w, h) : null;
        }
        catch (Exception) { return null; }
    }

    private async Task OwnAsync(AppDbContext db, string service, int serverId, IReadOnlyList<string> persons, string hash, CancellationToken ct)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var wanted = persons.Distinct().ToList();
        var rows = await db.ChatAvatarOwners.Where(o => o.Service == service && o.ServerId == serverId && wanted.Contains(o.Person)).ToListAsync(ct);
        var replaced = rows.Where(r => r.Hash != hash).Select(r => r.Hash).Distinct().ToList();

        foreach (var person in wanted)
        {
            var row = rows.FirstOrDefault(r => r.Person == person);
            if (row is null) db.ChatAvatarOwners.Add(new ChatAvatarOwner { Service = service, ServerId = serverId, Person = person, Hash = hash, UpdatedAt = now });
            else if (row.Hash != hash) { row.Hash = hash; row.UpdatedAt = now; }
        }
        await db.SaveChangesAsync(ct);
        await DeleteUnusedAsync(db, replaced, ct);

        lock (_gate) foreach (var person in wanted) _owners[(service, serverId, person)] = hash;
        Tell();
    }

    private async Task ForgetAsync(string service, int serverId, IReadOnlyList<string> persons, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var wanted = persons.Distinct().ToList();
        var gone = await db.ChatAvatarOwners.Where(o => o.Service == service && o.ServerId == serverId && wanted.Contains(o.Person)).Select(o => o.Hash).Distinct().ToListAsync(ct);
        await db.ChatAvatarOwners.Where(o => o.Service == service && o.ServerId == serverId && wanted.Contains(o.Person)).ExecuteDeleteAsync(ct);
        await DeleteUnusedAsync(db, gone, ct);

        lock (_gate)
            foreach (var person in wanted)
            {
                _owners.Remove((service, serverId, person));
                _announced.Remove((service, serverId, person));
            }
        Tell();
    }

    private static async Task DeleteUnusedAsync(AppDbContext db, List<string> hashes, CancellationToken ct)
    {
        if (hashes.Count == 0) return;
        await db.ChatAvatars.Where(a => hashes.Contains(a.Hash) && !db.ChatAvatarOwners.Any(o => o.Hash == a.Hash)).ExecuteDeleteAsync(ct);
    }
}
