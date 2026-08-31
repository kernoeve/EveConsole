using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

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

/// <summary>One list's heading, as the reader may have renamed it.</summary>
public sealed class Top10TitleRowVm : ReactiveObject
{
    public string Group        { get; }
    public string Key          { get; }
    public string DefaultTitle { get; }

    private string _title;

    /// <summary>Empty means the built-in heading. Held as typed and trimmed on save, so a
    /// half-typed name is not treated as a clearing.</summary>
    public string Title { get => _title; set => this.RaiseAndSetIfChanged(ref _title, value); }

    public Top10TitleRowVm(string group, string key, string defaultTitle, string current)
    {
        Group        = group;
        Key          = key;
        DefaultTitle = defaultTitle;
        _title       = current;
    }
}

public sealed class CorpTop10SettingsViewModel : ReactiveObject
{
    private readonly CorpTop10ExcludeService _svc;
    private readonly CorpReportTitles         _titles;

    public ObservableCollection<CorpTop10ExcludeRowVm> Excludes { get; } = [];

    /// <summary>The five Top 10 headings, each overridable.</summary>
    public ObservableCollection<Top10TitleRowVm> Titles { get; } = [];

    /// <summary>The monthly summary's seven section headings.</summary>
    public ObservableCollection<Top10TitleRowVm> SummaryTitles { get; } = [];

    private string _headerPrefix = "";

    /// <summary>Put in front of the summary's first line. Empty leaves it alone.</summary>
    public string HeaderPrefix
    {
        get => _headerPrefix;
        set => this.RaiseAndSetIfChanged(ref _headerPrefix, value);
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    private CorpTop10ExcludeRowVm? _searchMatch;

    /// <summary>The row the box is sitting on. Adding takes this rather than the typed
    /// text, so an id is never guessed from a name somebody half-typed.</summary>
    public CorpTop10ExcludeRowVm? SearchMatch
    {
        get => _searchMatch;
        set => this.RaiseAndSetIfChanged(ref _searchMatch, value);
    }

    /// <summary>
    /// Names matching what has been typed.
    ///
    /// <para>⚠️ AsyncPopulator with FilterMode None, the same shape the store's sender box
    /// uses. The search has already narrowed the list; letting the box filter again would
    /// drop matches it never received, and handing it the whole name cache would lay out
    /// hundreds of thousands of rows.</para>
    /// </summary>
    public Func<string?, System.Threading.CancellationToken, Task<IEnumerable<object>>> SearchPopulator =>
        async (text, ct) =>
        {
            var needle = (text ?? "").Trim();
            if (needle.Length < 2) return [];

            var hits = await _svc.SearchAsync(needle, EntityType, ct);
            return hits.Select(h => new CorpTop10ExcludeRowVm(h)).ToList();
        };

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

    public ReactiveCommand<Unit, Unit>                 AddCommand       { get; }
    public ReactiveCommand<CorpTop10ExcludeRowVm, Unit> RemoveCommand    { get; }
    public ReactiveCommand<Unit, Unit>                 SaveTitlesCommand { get; }

    public CorpTop10SettingsViewModel(CorpTop10ExcludeService svc, CorpReportTitles titles)
    {
        _svc    = svc;
        _titles = titles;

        AddCommand        = ReactiveCommand.CreateFromTask(AddSelectedAsync);
        RemoveCommand     = ReactiveCommand.CreateFromTask<CorpTop10ExcludeRowVm>(RemoveAsync);
        SaveTitlesCommand = ReactiveCommand.CreateFromTask(SaveTitlesAsync);

        AddCommand       .ThrownExceptions.Subscribe(ex => StatusText = $"Add error: {ex.Message}");
        RemoveCommand    .ThrownExceptions.Subscribe(ex => StatusText = $"Remove error: {ex.Message}");
        SaveTitlesCommand.ThrownExceptions.Subscribe(ex => StatusText = $"Save error: {ex.Message}");
    }

    public void Load()
    {
        Excludes.Clear();
        foreach (var e in _svc.GetAll())
            Excludes.Add(new CorpTop10ExcludeRowVm(e));

        Titles.Clear();
        foreach (var (key, title) in CorpReportTitles.Top10Categories)
            Titles.Add(new Top10TitleRowVm(
                CorpReportTitles.Top10Group, key, title,
                _titles.Override(CorpReportTitles.Top10Group, key)));

        SummaryTitles.Clear();
        foreach (var (key, title) in CorpReportTitles.SummarySections)
            SummaryTitles.Add(new Top10TitleRowVm(
                CorpReportTitles.SummaryGroup, key, title,
                _titles.Override(CorpReportTitles.SummaryGroup, key)));

        HeaderPrefix = _titles.HeaderPrefix;
    }

    private async Task SaveTitlesAsync(System.Threading.CancellationToken ct = default)
    {
        foreach (var t in Titles.Concat(SummaryTitles))
            await _titles.SetOverrideAsync(t.Group, t.Key, t.Title);

        await _titles.SetHeaderPrefixAsync(HeaderPrefix);
        StatusText = "Titles saved.";
    }

    /// <summary>Adds whatever the box is sitting on. ⚠️ The SELECTED row, never the typed
    /// text: two corporations can share the opening of a name, and an exclusion aimed at the
    /// wrong id hides the wrong entity silently.</summary>
    private async Task AddSelectedAsync(CancellationToken ct = default)
    {
        if (SearchMatch is not { } row) { StatusText = "Pick a name from the list first."; return; }

        await AddAsync(row, ct);
        SearchMatch = null;
        SearchText  = "";
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
