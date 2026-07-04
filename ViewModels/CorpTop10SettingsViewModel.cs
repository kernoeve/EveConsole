using System.Collections.ObjectModel;
using System.Reactive;
using EveCortex.Models;
using EveCortex.Services;
using ReactiveUI;

namespace EveCortex.ViewModels;

public sealed class CorpTop10ExcludeRowVm : ReactiveObject
{
    public long   EntityId   { get; }
    public string EntityType { get; }
    public string EntityName { get; }
    public string Display    => $"{EntityName}  ({EntityType})";

    public CorpTop10ExcludeRowVm(CorpTop10Exclude e)
    {
        EntityId   = e.EntityId;
        EntityType = e.EntityType;
        EntityName = e.EntityName;
    }
}

public sealed class CorpTop10SettingsViewModel : ReactiveObject
{
    private readonly CorpTop10ExcludeService _svc;

    public ObservableCollection<CorpTop10ExcludeRowVm> Excludes       { get; } = [];
    public ObservableCollection<CorpTop10ExcludeRowVm> SearchResults  { get; } = [];

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    private string _entityType = "character";
    public string EntityType
    {
        get => _entityType;
        set => this.RaiseAndSetIfChanged(ref _entityType, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public IReadOnlyList<string> EntityTypes { get; } = ["character", "corporation"];

    public ReactiveCommand<Unit, Unit>               SearchCommand { get; }
    public ReactiveCommand<CorpTop10ExcludeRowVm, Unit> AddCommand    { get; }
    public ReactiveCommand<CorpTop10ExcludeRowVm, Unit> RemoveCommand { get; }

    public CorpTop10SettingsViewModel(CorpTop10ExcludeService svc)
    {
        _svc = svc;

        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);
        AddCommand    = ReactiveCommand.CreateFromTask<CorpTop10ExcludeRowVm>(AddAsync);
        RemoveCommand = ReactiveCommand.CreateFromTask<CorpTop10ExcludeRowVm>(RemoveAsync);

        SearchCommand.ThrownExceptions.Subscribe(ex => StatusText = $"Search error: {ex.Message}");
        AddCommand   .ThrownExceptions.Subscribe(ex => StatusText = $"Add error: {ex.Message}");
        RemoveCommand.ThrownExceptions.Subscribe(ex => StatusText = $"Remove error: {ex.Message}");
    }

    public void Load()
    {
        Excludes.Clear();
        foreach (var e in _svc.GetAll())
            Excludes.Add(new CorpTop10ExcludeRowVm(e));
    }

    private async Task SearchAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        var results = await _svc.SearchAsync(SearchText.Trim(), EntityType, ct);
        SearchResults.Clear();
        foreach (var r in results)
            SearchResults.Add(new CorpTop10ExcludeRowVm(r));
        StatusText = results.Count == 0 ? "No results found." : "";
    }

    private async Task AddAsync(CorpTop10ExcludeRowVm row, CancellationToken ct = default)
    {
        // Don't add duplicates
        if (Excludes.Any(e => e.EntityId == row.EntityId && e.EntityType == row.EntityType))
        {
            StatusText = $"{row.EntityName} is already in the exclude list.";
            return;
        }
        await _svc.AddAsync(row.EntityId, row.EntityType, row.EntityName, ct);
        Excludes.Add(new CorpTop10ExcludeRowVm(
            new CorpTop10Exclude { EntityId = row.EntityId, EntityType = row.EntityType, EntityName = row.EntityName }));
        // Sort by name
        var sorted = Excludes.OrderBy(e => e.EntityName).ToList();
        Excludes.Clear();
        foreach (var e in sorted) Excludes.Add(e);
        StatusText = $"Added {row.EntityName} to exclude list.";
    }

    private async Task RemoveAsync(CorpTop10ExcludeRowVm row, CancellationToken ct = default)
    {
        await _svc.RemoveAsync(row.EntityId, row.EntityType, ct);
        var match = Excludes.FirstOrDefault(e => e.EntityId == row.EntityId && e.EntityType == row.EntityType);
        if (match is not null) Excludes.Remove(match);
        StatusText = $"Removed {row.EntityName} from exclude list.";
    }
}
