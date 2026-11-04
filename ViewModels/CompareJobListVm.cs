using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One line of the comparison's job list: an item or a blueprint, what to do with it, and that
/// operation's numbers. A blueprint offers building what it makes, copying, invention (when it
/// can be invented from) and ME and TE research; an item offers building only.
/// </summary>
public sealed class CompareJobVm : ReactiveObject
{
    private readonly Action _changed;

    public CompareJobVm(int typeId, string typeName, bool isBlueprint, bool inventable, CompareOp op,
                        long quantity, int second, int decryptorTypeId,
                        ObservableCollection<LabelledChoice> decryptors, Action<CompareJobVm> remove,
                        Action<CompareJobVm, int> move, Action changed)
    {
        TypeId      = typeId;
        TypeName    = typeName;
        IsBlueprint = isBlueprint;
        Inventable  = inventable;
        Decryptors  = decryptors;
        _changed    = changed;

        Operations = [new(nameof(CompareOp.Build), IndustryText.CompareOpBuild)];
        if (isBlueprint)
        {
            Operations.Add(new(nameof(CompareOp.Copy), IndustryText.CompareOpCopy));
            if (inventable) Operations.Add(new(nameof(CompareOp.Invention), IndustryText.CompareOpInvention));
            Operations.Add(new(nameof(CompareOp.MeResearch), IndustryText.CompareOpMeResearch));
            Operations.Add(new(nameof(CompareOp.TeResearch), IndustryText.CompareOpTeResearch));
        }
        _operation   = Operations.FirstOrDefault(o => o.Key == op.ToString()) ?? Operations[0];
        _quantity    = Math.Max(1, quantity).ToString();
        _second      = second.ToString();
        _decryptorId = decryptorTypeId;

        RemoveCommand   = ReactiveCommand.Create(() => remove(this));
        MoveUpCommand   = ReactiveCommand.Create(() => move(this, -1));
        MoveDownCommand = ReactiveCommand.Create(() => move(this, +1));
        this.WhenAnyValue(x => x.QuantityText, x => x.SecondText).Skip(1).Subscribe(_ => changed());
    }

    public int    TypeId      { get; }
    public string TypeName    { get; }
    public bool   IsBlueprint { get; }
    public bool   Inventable  { get; }
    public string DisplayName => SdeNames.Type(TypeId, TypeName);

    public List<LabelledChoice> Operations { get; }

    private LabelledChoice _operation;
    /// <summary>What to do. Changing it fills the boxes with that operation's starting numbers.</summary>
    public LabelledChoice Operation
    {
        get => _operation;
        set
        {
            if (value is null || value.Key == _operation.Key) return;
            this.RaiseAndSetIfChanged(ref _operation, value);
            (QuantityText, SecondText) = Op switch
            {
                CompareOp.Copy       => ("10", "1"),
                CompareOp.Invention  => ("10", "0"),
                CompareOp.MeResearch => ("1", "10"),
                CompareOp.TeResearch => ("1", "20"),
                _                    => ("1", "10"),
            };
            foreach (var n in new[] { nameof(Op), nameof(ShowQuantity), nameof(ShowSecond), nameof(ShowDecryptor),
                                      nameof(QuantityLabel), nameof(SecondLabel), nameof(QuantityTip), nameof(SecondTip) })
                this.RaisePropertyChanged(n);
            _changed();
        }
    }

    public CompareOp Op => Enum.TryParse<CompareOp>(Operation.Key, out var op) ? op : CompareOp.Build;

    // The boxes hold what was typed; what they mean depends on the operation, and a word reads
    // as the operation's default.
    private string _quantity, _second;
    public string QuantityText { get => _quantity; set => this.RaiseAndSetIfChanged(ref _quantity, value); }
    public string SecondText   { get => _second;   set => this.RaiseAndSetIfChanged(ref _second, value); }

    public bool ShowQuantity  => Op is CompareOp.Build or CompareOp.Copy or CompareOp.Invention;
    public bool ShowSecond    => Op is not CompareOp.Invention;
    public bool ShowDecryptor => Op is CompareOp.Invention;

    public string QuantityLabel => Op switch
    {
        CompareOp.Copy      => IndustryText.CompareRunsPerCopy,
        CompareOp.Invention => IndustryText.CompareRunsWanted,
        _                   => IndustryText.CompareUnits,
    };
    public string SecondLabel => Op switch
    {
        CompareOp.Copy       => IndustryText.CompareCopies,
        CompareOp.MeResearch => IndustryText.CompareToMe,
        CompareOp.TeResearch => IndustryText.CompareToTe,
        _                    => "ME",
    };
    public string QuantityTip => Op switch
    {
        CompareOp.Copy      => IndustryText.TipCompareRunsPerCopy,
        CompareOp.Invention => IndustryText.TipCompareRunsWanted,
        _                   => IndustryText.TipCompareUnits,
    };
    public string SecondTip => Op switch
    {
        CompareOp.Copy       => IndustryText.TipCompareCopies,
        CompareOp.MeResearch => IndustryText.TipCompareToMe,
        CompareOp.TeResearch => IndustryText.TipCompareToTe,
        _                    => IndustryText.TipCompareMe,
    };

    /// <summary>"No decryptor", then every decryptor; shared by every line.</summary>
    public ObservableCollection<LabelledChoice> Decryptors { get; }

    private int _decryptorId;
    public LabelledChoice? Decryptor
    {
        get => Decryptors.FirstOrDefault(d => d.Key == _decryptorId.ToString()) ?? Decryptors.FirstOrDefault();
        set
        {
            if (value is null) return;   // the list refilling, not a choice
            var id = int.TryParse(value.Key, out var v) ? v : 0;
            if (id == _decryptorId) return;
            _decryptorId = id;
            this.RaisePropertyChanged();
            _changed();
        }
    }

    /// <summary>Called when the decryptor list arrives, so a saved choice shows.</summary>
    public void DecryptorsLoaded() => this.RaisePropertyChanged(nameof(Decryptor));

    private static long Number(string text, long fallback) =>
        long.TryParse(text.Replace(",", "").Trim().TrimEnd('%'), out var v) && v > 0 ? v : fallback;

    public long Quantity => Number(QuantityText, Op is CompareOp.Copy or CompareOp.Invention ? 10 : 1);

    /// <summary>The second box: ME for a build, copies, the ME level, or the TE percent.</summary>
    public int Second => Op switch
    {
        CompareOp.Build      => int.TryParse(SecondText.Trim(), out var me) ? Math.Clamp(me, 0, 10) : 10,
        CompareOp.Copy       => (int)Math.Clamp(Number(SecondText, 1), 1, 1000),
        CompareOp.MeResearch => (int)Math.Clamp(Number(SecondText, 10), 1, 10),
        CompareOp.TeResearch => (int)Math.Clamp(Number(SecondText, 20), 2, 20),
        _                    => 0,
    };
    public int DecryptorTypeId => _decryptorId;

    /// <summary>The line as it is kept.</summary>
    public CompareJobSpec Spec => new(TypeId, TypeName, Quantity, Second, Op.ToString(), _decryptorId, IsBlueprint, Inventable);

    /// <summary>The line as the service takes it.</summary>
    public ParkCompareJob ToJob(int line) => Spec.ToJob(line);

    /// <summary>The line as the chart choice names it: the item, and the operation unless a build.</summary>
    public string LineLabel => Spec.Label;

    public ReactiveCommand<Unit, Unit> RemoveCommand   { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand   { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
}

/// <summary>
/// A Compare job list being edited: items and blueprints found by search, each line's operation
/// and numbers, moved and removed. The Compare tab keeps its list on this machine; a scheduled
/// post's section keeps its own in the task. Whoever holds the list saves it on
/// <see cref="ListChanged"/>, from <see cref="Specs"/>.
/// </summary>
public sealed class CompareJobListVm : ReactiveObject
{
    private readonly ParkComparisonService           _service;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public CompareJobListVm(ParkComparisonService service, IDbContextFactory<AppDbContext> dbFactory, IEnumerable<CompareJobSpec> jobs)
    {
        _service   = service;
        _dbFactory = dbFactory;

        Decryptors.Add(new LabelledChoice("0", IndustryText.CompareNoDecryptor));
        foreach (var j in jobs)
            Jobs.Add(NewJob(j.T, j.N, j.B, j.I, j.Operation, j.Q, j.Me, j.D));

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => _ = SearchAsync(t ?? ""));
    }

    /// <summary>Something in the list changed: a line added, removed, moved or edited.</summary>
    public event Action? ListChanged;

    public ObservableCollection<CompareJobVm> Jobs { get; } = [];

    /// <summary>The list as it is kept, in order.</summary>
    public List<CompareJobSpec> Specs => [.. Jobs.Select(j => j.Spec)];

    /// <summary>"No decryptor", then every decryptor with what it does; shared by every line.</summary>
    public ObservableCollection<LabelledChoice> Decryptors { get; } = [];

    private bool _decryptorsAsked;

    /// <summary>The decryptors, read the first time the list is shown.</summary>
    public async Task LoadDecryptorsAsync()
    {
        if (_decryptorsAsked) return;
        _decryptorsAsked = true;
        await SdeNames.EnsureLoadedAsync();
        var all = await Task.Run(() => _service.DecryptorsAsync());
        foreach (var d in all.Select(d => (d, Name: SdeNames.Type(d.TypeId, d.Name))).OrderBy(x => x.Name, StringComparer.CurrentCulture))
            Decryptors.Add(new LabelledChoice(d.d.TypeId.ToString(), string.Format(IndustryText.CompareDecryptorChoice,
                d.Name, d.d.ChanceMultiplier, d.d.RunModifier, d.d.MeModifier, d.d.TeModifier)));
        foreach (var j in Jobs) j.DecryptorsLoaded();
    }

    private void Changes() => ListChanged?.Invoke();

    private CompareJobVm NewJob(int typeId, string name, bool isBlueprint, bool inventable, CompareOp op, long quantity, int second, int decryptor) =>
        new(typeId, name, isBlueprint, inventable, op, quantity, second, decryptor, Decryptors,
            j => { Jobs.Remove(j); Changes(); }, Move, Changes);

    /// <summary>A line one place up (-1) or down (+1) the list; at either end it stays.</summary>
    private void Move(CompareJobVm job, int by)
    {
        var from = Jobs.IndexOf(job);
        var to   = from + by;
        if (from < 0 || to < 0 || to >= Jobs.Count) return;
        Jobs.Move(from, to);
        Changes();
    }

    /// <summary>A search result picked: added to the list, after the click that picked it.</summary>
    public ProdTypeSearchResult? PickedResult
    {
        get => null;
        set { if (value is { } pick) Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = AddAsync(pick)); }
    }

    /// <summary>
    /// Adds a line. A blueprint starts on copying, an item on building. The same blueprint may
    /// be listed more than once, each line its own operation.
    /// </summary>
    public async Task AddAsync(ProdTypeSearchResult pick)
    {
        SearchText = "";
        var bps  = await Task.Run(() => _service.BlueprintsAsync([pick.TypeId]));
        var isBp = bps.TryGetValue(pick.TypeId, out var bp);
        if (!isBp && Jobs.Any(j => j.TypeId == pick.TypeId)) return;
        Jobs.Add(NewJob(pick.TypeId, pick.TypeName, isBp, bp.Inventable,
                        isBp ? CompareOp.Copy : CompareOp.Build, isBp ? 10 : 1, isBp ? 1 : 10, 0));
        Changes();
    }

    // ── Search: what can be built, and blueprints ────────────────────────────

    private string _searchText = "";
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    public ObservableCollection<ProdTypeSearchResult> SearchResults { get; } = [];

    private bool _showResults;
    public bool ShowResults { get => _showResults; private set => this.RaiseAndSetIfChanged(ref _showResults, value); }

    private async Task SearchAsync(string text)
    {
        text = text.Trim();
        SearchResults.Clear();
        if (text.Length < 2) { ShowResults = false; return; }

        await SdeNames.EnsureLoadedAsync();
        var shownIds = SdeNames.Find(SdeNameKind.Type, text).Select(id => (int)id).ToList();
        var matches = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            // The products of a manufacturing or reaction blueprint, as the Production Calculator
            // offers them, and the blueprints themselves, for copying, invention and research.
            return await db.SdeTypes.AsNoTracking()
                .Where(t => t.Published
                         && (EF.Functions.Like(t.Name, $"%{text}%") || shownIds.Contains(t.TypeId))
                         && (db.SdeBlueprintProducts.Any(p => p.ProductTypeId == t.TypeId
                                && (p.Activity == "manufacturing" || p.Activity == "reaction"))
                             || db.SdeBlueprints.Any(b => b.TypeId == t.TypeId)))
                .OrderBy(t => t.Name).Take(60)
                .Select(t => new { t.TypeId, t.Name }).ToListAsync();
        });
        if (SearchText.Trim() != text) return;   // typed on since

        foreach (var r in matches.Select(m => new ProdTypeSearchResult { TypeId = m.TypeId, TypeName = m.Name })
                                 .OrderBy(r => r.DisplayName, StringComparer.CurrentCulture))
            SearchResults.Add(r);
        ShowResults = SearchResults.Count > 0;
    }
}
