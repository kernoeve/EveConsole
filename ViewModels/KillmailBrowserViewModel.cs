using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveCortex.Models;
using EveCortex.Services;
using ReactiveUI;

namespace EveCortex.ViewModels;

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
    private readonly int _typeId;

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
        var url = $"https://images.evetech.net/types/{_typeId}/icon?size=32";
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

public class KillmailSlotGroupVm
{
    public string                    GroupName { get; }
    public List<KillmailItemVm>      Items     { get; }

    public KillmailSlotGroupVm(string groupName, List<KillmailItemRow> items)
    {
        GroupName = groupName;
        Items     = items.Select(i => new KillmailItemVm(i)).ToList();
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

public class KillmailDetailVm
{
    public string ShipName        { get; }
    public string VictimName      { get; }
    public string VictimCorp      { get; }
    public string VictimAlliance  { get; }
    public string TimeText        { get; }
    public string SystemText      { get; }
    public string DamageTakenText { get; }
    public string DestroyedText   { get; }
    public string DroppedText     { get; }
    public string TotalIskText    { get; }

    public List<KillmailSlotGroupVm>  SlotGroups { get; }
    public List<KillmailAttackerVm>   Attackers  { get; }

    public KillmailDetailVm(KillmailDetailData d)
    {
        ShipName       = d.ShipName;
        VictimName     = d.VictimName;
        VictimCorp     = d.VictimCorp;
        VictimAlliance = d.VictimAlliance;
        TimeText       = d.KillMailTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        SystemText     = string.IsNullOrEmpty(d.RegionName)
            ? d.SystemName : $"{d.SystemName}  ({d.RegionName})";
        DamageTakenText= $"{d.VictimDamageTaken:N0} dmg";
        DestroyedText  = FmtIsk(d.DestroyedIsk);
        DroppedText    = FmtIsk(d.DroppedIsk);
        TotalIskText   = FmtIsk(d.DestroyedIsk + d.DroppedIsk);
        SlotGroups = d.SlotGroups.Select(g => new KillmailSlotGroupVm(g.SlotGroup, g.Items)).ToList();
        Attackers  = d.Attackers.Select(a => new KillmailAttackerVm(a)).ToList();

        // Mark the highest-damage attacker (may differ from final blow)
        Attackers.OrderByDescending(a => a.DamageDone).FirstOrDefault()?.MarkTopDamage();

        // Load portraits/ship/weapon icons and item icons asynchronously
        _ = Task.WhenAll(Attackers.Select(a => a.LoadImagesAsync()));
        _ = Task.WhenAll(SlotGroups.SelectMany(g => g.Items).Select(i => i.LoadIconAsync()));
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

    // Corp selection — Id=0 means "all corps"
    public static readonly Corporation AllCorps = new() { Id = 0, Name = "All Corps" };
    public ObservableCollection<Corporation> Corps       { get; }
    public ObservableCollection<Corporation> CorpsWithAll { get; }

    private Corporation? _selectedCorp;
    public Corporation? SelectedCorp
    {
        get => _selectedCorp;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCorp, value);
            if (value is not null) _ = LoadAsync();
        }
    }

    // Filters
    private DateTime? _filterFrom, _filterThru;
    private string    _filterChar = "", _filterShip = "", _filterSystem = "";

    public DateTime? FilterFrom
    {
        get => _filterFrom;
        set { this.RaiseAndSetIfChanged(ref _filterFrom, value); ApplyFilter(); }
    }
    public DateTime? FilterThru
    {
        get => _filterThru;
        set { this.RaiseAndSetIfChanged(ref _filterThru, value); ApplyFilter(); }
    }
    public string FilterChar
    {
        get => _filterChar;
        set { this.RaiseAndSetIfChanged(ref _filterChar, value); ApplyFilter(); }
    }
    public string FilterShip
    {
        get => _filterShip;
        set { this.RaiseAndSetIfChanged(ref _filterShip, value); ApplyFilter(); }
    }
    public string FilterSystem
    {
        get => _filterSystem;
        set { this.RaiseAndSetIfChanged(ref _filterSystem, value); ApplyFilter(); }
    }

    // List
    private List<KillmailListRowVm> _allRows = [];
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

    public KillmailBrowserViewModel(KillmailBrowserService service,
                                    ObservableCollection<Corporation> corps)
    {
        _service = service;
        Corps    = corps;

        CorpsWithAll = new ObservableCollection<Corporation> { AllCorps };
        foreach (var c in corps) CorpsWithAll.Add(c);
        corps.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (Corporation c in e.NewItems) CorpsWithAll.Add(c);
            if (e.OldItems is not null)
                foreach (Corporation c in e.OldItems) CorpsWithAll.Remove(c);
        };

        RefreshCommand = ReactiveUI.ReactiveCommand.CreateFromTask(LoadAsync);
        RefreshCommand.ThrownExceptions.Subscribe(_ => { });

        ClearFiltersCommand = ReactiveUI.ReactiveCommand.Create(() =>
        {
            _filterFrom   = null; this.RaisePropertyChanged(nameof(FilterFrom));
            _filterThru   = null; this.RaisePropertyChanged(nameof(FilterThru));
            _filterChar   = "";   this.RaisePropertyChanged(nameof(FilterChar));
            _filterShip   = "";   this.RaisePropertyChanged(nameof(FilterShip));
            _filterSystem = "";   this.RaisePropertyChanged(nameof(FilterSystem));
            ApplyFilter();
        });

        // Default date range: last 90 days
        _filterFrom = DateTime.Today.AddDays(-90);
        _filterThru = DateTime.Today;

        // Default to "All Corps" so the list loads immediately on first open
        SelectedCorp = AllCorps;
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        if (_selectedCorp is null) { StatusText = "Select a corporation"; return; }
        var corpId = (long)_selectedCorp.Id;  // 0 = all corps, positive = specific corp

        IsLoading   = true;
        StatusColor = "#555566";
        StatusText  = "Loading killmails…";
        try
        {
            var rows = await _service.GetListAsync(corpId, ct);
            _allRows = rows.Select(r => new KillmailListRowVm(r)).ToList();
            _ = Task.WhenAll(_allRows.Select(r => r.LoadImagesAsync()));
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; StatusColor = "#cc4444"; }
        finally { IsLoading = false; }
    }

    private void ApplyFilter()
    {
        var charF = _filterChar.Trim();
        var shipF = _filterShip.Trim();
        var sysF  = _filterSystem.Trim();

        KillmailRows.Clear();
        foreach (var r in _allRows)
        {
            if (_filterFrom.HasValue && r.TimeRaw.UtcDateTime.Date < _filterFrom.Value.Date) continue;
            if (_filterThru.HasValue && r.TimeRaw.UtcDateTime.Date > _filterThru.Value.Date) continue;
            if (charF.Length > 0 &&
                !r.VictimName.Contains(charF, StringComparison.OrdinalIgnoreCase) &&
                !r.FbName.Contains(charF, StringComparison.OrdinalIgnoreCase)) continue;
            if (shipF.Length > 0 && !r.ShipName.Contains(shipF, StringComparison.OrdinalIgnoreCase)) continue;
            if (sysF.Length  > 0 &&
                !r.SystemName.Contains(sysF, StringComparison.OrdinalIgnoreCase) &&
                !r.RegionName.Contains(sysF, StringComparison.OrdinalIgnoreCase)) continue;
            KillmailRows.Add(r);
        }
        StatusText = $"{KillmailRows.Count:N0} killmails";
    }

    public void SelectById(int killMailId)
    {
        // If no corp selected yet, pick the first available one
        if (_selectedCorp is null && Corps.Count > 0)
        {
            _selectedCorp = Corps[0];
            this.RaisePropertyChanged(nameof(SelectedCorp));
        }

        var row = KillmailRows.FirstOrDefault(r => r.KillMailId == killMailId)
                  ?? _allRows.FirstOrDefault(r => r.KillMailId == killMailId);
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
        var row = _allRows.FirstOrDefault(r => r.KillMailId == killMailId);
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
