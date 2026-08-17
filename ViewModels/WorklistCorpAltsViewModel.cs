using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

public sealed record CorpOption(long Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One corporation's assignment in the grid, with the character and note editable in place and
/// saved on change. The corporation is not: it is the row's identity.
/// </summary>
public sealed class CorpAltRow : ReactiveObject
{
    private readonly Func<CorpAltRow, Task> _save;
    private readonly bool _loaded;

    public WorklistCorpAlt Alt { get; }
    public int    Id              => Alt.Id;
    public string CorporationName => Alt.CorporationName;

    /// <summary>Held on the row, not reached for with $parent — see InvRuleRow.GroupOptions.</summary>
    public IEnumerable<CharacterOption> CharacterOptions { get; }

    public CorpAltRow(WorklistCorpAlt alt, CharacterOption? character,
                      IEnumerable<CharacterOption> characterOptions, Func<CorpAltRow, Task> save)
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
            // A null is the combo clearing itself as the row realises, not an unassignment.
            if (value is null) return;
            this.RaiseAndSetIfChanged(ref _character, value);
            this.RaisePropertyChanged(nameof(CharacterName));
            Persist();
        }
    }

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
/// Which character maintains each corporation's standing projects.
///
/// The definitions themselves already live in Corp Activity, so this is the only configuration
/// the worklist needs for them — which is why the tab is just this mapping and nothing else.
///
/// The corporation dropdown lists only corps that actually have standing projects. Offering
/// every known corp would invite assigning a character to one that can never produce an item.
/// </summary>
public class WorklistCorpAltsViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly WorklistCorpAltService          _corpAlts;

    public ObservableCollection<CorpAltRow>       Alts       { get; } = [];
    public ObservableCollection<CorpOption>       Corps      { get; } = [];
    public ObservableCollection<CharacterOption>  Characters { get; } = [];

    public ReactiveCommand<Unit, Unit>             AddCommand    { get; }
    public ReactiveCommand<CorpAltRow, Unit>       DeleteCommand { get; }

    public WorklistCorpAltsViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                     WorklistCorpAltService corpAlts)
    {
        _dbFactory = dbFactory;
        _corpAlts  = corpAlts;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<CorpAltRow>(async a =>
        {
            await _corpAlts.DeleteAsync(a.Id);
            await LoadAsync();
            if (CorpAltsChanged is not null) await CorpAltsChanged();
        });

        _ = LoadAsync();
    }

    public Func<Task>? CorpAltsChanged { get; set; }

    private CorpOption? _selectedCorp;
    public CorpOption? SelectedCorp { get => _selectedCorp; set => this.RaiseAndSetIfChanged(ref _selectedCorp, value); }

    private CharacterOption? _selectedCharacter;
    public CharacterOption? SelectedCharacter { get => _selectedCharacter; set => this.RaiseAndSetIfChanged(ref _selectedCharacter, value); }

    private string _note = "";
    public string Note { get => _note; set => this.RaiseAndSetIfChanged(ref _note, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var alts = await _corpAlts.GetAllAsync();

        var withProjects = await db.CorpStandingProjects.AsNoTracking()
            .GroupBy(p => p.CorporationId)
            .Select(g => new { CorpId = g.Key, Count = g.Count() })
            .ToListAsync();

        var corpIds   = withProjects.Select(w => w.CorpId).ToList();
        var corpNames = await db.Corporations.AsNoTracking()
            .Where(c => corpIds.Contains(c.Id))
            .ToDictionaryAsync(c => (long)c.Id, c => c.Name);

        var chars = await db.Characters.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync();

        var charOptions = chars.Select(c => new CharacterOption(c.Id, c.Name)).ToList();

        var unassigned = withProjects.Count(w => alts.All(a => a.CorporationId != w.CorpId));
        var totalDefs  = withProjects.Sum(w => w.Count);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Characters first: the rows carry one each and the grid combo binds its ItemsSource
            // here, so a row realised against an empty list draws blank and pushes null back.
            Characters.Clear();
            foreach (var c in charOptions) Characters.Add(c);

            Alts.Clear();
            foreach (var a in alts)
                Alts.Add(new CorpAltRow(a, charOptions.FirstOrDefault(o => o.Id == a.CharacterId),
                                        Characters, SaveRowAsync));

            Corps.Clear();
            foreach (var w in withProjects.OrderBy(w => corpNames.GetValueOrDefault(w.CorpId, "")))
                Corps.Add(new CorpOption(w.CorpId, corpNames.GetValueOrDefault(w.CorpId, $"Corp {w.CorpId}")));

            Status = withProjects.Count == 0
                ? "No standing projects defined yet — set them up in Corp Activity first."
                : $"{totalDefs:N0} standing project(s) across {withProjects.Count} corporation(s)"
                  + (unassigned > 0
                       ? $" · {unassigned} corporation(s) unassigned, so their items show as blocked"
                       : "");
        });
    }

    /// <summary>
    /// Writes one edited row back, a short pause after the last keystroke — the note would
    /// otherwise save on every character typed. Per row, so one edit cannot cancel another's
    /// pending write. No reload, which would replace the row under the cursor mid-edit.
    /// </summary>
    private readonly Dictionary<int, CancellationTokenSource> _pendingSaves = [];

    private async Task SaveRowAsync(CorpAltRow row)
    {
        if (_pendingSaves.Remove(row.Id, out var inFlight)) inFlight.Cancel();
        var cts = _pendingSaves[row.Id] = new CancellationTokenSource();

        try { await Task.Delay(500, cts.Token); }
        catch (OperationCanceledException) { return; }
        finally { if (ReferenceEquals(_pendingSaves.GetValueOrDefault(row.Id), cts)) _pendingSaves.Remove(row.Id); }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistCorpAlts
                .Where(x => x.Id == row.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CharacterId,   row.Alt.CharacterId)
                    .SetProperty(x => x.CharacterName, row.Alt.CharacterName)
                    .SetProperty(x => x.Note,          row.Alt.Note));

            Status = "Saved.";
            if (CorpAltsChanged is not null) await CorpAltsChanged();
        }
        catch (Exception ex)
        {
            Status = $"Could not save that change: {ex.Message}";
        }
    }

    private async Task AddAsync()
    {
        if (SelectedCorp is null)      { Status = "Pick a corporation."; return; }
        if (SelectedCharacter is null) { Status = "Pick the character who maintains its projects."; return; }

        await _corpAlts.SaveAsync(new WorklistCorpAlt
        {
            CorporationId   = SelectedCorp.Id,
            CorporationName = SelectedCorp.Name,
            CharacterId     = SelectedCharacter.Id,
            CharacterName   = SelectedCharacter.Name,
            Note            = Note,
        });

        Note = "";
        await LoadAsync();
        if (CorpAltsChanged is not null) await CorpAltsChanged();
    }
}
