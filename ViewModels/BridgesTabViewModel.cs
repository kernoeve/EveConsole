using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// The map tool's jump bridges tab: every bridge known — read from ESI, or added by hand — and
/// the ways to add more: two system pickers, or a pasted list.
/// </summary>
public sealed class BridgesTabViewModel : MapTabViewModel
{
    private readonly JumpBridgeService  _bridges;
    private readonly UniverseMapService _map;

    public BridgesTabViewModel(MapToolViewModel tool, JumpBridgeService bridges, UniverseMapService map) : base(tool)
    {
        _bridges = bridges;
        _map     = map;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        ImportCommand = ReactiveCommand.CreateFromTask(ImportAsync);
        AddCommand.ThrownExceptions.Subscribe(ex => Message = string.Format(CommonText.ErrorWithMessage, ex.Message));
        ImportCommand.ThrownExceptions.Subscribe(ex => Message = string.Format(CommonText.ErrorWithMessage, ex.Message));

        // Suggestions for the two pickers: systems only.
        this.WhenAnyValue(x => x.FromText).Skip(1).Throttle(TimeSpan.FromMilliseconds(180))
            .SelectMany(t => Observable.FromAsync(() => SuggestAsync(t, FromPlaces))).Subscribe();
        this.WhenAnyValue(x => x.ToText).Skip(1).Throttle(TimeSpan.FromMilliseconds(180))
            .SelectMany(t => Observable.FromAsync(() => SuggestAsync(t, ToPlaces))).Subscribe();
    }

    public override string TabTitle => MapText.JumpBridgesTab;
    public override string TabGlyph => "⇄";

    // ── The list ─────────────────────────────────────────────────────────────

    public ObservableCollection<BridgeRowVm> Rows   { get; } = [];
    public ObservableCollection<string>      Unread { get; } = [];

    private string _summary = "";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    public bool HasRows   => Rows.Count > 0;
    public bool HasUnread => Unread.Count > 0;

    /// <summary>Shows a fresh reading. UI thread.</summary>
    public void Show(JumpBridgeList list)
    {
        Rows.Clear();
        foreach (var b in list.Bridges) Rows.Add(new BridgeRowVm(b, this));
        Unread.Clear();
        foreach (var g in list.Unread)
            Unread.Add(string.Format(MapText.BridgeUnreadLine,
                g.Name.Length > 0 ? g.Name : MapText.BridgeNoName,
                SdeNames.SolarSystem(g.SystemId, g.SystemName.Length > 0 ? g.SystemName : g.SystemId.ToString(CultureInfo.InvariantCulture))));

        Summary = string.Format(MapText.BridgesSummary, list.Bridges.Count,
                                list.Bridges.Count(b => b.FromEsi), list.Bridges.Count(b => b.IsManual));
        this.RaisePropertyChanged(nameof(HasRows));
        this.RaisePropertyChanged(nameof(HasUnread));
    }

    internal Task RemoveAsync(int id) => _bridges.RemoveAsync(id);

    // ── Adding one ───────────────────────────────────────────────────────────

    public ObservableCollection<PlaceMatch> FromPlaces { get; } = [];
    public ObservableCollection<PlaceMatch> ToPlaces   { get; } = [];

    private string _fromText = "";
    public string FromText { get => _fromText; set => this.RaiseAndSetIfChanged(ref _fromText, value); }

    private string _toText = "";
    public string ToText { get => _toText; set => this.RaiseAndSetIfChanged(ref _toText, value); }

    private PlaceMatch? _from;
    public PlaceMatch? From { get => _from; set => this.RaiseAndSetIfChanged(ref _from, value); }

    private PlaceMatch? _to;
    public PlaceMatch? To { get => _to; set => this.RaiseAndSetIfChanged(ref _to, value); }

    private string _note = "";
    public string Note { get => _note; set => this.RaiseAndSetIfChanged(ref _note, value); }

    private string _message = "";
    /// <summary>What the last add or import did.</summary>
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    public ReactiveCommand<Unit, Unit> AddCommand    { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }

    private async Task SuggestAsync(string text, ObservableCollection<PlaceMatch> into)
    {
        try
        {
            var found = (await _map.SearchPlacesAsync(text, shownNames: true)).Where(p => p.IsSystem).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                into.Clear();
                foreach (var p in found) into.Add(p);
            });
        }
        catch
        {
            // The old suggestions stay; the next keystroke asks again.
        }
    }

    private async Task AddAsync()
    {
        if (From is not { IsSystem: true } from || To is not { IsSystem: true } to)
        {
            Message = MapText.BridgePickBoth;
            return;
        }

        var added = await _bridges.AddAsync(from.SystemId, to.SystemId, Note);
        Message = added
            ? string.Format(MapText.BridgeAdded, from.Label, to.Label)
            : MapText.BridgeNotAdded;
        if (added)
        {
            From = To = null;
            FromText = ToText = Note = "";
        }
    }

    // ── Pasting a list ───────────────────────────────────────────────────────

    private string _pasteText = "";
    public string PasteText { get => _pasteText; set => this.RaiseAndSetIfChanged(ref _pasteText, value); }

    private async Task ImportAsync()
    {
        var result = await _bridges.ImportAsync(PasteText);
        Message = string.Format(MapText.BridgesImported, result.Added, result.AlreadyThere, result.NotRead.Count);
        // What could not be read stays in the box, so it can be corrected and imported again.
        PasteText = string.Join(Environment.NewLine, result.NotRead);
    }
}

/// <summary>One bridge in the list.</summary>
public sealed class BridgeRowVm
{
    private readonly BridgesTabViewModel _tab;

    public BridgeRowVm(JumpBridge b, BridgesTabViewModel tab)
    {
        _tab     = tab;
        Bridge   = b;
        SystemA  = SdeNames.SolarSystem(b.SystemA, b.NameA);
        SystemB  = SdeNames.SolarSystem(b.SystemB, b.NameB);
        Source   = b.FromEsi && b.IsManual ? MapText.BridgeSourceBoth
                 : b.IsManual              ? MapText.BridgeSourceManual
                 : b.BothEnds              ? MapText.BridgeSourceEsiBoth
                 :                           MapText.BridgeSourceEsiOne;
        Gates    = string.Join("\n", b.Gates.Select(g => g.OwnerName.Length > 0 ? $"{g.Name}  ({g.OwnerName})" : g.Name));
        Note     = b.Note ?? "";

        if (b.FuelExpires is { } fuel)
        {
            var days = (fuel - DateTimeOffset.UtcNow).TotalDays;
            Fuel     = string.Format(MapText.BridgeFuelDays, Math.Max(0, (int)days), fuel.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));
            FuelLow  = days < 7;
        }

        OpenACommand  = ReactiveCommand.Create(() => tab.Tool.OpenSystem(b.SystemA));
        OpenBCommand  = ReactiveCommand.Create(() => tab.Tool.OpenSystem(b.SystemB));
        RemoveCommand = ReactiveCommand.CreateFromTask(() => b.ManualId is int id ? _tab.RemoveAsync(id) : Task.CompletedTask);
        RemoveCommand.ThrownExceptions.Subscribe(_ => { });
    }

    public JumpBridge Bridge  { get; }
    public string     SystemA { get; }
    public string     SystemB { get; }
    public string     Source  { get; }
    public string     Gates   { get; }
    public string     Note    { get; }
    public string     Fuel    { get; } = "";
    public bool       FuelLow { get; }

    /// <summary>Only a bridge added by hand can be removed here; one from ESI goes when its gate does.</summary>
    public bool CanRemove => Bridge.IsManual;

    public ReactiveCommand<Unit, Unit> OpenACommand  { get; }
    public ReactiveCommand<Unit, Unit> OpenBCommand  { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}
