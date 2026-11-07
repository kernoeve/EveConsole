using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.ClientLayouts;
using EveConsole.Views;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A saved layout, in the Layouts list and in a monitor's choice of layout.</summary>
public sealed class LayoutRowVm(int? id, string name, LayoutArea root) : ReactiveObject
{
    public int? Id { get; set; } = id;
    public LayoutArea Root { get; } = root;
    private string _name = name;
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public override string ToString() => Name;
}

/// <summary>A choice of when a monitor's panels start.</summary>
public sealed record PanelStartChoice(PanelStart Value, string Label);

/// <summary>A client position on the chosen monitor, with its group of characters.</summary>
public sealed class ClientCellVm : ReactiveObject
{
    public ClientCellVm(string areaId, string title, string size, bool small, IEnumerable<string> names, Func<ClientCellVm, string, Task> add, Func<string, Task> remove)
    {
        AreaId = areaId;
        Title = title;
        Size = size;
        Small = small;
        foreach (var name in names) Characters.Add(new CellCharacterVm(name, remove));
        AddCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var name = (NewName ?? "").Trim();
            if (name.Length == 0) return;
            await add(this, name);
        });
    }

    public string AreaId { get; }
    public string Title { get; }
    public string Size { get; }
    public bool Small { get; }
    public ObservableCollection<CellCharacterVm> Characters { get; } = [];
    public ReactiveCommand<Unit, Unit> AddCommand { get; }

    private string? _newName;
    public string? NewName { get => _newName; set => this.RaiseAndSetIfChanged(ref _newName, value); }

    private IReadOnlyList<string> _candidates = [];
    public IReadOnlyList<string> Candidates { get => _candidates; set => this.RaiseAndSetIfChanged(ref _candidates, value); }
}

public sealed class CellCharacterVm
{
    public CellCharacterVm(string name, Func<string, Task> remove)
    {
        Name = name;
        RemoveCommand = ReactiveCommand.CreateFromTask(() => remove(name));
    }

    public string Name { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}

/// <summary>Another desktop that shares this database. Its name can be changed from here too.</summary>
public sealed class OtherDeviceVm : ReactiveObject
{
    private readonly Func<string, string, Task> _rename;
    private string _name;

    public OtherDeviceVm(string id, string name, Func<string, string, Task> rename, Func<string, Task> takeOver)
    {
        Id = id;
        _name = name;
        _rename = rename;
        TakeOverCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!Confirming)
            {
                Confirming = true;
                return;
            }
            await takeOver(Id);
        });
    }

    public string Id { get; }
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public ReactiveCommand<Unit, Unit> TakeOverCommand { get; }

    private bool _confirming;
    public bool Confirming
    {
        get => _confirming;
        private set
        {
            this.RaiseAndSetIfChanged(ref _confirming, value);
            this.RaisePropertyChanged(nameof(TakeOverLabel));
        }
    }
    public string TakeOverLabel => Confirming ? ClientLayoutsText.TakeOverConfirm : ClientLayoutsText.TakeOver;

    public void SaveName() => _ = _rename(Id, Name);
}

/// <summary>
/// The Client Layouts tool: a map of this desktop's monitors, and the layouts they can use.
/// Everything saves as it's changed, but nothing moves on screen until Apply.
/// </summary>
public sealed class ClientLayoutsViewModel : ReactiveObject, IPeriodicRefresh
{
    private readonly ClientLayoutStore _store;
    private readonly ClientLayoutService _service;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger _errorLogger;

    private string _deviceId = "";
    private string _appliedJson = "";
    private List<DesktopMonitor> _monitors = [];
    private List<ClientLayoutMonitor> _monitorRows = [];
    private List<ClientLayoutCharacter> _characters = [];
    private List<string> _appCharacters = [];
    private bool _opened;
    private bool _loading;
    private string? _selectedMonitorKey;

    public ClientLayoutsViewModel(ClientLayoutStore store, ClientLayoutService service, IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _store = store;
        _service = service;
        _dbFactory = dbFactory;
        _errorLogger = errorLogger;

        PanelStarts =
        [
            new PanelStartChoice(PanelStart.WithConsole, ClientLayoutsText.PanelStartWithConsole),
            new PanelStartChoice(PanelStart.WithEve, ClientLayoutsText.PanelStartWithEve),
        ];
        LayoutChoices.Add(NoLayout);

        PickMonitor = new ActionCommand(key => { _selectedMonitorKey = key; ShowMonitor(); });
        LayoutEdited = new ActionCommand(edited => _ = Run(SaveEditedLayoutAsync));
        ApplyCommand = ReactiveCommand.CreateFromTask(() => Run(ApplyAsync));
        NewLayoutCommand = ReactiveCommand.CreateFromTask(() => Run(NewLayoutAsync));
        DuplicateLayoutCommand = ReactiveCommand.CreateFromTask(() => Run(DuplicateLayoutAsync));
        DeleteLayoutCommand = ReactiveCommand.CreateFromTask(() => Run(DeleteLayoutAsync));

        service.NoticesChanged += () => Dispatcher.UIThread.Post(ShowNotices);
        service.SeenCharactersChanged += () => Dispatcher.UIThread.Post(ShowCandidates);
    }

    public bool AutoRefreshEnabled
    {
        get => _opened;
        set
        {
            var first = value && !_opened;
            _opened = value;
            if (first) _ = Run(LoadAsync);
        }
    }

    // ---- Shown on screen ----

    public LayoutRowVm NoLayout { get; } = new(null, ClientLayoutsText.NoLayout, new LayoutArea());
    public ObservableCollection<LayoutRowVm> LayoutChoices { get; } = [];
    public ObservableCollection<LayoutRowVm> Layouts { get; } = [];
    public ObservableCollection<ClientCellVm> Cells { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> LayoutWarnings { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];
    public ObservableCollection<OtherDeviceVm> OtherDevices { get; } = [];
    public ObservableCollection<PanelPreviewChoice> PreviewChoices { get; } = [];
    public IReadOnlyList<PanelStartChoice> PanelStarts { get; }

    public ICommandLike PickMonitor { get; }
    public ICommandLike LayoutEdited { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> NewLayoutCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateLayoutCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteLayoutCommand { get; }

    private IReadOnlyList<MonitorMapItem> _mapItems = [];
    public IReadOnlyList<MonitorMapItem> MapItems { get => _mapItems; private set => this.RaiseAndSetIfChanged(ref _mapItems, value); }

    private string _deviceName = "";
    public string DeviceName { get => _deviceName; set => this.RaiseAndSetIfChanged(ref _deviceName, value); }

    private bool _placementOn;
    public bool PlacementOn
    {
        get => _placementOn;
        set
        {
            if (_placementOn == value) return;
            this.RaiseAndSetIfChanged(ref _placementOn, value);
            if (_loading) return;
            _ = Run(async () =>
            {
                await _store.SetPlacementOnAsync(_deviceId, value);
                _service.RequestReload();
            });
        }
    }

    private string _pending = "";
    public string Pending { get => _pending; private set => this.RaiseAndSetIfChanged(ref _pending, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _hasNotices;
    public bool HasNotices { get => _hasNotices; private set => this.RaiseAndSetIfChanged(ref _hasNotices, value); }

    private bool _hasWarnings;
    public bool HasWarnings { get => _hasWarnings; private set => this.RaiseAndSetIfChanged(ref _hasWarnings, value); }

    private bool _hasLayoutWarnings;
    public bool HasLayoutWarnings { get => _hasLayoutWarnings; private set => this.RaiseAndSetIfChanged(ref _hasLayoutWarnings, value); }

    private bool _hasOtherDevices;
    public bool HasOtherDevices { get => _hasOtherDevices; private set => this.RaiseAndSetIfChanged(ref _hasOtherDevices, value); }

    private bool _hasMonitor;
    public bool HasMonitor { get => _hasMonitor; private set => this.RaiseAndSetIfChanged(ref _hasMonitor, value); }

    private bool _hasNoCells;
    public bool HasNoCells { get => _hasNoCells; private set => this.RaiseAndSetIfChanged(ref _hasNoCells, value); }

    private string _monitorTitle = "";
    public string MonitorTitle { get => _monitorTitle; private set => this.RaiseAndSetIfChanged(ref _monitorTitle, value); }

    // ---- The chosen monitor's settings ----

    private LayoutRowVm? _monitorLayout;
    public LayoutRowVm? MonitorLayout
    {
        get => _monitorLayout;
        set
        {
            // A ComboBox reports null while its list is being changed; that isn't a choice.
            if (value == null || ReferenceEquals(_monitorLayout, value)) return;
            this.RaiseAndSetIfChanged(ref _monitorLayout, value);
            SaveMonitorSoon();
        }
    }

    private PanelStartChoice? _monitorPanelStart;
    public PanelStartChoice? MonitorPanelStart
    {
        get => _monitorPanelStart;
        set
        {
            if (value == null || _monitorPanelStart == value) return;
            this.RaiseAndSetIfChanged(ref _monitorPanelStart, value);
            SaveMonitorSoon();
        }
    }

    private bool _monitorFull;
    public bool MonitorFull
    {
        get => _monitorFull;
        set
        {
            if (_monitorFull == value) return;
            this.RaiseAndSetIfChanged(ref _monitorFull, value);
            SaveMonitorSoon();
        }
    }

    // ---- The layout being edited ----

    private LayoutRowVm? _selectedLayout;
    public LayoutRowVm? SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedLayout, value);
            SelectedId = null;
            this.RaisePropertyChanged(nameof(EditingRoot));
            this.RaisePropertyChanged(nameof(HasLayout));
            ShowLayoutWarnings();
        }
    }

    public LayoutArea? EditingRoot => SelectedLayout?.Root;
    public bool HasLayout => SelectedLayout != null;

    private int _revision;
    public int Revision { get => _revision; private set => this.RaiseAndSetIfChanged(ref _revision, value); }

    private PanelPreviewChoice? _previewOn;
    public PanelPreviewChoice? PreviewOn
    {
        get => _previewOn;
        set
        {
            if (value == null) return;
            this.RaiseAndSetIfChanged(ref _previewOn, value);
            this.RaisePropertyChanged(nameof(PreviewSpace));
        }
    }
    public LayoutRect PreviewSpace => PreviewOn?.Space ?? new LayoutRect(0, 0, 3840, 2112);

    private string? _selectedId;
    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedId, value);
            ShowSelection();
        }
    }

    private bool _hasNamed;
    public bool HasNamed { get => _hasNamed; private set => this.RaiseAndSetIfChanged(ref _hasNamed, value); }

    private bool _hasSize;
    public bool HasSize { get => _hasSize; private set => this.RaiseAndSetIfChanged(ref _hasSize, value); }

    private string _selectedName = "";
    public string SelectedName { get => _selectedName; set => this.RaiseAndSetIfChanged(ref _selectedName, value); }

    private decimal? _selectedSize;
    public decimal? SelectedSize { get => _selectedSize; set => this.RaiseAndSetIfChanged(ref _selectedSize, value); }

    // ---- Loading ----

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var device = await _store.ThisDeviceAsync();
            _deviceId = device.Id;
            _appliedJson = AppliedDesktop.Read(device.Applied).Write();
            DeviceName = device.Name;
            PlacementOn = device.PlacementOn;

            _monitors = ClientWindows.FindMonitors();
            _monitorRows = await _store.ListMonitorsAsync(_deviceId);
            _characters = await _store.ListCharactersAsync(_deviceId);

            var held = SelectedLayout?.Id;
            var layouts = await _store.ListLayoutsAsync();
            Layouts.Clear();
            foreach (var row in layouts) Layouts.Add(new LayoutRowVm(row.Id, row.Name, ClientLayoutJson.Read(row.Definition)));
            RefillChoices();
            SelectedLayout = Layouts.FirstOrDefault(l => l.Id == held) ?? Layouts.FirstOrDefault();

            PreviewChoices.Clear();
            foreach (var (monitor, number) in NumberedMonitors())
                PreviewChoices.Add(new PanelPreviewChoice(MonitorLabel(monitor, number), monitor.Work));
            PreviewOn ??= PreviewChoices.FirstOrDefault(p => p.Space == _monitors.FirstOrDefault(m => m.Primary)?.Work) ?? PreviewChoices.FirstOrDefault();

            await using (var db = await _dbFactory.CreateDbContextAsync())
                _appCharacters = await db.Characters.AsNoTracking().Select(c => c.Name).ToListAsync();

            var devices = await _store.ListDevicesAsync();
            OtherDevices.Clear();
            foreach (var other in devices.Where(d => d.Id != _deviceId))
                OtherDevices.Add(new OtherDeviceVm(other.Id, other.Name, RenameOtherAsync, TakeOverAsync));
            HasOtherDevices = OtherDevices.Count > 0;

            _selectedMonitorKey ??= _monitors.FirstOrDefault(m => m.Primary)?.Key ?? _monitors.FirstOrDefault()?.Key;
            ShowNotices();
        }
        finally
        {
            _loading = false;
        }
        ShowMonitor();
        ShowLayoutWarnings();
        await ShowPendingAsync();
    }

    /// <summary>The layout choices in place, so a ComboBox showing one of them doesn't go blank.</summary>
    private void RefillChoices()
    {
        foreach (var gone in LayoutChoices.Where(c => c != NoLayout && !Layouts.Contains(c)).ToList())
            LayoutChoices.Remove(gone);
        foreach (var row in Layouts)
            if (!LayoutChoices.Contains(row)) LayoutChoices.Add(row);
    }

    private IEnumerable<(DesktopMonitor Monitor, int Number)> NumberedMonitors() =>
        _monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).Select((m, i) => (m, i + 1));

    private static string MonitorLabel(DesktopMonitor monitor, int number)
    {
        var label = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.MonitorLabel, number, monitor.Bounds.Width, monitor.Bounds.Height);
        return monitor.Primary ? string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.MonitorPrimary, label) : label;
    }

    // ---- Showing the map and the chosen monitor ----

    private void ShowMonitor()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            RedrawMap();
            var numbered = NumberedMonitors().FirstOrDefault(m => m.Monitor.Key == _selectedMonitorKey);
            var monitor = numbered.Monitor;
            HasMonitor = monitor != null;
            Cells.Clear();
            Warnings.Clear();
            if (monitor == null) return;

            MonitorTitle = MonitorLabel(monitor, numbered.Number);
            var row = _monitorRows.FirstOrDefault(r => r.HardwareKey == monitor.Key);

            // Set directly: reading what's saved isn't a change to save.
            _monitorLayout = Layouts.FirstOrDefault(l => l.Id == row?.LayoutId) ?? NoLayout;
            this.RaisePropertyChanged(nameof(MonitorLayout));
            var start = Enum.TryParse<PanelStart>(row?.PanelStart, out var parsed) ? parsed : PanelStart.WithConsole;
            _monitorPanelStart = PanelStarts.First(p => p.Value == start);
            this.RaisePropertyChanged(nameof(MonitorPanelStart));
            _monitorFull = row?.FullMonitor ?? false;
            this.RaisePropertyChanged(nameof(MonitorFull));

            if (_monitorLayout.Id != null) ShowCells(monitor, row, numbered.Number);
            HasNoCells = _monitorLayout.Id != null && Cells.Count == 0;
            HasWarnings = Warnings.Count > 0;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void ShowCells(DesktopMonitor monitor, ClientLayoutMonitor? row, int number)
    {
        var result = ClientLayoutCalculator.Calculate(_monitorLayout!.Root, _monitorFull ? monitor.Bounds : monitor.Work);
        var candidates = Candidates();
        for (var i = 0; i < result.Clients.Count; i++)
        {
            var client = result.Clients[i];
            var names = _characters.Where(c => c.MonitorId == row?.Id && c.AreaId == client.AreaId)
                .Select(c => c.CharacterName).Order(StringComparer.CurrentCultureIgnoreCase);
            Cells.Add(new ClientCellVm(
                client.AreaId,
                string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasClient, i + 1),
                string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasSize, client.Rect.Width, client.Rect.Height),
                result.Problems.Any(p => p.Kind == LayoutProblemKind.SmallClient && p.AreaId == client.AreaId),
                names, AddCharacterAsync, RemoveCharacterAsync)
            { Candidates = candidates });
        }
        foreach (var warning in Describe(result, MonitorLabel(monitor, number))) Warnings.Add(warning);
    }

    /// <summary>What a layout's problems mean on one monitor, in the user's words.</summary>
    private static IEnumerable<string> Describe(ClientLayoutResult result, string monitorLabel)
    {
        foreach (var problem in result.Problems)
        {
            switch (problem.Kind)
            {
                case LayoutProblemKind.SmallClient:
                    var position = result.Clients.FindIndex(c => c.AreaId == problem.AreaId) + 1;
                    yield return string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.WarnSmall,
                        monitorLabel, position, problem.Rect.Width, problem.Rect.Height);
                    break;
                case LayoutProblemKind.PanelsDontFit:
                    yield return string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.WarnNoFit, monitorLabel);
                    break;
                default:
                    yield return string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.WarnBroken, monitorLabel);
                    break;
            }
        }
    }

    private void RedrawMap()
    {
        var items = new List<MonitorMapItem>();
        foreach (var (monitor, number) in NumberedMonitors())
        {
            var row = _monitorRows.FirstOrDefault(r => r.HardwareKey == monitor.Key);
            var layout = Layouts.FirstOrDefault(l => l.Id == row?.LayoutId);
            var space = row?.FullMonitor == true ? monitor.Bounds : monitor.Work;
            items.Add(new MonitorMapItem(monitor.Key, MonitorLabel(monitor, number), monitor.Bounds,
                layout == null ? null : ClientLayoutCalculator.Calculate(layout.Root, space),
                monitor.Key == _selectedMonitorKey));
        }
        MapItems = items;
    }

    private List<string> Candidates() =>
        [.. _appCharacters.Concat(_service.SeenCharacters).Concat(_characters.Select(c => c.CharacterName))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

    private void ShowCandidates()
    {
        var candidates = Candidates();
        foreach (var cell in Cells) cell.Candidates = candidates;
    }

    private void ShowNotices()
    {
        Notices.Clear();
        foreach (var notice in _service.Notices.Reverse()) Notices.Add(Describe(notice));
        HasNotices = Notices.Count > 0;
    }

    private static string Describe(ClientLayoutNotice notice)
    {
        var who = notice.Character ?? ClientLayoutsText.NoticeAClient;
        var text = notice.Kind switch
        {
            PlacementNoticeKind.NotFixedWindow => ClientLayoutsText.NoticeNotFixed,
            PlacementNoticeKind.PositionInUse => ClientLayoutsText.NoticeInUse,
            PlacementNoticeKind.CharacterHasNoPosition => ClientLayoutsText.NoticeNoPosition,
            PlacementNoticeKind.MovedByHand => ClientLayoutsText.NoticeMovedByHand,
            _ => ClientLayoutsText.NoticeDidNotHold,
        };
        return string.Format(CultureInfo.CurrentCulture, "{0:t}  ", notice.At) + string.Format(CultureInfo.CurrentCulture, text, who);
    }

    private async Task ShowPendingAsync()
    {
        var built = await _store.BuildAppliedAsync(_deviceId);
        var applied = AppliedDesktop.Read(_appliedJson);
        var differing = NumberedMonitors()
            .Where(m => MonitorJson(built, m.Monitor.Key) != MonitorJson(applied, m.Monitor.Key))
            .Select(m => m.Number.ToString(CultureInfo.CurrentCulture)).ToList();
        Pending = differing.Count == 0
            ? ClientLayoutsText.PendingNone
            : string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.PendingSome, string.Join(", ", differing));
    }

    private static string MonitorJson(AppliedDesktop desktop, string key)
    {
        var monitor = desktop.Monitors.FirstOrDefault(m => m.HardwareKey == key);
        return monitor == null ? "" : new AppliedDesktop { Monitors = [monitor] }.Write();
    }

    // ---- Changes on the Monitors tab ----

    public void SaveDeviceName()
    {
        var name = DeviceName.Trim();
        if (name.Length == 0 || _deviceId.Length == 0) return;
        _ = Run(() => _store.RenameDeviceAsync(_deviceId, name));
    }

    private void SaveMonitorSoon()
    {
        if (_loading) return;
        _ = Run(SaveMonitorAsync);
    }

    private async Task SaveMonitorAsync()
    {
        if (_selectedMonitorKey == null) return;
        await _store.SaveMonitorAsync(_deviceId, _selectedMonitorKey, _monitorLayout?.Id, _monitorFull,
            _monitorPanelStart?.Value ?? PanelStart.WithConsole);
        _monitorRows = await _store.ListMonitorsAsync(_deviceId);
        ShowMonitor();
        ShowLayoutWarnings();
        await ShowPendingAsync();
    }

    private async Task AddCharacterAsync(ClientCellVm cell, string name)
    {
        var key = _selectedMonitorKey;
        if (key == null) return;
        var row = _monitorRows.FirstOrDefault(r => r.HardwareKey == key);
        var monitorId = row?.Id ?? await _store.SaveMonitorAsync(_deviceId, key, _monitorLayout?.Id, _monitorFull,
            _monitorPanelStart?.Value ?? PanelStart.WithConsole);
        await _store.AssignCharacterAsync(_deviceId, monitorId, cell.AreaId, name);
        await ReloadAssignmentsAsync();
    }

    private async Task RemoveCharacterAsync(string name)
    {
        await _store.UnassignCharacterAsync(_deviceId, name);
        await ReloadAssignmentsAsync();
    }

    private async Task ReloadAssignmentsAsync()
    {
        _monitorRows = await _store.ListMonitorsAsync(_deviceId);
        _characters = await _store.ListCharactersAsync(_deviceId);
        ShowMonitor();
        await ShowPendingAsync();
    }

    private async Task ApplyAsync()
    {
        var built = await _store.ApplyAsync(_deviceId);
        _appliedJson = built.Write();
        _service.RequestApply();
        var numbers = NumberedMonitors().Where(m => built.Monitors.Any(b => b.HardwareKey == m.Monitor.Key))
            .Select(m => m.Number.ToString(CultureInfo.CurrentCulture));
        Status = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.StatusApplied, string.Join(", ", numbers));
        await ShowPendingAsync();
    }

    private Task RenameOtherAsync(string id, string name)
    {
        var clean = name.Trim();
        return clean.Length == 0 ? Task.CompletedTask : Run(() => _store.RenameDeviceAsync(id, clean));
    }

    private Task TakeOverAsync(string id) => Run(async () =>
    {
        await _store.TakeOverDeviceAsync(id);
        _service.RequestReload();
        await LoadAsync();
    });

    // ---- Changes on the Layouts tab ----

    private async Task NewLayoutAsync()
    {
        var root = new LayoutArea { Content = AreaContent.Client };
        var row = new LayoutRowVm(null, ClientLayoutsText.NewLayoutName, root);
        row.Id = await _store.SaveLayoutAsync(null, row.Name, root);
        Layouts.Add(row);
        RefillChoices();
        SelectedLayout = row;
    }

    private async Task DuplicateLayoutAsync()
    {
        if (SelectedLayout is not { } from) return;
        var root = ClientLayoutJson.Read(ClientLayoutJson.Write(from.Root));
        var row = new LayoutRowVm(null, string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CopyOfName, from.Name), root);
        row.Id = await _store.SaveLayoutAsync(null, row.Name, root);
        Layouts.Add(row);
        RefillChoices();
        SelectedLayout = row;
    }

    private async Task DeleteLayoutAsync()
    {
        if (SelectedLayout is not { Id: { } id } row) return;
        if (!await _store.DeleteLayoutAsync(id))
        {
            Status = ClientLayoutsText.StatusLayoutInUse;
            return;
        }
        Layouts.Remove(row);
        RefillChoices();
        SelectedLayout = Layouts.FirstOrDefault();
        Status = "";
    }

    public void SaveLayoutName()
    {
        if (SelectedLayout is not { } row) return;
        if (row.Name.Trim().Length == 0) row.Name = ClientLayoutsText.NewLayoutName;
        _ = Run(SaveEditedLayoutAsync);
    }

    /// <summary>A change on the canvas or in the boxes beside it has finished.</summary>
    private async Task SaveEditedLayoutAsync()
    {
        if (SelectedLayout is not { } row) return;
        await _store.SaveLayoutAsync(row.Id, row.Name, row.Root);
        Revision++;
        ShowSelection();
        ShowMonitor();
        ShowLayoutWarnings();
        await ShowPendingAsync();
    }

    private void ShowSelection()
    {
        var root = EditingRoot;
        var docked = root == null || SelectedId == null ? null : ClientLayoutEditing.FindDocked(root, SelectedId);
        var area = root == null || SelectedId == null ? null : ClientLayoutEditing.FindArea(root, SelectedId);
        HasSize = docked != null;
        HasNamed = docked != null || area?.Content == AreaContent.Panel;
        _selectedName = docked?.Panel.Name ?? area?.Name ?? "";
        this.RaisePropertyChanged(nameof(SelectedName));
        _selectedSize = docked?.Panel.Size;
        this.RaisePropertyChanged(nameof(SelectedSize));
    }

    /// <summary>The name and size boxes have finished being typed in.</summary>
    public void SaveSelection()
    {
        var root = EditingRoot;
        if (root == null || SelectedId == null) return;
        if (ClientLayoutEditing.FindDocked(root, SelectedId) is { } docked)
        {
            docked.Panel.Name = SelectedName;
            if (SelectedSize is { } size) docked.Panel.Size = Math.Max((int)size, 1);
        }
        else if (ClientLayoutEditing.FindArea(root, SelectedId) is { } area)
        {
            area.Name = SelectedName;
        }
        else return;
        _ = Run(SaveEditedLayoutAsync);
    }

    /// <summary>What this layout means on each monitor of this desktop that uses it.</summary>
    private void ShowLayoutWarnings()
    {
        LayoutWarnings.Clear();
        if (SelectedLayout is { Id: { } id } row)
        {
            foreach (var (monitor, number) in NumberedMonitors())
            {
                var saved = _monitorRows.FirstOrDefault(r => r.HardwareKey == monitor.Key);
                if (saved?.LayoutId != id) continue;
                var result = ClientLayoutCalculator.Calculate(row.Root, saved.FullMonitor ? monitor.Bounds : monitor.Work);
                foreach (var warning in Describe(result, MonitorLabel(monitor, number))) LayoutWarnings.Add(warning);
            }
        }
        HasLayoutWarnings = LayoutWarnings.Count > 0;
    }

    /// <summary>Nothing done from a button may fail silently: it goes to the status line and the error log.</summary>
    private async Task Run(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Status = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.StatusFailed, ex.Message);
            _errorLogger.Log("ClientLayouts", "changing client layouts", ex);
        }
    }
}

/// <summary>A monitor size to draw a layout on.</summary>
public sealed record PanelPreviewChoice(string Label, LayoutRect Space);
