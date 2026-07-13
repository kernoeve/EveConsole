using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Media.Imaging;
using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

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

    // characters = the (comma-joined) names of every character the notification arrived under.
    public NotificationRowVm(
        CharacterNotification n, string characters,
        IReadOnlyDictionary<long, string> names)
    {
        Record         = n;
        NotificationId = n.NotificationId;
        DateText       = n.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        TypeLabel      = NotificationFormatter.Humanize(n.Type);
        Character      = characters.Length > 0 ? characters : $"ID {n.CharacterId}";
        Sender         = n.SenderId > 0
            ? (names.TryGetValue(n.SenderId, out var sn) && sn.Length > 0 ? sn : $"ID {n.SenderId}")
            : "—";
        SenderType     = n.SenderType.Length > 0
            ? char.ToUpperInvariant(n.SenderType[0]) + n.SenderType[1..] : "";
        // n.IsRead here is MIN(IsRead) across recipients → Unread if any recipient hasn't read it.
        ReadText       = n.IsRead ? "Read" : "Unread";
    }
}

public class NotificationDetailVm
{
    public string TypeLabel  { get; }
    public string DateText   { get; }
    public string Character  { get; }
    public string Sender     { get; }
    public string ReadText   { get; }
    public string Body       { get; }

    // Same icon treatment as the Overview notifications list: sender portrait / corp-alliance logo
    // / structure-type icon, with a glyph fallback when there's no image.
    public Bitmap? Icon          { get; }
    public bool    HasIcon       => Icon is not null;
    public bool    NoIcon        => Icon is null;
    public string  FallbackGlyph { get; }

    public NotificationDetailVm(NotificationRowVm row, string body, Bitmap? icon, string glyph)
    {
        TypeLabel = row.TypeLabel;
        DateText  = row.Record.Timestamp.ToLocalTime().ToString("dddd, MMM d yyyy  HH:mm");
        Character = row.Character;
        Sender    = row.SenderType.Length > 0 ? $"{row.Sender} ({row.SenderType})" : row.Sender;
        ReadText  = row.ReadText;
        Body      = body.Length > 0 ? body : "(no details)";
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
    public ObservableCollection<string>              Types       { get; } = new();
    public IReadOnlyList<string>                     SenderTypes { get; } = ["All senders", "Corporation", "Character"];

    public IReadOnlyList<GridSortOption> SortOptions { get; } =
    [
        new("Date: newest first", "Timestamp DESC"),
        new("Date: oldest first", "Timestamp ASC"),
        new("Type (A → Z)",       "Type ASC, Timestamp DESC"),
    ];
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

    private string _selectedType = "All types";
    public string SelectedType
    {
        get => _selectedType;
        set { this.RaiseAndSetIfChanged(ref _selectedType, value ?? "All types"); ResetAndReload(); }
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
            _selectedType       = "All types";   this.RaisePropertyChanged(nameof(SelectedType));
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

            var types = await db.EsiNotifications.Select(n => n.Type).Distinct().OrderBy(t => t).ToListAsync();
            Types.Clear();
            Types.Add("All types");
            foreach (var t in types) Types.Add(t);

            _initialized = true;
            await ReloadPageAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("NotificationsViewModel", "InitAsync", ex);
            StatusText = "Error initialising notifications.";
        }
    }

    private (string Where, object[] Parameters) BuildFilter()
    {
        var parts = new List<string> { "1=1" };
        var ps    = new List<object>();

        if (_selectedCharacter?.Id is long cid)
        { parts.Add($"CharacterId = {{{ps.Count}}}"); ps.Add(cid); }

        if (_selectedType is { Length: > 0 } t && t != "All types")
        { parts.Add($"Type = {{{ps.Count}}}"); ps.Add(t); }

        var senderType = _selectedSenderType switch
        {
            "Corporation" => "corporation",
            "Character"   => "character",
            _             => null,
        };
        if (senderType is not null)
        { parts.Add($"SenderType = {{{ps.Count}}}"); ps.Add(senderType); }

        if (_fromDate is DateTime fd)
        { parts.Add($"Timestamp >= {{{ps.Count}}}"); ps.Add(UtcMidnight(fd)); }
        if (_thruDate is DateTime td)
        { parts.Add($"Timestamp < {{{ps.Count}}}"); ps.Add(UtcMidnight(td.AddDays(1))); }

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
            string where = baseWhere + (_showUnreadOnly ? " AND IsRead = 0" : "");

            // The same notification is delivered to multiple characters; the grid shows one row per
            // NotificationId, so counts and paging are over DISTINCT NotificationId.
#pragma warning disable EF1002
            // Unread count = distinct notifications with any unread recipient (ignores the toggle).
            UnreadCount = await db.EsiNotifications
                .FromSqlRaw($"SELECT * FROM EsiNotifications WHERE {baseWhere} AND IsRead = 0", ps)
                .AsNoTracking().Select(n => n.NotificationId).Distinct().CountAsync();

            Pager.TotalCount = await db.EsiNotifications
                .FromSqlRaw($"SELECT * FROM EsiNotifications WHERE {where}", ps)
                .AsNoTracking().Select(n => n.NotificationId).Distinct().CountAsync();
            Pager.ClampToRange();

            // One representative row per NotificationId (shared fields are identical across
            // recipients); IsRead is MIN so the group reads as unread if any recipient is unread.
            var rows = Pager.TotalCount == 0
                ? new List<CharacterNotification>()
                : await db.EsiNotifications.FromSqlRaw(
                        "SELECT MIN(CharacterId) AS CharacterId, NotificationId, Type, SenderId, " +
                        "SenderType, Timestamp, MIN(IsRead) AS IsRead, Text FROM EsiNotifications " +
                        $"WHERE {where} GROUP BY NotificationId " +
                        $"ORDER BY {_selectedSort.Sql} LIMIT {GridPager.PageSize} OFFSET {Pager.Offset}", ps)
                    .AsNoTracking().ToListAsync();

            // All characters each page notification arrived under (respecting the character/date/etc.
            // filters, but not the unread toggle — we want every recipient's name).
            var pageIds = rows.Select(r => r.NotificationId).Distinct().ToList();
            var recipients = pageIds.Count == 0
                ? new List<(long NotificationId, long CharacterId)>()
                : (await db.EsiNotifications.FromSqlRaw(
                        $"SELECT * FROM EsiNotifications WHERE {baseWhere} " +
                        $"AND NotificationId IN ({string.Join(",", pageIds)})", ps)
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
                var chars = recipientsByNotif.TryGetValue(r.NotificationId, out var ids)
                    ? string.Join(", ", ids.Select(id => names.TryGetValue(id, out var cn) && cn.Length > 0 ? cn : $"ID {id}")
                                            .OrderBy(s => s))
                    : "";
                Rows.Add(new NotificationRowVm(r, chars, names));
            }
            SelectedRow = Rows.FirstOrDefault();
            StatusText = Pager.TotalCount == 0 ? "No notifications match these filters." : "";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("NotificationsViewModel", "ReloadPageAsync", ex);
            StatusText = "Error loading notifications.";
        }
        finally { IsLoading = false; }
    }

    private async Task BuildDetailAsync()
    {
        var row = SelectedRow;
        if (row is null) { Detail = null; return; }
        try
        {
            var body = await NotificationFormatter.FormatAsync(row.Record.Text, _names, _dbFactory);

            // Resolve the notification icon the same way the Overview list does.
            var f = NotificationSummary.Parse(row.Record.Text);
            var (iconPath, glyph) = NotificationSummary.Icon(
                row.Record.Type, row.Record.SenderId, row.Record.SenderType, f);
            var icon = iconPath is null
                ? null
                : await EveImageCache.GetAsync($"https://images.evetech.net/{iconPath}");

            if (ReferenceEquals(row, SelectedRow))   // ignore if selection moved on
                Detail = new NotificationDetailVm(row, body, icon, glyph);
        }
        catch (Exception ex)
        {
            _errorLogger.Log("NotificationsViewModel", "BuildDetail", ex);
            Detail = new NotificationDetailVm(row, row.Record.Text ?? "", null, "✉");
        }
    }
}
