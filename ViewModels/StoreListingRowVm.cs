using EveConsole.Models;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One of a store's listings on the Stores screen: which posting, Sell or Buy, and what its tab
/// and mail are headed. Every change is saved at once through <paramref name="changed"/>, which
/// writes the whole list in its order.
/// </summary>
public sealed class StoreListingRowVm(
    StoresViewModel.PostingOption? posting, string side, string title, Action changed,
    IReadOnlyList<StoresViewModel.PostingOption> postingOptions) : ReactiveObject
{
    /// <summary>
    /// The postings to pick from: the screen's own list, carried by the row.
    ///
    /// <para>⚠️ Not reached through <c>$parent[ItemsControl].((vm:StoresViewModel)DataContext)</c>.
    /// That cast could not resolve its type at run time, threw on the dispatcher as the Config
    /// tab was built, and left the whole app unusable.</para>
    /// </summary>
    public IReadOnlyList<StoresViewModel.PostingOption> PostingOptions { get; } = postingOptions;

    private StoresViewModel.PostingOption? _posting = posting;
    public StoresViewModel.PostingOption? Posting
    {
        get => _posting;
        set
        {
            // ⚠️ A detaching or refilled ComboBox sets null; that is not a choice, and must not
            // be saved — it would drop the listing from the store. Ignored without a word: raising
            // the property back at a ComboBox that has no such item has it blank the selection
            // again, and the two answer each other for ever.
            if (value is null || value == _posting) return;
            this.RaiseAndSetIfChanged(ref _posting, value);
            changed();
        }
    }

    private string _side = side == StorePosting.Buy ? StorePosting.Buy : StorePosting.Sell;
    /// <summary><see cref="StorePosting.Sell"/> or <see cref="StorePosting.Buy"/>.</summary>
    public string SideValue => _side;
    public bool   IsBuy     => _side == StorePosting.Buy;

    public Choice<string>? Side
    {
        get => StoresViewModel.SideOptions.FirstOrDefault(o => o.Value == _side);
        set
        {
            if (value is null || value.Value == _side) return;   // see Posting
            _side = value.Value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsBuy));
            changed();
        }
    }

    private string _title = title;
    /// <summary>What the tab and the mail are headed; empty for the posting's own name.</summary>
    public string Title
    {
        get => _title;
        set
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed == _title) return;
            this.RaiseAndSetIfChanged(ref _title, trimmed);
            changed();
        }
    }

    /// <summary>The posting picked again from a refilled list, without saving anything.</summary>
    internal void Rebind(StoresViewModel.PostingOption? option)
    {
        _posting = option;
        this.RaisePropertyChanged(nameof(Posting));
    }
}
