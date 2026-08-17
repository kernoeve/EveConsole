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
/// One market alt in the grid, with the character and the note editable in place and saved on
/// change. The station is not: it is the row's identity, and repointing it is the same act as
/// adding one, which the form above already does.
/// </summary>
public sealed class MarketAltRow : ReactiveObject
{
    private readonly Func<MarketAltRow, Task> _save;
    private readonly bool _loaded;

    public WorklistMarketAlt Alt { get; }
    public int    Id           => Alt.Id;
    public string LocationName => Alt.LocationName;

    /// <summary>Held on the row, not reached for with $parent — see InvRuleRow.GroupOptions.</summary>
    public IEnumerable<CharacterOption> CharacterOptions { get; }

    public MarketAltRow(WorklistMarketAlt alt, CharacterOption? character,
                        IEnumerable<CharacterOption> characterOptions, Func<MarketAltRow, Task> save)
    {
        Alt              = alt;
        CharacterOptions = characterOptions;
        _save            = save;

        _character = character;
        _note      = alt.Note;

        _loaded = true;
    }

    private CharacterOption? _character;
    public CharacterOption? Character
    {
        get => _character;
        set
        {
            // A null is the combo clearing itself while the grid realises the row, not the user
            // unassigning the station — which is what Remove is for.
            if (value is null) return;
            this.RaiseAndSetIfChanged(ref _character, value);
            this.RaisePropertyChanged(nameof(CharacterName));
            Persist();
        }
    }

    /// <summary>Sorted on, so the column still orders by who rather than by row order.</summary>
    public string CharacterName => _character?.Name ?? Alt.CharacterName;

    private string _note;
    public string Note
    {
        get => _note;
        set { this.RaiseAndSetIfChanged(ref _note, value); Persist(); }
    }

    private void Persist()
    {
        if (!_loaded) return;

        if (_character is not null)
        {
            Alt.CharacterId   = _character.Id;
            Alt.CharacterName = _character.Name;
        }
        Alt.Note = _note;

        _ = _save(this);
    }
}

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

    public ObservableCollection<MarketAltRow>     MarketAlts { get; } = [];
    public ObservableCollection<CharacterOption>  Characters { get; } = [];

    public ReactiveCommand<Unit, Unit>          AddCommand    { get; }
    public ReactiveCommand<MarketAltRow, Unit>  DeleteCommand { get; }

    public WorklistMarketAltsViewModel(WorklistMarketAltService marketAlts, CorpActivityService stations,
                                  IDbContextFactory<AppDbContext> dbFactory)
    {
        _marketAlts     = marketAlts;
        _stations  = stations;
        _dbFactory = dbFactory;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<MarketAltRow>(async d =>
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
        var options = chars.Select(c => new CharacterOption(c.Id, c.Name)).ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Characters first: the rows carry one each and the grid combo binds its ItemsSource
            // here, so a row realised against an empty list draws blank and pushes null back.
            Characters.Clear();
            foreach (var c in options) Characters.Add(c);

            MarketAlts.Clear();
            foreach (var d in rows)
                MarketAlts.Add(new MarketAltRow(d, options.FirstOrDefault(o => o.Id == d.CharacterId),
                                                Characters, SaveRowAsync));

            Status = rows.Count == 0
                ? "No marketAlts yet. Until a station has one, its items show as blocked because "
                + "nothing knows which character should do the work."
                : $"{rows.Count:N0} market alt(s)";
        });
    }

    /// <summary>
    /// Writes one edited row back, a short pause after the last keystroke.
    ///
    /// <para>The pause is for the note, which would otherwise save on every character typed and
    /// rebuild the worklist behind the tab each time. Per row, so editing one cannot cancel a
    /// neighbour's pending write.</para>
    ///
    /// <para>No reload: rebuilding the grid would replace the row under the cursor mid-edit.</para>
    /// </summary>
    private readonly Dictionary<int, CancellationTokenSource> _pendingSaves = [];

    private async Task SaveRowAsync(MarketAltRow row)
    {
        if (_pendingSaves.Remove(row.Id, out var inFlight)) inFlight.Cancel();
        var cts = _pendingSaves[row.Id] = new CancellationTokenSource();

        try { await Task.Delay(500, cts.Token); }
        catch (OperationCanceledException) { return; }
        finally { if (ReferenceEquals(_pendingSaves.GetValueOrDefault(row.Id), cts)) _pendingSaves.Remove(row.Id); }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistMarketAlts
                .Where(x => x.Id == row.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CharacterId,   row.Alt.CharacterId)
                    .SetProperty(x => x.CharacterName, row.Alt.CharacterName)
                    .SetProperty(x => x.Note,          row.Alt.Note));

            Status = "Saved.";
            if (MarketAltsChanged is not null) await MarketAltsChanged();
        }
        catch (Exception ex)
        {
            Status = $"Could not save that change: {ex.Message}";
        }
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
