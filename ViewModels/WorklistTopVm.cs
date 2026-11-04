using System.Reactive;
using Avalonia.Media;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A figure at the top of the Worklist, which narrows the list to what it counts.</summary>
public sealed class TopStatVm(string key, string label, int count, Action<string> pick) : ReactiveObject
{
    public string Key   => key;
    public string Label => label;
    public string Value => count.ToString("N0");

    public ReactiveCommand<Unit, Unit> Pick { get; } = ReactiveCommand.Create(() => pick(key));

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }
}

/// <summary>One question the summary answers: its name, how many, and how those divide.</summary>
public sealed record TopGroupVm(string Title, TopStatVm Headline, IBrush HeadlineColor, IReadOnlyList<TopStatVm> Parts);

/// <summary>How full one kind of slot is, which is what decides whether the rest can start at all.</summary>
public sealed record TopSlotVm(TopStatVm Stat, IBrush Color);

/// <summary>A task from the top of the plan: what it is, where, and what it restarts.</summary>
public sealed class NextMoveVm(WorklistRowVm row, string verb, string line, string frees, Action<string> go)
{
    public WorklistRowVm Row => row;
    public string Verb  => verb;
    public string Line  => line;
    public string Frees => frees;

    public ReactiveCommand<Unit, Unit> Go { get; } = ReactiveCommand.Create(() => go(row.Key));
}
