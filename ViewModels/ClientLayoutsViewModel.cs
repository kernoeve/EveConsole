using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using Avalonia.Media;
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

/// <summary>A desktop profile, in the Monitors tab's dropdown.</summary>
public sealed class ProfileRowVm(int id, string name) : ReactiveObject
{
    public int Id { get; } = id;
    private string _name = name;
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public override string ToString() => Name;
}

/// <summary>A defined Mumble server a Mumble control can be for.</summary>
public sealed record MumbleServerChoice(int Id, string Name);

/// <summary>How a Mumble control can show.</summary>
public sealed record MumbleModeChoice(MumbleControlMode Mode, string Label);

/// <summary>How a Worklist control can show.</summary>
public sealed record WorklistModeChoice(WorklistControlMode Mode, string Label);

/// <summary>How an Intel control can show.</summary>
public sealed record IntelModeChoice(IntelControlMode Mode, string Label);

/// <summary>How far an Intel control can look, in jumps.</summary>
public sealed record IntelRangeChoice(int Jumps, string Label);

/// <summary>Where a menu control can open tools: a window of its own, the last used, or a Tool Container.</summary>
public sealed record MenuTargetChoice(string Value, string Label);

/// <summary>Whether a Tool Container holds one tool or tabs.</summary>
public sealed record ContainerModeChoice(bool Tabs, string Label);

/// <summary>A tool that can go in a Tool Container.</summary>
public sealed record ToolChoice(string Id, string Label);

/// <summary>A tool a Tool Container starts with, in the editor.</summary>
public sealed class ContainerToolRowVm : ReactiveObject
{
    private readonly ContainerTool _tool;
    private readonly Action _changed;

    public ContainerToolRowVm(ContainerTool tool, string label, bool holdsTabs, Action changed, Action<ContainerToolRowVm> remove)
    {
        _tool = tool;
        _changed = changed;
        Label = label;
        _holdsTabs = holdsTabs;
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
    }

    public ContainerTool Tool => _tool;
    public string Label { get; }

    private bool _holdsTabs;
    /// <summary>Only a tab can be closed: a container holding one tool always keeps it.</summary>
    public bool HoldsTabs { get => _holdsTabs; set => this.RaiseAndSetIfChanged(ref _holdsTabs, value); }

    public bool CanClose
    {
        get => _tool.CanClose;
        set
        {
            if (_tool.CanClose == value) return;
            _tool.CanClose = value;
            this.RaisePropertyChanged();
            _changed();
        }
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    /// <summary>The tool's settings were changed from outside the row, as switching to tabs makes every tab closable.</summary>
    public void Refresh() => this.RaisePropertyChanged(nameof(CanClose));
}

/// <summary>An edge style a panel can have.</summary>
public sealed record PanelStyleChoice(PanelStyle Style, string Label);

/// <summary>Where the selected panel, control or grid can sit: an edge, or full.</summary>
public sealed record DockPlaceChoice(DockPlace Place, string Label);

/// <summary>One thing docked in the same area as the selection, in dock order.</summary>
public sealed class DockOrderRowVm(DockedPanel panel, string name, string edge, bool isSelected)
{
    public DockedPanel Panel { get; } = panel;
    public string Name { get; } = name;
    public string Edge { get; } = edge;
    public bool IsSelected { get; } = isSelected;
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
    private int _profileId;
    private string _appliedJson = "";
    private List<DesktopMonitor> _monitors = [];
    private List<ClientLayoutMonitor> _monitorRows = [];
    private List<ClientLayoutCharacter> _characters = [];
    private List<string> _appCharacters = [];
    private bool _opened;
    private bool _loading;
    private string? _selectedMonitorKey;

    private readonly Func<IReadOnlyList<MumbleServerChoice>>? _mumbleServers;
    private readonly Func<IReadOnlyList<ToolChoice>>? _tools;
    private readonly Func<IReadOnlyList<PanelRoomChoice>>? _groupRooms;

    public ClientLayoutsViewModel(ClientLayoutStore store, ClientLayoutService service, IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger,
        Func<IReadOnlyList<MumbleServerChoice>>? mumbleServers = null, Func<IReadOnlyList<ToolChoice>>? tools = null,
        Func<IReadOnlyList<PanelRoomChoice>>? groupRooms = null)
    {
        _mumbleServers = mumbleServers;
        _groupRooms = groupRooms;
        _tools = tools;
        AddToolCommand = ReactiveCommand.Create(AddTool);
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
        ResetColorCommand = ReactiveCommand.Create(ResetColor);
        ApplyCommand = ReactiveCommand.CreateFromTask(() => Run(ApplyAsync));
        NewLayoutCommand = ReactiveCommand.CreateFromTask(() => Run(NewLayoutAsync));
        DuplicateLayoutCommand = ReactiveCommand.CreateFromTask(() => Run(DuplicateLayoutAsync));
        DeleteLayoutCommand = ReactiveCommand.CreateFromTask(() => Run(DeleteLayoutAsync));
        NewProfileCommand = ReactiveCommand.CreateFromTask(() => Run(NewProfileAsync));
        DuplicateProfileCommand = ReactiveCommand.CreateFromTask(() => Run(DuplicateProfileAsync));
        DeleteProfileCommand = ReactiveCommand.CreateFromTask(() => Run(DeleteProfileAsync));
        UndoCommand = ReactiveCommand.Create(Undo, this.WhenAnyValue(x => x.CanUndo));
        // The outline follows every change to the layout, names typed included.
        this.WhenAnyValue(x => x.Revision).Subscribe(_ => ShowOutline());
        RedoCommand = ReactiveCommand.Create(Redo, this.WhenAnyValue(x => x.CanRedo));

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
    public ObservableCollection<ProfileRowVm> Profiles { get; } = [];
    public ObservableCollection<MumbleServerChoice> MumbleServers { get; } = [];
    public IReadOnlyList<MumbleModeChoice> MumbleModes { get; } =
    [
        new(MumbleControlMode.SingleLine, ClientLayoutsText.MumbleModeLine),
        new(MumbleControlMode.List, ClientLayoutsText.MumbleModeList),
    ];
    public IReadOnlyList<PanelStyleChoice> StyleChoices { get; } =
    [
        new(PanelStyle.Flat, ClientLayoutsText.StyleFlat),
        new(PanelStyle.Raised, ClientLayoutsText.StyleRaised),
        new(PanelStyle.Inset, ClientLayoutsText.StyleInset),
        new(PanelStyle.Ridge, ClientLayoutsText.StyleRidge),
        new(PanelStyle.Card, ClientLayoutsText.StyleCard),
    ];
    public IReadOnlyList<PanelStartChoice> PanelStarts { get; }

    public ICommandLike PickMonitor { get; }
    public ICommandLike LayoutEdited { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> NewProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> NewLayoutCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateLayoutCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteLayoutCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }

    // ---- The outline: the layout as a tree, to select from ----

    /// <summary>The monitor, as the outline's one top line.</summary>
    public ObservableCollection<OutlineNodeVm> Outline { get; } = [];

    /// <summary>What has been folded away, by id, so a rebuilt outline keeps it folded.</summary>
    private readonly HashSet<string> _folded = [];
    private string _outlineShape = "";
    private bool _syncingOutline;

    private OutlineNodeVm? _outlineSelected;
    /// <summary>The line picked in the outline, which selects the same thing on the drawing.</summary>
    public OutlineNodeVm? OutlineSelected
    {
        get => _outlineSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _outlineSelected, value);
            if (_syncingOutline || value?.Id is not { } id || id == SelectedId) return;
            SelectedId = id;
        }
    }

    /// <summary>
    /// Builds the outline again, only if something in it changed — a rebuilt tree would close the
    /// folds being opened and jump its scroll — and marks what's selected.
    /// </summary>
    private void ShowOutline()
    {
        if (EditingRoot is not { } root)
        {
            Outline.Clear();
            _outlineShape = "";
            return;
        }
        var built = LayoutOutline.Build(root);
        var lines = LayoutOutline.Walk(built).ToList();
        var shape = string.Join("\n", lines.Select(l => $"{l.Depth}|{l.Node.Id}|{l.Node.Label}|{l.Node.Detail}"));
        if (shape != _outlineShape)
        {
            _outlineShape = shape;
            foreach (var (node, _) in lines)
            {
                node.IsExpanded = !_folded.Contains(node.Key);
                node.Expanded = (n, open) => { if (open) _folded.Remove(n.Key); else _folded.Add(n.Key); };
            }
            _syncingOutline = true;
            try
            {
                Outline.Clear();
                Outline.Add(built);
            }
            finally { _syncingOutline = false; }
        }
        SelectInOutline();
    }

    /// <summary>
    /// Removes what's selected, as its menu's Remove does, and selects what it was in. The Delete key.
    /// A grid's cell and the monitor stay; Undo brings back anything removed.
    /// </summary>
    public void DeleteSelected()
    {
        if (SelectedLayout is not { } row || SelectedId is not { } id) return;
        var next = ClientLayoutEditing.Remove(row.Root, id, out var removed);
        if (!removed) return;
        Revision++;
        StepNow(row);
        SelectedId = next;
    }

    /// <summary>
    /// Whether what has this id can be moved into the panel with <paramref name="targetId"/>; null is
    /// the monitor. A grid's cell stands for its grid (see <see cref="ClientLayoutEditing.DraggedBy"/>).
    /// </summary>
    public bool CanMoveTo(string id, string? targetId) =>
        SelectedLayout is { } row && ClientLayoutEditing.CanMove(row.Root, ClientLayoutEditing.DraggedBy(row.Root, id), targetId ?? ClientLayoutEditing.MonitorTarget);

    /// <summary>Moves what has this id — a cell, its grid — into another panel, or onto the monitor (null), and selects it there.</summary>
    public void MoveTo(string id, string? targetId)
    {
        if (SelectedLayout is not { } row
            || ClientLayoutEditing.Move(row.Root, ClientLayoutEditing.DraggedBy(row.Root, id), targetId ?? ClientLayoutEditing.MonitorTarget) is not { } moved) return;
        Revision++;
        StepNow(row);
        SelectedId = moved;
    }

    /// <summary>The selection's line, with every fold above it opened so it can be seen.</summary>
    private void SelectInOutline()
    {
        var node = Outline.Count == 0 || SelectedId == null ? null
            : LayoutOutline.Walk(Outline[0]).Select(l => l.Node).FirstOrDefault(n => n.Id == SelectedId);
        for (var up = node?.Parent; up != null; up = up.Parent) up.IsExpanded = true;
        _syncingOutline = true;
        try { OutlineSelected = node; }
        finally { _syncingOutline = false; }
    }

    // ---- Undo and redo, per layout ----

    /// <summary>
    /// By the layout's id, so its history is still there after the layouts are read again on coming
    /// back to the tab. Kept for as long as this editor is.
    /// </summary>
    private readonly Dictionary<object, LayoutHistory> _histories = [];

    private bool _canUndo, _canRedo;
    public bool CanUndo { get => _canUndo; private set => this.RaiseAndSetIfChanged(ref _canUndo, value); }
    public bool CanRedo { get => _canRedo; private set => this.RaiseAndSetIfChanged(ref _canRedo, value); }

    /// <summary>The layout's history, begun with the layout as it is now if it hasn't one.</summary>
    private LayoutHistory HistoryOf(LayoutRowVm row)
    {
        object key = row.Id is { } id ? id : row;
        if (!_histories.TryGetValue(key, out var history)) _histories[key] = history = new LayoutHistory(row.Root);
        return history;
    }

    /// <summary>A change to the layout has settled: it becomes a step that can be undone.</summary>
    private void RecordStep(LayoutRowVm row)
    {
        HistoryOf(row).Record(row.Root);
        ShowHistory();
    }

    private void ShowHistory()
    {
        var history = SelectedLayout is { } row ? HistoryOf(row) : null;
        CanUndo = history?.CanUndo == true;
        CanRedo = history?.CanRedo == true;
    }

    public void Undo() => StepHistory(undo: true);

    public void Redo() => StepHistory(undo: false);

    private void StepHistory(bool undo)
    {
        if (SelectedLayout is not { } row) return;
        // ⚠️ Typing still waiting to be saved is taken as a step first, so it is what an undo takes
        // back rather than being saved over the layout an undo put back.
        if (ReferenceEquals(_typedInto, row))
        {
            _typedInto = null;
            _typedSave?.Stop();
        }
        var history = HistoryOf(row);
        if (!(undo ? history.Undo(row.Root) : history.Redo(row.Root))) { ShowHistory(); return; }

        // What was selected may not be there any more.
        if (SelectedId is { } id && ClientLayoutEditing.FindArea(row.Root, id) == null && ClientLayoutEditing.FindDocked(row.Root, id) == null)
            _selectedId = null;
        this.RaisePropertyChanged(nameof(SelectedId));
        ShowHistory();
        _ = Run(SaveEditedLayoutAsync);
    }
    public ReactiveCommand<Unit, Unit> ResetColorCommand { get; }

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

    // ---- The profile being edited ----

    private ProfileRowVm? _selectedProfile;

    /// <summary>
    /// Which profile the Monitors tab shows and edits. Choosing one moves nothing: as with any other
    /// change, that waits for Apply, which is also how to switch to it.
    /// </summary>
    public ProfileRowVm? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            // A ComboBox reports null while its list is being changed; that isn't a choice.
            if (value == null || ReferenceEquals(_selectedProfile, value)) return;
            this.RaiseAndSetIfChanged(ref _selectedProfile, value);
            _profileId = value.Id;
            if (!_loading) _ = Run(ShowProfileAsync);
        }
    }

    private string _profileInUse = "";
    public string ProfileInUse { get => _profileInUse; private set => this.RaiseAndSetIfChanged(ref _profileInUse, value); }

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
            _ = SaveTypedNowAsync();
            this.RaiseAndSetIfChanged(ref _selectedLayout, value);
            // Its history begins here, before anything is changed, if it hasn't begun already.
            if (value != null) HistoryOf(value);
            ShowHistory();
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

    private bool _hasControl;
    public bool HasControl { get => _hasControl; private set => this.RaiseAndSetIfChanged(ref _hasControl, value); }

    private bool _isGroupChatRoom;
    /// <summary>A Group Chat control, which opens on a room chosen here.</summary>
    public bool IsGroupChatRoom { get => _isGroupChatRoom; private set => this.RaiseAndSetIfChanged(ref _isGroupChatRoom, value); }

    public ObservableCollection<PanelRoomChoice> GroupRooms { get; } = [];

    private PanelRoomChoice? _selectedGroupRoom;
    public PanelRoomChoice? SelectedGroupRoom
    {
        get => _selectedGroupRoom;
        set
        {
            if (value == null || _selectedGroupRoom == value) return;
            this.RaiseAndSetIfChanged(ref _selectedGroupRoom, value);
            OptionsOfSelection(o => o.GroupChatRoom = value.Id);
        }
    }

    private bool _isMumbleControl;
    public bool IsMumbleControl { get => _isMumbleControl; private set => this.RaiseAndSetIfChanged(ref _isMumbleControl, value); }

    private MumbleServerChoice? _selectedMumbleServer;
    public MumbleServerChoice? SelectedMumbleServer
    {
        get => _selectedMumbleServer;
        set
        {
            if (value == null || _selectedMumbleServer == value) return;
            this.RaiseAndSetIfChanged(ref _selectedMumbleServer, value);
            OptionsOfSelection(o => o.MumbleServerId = value.Id);
        }
    }

    private MumbleModeChoice? _selectedMumbleMode;
    public MumbleModeChoice? SelectedMumbleMode
    {
        get => _selectedMumbleMode;
        set
        {
            if (value == null || _selectedMumbleMode == value) return;
            this.RaiseAndSetIfChanged(ref _selectedMumbleMode, value);
            OptionsOfSelection(o => o.MumbleMode = value.Mode.ToString());
        }
    }

    // ---- A Worklist control's mode ----

    private bool _isWorklistControl;
    public bool IsWorklistControl { get => _isWorklistControl; private set => this.RaiseAndSetIfChanged(ref _isWorklistControl, value); }

    public IReadOnlyList<WorklistModeChoice> WorklistModes { get; } =
    [
        new(WorklistControlMode.List, ClientLayoutsText.WorklistModeList),
        new(WorklistControlMode.SingleLine, ClientLayoutsText.WorklistModeLine),
    ];

    private WorklistModeChoice? _selectedWorklistMode;
    public WorklistModeChoice? SelectedWorklistMode
    {
        get => _selectedWorklistMode;
        set
        {
            if (value == null || _selectedWorklistMode == value) return;
            this.RaiseAndSetIfChanged(ref _selectedWorklistMode, value);
            OptionsOfSelection(o => o.WorklistMode = value.Mode.ToString());
        }
    }

    // ---- An Intel control's mode and range ----

    private bool _isIntelControl;
    public bool IsIntelControl { get => _isIntelControl; private set => this.RaiseAndSetIfChanged(ref _isIntelControl, value); }

    public IReadOnlyList<IntelModeChoice> IntelModes { get; } =
    [
        new(IntelControlMode.List, ClientLayoutsText.IntelModeList),
        new(IntelControlMode.SingleLine, ClientLayoutsText.IntelModeLine),
    ];

    private IntelModeChoice? _selectedIntelMode;
    public IntelModeChoice? SelectedIntelMode
    {
        get => _selectedIntelMode;
        set
        {
            if (value == null || _selectedIntelMode == value) return;
            this.RaiseAndSetIfChanged(ref _selectedIntelMode, value);
            OptionsOfSelection(o => o.IntelMode = value.Mode.ToString());
        }
    }

    public IReadOnlyList<IntelRangeChoice> IntelRangeChoices { get; } =
        [.. PanelControls.IntelRanges.Select(j => new IntelRangeChoice(j,
            string.Format(System.Globalization.CultureInfo.CurrentCulture, ClientLayoutsText.IntelRangeJumps, j)))];

    private IntelRangeChoice? _selectedIntelRange;
    public IntelRangeChoice? SelectedIntelRange
    {
        get => _selectedIntelRange;
        set
        {
            if (value == null || _selectedIntelRange == value) return;
            this.RaiseAndSetIfChanged(ref _selectedIntelRange, value);
            // Ten is the default and is saved as nothing, so a layout that never chose stays as it was.
            OptionsOfSelection(o => o.IntelMaxJumps = value.Jumps == PanelControls.DefaultIntelRange ? null : value.Jumps);
        }
    }

    // ---- A menu control's target ----

    private bool _isMenuControl;
    public bool IsMenuControl { get => _isMenuControl; private set => this.RaiseAndSetIfChanged(ref _isMenuControl, value); }

    public ObservableCollection<MenuTargetChoice> MenuTargets { get; } = [];

    private MenuTargetChoice? _selectedMenuTarget;
    public MenuTargetChoice? SelectedMenuTarget
    {
        get => _selectedMenuTarget;
        set
        {
            if (value == null || _selectedMenuTarget == value) return;
            this.RaiseAndSetIfChanged(ref _selectedMenuTarget, value);
            OptionsOfSelection(o => o.MenuTarget = value.Value);
        }
    }

    /// <summary>The two fixed places, then every Tool Container of tabs in this desktop's layouts.</summary>
    private void ShowMenuTargets(ControlOptions? options)
    {
        var choices = new List<MenuTargetChoice>
        {
            new(ToolTargets.NewWindow, ClientLayoutsText.MenuTargetNewWindow),
            new(ToolTargets.LastUsed, ClientLayoutsText.MenuTargetLastUsed),
        };
        foreach (var layout in Layouts)
            foreach (var c in ClientLayoutEditing.Controls(layout.Root)
                         .Where(c => PanelControls.Parse(c.Control) == PanelControlKind.ToolContainer && PanelControls.HoldsTabs(c.Options)))
                choices.Add(new MenuTargetChoice(c.Id, string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.MenuTargetContainer,
                    layout.Name, c.Name.Length > 0 ? c.Name : ClientLayoutsText.ItemToolContainer)));
        var target = ToolTargets.Of(options);
        if (choices.All(c => c.Value != target)) choices.Add(new MenuTargetChoice(target, ClientLayoutsText.MenuTargetGone));

        MenuTargets.Clear();
        foreach (var choice in choices) MenuTargets.Add(choice);
        _selectedMenuTarget = MenuTargets.First(c => c.Value == target);
        this.RaisePropertyChanged(nameof(SelectedMenuTarget));
    }

    // ---- A Tool Container's settings ----

    private bool _isToolContainer;
    public bool IsToolContainer { get => _isToolContainer; private set => this.RaiseAndSetIfChanged(ref _isToolContainer, value); }

    public IReadOnlyList<ContainerModeChoice> ContainerModes { get; } =
    [
        new(false, ClientLayoutsText.ContainerModeSingle),
        new(true, ClientLayoutsText.ContainerModeTabs),
    ];

    private ContainerModeChoice? _selectedContainerMode;
    public ContainerModeChoice? SelectedContainerMode
    {
        get => _selectedContainerMode;
        set
        {
            if (value == null || _selectedContainerMode == value) return;
            this.RaiseAndSetIfChanged(ref _selectedContainerMode, value);
            OptionsOfSelection(o =>
            {
                o.ContainerTabs = value.Tabs;
                // One tool: only the first stays, and it can't be closed.
                if (!value.Tabs && o.ContainerTools is { Count: > 1 } tools) tools.RemoveRange(1, tools.Count - 1);
                if (!value.Tabs && o.ContainerTools is { Count: 1 } one) one[0].CanClose = false;
                // Tabs: closable, as a new tab would be.
                if (value.Tabs) foreach (var tool in o.ContainerTools ?? []) tool.CanClose = true;
            });
            ShowSelection();
        }
    }

    private bool _containerSplit;
    public bool ContainerSplit
    {
        get => _containerSplit;
        set
        {
            if (_containerSplit == value) return;
            this.RaiseAndSetIfChanged(ref _containerSplit, value);
            OptionsOfSelection(o => o.ContainerSplit = value);
        }
    }

    private bool _containerHoldsTabs;
    public bool ContainerHoldsTabs { get => _containerHoldsTabs; private set => this.RaiseAndSetIfChanged(ref _containerHoldsTabs, value); }

    public ObservableCollection<ContainerToolRowVm> ContainerTools { get; } = [];
    public ObservableCollection<ToolChoice> ToolChoices { get; } = [];

    private ToolChoice? _toolToAdd;
    public ToolChoice? ToolToAdd { get => _toolToAdd; set => this.RaiseAndSetIfChanged(ref _toolToAdd, value); }

    private bool _canAddTool;
    /// <summary>A container of tabs takes any number of tools; one holding a single tool, one.</summary>
    public bool CanAddTool { get => _canAddTool; private set => this.RaiseAndSetIfChanged(ref _canAddTool, value); }

    public ReactiveCommand<Unit, Unit> AddToolCommand { get; }

    // ---- The settings of a container's starting tool, picked from its list ----

    private ContainerToolRowVm? _selectedContainerTool;
    /// <summary>Picked to set what it opens with. Most tools have nothing to set; the Overview has its layout.</summary>
    public ContainerToolRowVm? SelectedContainerTool
    {
        get => _selectedContainerTool;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedContainerTool, value);
            this.RaisePropertyChanged(nameof(SelectedToolIsOverview));
            this.RaisePropertyChanged(nameof(SelectedToolHasNoSettings));
            this.RaisePropertyChanged(nameof(SelectedToolTitle));
            OverviewLayouts.Clear();
            if (value?.Tool.ToolId != "overview") return;
            foreach (var choice in OverviewLayoutStore.Shared.Choices) OverviewLayouts.Add(choice);
            _selectedOverviewLayout = OverviewLayouts.FirstOrDefault(c => c.Key == OverviewLayoutStore.Shared.KeyOf(value.Tool.Layout));
            this.RaisePropertyChanged(nameof(SelectedOverviewLayout));
        }
    }

    public bool SelectedToolIsOverview => _selectedContainerTool?.Tool.ToolId == "overview";
    public bool SelectedToolHasNoSettings => _selectedContainerTool is not null && !SelectedToolIsOverview;
    public string SelectedToolTitle => _selectedContainerTool is { } row
        ? string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.LabelToolSettings, row.Label) : "";

    /// <summary>The Overview's saved layouts, for the one an Overview in this container opens with.</summary>
    public ObservableCollection<OverviewLayoutChoice> OverviewLayouts { get; } = [];

    private OverviewLayoutChoice? _selectedOverviewLayout;
    public OverviewLayoutChoice? SelectedOverviewLayout
    {
        get => _selectedOverviewLayout;
        set
        {
            if (value is null || value == _selectedOverviewLayout || _selectedContainerTool is not { } row) return;
            this.RaiseAndSetIfChanged(ref _selectedOverviewLayout, value);
            row.Tool.Layout = OverviewLayoutStore.IsDefault(value.Key) ? null : value.Key;
            OptionsOfSelection(_ => { });
        }
    }

    private void ShowContainer(ControlOptions? options)
    {
        var tabs = PanelControls.HoldsTabs(options);
        _selectedContainerMode = ContainerModes.First(m => m.Tabs == tabs);
        this.RaisePropertyChanged(nameof(SelectedContainerMode));
        _containerSplit = options?.ContainerSplit == true;
        this.RaisePropertyChanged(nameof(ContainerSplit));
        ContainerHoldsTabs = tabs;

        var all = _tools?.Invoke() ?? [];
        // ⚠️ Kept rather than made again: the list's selection is cleared by its rows being replaced,
        // which lost the tool picked every time anything was saved.
        var tools = options?.ContainerTools ?? [];
        foreach (var gone in ContainerTools.Where(r => !tools.Any(t => ReferenceEquals(t, r.Tool))).ToList()) ContainerTools.Remove(gone);
        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            var row = ContainerTools.FirstOrDefault(r => ReferenceEquals(r.Tool, tool));
            if (row == null)
                ContainerTools.Insert(i, new ContainerToolRowVm(tool, all.FirstOrDefault(t => t.Id == tool.ToolId)?.Label ?? tool.ToolId, tabs,
                    () => OptionsOfSelection(_ => { }), RemoveTool));
            else
            {
                row.HoldsTabs = tabs;
                row.Refresh();
                if (ContainerTools.IndexOf(row) != i) ContainerTools.Move(ContainerTools.IndexOf(row), i);
            }
        }

        // Each tool once: a tool is only ever open in one place.
        ToolChoices.Clear();
        foreach (var choice in all.Where(t => ContainerTools.All(r => r.Tool.ToolId != t.Id))) ToolChoices.Add(choice);
        ToolToAdd = ToolChoices.FirstOrDefault();
        CanAddTool = ToolChoices.Count > 0 && (tabs || ContainerTools.Count == 0);
        // A new tool is picked as it's added; one picked that's gone leaves nothing picked.
        if (_addedTool is { } added) SelectedContainerTool = ContainerTools.FirstOrDefault(r => ReferenceEquals(r.Tool, added));
        else if (_selectedContainerTool is { } picked && !ContainerTools.Contains(picked)) SelectedContainerTool = null;
        else SelectedContainerTool = _selectedContainerTool;   // shown again: its settings brought up to date
        _addedTool = null;
    }

    private ContainerTool? _addedTool;

    private void AddTool()
    {
        if (ToolToAdd is not { } choice || !CanAddTool) return;
        OptionsOfSelection(o =>
        {
            // A tab can be closed unless set otherwise; a single tool never can.
            _addedTool = new ContainerTool { ToolId = choice.Id, CanClose = PanelControls.HoldsTabs(o) };
            (o.ContainerTools ??= []).Add(_addedTool);
        });
        ShowSelection();
    }

    private void RemoveTool(ContainerToolRowVm row)
    {
        OptionsOfSelection(o => o.ContainerTools?.Remove(row.Tool));
        ShowSelection();
    }

    private string _selectedControlName = "";
    public string SelectedControlName { get => _selectedControlName; private set => this.RaiseAndSetIfChanged(ref _selectedControlName, value); }

    private string _sizeLabel = "";
    public string SizeLabel { get => _sizeLabel; private set => this.RaiseAndSetIfChanged(ref _sizeLabel, value); }

    private Color _selectedColor;
    public Color SelectedColor
    {
        get => _selectedColor;
        set
        {
            if (_selectedColor == value) return;
            this.RaiseAndSetIfChanged(ref _selectedColor, value);
            LookOfSelection(look => look.Color = value.ToString().Length == 9 ? "#" + value.ToString()[3..] : value.ToString());
        }
    }

    private PanelStyleChoice? _selectedStyle;
    public PanelStyleChoice? SelectedStyle
    {
        get => _selectedStyle;
        set
        {
            // A ComboBox reports null while it's being refilled; that isn't a choice.
            if (value == null || _selectedStyle == value) return;
            this.RaiseAndSetIfChanged(ref _selectedStyle, value);
            LookOfSelection(look => look.Style = value.Style.ToString(), step: true);
        }
    }

    /// <summary>Where the selection can go. Only the places it can go: full is offered only when the
    /// middle it would take is empty.</summary>
    public ObservableCollection<DockPlaceChoice> DockChoices { get; } = [];

    private bool _hasDock;
    public bool HasDock { get => _hasDock; private set => this.RaiseAndSetIfChanged(ref _hasDock, value); }

    private DockPlaceChoice? _selectedDock;
    public DockPlaceChoice? SelectedDock
    {
        get => _selectedDock;
        set
        {
            // A ComboBox reports null while it's being refilled; that isn't a choice.
            if (value == null || _selectedDock == value) return;
            this.RaiseAndSetIfChanged(ref _selectedDock, value);
            if (SelectedLayout is not { } row || SelectedId == null || ClientLayoutEditing.PlaceOf(row.Root, SelectedId) == value.Place) return;
            if (ClientLayoutEditing.Redock(row.Root, SelectedId, value.Place) is not { } id) return;
            Revision++;
            StepNow(row);
            SelectedId = id;   // shows it again, in its new place
        }
    }

    /// <summary>What's docked beside the selection, first docked first. Dragged into a new order.</summary>
    public ObservableCollection<DockOrderRowVm> DockOrder { get; } = [];

    private bool _hasDockOrder;
    public bool HasDockOrder { get => _hasDockOrder; private set => this.RaiseAndSetIfChanged(ref _hasDockOrder, value); }

    /// <summary>Moves a row of the dock order, and what it stands for, to another place in it.
    /// Saved, and a step to undo, when the drag ends: see <see cref="DockOrderDropped"/>.</summary>
    public void MoveDockOrder(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= DockOrder.Count || to >= DockOrder.Count) return;
        if (SelectedLayout is not { } row) return;
        var moving = DockOrder[from].Panel;
        if (ClientLayoutEditing.FindDocked(row.Root, moving.Id) is not { } docked) return;
        if (!ClientLayoutEditing.MoveDocked(docked.Area, moving, to)) return;
        DockOrder.Move(from, to);
        Revision++;
        _dockOrderMoved = true;
    }

    private bool _dockOrderMoved;

    /// <summary>A drag in the dock order has ended: however many rows it passed, one step.</summary>
    public void DockOrderDropped()
    {
        if (!_dockOrderMoved || SelectedLayout is not { } row) return;
        _dockOrderMoved = false;
        StepNow(row);
    }

    private static string DockLabel(DockPlace place) => place switch
    {
        DockPlace.Top => ClientLayoutsText.MenuEdgeTop,
        DockPlace.Bottom => ClientLayoutsText.MenuEdgeBottom,
        DockPlace.Left => ClientLayoutsText.MenuEdgeLeft,
        DockPlace.Right => ClientLayoutsText.MenuEdgeRight,
        _ => ClientLayoutsText.MenuEdgeFull,
    };

    private void ShowDock(LayoutArea? root, (LayoutArea Area, DockedPanel Panel)? docked)
    {
        DockChoices.Clear();
        var now = root == null || SelectedId == null ? null : ClientLayoutEditing.PlaceOf(root, SelectedId);
        if (now != null)
            foreach (var place in Enum.GetValues<DockPlace>())
                if (ClientLayoutEditing.CanPlace(root!, SelectedId!, place)) DockChoices.Add(new DockPlaceChoice(place, DockLabel(place)));
        HasDock = now != null;
        _selectedDock = DockChoices.FirstOrDefault(c => c.Place == now);
        this.RaisePropertyChanged(nameof(SelectedDock));

        DockOrder.Clear();
        if (docked is { } d)
            foreach (var panel in d.Area.Docked)
            {
                var name = panel.Grid != null ? ClientLayoutsText.ParentGrid
                    : panel.Control != null ? ClientLayoutPainter.ControlName(panel.Control)
                    : panel.Name.Length > 0 ? panel.Name : ClientLayoutsText.PanelDefaultName;
                if (panel.Control != null && panel.Name.Length > 0) name = panel.Name + " · " + name;
                DockOrder.Add(new DockOrderRowVm(panel, name, DockLabel((DockPlace)panel.Edge), ReferenceEquals(panel, d.Panel)));
            }
        HasDockOrder = DockOrder.Count > 1;
    }

    private bool _hasPadding;
    /// <summary>A panel or a grid is selected: what it holds can be kept in from its sides.</summary>
    public bool HasPadding { get => _hasPadding; private set => this.RaiseAndSetIfChanged(ref _hasPadding, value); }

    /// <summary>A panel is selected: it has a colour and an edge.</summary>
    private bool _hasLook;
    public bool HasLook { get => _hasLook; private set => this.RaiseAndSetIfChanged(ref _hasLook, value); }

    private bool _hasGrid;
    /// <summary>A grid is selected: its columns and rows can be set.</summary>
    public bool HasGrid { get => _hasGrid; private set => this.RaiseAndSetIfChanged(ref _hasGrid, value); }

    public int MaxGridLines => ClientLayoutEditing.MaxGridLines;


    private decimal? _gridColumns, _gridRows;
    public decimal? GridColumns { get => _gridColumns; set => SetGridSize(ref _gridColumns, value, columns: true); }
    public decimal? GridRows { get => _gridRows; set => SetGridSize(ref _gridRows, value, columns: false); }

    /// <summary>A grid's columns or rows, set from its box: a step to undo of its own, as dropping cells is a big change.</summary>
    private void SetGridSize(ref decimal? field, decimal? value, bool columns, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        this.RaisePropertyChanged(name);
        if (value is not { } count || SelectedLayout is not { } row || SelectedId == null
            || ClientLayoutEditing.FindGrid(row.Root, SelectedId) is not { } grid) return;
        var changed = columns
            ? ClientLayoutEditing.ResizeGrid(grid, (int)count, Math.Max(grid.Rows.Count, 1))
            : ClientLayoutEditing.ResizeGrid(grid, Math.Max(grid.Columns.Count, 1), (int)count);
        if (!changed) return;
        Revision++;
        StepNow(row);
    }

    private decimal? _paddingLeft, _paddingTop, _paddingRight, _paddingBottom;
    public decimal? PaddingLeft { get => _paddingLeft; set => SetPadding(ref _paddingLeft, value, (p, v) => p.Left = v); }
    public decimal? PaddingTop { get => _paddingTop; set => SetPadding(ref _paddingTop, value, (p, v) => p.Top = v); }
    public decimal? PaddingRight { get => _paddingRight; set => SetPadding(ref _paddingRight, value, (p, v) => p.Right = v); }
    public decimal? PaddingBottom { get => _paddingBottom; set => SetPadding(ref _paddingBottom, value, (p, v) => p.Bottom = v); }

    private void SetPadding(ref decimal? field, decimal? value, Action<LayoutPadding, int> side, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        this.RaisePropertyChanged(name);
        // An emptied box is 0, as the side was before anything was set.
        var pixels = Math.Max((int)(value ?? 0), 0);
        PaddingOfSelection(p => side(p, pixels));
    }

    private LayoutRowVm? _typedInto;
    private bool _deviceNameTyped;
    private ProfileRowVm? _profileTyped;
    private DispatcherTimer? _typedSave;

    private bool _hasNamed;
    public bool HasNamed { get => _hasNamed; private set => this.RaiseAndSetIfChanged(ref _hasNamed, value); }

    private bool _hasSize;
    public bool HasSize { get => _hasSize; private set => this.RaiseAndSetIfChanged(ref _hasSize, value); }

    private string _selectedName = "";
    public string SelectedName
    {
        get => _selectedName;
        set
        {
            if (_selectedName == value) return;
            this.RaiseAndSetIfChanged(ref _selectedName, value);
            TypedIntoSelection();
        }
    }

    private decimal? _selectedSize;
    public decimal? SelectedSize
    {
        get => _selectedSize;
        set
        {
            if (_selectedSize == value) return;
            this.RaiseAndSetIfChanged(ref _selectedSize, value);
            TypedIntoSelection();
        }
    }

    // ---- Loading ----

    private async Task LoadAsync()
    {
        await SaveTypedNowAsync();
        _loading = true;
        try
        {
            var device = await _store.ThisDeviceAsync();
            _deviceId = device.Id;
            _appliedJson = AppliedDesktop.Read(device.Applied).Write();
            DeviceName = device.Name;
            PlacementOn = device.PlacementOn;

            _monitors = ClientWindows.FindMonitors();
            await LoadProfilesAsync(_selectedProfile?.Id ?? AppliedDesktop.Read(device.Applied).ProfileId);
            ShowProfileInUse();
            _monitorRows = await _store.ListMonitorsAsync(_deviceId, _profileId);
            _characters = await _store.ListCharactersAsync(_deviceId, _profileId);

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

    /// <summary>The profile list afresh, with this one selected if it's still there.</summary>
    private async Task LoadProfilesAsync(int? select)
    {
        var rows = await _store.ListProfilesAsync(_deviceId, ClientLayoutsText.ProfileFirstName);
        var chosen = rows.FirstOrDefault(r => r.Id == select) ?? rows[0];
        foreach (var gone in Profiles.Where(p => rows.All(r => r.Id != p.Id)).ToList()) Profiles.Remove(gone);
        foreach (var row in rows)
        {
            var existing = Profiles.FirstOrDefault(p => p.Id == row.Id);
            if (existing == null) Profiles.Add(new ProfileRowVm(row.Id, row.Name));
            else existing.Name = row.Name;
        }
        var wasLoading = _loading;
        _loading = true;
        SelectedProfile = Profiles.First(p => p.Id == chosen.Id);
        _loading = wasLoading;
    }

    private void ShowProfileInUse()
    {
        var name = AppliedDesktop.Read(_appliedJson).ProfileName;
        ProfileInUse = string.IsNullOrEmpty(name) ? "" : string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.ProfileInUse, name);
    }

    private async Task ShowProfileAsync()
    {
        _monitorRows = await _store.ListMonitorsAsync(_deviceId, _profileId);
        _characters = await _store.ListCharactersAsync(_deviceId, _profileId);
        ShowMonitor();
        ShowLayoutWarnings();
        ShowCandidates();
        await ShowPendingAsync();
    }

    private async Task NewProfileAsync()
    {
        var id = await _store.AddProfileAsync(_deviceId, ClientLayoutsText.ProfileNewName);
        await LoadProfilesAsync(id);
        await ShowProfileAsync();
    }

    private async Task DuplicateProfileAsync()
    {
        if (SelectedProfile is not { } from) return;
        await SaveTypedNowAsync();
        var name = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CopyOfName, from.Name);
        var id = await _store.DuplicateProfileAsync(_deviceId, from.Id, name);
        await LoadProfilesAsync(id);
        await ShowProfileAsync();
    }

    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { } row) return;
        await SaveTypedNowAsync();
        if (!await _store.DeleteProfileAsync(_deviceId, row.Id))
        {
            Status = ClientLayoutsText.StatusLastProfile;
            return;
        }
        Status = "";
        _selectedProfile = null;
        await LoadProfilesAsync(null);
        await ShowProfileAsync();
    }

    /// <summary>A key pressed in the profile's name box. A blank name waits for the box to be left.</summary>
    public void TypedProfileName()
    {
        if (SelectedProfile is not { } row || row.Name.Trim().Length == 0) return;
        _profileTyped = row;
        RestartTypedSave();
    }

    /// <summary>Finished typing a profile's name, so a blank one can get its default.</summary>
    public void SaveProfileName()
    {
        if (SelectedProfile is not { } row) return;
        if (row.Name.Trim().Length == 0) row.Name = ClientLayoutsText.ProfileNewName;
        _profileTyped = row;
        _ = SaveTypedNowAsync();
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
        var result = ClientLayoutCalculator.Calculate(_monitorLayout!.Root, _monitorFull ? monitor.Bounds : monitor.Work, UiScaleService.Scale);
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
                names, AddCharacterAsync, name => RemoveCharacterAsync(row?.Id, client.AreaId, name))
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
                layout == null ? null : ClientLayoutCalculator.Calculate(layout.Root, space, UiScaleService.Scale),
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
        var built = await _store.BuildAppliedAsync(_deviceId, _profileId);
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
        return monitor == null ? "" : new AppliedDesktop { Monitors = [monitor] }.Write();    // without the profile: two can match
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
        await _store.SaveMonitorAsync(_deviceId, _profileId, _selectedMonitorKey, _monitorLayout?.Id, _monitorFull,
            _monitorPanelStart?.Value ?? PanelStart.WithConsole);
        _monitorRows = await _store.ListMonitorsAsync(_deviceId, _profileId);
        _characters = await _store.ListCharactersAsync(_deviceId, _profileId);   // a new layout takes over the groups
        ShowMonitor();
        ShowLayoutWarnings();
        await ShowPendingAsync();
    }

    private async Task AddCharacterAsync(ClientCellVm cell, string name)
    {
        var key = _selectedMonitorKey;
        if (key == null) return;
        var row = _monitorRows.FirstOrDefault(r => r.HardwareKey == key);
        var monitorId = row?.Id ?? await _store.SaveMonitorAsync(_deviceId, _profileId, key, _monitorLayout?.Id, _monitorFull,
            _monitorPanelStart?.Value ?? PanelStart.WithConsole);
        await _store.AssignCharacterAsync(_deviceId, _profileId, monitorId, cell.AreaId, name);
        await ReloadAssignmentsAsync();
    }

    private async Task RemoveCharacterAsync(int? monitorId, string areaId, string name)
    {
        if (monitorId is { } id) await _store.UnassignCharacterAsync(id, areaId, name);
        await ReloadAssignmentsAsync();
    }

    private async Task ReloadAssignmentsAsync()
    {
        _monitorRows = await _store.ListMonitorsAsync(_deviceId, _profileId);
        _characters = await _store.ListCharactersAsync(_deviceId, _profileId);
        ShowMonitor();
        await ShowPendingAsync();
    }

    private async Task ApplyAsync()
    {
        var built = await _store.ApplyAsync(_deviceId, _profileId);
        _appliedJson = built.Write();
        ShowProfileInUse();
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

    private Task NewLayoutAsync() => NewLayoutAsync(null);

    /// <summary>A new layout: blank, one empty panel, or a copy of a template under the template's name.</summary>
    public Task NewLayoutAsync(LayoutTemplate? template) => Run(async () =>
    {
        var root = template?.Root ?? ClientLayoutEditing.BlankRoot();
        await AddLayoutAsync(template?.Name ?? ClientLayoutsText.NewLayoutName, root);
    });

    /// <summary>The templates a new layout can start from, read fresh, so one just exported there is listed.</summary>
    public async Task<List<LayoutTemplate>> TemplatesAsync()
    {
        try
        {
            return await LayoutTemplates.ListAsync(await MumbleServerRowsAsync());
        }
        catch (Exception ex)
        {
            Status = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.StatusFailed, ex.Message);
            _errorLogger.Log("ClientLayouts", "reading layout templates", ex);
            return [];
        }
    }

    /// <summary>A layout kept as a new one under a name of its own, and selected.</summary>
    private async Task<string> AddLayoutAsync(string name, LayoutArea root)
    {
        // A name of its own, so the monitors' lists can tell it from the one it may be a copy of.
        if (Layouts.Any(l => string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            var taken = Layouts.Select(l => l.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var n = 2;
            while (taken.Contains($"{name} ({n})")) n++;
            name = $"{name} ({n})";
        }
        var row = new LayoutRowVm(null, name, root);
        row.Id = await _store.SaveLayoutAsync(null, row.Name, root);
        Layouts.Add(row);
        RefillChoices();
        SelectedLayout = row;
        return name;
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

    /// <summary>The selected layout written to a file, as it is now.</summary>
    public Task ExportLayoutAsync(Func<Task<Stream>> open) => Run(async () =>
    {
        if (SelectedLayout is not { } row) return;
        await SaveTypedNowAsync();
        await using var stream = await open();
        await ClientLayoutFile.WriteAsync(stream, row.Name, row.Root, await MumbleServerRowsAsync());
        Status = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.StatusLayoutExported, row.Name);
    });

    /// <summary>A layout from a file, kept as a new one beside the others and selected.</summary>
    public Task ImportLayoutAsync(Func<Task<Stream>> open) => Run(async () =>
    {
        (string Name, LayoutArea Root) read;
        try
        {
            await using var stream = await open();
            read = await ClientLayoutFile.ReadAsync(stream, await MumbleServerRowsAsync());
        }
        catch (LayoutFileException e)
        {
            Status = e.Newer ? ClientLayoutsText.StatusLayoutFileNewer : ClientLayoutsText.StatusNotLayoutFile;
            return;
        }
        var name = await AddLayoutAsync(read.Name.Length > 0 ? read.Name : ClientLayoutsText.NewLayoutName, read.Root);
        Status = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.StatusLayoutImported, name);
    });

    private async Task<List<MumbleServer>> MumbleServerRowsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.MumbleServers.AsNoTracking().ToListAsync();
    }

    private async Task DeleteLayoutAsync()
    {
        if (SelectedLayout is not { Id: { } id } row) return;
        await SaveTypedNowAsync();
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

    /// <summary>Finished typing a layout's name, so a blank one can get its default.</summary>
    public void SaveLayoutName()
    {
        if (SelectedLayout is not { } row) return;
        if (row.Name.Trim().Length == 0) row.Name = ClientLayoutsText.NewLayoutName;
        _ = Run(SaveEditedLayoutAsync);
    }

    /// <summary>A key pressed in the layout's name box. A blank name waits for the box to be left.</summary>
    public void TypedLayoutName()
    {
        if (SelectedLayout is { } row && row.Name.Trim().Length > 0) SaveTypedSoon(row);
    }

    /// <summary>A change on the canvas or in the boxes beside it has finished.</summary>
    private async Task SaveEditedLayoutAsync()
    {
        if (SelectedLayout is not { } row) return;
        RecordStep(row);
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
        var control = docked?.Panel.Control ?? (area?.Content == AreaContent.Control ? area.Control : null);
        var isPanel = docked is { Panel: { Control: null, Grid: null } } || (docked == null && area?.Content == AreaContent.Panel);

        HasSize = docked != null;
        SizeLabel = docked?.Panel.Edge is DockEdge.Top or DockEdge.Bottom ? ClientLayoutsText.LabelHeight : ClientLayoutsText.LabelWidth;
        HasNamed = isPanel;
        HasControl = control != null;
        SelectedControlName = ClientLayoutPainter.ControlName(control);
        _selectedName = docked?.Panel.Name ?? area?.Name ?? "";
        this.RaisePropertyChanged(nameof(SelectedName));
        _selectedSize = docked?.Panel.Size;
        this.RaisePropertyChanged(nameof(SelectedSize));

        // Directly, so that showing what's there doesn't count as choosing it.
        var look = root == null || SelectedId == null ? null : LookOf(root, SelectedId, create: false);
        _selectedColor = PanelLooks.ColorOf(look) is { } own && Color.TryParse(own, out var color) ? color : ClientPanelSurface.ThemeGround(null);
        this.RaisePropertyChanged(nameof(SelectedColor));
        _selectedStyle = StyleChoices.First(s => s.Style == PanelLooks.StyleOf(look));
        this.RaisePropertyChanged(nameof(SelectedStyle));

        ShowDock(root, docked);
        ShowOutline();

        var grid = root == null || SelectedId == null ? null : ClientLayoutEditing.FindGrid(root, SelectedId);
        // A panel has a colour, an edge and padding; a grid only padding: its panel, or the panels in it, have the rest.
        HasLook = isPanel;
        HasPadding = isPanel || grid != null;
        HasGrid = grid != null;
        _gridColumns = grid == null ? null : Math.Max(grid.Columns.Count, 1);
        _gridRows = grid == null ? null : Math.Max(grid.Rows.Count, 1);

        this.RaisePropertyChanged(nameof(GridColumns));
        this.RaisePropertyChanged(nameof(GridRows));
        var padding = grid != null ? grid.Padding : look?.Padding;
        _paddingLeft = padding?.Left ?? 0;
        _paddingTop = padding?.Top ?? 0;
        _paddingRight = padding?.Right ?? 0;
        _paddingBottom = padding?.Bottom ?? 0;
        this.RaisePropertyChanged(nameof(PaddingLeft));
        this.RaisePropertyChanged(nameof(PaddingTop));
        this.RaisePropertyChanged(nameof(PaddingRight));
        this.RaisePropertyChanged(nameof(PaddingBottom));

        var options = docked?.Panel.Options ?? area?.Options;
        IsWorklistControl = PanelControls.Parse(control) == PanelControlKind.Worklist;
        if (IsWorklistControl)
        {
            _selectedWorklistMode = WorklistModes.First(m => m.Mode == PanelControls.WorklistModeOf(options));
            this.RaisePropertyChanged(nameof(SelectedWorklistMode));
        }
        IsIntelControl = PanelControls.Parse(control) == PanelControlKind.Intel;
        if (IsIntelControl)
        {
            _selectedIntelMode = IntelModes.First(m => m.Mode == PanelControls.IntelModeOf(options));
            this.RaisePropertyChanged(nameof(SelectedIntelMode));
            _selectedIntelRange = IntelRangeChoices.First(r => r.Jumps == PanelControls.IntelRangeOf(options));
            this.RaisePropertyChanged(nameof(SelectedIntelRange));
        }
        IsMenuControl = PanelControls.Parse(control) == PanelControlKind.Menu;
        if (IsMenuControl) ShowMenuTargets(options);
        IsToolContainer = PanelControls.Parse(control) == PanelControlKind.ToolContainer;
        if (IsToolContainer) ShowContainer(options);

        IsGroupChatRoom = PanelControls.Parse(control) == PanelControlKind.GroupChatRoom;
        if (IsGroupChatRoom)
        {
            var rooms = _groupRooms?.Invoke() ?? [];
            GroupRooms.Clear();
            foreach (var room in rooms) GroupRooms.Add(room);
            // A room set once and no longer in Group Chat stays chosen, by its saved name.
            if (options?.GroupChatRoom is { } saved && GroupRooms.All(r => r.Id != saved))
                GroupRooms.Add(new PanelRoomChoice(saved, ClientLayoutsText.GroupChatRoomGone));
            _selectedGroupRoom = GroupRooms.FirstOrDefault(r => r.Id == options?.GroupChatRoom);
            this.RaisePropertyChanged(nameof(SelectedGroupRoom));
        }

        IsMumbleControl = PanelControls.Parse(control) == PanelControlKind.Mumble;
        if (IsMumbleControl)
        {
            var servers = _mumbleServers?.Invoke() ?? [];
            foreach (var gone in MumbleServers.Where(s => !servers.Contains(s)).ToList()) MumbleServers.Remove(gone);
            foreach (var server in servers.Where(s => !MumbleServers.Contains(s))) MumbleServers.Add(server);
            _selectedMumbleServer = MumbleServers.FirstOrDefault(s => s.Id == options?.MumbleServerId);
            this.RaisePropertyChanged(nameof(SelectedMumbleServer));
            _selectedMumbleMode = MumbleModes.First(m => m.Mode == PanelControls.MumbleModeOf(options));
            this.RaisePropertyChanged(nameof(SelectedMumbleMode));
        }
    }

    /// <summary>Changes what's chosen for the selected control, and saves shortly after.</summary>
    private void OptionsOfSelection(Action<ControlOptions> change)
    {
        var row = SelectedLayout;
        if (row == null || SelectedId == null) return;
        if (ClientLayoutEditing.FindDocked(row.Root, SelectedId) is { Panel.Control: not null } docked)
            change(docked.Panel.Options ??= new ControlOptions());
        else if (ClientLayoutEditing.FindArea(row.Root, SelectedId) is { Content: AreaContent.Control } area)
            change(area.Options ??= new ControlOptions());
        else return;
        StepNow(row);
    }

    /// <summary>Changes how the selected panel looks, and saves once the changes stop, as a color drag makes many.</summary>
    /// <param name="step">A choice made in one go, as a style is, which is a step to undo of its own
    /// at once rather than whenever the changes stop.</param>
    private void LookOfSelection(Action<PanelLook> change, bool step = false)
    {
        var row = SelectedLayout;
        if (row == null || SelectedId == null || LookOf(row.Root, SelectedId, create: true) is not { } look) return;
        change(look);
        Revision++;
        if (step) StepNow(row);
        else SaveTypedSoon(row);
    }

    /// <summary>Changes the selected panel's or grid's padding, and saves once the changes stop.
    /// Back to none on every side, it's not kept at all.</summary>
    private void PaddingOfSelection(Action<LayoutPadding> change)
    {
        if (SelectedLayout is { } row && SelectedId != null && ClientLayoutEditing.FindGrid(row.Root, SelectedId) is { } grid)
        {
            change(grid.Padding ??= new LayoutPadding());
            if (grid.Padding.IsNone) grid.Padding = null;
            Revision++;
            SaveTypedSoon(row);
            return;
        }
        LookOfSelection(look =>
        {
            change(look.Padding ??= new LayoutPadding());
            if (look.Padding.IsNone) look.Padding = null;
        });
    }

    /// <summary>
    /// The look of the panel with this id, made if asked for and it hasn't one. Null for anything
    /// else: a grid has no look of its own, only padding.
    /// </summary>
    private static PanelLook? LookOf(LayoutArea root, string id, bool create)
    {
        if (ClientLayoutEditing.FindDocked(root, id) is { } docked)
            return docked.Panel.Control != null || docked.Panel.Grid != null ? null : create ? docked.Panel.Look ??= new PanelLook() : docked.Panel.Look;
        return ClientLayoutEditing.FindArea(root, id) switch
        {
            { Content: AreaContent.Panel } area => create ? area.Look ??= new PanelLook() : area.Look,
            _ => null,
        };
    }

    private void ResetColor()
    {
        LookOfSelection(look => look.Color = null, step: true);
        ShowSelection();
    }

    /// <summary>
    /// A key pressed in the name or size box goes straight into the panel. Waiting for the box to
    /// lose focus lost edits: clicking the drawing doesn't take focus, and coming back to the tab
    /// reads the layouts again.
    /// </summary>
    private void TypedIntoSelection()
    {
        var row = SelectedLayout;
        if (row == null || SelectedId == null) return;
        // Panels have names, and so do Tool Containers, so a menu can say which one it opens tools in.
        static bool Named(string? control) => control == null || PanelControls.Parse(control) == PanelControlKind.ToolContainer;
        if (ClientLayoutEditing.FindDocked(row.Root, SelectedId) is { } docked)
        {
            if (docked.Panel.Grid == null && Named(docked.Panel.Control)) docked.Panel.Name = SelectedName;
            if (SelectedSize is { } size) docked.Panel.Size = Math.Max((int)size, 1);
        }
        else if (ClientLayoutEditing.FindArea(row.Root, SelectedId) is { } area
                 && (area.Content == AreaContent.Panel || (area.Content == AreaContent.Control && Named(area.Control) && area.Control != null)))
        {
            area.Name = SelectedName;
        }
        else return;
        Revision++;
        SaveTypedSoon(row);
    }

    /// <summary>A change made in one go: a step to undo at once, saved with the rest shortly after.</summary>
    private void StepNow(LayoutRowVm row)
    {
        SaveTypedSoon(row);
        RecordStep(row);
    }

    /// <summary>Saves once the typing stops, not for every key.</summary>
    private void SaveTypedSoon(LayoutRowVm row)
    {
        _typedInto = row;
        // A change waiting to be saved can already be undone, and ends what could be redone.
        if (ReferenceEquals(row, SelectedLayout))
        {
            CanUndo = true;
            CanRedo = false;
        }
        RestartTypedSave();
    }

    /// <summary>A key pressed in this desktop's name box.</summary>
    public void TypedDeviceName()
    {
        _deviceNameTyped = true;
        RestartTypedSave();
    }

    private void RestartTypedSave()
    {
        _typedSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => _ = SaveTypedNowAsync());
        _typedSave.Stop();
        _typedSave.Start();
    }

    /// <summary>
    /// Takes the row rather than the current selection, as this also runs when the selection is
    /// about to change. It leaves the boxes alone: putting the panel's size back into a size box
    /// someone has just cleared to retype would undo what they're doing.
    /// </summary>
    private Task SaveTypedNowAsync()
    {
        _typedSave?.Stop();
        if (_deviceNameTyped)
        {
            _deviceNameTyped = false;
            SaveDeviceName();
        }
        if (_profileTyped is { } profile)
        {
            _profileTyped = null;
            _ = Run(() => _store.RenameProfileAsync(profile.Id, profile.Name.Trim()));
        }
        if (_typedInto is not { } row) return Task.CompletedTask;
        _typedInto = null;
        RecordStep(row);
        return Run(async () =>
        {
            await _store.SaveLayoutAsync(row.Id, row.Name, row.Root);
            ShowMonitor();
            ShowLayoutWarnings();
            await ShowPendingAsync();
        });
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
                var result = ClientLayoutCalculator.Calculate(row.Root, saved.FullMonitor ? monitor.Bounds : monitor.Work, UiScaleService.Scale);
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
