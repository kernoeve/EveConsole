using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Media.Imaging;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

public class NotificationRowVm
{
    public CharacterNotification Record { get; }
    public long   NotificationId { get; }
    public string DateText   { get; }
    public string TypeLabel  { get; }
    public string Character  { get; }
    public string Sender     { get; }
    public string SenderType { get; }
    public string ReadText   { get; }

    // ── Links ─────────────────────────────────────────────────────────────────
    private readonly long   _characterId;
    private readonly long   _senderId;
    private readonly string _senderTypeRaw;

    /// <summary>A notification that arrived under several characters names them all in one
    /// cell, and CharacterId is only one of them — so that row stays plain rather than
    /// opening whichever happened to sort first.</summary>
    public bool HasCharacterLink { get; }
    public bool HasSenderLink => _senderId > 0;

    /// <summary>Every character the notification arrived under, by name.</summary>
    public IReadOnlyList<(long Id, string Name)> Recipients { get; }

    public void OpenCharacter()
        => EntityNavigator.Instance.Entity(EntityKind.Pilot, _characterId);

    /// <summary>ESI names the sender's type outright, including "faction", which the id-range
    /// guess would read as a character.</summary>
    public void OpenSender()
    {
        if (_senderId <= 0) return;
        var kind = _senderTypeRaw == "faction"
            ? EntityKind.Faction
            : EntityLinks.KindOf(_senderId, _senderTypeRaw);
        EntityNavigator.Instance.Entity(kind, _senderId);
    }

    // recipients = every character the notification arrived under, sorted by name.
    public NotificationRowVm(
        CharacterNotification n, IReadOnlyList<(long Id, string Name)> recipients,
        IReadOnlyDictionary<long, string> names)
    {
        Record         = n;
        NotificationId = n.NotificationId;
        DateText       = n.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        TypeLabel      = NotificationTitles.For(n.Type);
        Recipients     = recipients;
        Character      = recipients.Count > 0 ? string.Join(", ", recipients.Select(x => x.Name)) : $"ID {n.CharacterId}";
        Sender         = n.SenderId > 0
            ? (names.TryGetValue(n.SenderId, out var sn) && sn.Length > 0 ? sn : $"ID {n.SenderId}")
            : "—";
        SenderType     = n.SenderType.Length > 0
            ? char.ToUpperInvariant(n.SenderType[0]) + n.SenderType[1..] : "";
        // n.IsRead here is MIN(IsRead) across recipients → Unread if any recipient hasn't read it.
        ReadText       = n.IsRead ? "Read" : "Unread";
        _characterId     = n.CharacterId;
        _senderId        = n.SenderId;
        _senderTypeRaw   = n.SenderType;
        HasCharacterLink = n.CharacterId > 0 && recipients.Count <= 1;
    }
}

/// <summary>A choice in the type filter: the name a type is shown by, and every ESI type shown
/// under it — "NPC standings changed" covers both NPCStandingsLost and NPCStandingsGained.</summary>
public sealed class NotifTypeOption(string label, IReadOnlyList<string> types)
{
    public string                Label { get; } = label;
    public IReadOnlyList<string> Types { get; } = types;
    public bool IsAll => Types.Count == 0;

    /// <summary>ESI's own name, for anyone matching a type against the API documentation.</summary>
    public string? RawTypes => IsAll ? null : string.Join(", ", Types);

    public override string ToString() => Label;
}

/// <summary>
/// The selected notification. A header — who sent it, when, whether it was read, who it came
/// to — over the body, laid out for its type by <see cref="NotificationBody"/>.
/// </summary>
public class NotificationDetailVm
{
    public string       Title      { get; }
    public string       DateText   { get; }
    public NotifValueVm Sender     { get; }
    public string       SenderType { get; }
    public string       ReadText   { get; }

    /// <summary>The characters it came to, as links; their portraits sit at the right.</summary>
    public IReadOnlyList<NotifValueVm> Recipients { get; }
    public IReadOnlyList<NotifValueVm> Portraits  { get; }
    public string MoreRecipients    { get; }
    public bool   HasMoreRecipients => MoreRecipients.Length > 0;

    public NotificationBodyVm Body { get; }
    public bool   NoBody     => Body.IsEmpty;
    public string RawText    { get; }
    public bool   HasRawText => RawText.Length > 0;

    /// <summary>Beside the sender: the structure's own icon for a structure notification, the
    /// sender's portrait or logo otherwise, with a glyph when there is neither.</summary>
    public Bitmap? Icon          { get; }
    public bool    HasIcon       => Icon is not null;
    public bool    NoIcon        => Icon is null;
    public string  FallbackGlyph { get; }

    /// <summary>A crowd of portraits stops saying anything; past this many the rest are a count.</summary>
    private const int MaxPortraits = 6;

    public NotificationDetailVm(NotificationRowVm row, NotificationBodyVm body, Bitmap? icon, string glyph)
    {
        Title      = row.TypeLabel;
        DateText   = row.Record.Timestamp.ToLocalTime().ToString("dddd, MMM d yyyy  HH:mm");
        Sender     = new NotifValueVm
        {
            Text = row.Sender,
            Tip  = row.HasSenderLink ? "Open in the entity browser" : null,
            Open = row.HasSenderLink ? row.OpenSender : null,
        };
        SenderType = row.SenderType;
        ReadText   = row.ReadText;

        Recipients = row.Recipients.Count > 0
            ? [.. row.Recipients.Select(c => new NotifValueVm
                {
                    Text    = c.Name,
                    Tip     = "Open in the entity browser",
                    IconUrl = $"characters/{c.Id}/portrait?size=64",
                    Open    = () => EntityNavigator.Instance.Entity(EntityKind.Pilot, c.Id),
                })]
            : [new NotifValueVm { Text = row.Character }];
        Portraits      = [.. Recipients.Where(r => r.HasIconSlot).Take(MaxPortraits)];
        MoreRecipients = Recipients.Count > MaxPortraits ? $"+{Recipients.Count - MaxPortraits}" : "";
        foreach (var p in Portraits) _ = p.LoadIconAsync();

        Body          = body;
        RawText       = row.Record.Text?.Trim() ?? "";
        Icon          = icon;
        FallbackGlyph = glyph;
    }
}

// Server-side paged view over EsiNotifications: filter (character / type / sender type / date
// range), sort and page all run in the DB, so they apply to the whole table. The selected row's
// raw YAML "text" is formatted for the detail pane below the grid.
public class NotificationsViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly ContractNameResolver            _names;
    private bool _initialized;

    public ObservableCollection<NotificationRowVm> Rows { get; } = new();
    public GridPager Pager { get; }

    public ObservableCollection<ContractPartyOption> Characters  { get; } = new();
    public ObservableCollection<NotifTypeOption>     Types       { get; } = new();
    private static readonly NotifTypeOption AllTypes = new("All types", []);
    public IReadOnlyList<string>                     SenderTypes { get; } = ["All senders", "Corporation", "Character"];

    public IReadOnlyList<GridSortOption> SortOptions { get; } =
    [
        new("Date: newest first", "\"Timestamp\" DESC"),
        new("Date: oldest first", "\"Timestamp\" ASC"),
        new("Type (A → Z)",       TypeOrderToken + " ASC, \"Timestamp\" DESC"),
    ];

    /// <summary>
    /// Types in the order of the names they are shown by. Sorting on ESI's identifier would put
    /// "New corporation application" (CorpAppNewMsg) among the C's; this ranks each type by its
    /// place in the type list instead, as SQL, so the sort still runs in the database. Stands in
    /// for <see cref="TypeOrderToken"/> in the sort; a type first seen after start-up sorts last.
    /// </summary>
    private string _typeOrderSql = "\"Type\"";
    private const string TypeOrderToken = "{type-order}";
    private GridSortOption _selectedSort;
    public GridSortOption SelectedSort
    {
        get => _selectedSort;
        set { this.RaiseAndSetIfChanged(ref _selectedSort, value ?? SortOptions[0]); ResetAndReload(); }
    }

    private ContractPartyOption? _selectedCharacter;
    public ContractPartyOption? SelectedCharacter
    {
        get => _selectedCharacter;
        set { this.RaiseAndSetIfChanged(ref _selectedCharacter, value); ResetAndReload(); }
    }

    private NotifTypeOption _selectedType = AllTypes;
    public NotifTypeOption SelectedType
    {
        get => _selectedType;
        set { this.RaiseAndSetIfChanged(ref _selectedType, value ?? AllTypes); ResetAndReload(); }
    }

    private string _selectedSenderType = "All senders";
    public string SelectedSenderType
    {
        get => _selectedSenderType;
        set { this.RaiseAndSetIfChanged(ref _selectedSenderType, value ?? "All senders"); ResetAndReload(); }
    }

    private DateTime? _fromDate = DateTime.Today.AddDays(-30);
    public DateTime? FromDate
    {
        get => _fromDate;
        set { this.RaiseAndSetIfChanged(ref _fromDate, value); ResetAndReload(); }
    }

    private DateTime? _thruDate;
    public DateTime? ThruDate
    {
        get => _thruDate;
        set { this.RaiseAndSetIfChanged(ref _thruDate, value); ResetAndReload(); }
    }

    private bool _showUnreadOnly;
    public bool ShowUnreadOnly
    {
        get => _showUnreadOnly;
        set { this.RaiseAndSetIfChanged(ref _showUnreadOnly, value); ResetAndReload(); }
    }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        private set { this.RaiseAndSetIfChanged(ref _unreadCount, value); this.RaisePropertyChanged(nameof(UnreadText)); }
    }
    // Unread among the current character/type/sender/date filters (ignores the unread-only toggle).
    public string UnreadText => $"{UnreadCount:N0} unread";

    private NotificationRowVm? _selectedRow;
    public NotificationRowVm? SelectedRow
    {
        get => _selectedRow;
        set { this.RaiseAndSetIfChanged(ref _selectedRow, value); _ = BuildDetailAsync(); }
    }

    private NotificationDetailVm? _detail;
    public NotificationDetailVm? Detail
    {
        get => _detail;
        private set => this.RaiseAndSetIfChanged(ref _detail, value);
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand      { get; }
    public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; }

    public NotificationsViewModel(
        IDbContextFactory<AppDbContext> dbFactory, EsiClient esi, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _names       = new ContractNameResolver(dbFactory, esi, errorLogger);
        _selectedSort = SortOptions[0];
        Pager = new GridPager(ReloadPageAsync);

        RefreshCommand      = ReactiveCommand.CreateFromTask(ReloadPageAsync);
        ClearFiltersCommand = ReactiveCommand.Create(() =>
        {
            _selectedCharacter  = Characters.FirstOrDefault(); this.RaisePropertyChanged(nameof(SelectedCharacter));
            _selectedType       = AllTypes;      this.RaisePropertyChanged(nameof(SelectedType));
            _selectedSenderType = "All senders";  this.RaisePropertyChanged(nameof(SelectedSenderType));
            _fromDate           = DateTime.Today.AddDays(-30); this.RaisePropertyChanged(nameof(FromDate));
            _thruDate           = null; this.RaisePropertyChanged(nameof(ThruDate));
            _showUnreadOnly     = false; this.RaisePropertyChanged(nameof(ShowUnreadOnly));
            ResetAndReload();
        });
        _ = InitAsync();
    }

    private void ResetAndReload()
    {
        if (!_initialized) return;
        Pager.Reset();
        _ = ReloadPageAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var chars = await db.Characters.OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name }).ToListAsync();
            Characters.Clear();
            Characters.Add(new ContractPartyOption("All characters", null));
            foreach (var c in chars)
                Characters.Add(new ContractPartyOption(c.Name, c.Id));
            _selectedCharacter = Characters.FirstOrDefault();
            this.RaisePropertyChanged(nameof(SelectedCharacter));

            // By the name each type is shown under, so the list reads like the grid; types
            // that share a name are one choice.
            var types = await db.EsiNotifications.Select(n => n.Type).Distinct().ToListAsync();
            Types.Clear();
            Types.Add(AllTypes);
            foreach (var g in types.GroupBy(NotificationTitles.For)
                                   .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
                Types.Add(new NotifTypeOption(g.Key, [.. g.OrderBy(t => t, StringComparer.Ordinal)]));
            _selectedType = AllTypes;
            this.RaisePropertyChanged(nameof(SelectedType));

            var rank  = 0;
            var whens = new System.Text.StringBuilder();
            foreach (var option in Types.Where(o => !o.IsAll))
            {
                foreach (var t in option.Types) whens.Append($" WHEN '{t.Replace("'", "''")}' THEN {rank}");
                rank++;
            }
            _typeOrderSql = whens.Length > 0 ? $"CASE \"Type\"{whens} ELSE {rank} END" : "\"Type\"";

            _initialized = true;
            await ReloadPageAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("NotificationsViewModel", "InitAsync", ex);
            StatusText = AppErrorLogger.Line("Error initialising notifications", ex);
        }
    }

    private (string Where, object[] Parameters) BuildFilter()
    {
        var parts = new List<string> { "1=1" };
        var ps    = new List<object>();

        if (_selectedCharacter?.Id is long cid)
        { parts.Add($"\"CharacterId\" = {{{ps.Count}}}"); ps.Add(cid); }

        if (!_selectedType.IsAll)
        {
            var marks = new List<string>();
            foreach (var t in _selectedType.Types) { marks.Add($"{{{ps.Count}}}"); ps.Add(t); }
            parts.Add($"\"Type\" IN ({string.Join(", ", marks)})");
        }

        var senderType = _selectedSenderType switch
        {
            "Corporation" => "corporation",
            "Character"   => "character",
            _             => null,
        };
        if (senderType is not null)
        { parts.Add($"\"SenderType\" = {{{ps.Count}}}"); ps.Add(senderType); }

        if (_fromDate is DateTime fd)
        { parts.Add($"\"Timestamp\" >= {{{ps.Count}}}"); ps.Add(UtcMidnight(fd)); }
        if (_thruDate is DateTime td)
        { parts.Add($"\"Timestamp\" < {{{ps.Count}}}"); ps.Add(UtcMidnight(td.AddDays(1))); }

        return (string.Join(" AND ", parts), ps.ToArray());
    }

    // Treats a picked calendar date as UTC midnight. Building a DateTimeOffset with a zero offset
    // directly from a Local-kind DateTime (what the date picker returns) throws, so use components.
    private static DateTimeOffset UtcMidnight(DateTime d) =>
        new(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero);

    private async Task ReloadPageAsync()
    {
        if (!_initialized || IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var (baseWhere, ps) = BuildFilter();
            string where = baseWhere + (_showUnreadOnly ? " AND \"IsRead\" = FALSE" : "");

            // The same notification is delivered to multiple characters; the grid shows one row per
            // NotificationId, so counts and paging are over DISTINCT NotificationId.
#pragma warning disable EF1002
            // Unread count = distinct notifications with any unread recipient (ignores the toggle).
            UnreadCount = await db.EsiNotifications
                .FromSqlRaw($"SELECT * FROM \"EsiNotifications\" WHERE {baseWhere} AND \"IsRead\" = FALSE", ps)
                .AsNoTracking().Select(n => n.NotificationId).Distinct().CountAsync();

            Pager.TotalCount = await db.EsiNotifications
                .FromSqlRaw($"SELECT * FROM \"EsiNotifications\" WHERE {where}", ps)
                .AsNoTracking().Select(n => n.NotificationId).Distinct().CountAsync();
            Pager.ClampToRange();

            // One representative row per NotificationId (shared fields are identical across
            // recipients); IsRead is MIN so the group reads as unread if any recipient is unread.
            var rows = Pager.TotalCount == 0
                ? new List<CharacterNotification>()
            // ⚠️ Every selected column is either aggregated or grouped. SQLite allows a
            // bare column beside a GROUP BY and picks it from an arbitrary row in the group;
            // PostgreSQL rejects it outright unless the grouping key is the table's primary key,
            // and NotificationId is not — a notification has one row per recipient.
            //
            // Adding them to the key rather than wrapping them in an aggregate is not a
            // workaround: they are genuinely identical across a notification's rows, because it
            // is one notification delivered to several characters. Only CharacterId and IsRead
            // actually vary, and those two are the ones that stay aggregated.
                : await db.EsiNotifications.FromSqlRaw(
                        "SELECT MIN(\"CharacterId\") AS \"CharacterId\", \"NotificationId\", \"Type\", \"SenderId\", " +
                        "\"SenderType\", \"Timestamp\", " + AppDb.AllTrue("\"IsRead\"") + " AS \"IsRead\", \"Text\" FROM \"EsiNotifications\" " +
                        $"WHERE {where} " +
                        "GROUP BY \"NotificationId\", \"Type\", \"SenderId\", \"SenderType\", " +
                        "\"Timestamp\", \"Text\" " +
                        $"ORDER BY {_selectedSort.Sql.Replace(TypeOrderToken, _typeOrderSql)} " +
                        $"LIMIT {GridPager.PageSize} OFFSET {Pager.Offset}", ps)
                    .AsNoTracking().ToListAsync();

            // All characters each page notification arrived under (respecting the character/date/etc.
            // filters, but not the unread toggle — we want every recipient's name).
            var pageIds = rows.Select(r => r.NotificationId).Distinct().ToList();
            var recipients = pageIds.Count == 0
                ? new List<(long NotificationId, long CharacterId)>()
                : (await db.EsiNotifications.FromSqlRaw(
                        $"SELECT * FROM \"EsiNotifications\" WHERE {baseWhere} " +
                        $"AND \"NotificationId\" IN ({string.Join(",", pageIds)})", ps)
                    .AsNoTracking().Select(n => new { n.NotificationId, n.CharacterId }).ToListAsync())
                  .Select(x => (x.NotificationId, x.CharacterId)).ToList();
#pragma warning restore EF1002

            var recipientsByNotif = recipients
                .GroupBy(x => x.NotificationId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.CharacterId).Distinct().ToList());

            var names = await _names.ResolveAsync(
                rows.Select(r => r.SenderId).Concat(recipients.Select(x => x.CharacterId)));

            Rows.Clear();
            foreach (var r in rows)
            {
                IReadOnlyList<(long Id, string Name)> recipientList = recipientsByNotif.TryGetValue(r.NotificationId, out var ids)
                    ? [.. ids.Select(id => (id, names.TryGetValue(id, out var cn) && cn.Length > 0 ? cn : $"ID {id}"))
                             .OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)]
                    : [];
                Rows.Add(new NotificationRowVm(r, recipientList, names));
            }
            SelectedRow = Rows.FirstOrDefault();
            StatusText = Pager.TotalCount == 0 ? "No notifications match these filters." : "";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("NotificationsViewModel", "ReloadPageAsync", ex);
            StatusText = AppErrorLogger.Line("Error loading notifications", ex);
        }
        finally { IsLoading = false; }
    }

    private async Task BuildDetailAsync()
    {
        var row = SelectedRow;
        if (row is null) { Detail = null; return; }
        try
        {
            var body = await NotificationBody.BuildAsync(row.Record.Type, row.Record.Text, _names, _dbFactory);

            // The icon sits beside the sender: a structure notification shows the structure,
            // anything else the sender.
            var f = NotificationSummary.Parse(row.Record.Text);
            var (iconPath, glyph) = NotificationSummary.Icon(
                row.Record.Type, row.Record.SenderId, row.Record.SenderType, f, senderFirst: true);
            var icon = iconPath is null
                ? null
                : await EveImageCache.GetAsync($"https://images.evetech.net/{iconPath}");

            if (ReferenceEquals(row, SelectedRow))   // ignore if selection moved on
                Detail = new NotificationDetailVm(row, body, icon, glyph);
        }
        catch (Exception ex)
        {
            _errorLogger.Log("NotificationsViewModel", "BuildDetail", ex);
            if (ReferenceEquals(row, SelectedRow))
                Detail = new NotificationDetailVm(row, new NotificationBodyVm
                {
                    Notes = [new NotifFieldVm
                    {
                        Label  = AppErrorLogger.Line("Could not lay this notification out", ex),
                        Values = [new NotifValueVm { Text = row.Record.Text?.Trim() ?? "" }],
                    }],
                }, null, "✉");
        }
    }
}
