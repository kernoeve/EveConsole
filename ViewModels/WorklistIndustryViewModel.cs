using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One enabled character, with their slot picture alongside the switches.</summary>
public sealed record IndyCharRow(int Id, string CharacterName, string Activities,
                                 string Assets, string Slots, WorklistIndyChar Config);

/// <summary>
/// Which characters run industry, on what, and where their materials come from.
///
/// Opt-in rather than "all my alts": slots belonging to characters in corps that never run
/// industry are not available for work, and counting them would turn the free-slot number into
/// fiction.
/// </summary>
public class WorklistIndustryViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IndustryAssignmentService       _assignment;
    private readonly WorklistSettings                _settings;

    public ObservableCollection<IndyCharRow>     Chars      { get; } = [];
    public ObservableCollection<CharacterOption> Characters { get; } = [];
    public ObservableCollection<ParkOption>      Parks      { get; } = [];

    public ReactiveCommand<Unit, Unit>            AddCommand    { get; }
    public ReactiveCommand<IndyCharRow, Unit>     DeleteCommand { get; }

    public WorklistIndustryViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                     IndustryAssignmentService assignment,
                                     WorklistSettings settings)
    {
        _dbFactory  = dbFactory;
        _assignment = assignment;
        _settings   = settings;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<IndyCharRow>(async r =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistIndyChars.Where(x => x.Id == r.Id).ExecuteDeleteAsync();
            await LoadAsync();
            if (IndustryChanged is not null) await IndustryChanged();
        });

        _ = LoadAsync();
    }

    public Func<Task>? IndustryChanged { get; set; }

    // ── Park ──────────────────────────────────────────────────────────────────

    private ParkOption? _selectedPark;
    public ParkOption? SelectedPark
    {
        get => _selectedPark;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPark, value);
            if (value is not null && !_loading) _ = ApplyParkAsync(value.Id);
        }
    }

    private async Task ApplyParkAsync(int parkId)
    {
        await _settings.SetIndustryParkAsync(parkId);
        await LoadAsync();
        if (IndustryChanged is not null) await IndustryChanged();
    }

    /// <summary>
    /// Structures in the chosen park with no real location behind them.
    ///
    /// Worth its own warning rather than a silent gap: an unlinked structure models rigs but
    /// points nowhere, so materials sitting in the real facility cannot be counted. Jobs there
    /// would read as blocked for want of materials that are actually present.
    /// </summary>
    private string _parkWarning = "";
    public string ParkWarning { get => _parkWarning; private set => this.RaiseAndSetIfChanged(ref _parkWarning, value); }
    public bool HasParkWarning => ParkWarning.Length > 0;

    // ── New row ───────────────────────────────────────────────────────────────

    private CharacterOption? _selectedCharacter;
    public CharacterOption? SelectedCharacter { get => _selectedCharacter; set => this.RaiseAndSetIfChanged(ref _selectedCharacter, value); }

    private bool _manufacturing = true;
    public bool Manufacturing { get => _manufacturing; set => this.RaiseAndSetIfChanged(ref _manufacturing, value); }

    private bool _reactions = true;
    public bool Reactions { get => _reactions; set => this.RaiseAndSetIfChanged(ref _reactions, value); }

    private bool _science;
    public bool Science { get => _science; set => this.RaiseAndSetIfChanged(ref _science, value); }

    private bool _corpAssets = true;
    public bool CorpAssets { get => _corpAssets; set => this.RaiseAndSetIfChanged(ref _corpAssets, value); }

    private bool _personalAssets = true;
    public bool PersonalAssets { get => _personalAssets; set => this.RaiseAndSetIfChanged(ref _personalAssets, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _loading;

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var parks = await db.IndyParks.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
            var chars = await db.Characters.AsNoTracking().OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name }).ToListAsync();

            var parkId = _settings.IndustryParkId;

            var unlinked = parkId > 0
                ? await db.IndyStructures.AsNoTracking()
                    .Where(s => s.ParkId == parkId && s.RealStructureId == null)
                    .Select(s => s.DisplayName)
                    .ToListAsync()
                : [];

            var linkedCount = parkId > 0
                ? await db.IndyStructures.CountAsync(s => s.ParkId == parkId && s.RealStructureId != null)
                : 0;

            // Slot figures come from the assignment service so the tab and the generator can
            // never disagree about how many slots a character has free.
            var candidates = await _assignment.LoadCandidatesAsync();

            var rows = candidates
                .OrderBy(c => c.Config.CharacterName)
                .Select(c => new IndyCharRow(
                    c.Config.Id,
                    c.Config.CharacterName,
                    Describe(c.Config),
                    c.Config.IncludeCorpAssets && c.Config.IncludePersonalAssets ? "Corp + personal"
                        : c.Config.IncludeCorpAssets     ? "Corp"
                        : c.Config.IncludePersonalAssets ? "Personal"
                        : "— none —",
                    $"M {c.FreeSlots[IndustryPool.Manufacturing]}/{c.Capacity[IndustryPool.Manufacturing]}  ·  "
                    + $"R {c.FreeSlots[IndustryPool.Reaction]}/{c.Capacity[IndustryPool.Reaction]}  ·  "
                    + $"S {c.FreeSlots[IndustryPool.Science]}/{c.Capacity[IndustryPool.Science]}",
                    c.Config))
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Chars.Clear();
                foreach (var r in rows) Chars.Add(r);

                Characters.Clear();
                foreach (var c in chars) Characters.Add(new CharacterOption(c.Id, c.Name));

                Parks.Clear();
                foreach (var p in parks) Parks.Add(new ParkOption { Id = p.Id, Name = p.Name });
                _selectedPark = Parks.FirstOrDefault(p => p.Id == parkId);
                this.RaisePropertyChanged(nameof(SelectedPark));

                ParkWarning = parkId <= 0
                    ? "No park selected. Industry jobs stay silent until one is chosen, because the park decides facilities and rigs."
                    : unlinked.Count > 0
                        ? $"{unlinked.Count} structure(s) in this park are not linked to a real location "
                          + $"({string.Join(", ", unlinked.Take(3))}"
                          + (unlinked.Count > 3 ? ", …" : "") + "). "
                          + "Materials there cannot be counted, so jobs may read as blocked when the inputs are actually present."
                        : linkedCount == 0
                            ? "This park has no structures. Nothing can be checked for materials."
                            : "";
                this.RaisePropertyChanged(nameof(HasParkWarning));

                Status = rows.Count == 0
                    ? "No characters enabled for industry yet. Until one is, no jobs can be assigned."
                    : $"{rows.Count:N0} character(s) enabled";
            });
        }
        finally { _loading = false; }
    }

    private static string Describe(WorklistIndyChar c)
    {
        var parts = new List<string>(3);
        if (c.Manufacturing) parts.Add("Manufacturing");
        if (c.Reactions)     parts.Add("Reactions");
        if (c.Science)       parts.Add("Science");
        return parts.Count == 0 ? "— none —" : string.Join(", ", parts);
    }

    private async Task AddAsync()
    {
        if (SelectedCharacter is null) { Status = "Pick a character."; return; }
        if (!Manufacturing && !Reactions && !Science)
        {
            Status = "Enable at least one activity, or the character can never be assigned a job.";
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.WorklistIndyChars
            .FirstOrDefaultAsync(c => c.CharacterId == SelectedCharacter.Id);

        if (existing is null)
        {
            db.WorklistIndyChars.Add(new WorklistIndyChar
            {
                CharacterId           = SelectedCharacter.Id,
                CharacterName         = SelectedCharacter.Name,
                Manufacturing         = Manufacturing,
                Reactions             = Reactions,
                Science               = Science,
                IncludeCorpAssets     = CorpAssets,
                IncludePersonalAssets = PersonalAssets,
            });
        }
        else
        {
            existing.CharacterName         = SelectedCharacter.Name;
            existing.Manufacturing         = Manufacturing;
            existing.Reactions             = Reactions;
            existing.Science               = Science;
            existing.IncludeCorpAssets     = CorpAssets;
            existing.IncludePersonalAssets = PersonalAssets;
        }

        await db.SaveChangesAsync();

        await LoadAsync();
        if (IndustryChanged is not null) await IndustryChanged();
    }
}
