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
///
/// <para>The "Save:" chips choose the standings tiers a Local saves (Red, Orange and Neutral by default,
/// kept on this PC): a player copies all of Local, blues too, for speed. The preview still lists everyone,
/// the tiers left out folded to a dimmed line each, and the headline counts only what is saved. A Local
/// with nobody of the kept tiers on it saves as a clear of the system. Save is off, with the reason shown,
/// while every chip is off, while the chips leave out someone of Red, Orange or Neutral (a Local is a
/// replace, so they would be taken out of the system), and while a name is not looked up (ESI not
/// answering; the preview then asks again every 15 seconds).</para>
///
/// <para>Pasted by follows the system: the first online character's system and that same character by
/// default, and the character in a system chosen from the list, until the user picks Pasted by here.</para>
/// </summary>
public sealed class IntelPasteVm : ReactiveObject
{
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How soon a preview with names not looked up (ESI not answering) asks again.</summary>
    private static readonly TimeSpan LookupRetry = TimeSpan.FromSeconds(15);

    /// <summary>The tiers a pasted Local saves, kept on this PC as a list of tier numbers.</summary>
    public const string KeySaveTiers = "intel.paste_tiers";

    private static readonly StandingTier[] FiveTiers =
        [StandingTier.Red, StandingTier.Orange, StandingTier.Neutral, StandingTier.LightBlue, StandingTier.Blue];

    private readonly IntelViewModel _tool;
    private readonly IntelPasteService? _service;
    private readonly UniverseMapService? _map;
    private readonly ObservableCollection<Character> _characters;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _retry;
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
        // A Local held back by names not looked up asks again by itself, off the UI thread like any preview.
        _retry = new DispatcherTimer(LookupRetry, DispatcherPriority.Background, (_, _) =>
        {
            _retry!.Stop();
            if (!_debounce.IsEnabled && !IsBusy && _preview is { Block: IntelPasteBlock.Lookup } p && IsCurrent(p)) Preview();
        });
        _retry.Stop();
        _keep = ReadKeep();
        _characters.CollectionChanged += OnCharactersChanged;
        SyncCharacters();

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, this.WhenAnyValue(x => x.CanSave));
        SaveCommand.ThrownExceptions.Subscribe(ex => StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message));
        ClearCommand = ReactiveCommand.Create(Clear);

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

    /// <summary>The Clear button by the box: empties the box and the preview; the system and Pasted by stay.</summary>
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    /// <summary>Empties the box and the preview, at once rather than after the pause. The system and the
    /// character chosen stay, and so does what the last Save said.</summary>
    public void Clear()
    {
        ClearBox();
        StatusText = "";
    }

    private void ClearBox()
    {
        _debounce.Stop();
        _retry.Stop();
        Interlocked.Increment(ref _ticket);
        _text = "";
        this.RaisePropertyChanged(nameof(Text));
        DetectedText = "";
        IsUnknown = false;
        IsBusy = false;
        Show(null);
    }

    // ── The standings saved ──────────────────────────────────────────────────

    private readonly HashSet<StandingTier> _keep;

    /// <summary>The tiers a pasted Local saves now.</summary>
    public IReadOnlySet<StandingTier> SaveTiers => _keep;

    public bool SaveRed       { get => _keep.Contains(StandingTier.Red);       set => SetKeep(StandingTier.Red, value); }
    public bool SaveOrange    { get => _keep.Contains(StandingTier.Orange);    set => SetKeep(StandingTier.Orange, value); }
    public bool SaveNeutral   { get => _keep.Contains(StandingTier.Neutral);   set => SetKeep(StandingTier.Neutral, value); }
    public bool SaveLightBlue { get => _keep.Contains(StandingTier.LightBlue); set => SetKeep(StandingTier.LightBlue, value); }
    public bool SaveBlue      { get => _keep.Contains(StandingTier.Blue);      set => SetKeep(StandingTier.Blue, value); }

    /// <summary>What this PC chose last, else Red, Orange and Neutral: everyone not blue.</summary>
    private static HashSet<StandingTier> ReadKeep()
    {
        string? saved;
        try { saved = UiState.Get(KeySaveTiers); }
        catch { saved = null; }
        if (saved is null) return [.. StandingTiers.Hostile];
        // "-" is every tier switched off: an empty value would read back as never chosen.
        if (saved.Trim() == "-") return [];
        HashSet<StandingTier> read = [.. saved.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => int.TryParse(t.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (StandingTier?)n : null)
                        .Where(t => t is { } v && FiveTiers.Contains(v)).Select(t => t!.Value)];
        // ⚠️ A damaged value with no tier in it is the default, never every chip off.
        return read.Count > 0 ? read : [.. StandingTiers.Hostile];
    }

    /// <summary>A chip clicked: kept on this PC, and the preview on screen says at once what Save now stores.
    /// Nothing is asked of the database again: the tiers are already known.</summary>
    private void SetKeep(StandingTier tier, bool on, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (on == _keep.Contains(tier)) return;
        if (on) _keep.Add(tier); else _keep.Remove(tier);
        this.RaisePropertyChanged(name);
        var value = _keep.Count == 0 ? "-" : string.Join(",", FiveTiers.Where(_keep.Contains).Select(t => ((int)t).ToString(CultureInfo.InvariantCulture)));
        try { UiState.Set(KeySaveTiers, value); }
        catch { /* A view's state is a nicety: a choice that cannot be kept is simply not kept. */ }
        if (_preview is not null) Show(_preview);
    }

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
            FollowSystem();
            Stale();
        }
    }

    /// <summary>
    /// The system chosen is where another of the user's characters is ("QZ-X77 (Pilot B here)"): Pasted by
    /// moves to that character, since it is its Local, unless the user picked Pasted by here. A system with
    /// nobody of ours in it moves nothing.
    /// </summary>
    private void FollowSystem()
    {
        if (_characterChosen) return;
        var here = _snapshot.Own.Where(o => o.SystemId == _systemId).Select(o => o.CharacterId).ToList();
        if (here.Count == 0 || (_selectedCharacter is { } held && here.Contains(held.Value))) return;
        if (CharacterChoices.FirstOrDefault(c => c.Value == here[0]) is { } who) SelectedCharacter = who;
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
        _retry.Stop();
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

    /// <summary>
    /// The character pasting when the user has not picked one: the "Distance from" character when online,
    /// else the first online character in the system chosen, else the first online character (whose system
    /// is then the default), else the first there is. ⚠️ Never a logged-off character while one is online:
    /// it was the default before, alphabetically first, beside the system of another character.
    /// </summary>
    private IntelChoice<long>? Default()
    {
        var from = _tool.FromCharacterId;
        var online = _snapshot.Own.Select(o => o.CharacterId).ToList();
        IntelChoice<long>? Choice(long id) => CharacterChoices.FirstOrDefault(c => c.Value == id);
        return (from != 0 && online.Contains(from) ? Choice(from) : null)
               ?? _snapshot.Own.Where(o => o.SystemId == _systemId && _systemId > 0).Select(o => Choice(o.CharacterId)).FirstOrDefault(c => c is not null)
               ?? _snapshot.Own.Select(o => Choice(o.CharacterId)).FirstOrDefault(c => c is not null)
               ?? (from != 0 ? Choice(from) : null)
               ?? CharacterChoices.FirstOrDefault();
    }

    private bool IsOnline(long characterId) => _snapshot.Own.Any(o => o.CharacterId == characterId);

    /// <summary>"Distance from" changed: the character pasting follows it, unless one was picked here.</summary>
    internal void FromChanged()
    {
        if (_characterChosen || Default() is not { } d) return;
        // Nothing pasted yet: the system follows too, to where that character is.
        if (_preview is null && _text.Length == 0)
        {
            SelectedCharacter = d;
            if (_snapshot.Own.FirstOrDefault(o => o.CharacterId == d.Value) is { } own) ChooseSystem(own.SystemId);
        }
        // A paste in the box stays the Local of the system chosen: it moves only when none of ours is there.
        else if (_snapshot.Own.All(o => o.SystemId != _systemId))
            SelectedCharacter = d;
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
        var repicked = false;
        if (_selectedSystem is null || SystemChoices.All(c => !ReferenceEquals(c, _selectedSystem)))
        {
            repicked = true;
            // The system of the character pasting: the one picked, else the default (the first online).
            var who = _characterChosen ? _selectedCharacter?.Value : Default()?.Value;
            var mine = snapshot.Own.FirstOrDefault(o => o.CharacterId == who)?.SystemId;
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
        // Not picked here: the character in the system chosen, and never a logged-off one while one is online.
        if (!_characterChosen)
        {
            if (repicked) FollowSystem();
            if ((_selectedCharacter is null || (!IsOnline(_selectedCharacter.Value) && snapshot.Own.Count > 0))
                && Default() is { } d && !ReferenceEquals(d, _selectedCharacter))
                SelectedCharacter = d;
        }

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
        _retry.Stop();
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
        var keep = _keep.ToHashSet();
        IsBusy = true;
        try
        {
            var preview = await Task.Run(() => _service.PreviewAsync(text, viewer, system, snapshot, now, keep));
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

    /// <summary>Shows a preview given rather than worked out: for a check. It is shown, and saved, with the
    /// tiers the chips keep now.</summary>
    public void Show(IntelPastePreview? preview)
    {
        var culture = CultureInfo.CurrentCulture;
        if (preview is not null && !preview.Keep.SetEquals(_keep)) preview = preview with { Keep = _keep.ToHashSet() };
        _preview = preview;
        var local = preview is { Kind: IntelPasteKind.Local };
        var dscan = preview is { Kind: IntelPasteKind.Dscan };
        IsLocal = local;
        IsDscan = dscan;

        if (preview is null)
        {
            CountsText = "";
            PlacesText = ReplacesText = UnknownText = OwnText = LeftOutText = BlockText = "";
            IsClearPaste = false;
            Groups = [];
            DscanShipsText = DscanFlagsText = "";
            DscanHulls = [];
        }
        else if (local)
        {
            CountsText = Plural(nameof(IntelText.PasteNamesCountOther), preview.Names.Count, preview.Pilots.Count, preview.Unresolved.Count);
            var system = SystemNameOf(preview.SystemId, preview.SystemName);
            IsClearPaste = preview.IsClear && preview.SystemId > 0;
            // Why it is not saved, when it is not: then no headline, and nothing said of what it would replace.
            BlockText = preview.Block switch
            {
                IntelPasteBlock.NoTiers => IntelText.PasteBlockedNoTiers,
                IntelPasteBlock.Lookup  => string.Format(culture, IntelText.PasteBlockedLookup, preview.Unchecked.Count),
                IntelPasteBlock.Partial => string.Format(culture, IntelText.PasteBlockedPartial, preview.HostileLeftOut,
                    string.Join(", ", preview.LeftOutByTier.Where(t => StandingTiers.IsHostile(t.Tier)).Select(t => IntelWords.TierName(t.Tier))), system),
                _ => "",
            };
            // Only what is saved: the pilots of the kept tiers and the unnamed, as Save counts them. Nobody kept is a clear.
            PlacesText = preview.SystemId <= 0 ? IntelText.PasteChooseSystem
                       : preview.Block != IntelPasteBlock.None ? ""
                       : preview.IsClear ? string.Format(culture, IntelText.PasteSavesClear, system)
                       : Plural(nameof(IntelText.PastePlacesOther), preview.PlayerCount, system);
            ShowReplaces(preview.Replaces);
            UnknownText = preview.KeptUnresolved.Count > 0 ? Plural(nameof(IntelText.PasteUnknownNamesOther), preview.KeptUnresolved.Count) : "";
            OwnText = preview.Own > 0 ? string.Format(culture, IntelText.PasteOwnNotSaved, preview.Own) : "";
            var outTiers = preview.LeftOutByTier.Select(t => IntelWords.TierName(t.Tier)).ToList();
            if (preview.Own > 0) outTiers.Add(IntelText.PasteGroupYours);
            LeftOutText = preview.LeftOut > 0 ? string.Format(culture, IntelText.PasteLeftOut, preview.LeftOut, string.Join(", ", outTiers)) : "";
            // The kept tiers in full; each tier left out folded to one dimmed line with its count, the user's own last.
            // Names not looked up always show, whatever the chips: they are what holds Save back.
            var groups = preview.Groups.Where(g => g.Kind == IntelPasteGroupKind.Unchecked || (g.Kind != IntelPasteGroupKind.Own && preview.Keeps(g.Tier)))
                .Select(g => new IntelPasteGroupVm(g)).ToList();
            groups.AddRange(preview.LeftOutByTier.Select(t => IntelPasteGroupVm.LeftOutLine(t.Tier, t.Count)));
            if (preview.Own > 0) groups.Add(IntelPasteGroupVm.LeftOutLine(StandingTier.Own, preview.Own));
            Groups = groups;
            DscanShipsText = DscanFlagsText = "";
            DscanHulls = [];
        }
        else
        {
            var d = preview.Dscan;
            CountsText = Plural(nameof(IntelText.PasteDscanCountOther), d.Ships, d.Unknown);
            PlacesText = preview.SystemId > 0 ? "" : IntelText.PasteChooseSystem;
            ReplacesText = UnknownText = OwnText = LeftOutText = BlockText = "";
            IsClearPaste = false;
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
        _retry.Stop();
        if (preview is { Block: IntelPasteBlock.Lookup } && IsCurrent(preview) && _service is not null) _retry.Start();
        this.RaisePropertyChanged(nameof(HasPreview));
        this.RaisePropertyChanged(nameof(HasBlock));
        this.RaisePropertyChanged(nameof(HasUnknown));
        this.RaisePropertyChanged(nameof(HasOwn));
        this.RaisePropertyChanged(nameof(HasLeftOut));
        this.RaisePropertyChanged(nameof(HasDscanFlags));
    }

    /// <summary>The preview on screen, as Save would store it: for a check.</summary>
    public IntelPastePreview? Shown => _preview;

    /// <summary>A counted phrase in the form the interface language needs for the number.</summary>
    private static string Plural(string otherKey, long n, params object?[] more) =>
        Plurals.Format(IntelText.ResourceManager, otherKey, n, more);

    /// <summary>"Replaces N", only while Save would: a Local held back replaces nothing.</summary>
    private void ShowReplaces(int replaces) =>
        ReplacesText = _preview is { Block: not IntelPasteBlock.None } ? ""
                     : string.Format(CultureInfo.CurrentCulture, IntelText.PasteReplaces, replaces);

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

    private string _block = "";
    /// <summary>Why Save is off for this Local: every chip off, names not looked up yet, or someone who may be hostile
    /// left out of what is a replace.</summary>
    public string BlockText { get => _block; private set => this.RaiseAndSetIfChanged(ref _block, value); }
    public bool HasBlock => BlockText.Length > 0;

    private string _leftOut = "";
    /// <summary>"89 left out (Blue, Light blue)": everyone on the Local that Save does not store.</summary>
    public string LeftOutText { get => _leftOut; private set => this.RaiseAndSetIfChanged(ref _leftOut, value); }
    public bool HasLeftOut => LeftOutText.Length > 0;

    private bool _isClearPaste;
    /// <summary>A Local with nobody of the kept tiers on it: saved as a clear of the system.</summary>
    public bool IsClearPaste { get => _isClearPaste; private set => this.RaiseAndSetIfChanged(ref _isClearPaste, value); }

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
        var system = SystemNameOf(preview.SystemId, preview.SystemName);
        var status = preview.IsClear ? string.Format(CultureInfo.CurrentCulture, IntelText.PasteSavedClear, system)
                   : preview.Kind == IntelPasteKind.Local ? Plural(nameof(IntelText.PasteSavedLocalOther), preview.PlayerCount, system)
                   : Plural(nameof(IntelText.PasteSavedDscanOther), preview.Dscan.Ships, system);
        ClearBox();
        StatusText = status;
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
            IntelPasteGroupKind.Unchecked => IntelText.PasteGroupUnchecked,
            IntelPasteGroupKind.NoAlliance => IntelText.PasteGroupNoAlliance,
            _ => group.Name.Length > 0 ? group.Name : string.Format(culture, MapText.AllianceNumbered, group.AllianceId),
        };
        TierName = group.Kind switch
        {
            IntelPasteGroupKind.Unresolved => IntelText.PasteTierUnnamed,
            IntelPasteGroupKind.Unchecked => IntelText.PasteTierUnchecked,
            _ => IntelWords.TierName(group.Tier),
        };
        CountText = group.Count.ToString(culture);
        Corps = [.. group.Corps.Select(c => new IntelPasteCorpVm(c, group.Kind))];
    }

    private IntelPasteGroupVm(StandingTier tier, int count)
    {
        TierBrush = IntelTierColors.Of(tier);
        Name = tier == StandingTier.Own ? IntelText.PasteGroupYours : IntelWords.TierName(tier);
        TierName = tier == StandingTier.Own ? IntelText.PasteNeverSaved : IntelText.PasteNotSaved;
        CountText = count.ToString(CultureInfo.CurrentCulture);
        Corps = [];
        IsLeftOut = true;
    }

    /// <summary>A tier the chips leave out (or the user's own characters), folded to one dimmed line: who it is
    /// and how many, not saved.</summary>
    public static IntelPasteGroupVm LeftOutLine(StandingTier tier, int count) => new(tier, count);

    public IBrush TierBrush { get; }
    public string Name { get; }
    public string TierName { get; }
    public string CountText { get; }
    public IReadOnlyList<IntelPasteCorpVm> Corps { get; }

    /// <summary>Not saved: drawn dimmed, with no pilots under it.</summary>
    public bool IsLeftOut { get; }
    public double RowOpacity => IsLeftOut ? 0.5 : 1;

    public Task LoadPicturesAsync() => Task.WhenAll(Corps.SelectMany(c => c.Pilots).Select(p => p.LoadAsync()));
}

/// <summary>The pilots of one corporation in a pasted Local.</summary>
public sealed class IntelPasteCorpVm
{
    public IntelPasteCorpVm(IntelPasteCorp corp, IntelPasteGroupKind kind)
    {
        Name = kind is IntelPasteGroupKind.Unresolved or IntelPasteGroupKind.Unchecked ? ""
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
        // A Local with nobody of the kept standings on it was saved as a clear.
        CountText = paste.IsClear ? IntelText.KindClear
            : Plurals.Format(IntelText.ResourceManager,
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
