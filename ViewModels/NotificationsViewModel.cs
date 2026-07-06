using System.Collections.ObjectModel;
using System.Reactive;
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

    public NotificationRowVm(
        CharacterNotification n,
        IReadOnlyDictionary<long, string> names)
    {
        Record         = n;
        NotificationId = n.NotificationId;
        DateText       = n.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        TypeLabel      = NotificationFormatter.Humanize(n.Type);
        Character      = names.TryGetValue(n.CharacterId, out var cn) && cn.Length > 0 ? cn : $"ID {n.CharacterId}";
        Sender         = n.SenderId > 0
            ? (names.TryGetValue(n.SenderId, out var sn) && sn.Length > 0 ? sn : $"ID {n.SenderId}")
            : "—";
        SenderType     = n.SenderType.Length > 0
            ? char.ToUpperInvariant(n.SenderType[0]) + n.SenderType[1..] : "";
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

    public NotificationDetailVm(NotificationRowVm row, string body)
    {
        TypeLabel = row.TypeLabel;
        DateText  = row.Record.Timestamp.ToLocalTime().ToString("dddd, MMM d yyyy  HH:mm");
        Character = row.Character;
        Sender    = row.SenderType.Length > 0 ? $"{row.Sender} ({row.SenderType})" : row.Sender;
        ReadText  = row.ReadText;
        Body      = body.Length > 0 ? body : "(no details)";
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
        { parts.Add($"Timestamp >= {{{ps.Count}}}"); ps.Add(new DateTimeOffset(fd.Date, TimeSpan.Zero)); }
        if (_thruDate is DateTime td)
        { parts.Add($"Timestamp < {{{ps.Count}}}"); ps.Add(new DateTimeOffset(td.Date.AddDays(1), TimeSpan.Zero)); }

        return (string.Join(" AND ", parts), ps.ToArray());
    }

    private async Task ReloadPageAsync()
    {
        if (!_initialized || IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var (where, ps) = BuildFilter();

#pragma warning disable EF1002
            Pager.TotalCount = await db.EsiNotifications
                .FromSqlRaw($"SELECT * FROM EsiNotifications WHERE {where}", ps)
                .AsNoTracking().CountAsync();
            Pager.ClampToRange();

            var rows = Pager.TotalCount == 0
                ? new List<CharacterNotification>()
                : await db.EsiNotifications.FromSqlRaw(
                        $"SELECT * FROM EsiNotifications WHERE {where} " +
                        $"ORDER BY {_selectedSort.Sql} LIMIT {GridPager.PageSize} OFFSET {Pager.Offset}", ps)
                    .AsNoTracking().ToListAsync();
#pragma warning restore EF1002

            var names = await _names.ResolveAsync(
                rows.SelectMany(r => new[] { r.CharacterId, r.SenderId }));

            Rows.Clear();
            foreach (var r in rows) Rows.Add(new NotificationRowVm(r, names));
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
            if (ReferenceEquals(row, SelectedRow))   // ignore if selection moved on
                Detail = new NotificationDetailVm(row, body);
        }
        catch (Exception ex)
        {
            _errorLogger.Log("NotificationsViewModel", "BuildDetail", ex);
            Detail = new NotificationDetailVm(row, row.Record.Text ?? "");
        }
    }
}
