using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One store in the list on the left.
///
/// <para>⚠️ Updated in place rather than replaced. The settings panel writes through as the user
/// types, and swapping the row object on each save would rebuild the list item and drop the
/// selection out from under them — mid-word, on every character.</para>
/// </summary>
public class StoreRowVm : ReactiveObject
{
    private Store _model;

    public StoreRowVm(Store model) => _model = model;

    public Store Model => _model;
    public int   Id    => _model.Id;

    public string Name          => _model.Name.Length > 0 ? _model.Name : "(unnamed)";
    public string CharacterName => _model.CharacterName;
    public bool   Enabled       => _model.Enabled;

    /// <summary>Open or closed, said plainly — the list is the first place someone looks to
    /// find out why a buyer got no answer.</summary>
    public string StateText => _model.Enabled ? "Open" : "Closed";

    public void Refresh(Store model)
    {
        _model = model;
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(CharacterName));
        this.RaisePropertyChanged(nameof(Enabled));
        this.RaisePropertyChanged(nameof(StateText));
    }
}

/// <summary>One message in the log.</summary>
public class StoreMailRowVm(StoreMail m)
{
    public string When      => m.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Direction => m.Direction == "in" ? "Received" : "Sent";
    public string Party     => m.PartyName.Length > 0 ? m.PartyName : m.PartyId.ToString();
    public string Command   => m.Command;
    public string Subject   => m.Subject;
    public string Outcome   => m.Outcome;
    public string Detail    => m.Detail;
    public string OrderRef  => m.OrderRef;
    public string Body      => StoreMailService.Strip(m.Body).Trim();

    /// <summary>Rejections and failures are the rows worth finding, so they say so rather than
    /// relying on the reader to notice a word in a column.</summary>
    public bool IsProblem => m.Outcome is "rejected" or "error" or "failed";
}

/// <summary>One allow-list entry.</summary>
public class StoreSenderRowVm(StoreSender s)
{
    public int    Id   => s.Id;
    public string Name => s.Name.Length > 0 ? s.Name : s.EntityId.ToString();
    public string Kind => s.EntityType switch
    {
        "corporation" => "Corporation",
        "alliance"    => "Alliance",
        _             => "Character",
    };
}

/// <summary>
/// The shop front: which stores exist, who may write to them, and everything that has been said.
///
/// <para>Deliberately not a place where an order is edited. Orders live in the Order Tracker and
/// always have — a store order is an ordinary tracked order with a reference on it — and a second
/// screen that edited them would be a second set of rules about what an order is.</para>
/// </summary>
public class StoresViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SalePostingService              _postings;
    private readonly StoreMailService                _storeMail;
    private readonly OrderLabelService               _labels;
    private readonly AppErrorLogger                  _errorLogger;

    public ObservableCollection<StoreRowVm>       Stores  { get; } = [];
    public ObservableCollection<StoreMailRowVm>   Mails   { get; } = [];
    public ObservableCollection<StoreSenderRowVm> Senders { get; } = [];

    /// <summary>Characters we hold a token for — the only ones that can be a shop's address.</summary>
    public ObservableCollection<CharacterOption> CharacterOptions { get; } = [];
    public ObservableCollection<PostingOption>   PostingOptions   { get; } = [];

    public IReadOnlyList<string> PolicyOptions { get; } = ["List", "Anyone"];

    public sealed record CharacterOption(long Id, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record PostingOption(int Id, string Name)
    {
        public override string ToString() => Name;
    }

    public StoresViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        SalePostingService              postings,
        StoreMailService                storeMail,
        OrderLabelService               labels,
        AppErrorLogger                  errorLogger)
    {
        _dbFactory   = dbFactory;
        _postings    = postings;
        _storeMail   = storeMail;
        _labels      = labels;
        _errorLogger = errorLogger;

        AddStoreCommand    = ReactiveCommand.CreateFromTask(AddStoreAsync);
        DeleteStoreCommand = ReactiveCommand.CreateFromTask(DeleteStoreAsync);
        RefreshCommand     = ReactiveCommand.CreateFromTask(LoadAsync);
        CheckMailCommand   = ReactiveCommand.CreateFromTask(CheckMailNowAsync);
        AddSenderCommand   = ReactiveCommand.CreateFromTask(AddSenderAsync);

        foreach (var c in new[] { AddStoreCommand, DeleteStoreCommand, RefreshCommand,
                                  CheckMailCommand, AddSenderCommand })
            c.ThrownExceptions.Subscribe(ex => errorLogger.Log(nameof(StoresViewModel), "command", ex));

        this.WhenAnyValue(x => x.SelectedStore)
            .Skip(1)
            .SubscribeAsyncSafe(_ => LoadSelectedAsync(), errorLogger, "Stores.SelectStore");

        // The log is the only sign the shop is doing anything, and it changes without anyone
        // touching this screen.
        Observable.Interval(TimeSpan.FromSeconds(30))
            .ObserveOnUi("Stores.AutoRefresh")
            .SubscribeAsyncSafe(_ => LoadSelectedAsync(), errorLogger, "Stores.AutoRefresh");

        _ = LoadAsync();
    }

    public ReactiveCommand<Unit, Unit> AddStoreCommand    { get; }
    public ReactiveCommand<Unit, Unit> DeleteStoreCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand     { get; }
    public ReactiveCommand<Unit, Unit> CheckMailCommand   { get; }
    public ReactiveCommand<Unit, Unit> AddSenderCommand   { get; }

    private StoreRowVm? _selectedStore;
    public StoreRowVm? SelectedStore
    {
        get => _selectedStore;
        set => this.RaiseAndSetIfChanged(ref _selectedStore, value);
    }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public bool HasSelection => SelectedStore is not null;

    // ── Editable settings for the selected store ──────────────────────────────
    //
    // Written straight through on change. A shop has four settings and a Save button would be one
    // more thing to forget; the one that matters — Enabled — is the switch that makes it live, so
    // it is better applied the moment it is flipped than left pending.

    private string _storeName = "";
    public string StoreName
    {
        get => _storeName;
        set { this.RaiseAndSetIfChanged(ref _storeName, value); _ = SaveAsync(s => s.Name = value?.Trim() ?? ""); }
    }

    private CharacterOption? _storeCharacter;
    public CharacterOption? StoreCharacter
    {
        get => _storeCharacter;
        set
        {
            this.RaiseAndSetIfChanged(ref _storeCharacter, value);
            if (value is null) return;
            _ = SaveAsync(s => { s.CharacterId = value.Id; s.CharacterName = value.Name; });
        }
    }

    private PostingOption? _storePosting;
    public PostingOption? StorePosting
    {
        get => _storePosting;
        set
        {
            this.RaiseAndSetIfChanged(ref _storePosting, value);
            _ = MeasurePostingAsync();
            if (value is null) return;
            _ = SaveAsync(s => s.PostingId = value.Id);
        }
    }

    private string _senderPolicy = "List";
    public string SenderPolicy
    {
        get => _senderPolicy;
        set { this.RaiseAndSetIfChanged(ref _senderPolicy, value); _ = SaveAsync(s => s.SenderPolicy = value); }
    }

    private bool _storeEnabled;
    public bool StoreEnabled
    {
        get => _storeEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _storeEnabled, value);
            _ = SaveAsync(s =>
            {
                s.Enabled = value;
                // ⚠️ The listening mark moves forward every time the shop opens. Without this,
                // reopening after a week would answer the week's backlog at once — real mail, to
                // real people, that cannot be recalled.
                if (value) s.ListenFrom = DateTimeOffset.UtcNow;
            });
        }
    }

    private string _postingSizeText = "";

    /// <summary>The rendered price list's size against what one mail can carry.</summary>
    public string PostingSizeText
    {
        get => _postingSizeText;
        private set => this.RaiseAndSetIfChanged(ref _postingSizeText, value);
    }

    private bool _postingSplits;

    /// <summary>True when the price list would arrive as more than one mail.</summary>
    public bool PostingSplits
    {
        get => _postingSplits;
        private set => this.RaiseAndSetIfChanged(ref _postingSplits, value);
    }

    /// <summary>
    /// Measures the price list this store would send.
    ///
    /// <para>Shown while the posting is being edited, because the alternative is finding out from
    /// a buyer who received two mails. The figure is in bytes rather than characters: that is the
    /// unit the limit is enforced in, and a posting full of × and — weighs more than it reads.</para>
    /// </summary>
    private async Task MeasurePostingAsync()
    {
        if (SelectedStore is not StoreRowVm row) { Clear(); return; }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == row.Id);
            if (store is null) { Clear(); return; }

            var size = await _storeMail.MeasurePriceListAsync(store);
            if (size is null) { Clear(); return; }

            var text = size.Splits
                ? $"⚠  Price list is {size.Bytes:N0} of {size.Limit:N0} bytes — "
                + $"{size.Over:N0} over, so it will arrive as {size.Parts} mails. "
                + "Shorten the posting to send it as one."
                : $"Price list is {size.Bytes:N0} of {size.Limit:N0} bytes — fits in one mail "
                + $"with {size.Limit - size.Bytes:N0} to spare.";

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PostingSizeText = text;
                PostingSplits   = size.Splits;
            });
        }
        catch (Exception ex)
        {
            _errorLogger.Log("StoresViewModel", "MeasurePosting", ex);
            Clear();
        }

        void Clear() => Dispatcher.UIThread.Post(() =>
        {
            PostingSizeText = "";
            PostingSplits   = false;
        });
    }

    private readonly ObservableCollection<string> _knownLabels = [];

    /// <summary>What the label box offers: every label already in use anywhere, plus the ones
    /// other stores are set to apply.</summary>
    public ObservableCollection<string> KnownLabels => _knownLabels;

    private bool _useCustomUsage;
    public bool UseCustomUsage
    {
        get => _useCustomUsage;
        set
        {
            this.RaiseAndSetIfChanged(ref _useCustomUsage, value);
            _ = SaveAsync(s => s.UseCustomUsage = value);

            // Turning it on with nothing written puts the stock message in the box. Starting from
            // an empty field means rebuilding the markup, the links and the command list from
            // nothing, when the point is usually to change two paragraphs of it.
            if (value && CustomUsage.Trim().Length == 0) CustomUsage = DefaultUsageText();
        }
    }

    private string _customUsage = "";
    public string CustomUsage
    {
        get => _customUsage;
        set
        {
            this.RaiseAndSetIfChanged(ref _customUsage, value);
            _ = SaveAsync(s => s.CustomUsage = value ?? "");
        }
    }

    /// <summary>Puts the stock message back in the box, discarding what was written.</summary>
    public void ResetUsage() => CustomUsage = DefaultUsageText();

    /// <summary>
    /// The stock usage message for the selected store, exactly as a buyer would receive it.
    ///
    /// <para>⚠️ Built from the real generator rather than a copy kept here. Two versions of the
    /// same message drift, and the one in the box would quietly become the one nobody sends.</para>
    /// </summary>
    private string DefaultUsageText()
    {
        if (SelectedStore is not StoreRowVm row) return "";

        return StoreMailService.DefaultUsageForEditing(new Store
        {
            Id            = row.Id,
            Name          = StoreName,
            MessageHeader = MessageHeader,
            MessageFooter = MessageFooter,
        });
    }

    private string _storeOrderLabels = "";
    public string StoreOrderLabels
    {
        get => _storeOrderLabels;
        set
        {
            this.RaiseAndSetIfChanged(ref _storeOrderLabels, value);
            _ = SaveAsync(s => s.OrderLabels =
                string.Join(", ", OrderLabelService.Split(value)));
        }
    }

    private string _messageHeader = "";
    public string MessageHeader
    {
        get => _messageHeader;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageHeader, value);
            _ = SaveAsync(s => s.MessageHeader = value?.Trim() ?? "");
        }
    }

    private string _messageHeaderColor = "";
    public string MessageHeaderColor
    {
        get => _messageHeaderColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageHeaderColor, value);
            _ = SaveAsync(s => s.MessageHeaderColor = value?.Trim() ?? "");
        }
    }

    private string _messageFooter = "";
    public string MessageFooter
    {
        get => _messageFooter;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageFooter, value);
            _ = SaveAsync(s => s.MessageFooter = value?.Trim() ?? "");
        }
    }

    private string _messageFooterColor = "";
    public string MessageFooterColor
    {
        get => _messageFooterColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageFooterColor, value);
            _ = SaveAsync(s => s.MessageFooterColor = value?.Trim() ?? "");
        }
    }

    private bool _autoEstimate = true;
    public bool AutoEstimate
    {
        get => _autoEstimate;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoEstimate, value);
            _ = SaveAsync(s => s.AutoEstimateInStock = value);
        }
    }

    private int _autoEstimateDays = 1;
    public int AutoEstimateDays
    {
        get => _autoEstimateDays;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoEstimateDays, value);
            _ = SaveAsync(s => s.AutoEstimateDays = Math.Max(0, value));
        }
    }

    /// <summary>Typed name for the allow list, resolved when added.</summary>
    private string _senderName = "";
    public string SenderName { get => _senderName; set => this.RaiseAndSetIfChanged(ref _senderName, value); }

    private string _senderKind = "Character";
    public string SenderKind { get => _senderKind; set => this.RaiseAndSetIfChanged(ref _senderKind, value); }

    public IReadOnlyList<string> SenderKinds { get; } = ["Character", "Corporation", "Alliance"];

    /// <summary>One suggestion in the name box.</summary>
    public sealed record SenderOption(long Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>What the user picked from the dropdown, if they picked rather than typed.</summary>
    private SenderOption? _senderMatch;
    public SenderOption? SenderMatch
    {
        get => _senderMatch;
        set => this.RaiseAndSetIfChanged(ref _senderMatch, value);
    }

    /// <summary>How many suggestions the name box offers at once. Anyone after a particular
    /// corporation types more of its name; nobody scrolls forty thousand rows.</summary>
    private const int NameMatchLimit = 50;

    /// <summary>
    /// Names matching what has been typed so far, from the ids the app has already resolved.
    ///
    /// <para><b>⚠️ AsyncPopulator, not ItemsSource.</b> The name cache holds 267,000 characters
    /// and 39,000 corporations; handing the box that list would have it lay out every one.
    /// FilterMode must be None to match — this has already narrowed the list, and letting the box
    /// filter again would drop matches it never received.</para>
    ///
    /// <para>The point is being able to see whether a name is right before pressing Add. Without
    /// it the only feedback was Add working or not working, with nothing to say which character
    /// of a long corporation name was wrong.</para>
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> SenderPopulator => async (text, ct) =>
    {
        var needle = (text ?? "").Trim();
        if (needle.Length < 2) return [];

        var category = CategoryOf(SenderKind);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // ⚠️ LIKE, not ==. SQLite compares text with = case-sensitively, which is the whole
            // reason this box needed fixing; LIKE is case-insensitive for ASCII and is what makes
            // "ytiri" find "Ytiri". A contained match rather than a prefix because the
            // distinctive word of a corporation name is often not its first.
            //
            // No index exists on this table, so this is a scan — but LIMIT stops it early and it
            // measured between 1 and 28 ms across the categories, which is well inside what a
            // keystroke can absorb.
            var hits = await db.UniverseNames.AsNoTracking()
                .Where(n => n.Category == category && EF.Functions.Like(n.Name, $"%{needle}%"))
                .OrderBy(n => n.Name)
                .Take(NameMatchLimit)
                .Select(n => new { n.EntityId, n.Name })
                .ToListAsync(ct);

            return hits.Select(h => (object)new SenderOption(h.EntityId, h.Name)).ToList();
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(SenderPopulator), ex);
            return [];
        }
    };

    private static string CategoryOf(string kind) => kind switch
    {
        "Corporation" => "corporation",
        "Alliance"    => "alliance",
        _             => "character",
    };

    private StoreSenderRowVm? _selectedSender;
    public StoreSenderRowVm? SelectedSender
    {
        get => _selectedSender;
        set => this.RaiseAndSetIfChanged(ref _selectedSender, value);
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    private string _statInquiries = "0", _statActive = "0", _statCompleted = "0", _statCancelled = "0";
    public string StatInquiries { get => _statInquiries; private set => this.RaiseAndSetIfChanged(ref _statInquiries, value); }
    public string StatActive    { get => _statActive;    private set => this.RaiseAndSetIfChanged(ref _statActive,    value); }
    public string StatCompleted { get => _statCompleted; private set => this.RaiseAndSetIfChanged(ref _statCompleted, value); }
    public string StatCancelled { get => _statCancelled; private set => this.RaiseAndSetIfChanged(ref _statCancelled, value); }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var stores = await db.Stores.AsNoTracking()
                .Where(s => !s.IsDeleted)
                .OrderBy(s => s.Name).ToListAsync();

            // Offered in the label box below. Loaded here rather than on demand because this list
            // barely changes and the box needs it the moment a store is selected.
            var known = await _labels.AllAsync();
            _knownLabels.Clear();
            foreach (var label in known) _knownLabels.Add(label);

            // Only characters we hold a token for: a shop's address has to be a mailbox we can
            // read and send from, and one we cannot is a store that silently never answers.
            var chars = await db.Characters.AsNoTracking()
                .Where(c => c.RefreshToken != "")
                .Select(c => new { c.Id, c.Name })
                .OrderBy(c => c.Name).ToListAsync();

            var postings = await db.SalePostings.AsNoTracking()
                .Select(p => new { p.Id, p.Name }).OrderBy(p => p.Name).ToListAsync();

            var keepId = SelectedStore?.Id;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CharacterOptions.Clear();
                foreach (var c in chars) CharacterOptions.Add(new CharacterOption(c.Id, c.Name));

                PostingOptions.Clear();
                foreach (var p in postings) PostingOptions.Add(new PostingOption(p.Id, p.Name));

                Stores.Clear();
                foreach (var s in stores) Stores.Add(new StoreRowVm(s));

                SelectedStore = keepId is int id
                    ? Stores.FirstOrDefault(s => s.Id == id) ?? Stores.FirstOrDefault()
                    : Stores.FirstOrDefault();

                Status = Stores.Count == 0
                    ? "No stores yet — add one to let buyers ask by EVE mail."
                    : "";
            });

            await LoadSelectedAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(LoadAsync), ex);
            Status = $"Load failed: {ex.Message}";
        }
    }

    private async Task LoadSelectedAsync()
    {
        if (SelectedStore is not StoreRowVm row)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { Mails.Clear(); Senders.Clear(); });
            return;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == row.Id);
            if (store is null) return;

            var mails = await db.StoreMails.AsNoTracking()
                .Where(m => m.StoreId == row.Id)
                .OrderByDescending(m => m.Id)
                .Take(200)
                .ToListAsync();

            var senders = await db.StoreSenders.AsNoTracking()
                .Where(s => s.StoreId == row.Id).OrderBy(s => s.Name).ToListAsync();

            // ⚠️ Counted off orders, not off the mail log. A mail says what was asked for; only
            // the order says what became of it, and an order cancelled in the Order Tracker by
            // hand never produced a mail at all.
            var orders = await db.TrackedOrders.AsNoTracking()
                .Where(o => o.StoreId == row.Id && o.OrderRef != "")
                .Select(o => new { o.OrderRef, o.Status })
                .ToListAsync();

            // By order, not by line: six items on one order is one order.
            var byRef = orders.GroupBy(o => o.OrderRef).ToList();
            var active    = byRef.Count(g => g.Any(o => o.Status == "pending"));
            var completed = byRef.Count(g => g.All(o => o.Status == "completed"));
            var cancelled = byRef.Count(g => g.All(o => o.Status == "canceled"));

            var inquiries = mails.Count(m => m.Direction == "in");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _suppressSave = true;
                try
                {
                    StoreName      = store.Name;
                    StoreCharacter = CharacterOptions.FirstOrDefault(c => c.Id == store.CharacterId);
                    StorePosting   = PostingOptions.FirstOrDefault(p => p.Id == store.PostingId);
                    SenderPolicy   = store.SenderPolicy;
                    StoreEnabled     = store.Enabled;
                    AutoEstimate     = store.AutoEstimateInStock;
                    AutoEstimateDays = store.AutoEstimateDays;
                    StoreOrderLabels   = store.OrderLabels;
                    UseCustomUsage     = store.UseCustomUsage;

                    // ⚠️ The stock text when nothing is saved, so the box always shows what this
                    // store actually sends. Assigned after the flag, since setting the flag is
                    // what would otherwise fill it — and inside the save-suppressed block, so
                    // merely selecting a store does not write anything back.
                    CustomUsage        = store.CustomUsage.Length > 0
                                       ? store.CustomUsage
                                       : StoreMailService.DefaultUsageForEditing(store);
                    MessageHeader      = store.MessageHeader;
                    MessageHeaderColor = store.MessageHeaderColor;
                    MessageFooter      = store.MessageFooter;
                    MessageFooterColor = store.MessageFooterColor;
                }
                finally { _suppressSave = false; }

                Mails.Clear();
                foreach (var m in mails) Mails.Add(new StoreMailRowVm(m));

                Senders.Clear();
                foreach (var s in senders) Senders.Add(new StoreSenderRowVm(s));

                StatInquiries = inquiries.ToString("N0");
                StatActive    = active.ToString("N0");
                StatCompleted = completed.ToString("N0");
                StatCancelled = cancelled.ToString("N0");

                this.RaisePropertyChanged(nameof(HasSelection));
            });
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(LoadSelectedAsync), ex);
        }
    }

    // ── Editing ───────────────────────────────────────────────────────────────

    /// <summary>⚠️ Set while the fields are being filled from the database. Without it, loading a
    /// store writes every one of its own settings straight back — and worse, writes the previous
    /// store's values onto it in the instant before the rest arrive.</summary>
    private bool _suppressSave;

    private async Task SaveAsync(Action<Store> apply)
    {
        if (_suppressSave || SelectedStore is not StoreRowVm row) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == row.Id);
            if (store is null) return;

            apply(store);
            await db.SaveChangesAsync();

            // The list shows the name and whether it is open, so it has to follow — in place, so
            // the row the user is editing stays the row that is selected.
            await Dispatcher.UIThread.InvokeAsync(() => row.Refresh(store));
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(SaveAsync), ex);
            Status = $"Save failed: {ex.Message}";
        }
    }

    private async Task AddStoreAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var store = new Store
        {
            Name       = "New store",
            CreatedAt  = DateTimeOffset.UtcNow,
            // Closed, with nothing before now to answer. Both are the safe position: a shop is
            // configured first and opened deliberately.
            Enabled    = false,
            ListenFrom = DateTimeOffset.UtcNow,
        };
        db.Stores.Add(store);
        await db.SaveChangesAsync();

        await LoadAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
            SelectedStore = Stores.FirstOrDefault(s => s.Id == store.Id));
    }

    /// <summary>
    /// Asked before a store is deleted. Set by the view, which owns the dialog.
    ///
    /// <para>⚠️ Null means no confirmation, and the delete proceeds. That is deliberate — a view
    /// model that refused to work without a dialog wired up would be a worse failure than the one
    /// this guards against — but every view that shows the button should set it.</para>
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    private async Task DeleteStoreAsync()
    {
        if (SelectedStore is not StoreRowVm row) return;

        // Naming what survives is half the point. Deleting a shop reads as though it might take
        // the orders with it, and someone hesitating over that deserves the answer in the prompt
        // rather than after.
        if (ConfirmDelete is { } ask)
        {
            var confirmed = await ask(
                $"Delete the store \"{row.Name}\"?\n\n" +
                "It closes and disappears from this list.\n\n" +
                "Nothing is destroyed: its orders stay in the Order Tracker, and its settings, " +
                "allow list and message history are kept so anything referring to it still " +
                "resolves. It simply stops reading and answering mail.");

            if (!confirmed) return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == row.Id);
        if (store is null) return;

        // ⚠️ Hidden, not removed. Orders keep their StoreId for life — they outlive the shop on
        // purpose — and deleting the row left that id pointing at nothing, so an order could no
        // longer say which shop took it. The senders and the message log stay for the same
        // reason: they are the record of what was agreed and with whom.
        store.IsDeleted = true;

        // Closed as well as hidden. The poll already skips deleted stores, but a shop that is
        // invisible and still marked open is a state waiting to be misread by the next thing
        // that queries this table.
        store.Enabled = false;

        await db.SaveChangesAsync();
        await LoadAsync();
    }

    private async Task AddSenderAsync()
    {
        if (SelectedStore is not StoreRowVm row) return;
        if (string.IsNullOrWhiteSpace(SenderName)) return;

        var kind = CategoryOf(SenderKind);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Resolved to an id now rather than matched by name later. Not because names change
            // — in EVE they cannot, short of a petition — but because a name is not unique
            // across categories and is easy to mistype. An id either matches the sender or does
            // not, which is what an authorisation check needs.
            //
            // A pick from the dropdown already carries its id, so it is trusted over a fresh
            // lookup — but only while the text still matches it. Picking a suggestion and then
            // editing the box would otherwise add the entity that was picked rather than the one
            // now written, which is the sort of thing nobody notices until the wrong person is
            // being served.
            var typed = SenderName.Trim();
            var resolved = SenderMatch is { } picked
                        && string.Equals(picked.Name, typed, StringComparison.OrdinalIgnoreCase)
                ? (picked.Id, picked.Name)
                : await ResolveAsync(db, typed, kind);
            if (resolved is null)
            {
                Status = $"Could not find a {kind} called \"{typed}\".";
                return;
            }

            var (id, name) = resolved.Value;

            if (await db.StoreSenders.AnyAsync(s => s.StoreId == row.Id && s.EntityId == id))
            {
                Status = $"{name} is already on the list.";
                return;
            }

            db.StoreSenders.Add(new StoreSender
            {
                StoreId = row.Id, EntityId = id, EntityType = kind, Name = name,
            });
            await db.SaveChangesAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SenderName  = "";
                SenderMatch = null;
                Status      = $"Added {name}.";
            });
            await LoadSelectedAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(AddSenderAsync), ex);
            Status = $"Could not add: {ex.Message}";
        }
    }

    public async Task RemoveSenderAsync(StoreSenderRowVm sender)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.StoreSenders.Where(s => s.Id == sender.Id).ExecuteDeleteAsync();
        await LoadSelectedAsync();
    }

    /// <summary>
    /// A name to an id, from what the app already knows.
    ///
    /// <para>UniverseNames is the app's own cache of every id it has ever resolved, which for a
    /// corporation or alliance the user deals with is almost always a hit. It is checked before
    /// anything is asked of ESI.</para>
    /// </summary>
    private static async Task<(long Id, string Name)?> ResolveAsync(
        AppDbContext db, string name, string kind)
    {
        // ⚠️ NOT `n.Name == name`. SQLite compares TEXT with = case-sensitively, so "ytiri" found
        // nothing while "Ytiri" found the corporation — the box appeared to reject a name that
        // was perfectly correct apart from a capital letter. LIKE with no wildcards is an exact
        // match that ignores ASCII case, which is what a name box should do.
        //
        // ⚠️ Escaped first: a name containing % or _ would otherwise become a pattern and match
        // something else entirely. EVE allows neither today, but a lookup that silently matches
        // the wrong entity is not a thing to leave resting on that.
        var category = kind switch
        {
            "corporation" => "corporation",
            "alliance"    => "alliance",
            _             => "character",
        };

        var pattern = name.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        var hits = await db.UniverseNames.AsNoTracking()
            .Where(n => n.Category == category && EF.Functions.Like(n.Name, pattern, "\\"))
            .Select(n => new { n.EntityId, n.Name })
            .Take(5)
            .ToListAsync();

        if (hits.Count == 0) return null;

        // An exact-case match wins if there is one; otherwise the single case-insensitive hit.
        // Two entities differing only in case is not something EVE allows, so more than one hit
        // means the pattern escaped — and picking arbitrarily between them would be a guess.
        var exact = hits.FirstOrDefault(h => h.Name == name);
        if (exact is not null) return (exact.EntityId, exact.Name);

        return hits.Count == 1 ? (hits[0].EntityId, hits[0].Name) : null;
    }

    private async Task CheckMailNowAsync()
    {
        Status = "Checking…";
        await _storeMail.RunOnceAsync();
        await LoadSelectedAsync();
        Status = _storeMail.StatusText;
    }
}
