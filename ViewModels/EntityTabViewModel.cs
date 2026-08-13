using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One entity tab: a name search that feeds a dropdown, and a set of sub-tabs describing
/// whatever gets picked.
///
/// The same class serves all six tabs across both tools. They differ only in which kind
/// they search and which sub-tabs apply, and six near-identical view models would drift
/// apart the first time one of them was fixed.
/// </summary>
public class EntityTabViewModel : ReactiveObject
{
    private readonly EntityBrowserService  _service;
    private readonly KillmailBrowserService _killmails;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public EntityKind Kind { get; }

    /// <summary>Kills / Losses only means anything for entities that appear on killmails.</summary>
    public bool HasKills => Kind is EntityKind.Pilot or EntityKind.PlayerCorp or EntityKind.Alliance;

    /// <summary>Intel sightings are recorded per character.</summary>
    public bool HasIntel => Kind is EntityKind.Pilot;

    public EntityTabViewModel(EntityBrowserService service, KillmailBrowserService killmails, EntityKind kind)
    {
        _service   = service;
        _killmails = killmails;
        Kind       = kind;
        OpenFactCommand    = ReactiveCommand.Create<EntityFact>(OpenFact);
        OpenMemberCommand  = ReactiveCommand.Create<EntityMemberRow>(r => Open(MemberLinkKind, r.Id));
        OpenHistoryCommand = ReactiveCommand.Create<EntityHistoryRow>(r => Open(HistoryLinkKind, r.LinkId));
        OpenItemCommand    = ReactiveCommand.Create<int>(id => NavigateToItemAction?.Invoke(id));
    }

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Feeds the AutoCompleteBox. Also records how many matched in total, so the tab can
    /// say when the dropdown was truncated rather than letting 300 look like all of them.
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> Populator =>
        async (text, ct) =>
        {
            var hits = await _service.SearchWithEsiAsync(Kind, text ?? "", ct);

            if (hits.Count >= EntityBrowserService.MaxMatches)
            {
                var total = await _service.CountMatchesAsync(Kind, text ?? "", ct);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    SearchNote = $"Showing {hits.Count:N0} of {total:N0} matches — keep typing to narrow it.");
            }
            else await Dispatcher.UIThread.InvokeAsync(() => SearchNote = "");

            return hits.Cast<object>().ToList();
        };

    private string _searchText = "";
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    private string _searchNote = "";
    public string SearchNote { get => _searchNote; private set => this.RaiseAndSetIfChanged(ref _searchNote, value); }

    /// <summary>What is on screen. Guards against reloading the same entity.</summary>
    private long _loadedId;

    private object? _selectedMatch;
    public object? SelectedMatch
    {
        get => _selectedMatch;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMatch, value);

            // Switching tabs re-attaches the AutoCompleteBox, which pushes this binding
            // through again with whatever was last picked. Without the id guard that
            // re-load overwrote an entity a link had just opened.
            if (value is EntityMatch m && m.Id != _loadedId) _ = LoadAsync(m.Id);
        }
    }

    // ── Selection state ───────────────────────────────────────────────────────

    private bool _hasSelection;
    public bool HasSelection { get => _hasSelection; private set => this.RaiseAndSetIfChanged(ref _hasSelection, value); }

    public bool NoSelection => !HasSelection;

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    // ── About ─────────────────────────────────────────────────────────────────

    private string _name = "";
    public string Name { get => _name; private set => this.RaiseAndSetIfChanged(ref _name, value); }

    private string _subtitle = "";
    public string Subtitle { get => _subtitle; private set => this.RaiseAndSetIfChanged(ref _subtitle, value); }

    private string _description = "";
    public string Description { get => _description; private set => this.RaiseAndSetIfChanged(ref _description, value); }

    public bool HasDescription => Description.Length > 0;

    private Bitmap? _image;
    public Bitmap? Image { get => _image; private set => this.RaiseAndSetIfChanged(ref _image, value); }

    public ObservableCollection<EntityFact>       Facts { get; } = [];
    public ObservableCollection<KillmailListRowVm> Kills { get; } = [];
    public ObservableCollection<IntelSightingRow> Intel   { get; } = [];
    public ObservableCollection<EntityMemberRow>  Members { get; } = [];
    public ObservableCollection<EntityHistoryRow> History { get; } = [];

    public ObservableCollection<EntityStationRow>   Stations  { get; } = [];
    public ObservableCollection<LpOfferRow>         LpOffers  { get; } = [];
    public ObservableCollection<FactionWarfareRow>  Warfare   { get; } = [];

    /// <summary>Alliances list their member corporations; corporations list where they have been.</summary>
    /// Members: an alliance lists its corporations, an NPC corporation its agents, a
    /// faction its corporations — three different rosters, one grid.
    public bool HasMembers  => Kind is EntityKind.Alliance or EntityKind.NpcCorp or EntityKind.Faction;
    public bool HasHistory  => Kind is EntityKind.PlayerCorp or EntityKind.Pilot;
    public bool HasStations => Kind is EntityKind.NpcCorp;
    public bool HasLpOffers => Kind is EntityKind.NpcCorp;
    public bool HasWarfare  => Kind is EntityKind.Faction;

    public string MembersHeader => Kind switch
    {
        EntityKind.Alliance => "Member Corps",
        EntityKind.NpcCorp  => "Agents",
        _                   => "Corporations",
    };

    public string HistoryHeader => Kind is EntityKind.Pilot ? "Corp History" : "Alliance History";

    /// <summary>The column heading over the history grid's first column.</summary>
    public string HistoryEntityHeader => Kind is EntityKind.Pilot ? "Corporation" : "Alliance";

    /// <summary>Where a click in the members or history grid should go.</summary>
    public EntityKind MemberLinkKind => Kind switch
    {
        EntityKind.Alliance => EntityKind.PlayerCorp,
        EntityKind.NpcCorp  => EntityKind.Agent,
        _                   => EntityKind.NpcCorp,
    };

    public EntityKind HistoryLinkKind => Kind is EntityKind.Pilot ? EntityKind.PlayerCorp : EntityKind.Alliance;

    /// <summary>Set by the owning tool — switches to another tab and loads an entity there.</summary>
    public Action<EntityKind, long>? NavigateTo { get; set; }

    public void Open(EntityKind kind, long id)
    {
        if (id > 0) NavigateTo?.Invoke(kind, id);
    }

    private Bitmap? _corpLogo;
    public Bitmap? CorpLogo { get => _corpLogo; private set => this.RaiseAndSetIfChanged(ref _corpLogo, value); }

    private Bitmap? _allianceLogo;
    public Bitmap? AllianceLogo { get => _allianceLogo; private set => this.RaiseAndSetIfChanged(ref _allianceLogo, value); }

    private bool _hasAffiliation;
    public bool HasAffiliation { get => _hasAffiliation; private set => this.RaiseAndSetIfChanged(ref _hasAffiliation, value); }

    public ReactiveCommand<EntityFact, System.Reactive.Unit>        OpenFactCommand    { get; }
    public ReactiveCommand<EntityMemberRow, System.Reactive.Unit>  OpenMemberCommand  { get; }
    public ReactiveCommand<EntityHistoryRow, System.Reactive.Unit> OpenHistoryCommand { get; }
    public ReactiveCommand<int, System.Reactive.Unit>              OpenItemCommand    { get; }

    /// <summary>Set by MainWindowViewModel — opens the Item Browser on a type.</summary>
    public Action<int>? NavigateToItemAction { get; set; }

    public void OpenFact(EntityFact fact)
    {
        if (fact.IsEntityLink) Open(fact.LinkKind!.Value, fact.LinkId);
        else if (fact.IsUrlLink) OpenUrl(fact.Url!);
    }

    /// <summary>Corporation URLs open in the system browser, not in the app.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* a bad URL in a corp description is not worth surfacing */ }
    }

    private string _stationsStatus = "";
    public string StationsStatus { get => _stationsStatus; private set => this.RaiseAndSetIfChanged(ref _stationsStatus, value); }

    private string _lpOffersStatus = "";
    public string LpOffersStatus { get => _lpOffersStatus; private set => this.RaiseAndSetIfChanged(ref _lpOffersStatus, value); }

    private string _warfareStatus = "";
    public string WarfareStatus { get => _warfareStatus; private set => this.RaiseAndSetIfChanged(ref _warfareStatus, value); }

    /// <summary>
    /// Loads a straightforward list into a collection and reports the count. The remaining
    /// panes differ only in their source and their noun, which is not enough to justify a
    /// method each.
    /// </summary>
    private async Task LoadListAsync<T>(ObservableCollection<T> target, Func<Task<List<T>>> load,
                                        Action<string> setStatus, string noun)
    {
        try
        {
            var rows = await load();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                target.Clear();
                foreach (var r in rows) target.Add(r);
                setStatus(rows.Count == 0 ? $"No {noun}s found." : $"{rows.Count:N0} {noun}(s)");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { setStatus($"Error: {ex.Message}"); }
    }

    private string _membersStatus = "";
    public string MembersStatus { get => _membersStatus; private set => this.RaiseAndSetIfChanged(ref _membersStatus, value); }

    private string _historyStatus = "";
    public string HistoryStatus { get => _historyStatus; private set => this.RaiseAndSetIfChanged(ref _historyStatus, value); }

    private string _killsStatus = "";
    public string KillsStatus { get => _killsStatus; private set => this.RaiseAndSetIfChanged(ref _killsStatus, value); }

    private string _intelStatus = "";
    public string IntelStatus { get => _intelStatus; private set => this.RaiseAndSetIfChanged(ref _intelStatus, value); }

    // Each selection cancels the last, so switching quickly cannot leave a slower load
    // writing its results over a newer one.
    private CancellationTokenSource _cts = new();

    public async Task LoadAsync(long id)
    {
        var prev = _cts;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try { prev.Cancel(); prev.Dispose(); } catch { }

        try
        {
            var detail = await _service.DetailAsync(Kind, id, ct);
            if (detail is null || ct.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _loadedId = id;

                // Keep the picker in step with what a link loaded, so the box does not
                // still read the previous entity.
                _selectedMatch = new EntityMatch(id, detail.Name, "");
                this.RaisePropertyChanged(nameof(SelectedMatch));
                _searchText = detail.Name;
                this.RaisePropertyChanged(nameof(SearchText));

                Name        = detail.Name;
                Subtitle    = detail.Subtitle;
                Description = detail.Description;
                Image = null; CorpLogo = null; AllianceLogo = null; HasAffiliation = false;

                Facts.Clear();
                foreach (var f in detail.Facts)
                    if (!string.IsNullOrWhiteSpace(f.Value)) Facts.Add(f);

                HasSelection = true;
                this.RaisePropertyChanged(nameof(NoSelection));
                this.RaisePropertyChanged(nameof(HasDescription));
                Status = "";
            });

            // Everything below is optional detail — the About pane is already usable, so
            // none of it blocks the others.
            if (detail.ImageUrl is { } url) _ = LoadImageAsync(url, ct);
            _ = EnrichAsync(id, ct);
            if (HasKills) _ = LoadKillsAsync(id, ct);
            if (HasIntel)   _ = LoadIntelAsync(id, ct);
            if (HasMembers) _ = LoadMembersAsync(id, ct);
            if (HasHistory)  _ = LoadHistoryAsync(id, ct);
            if (HasStations) _ = LoadListAsync(Stations, () => _service.NpcCorpStationsAsync(id, ct),
                                               v => StationsStatus = v, "station");
            if (HasLpOffers) _ = LoadListAsync(LpOffers, () => _service.NpcCorpLpOffersAsync(id, ct),
                                               v => LpOffersStatus = v, "LP offer");
            if (HasWarfare)  _ = LoadListAsync(Warfare, () => _service.FactionWarfareAsync(id, ct),
                                               v => WarfareStatus = v, "faction warfare system");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Public ESI detail, folded in once it arrives. Appended rather than replacing the
    /// local facts, and only after they are already on screen — the pane must not sit empty
    /// waiting on a network call.
    /// </summary>
    private async Task EnrichAsync(long id, CancellationToken ct)
    {
        try
        {
            var (facts, description) = await _service.EnrichAsync(Kind, id, ct);
            if (ct.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;

                foreach (var f in facts)
                    if (!string.IsNullOrWhiteSpace(f.Value)) Facts.Add(f);

                foreach (var f in facts)
                {
                    if (f.LinkKind == EntityKind.PlayerCorp && f.Label == "Corporation")
                        _ = LoadLogoAsync(EntityBrowserService.ImageUrlFor(EntityKind.PlayerCorp, f.LinkId),
                                          v => CorpLogo = v, ct);
                    if (f.LinkKind == EntityKind.Alliance)
                        _ = LoadLogoAsync(EntityBrowserService.ImageUrlFor(EntityKind.Alliance, f.LinkId),
                                          v => AllianceLogo = v, ct);
                }
                HasAffiliation = facts.Any(f => f.LinkKind is EntityKind.PlayerCorp or EntityKind.Alliance);

                if (description.Length > 0)
                {
                    Description = description;
                    this.RaisePropertyChanged(nameof(HasDescription));
                }
            });
        }
        catch (OperationCanceledException) { }
        catch { /* additive only */ }
    }

    private async Task LoadMembersAsync(long id, CancellationToken ct)
    {
        try
        {
            var rows = Kind switch
            {
                EntityKind.Alliance => await _service.AllianceCorpsAsync(id, ct),
                EntityKind.NpcCorp  => await _service.NpcCorpAgentsAsync(id, ct),
                _                   => await _service.FactionCorpsAsync(id, ct),
            };
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                Members.Clear();
                foreach (var r in rows) Members.Add(r);
                MembersStatus = rows.Count == 0
                    ? "No member corporations returned. ESI reports current membership only."
                    : $"{rows.Count:N0} member corporation(s)";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MembersStatus = $"Error: {ex.Message}"; }
    }

    private async Task LoadHistoryAsync(long id, CancellationToken ct)
    {
        try
        {
            var rows = Kind is EntityKind.Pilot
                ? await _service.CharacterCorpHistoryAsync(id, ct)
                : await _service.CorpAllianceHistoryAsync(id, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                History.Clear();
                foreach (var r in rows) History.Add(r);
                HistoryStatus = rows.Count == 0
                    ? "No alliance history recorded for this corporation."
                    : $"{rows.Count:N0} period(s), newest first";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { HistoryStatus = $"Error: {ex.Message}"; }
    }

    private async Task LoadLogoAsync(string? url, Action<Bitmap> set, CancellationToken ct)
    {
        if (url is null) return;
        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() => { if (!ct.IsCancellationRequested) set(bmp); });
        }
        catch { /* decoration */ }
    }

    private async Task LoadImageAsync(string url, CancellationToken ct)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() => { if (!ct.IsCancellationRequested) Image = bmp; });
        }
        catch { /* portraits are decoration — a missing one is not worth reporting */ }
    }

    private async Task LoadKillsAsync(long id, CancellationToken ct)
    {
        try
        {
            var page = await _killmails.GetListAsync(0, EntityBrowserService.MaxDetailRows,
                entityKind: Kind, entityId: id, ct: ct);
            var rows = page.Rows.Select(r => new KillmailListRowVm(r)).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                Kills.Clear();
                foreach (var r in rows) Kills.Add(r);
                _ = Task.WhenAll(rows.Select(r => r.LoadImagesAsync()));
                KillsStatus = rows.Count == 0
                    ? "No killmails recorded for this entity."
                    : $"{rows.Count:N0} most recent killmail(s)"
                      + (rows.Count >= EntityBrowserService.MaxDetailRows ? " (capped)" : "");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KillsStatus = $"Error: {ex.Message}"; }
    }

    private async Task LoadIntelAsync(long id, CancellationToken ct)
    {
        try
        {
            var rows = await _service.IntelAsync(id, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                Intel.Clear();
                foreach (var r in rows) Intel.Add(r);
                IntelStatus = rows.Count == 0
                    ? "No intel sightings. These come from intel channels via the chat log importer."
                    : $"{rows.Count:N0} sighting(s)";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { IntelStatus = $"Error: {ex.Message}"; }
    }
}
