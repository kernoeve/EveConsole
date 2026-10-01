using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Pi;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// The Planetary Industry tool: every colony of every character that does PI, one colony in
/// detail, and the characters' colony slots and command-center headroom.
///
/// <para>Everything comes from <see cref="PiService"/>, which works on stored layouts: the poll
/// brings new ones every ten minutes, and this re-reads every minute while the tool is open so the
/// countdowns move. ⚠️ Extractor times are exact; storage and input times are replayed from the
/// last time each colony was opened in game, so every one of them is labelled an estimate and the
/// data's age is always on screen.</para>
/// </summary>
public sealed class PlanetaryIndustryViewModel : ReactiveObject, IPeriodicRefresh
{
    /// <summary>Set the first time this tool is opened; until then its refresh timer is a no-op.
    /// See IPeriodicRefresh.</summary>
    public bool AutoRefreshEnabled { get; set; }

    public const int TabColonies   = 0;
    public const int TabColony     = 1;
    public const int TabCharacters = 2;

    private readonly PiService      _pi;
    private readonly AppErrorLogger _errorLogger;
    private bool _loading;

    // What the last load read, kept for building a colony's detail without another read.
    private Dictionary<int, string> _typeNames = [];
    private PiStaticData            _static    = new();

    // A colony asked for before the list had it (an Overview alert, opened as the tool opens).
    private (long CharacterId, int PlanetId)? _pending;

    public BulkObservableCollection<PiColonyRowVm>    Colonies   { get; } = [];
    public BulkObservableCollection<PiCharacterRowVm> Characters { get; } = [];

    private PiColonyRowVm? _selected;
    public PiColonyRowVm? Selected
    {
        get => _selected;
        set { this.RaiseAndSetIfChanged(ref _selected, value); this.RaisePropertyChanged(nameof(HasSelection)); }
    }
    public bool HasSelection => _selected is not null;

    private PiColonyDetailVm? _detail;
    public PiColonyDetailVm? Detail
    {
        get => _detail;
        private set { this.RaiseAndSetIfChanged(ref _detail, value); this.RaisePropertyChanged(nameof(HasDetail)); this.RaisePropertyChanged(nameof(NoDetail)); }
    }
    public bool HasDetail => _detail is not null;
    public bool NoDetail  => _detail is null;

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }

    private string _summary = "";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    private string _status = CommonText.Loading;
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isEmpty;
    /// <summary>No colony with a layout read: the list says why rather than sitting blank.</summary>
    public bool IsEmpty { get => _isEmpty; private set => this.RaiseAndSetIfChanged(ref _isEmpty, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand    { get; }
    public ReactiveCommand<Unit, Unit> OpenColonyCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

    /// <summary>Settings → Characters, where the PI switch is. Wired by the main window.</summary>
    public Action? OpenPiSettingsRequested { get; set; }

    public PlanetaryIndustryViewModel(PiService pi, AppErrorLogger errorLogger)
    {
        _pi          = pi;
        _errorLogger = errorLogger;

        RefreshCommand      = ReactiveCommand.CreateFromTask(RefreshAsync);
        OpenColonyCommand   = ReactiveCommand.Create(OpenSelected, this.WhenAnyValue(x => x.HasSelection));
        OpenSettingsCommand = ReactiveCommand.Create(() => OpenPiSettingsRequested?.Invoke());

        // The countdowns move by the minute, and the poll can bring a new layout any ten minutes.
        Observable.Interval(TimeSpan.FromMinutes(1))
            .Where(_ => AutoRefreshEnabled)
            .ObserveOnUi("PlanetaryIndustry.AutoRefresh")
            .SubscribeAsyncSafe(_ => RefreshAsync(), errorLogger, "PlanetaryIndustry.AutoRefresh");

        // Names arrive in the interface language once they are loaded; until then a list built at
        // start reads English.
        SdeNames.Changed += () => Dispatcher.UIThread.Post(() => { if (AutoRefreshEnabled) _ = RefreshAsync(); });
    }

    /// <summary>Opens the selected colony's detail — a double-click on its row, or the button.</summary>
    public void OpenSelected()
    {
        if (_selected is null) return;
        Detail = BuildDetail(_selected);
        SelectedTabIndex = TabColony;
    }

    /// <summary>
    /// The tool on one colony, or on its list when <paramref name="characterId"/> is null — what an
    /// Overview alert does. Applied now if the list has it, otherwise when the next load does.
    /// </summary>
    public void ShowColony(long? characterId, int? planetId)
    {
        if (characterId is not { } ch || planetId is not { } planet)
        {
            SelectedTabIndex = TabColonies;
            _ = RefreshAsync();
            return;
        }
        var row = Colonies.FirstOrDefault(r => r.CharacterId == ch && r.PlanetId == planet);
        if (row is not null) { Selected = row; OpenSelected(); }
        else _pending = (ch, planet);
        _ = RefreshAsync();
    }

    public void ShowCharacters()
    {
        SelectedTabIndex = TabCharacters;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            // ⚠️ All of it off the UI thread: layouts, a simulation per colony, prices and names.
            var (colonies, characters, sd, names) = await Task.Run(async () =>
            {
                var c  = await _pi.ColoniesAsync(now).ConfigureAwait(false);
                var ch = await _pi.CharactersAsync().ConfigureAwait(false);
                var s  = await _pi.StaticDataAsync().ConfigureAwait(false);
                var typeIds = c.SelectMany(x => TypesOf(x.Forecast)).ToHashSet();
                var n  = await _pi.TypeNamesAsync(typeIds).ConfigureAwait(false);
                return (c, ch, s, n);
            });

            _typeNames = names;
            _static    = sd;
            var t = _pi.Settings.Thresholds;

            var rows = colonies
                .Select(c => new PiColonyRowVm(c, PiColonyAttention.For(c.Forecast, t)))
                // Soonest action first: what has already happened, then what happens next; a colony
                // with nothing coming due within the horizon last.
                .OrderBy(r => r.NextActionSort)
                .ThenBy(r => r.CharacterName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r.PlanetName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // Asked for once: a colony this reading does not have is not chased on every refresh.
            var pending = _pending;
            _pending = null;
            var keep = pending ?? (_selected is { } sel ? (sel.CharacterId, sel.PlanetId) : null);
            Colonies.ResetTo(rows);
            Characters.ResetTo(characters.Select(c => new PiCharacterRowVm(c)).ToList());

            PiColonyRowVm? Find(long ch, int planet) => rows.FirstOrDefault(r => r.CharacterId == ch && r.PlanetId == planet);

            Selected = keep is { } k ? Find(k.CharacterId, k.PlanetId) : null;
            if (pending is not null && _selected is not null) OpenSelected();
            // The colony on the Colony tab, rebuilt from this reading so its figures move too —
            // or gone, when the colony has gone (given up, or its character's PI box cleared).
            else if (_detail is { } shown)
                Detail = Find(shown.CharacterId, shown.PlanetId) is { } same ? BuildDetail(same) : null;

            Summary = SummaryOf(rows);
            IsEmpty = rows.Count == 0;
            Status  = string.Format(PiText.Updated, DateTimeOffset.Now.ToString(CommonText.DateMonthDayTime));

            foreach (var r in rows) _ = r.LoadPortraitAsync();
            foreach (var c in Characters) _ = c.LoadPortraitAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("PlanetaryIndustryViewModel", "Refresh", ex);
            Status = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
        finally { _loading = false; }
    }

    /// <summary>Every type a colony's screens name: products, contents, flows, pins.</summary>
    private static IEnumerable<int> TypesOf(PiColonyForecast f)
        => f.Flows.Select(x => x.TypeId)
            .Concat(f.Extractors.Where(x => x.ProductTypeId is not null).Select(x => x.ProductTypeId!.Value))
            .Concat(f.Storage.SelectMany(s => s.ContentsAt.Keys.Append(s.TypeId)))
            .Concat(f.Factories.Select(p => p.TypeId))
            .Concat(f.Factories.Where(p => p.OutputTypeId is not null).Select(p => p.OutputTypeId!.Value));

    /// <summary>"22 colonies · 3 extractors stopped · 2 need hauling · 1 inputs low".</summary>
    private static string SummaryOf(IReadOnlyList<PiColonyRowVm> rows)
    {
        var stopped = rows.Sum(r => r.Colony.Forecast.Extractors.Count(x => x.IsExpired));
        var haul    = rows.Count(r => r.Attention.StorageFull || r.Attention.StorageFilling);
        var inputs  = rows.Count(r => r.Colony.Forecast.Kind == PiColonyKind.Factory && (r.Attention.InputsOut || r.Attention.InputsLow));
        string C(string family, long n) => Plurals.Format(PiText.ResourceManager, family, n);
        return string.Join(PiText.SummarySeparator,
            C(nameof(PiText.SummaryColoniesOther), rows.Count),
            C(nameof(PiText.SummaryStoppedOther), stopped),
            C(nameof(PiText.SummaryHaulingOther), haul),
            C(nameof(PiText.SummaryInputsOther), inputs));
    }

    private PiColonyDetailVm BuildDetail(PiColonyRowVm row) => new(row, _static, _typeNames);
}

// ── Colonies tab ────────────────────────────────────────────────────────────────────────────

/// <summary>One colony on the Colonies tab.</summary>
public sealed class PiColonyRowVm : ReactiveObject
{
    public PiColonyStatus    Colony    { get; }
    public PiColonyAttention Attention { get; }

    public long   CharacterId   => Colony.CharacterId;
    public int    PlanetId      => Colony.PlanetId;
    public string CharacterName => Colony.CharacterName;
    public string PlanetName    { get; }
    public string SystemName    { get; }
    public string RegionName    { get; }
    public string SecurityText  { get; }
    public string SecurityColor { get; }
    public string SecurityTip   { get; }
    public string PlanetType    { get; }
    public string KindText      { get; }

    private Bitmap? _planetIcon;
    /// <summary>The planet type's icon from EVE's image server, beside the planet's name.</summary>
    public Bitmap? PlanetIcon { get => _planetIcon; private set => this.RaiseAndSetIfChanged(ref _planetIcon, value); }
    public string CcLevelText   { get; }
    public string CcLevelTip    { get; }
    public IBrush CcLevelColor  { get; }
    public int    CcLevel       => Colony.Forecast.Layout.UpgradeLevel;

    public string ExtractorsText { get; }
    public string ExtractorsWhen { get; }
    public string ExtractorsTip  { get; }
    public IBrush ExtractorsColor { get; }
    public DateTimeOffset ExtractorsSort { get; }

    public string StorageText  { get; }
    public string StorageWhen  { get; }
    public string StorageTip   { get; }
    public IBrush StorageColor { get; }
    public DateTimeOffset StorageSort { get; }

    public string InputsText  { get; }
    public string InputsWhen  { get; }
    public string InputsTip   { get; }
    public IBrush InputsColor { get; }
    public DateTimeOffset InputsSort { get; }

    public string StateText    { get; }
    public string StateGlyph   { get; }
    public string StateTip     { get; }
    public IBrush StateColor   { get; }
    public IBrush StateSurface { get; }
    public int    StateSort    => Attention.State switch { PiColonyState.Action => 0, PiColonyState.Attention => 1, _ => 2 };

    public string AgeText  { get; }
    public string AgeTip   { get; }
    public IBrush AgeColor { get; }
    public double AgeHours => Attention.DataAge.TotalHours;

    public string ProfitText   { get; }
    public string ProfitTip    { get; }
    public double ProfitPerDay => Colony.Economics.ProfitPerDay;

    public DateTimeOffset NextActionSort => Attention.NextActionAt ?? DateTimeOffset.MaxValue;

    private Bitmap? _portrait;
    public Bitmap? Portrait { get => _portrait; private set => this.RaiseAndSetIfChanged(ref _portrait, value); }

    public PiColonyRowVm(PiColonyStatus c, PiColonyAttention a)
    {
        Colony    = c;
        Attention = a;
        var f   = c.Forecast;
        var now = a.Now;

        PlanetName    = PiNames.Planet(c);
        SystemName    = PiNames.System(c.SolarSystemId, c.SystemName);
        RegionName    = c.RegionId > 0 ? SdeNames.Region(c.RegionId, c.RegionName) : c.RegionName;
        SecurityText  = SecurityColors.Text(c.Security);
        SecurityColor = SecurityColors.Hex(c.Security);
        SecurityTip   = SecurityColors.Tip(c.Security);
        PlanetType    = PiNames.PlanetType(c.PlanetTypeId, c.PlanetTypeName, f.Layout.PlanetType);
        KindText      = KindName(f.Kind);

        var level = f.Layout.UpgradeLevel;
        CcLevelText  = string.Format(PiText.LevelOfMax, level, c.MaxUpgradeLevel);
        CcLevelTip   = level < c.MaxUpgradeLevel
            ? string.Format(PiText.TipCcHeadroom, level, c.MaxUpgradeLevel)
            : string.Format(PiText.TipCcAtMax, level);
        CcLevelColor = level < c.MaxUpgradeLevel ? Palette.Info : Palette.TextSecondary;

        // Extractors: exact.
        if (a.ExtractorsStopAt is { } stop)
        {
            ExtractorsText  = a.ExtractorsStopped ? PiText.Stopped : PiFormat.Relative(stop, now);
            ExtractorsWhen  = PiFormat.When(stop);
            ExtractorsTip   = a.ExtractorsStopped
                ? string.Format(PiText.TipExtractorsStopped, PiFormat.Duration(now - stop), PiFormat.When(stop))
                : string.Format(PiText.TipExtractorsStop, PiFormat.When(stop));
            ExtractorsColor = a.ExtractorsStopped ? Palette.Bad : a.ExtractorsStopping ? Palette.Warn : Palette.TextPrimary;
            ExtractorsSort  = stop;
        }
        else
        {
            ExtractorsText = ""; ExtractorsWhen = ""; ExtractorsTip = PiText.TipNoExtractors;
            ExtractorsColor = Palette.TextFaint; ExtractorsSort = DateTimeOffset.MaxValue;
        }

        // Storage: estimated.
        if (a.StorageFullAt is { } full)
        {
            StorageText  = a.StorageFull ? PiText.Full : PiFormat.Relative(full, now);
            StorageWhen  = PiFormat.When(full);
            StorageTip   = a.StorageFull
                ? string.Format(PiText.TipStorageFull, PiFormat.When(full))
                : string.Format(PiText.TipStorageFills, PiFormat.When(full));
            StorageColor = a.StorageFull ? Palette.Bad : a.StorageFilling ? Palette.Warn : Palette.TextPrimary;
            StorageSort  = full;
        }
        else
        {
            StorageText = PiText.NotWithinHorizon; StorageWhen = ""; StorageTip = PiText.TipStorageNotFull;
            StorageColor = Palette.TextFaint; StorageSort = DateTimeOffset.MaxValue;
        }

        // Inputs: factory planets, estimated.
        if (f.Kind == PiColonyKind.Factory && a.InputsRunOutAt is { } empty)
        {
            InputsText  = a.InputsOut ? PiText.RanOut : PiFormat.Relative(empty, now);
            InputsWhen  = PiFormat.When(empty);
            InputsTip   = a.InputsOut
                ? string.Format(PiText.TipInputsOut, PiFormat.When(empty))
                : string.Format(PiText.TipInputsRunOut, PiFormat.When(empty));
            InputsColor = a.InputsOut ? Palette.Bad : a.InputsLow ? Palette.Warn : Palette.TextPrimary;
            InputsSort  = empty;
        }
        else
        {
            InputsText  = f.Kind == PiColonyKind.Factory ? PiText.NotWithinHorizon : "";
            InputsWhen  = "";
            InputsTip   = f.Kind == PiColonyKind.Factory ? PiText.TipInputsNotOut : PiText.TipNoInputs;
            InputsColor = Palette.TextFaint;
            InputsSort  = DateTimeOffset.MaxValue;
        }

        // The chip: a word and a mark, so the state reads without the colour.
        (StateText, StateGlyph, StateColor, StateSurface) = a.State switch
        {
            PiColonyState.Action    => (PiText.StateAction,    "■", Palette.Bad,  Palette.BadSurface),
            PiColonyState.Attention => (PiText.StateAttention, "▲", Palette.Warn, Palette.WarnSurface),
            _                       => (PiText.StateOk,        "●", Palette.Good, Palette.GoodSurface),
        };
        StateTip = StateReasons(a);

        AgeText  = a.Stale ? string.Format(PiText.AgeStale, PiFormat.Duration(a.DataAge)) : PiFormat.Duration(a.DataAge);
        AgeTip   = string.Format(a.Stale ? PiText.TipAgeStale : PiText.TipAge, PiFormat.When(f.SnapshotAt));
        AgeColor = a.Stale ? Palette.Warn : Palette.TextSecondary;

        ProfitText = string.Format(PiText.PerDay, MarketFmt.Isk(c.Economics.ProfitPerDay));
        ProfitTip  = string.Format(PiText.TipProfit, MarketFmt.Isk(c.Economics.OutputValuePerDay),
                                   MarketFmt.Isk(c.Economics.InputCostPerDay),
                                   MarketFmt.Isk(c.Economics.ExportChargesPerDay + c.Economics.ImportChargesPerDay));
    }

    public static string KindName(PiColonyKind kind) => kind switch
    {
        PiColonyKind.Extractor => PiText.KindExtractor,
        PiColonyKind.Factory   => PiText.KindFactory,
        _                      => PiText.KindEmpty,
    };

    /// <summary>Every reason the chip says what it says, one per line.</summary>
    private static string StateReasons(PiColonyAttention a)
    {
        var why = new List<string>();
        if (a.ExtractorsStopped)  why.Add(PiText.ReasonExtractorsStopped);
        if (a.ExtractorsStopping) why.Add(PiText.ReasonExtractorsStopping);
        if (a.StorageFull)        why.Add(PiText.ReasonStorageFull);
        if (a.StorageFilling)     why.Add(PiText.ReasonStorageFilling);
        if (a.InputsOut)          why.Add(PiText.ReasonInputsOut);
        if (a.InputsLow)          why.Add(PiText.ReasonInputsLow);
        if (a.Stale)              why.Add(PiText.ReasonStale);
        return why.Count == 0 ? PiText.ReasonNothingDue : string.Join("\n", why);
    }

    public async Task LoadPortraitAsync()
    {
        var bmp = await EveImageCache.GetAsync($"https://images.evetech.net/characters/{CharacterId}/portrait?size=32");
        if (bmp is not null) Dispatcher.UIThread.Post(() => Portrait = bmp);

        // The planet's own type (wormhole and "scorched" variants included), so the icon is the
        // one the game shows for this planet.
        if (Colony.PlanetTypeId > 0
            && await EveImageCache.GetAsync($"https://images.evetech.net/types/{Colony.PlanetTypeId}/icon?size=32") is { } icon)
            Dispatcher.UIThread.Post(() => PlanetIcon = icon);
    }
}

// ── Characters tab ──────────────────────────────────────────────────────────────────────────

/// <summary>One PI character: colony slots used against allowed, and each colony's command center
/// against what Command Center Upgrades allows.</summary>
public sealed class PiCharacterRowVm : ReactiveObject
{
    public long   CharacterId { get; }
    public string Name        { get; }
    public string ColoniesText { get; }
    public int    ColoniesUsed { get; }
    public int    FreeSlots   { get; }
    public string FreeText    { get; }
    public IBrush FreeColor   { get; }
    public string SkillsText  { get; }
    public string SkillsTip   { get; }
    public int    Headroom    { get; }
    public IReadOnlyList<PiSlotVm> Slots { get; }

    /// <summary>For copying a row: the colonies written out.</summary>
    public string SlotsText => string.Join(CommonText.ListSeparator, Slots.Select(s => s.Text));

    private Bitmap? _portrait;
    public Bitmap? Portrait { get => _portrait; private set => this.RaiseAndSetIfChanged(ref _portrait, value); }

    public PiCharacterRowVm(PiCharacterStatus c)
    {
        CharacterId  = c.CharacterId;
        Name         = c.Name;
        ColoniesUsed = c.ColoniesUsed;
        FreeSlots    = c.ColoniesFree;
        ColoniesText = string.Format(PiText.ColoniesOfAllowed, c.ColoniesUsed, c.ColoniesAllowed);
        FreeText     = c.ColoniesFree > 0
            ? Plurals.Format(PiText.ResourceManager, nameof(PiText.FreeSlotsOther), c.ColoniesFree)
            : PiText.NoFreeSlots;
        FreeColor    = c.ColoniesFree > 0 ? Palette.Warn : Palette.TextFaint;
        SkillsText   = string.Format(PiText.SkillsShort, c.Skills.InterplanetaryConsolidation, c.Skills.CommandCenterUpgrades);
        SkillsTip    = PiText.TipSkills;
        Slots        = c.Colonies.Select(s => new PiSlotVm(s)).ToList();
        Headroom     = c.Colonies.Sum(s => s.UpgradeHeadroom);
    }

    public async Task LoadPortraitAsync()
    {
        var bmp = await EveImageCache.GetAsync($"https://images.evetech.net/characters/{CharacterId}/portrait?size=32");
        if (bmp is not null) Dispatcher.UIThread.Post(() => Portrait = bmp);
    }
}

/// <summary>One colony slot in use: its planet and command center level against the most the
/// skill allows.</summary>
public sealed class PiSlotVm
{
    public string Text  { get; }
    public string Tip   { get; }
    public IBrush Color { get; }

    public PiSlotVm(PiColonySlot s)
    {
        var planet = PiNames.Planet(s.PlanetId, s.PlanetName, s.SolarSystemId, s.SystemName);
        Text  = s.UpgradeHeadroom > 0
            ? string.Format(PiText.SlotWithHeadroom, planet, s.UpgradeLevel, s.MaxUpgradeLevel)
            : string.Format(PiText.SlotAtMax, planet, s.UpgradeLevel, s.MaxUpgradeLevel);
        Tip   = s.UpgradeHeadroom > 0
            ? string.Format(PiText.TipCcHeadroom, s.UpgradeLevel, s.MaxUpgradeLevel)
            : string.Format(PiText.TipCcAtMax, s.UpgradeLevel);
        Color = s.UpgradeHeadroom > 0 ? Palette.Info : Palette.TextSecondary;
    }
}

// ── Colony tab ──────────────────────────────────────────────────────────────────────────────

/// <summary>One colony in full: its pins, flows and money.</summary>
public sealed class PiColonyDetailVm
{
    public long   CharacterId { get; }
    public int    PlanetId    { get; }
    public string Title       { get; }
    public string Subtitle    { get; }
    public string AgeText     { get; }
    public IBrush AgeColor    { get; }
    public string CommandCenterText { get; }

    public IReadOnlyList<PiExtractorVm>      Extractors { get; }
    public IReadOnlyList<PiProcessorRowVm>   Processors { get; }
    public IReadOnlyList<PiStorageRowVm>     Storage    { get; }
    public IReadOnlyList<PiFlowRowVm>        Flows      { get; }
    public bool HasExtractors => Extractors.Count > 0;
    public bool HasProcessors => Processors.Count > 0;

    public string OutputValueText   { get; }
    public string InputCostText     { get; }
    public string ExportChargesText { get; }
    public string ImportChargesText { get; }
    public string ProfitText        { get; }
    public IBrush ProfitColor       { get; }
    public string RateText          { get; }

    public PiColonyDetailVm(PiColonyRowVm row, PiStaticData sd, IReadOnlyDictionary<int, string> names)
    {
        var c = row.Colony;
        var f = c.Forecast;
        var now = row.Attention.Now;
        string Name(int typeId) => PiNames.Type(typeId, names.GetValueOrDefault(typeId));

        CharacterId = c.CharacterId;
        PlanetId    = c.PlanetId;
        Title       = row.PlanetName;
        Subtitle    = string.Format(PiText.DetailSubtitle, c.CharacterName, row.SystemName, row.SecurityText,
                                    row.PlanetType, row.KindText);
        AgeText     = string.Format(row.Attention.Stale ? PiText.DetailAgeStale : PiText.DetailAge,
                                    PiFormat.Duration(row.Attention.DataAge), PiFormat.When(f.SnapshotAt));
        AgeColor    = row.AgeColor;

        var (cpu, power) = PiCommandCenter.At(f.Layout.UpgradeLevel);
        CommandCenterText = string.Format(PiText.DetailCommandCenter, f.Layout.UpgradeLevel, c.MaxUpgradeLevel, cpu, power);

        Extractors = f.Extractors.Select(x => new PiExtractorVm(x, now, Name)).ToList();

        Processors = f.Factories
            .Select(p => new PiProcessorRowVm(p, sd, Name))
            .OrderBy(p => p.Tier).ThenBy(p => p.SchematicName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Storage = f.Storage.Select(s => new PiStorageRowVm(s, now, Name)).ToList();

        Flows = f.Flows
            .Select(x => new PiFlowRowVm(x, Name(x.TypeId)))
            .OrderBy(x => x.TierSort).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var e = c.Economics;
        OutputValueText   = string.Format(PiText.PerDay, MarketFmt.Isk(e.OutputValuePerDay));
        InputCostText     = string.Format(PiText.PerDay, MarketFmt.Isk(e.InputCostPerDay));
        ExportChargesText = string.Format(PiText.PerDay, MarketFmt.Isk(e.ExportChargesPerDay));
        ImportChargesText = string.Format(PiText.PerDay, MarketFmt.Isk(e.ImportChargesPerDay));
        ProfitText        = string.Format(PiText.PerDay, MarketFmt.Isk(e.ProfitPerDay));
        ProfitColor       = e.ProfitPerDay >= 0 ? Palette.Good : Palette.Bad;

        RateText = f.Rate is not { } r ? PiText.RateUnknown
            : r.Learned
                ? string.Format(PiText.RateLearned, (r.Rate * 100).ToString("0.##"), CollectorName(r.Kind),
                                r.LearnedAt is { } at ? PiFormat.When(at) : "")
                : string.Format(PiText.RateDefault, (r.Rate * 100).ToString("0.##"), CollectorName(r.Kind));
    }

    private static string CollectorName(PiChargeKind kind)
        => kind == PiChargeKind.Skyhook ? PiText.Skyhook : PiText.CustomsOffice;
}

/// <summary>One extractor: its program, and a bar per cycle with the current one marked.</summary>
public sealed class PiExtractorVm
{
    public string ProductName { get; }
    public string HeadsText   { get; }
    public string ProgramText { get; }
    public string CyclesText  { get; }
    public string OutputText  { get; }
    public string StateText   { get; }
    public IBrush StateColor  { get; }
    public bool   HasChart    { get; }

    public ISeries[] Series { get; }
    public Axis[]    XAxes  { get; }
    public Axis[]    YAxes  { get; }

    public PiExtractorVm(PiExtractorProgram x, DateTimeOffset now, Func<int, string> name)
    {
        ProductName = x.ProductTypeId is { } p ? name(p) : PiText.NoProduct;
        HeadsText   = Plurals.Format(PiText.ResourceManager, nameof(PiText.HeadsOther), x.HeadCount);
        ProgramText = x.InstallTime is { } start && x.ExpiryTime is { } end
            ? string.Format(PiText.ProgramFromTo, PiFormat.When(start), PiFormat.When(end))
            : PiText.NoProgram;
        CyclesText  = string.Format(PiText.CyclesDoneOfTotal, x.CyclesDone, x.TotalCycles,
                                    PiFormat.Duration(TimeSpan.FromSeconds(x.CycleSeconds)));
        OutputText  = x.YieldKnown
            ? string.Format(PiText.OutputTotalRemaining, x.TotalOutput.ToString("N0"), x.RemainingOutput.ToString("N0"))
            : PiText.YieldUnknown;
        (StateText, StateColor) = x.IsExpired
            ? (x.ExpiryTime is { } stopped ? string.Format(PiText.ExtractorStopped, PiFormat.Relative(stopped, now)) : PiText.Stopped, Palette.Bad)
            : (x.ExpiryTime is { } stops ? string.Format(PiText.ExtractorStops, PiFormat.Relative(stops, now)) : "", Palette.TextSecondary);

        HasChart = x.YieldKnown && x.CycleOutputs.Count > 0 && x.InstallTime is not null && x.CycleSeconds > 0;
        if (!HasChart) { Series = []; XAxes = []; YAxes = []; Sections = []; return; }

        // ⚠️ A timeline, not a row of cycle numbers: "cycle 37" says nothing about when to log in,
        // a date does. Each bar sits across its own cycle's window in local time — the program
        // starts at install and every cycle is the same length — so the axis reads in days (or
        // hours, for a short program), the bar under the "now" line is the one running, and the
        // hover gives the window the bar covers.
        var begin = x.InstallTime!.Value.ToLocalTime().DateTime;
        var cycle = TimeSpan.FromSeconds(x.CycleSeconds);
        DateTime CycleStart(int i) => begin + cycle * i;

        var done    = new List<DateTimePoint>();
        var current = new List<DateTimePoint>();
        var ahead   = new List<DateTimePoint>();
        for (var i = 0; i < x.CycleOutputs.Count; i++)
        {
            var point = new DateTimePoint(CycleStart(i) + cycle / 2, x.CycleOutputs[i]);
            if (i < x.CyclesDone)                       done.Add(point);
            else if (i == x.CyclesDone && !x.IsExpired) current.Add(point);
            else                                        ahead.Add(point);
        }

        // The window a bar covers, for its hover: from the start of its cycle to the end.
        string Window(DateTimePoint p)
        {
            var from = p.DateTime - cycle / 2;
            return string.Format(PiText.ChartCycleWindow, from.ToString(CommonText.DateMonthDayTime),
                                 (from + cycle).ToString("t", System.Globalization.CultureInfo.CurrentCulture));
        }

        ColumnSeries<DateTimePoint> Bars(string title, List<DateTimePoint> values, string token) => new()
        {
            Name               = title,
            Values             = values,
            Fill               = new SolidColorPaint(Palette.Sk(token)),
            Stroke             = null,
            IgnoresBarPosition = true,
            Padding            = 1,
            MaxBarWidth        = 14,
            XToolTipLabelFormatter = pt => pt.Model is { } m ? Window(m) : "",
            YToolTipLabelFormatter = pt => pt.Coordinate.PrimaryValue.ToString("N0"),
        };

        Series =
        [
            Bars(PiText.ChartDone,    done,    "Accent"),
            Bars(PiText.ChartCurrent, current, "Warn"),
            Bars(PiText.ChartAhead,   ahead,   "TextFaint"),
        ];

        // Days along the bottom for a program of two days or more — every second day past ten —
        // and hours for a shorter one. The column width is one cycle.
        var span = cycle * x.CycleOutputs.Count;
        var (step, format) = span >= TimeSpan.FromDays(10) ? (TimeSpan.FromDays(2), CommonText.DateMonthDay)
                           : span >= TimeSpan.FromDays(2)  ? (TimeSpan.FromDays(1), CommonText.DateMonthDay)
                           : span >= TimeSpan.FromHours(12) ? (TimeSpan.FromHours(6), "t")
                           :                                  (TimeSpan.FromHours(1), "t");
        XAxes =
        [
            new DateTimeAxis(cycle, d => d.ToString(format, System.Globalization.CultureInfo.CurrentCulture))
            {
                LabelsPaint     = ChartPaint.Labels,
                SeparatorsPaint = ChartPaint.Separators,
                TextSize        = 11,
                MinStep         = step.Ticks,
                MinLimit        = begin.Ticks,
                MaxLimit        = (begin + span).Ticks,
            },
        ];

        // A few round steps up the side, so the labels never crowd a short chart.
        var top = x.CycleOutputs.Max();
        YAxes =
        [
            new Axis
            {
                Name            = PiText.ChartUnits,
                NamePaint       = ChartPaint.Labels,
                LabelsPaint     = ChartPaint.Labels,
                SeparatorsPaint = ChartPaint.Separators,
                TextSize        = 11,
                Labeler         = v => v.ToString("N0"),
                MinLimit        = 0,
                MinStep         = NiceStep(top / 3.0),
            },
        ];

        // Now, as a line across the timeline, while the program runs.
        var nowLocal = now.ToLocalTime().DateTime;
        Sections = !x.IsExpired && nowLocal >= begin && nowLocal <= begin + span
            ? [new RectangularSection
              {
                  Xi     = nowLocal.Ticks,
                  Xj     = nowLocal.Ticks,
                  Stroke = new SolidColorPaint(Palette.Sk("Warn")) { StrokeThickness = 1.5f },
              }]
            : [];
        ChartPaint.TrackAxesOf(this);
    }

    /// <summary>The marker for "now" on the timeline, or none.</summary>
    public RectangularSection[] Sections { get; }

    /// <summary>1, 2 or 5 times a power of ten, at least <paramref name="rough"/>.</summary>
    private static double NiceStep(double rough)
    {
        if (rough <= 1) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (m * power >= rough) return m * power;
        return 10 * power;
    }
}

public sealed class PiProcessorRowVm
{
    public string PinName       { get; }
    public string SchematicName { get; }
    public string OutputName    { get; }
    public int    Tier          { get; }
    public string StateText     { get; }
    public IBrush StateColor    { get; }

    public PiProcessorRowVm(PiFactoryForecast p, PiStaticData sd, Func<int, string> name)
    {
        PinName       = name(p.TypeId);
        Tier          = (int)p.Tier;
        SchematicName = p.SchematicId is { } id && sd.Schematics.TryGetValue(id, out var s)
            ? PiNames.Schematic(id, s.Name) : PiText.NoSchematic;
        OutputName    = p.OutputTypeId is { } o ? name(o) : "";
        (StateText, StateColor) = p.StateAt switch
        {
            PiFactoryState.Running => (PiText.ProcessorRunning, Palette.Good),
            PiFactoryState.Idle    => (p.IdleSince is { } since
                                          ? string.Format(PiText.ProcessorIdleSince, PiFormat.When(since))
                                          : PiText.ProcessorIdle, Palette.Warn),
            _                      => (PiText.NoSchematic, Palette.TextFaint),
        };
    }
}

public sealed class PiStorageRowVm
{
    public string Name         { get; }
    public string KindText     { get; }
    public string FillText     { get; }
    public double Fill         { get; }
    public string VolumeText   { get; }
    public string ContentsText { get; }
    public string FullText     { get; }
    public IBrush FullColor    { get; }

    public PiStorageRowVm(PiStorageForecast s, DateTimeOffset now, Func<int, string> name)
    {
        Name     = name(s.TypeId);
        KindText = s.Kind switch
        {
            PiPinKind.Launchpad     => PiText.PinLaunchpad,
            PiPinKind.CommandCenter => PiText.PinCommandCenter,
            _                       => PiText.PinStorage,
        };
        Fill       = s.FillAt;
        FillText   = string.Format(PiText.Percent, (s.FillAt * 100).ToString("0"));
        VolumeText = string.Format(PiText.VolumeOfCapacity, s.UsedAt.ToString("N0"), s.Capacity.ToString("N0"));
        ContentsText = s.ContentsAt.Count == 0 ? PiText.Empty
            : string.Join(CommonText.ListSeparator, s.ContentsAt
                .OrderByDescending(kv => kv.Value)
                .Select(kv => string.Format(PiText.QuantityOf, kv.Value.ToString("N0"), name(kv.Key))));
        (FullText, FullColor) = s.FullAt is { } full
            ? (full <= now ? PiText.Full : PiFormat.Relative(full, now), full <= now ? Palette.Bad : Palette.TextPrimary)
            : (PiText.NotWithinHorizon, Palette.TextFaint);
    }
}

public sealed class PiFlowRowVm(PiFlow x, string name)
{
    public string Name     { get; } = name;
    public string TierText { get; } = x.Tier is { } t ? $"P{(int)t}" : "";
    public int    TierSort { get; } = x.Tier is { } t ? (int)t : 9;
    public string Produced { get; } = Amount(x.ProducedPerDay);
    public string Consumed { get; } = Amount(x.ConsumedPerDay);
    public string Imported { get; } = Amount(x.ImportedPerDay);
    public string Exported { get; } = Amount(x.ExportedPerDay);

    private static string Amount(double v) => v <= 0 ? "" : v >= 100 ? v.ToString("N0") : v.ToString("N1");
}
