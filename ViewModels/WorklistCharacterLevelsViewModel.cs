using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A character or corporation offered by a picker.</summary>
public sealed record OwnerOption(long Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One character level as listed: on or off, or deleted. Changing what it is means
/// adding it again, as a level is the group, the station and the character together.</summary>
public sealed class CharacterLevelRow : ReactiveObject
{
    private readonly Func<CharacterLevelRow, Task> _save;
    private readonly bool _loaded;

    public WorklistCharacterLevel Level { get; }
    public string GroupName     { get; }
    public string LocationShown => SdeNames.Location(Level.LocationId, Level.LocationName);
    public string CharacterName => Level.CharacterName;

    /// <summary>Said when the character is in the default corporation, where a level does little:
    /// what that corporation holds is theirs to use already.</summary>
    public string Note { get; }

    public CharacterLevelRow(WorklistCharacterLevel level, string groupName, string note, Func<CharacterLevelRow, Task> save)
    {
        Level     = level;
        GroupName = groupName;
        Note      = note;
        _save     = save;
        _enabled  = level.Enabled;
        _loaded   = true;
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _enabled, value);
            if (!_loaded) return;
            Level.Enabled = value;
            _ = _save(this);
        }
    }
}

/// <summary>
/// The default corporation, and what each character keeps of a group at a station.
///
/// <para>A character can build only with what is in its own hangar or its corporation's. The
/// default corporation is where the Worklist's items should be, so that every alt in it can use
/// them; a character level keeps some with one character instead, which for an alt outside that
/// corporation is all it can build with. Both raise Transfer tasks (see <see cref="TransferGenerator"/>).</para>
/// </summary>
public class WorklistCharacterLevelsViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly WorklistSettings                _settings;
    private bool _settingsLoaded;

    public ObservableCollection<CharacterLevelRow> Levels      { get; } = [];
    public ObservableCollection<InvGroupOption>    Groups      { get; } = [];
    public ObservableCollection<OwnerOption>       Characters  { get; } = [];
    public ObservableCollection<OwnerOption>       Corporations { get; } = [];

    public ReactiveCommand<Unit, Unit>              AddCommand    { get; }
    public ReactiveCommand<CharacterLevelRow, Unit> DeleteCommand { get; }

    /// <summary>Stations for the picker: the station levels' own search.</summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> LocationPopulator { get; }

    public Func<Task>? LevelsChanged { get; set; }

    public WorklistCharacterLevelsViewModel(IDbContextFactory<AppDbContext> dbFactory, WorklistSettings settings,
                                            Func<string?, CancellationToken, Task<IEnumerable<object>>> locationPopulator)
    {
        _dbFactory        = dbFactory;
        _settings         = settings;
        LocationPopulator = locationPopulator;
        _fromOtherCorps   = settings.DefaultCorpFromOtherCorps;
        _restockBand      = settings.CharacterRestockBandPercent;
        _surplusBand      = settings.CharacterSurplusBandPercent;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<CharacterLevelRow>(async r =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistCharacterLevels.Where(x => x.Id == r.Level.Id).ExecuteDeleteAsync();
            await LoadAsync();
            if (LevelsChanged is not null) await LevelsChanged();
        });

        _ = LoadAsync();
    }

    // ── The default corporation ───────────────────────────────────────────────

    /// <summary>"None" first: with none, nothing is given to a corporation.</summary>
    private OwnerOption? _defaultCorp;
    public OwnerOption? DefaultCorp
    {
        get => _defaultCorp;
        set
        {
            // A refilled combo sets null; that is not a choice.
            if (value is null || ReferenceEquals(value, _defaultCorp)) return;
            this.RaiseAndSetIfChanged(ref _defaultCorp, value);
            if (_settingsLoaded) Save(() => _settings.SetDefaultCorporationAsync(value.Id));
        }
    }

    private bool _fromOtherCorps;
    public bool FromOtherCorps
    {
        get => _fromOtherCorps;
        set
        {
            this.RaiseAndSetIfChanged(ref _fromOtherCorps, value);
            if (_settingsLoaded) Save(() => _settings.SetDefaultCorpFromOtherCorpsAsync(value));
        }
    }

    // ── How far off its level a character may be ─────────────────────────────
    //
    // As the station levels' two bands, for what a Transfer task gives or takes back: a contract
    // for a handful is no more worth making than a trip for one.

    private double _restockBand;
    public double RestockBand
    {
        get => _restockBand;
        set
        {
            this.RaiseAndSetIfChanged(ref _restockBand, value);
            if (_settingsLoaded) Save(() => _settings.SetBandAsync(WorklistSettings.CharacterRestockBandKey, value));
        }
    }

    private double _surplusBand;
    public double SurplusBand
    {
        get => _surplusBand;
        set
        {
            this.RaiseAndSetIfChanged(ref _surplusBand, value);
            if (_settingsLoaded) Save(() => _settings.SetBandAsync(WorklistSettings.CharacterSurplusBandKey, value));
        }
    }

    private void Save(Func<Task> write) => _ = Task.Run(async () =>
    {
        try
        {
            await write();
            Status = WorklistText.StatusSaved;
            if (LevelsChanged is not null) await LevelsChanged();
        }
        catch (Exception ex) { Status = string.Format(WorklistText.StatusSaveFailed, ex.Message); }
    });

    // ── Adding a level ────────────────────────────────────────────────────────

    private InvGroupOption? _selectedGroup;
    public InvGroupOption? SelectedGroup { get => _selectedGroup; set => this.RaiseAndSetIfChanged(ref _selectedGroup, value); }

    private object? _selectedLocation;
    public object? SelectedLocation { get => _selectedLocation; set => this.RaiseAndSetIfChanged(ref _selectedLocation, value); }

    private string _locationText = "";
    public string LocationText { get => _locationText; set => this.RaiseAndSetIfChanged(ref _locationText, value); }

    private OwnerOption? _selectedCharacter;
    public OwnerOption? SelectedCharacter { get => _selectedCharacter; set => this.RaiseAndSetIfChanged(ref _selectedCharacter, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private async Task AddAsync()
    {
        if (SelectedGroup is null) { Status = WorklistText.PickInventoryGroup; return; }
        if (SelectedLocation is not SdeStationResult loc) { Status = WorklistText.PickStationOrStructure; return; }
        if (SelectedCharacter is not { } who) { Status = WorklistText.CharacterLevelPickCharacter; return; }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.WorklistCharacterLevels.FirstOrDefaultAsync(x =>
            x.GroupId == SelectedGroup.Id && x.LocationId == loc.StationId && x.CharacterId == who.Id);
        if (existing is not null)
        {
            existing.Enabled       = true;
            existing.LocationName  = loc.Name;
            existing.CharacterName = who.Name;
        }
        else
        {
            db.WorklistCharacterLevels.Add(new WorklistCharacterLevel
            {
                GroupId       = SelectedGroup.Id,
                LocationId    = loc.StationId,
                LocationName  = loc.Name,
                CharacterId   = who.Id,
                CharacterName = who.Name,
            });
        }
        await db.SaveChangesAsync();

        // The station and character are left as they were: levels are entered a character and a
        // station at a time, several groups each.
        await LoadAsync();
        if (LevelsChanged is not null) await LevelsChanged();
    }

    private async Task SaveRowAsync(CharacterLevelRow row)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistCharacterLevels.Where(x => x.Id == row.Level.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, row.Level.Enabled));
            Status = WorklistText.StatusSaved;
            if (LevelsChanged is not null) await LevelsChanged();
        }
        catch (Exception ex) { Status = string.Format(WorklistText.StatusSaveFailed, ex.Message); }
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var groups = (await db.InvLevelGroups.AsNoTracking().OrderBy(g => g.Name)
                .Select(g => new { g.Id, g.Name }).ToListAsync())
            .Select(g => new InvGroupOption(g.Id, g.Name)).ToList();
        var characters = await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.Name, Corp = (long)c.CorporationId }).ToListAsync();
        var corps = await db.Corporations.AsNoTracking()
            .Select(c => new { Id = (long)c.Id, c.Name }).ToListAsync();
        var levels = await db.WorklistCharacterLevels.AsNoTracking().ToListAsync();

        await SdeNames.EnsureLoadedAsync();

        var home = _settings.DefaultCorporationId;
        var corpOf = characters.ToDictionary(c => c.Id, c => c.Corp);
        var rows = levels
            .Select(l => new CharacterLevelRow(
                l,
                groups.FirstOrDefault(g => g.Id == l.GroupId)?.Name ?? string.Format(WorklistText.GroupWithId, l.GroupId),
                home != 0 && corpOf.GetValueOrDefault(l.CharacterId) == home ? WorklistText.CharacterLevelInDefaultCorp : "",
                SaveRowAsync))
            .OrderBy(r => r.CharacterName, StringComparer.CurrentCulture)
            .ThenBy(r => r.LocationShown, StringComparer.CurrentCulture)
            .ThenBy(r => r.GroupName, StringComparer.CurrentCulture)
            .ToList();

        var corpOptions = new List<OwnerOption> { new(0, WorklistText.DefaultCorpNone) };
        corpOptions.AddRange(corps.OrderBy(c => c.Name, StringComparer.CurrentCulture).Select(c => new OwnerOption(c.Id, c.Name)));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Groups.Clear();
            foreach (var g in groups) Groups.Add(g);
            Characters.Clear();
            foreach (var c in characters.OrderBy(c => c.Name, StringComparer.CurrentCulture)) Characters.Add(new OwnerOption(c.Id, c.Name));

            _settingsLoaded = false;
            Corporations.Clear();
            foreach (var c in corpOptions) Corporations.Add(c);
            // ⚠️ After the refill: a combo refilled under its selection shows blank.
            _defaultCorp = null;
            DefaultCorp = corpOptions.FirstOrDefault(c => c.Id == home) ?? corpOptions[0];
            _settingsLoaded = true;

            Levels.Clear();
            foreach (var r in rows) Levels.Add(r);
            Status = rows.Count == 0
                ? WorklistText.CharacterLevelsNone
                : Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.CharacterLevelsCountOther), rows.Count);
        });
    }
}
