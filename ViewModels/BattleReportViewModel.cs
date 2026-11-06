using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Battle;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One saved battle in the list.</summary>
public sealed class SavedBattleVm : ReactiveObject
{
    public SavedBattleVm(SavedBattleRow row, Action<SavedBattleVm> open, Action<SavedBattleVm> delete)
    {
        Row = row;
        OpenCommand   = ReactiveCommand.Create(() => open(this));
        DeleteCommand = ReactiveCommand.Create(() => delete(this));
    }

    public SavedBattleRow Row { get; }
    public int    Id   => Row.Id;
    public string Name => Row.Name;

    /// <summary>How many systems, and when it starts.</summary>
    public string Summary
    {
        get
        {
            var windows = Row.Definition.Windows;
            if (windows.Count == 0) return "";
            var start = windows.Min(w => w.From).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return string.Format(BattleText.SavedSummary, windows.Select(w => w.SystemId).Distinct().Count(), start);
        }
    }

    /// <summary>Any of it is still open, so it could be watched live.</summary>
    public bool IsOpenEnded => Row.Definition.Windows.Any(w => w.IsOpenAt(DateTimeOffset.UtcNow));

    public ReactiveCommand<Unit, Unit> OpenCommand   { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
}

/// <summary>
/// The Battle Report tool: the battles the user has defined and kept, and those open now, as many as
/// they like, each with its own switch for watching live.
///
/// <para>Only this user's screen: nothing here is published anywhere.</para>
/// </summary>
public sealed class BattleReportViewModel : ReactiveObject
{
    private readonly BattleReportService _service;
    private readonly Func<IReadOnlyList<long>, Task<Dictionary<long, string>>> _nameLookup;
    private readonly ZkillboardSettings _zkb;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public BattleReportViewModel(BattleReportService service, Func<IReadOnlyList<long>, Task<Dictionary<long, string>>> nameLookup,
                                 ZkillboardSettings zkb, IDbContextFactory<AppDbContext> dbFactory)
    {
        _service    = service;
        _nameLookup = nameLookup;
        _zkb        = zkb;
        _dbFactory  = dbFactory;
        NewCommand  = ReactiveCommand.Create(() => { Selected = NewSession(); });
        ToggleFinderCommand = ReactiveCommand.Create(() => { ShowFinder = !ShowFinder; });
        Finder = new BattleFinderViewModel(service, nameLookup, zkb, dbFactory, OpenFound);
    }

    public ObservableCollection<SavedBattleVm> Saved { get; } = [];
    public ObservableCollection<BattleSessionViewModel> Open { get; } = [];

    private BattleSessionViewModel? _selected;
    public BattleSessionViewModel? Selected
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(HasOpen));
            this.RaisePropertyChanged(nameof(ShowTabs));
            this.RaisePropertyChanged(nameof(ShowEmpty));
        }
    }

    public bool HasOpen => Selected is not null;

    private bool _hasSaved;
    public bool NoneSaved { get => !_hasSaved; private set { _hasSaved = !value; this.RaisePropertyChanged(); } }

    private string _problem = "";
    public string Problem { get => _problem; private set => this.RaiseAndSetIfChanged(ref _problem, value); }

    public ReactiveCommand<Unit, Unit> NewCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFinderCommand { get; }

    /// <summary>Looking for large fights to start a battle from.</summary>
    public BattleFinderViewModel Finder { get; }

    private bool _showFinder;
    public bool ShowFinder
    {
        get => _showFinder;
        set
        {
            this.RaiseAndSetIfChanged(ref _showFinder, value);
            this.RaisePropertyChanged(nameof(ShowTabs));
            this.RaisePropertyChanged(nameof(ShowEmpty));
        }
    }

    public bool ShowTabs  => !ShowFinder && HasOpen;
    public bool ShowEmpty => !ShowFinder && !HasOpen;

    /// <summary>A fight found becomes a new battle, its systems and times filled in to adjust.</summary>
    private void OpenFound(FoundFight fight)
    {
        var session = NewSession();
        Selected = session;
        _ = LoadFoundAsync(session, fight);
    }

    private async Task LoadFoundAsync(BattleSessionViewModel session, FoundFight fight)
    {
        try { await session.LoadSuggestedAsync(fight.Windows); }
        catch (Exception ex) { Problem = string.Format(BattleText.ReadFailed, ex.Message); }
    }

    /// <summary>Asks before a saved battle is forgotten. Set by the view, which owns the window.</summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>The saved list, read again each time the tool is shown: another machine on the same
    /// server may have changed it.</summary>
    public async Task InitializeAsync()
    {
        try { await RefreshSavedAsync(); }
        catch (Exception ex) { Problem = string.Format(BattleText.ReadFailed, ex.Message); }
    }

    private async Task RefreshSavedAsync()
    {
        var rows = await Task.Run(() => _service.ListSavedAsync());
        Saved.Clear();
        foreach (var row in rows) Saved.Add(new SavedBattleVm(row, OpenSaved, DeleteSaved));
        NoneSaved = Saved.Count == 0;
    }

    private BattleSessionViewModel NewSession()
    {
        var session = new BattleSessionViewModel(_service, _nameLookup, _zkb, _dbFactory, SaveAsync, Close);
        Open.Add(session);
        ShowFinder = false;
        return session;
    }

    private void OpenSaved(SavedBattleVm saved)
    {
        // Already open: go to it. A second copy would have two tabs saving over each other.
        if (Open.FirstOrDefault(s => s.SavedId == saved.Id) is { } open) { Selected = open; ShowFinder = false; return; }

        var session = NewSession();
        Selected = session;
        _ = LoadAsync(session, saved.Row);
    }

    private async Task LoadAsync(BattleSessionViewModel session, SavedBattleRow row)
    {
        try { await session.LoadAsync(row); }
        catch (Exception ex) { Problem = string.Format(BattleText.ReadFailed, ex.Message); }
    }

    /// <summary>A new battle, on the fight around one kill.</summary>
    public async Task OpenFromKillAsync(int killMailId)
    {
        var session = NewSession();
        Selected = session;
        await session.OpenFromKillAsync(killMailId);
    }

    /// <summary>Closes a battle's tab, ending its watching. A saved one stays in the list.</summary>
    private void Close(BattleSessionViewModel session)
    {
        session.Shut();
        var index = Open.IndexOf(session);
        Open.Remove(session);
        if (ReferenceEquals(Selected, session))
            Selected = Open.Count == 0 ? null : Open[Math.Min(index, Open.Count - 1)];
    }

    /// <summary>Keeps a battle, a new row the first time and that row afterwards.</summary>
    private async Task SaveAsync(BattleSessionViewModel session)
    {
        var name = session.Title;
        var definition = session.Definition();
        var id = await Task.Run(() => _service.SaveBattleAsync(session.SavedId, name, definition));
        if (session.SavedId != id) session.SavedId = id;
        await RefreshSavedAsync();
    }

    private async void DeleteSaved(SavedBattleVm saved)
    {
        try
        {
            if (ConfirmDelete is { } confirm && !await confirm(string.Format(BattleText.ConfirmDelete, saved.Name))) return;

            await Task.Run(() => _service.DeleteBattleAsync(saved.Id));

            // A tab still showing it stays open, no longer saved.
            foreach (var session in Open.Where(s => s.SavedId == saved.Id)) session.SavedId = null;
            await RefreshSavedAsync();
        }
        catch (Exception ex) { Problem = string.Format(BattleText.ReadFailed, ex.Message); }
    }
}
