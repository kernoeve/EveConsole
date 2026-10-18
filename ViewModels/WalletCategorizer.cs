using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using EveConsole.Localization;
using EveConsole.Services;

namespace EveConsole.ViewModels;

public sealed record WalletCategory(string Name, decimal Amount, bool IsIncome, SKColor Color);

// Turns wallet-journal totals (in and out per RefType) into the income / expense categories shown
// on the Overview pies, the Wallet tool's pies and the Income & Expense tool. Kept in one place so
// all three stay in sync.
public static class WalletCategorizer
{
    private static readonly HashSet<string> BountyTypes = new(System.StringComparer.OrdinalIgnoreCase)
        { "bounty_prizes", "npc_bounty", "bounty_prize", "corporate_reward", "agent_bounty_prize" };
    private static readonly HashSet<string> ContractTypes = new(System.StringComparer.OrdinalIgnoreCase)
        { "contract_reward", "contract_price", "contract_price_payment_corp",
          "contract_reward_refund", "contract_auction_sold" };

    /// <summary>
    /// Where one journal type's money goes: the named category for each direction it has one in,
    /// or null for "the type on its own" (Other, on the Overview).
    ///
    /// <para>⚠️ Every entry lands somewhere, in its own direction. These categories used to take a
    /// type's net, and some types — planetary import and export tax, contract deposits and their
    /// tax — were named as known expenses and then counted nowhere at all, so the totals here
    /// fell short of the entries they summarise. Now income is every entry above zero and
    /// expense every entry below, the same as the tool's Income and Expense tabs.</para>
    /// </summary>
    private static (string? In, string? Out) BucketsOf(string refType) => refType switch
    {
        "market_transaction"                     => (FinanceText.SliceMarketSales, FinanceText.SliceMarketPurchases),
        _ when BountyTypes.Contains(refType)     => (FinanceText.SliceNpcBounties, null),
        _ when ContractTypes.Contains(refType)   => (FinanceText.SliceContractSales, FinanceText.SliceContractPurchases),
        "broker_fee" or "brokers_fee"            => (null, FinanceText.SliceBrokerFees),
        "transaction_tax"                        => (null, FinanceText.SliceTransactionTax),
        "industry_job_tax" or "manufacturing_tax" => (null, FinanceText.SliceIndustryTax),
        _                                        => (null, null),
    };

    private static readonly (string Name, bool IsIncome, SKColor Color)[] Named =
    [
        (FinanceText.SliceMarketSales,       true,  new SKColor(200, 168,  75)),
        (FinanceText.SliceNpcBounties,       true,  new SKColor(110, 190, 100)),
        (FinanceText.SliceContractSales,     true,  new SKColor( 91, 155, 213)),
        (FinanceText.SliceMarketPurchases,   false, new SKColor(200,  90,  90)),
        (FinanceText.SliceContractPurchases, false, new SKColor(200, 120, 160)),
        (FinanceText.SliceBrokerFees,        false, new SKColor(220, 150,  60)),
        (FinanceText.SliceTransactionTax,    false, new SKColor(180, 180,  60)),
        (FinanceText.SliceIndustryTax,       false, new SKColor(100, 170, 200)),
    ];

    private static readonly SKColor OtherIncomeColor  = new(155, 120, 200);
    private static readonly SKColor OtherExpenseColor = new(160, 100, 120);

    /// <summary>The named categories, then everything else as one Other on each side: the
    /// Overview's pies and the Wallet tool's.</summary>
    public static List<WalletCategory> Categorize(IReadOnlyDictionary<string, RefTypeFlow> byRefType)
    {
        var (named, otherIn, otherOut) = Split(byRefType);
        var cats = NamedCategories(named);
        var oi = otherIn.Values.Sum();
        var oo = otherOut.Values.Sum();
        if (oi > 0) cats.Add(new WalletCategory(FinanceText.SliceOtherIncome,   oi, true,  OtherIncomeColor));
        if (oo > 0) cats.Add(new WalletCategory(FinanceText.SliceOtherExpenses, oo, false, OtherExpenseColor));
        return cats;
    }

    /// <summary>As <see cref="Categorize"/>, but every other journal type on a line of its own,
    /// largest first: the Income &amp; Expense tool's lists.</summary>
    public static List<WalletCategory> CategorizeDetailed(IReadOnlyDictionary<string, RefTypeFlow> byRefType)
    {
        var (named, otherIn, otherOut) = Split(byRefType);
        var cats = NamedCategories(named);
        foreach (var (rt, amt) in otherIn.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value))
            cats.Add(new WalletCategory(CorpActivityViewModel.FormatRefType(rt), amt, true, OtherIncomeColor));
        foreach (var (rt, amt) in otherOut.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value))
            cats.Add(new WalletCategory(CorpActivityViewModel.FormatRefType(rt), amt, false, OtherExpenseColor));
        return cats;
    }

    private static (Dictionary<(string, bool), decimal> Named, Dictionary<string, decimal> OtherIn, Dictionary<string, decimal> OtherOut)
        Split(IReadOnlyDictionary<string, RefTypeFlow> byRefType)
    {
        var named    = new Dictionary<(string, bool), decimal>();
        var otherIn  = new Dictionary<string, decimal>(System.StringComparer.OrdinalIgnoreCase);
        var otherOut = new Dictionary<string, decimal>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (refType, flow) in byRefType)
        {
            var (inBucket, outBucket) = BucketsOf(refType);
            if (flow.In > 0)
            {
                if (inBucket is not null) named[(inBucket, true)] = named.GetValueOrDefault((inBucket, true)) + flow.In;
                else                      otherIn[refType]        = otherIn.GetValueOrDefault(refType) + flow.In;
            }
            if (flow.Out > 0)
            {
                if (outBucket is not null) named[(outBucket, false)] = named.GetValueOrDefault((outBucket, false)) + flow.Out;
                else                       otherOut[refType]         = otherOut.GetValueOrDefault(refType) + flow.Out;
            }
        }
        return (named, otherIn, otherOut);
    }

    private static List<WalletCategory> NamedCategories(Dictionary<(string, bool), decimal> named) =>
        [.. Named.Where(n => named.GetValueOrDefault((n.Name, n.IsIncome)) > 0)
                 .Select(n => new WalletCategory(n.Name, named[(n.Name, n.IsIncome)], n.IsIncome, n.Color))];
}
