using System.Reactive.Disposables;
using EveConsole.Services;

namespace EveConsole.ViewModels;

/// <summary>
/// What a tool keeps running for as long as it's open: its clocks, and what it follows in the app's
/// services. The app's own copy of a tool lives as long as the app, so nothing here is ever undone
/// for it; another copy undoes all of it when its tab closes (<see cref="Dispose"/>), or it would go
/// on refreshing for nobody, held by the services it follows.
/// </summary>
public sealed class ToolLife : IDisposable
{
    private readonly CompositeDisposable _all = [];

    /// <summary>A subscription, such as a clock, ended with the tool.</summary>
    public IDisposable Keep(IDisposable subscription)
    {
        _all.Add(subscription);
        return subscription;
    }

    /// <summary>Something followed through an event: <paramref name="unfollow"/> runs with the tool's end.</summary>
    public void Follow(Action follow, Action unfollow)
    {
        follow();
        _all.Add(Disposable.Create(unfollow));
    }

    /// <summary>Characters or corporations added or removed (<see cref="OwnerList.Changed"/>).</summary>
    public void OnOwnersChanged(Action changed) =>
        Follow(() => OwnerList.Changed += changed, () => OwnerList.Changed -= changed);

    public void Dispose() => _all.Dispose();
}
