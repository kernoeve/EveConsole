using System.Reactive;
using ReactiveUI;

namespace EveConsole.ViewModels;

// A trusted ORDER BY expression paired with a display label, for server-side sort combos.
public class GridSortOption
{
    public string Label { get; }
    public string Sql   { get; }
    public GridSortOption(string label, string sql) { Label = label; Sql = sql; }
    public override string ToString() => Label;
}

// Reusable numbered-page state for a DB-backed grid. The owner supplies a reload callback that
// re-runs the page query; this object owns the page number, total count and nav commands.
public class GridPager : ReactiveObject
{
    public const int PageSize = 200;

    private readonly Func<Task> _reload;

    public GridPager(Func<Task> reload)
    {
        _reload = reload;
        FirstPageCommand = ReactiveCommand.Create(() => Go(1));
        PrevPageCommand  = ReactiveCommand.Create(() => Go(CurrentPage - 1));
        NextPageCommand  = ReactiveCommand.Create(() => Go(CurrentPage + 1));
        LastPageCommand  = ReactiveCommand.Create(() => Go(TotalPages));
    }

    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        private set { this.RaiseAndSetIfChanged(ref _currentPage, value); RaisePaging(); }
    }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        set { this.RaiseAndSetIfChanged(ref _totalCount, value); RaisePaging(); }
    }

    public int  Offset     => (CurrentPage - 1) * PageSize;
    public int  TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool CanPrev    => CurrentPage > 1;
    public bool CanNext    => CurrentPage < TotalPages;
    public string PageInfo => TotalCount == 0
        ? "No results"
        : $"Page {CurrentPage:N0} of {TotalPages:N0}  ·  {TotalCount:N0}";

    public ReactiveCommand<Unit, Unit> FirstPageCommand { get; }
    public ReactiveCommand<Unit, Unit> PrevPageCommand  { get; }
    public ReactiveCommand<Unit, Unit> NextPageCommand  { get; }
    public ReactiveCommand<Unit, Unit> LastPageCommand  { get; }

    // Jump to page 1 without reloading — the caller reloads (used on filter/sort change).
    public void Reset()
    {
        if (_currentPage != 1) { _currentPage = 1; this.RaisePropertyChanged(nameof(CurrentPage)); }
        RaisePaging();
    }

    // Pull CurrentPage back into range after a fresh count (call before reading Offset).
    public void ClampToRange()
    {
        int clamped = Math.Clamp(_currentPage, 1, TotalPages);
        if (clamped != _currentPage) { _currentPage = clamped; this.RaisePropertyChanged(nameof(CurrentPage)); RaisePaging(); }
    }

    private void Go(int page)
    {
        int target = Math.Clamp(page, 1, TotalPages);
        if (target == _currentPage) return;
        CurrentPage = target;
        _ = _reload();
    }

    private void RaisePaging()
    {
        this.RaisePropertyChanged(nameof(TotalPages));
        this.RaisePropertyChanged(nameof(CanPrev));
        this.RaisePropertyChanged(nameof(CanNext));
        this.RaisePropertyChanged(nameof(PageInfo));
        this.RaisePropertyChanged(nameof(Offset));
    }
}
