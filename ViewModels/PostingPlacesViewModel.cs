using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Fitting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One step of the places tree: a station, an owner there, a hangar, a container.</summary>
public sealed class PlaceNodeVm : ReactiveObject
{
    public PlaceNodeVm(PostingLocations.Node node, PlaceNodeVm? parent)
    {
        Node   = node;
        Parent = parent;
        _label = node.Label;
        foreach (var child in node.Children) Children.Add(new PlaceNodeVm(child, this));
    }

    public PostingLocations.Node Node { get; }
    public PlaceNodeVm? Parent { get; }
    public ObservableCollection<PlaceNodeVm> Children { get; } = [];

    private string _label;
    public string Label { get => _label; set => this.RaiseAndSetIfChanged(ref _label, value); }

    /// <summary>The whole way to it, "Jita IV - Moon 4 › Some Corp › Ships › Blue box".</summary>
    public string Path => Parent is null ? Label : $"{Parent.Path} › {Label}";

    public IEnumerable<PlaceNodeVm> Self() => Children.SelectMany(c => c.Self()).Prepend(this);
}

/// <summary>A place picked, in the list beside the tree.</summary>
public sealed class PlaceChoiceVm(SalePostingLocation model)
{
    public SalePostingLocation Model { get; } = model;
    public string Path => Model.Path;

    /// <summary>Two picks of the same place are one.</summary>
    public bool Same(SalePostingLocation other) =>
        Model.StationId == other.StationId && Model.OwnerType == other.OwnerType && Model.OwnerId == other.OwnerId
        && Model.Flag == other.Flag && Model.ContainerId == other.ContainerId;
}

/// <summary>
/// The places a posting lists whatever is found in, picked from a tree of where our things are:
/// a station or structure, an owner there, a hangar or corporation division, and the containers
/// in it at any depth. Any step can be picked; ships are not offered. Saved by the caller when
/// the dialog closes with them.
/// </summary>
public sealed class PostingPlacesViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EsiClient? _esi;

    public PostingPlacesViewModel(IDbContextFactory<AppDbContext> dbFactory, EsiClient? esi, IReadOnlyList<SalePostingLocation> current)
    {
        _dbFactory = dbFactory;
        _esi       = esi;
        foreach (var l in current) Chosen.Add(new PlaceChoiceVm(Copy(l)));

        AddCommand = ReactiveCommand.Create(Add, this.WhenAnyValue(v => v.Selected).Select(s => s is not null));
        _ = LoadAsync();
    }

    public ObservableCollection<PlaceNodeVm>  Nodes  { get; } = [];
    public ObservableCollection<PlaceChoiceVm> Chosen { get; } = [];

    // Every station, the filter's source: there are hundreds once a corporation's offices count.
    private List<PlaceNodeVm> _all = [];

    private string _filter = "";
    /// <summary>Narrows the stations to those whose name, or an owner's, hangar's or container's
    /// below them, has these words.</summary>
    public string Filter
    {
        get => _filter;
        set { this.RaiseAndSetIfChanged(ref _filter, value); ApplyFilter(); }
    }

    private void ApplyFilter()
    {
        var words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = words.Length == 0 ? _all
            : _all.Where(s => s.Self().Any(n => words.All(w => n.Label.Contains(w, StringComparison.CurrentCultureIgnoreCase)))).ToList();
        // Replaced in one go: the list can be hundreds long.
        Nodes.Clear();
        foreach (var n in shown) Nodes.Add(n);
    }

    private PlaceNodeVm? _selected;
    public PlaceNodeVm? Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }

    private string _status = CommonText.Loading;
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }

    /// <summary>The picked node into the list, unless it is there already.</summary>
    public void Add()
    {
        if (Selected is not { } node) return;
        var place = Copy(node.Node.Place);
        place.Path = node.Path;
        if (Chosen.Any(c => c.Same(place))) return;
        Chosen.Add(new PlaceChoiceVm(place));
    }

    public void Remove(PlaceChoiceVm choice) => Chosen.Remove(choice);

    public void Move(PlaceChoiceVm choice, int by)
    {
        var from = Chosen.IndexOf(choice);
        var to   = from + by;
        if (from >= 0 && to >= 0 && to < Chosen.Count) Chosen.Move(from, to);
    }

    /// <summary>What the dialog closes with: the places in their order, titles kept.</summary>
    public List<SalePostingLocation> Result() => Chosen.Select(c => c.Model).ToList();

    private async Task LoadAsync()
    {
        try
        {
            List<PostingLocations.Node> tree;
            await using (var db = await _dbFactory.CreateDbContextAsync())
                tree = await PostingLocations.TreeAsync(db);

            var nodes = tree.Select(n => new PlaceNodeVm(n, null)).ToList();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _all = nodes;
                ApplyFilter();
                Status = nodes.Count == 0 ? SalesText.PlacesNone : "";
            });

            // The names owners gave their containers, asked of ESI once a session (as the fitting
            // tool asks for ships'): "Blue box" says more than "Station Container".
            var boxes = nodes.SelectMany(n => n.Self()).Where(n => n.Node.Kind == "container").ToList();
            if (boxes.Count == 0 || _esi is null) return;
            var names = await ExistingShips.NamesAsync(_esi,
                boxes.Select(b => (b.Node.OwnerId, b.Node.OwnerIsCorporation, b.Node.ItemId)).ToList(), CancellationToken.None);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var b in boxes)
                    if (names.TryGetValue(b.Node.ItemId, out var name) && name.Length > 0)
                        b.Label = string.Format(SalesText.PlacesNamedContainer, name, b.Node.Label);
            });
        }
        catch (Exception ex)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                Status = string.Format(SalesText.PlacesLoadFailed, ex.Message));
        }
    }

    private static SalePostingLocation Copy(SalePostingLocation l) => new()
    {
        StationId = l.StationId, OwnerType = l.OwnerType, OwnerId = l.OwnerId, Flag = l.Flag,
        ContainerId = l.ContainerId, Path = l.Path, Title = l.Title,
    };
}
