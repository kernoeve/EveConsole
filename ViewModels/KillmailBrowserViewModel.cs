using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

// ── List row VM ───────────────────────────────────────────────────────────────
public class KillmailListRowVm : ReactiveObject
{
    private readonly int  _victimShipTypeId;
    private readonly long _victimCorpId;
    private readonly long _victimAllianceId;
    private readonly long _fbCorpId;
    private readonly long _fbAllianceId;

    public int            KillMailId        { get; }
    public DateTimeOffset TimeRaw           { get; }
    public string         DateText          { get; }
    public string         TimeText          { get; }
    public string         TotalIskText      { get; }
    public string         ShipName          { get; }
    public int            SystemId          { get; }
    public string         SystemName        { get; }
    public string         ConstellationName { get; }
    public string         RegionName        { get; }
    public string         SecurityText      { get; }
    public string         SecurityColor     { get; }
    public string         VictimName        { get; }
    public string         VictimCorp        { get; }
    public string         VictimAlliance    { get; }
    public string         FbName            { get; }
    public string         FbCorp            { get; }
    public string         FbAlliance        { get; }

    private Bitmap? _shipRender;
    private Bitmap? _victimLogo;
    private Bitmap? _fbLogo;
    public Bitmap? ShipRender  { get => _shipRender;  private set => this.RaiseAndSetIfChanged(ref _shipRender,  value); }
    public Bitmap? VictimLogo  { get => _victimLogo;  private set => this.RaiseAndSetIfChanged(ref _victimLogo,  value); }
    public Bitmap? FbLogo      { get => _fbLogo;      private set => this.RaiseAndSetIfChanged(ref _fbLogo,      value); }

    public KillmailListRowVm(KillmailListRow r)
    {
        KillMailId        = r.KillMailId;
        TimeRaw           = r.KillMailTime;
        DateText          = r.KillMailTime.UtcDateTime.ToString("yyyy-MM-dd");
        TimeText          = r.KillMailTime.UtcDateTime.ToString("HH:mm");
        TotalIskText      = r.TotalIsk > 0 ? FmtIsk(r.TotalIsk) : "";
        ShipName          = r.ShipName;
        SystemId          = r.SystemId;
        SystemName        = r.SystemName;
        ConstellationName = r.ConstellationName;
        RegionName        = r.RegionName;
        VictimName        = r.VictimName;
        VictimCorp        = r.VictimCorp;
        VictimAlliance    = r.VictimAlliance;
        FbName            = r.FbName;
        FbCorp            = r.FbCorp;
        FbAlliance        = r.FbAlliance;
        _victimShipTypeId = r.VictimShipTypeId;
        _victimCorpId     = r.VictimCorpId;
        _victimAllianceId = r.VictimAllianceId;
        _fbCorpId         = r.FbCorpId;
        _fbAllianceId     = r.FbAllianceId;

        var sec       = r.SecurityStatus;
        SecurityText  = sec >= 0.05 ? $"{sec:F1}" : "0.0";
        SecurityColor = sec >= 0.5 ? "#44bb44" : sec >= 0.1 ? "#cccc44" : "#cc4444";
    }

    public Task LoadImagesAsync() => Task.WhenAll(
        _victimShipTypeId > 0
            ? LoadAsync($"https://images.evetech.net/types/{_victimShipTypeId}/render?size=64",  v => ShipRender = v)
            : Task.CompletedTask,
        _victimAllianceId > 0
            ? LoadAsync($"https://images.evetech.net/alliances/{_victimAllianceId}/logo?size=32", v => VictimLogo = v)
            : _victimCorpId > 0
                ? LoadAsync($"https://images.evetech.net/corporations/{_victimCorpId}/logo?size=32", v => VictimLogo = v)
                : Task.CompletedTask,
        _fbAllianceId > 0
            ? LoadAsync($"https://images.evetech.net/alliances/{_fbAllianceId}/logo?size=32", v => FbLogo = v)
            : _fbCorpId > 0
                ? LoadAsync($"https://images.evetech.net/corporations/{_fbCorpId}/logo?size=32", v => FbLogo = v)
                : Task.CompletedTask
    );

    private static async Task LoadAsync(string url, Action<Bitmap?> set)
    {
        var bmp = await EveImageCache.GetAsync(url);
        Dispatcher.UIThread.Post(() => set(bmp));
    }

    private static string FmtIsk(double v) => v switch
    {
        >= 1_000_000_000 => $"{v / 1_000_000_000:F2}B",
        >= 1_000_000     => $"{v / 1_000_000:F2}M",
        >= 1_000         => $"{v / 1_000:F1}K",
        _                => $"{v:F0}",
    };
}

// ── Detail VMs ────────────────────────────────────────────────────────────────
public class KillmailItemVm : ReactiveObject
{
    private readonly int    _typeId;
    private readonly string _iconVariant;

    public int    TypeId        => _typeId;
    public string TypeName      { get; }
    public string QtyDestroyed  { get; }
    public string QtyDropped    { get; }
    public string EstValueText  { get; }
    public bool   HasDestroyed  { get; }
    public bool   HasDropped    { get; }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }

    public KillmailItemVm(KillmailItemRow r)
    {
        _typeId      = r.TypeId;
        // Blueprint render variant, same "bp"/"bpc" path-segment trick the Industry Jobs
        // UI uses (Views/EveImageLoader.cs) — driven by Singleton==2 here rather than
        // industry ActivityId, since a killmail item has no activity to check.
        _iconVariant = r.IsBpc ? "bpc" : r.IsBpo ? "bp" : "icon";
        TypeName     = r.TypeName;
        HasDestroyed = r.QtyDestroyed > 0;
        HasDropped   = r.QtyDropped   > 0;
        QtyDestroyed = r.QtyDestroyed > 0 ? $"x{r.QtyDestroyed} dest" : "";
        QtyDropped   = r.QtyDropped   > 0 ? $"x{r.QtyDropped} drop" : "";
        EstValueText = r.EstValue > 0 ? FmtIsk(r.EstValue) : "";
    }

    public Task LoadIconAsync()
    {
        if (_typeId <= 0) return Task.CompletedTask;
        var url = $"https://images.evetech.net/types/{_typeId}/{_iconVariant}?size=32";
        return EveImageCache.GetAsync(url).ContinueWith(t =>
        {
            var bmp = t.Result;
            Dispatcher.UIThread.Post(() => Icon = bmp);
        }, TaskScheduler.Default);
    }

    private static string FmtIsk(double v) => v switch
    {
        >= 1_000_000_000 => $"{v / 1_000_000_000:F2}B",
        >= 1_000_000     => $"{v / 1_000_000:F2}M",
        >= 1_000         => $"{v / 1_000:F1}K",
        _                => $"{v:F0}",
    };
}

/// <summary>One sub-group within a slot section. SubGroupName is empty for every slot
/// except Cargo Hold (which is split further by market group) — the View only renders a
/// sub-header when it's non-empty.</summary>
public class KillmailSubGroupVm
{
    public string               SubGroupName { get; }
    public List<KillmailItemVm> Items        { get; }

    public KillmailSubGroupVm(KillmailSubGroupRow r)
    {
        SubGroupName = r.SubGroupName;
        Items        = r.Items.Select(i => new KillmailItemVm(i)).ToList();
    }
}

public class KillmailSlotGroupVm
{
    public string                    GroupName  { get; }
    public List<KillmailSubGroupVm>  SubGroups  { get; }

    public KillmailSlotGroupVm(KillmailSlotGroupRow r)
    {
        GroupName = r.GroupName;
        SubGroups = r.SubGroups.Select(g => new KillmailSubGroupVm(g)).ToList();
    }
}

public class KillmailAttackerVm : ReactiveObject
{
    private readonly long _characterId;
    private readonly int  _shipTypeId;
    private readonly int  _weaponTypeId;

    public string CharName     { get; }
    public string CorpName     { get; }
    public string AllianceName { get; }
    public string ShipName     { get; }
    public string WeaponName   { get; }
    public string DamageText   { get; }
    public int    DamageDone   { get; }
    public bool   FinalBlow    { get; }
    public bool   IsTopDamage  { get; set; }
    public string RoleLabel    { get; private set; } = "";
    public string RoleColor    { get; private set; } = "#555566";

    private Bitmap? _portrait;
    private Bitmap? _shipIcon;
    private Bitmap? _weaponIcon;
    public Bitmap? Portrait   { get => _portrait;   private set => this.RaiseAndSetIfChanged(ref _portrait,   value); }
    public Bitmap? ShipIcon   { get => _shipIcon;   private set => this.RaiseAndSetIfChanged(ref _shipIcon,   value); }
    public Bitmap? WeaponIcon { get => _weaponIcon; private set => this.RaiseAndSetIfChanged(ref _weaponIcon, value); }

    public KillmailAttackerVm(KillmailAttackerRow r)
    {
        CharName      = r.CharName;
        CorpName      = r.CorpName;
        AllianceName  = r.AllianceName;
        ShipName      = r.ShipName;
        WeaponName    = r.WeaponName;
        DamageDone    = r.DamageDone;
        DamageText    = $"{r.DamageDone:N0}";
        FinalBlow     = r.FinalBlow;
        _characterId  = r.CharacterId;
        _shipTypeId   = r.ShipTypeId;
        _weaponTypeId = r.WeaponTypeId;
        if (r.FinalBlow) { RoleLabel = "★ FB"; RoleColor = "#c8a84b"; }
    }

    public void MarkTopDamage()
    {
        IsTopDamage = true;
        if (!FinalBlow) { RoleLabel = "▲ TD"; RoleColor = "#6aaa88"; }
        else            { RoleLabel = "★ FB  ▲ TD"; }
    }

    public Task LoadImagesAsync() => Task.WhenAll(
        _characterId  > 0 ? LoadAsync(() => Portrait   = null, $"https://images.evetech.net/characters/{_characterId}/portrait?size=64",  v => Portrait   = v) : Task.CompletedTask,
        _shipTypeId   > 0 ? LoadAsync(() => ShipIcon   = null, $"https://images.evetech.net/types/{_shipTypeId}/render?size=32",           v => ShipIcon   = v) : Task.CompletedTask,
        _weaponTypeId > 0 ? LoadAsync(() => WeaponIcon = null, $"https://images.evetech.net/types/{_weaponTypeId}/icon?size=32",           v => WeaponIcon = v) : Task.CompletedTask
    );

    private static async Task LoadAsync(Action clear, string url, Action<Bitmap?> set)
    {
        var bmp = await EveImageCache.GetAsync(url);
        Dispatcher.UIThread.Post(() => set(bmp));
    }
}

public class KillmailDetailVm : ReactiveObject
{
    private readonly long _victimCharId;
    private readonly long _victimCorpId;
    private readonly long _victimAllianceId;
    private readonly int  _victimShipTypeId;

    public int VictimShipTypeId => _victimShipTypeId;

    public string ShipName        { get; }
    public string VictimName      { get; }
    public string VictimCorp      { get; }
    public string VictimAlliance  { get; }
    public string TimeText        { get; }
    public int    SystemId        { get; }
    public string SystemText      { get; }
    public string LocationText    { get; }
    public string DamageTakenText { get; }
    public string DestroyedText   { get; }
    public string DroppedText     { get; }
    public string TotalIskText    { get; }

    private Bitmap? _victimPortrait;
    private Bitmap? _victimCorpLogo;
    private Bitmap? _victimAllianceLogo;
    private Bitmap? _shipRender;
    public Bitmap? VictimPortrait     { get => _victimPortrait;     private set => this.RaiseAndSetIfChanged(ref _victimPortrait,     value); }
    public Bitmap? VictimCorpLogo     { get => _victimCorpLogo;     private set => this.RaiseAndSetIfChanged(ref _victimCorpLogo,     value); }
    public Bitmap? VictimAllianceLogo { get => _victimAllianceLogo; private set => this.RaiseAndSetIfChanged(ref _victimAllianceLogo, value); }
    public Bitmap? ShipRender         { get => _shipRender;         private set => this.RaiseAndSetIfChanged(ref _shipRender,         value); }

    public List<KillmailSlotGroupVm>  SlotGroups { get; }
    public List<KillmailAttackerVm>   Attackers  { get; }

    public KillmailDetailVm(KillmailDetailData d)
    {
        _victimCharId     = d.VictimCharId;
        _victimCorpId     = d.VictimCorpId;
        _victimAllianceId = d.VictimAllianceId;
        _victimShipTypeId = d.VictimShipTypeId;

        ShipName       = d.ShipName;
        VictimName     = d.VictimName;
        VictimCorp     = d.VictimCorp;
        VictimAlliance = d.VictimAlliance;
        TimeText       = d.KillMailTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        SystemId       = d.SystemId;
        SystemText     = string.IsNullOrEmpty(d.RegionName)
            ? d.SystemName : $"{d.SystemName}  ({d.RegionName})";
        LocationText   = d.LocationText;
        DamageTakenText= $"{d.VictimDamageTaken:N0} dmg";
        DestroyedText  = FmtIsk(d.DestroyedIsk);
        DroppedText    = FmtIsk(d.DroppedIsk);
        TotalIskText   = FmtIsk(d.DestroyedIsk + d.DroppedIsk);
        SlotGroups = d.SlotGroups.Select(g => new KillmailSlotGroupVm(g)).ToList();
        Attackers  = d.Attackers.Select(a => new KillmailAttackerVm(a)).ToList();

        // Mark the highest-damage attacker (may differ from final blow)
        Attackers.OrderByDescending(a => a.DamageDone).FirstOrDefault()?.MarkTopDamage();

        // Load header portrait/logo/ship render, attacker portraits/ship/weapon icons,
        // and item icons asynchronously.
        _ = LoadHeaderImagesAsync();
        _ = Task.WhenAll(Attackers.Select(a => a.LoadImagesAsync()));
        _ = Task.WhenAll(SlotGroups.SelectMany(g => g.SubGroups).SelectMany(sg => sg.Items).Select(i => i.LoadIconAsync()));
    }

    // Portrait is the same height as the ship render (64px); corp/alliance are shown
    // separately (not one-wins-the-other) at half that height (32px) each.
    private Task LoadHeaderImagesAsync() => Task.WhenAll(
        _victimCharId > 0
            ? LoadAsync($"https://images.evetech.net/characters/{_victimCharId}/portrait?size=64", v => VictimPortrait = v)
            : Task.CompletedTask,
        _victimCorpId > 0
            ? LoadAsync($"https://images.evetech.net/corporations/{_victimCorpId}/logo?size=32", v => VictimCorpLogo = v)
            : Task.CompletedTask,
        _victimAllianceId > 0
            ? LoadAsync($"https://images.evetech.net/alliances/{_victimAllianceId}/logo?size=32", v => VictimAllianceLogo = v)
            : Task.CompletedTask,
        _victimShipTypeId > 0
            ? LoadAsync($"https://images.evetech.net/types/{_victimShipTypeId}/render?size=64", v => ShipRender = v)
            : Task.CompletedTask
    );

    private static async Task LoadAsync(string url, Action<Bitmap?> set)
    {
        var bmp = await EveImageCache.GetAsync(url);
        Dispatcher.UIThread.Post(() => set(bmp));
    }

    private static string FmtIsk(double v) => v switch
    {
        >= 1_000_000_000 => $"{v / 1_000_000_000:F2}B ISK",
        >= 1_000_000     => $"{v / 1_000_000:F2}M ISK",
        >= 1_000         => $"{v / 1_000:F1}K ISK",
        _                => $"{v:F0} ISK",
    };
}

// ── Main ViewModel ────────────────────────────────────────────────────────────
public class KillmailBrowserViewModel : ReactiveObject
{
    private readonly KillmailBrowserService _service;

    // Filters — all run server-side in KillmailBrowserService.GetListAsync, not against
    // whatever page happens to already be loaded (with 100K+ rows total and one page
    // loaded at a time, a client-side filter would silently miss anything outside the
    // loaded window). Changing any of them re-queries from scratch via the debounced
    // subscription set up in the constructor, rather than firing on every keystroke.
    // Corp used to be a dropdown of only our own tracked corps (joined through
    // EsiKillMailRefs) — that could only ever show "my corp"'s kills by construction, so
    // it's now a free-text search like Character/Ship/System, matching victim or
    // final-blow-attacker corp by name (any corp, not just tracked ones).
    private DateTime? _filterFrom, _filterThru;
    private string    _filterChar = "", _filterCorp = "", _filterShip = "", _filterSystem = "";

    public DateTime? FilterFrom
    {
        get => _filterFrom;
        set => this.RaiseAndSetIfChanged(ref _filterFrom, value);
    }
    public DateTime? FilterThru
    {
        get => _filterThru;
        set => this.RaiseAndSetIfChanged(ref _filterThru, value);
    }
    public string FilterChar
    {
        get => _filterChar;
        set => this.RaiseAndSetIfChanged(ref _filterChar, value);
    }
    public string FilterCorp
    {
        get => _filterCorp;
        set => this.RaiseAndSetIfChanged(ref _filterCorp, value);
    }
    public string FilterShip
    {
        get => _filterShip;
        set => this.RaiseAndSetIfChanged(ref _filterShip, value);
    }
    public string FilterSystem
    {
        get => _filterSystem;
        set => this.RaiseAndSetIfChanged(ref _filterSystem, value);
    }

    // List — populated directly from whatever the server already filtered/paged, no
    // client-side re-filtering step.
    public ObservableCollection<KillmailListRowVm> KillmailRows { get; } = [];

    private KillmailListRowVm? _selectedKillmail;
    public KillmailListRowVm? SelectedKillmail
    {
        get => _selectedKillmail;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedKillmail, value);
            if (value is not null) _ = LoadDetailAsync(value.KillMailId);
            else Detail = null;
        }
    }

    // Detail
    private KillmailDetailVm? _detail;
    public KillmailDetailVm? Detail
    {
        get => _detail;
        private set => this.RaiseAndSetIfChanged(ref _detail, value);
    }

    // Status
    private bool   _isLoading;
    private string _statusText  = "";
    private string _statusColor = "#555566";
    public bool   IsLoading    { get => _isLoading;    private set => this.RaiseAndSetIfChanged(ref _isLoading,    value); }
    public string StatusText   { get => _statusText;   private set => this.RaiseAndSetIfChanged(ref _statusText,   value); }
    public string StatusColor  { get => _statusColor;  private set => this.RaiseAndSetIfChanged(ref _statusColor,  value); }

    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand      { get; }
    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearFiltersCommand { get; }
    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LoadMoreCommand     { get; }

    /// <summary>Set by MainWindowViewModel — opens the Item Browser tool and selects the
    /// given type id, same wiring as ProductionCalculatorViewModel/
    /// CharacterViewerViewModel's identical property.</summary>
    public Action<int>? NavigateToItemAction { get; set; }

    /// <summary>Set by the shell so a system name opens that system on the Universe map.</summary>
    public Action<int>? NavigateToSystemAction { get; set; }
    public ReactiveUI.ReactiveCommand<int, System.Reactive.Unit> OpenInItemBrowserCommand { get; }
    public ReactiveUI.ReactiveCommand<int, System.Reactive.Unit> OpenSystemMapCommand     { get; }

    // Paging — the underlying table can now hold 100K+ rows (zKillboard backfill/firehose),
    // so the browser loads a page at a time instead of one hard-capped query.
    private int  _offset;
    private bool _hasMore;
    public bool HasMore { get => _hasMore; private set => this.RaiseAndSetIfChanged(ref _hasMore, value); }

    private bool _isLoadingMore;
    public bool IsLoadingMore { get => _isLoadingMore; private set => this.RaiseAndSetIfChanged(ref _isLoadingMore, value); }

    public KillmailBrowserViewModel(KillmailBrowserService service)
    {
        _service = service;

        RefreshCommand = ReactiveUI.ReactiveCommand.CreateFromTask(LoadAsync);
        RefreshCommand.ThrownExceptions.Subscribe(_ => { });

        LoadMoreCommand = ReactiveUI.ReactiveCommand.CreateFromTask(LoadMoreAsync,
            this.WhenAnyValue(x => x.HasMore, x => x.IsLoadingMore, (more, loading) => more && !loading));
        LoadMoreCommand.ThrownExceptions.Subscribe(_ => { });

        OpenInItemBrowserCommand = ReactiveUI.ReactiveCommand.Create<int>(typeId => NavigateToItemAction?.Invoke(typeId));
        OpenSystemMapCommand     = ReactiveUI.ReactiveCommand.Create<int>(id => { if (id > 0) NavigateToSystemAction?.Invoke(id); });

        ClearFiltersCommand = ReactiveUI.ReactiveCommand.Create(() =>
        {
            FilterFrom   = null;
            FilterThru   = null;
            FilterChar   = "";
            FilterCorp   = "";
            FilterShip   = "";
            FilterSystem = "";
        });

        // Filter changes re-query the server, so debounce rather than firing on every
        // keystroke — 400ms after the user stops typing/picking dates.
        this.WhenAnyValue(x => x.FilterFrom, x => x.FilterThru, x => x.FilterChar, x => x.FilterCorp, x => x.FilterShip, x => x.FilterSystem)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(400), RxApp.TaskpoolScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => { var task = LoadAsync(); });

        // Default date range: last 30 days
        _filterFrom = DateTime.Today.AddDays(-30);
        _filterThru = DateTime.Today;

        _ = LoadAsync();
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading   = true;
        StatusColor = "#555566";
        StatusText  = "Loading killmails…";
        _offset     = 0;
        HasMore     = false;
        try
        {
            var page = await _service.GetListAsync(
                _offset, KillmailBrowserService.PageSize,
                _filterFrom is { } f ? DateOnly.FromDateTime(f) : null,
                _filterThru is { } t ? DateOnly.FromDateTime(t) : null,
                _filterChar, _filterCorp, _filterShip, _filterSystem, ct: ct);

            var rows = page.Rows.Select(r => new KillmailListRowVm(r)).ToList();
            KillmailRows.Clear();
            foreach (var r in rows) KillmailRows.Add(r);
            _offset = rows.Count;
            HasMore = page.HasMore;
            _ = Task.WhenAll(rows.Select(r => r.LoadImagesAsync()));
            UpdateStatusText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; StatusColor = "#cc4444"; }
        finally { IsLoading = false; }
    }

    /// <summary>Fetches the next page (same filters/order as the current load) and
    /// appends it — never re-sorts, so it stays consistent with what's already
    /// rendered above it (GetListAsync's ORDER BY KillMailTime DESC is the single
    /// source of truth for row order throughout).</summary>
    private async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (!HasMore || IsLoadingMore) return;

        IsLoadingMore = true;
        try
        {
            var page = await _service.GetListAsync(
                _offset, KillmailBrowserService.PageSize,
                _filterFrom is { } f ? DateOnly.FromDateTime(f) : null,
                _filterThru is { } t ? DateOnly.FromDateTime(t) : null,
                _filterChar, _filterCorp, _filterShip, _filterSystem, ct: ct);

            var newRows = page.Rows.Select(r => new KillmailListRowVm(r)).ToList();
            foreach (var r in newRows) KillmailRows.Add(r);
            _offset += newRows.Count;
            HasMore  = page.HasMore;
            _ = Task.WhenAll(newRows.Select(r => r.LoadImagesAsync()));
            UpdateStatusText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; StatusColor = "#cc4444"; }
        finally { IsLoadingMore = false; }
    }

    private void UpdateStatusText()
    {
        StatusText = HasMore
            ? $"{KillmailRows.Count:N0} killmails loaded — more available, click Load More"
            : $"{KillmailRows.Count:N0} killmails";
    }

    public void SelectById(int killMailId)
    {
        var row = KillmailRows.FirstOrDefault(r => r.KillMailId == killMailId);
        if (row is not null)
        {
            SelectedKillmail = row;
        }
        else
        {
            // Kill not loaded yet — reload then select
            _ = LoadAndSelectAsync(killMailId);
        }
    }

    private async Task LoadAndSelectAsync(int killMailId)
    {
        await LoadAsync();
        var row = KillmailRows.FirstOrDefault(r => r.KillMailId == killMailId);
        if (row is not null) SelectedKillmail = row;
    }

    private async Task LoadDetailAsync(int killMailId, CancellationToken ct = default)
    {
        try
        {
            var data = await _service.GetDetailAsync(killMailId, ct);
            Detail = data is not null ? new KillmailDetailVm(data) : null;
        }
        catch (OperationCanceledException) { }
        catch { Detail = null; }
    }
}
