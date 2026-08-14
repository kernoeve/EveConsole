using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Which character works which station.
///
/// Config rather than a report, and it lives inside the Worklist tool because it exists only to
/// serve it — every generator asks the same question, and an unassigned station is why an item
/// shows up blocked.
/// </summary>
public class WorklistMarketAltsViewModel : ReactiveObject
{
    private readonly WorklistMarketAltService             _marketAlts;
    private readonly CorpActivityService             _stations;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ObservableCollection<WorklistMarketAlt>     MarketAlts      { get; } = [];
    public ObservableCollection<CharacterOption>  Characters { get; } = [];

    public ReactiveCommand<Unit, Unit>          AddCommand    { get; }
    public ReactiveCommand<WorklistMarketAlt, Unit>  DeleteCommand { get; }

    public WorklistMarketAltsViewModel(WorklistMarketAltService marketAlts, CorpActivityService stations,
                                  IDbContextFactory<AppDbContext> dbFactory)
    {
        _marketAlts     = marketAlts;
        _stations  = stations;
        _dbFactory = dbFactory;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<WorklistMarketAlt>(async d =>
        {
            await _marketAlts.DeleteAsync(d.Id);
            await LoadAsync();
        });

        _ = LoadAsync();
    }

    /// <summary>Raised after a market alt changes so the worklist can pick up the new routing.
    /// Not named Changed — ReactiveObject already has one.</summary>
    public Func<Task>? MarketAltsChanged { get; set; }

    // ── Station picker ────────────────────────────────────────────────────────

    /// <summary>Same station search the standing-buy dialog uses, so both cover NPC stations
    /// and player structures identically.</summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> LocationPopulator =>
        async (text, ct) =>
        {
            var hits = await _stations.SearchSdeStationsAsync(text ?? "", ct);
            return hits.Cast<object>().ToList();
        };

    private object? _selectedLocation;
    public object? SelectedLocation
    {
        get => _selectedLocation;
        set => this.RaiseAndSetIfChanged(ref _selectedLocation, value);
    }

    private string _locationText = "";
    public string LocationText
    {
        get => _locationText;
        set => this.RaiseAndSetIfChanged(ref _locationText, value);
    }

    private CharacterOption? _selectedCharacter;
    public CharacterOption? SelectedCharacter
    {
        get => _selectedCharacter;
        set => this.RaiseAndSetIfChanged(ref _selectedCharacter, value);
    }

    private string _note = "";
    public string Note { get => _note; set => this.RaiseAndSetIfChanged(ref _note, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public async Task LoadAsync()
    {
        var rows = await _marketAlts.GetAllAsync();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var chars = await db.Characters.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            MarketAlts.Clear();
            foreach (var d in rows) MarketAlts.Add(d);

            Characters.Clear();
            foreach (var c in chars) Characters.Add(new CharacterOption(c.Id, c.Name));

            Status = rows.Count == 0
                ? "No marketAlts yet. Until a station has one, its items show as blocked because "
                + "nothing knows which character should do the work."
                : $"{rows.Count:N0} market alt(s)";
        });
    }

    private async Task AddAsync()
    {
        if (SelectedLocation is not SdeStationResult loc)
        {
            Status = "Pick a station or structure from the list.";
            return;
        }
        if (SelectedCharacter is null)
        {
            Status = "Pick the character who works there.";
            return;
        }

        // Saving by LocationId reassigns an existing market alt rather than failing on the unique
        // index — moving a station to a different alt is the common edit, not an error.
        await _marketAlts.SaveAsync(new WorklistMarketAlt
        {
            LocationId    = loc.StationId,
            LocationName  = loc.Name,
            CharacterId   = SelectedCharacter.Id,
            CharacterName = SelectedCharacter.Name,
            Note          = Note,
        });

        SelectedLocation = null;
        LocationText     = "";
        Note             = "";

        await LoadAsync();
        if (MarketAltsChanged is not null) await MarketAltsChanged();
    }
}
