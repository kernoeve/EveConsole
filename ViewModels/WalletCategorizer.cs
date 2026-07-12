using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace EveCortex.ViewModels;

public sealed record WalletCategory(string Name, decimal Amount, bool IsIncome, SKColor Color);

// Turns wallet-journal totals (summed per RefType) into the income / expense categories shown on
// the Overview pies and the Income & Expense tool. Kept in one place so both stay in sync.
public static class WalletCategorizer
{
    private static readonly HashSet<string> BountyTypes = new(System.StringComparer.OrdinalIgnoreCase)
        { "bounty_prizes", "npc_bounty", "bounty_prize", "corporate_reward", "agent_bounty_prize" };
    private static readonly HashSet<string> ContractIncTypes = new(System.StringComparer.OrdinalIgnoreCase)
        { "contract_reward", "contract_price", "contract_price_payment_corp",
          "contract_reward_refund", "contract_auction_sold" };
    private static readonly HashSet<string> KnownExpenseTypes = new(System.StringComparer.OrdinalIgnoreCase)
        { "broker_fee", "brokers_fee", "transaction_tax",
          "industry_job_tax", "manufacturing_tax",
          "contract_deposit", "contract_sales_tax", "contract_deposit_sales_tax",
          "planetary_import_tax", "planetary_export_tax", "planetary_construction" };

    public static List<WalletCategory> Categorize(IReadOnlyDictionary<string, decimal> byRefType)
    {
        decimal mktSell = 0, mktBuy = 0, npcBounty = 0, contractInc = 0, contractExp = 0, otherIncome = 0;
        decimal brokerFees = 0, txnTax = 0, indyTax = 0, otherExpense = 0;

        foreach (var (refType, total) in byRefType)
        {
            if (refType == "market_transaction")
            {
                if (total > 0) mktSell += total;
                else           mktBuy  += -total;
            }
            else if (BountyTypes.Contains(refType))
            {
                if (total > 0) npcBounty += total;
            }
            else if (ContractIncTypes.Contains(refType))
            {
                if (total > 0) contractInc += total;
                else           contractExp += -total;
            }
            else if (refType is "broker_fee" or "brokers_fee")
                brokerFees += System.Math.Abs(total);
            else if (refType == "transaction_tax")
                txnTax += System.Math.Abs(total);
            else if (refType is "industry_job_tax" or "manufacturing_tax")
                indyTax += System.Math.Abs(total);
            else if (!KnownExpenseTypes.Contains(refType))
            {
                if (total > 0) otherIncome  += total;
                else           otherExpense += -total;
            }
        }

        var cats = new List<WalletCategory>();
        void Inc(string n, decimal v, SKColor c) { if (v > 0) cats.Add(new WalletCategory(n, v, true, c)); }
        void Exp(string n, decimal v, SKColor c) { if (v > 0) cats.Add(new WalletCategory(n, v, false, c)); }

        Inc("Market Sales",       mktSell,     new SKColor(200, 168,  75));
        Inc("NPC Bounties",       npcBounty,   new SKColor(110, 190, 100));
        Inc("Contract Sales",     contractInc, new SKColor( 91, 155, 213));
        Inc("Other Income",       otherIncome, new SKColor(155, 120, 200));

        Exp("Market Purchases",   mktBuy,      new SKColor(200,  90,  90));
        Exp("Contract Purchases", contractExp, new SKColor(200, 120, 160));
        Exp("Broker Fees",        brokerFees,  new SKColor(220, 150,  60));
        Exp("Transaction Tax",    txnTax,      new SKColor(180, 180,  60));
        Exp("Industry Tax",       indyTax,     new SKColor(100, 170, 200));
        Exp("Other Expenses",     otherExpense,new SKColor(160, 100, 120));

        return cats;
    }
}
