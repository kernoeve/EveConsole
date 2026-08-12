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
    private readonly EntityBrowserService _service;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public EntityKind Kind { get; }

    /// <summary>Kills / Losses only means anything for entities that appear on killmails.</summary>
    public bool HasKills => Kind is EntityKind.Pilot or EntityKind.PlayerCorp or EntityKind.Alliance;

    /// <summary>Intel sightings are recorded per character.</summary>
    public bool HasIntel => Kind is EntityKind.Pilot;

    public EntityTabViewModel(EntityBrowserService service, EntityKind kind)
    {
        _service = service;
        Kind     = kind;
    }

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Feeds the AutoCompleteBox. Also records how many matched in total, so the tab can
    /// say when the dropdown was truncated rather than letting 300 look like all of them.
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> Populator =>
        async (text, ct) =>
        {
            var hits = await _service.SearchAsync(Kind, text ?? "", ct);

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

    private object? _selectedMatch;
    public object? SelectedMatch
    {
        get => _selectedMatch;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMatch, value);
            if (value is EntityMatch m) _ = LoadAsync(m.Id);
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
    public ObservableCollection<EntityKillRow>    Kills { get; } = [];
    public ObservableCollection<IntelSightingRow> Intel { get; } = [];

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
                Name        = detail.Name;
                Subtitle    = detail.Subtitle;
                Description = detail.Description;
                Image       = null;

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
            if (HasKills) _ = LoadKillsAsync(id, ct);
            if (HasIntel) _ = LoadIntelAsync(id, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"Error: {ex.Message}");
        }
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
            var rows = await _service.KillsAsync(Kind, id, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                Kills.Clear();
                foreach (var r in rows) Kills.Add(r);
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
