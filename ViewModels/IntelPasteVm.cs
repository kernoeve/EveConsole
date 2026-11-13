using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Intel;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// The Intel tool's Paste tab: the Local member list or a d-scan pasted into a box (the user pastes;
/// our code never reads the clipboard), turned into a sighting for the system chosen.
///
/// <para>What the paste would do shows under the box as it is typed, 300 ms after the last change: who
/// is on it, grouped by standing, alliance and corporation; for a Local, how many sightings it replaces,
/// since a Local clears the system first and a partial paste would clear it just the same. Save, or
/// Ctrl+Enter, stores it; the line shows in the Feed and on the Board at once.</para>
/// </summary>
public sealed class IntelPasteVm : ReactiveObject
{
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(300);

    private readonly IntelViewModel _tool;
    private readonly IntelPasteService? _service;
    private readonly UniverseMapService? _map;
    private readonly ObservableCollection<Character> _characters;
    private readonly DispatcherTimer _debounce;
    private long _ticket;
    private bool _characterChosen;
    private IntelSnapshot _snapshot = IntelSnapshot.Empty;
    private IntelPastePreview? _preview;
    private IReadOnlyList<IntelRecentPaste> _recent = [];

    public IntelPasteVm(IntelViewModel tool, IntelPasteService? service, UniverseMapService? map, ObservableCollection<Character> characters)
    {
        _tool = tool;
        _service = service;
        _map = map;
        _characters = characters;
        _debounce = new DispatcherTimer(PreviewDelay, DispatcherPriority.Background, (_, _) => { _debounce!.Stop(); Preview(); });
        _debounce.Stop();
        _characters.CollectionChanged += OnCharactersChanged;
        SyncCharacters();

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, this.WhenAnyValue(x => x.CanSave));
        SaveCommand.ThrownExceptions.Subscribe(ex => StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message));

        this.WhenAnyValue(x => x.OtherSystemText).Skip(1).Throttle(TimeSpan.FromMilliseconds(180))
            .SelectMany(t => Observable.FromAsync(() => SuggestAsync(t))).Subscribe();
    }

    // ── The box ──────────────────────────────────────────────────────────────

    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            this.RaiseAndSetIfChanged(ref _text, value);
            var kind = IntelPasteService.Classify(value);
            DetectedText = kind switch
            {
                IntelPasteKind.Local   => IntelText.PasteDetectedLocal,
                IntelPasteKind.Dscan   => IntelText.PasteDetectedDscan,
                IntelPasteKind.Unknown => IntelText.PasteDetectedUnknown,
                _                      => "",
            };
            IsUnknown = kind == IntelPasteKind.Unknown;
            StatusText = "";
            Stale();
        }
    }

    private string _detected = "";
    /// <summary>What the paste looks like as typed: a Local list, a d-scan, or neither.</summary>
    public string DetectedText { get => _detected; private set => this.RaiseAndSetIfChanged(ref _detected, value); }

    private bool _isUnknown;
    public bool IsUnknown { get => _isUnknown; private set => this.RaiseAndSetIfChanged(ref _isUnknown, value); }

    private string _counts = "";
    /// <summary>"12 names · 11 resolved · 1 unknown".</summary>
    public string CountsText { get => _counts; private set => this.RaiseAndSetIfChanged(ref _counts, value); }

    // ── The system ───────────────────────────────────────────────────────────

    /// <summary>Where each online character is, then any other system chosen.</summary>
    public ObservableCollection<IntelChoice<int>> SystemChoices { get; } = [];

    private IntelChoice<int>? _selectedSystem;
    public IntelChoice<int>? SelectedSystem
    {
        get => _selectedSystem;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedSystem)) return;
            this.RaiseAndSetIfChanged(ref _selectedSystem, value);
            _systemId = value.Value;
            Stale();
        }
    }

    private int _systemId;

    private string _otherSystemText = "";
    /// <summary>Any other system, by name.</summary>
    public string OtherSystemText { get => _otherSystemText; set => this.RaiseAndSetIfChanged(ref _otherSystemText, value); }

    public ObservableCollection<PlaceMatch> OtherPlaces { get; } = [];

    private PlaceMatch? _otherPlace;
    public PlaceMatch? OtherPlace
    {
        get => _otherPlace;
        set
        {
            this.RaiseAndSetIfChanged(ref _otherPlace, value);
            if (value is { IsSystem: true } place) ChooseSystem(place.SystemId, place.Label);
        }
    }

    private async Task SuggestAsync(string text)
    {
        if (_map is null || text.Trim().Length < 2) return;
        try
        {
            var found = (await _map.SearchPlacesAsync(text, shownNames: true)).Where(p => p.IsSystem).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OtherPlaces.Clear();
                foreach (var p in found) OtherPlaces.Add(p);
            });
        }
        catch
        {
            // The old suggestions stay; the next keystroke asks again.
        }
    }

    private void ChooseSystem(int systemId, string? name = null)
    {
        if (systemId <= 0) return;
        var choice = SystemChoices.FirstOrDefault(c => c.Value == systemId);
        if (choice is null)
        {
            choice = new IntelChoice<int>(systemId, name ?? _snapshot.SystemName(systemId));
            SystemChoices.Add(choice);
        }
        SelectedSystem = choice;
    }

    // ── Who pasted it ────────────────────────────────────────────────────────

    public ObservableCollection<IntelChoice<long>> CharacterChoices { get; } = [];

    private IntelChoice<long>? _selectedCharacter;
    /// <summary>The character whose Local or d-scan it is: the report's reporter, and whose standings
    /// colour the preview.</summary>
    public IntelChoice<long>? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedCharacter)) return;
            this.RaiseAndSetIfChanged(ref _selectedCharacter, value);
            Stale();
        }
    }

    /// <summary>Chosen by hand: no longer follows "Distance from".</summary>
    public void CharacterPicked() => _characterChosen = true;

    private void OnCharactersChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncCharacters();

    /// <summary>The tool closed: stops following the app's character list.</summary>
    internal void Detach()
    {
        _characters.CollectionChanged -= OnCharactersChanged;
        _debounce.Stop();
    }

    private void SyncCharacters()
    {
        var wanted = _characters.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new IntelChoice<long>(c.Id, c.Name)).ToList();
        if (CharacterChoices.Select(c => c.Value).SequenceEqual(wanted.Select(w => w.Value))) return;
        var held = _selectedCharacter?.Value;
        CharacterChoices.Clear();
        foreach (var c in wanted) CharacterChoices.Add(c);
        _selectedCharacter = null;
        SelectedCharacter = CharacterChoices.FirstOrDefault(c => c.Value == held) ?? Default();
    }

    /// <summary>The "Distance from" character, else the first online, else the first there is.</summary>
    private IntelChoice<long>? Default()
    {
        var from = _tool.FromCharacterId;
        var online = _snapshot.Own.Select(o => o.CharacterId).ToList();
        return CharacterChoices.FirstOrDefault(c => from != 0 && c.Value == from)
               ?? CharacterChoices.FirstOrDefault(c => online.Contains(c.Value))
               ?? CharacterChoices.FirstOrDefault();
    }

    /// <summary>"Distance from" changed: the character pasting follows it, unless one was picked here.</summary>
    internal void FromChanged()
    {
        if (_characterChosen || Default() is not { } d) return;
        SelectedCharacter = d;
        // Nothing pasted yet: the system follows too, to where that character is.
        if (_snapshot.Own.FirstOrDefault(o => o.CharacterId == d.Value) is { } own && _preview is null && _text.Length == 0)
            ChooseSystem(own.SystemId);
    }

    /// <summary>The panel's paste: its character, and the system it is in.</summary>
    public void Choose(long? characterId, int? systemId)
    {
        if (characterId is long id && CharacterChoices.FirstOrDefault(c => c.Value == id) is { } who)
        {
            _characterChosen = true;
            SelectedCharacter = who;
        }
        var system = systemId ?? _snapshot.Own.FirstOrDefault(o => o.CharacterId == characterId)?.SystemId;
        if (system is int s) ChooseSystem(s);
    }

    // ── From the tool ────────────────────────────────────────────────────────

    internal void SnapshotChanged(IntelSnapshot snapshot)
    {
        _snapshot = snapshot;

        // Where each online character is, as "QZ-X77 (Pilot A here)"; a system chosen otherwise stays.
        var here = snapshot.Own.GroupBy(o => o.SystemId)
            .Select(g => new IntelChoice<int>(g.Key, string.Format(CultureInfo.CurrentCulture, IntelText.PasteSystemHere,
                snapshot.SystemName(g.Key), string.Join(", ", g.Select(o => o.Name)))))
            .ToList();
        var kept = SystemChoices.Where(c => here.All(h => h.Value != c.Value) && c.Value == _systemId).ToList();
        var wanted = here.Concat(kept).ToList();
        if (!SystemChoices.Select(c => (c.Value, c.Label)).SequenceEqual(wanted.Select(w => (w.Value, w.Label))))
        {
            SystemChoices.Clear();
            foreach (var c in wanted) SystemChoices.Add(c);
            _selectedSystem = null;
        }
        if (_selectedSystem is null || SystemChoices.All(c => !ReferenceEquals(c, _selectedSystem)))
        {
            var mine = snapshot.Own.FirstOrDefault(o => o.CharacterId == _selectedCharacter?.Value)?.SystemId;
            var pick = SystemChoices.FirstOrDefault(c => c.Value == _systemId)
                       ?? SystemChoices.FirstOrDefault(c => c.Value == mine)
                       ?? SystemChoices.FirstOrDefault();
            if (pick is not null)
            {
                var moved = pick.Value != _systemId;
                _selectedSystem = pick;
                _systemId = pick.Value;
                this.RaisePropertyChanged(nameof(SelectedSystem));
                if (moved) Stale();
            }
        }
        if (!_characterChosen && _selectedCharacter is null) SelectedCharacter = Default();

        // A Local's "replaces N" follows what the snapshot now holds, without asking the database again.
        if (_preview is { Kind: IntelPasteKind.Local } p && p.SystemId == _systemId)
            ShowReplaces(IntelPasteService.ReplacedIn(snapshot, p.SystemId, _tool.Clock()));
    }

    internal void OnScreen() => _ = LoadRecentAsync();

    // ── The preview ──────────────────────────────────────────────────────────

    /// <summary>
    /// The box, the system or the character changed: what is shown no longer describes what Save would
    /// store, so Save is off until the new preview is on screen, and that preview comes after the pause.
    /// ⚠️ The preview is the safeguard: a Local clears the system first, and only its "Replaces N" tells
    /// the user a partial paste would clear it all the same. Nothing is ever saved that was not shown.
    /// </summary>
    private void Stale()
    {
        CanSave = false;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>The preview is of what is in the box now, with the system and the character chosen now.</summary>
    private bool IsCurrent(IntelPastePreview preview) =>
        preview.Text == _text && preview.SystemId == _systemId && preview.ViewerCharacterId == (_selectedCharacter?.Value ?? 0);

    /// <summary>The preview now, rather than after the pause: a check's, and Save's or Ctrl+Enter's when
    /// pressed before the pause ran out (which shows it, and saves nothing).</summary>
    public Task PreviewNowAsync()
    {
        _debounce.Stop();
        return PreviewCoreAsync();
    }

    private void Preview() => _ = PreviewCoreAsync();

    private async Task PreviewCoreAsync()
    {
        var ticket = Interlocked.Increment(ref _ticket);
        var text = _text;
        var kind = IntelPasteService.Classify(text);
        if (_service is null || kind is IntelPasteKind.Empty or IntelPasteKind.Unknown)
        {
            Show(null);
            return;
        }

        var viewer = _selectedCharacter?.Value ?? 0;
        var system = _systemId;
        var snapshot = _snapshot;
        var now = _tool.Clock();
        IsBusy = true;
        try
        {
            var preview = await Task.Run(() => _service.PreviewAsync(text, viewer, system, snapshot, now));
            if (Interlocked.Read(ref _ticket) == ticket) Show(preview);
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref _ticket) == ticket)
            {
                Show(null);
                StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message);
            }
        }
        finally
        {
            if (Interlocked.Read(ref _ticket) == ticket) IsBusy = false;
        }
    }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    /// <summary>Shows a preview given rather than worked out: for a check.</summary>
    public void Show(IntelPastePreview? preview)
    {
        var culture = CultureInfo.CurrentCulture;
        _preview = preview;
        var local = preview is { Kind: IntelPasteKind.Local };
        var dscan = preview is { Kind: IntelPasteKind.Dscan };
        IsLocal = local;
        IsDscan = dscan;

        if (preview is null)
        {
            CountsText = "";
            PlacesText = ReplacesText = UnknownText = OwnText = "";
            Groups = [];
            DscanShipsText = DscanFlagsText = "";
            DscanHulls = [];
        }
        else if (local)
        {
            CountsText = Plural(nameof(IntelText.PasteNamesCountOther), preview.Names.Count, preview.Pilots.Count, preview.Unresolved.Count);
            PlacesText = preview.SystemId > 0
                ? Plural(nameof(IntelText.PastePlacesOther), preview.Pilots.Count, SystemNameOf(preview.SystemId, preview.SystemName))
                : IntelText.PasteChooseSystem;
            ShowReplaces(preview.Replaces);
            UnknownText = preview.Unresolved.Count > 0 ? Plural(nameof(IntelText.PasteUnknownNamesOther), preview.Unresolved.Count) : "";
            OwnText = preview.Own > 0 ? string.Format(culture, IntelText.PasteOwnNotCounted, preview.Own) : "";
            Groups = [.. preview.Groups.Select(g => new IntelPasteGroupVm(g))];
            DscanShipsText = DscanFlagsText = "";
            DscanHulls = [];
        }
        else
        {
            var d = preview.Dscan;
            CountsText = Plural(nameof(IntelText.PasteDscanCountOther), d.Ships, d.Unknown);
            PlacesText = preview.SystemId > 0 ? "" : IntelText.PasteChooseSystem;
            ReplacesText = UnknownText = OwnText = "";
            Groups = [];
            DscanShipsText = Plural(nameof(IntelText.PasteDscanShipsOther), d.Ships);
            DscanHulls = [.. d.Hulls.Select(h => IntelPictureVm.Ship(h.TypeId, SdeNames.Type(h.TypeId, h.Name), h.Count))];
            var flags = new List<string>();
            if (d.Bubbles > 0) flags.Add(string.Format(culture, IntelText.PasteDscanBubbles, d.Bubbles));
            if (d.Cynos > 0) flags.Add(string.Format(culture, IntelText.PasteDscanCyno, d.Cynos));
            DscanFlagsText = string.Join(" · ", flags);
            foreach (var h in DscanHulls) _ = h.LoadAsync();
        }
        foreach (var g in Groups) _ = g.LoadPicturesAsync();
        // A preview that came back after the box changed again is shown, but Save waits for the next one.
        CanSave = preview is { CanSave: true } && IsCurrent(preview);
        this.RaisePropertyChanged(nameof(HasPreview));
        this.RaisePropertyChanged(nameof(HasUnknown));
        this.RaisePropertyChanged(nameof(HasOwn));
        this.RaisePropertyChanged(nameof(HasDscanFlags));
    }

    /// <summary>A counted phrase in the form the interface language needs for the number.</summary>
    private static string Plural(string otherKey, long n, params object?[] more) =>
        Plurals.Format(IntelText.ResourceManager, otherKey, n, more);

    private void ShowReplaces(int replaces) =>
        ReplacesText = string.Format(CultureInfo.CurrentCulture, IntelText.PasteReplaces, replaces);

    /// <summary>A system's name in the interface language, from the snapshot or the paste's English one.</summary>
    private string SystemNameOf(int systemId, string english) =>
        _snapshot.SystemNames.ContainsKey(systemId) || english.Length == 0
            ? _snapshot.SystemName(systemId)
            : SdeNames.SolarSystem(systemId, english);

    public bool HasPreview => _preview is not null;

    private bool _isLocal, _isDscan;
    public bool IsLocal { get => _isLocal; private set => this.RaiseAndSetIfChanged(ref _isLocal, value); }
    public bool IsDscan { get => _isDscan; private set => this.RaiseAndSetIfChanged(ref _isDscan, value); }

    private string _places = "", _replaces = "", _unknown = "", _own = "";
    public string PlacesText   { get => _places;   private set => this.RaiseAndSetIfChanged(ref _places, value); }
    public string ReplacesText { get => _replaces; private set => this.RaiseAndSetIfChanged(ref _replaces, value); }
    public string UnknownText  { get => _unknown;  private set => this.RaiseAndSetIfChanged(ref _unknown, value); }
    public string OwnText      { get => _own;      private set => this.RaiseAndSetIfChanged(ref _own, value); }
    public bool HasUnknown => UnknownText.Length > 0;
    public bool HasOwn => OwnText.Length > 0;

    private IReadOnlyList<IntelPasteGroupVm> _groups = [];
    public IReadOnlyList<IntelPasteGroupVm> Groups { get => _groups; private set => this.RaiseAndSetIfChanged(ref _groups, value); }

    private string _dscanShips = "", _dscanFlags = "";
    public string DscanShipsText { get => _dscanShips; private set => this.RaiseAndSetIfChanged(ref _dscanShips, value); }
    public string DscanFlagsText { get => _dscanFlags; private set => this.RaiseAndSetIfChanged(ref _dscanFlags, value); }
    public bool HasDscanFlags => DscanFlagsText.Length > 0;

    private IReadOnlyList<IntelPictureVm> _dscanHulls = [];
    public IReadOnlyList<IntelPictureVm> DscanHulls { get => _dscanHulls; private set => this.RaiseAndSetIfChanged(ref _dscanHulls, value); }

    // ── Saving ───────────────────────────────────────────────────────────────

    private bool _canSave;
    public bool CanSave { get => _canSave; private set => this.RaiseAndSetIfChanged(ref _canSave, value); }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    private string _status = "";
    /// <summary>What the last Save did.</summary>
    public string StatusText { get => _status; private set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Ctrl+Enter in the box. Before the preview has caught up with the box, it shows the
    /// preview and stops: a second Ctrl+Enter saves what is now on screen.</summary>
    public void SaveFromKeyboard()
    {
        if (_debounce.IsEnabled || IsBusy || _preview is not { } shown || !IsCurrent(shown)) _ = PreviewNowAsync();
        else if (CanSave) SaveCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private async Task SaveAsync()
    {
        if (_service is null || _preview is not { CanSave: true } preview) return;
        // Only the preview on screen, of what is in the box now with the system and character chosen
        // now. Otherwise the fresh one is shown and nothing is saved: the user saves it once seen.
        if (_debounce.IsEnabled || !IsCurrent(preview))
        {
            await PreviewNowAsync();
            return;
        }

        await Task.Run(() => _service.SaveAsync(preview));
        var culture = CultureInfo.CurrentCulture;
        var system = SystemNameOf(preview.SystemId, preview.SystemName);
        StatusText = preview.Kind == IntelPasteKind.Local
            ? Plural(nameof(IntelText.PasteSavedLocalOther), preview.Names.Count, system)
            : Plural(nameof(IntelText.PasteSavedDscanOther), preview.Dscan.Ships, system);
        _text = "";
        this.RaisePropertyChanged(nameof(Text));
        DetectedText = "";
        IsUnknown = false;
        Interlocked.Increment(ref _ticket);
        Show(null);
        await LoadRecentAsync();
    }

    // ── Recent pastes ────────────────────────────────────────────────────────

    public BulkObservableCollection<IntelRecentPasteVm> Recent { get; } = [];
    public bool HasRecent => Recent.Count > 0;

    private async Task LoadRecentAsync()
    {
        if (_service is null) return;
        try
        {
            _recent = await Task.Run(() => _service.RecentAsync());
            ShowRecent(_recent);
        }
        catch
        {
            // The list is a reminder of what was pasted, not intel: without it the tab still works.
        }
    }

    /// <summary>Shows recent pastes given rather than read: for a check.</summary>
    public void ShowRecent(IReadOnlyList<IntelRecentPaste> recent)
    {
        _recent = recent;
        var now = _tool.Clock();
        var rows = recent.Select(r => new IntelRecentPasteVm(r, _snapshot, now)).ToList();
        Recent.ResetTo(rows);
        foreach (var r in rows) _ = r.Reporter.LoadAsync();
        this.RaisePropertyChanged(nameof(HasRecent));
    }

    internal void Tick(DateTimeOffset now)
    {
        foreach (var r in Recent) r.Tick(now);
    }
}

/// <summary>Pilots of one tier and alliance in a pasted Local, by corporation, each with their portrait.</summary>
public sealed class IntelPasteGroupVm
{
    public IntelPasteGroupVm(IntelPasteGroup group)
    {
        var culture = CultureInfo.CurrentCulture;
        TierBrush = IntelTierColors.Of(group.Tier);
        Name = group.Kind switch
        {
            IntelPasteGroupKind.Own => IntelText.PasteGroupYours,
            IntelPasteGroupKind.Unresolved => IntelText.PasteGroupUnresolved,
            IntelPasteGroupKind.NoAlliance => IntelText.PasteGroupNoAlliance,
            _ => group.Name.Length > 0 ? group.Name : string.Format(culture, MapText.AllianceNumbered, group.AllianceId),
        };
        TierName = group.Kind == IntelPasteGroupKind.Unresolved ? IntelText.PasteTierUnnamed : IntelWords.TierName(group.Tier);
        CountText = group.Count.ToString(culture);
        Corps = [.. group.Corps.Select(c => new IntelPasteCorpVm(c, group.Kind))];
    }

    public IBrush TierBrush { get; }
    public string Name { get; }
    public string TierName { get; }
    public string CountText { get; }
    public IReadOnlyList<IntelPasteCorpVm> Corps { get; }

    public Task LoadPicturesAsync() => Task.WhenAll(Corps.SelectMany(c => c.Pilots).Select(p => p.LoadAsync()));
}

/// <summary>The pilots of one corporation in a pasted Local.</summary>
public sealed class IntelPasteCorpVm
{
    public IntelPasteCorpVm(IntelPasteCorp corp, IntelPasteGroupKind kind)
    {
        Name = kind == IntelPasteGroupKind.Unresolved ? ""
             : corp.Name.Length > 0 ? corp.Name
             : corp.CorporationId > 0 ? string.Format(CultureInfo.CurrentCulture, MapText.CorporationNumbered, corp.CorporationId)
             : "";
        Pilots = [.. corp.Pilots.Select(p => p.CharacterId > 0 ? IntelPictureVm.Pilot(p.CharacterId, p.Name) : IntelPictureVm.Words(p.Name))];
    }

    public string Name { get; }
    public bool HasName => Name.Length > 0;
    public IReadOnlyList<IntelPictureVm> Pilots { get; }

    public void Open(IntelPictureVm picture) => picture.Open();
}

/// <summary>One recent paste: where, by whom (with their portrait), how long ago.</summary>
public sealed class IntelRecentPasteVm : ReactiveObject
{
    private readonly DateTimeOffset _at;

    public IntelRecentPasteVm(IntelRecentPaste paste, IntelSnapshot snapshot, DateTimeOffset now)
    {
        _at = paste.At;
        SystemName = snapshot.SystemNames.ContainsKey(paste.SystemId) ? snapshot.SystemName(paste.SystemId)
                   : SdeNames.SolarSystem(paste.SystemId, paste.SystemName.Length > 0 ? paste.SystemName : paste.SystemId.ToString(CultureInfo.InvariantCulture));
        KindText = paste.Source == IntelReportSource.LocalPaste ? IntelText.KindLocalPaste : IntelText.KindDscan;
        Reporter = paste.ReporterId > 0 ? IntelPictureVm.Pilot(paste.ReporterId, paste.ReporterName) : IntelPictureVm.Words(paste.ReporterName);
        CountText = Plurals.Format(IntelText.ResourceManager,
            paste.Source == IntelReportSource.LocalPaste ? nameof(IntelText.RecentPilotsOther) : nameof(IntelText.RecentShipsOther),
            paste.PlayerCount);
        Tick(now);
    }

    public string SystemName { get; }
    public string KindText { get; }
    public IntelPictureVm Reporter { get; }
    public string CountText { get; }

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => this.RaiseAndSetIfChanged(ref _ageText, value); }

    public void Tick(DateTimeOffset now) => AgeText = IntelWords.Age(_at, now);
}
