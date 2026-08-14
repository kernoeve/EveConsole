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

    public ObservableCollection<WorklistCorpAlt>  Alts       { get; } = [];
    public ObservableCollection<CorpOption>       Corps      { get; } = [];
    public ObservableCollection<CharacterOption>  Characters { get; } = [];

    public ReactiveCommand<Unit, Unit>             AddCommand    { get; }
    public ReactiveCommand<WorklistCorpAlt, Unit>  DeleteCommand { get; }

    public WorklistCorpAltsViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                     WorklistCorpAltService corpAlts)
    {
        _dbFactory = dbFactory;
        _corpAlts  = corpAlts;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<WorklistCorpAlt>(async a =>
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

        var unassigned = withProjects.Count(w => alts.All(a => a.CorporationId != w.CorpId));
        var totalDefs  = withProjects.Sum(w => w.Count);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Alts.Clear();
            foreach (var a in alts) Alts.Add(a);

            Corps.Clear();
            foreach (var w in withProjects.OrderBy(w => corpNames.GetValueOrDefault(w.CorpId, "")))
                Corps.Add(new CorpOption(w.CorpId, corpNames.GetValueOrDefault(w.CorpId, $"Corp {w.CorpId}")));

            Characters.Clear();
            foreach (var c in chars) Characters.Add(new CharacterOption(c.Id, c.Name));

            Status = withProjects.Count == 0
                ? "No standing projects defined yet — set them up in Corp Activity first."
                : $"{totalDefs:N0} standing project(s) across {withProjects.Count} corporation(s)"
                  + (unassigned > 0
                       ? $" · {unassigned} corporation(s) unassigned, so their items show as blocked"
                       : "");
        });
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
