using System.Text.Json;
using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;

namespace EveCortex.Services;

// â”€â”€ Public result types â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

public sealed record WalletMonthRow(
    string  Month,
    // Income
    decimal RattingTax,
    decimal MiningTax,
    decimal Donations,
    decimal IndustryTax,
    decimal ContractIncome,
    decimal MarketIncome,
    decimal OtherIncome,
    // Expenses (stored as positive amounts)
    decimal MarketExpense,
    decimal ContractExpense,
    decimal AccountWithdraw,
    decimal ProjectPayouts,
    decimal OtherExpense)
{
    public decimal TotalIncome  => RattingTax + MiningTax + Donations + IndustryTax
                                 + ContractIncome + MarketIncome + OtherIncome;
    public decimal TotalExpense => MarketExpense + ContractExpense + AccountWithdraw
                                 + ProjectPayouts + OtherExpense;
}

public sealed record WalletDayRow(
    string  Day,
    decimal RattingTax,
    decimal MiningTax,
    decimal Donations,
    decimal IndustryTax,
    decimal ContractIncome,
    decimal MarketIncome,
    decimal OtherIncome);

public sealed record WalletExpenseDayRow(
    string  Day,
    decimal MarketExpense,
    decimal ContractExpense,
    decimal AccountWithdraw,
    decimal ProjectPayouts,
    decimal OtherExpense);

public sealed record PlayerAmountRow(long CharacterId, decimal Amount);
public sealed record RankedPlayerRow(int Rank, long CharacterId, decimal Amount);
public sealed record DailyAmountRow(string Day, decimal Amount);
public sealed record TaxPayerRow(int Rank, long EntityId, string Name, decimal Amount);
public sealed record WalletDetailRow(DateTimeOffset Date, string RefType, decimal Amount, long PartyId, string PartyName, string Reason = "");

public sealed record KillMonthRow(string Month, int Kills, int Losses);
public sealed record KillDayRow(string Day, int Kills, int Losses);
public sealed record KillCharRow(long CharacterId, int Kills, int Losses);

public sealed record MonthlyActivityRow(
    string  Month,
    decimal TotalIncome,
    decimal TotalExpense,
    decimal RattingTax,
    decimal IndustryTax,
    decimal ProjectPayouts,
    long    UnitsMined,
    int     Kills,
    int     Losses,
    int     PlayersActive);

public sealed record SdeTypeResult(int TypeId, string Name);
public sealed record SdeStationResult(long StationId, string Name);
public sealed record SdeSystemResult(int SystemId, string Name);
public sealed record SdeRegionResult(int RegionId, string Name);
public sealed record SdeConstellationResult(int ConstellationId, string Name);

public sealed record StandingProjectGridRow(
    long   DbId,
    string TypeDisplay,
    string TargetDisplay,
    string DestDisplay,
    int?   ExpandedSystemId,
    string MatchStatus,       // “matched” | “not_active” | “no_systems”
    string MatchedName,
    string RemainingText,
    string RemainingPayoutText,
    int?   ItemTypeId,
    string ItemTypeName);

// â”€â”€ Service â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

public class CorpActivityService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EsiClient                       _esi;

    public CorpActivityService(IDbContextFactory<AppDbContext> dbFactory, EsiClient esi)
    {
        _dbFactory = dbFactory;
        _esi       = esi;
    }

    public async Task<List<WalletMonthRow>> GetWalletMonthsAsync(
        long corpId, int months = 12, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var cutoff    = SqlCutoff(DateTimeOffset.UtcNow.AddMonths(-months));
        var rows      = await db.Database.SqlQuery<WalletMonthRaw>($"""
            SELECT
                strftime('%Y-%m', "Date") AS "Month",
                -- Income
                COALESCE(SUM(CASE WHEN "RefType" IN ('bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "RattingTax",
                COALESCE(SUM(CASE WHEN "RefType" = 'mining_tax'
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "MiningTax",
                COALESCE(SUM(CASE WHEN "RefType" IN ('player_donation','corporate_reward_payout')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "Donations",
                COALESCE(SUM(CASE WHEN "RefType" IN ('industry_job_tax','manufacturing_tax','reprocessing_tax')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "IndustryTax",
                COALESCE(SUM(CASE WHEN "RefType" IN ('contract_price','contract_price_payment_corp')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "ContractIncome",
                COALESCE(SUM(CASE WHEN "RefType" = 'market_transaction'
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "MarketIncome",
                COALESCE(SUM(CASE WHEN "RefType" NOT IN (
                                       'bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts',
                                       'mining_tax','player_donation','corporate_reward_payout',
                                       'industry_job_tax','manufacturing_tax','reprocessing_tax',
                                       'contract_price','contract_price_payment_corp',
                                       'market_transaction','corporation_account_withdrawal')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "OtherIncome",
                -- Expenses (returned as positive values)
                COALESCE(SUM(CASE WHEN "RefType" IN ('market_transaction','market_escrow')
                                   AND CAST("Amount" AS REAL) < 0 THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "MarketExpense",
                COALESCE(SUM(CASE WHEN "RefType" = 'contract_price_payment_corp'
                                   AND CAST("Amount" AS REAL) < 0 THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "ContractExpense",
                COALESCE(SUM(CASE WHEN "RefType" = 'corporation_account_withdrawal'
                                   AND CAST("Amount" AS REAL) < 0
                                   AND "SecondPartyId" != "FirstPartyId"
                              THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "AccountWithdraw",
                COALESCE(SUM(CASE WHEN "RefType" = 'project_payouts'
                                   AND CAST("Amount" AS REAL) < 0 THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "ProjectPayouts",
                COALESCE(SUM(CASE WHEN "RefType" NOT IN (
                                       'bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts',
                                       'mining_tax','player_donation','corporate_reward_payout',
                                       'industry_job_tax','manufacturing_tax','reprocessing_tax',
                                       'contract_price','contract_price_payment_corp',
                                       'market_transaction','market_escrow',
                                       'corporation_account_withdrawal','project_payouts')
                                   AND CAST("Amount" AS REAL) < 0 THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "OtherExpense"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
            GROUP BY "Month"
            ORDER BY "Month" DESC
            """).ToListAsync(ct);

        return rows.Select(r => new WalletMonthRow(
            r.Month,
            (decimal)r.RattingTax,  (decimal)r.MiningTax,      (decimal)r.Donations,
            (decimal)r.IndustryTax, (decimal)r.ContractIncome,  (decimal)r.MarketIncome,
            (decimal)r.OtherIncome,
            (decimal)r.MarketExpense, (decimal)r.ContractExpense,
            (decimal)r.AccountWithdraw, (decimal)r.ProjectPayouts, (decimal)r.OtherExpense)).ToList();
    }

    public async Task<List<WalletDayRow>> GetDailyWalletAsync(
        long corpId, int days = 90, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var cutoff    = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows      = await db.Database.SqlQuery<WalletDayRaw>($"""
            SELECT
                strftime('%Y-%m-%d', "Date") AS "Day",
                COALESCE(SUM(CASE WHEN "RefType" IN ('bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "RattingTax",
                COALESCE(SUM(CASE WHEN "RefType" = 'mining_tax'
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "MiningTax",
                COALESCE(SUM(CASE WHEN "RefType" IN ('player_donation','corporate_reward_payout')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "Donations",
                COALESCE(SUM(CASE WHEN "RefType" IN ('industry_job_tax','manufacturing_tax','reprocessing_tax')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "IndustryTax",
                COALESCE(SUM(CASE WHEN "RefType" IN ('contract_price','contract_price_payment_corp')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "ContractIncome",
                COALESCE(SUM(CASE WHEN "RefType" = 'market_transaction'
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "MarketIncome",
                COALESCE(SUM(CASE WHEN "RefType" NOT IN (
                                       'bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts',
                                       'mining_tax','player_donation','corporate_reward_payout',
                                       'industry_job_tax','manufacturing_tax','reprocessing_tax',
                                       'contract_price','contract_price_payment_corp',
                                       'market_transaction','corporation_account_withdrawal')
                                   AND CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "OtherIncome"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
              AND CAST("Amount" AS REAL) > 0
            GROUP BY "Day"
            ORDER BY "Day" ASC
            """).ToListAsync(ct);

        return rows.Select(r => new WalletDayRow(
            r.Day,
            (decimal)r.RattingTax,  (decimal)r.MiningTax,     (decimal)r.Donations,
            (decimal)r.IndustryTax, (decimal)r.ContractIncome, (decimal)r.MarketIncome,
            (decimal)r.OtherIncome)).ToList();
    }

    public async Task<List<WalletExpenseDayRow>> GetDailyExpenseWalletAsync(
        long corpId, int days = 90, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var cutoff    = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows      = await db.Database.SqlQuery<WalletExpenseDayRaw>($"""
            SELECT
                strftime('%Y-%m-%d', "Date") AS "Day",
                COALESCE(SUM(CASE WHEN "RefType" IN ('market_transaction','market_escrow')
                                   AND CAST("Amount" AS REAL) < 0 THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "MarketExpense",
                COALESCE(SUM(CASE WHEN "RefType" = 'contract_price_payment_corp'
                                   AND CAST("Amount" AS REAL) < 0 THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "ContractExpense",
                COALESCE(SUM(CASE WHEN "RefType" = 'corporation_account_withdrawal'
                                   AND CAST("Amount" AS REAL) < 0
                                   AND "SecondPartyId" != "FirstPartyId"
                              THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "AccountWithdraw",
                COALESCE(SUM(CASE WHEN "RefType" = 'project_payouts'
                                   AND CAST("Amount" AS REAL) < 0 THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "ProjectPayouts",
                COALESCE(SUM(CASE WHEN "RefType" NOT IN (
                                       'bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts',
                                       'mining_tax','player_donation','corporate_reward_payout',
                                       'industry_job_tax','manufacturing_tax','reprocessing_tax',
                                       'contract_price','contract_price_payment_corp',
                                       'market_transaction','market_escrow',
                                       'corporation_account_withdrawal','project_payouts')
                                   AND CAST("Amount" AS REAL) < 0 THEN ABS(CAST("Amount" AS REAL)) ELSE 0 END), 0) AS "OtherExpense"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
              AND CAST("Amount" AS REAL) < 0
            GROUP BY "Day"
            ORDER BY "Day" ASC
            """).ToListAsync(ct);

        return rows.Select(r => new WalletExpenseDayRow(
            r.Day,
            (decimal)r.MarketExpense, (decimal)r.ContractExpense,
            (decimal)r.AccountWithdraw, (decimal)r.ProjectPayouts, (decimal)r.OtherExpense)).ToList();
    }

    public async Task<List<DailyAmountRow>> GetDailyRattingTaxAsync(
        long corpId, int days, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows     = await db.Database.SqlQuery<DailyAmountRaw>($"""
            SELECT strftime('%Y-%m-%d', "Date") AS "Day",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts')
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {cutoff}
            GROUP BY "Day"
            ORDER BY "Day" ASC
            """).ToListAsync(ct);
        return rows.Select(r => new DailyAmountRow(r.Day, (decimal)r.Amount)).ToList();
    }

    public async Task<List<DailyAmountRow>> GetDailyIndustryTaxAsync(
        long corpId, int days, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows     = await db.Database.SqlQuery<DailyAmountRaw>($"""
            SELECT strftime('%Y-%m-%d', "Date") AS "Day",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('industry_job_tax','manufacturing_tax','reprocessing_tax')
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {cutoff}
            GROUP BY "Day"
            ORDER BY "Day" ASC
            """).ToListAsync(ct);
        return rows.Select(r => new DailyAmountRow(r.Day, (decimal)r.Amount)).ToList();
    }

    public async Task<List<TaxPayerRow>> GetDonationPayersAsync(
        long corpId, DateTimeOffset since, DateTimeOffset until, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var sinceStr  = SqlCutoff(since);
        var untilStr  = SqlCutoff(until);
        var rows      = await db.Database.SqlQuery<TaxPayerRaw>($"""
            SELECT "FirstPartyId" AS "EntityId",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" = 'player_donation'
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {sinceStr}
              AND "Date" <= {untilStr}
              AND "FirstPartyId" IS NOT NULL
              AND "FirstPartyId" >= 90000000
            GROUP BY "FirstPartyId"
            ORDER BY SUM(CAST("Amount" AS REAL)) DESC
            LIMIT 200
            """).ToListAsync(ct);
        var names = await ResolveNamesAsync(rows.Select(r => r.EntityId), ct);
        return rows.Select((r, i) => new TaxPayerRow(
            i + 1, r.EntityId,
            names.TryGetValue(r.EntityId, out var n) ? n : r.EntityId.ToString(),
            (decimal)r.Amount)).ToList();
    }

    public async Task<List<DailyAmountRow>> GetDailyDonationsAsync(
        long corpId, int days, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows     = await db.Database.SqlQuery<DailyAmountRaw>($"""
            SELECT strftime('%Y-%m-%d', "Date") AS "Day",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" = 'player_donation'
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {cutoff}
            GROUP BY "Day"
            ORDER BY "Day" ASC
            """).ToListAsync(ct);
        return rows.Select(r => new DailyAmountRow(r.Day, (decimal)r.Amount)).ToList();
    }

    public async Task<List<TaxPayerRow>> GetRattingTaxPayersAsync(
        long corpId, DateTimeOffset since, DateTimeOffset until, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var sinceStr  = SqlCutoff(since);
        var untilStr  = SqlCutoff(until);
        var rows      = await db.Database.SqlQuery<TaxPayerRaw>($"""
            SELECT "SecondPartyId" AS "EntityId",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts')
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {sinceStr}
              AND "Date" <= {untilStr}
              AND "SecondPartyId" IS NOT NULL
              AND "SecondPartyId" >= 90000000
              AND "SecondPartyId" != {corpId}
            GROUP BY "SecondPartyId"
            ORDER BY SUM(CAST("Amount" AS REAL)) DESC
            LIMIT 200
            """).ToListAsync(ct);
        var names = await ResolveNamesAsync(rows.Select(r => r.EntityId), ct);
        return rows.Select((r, i) => new TaxPayerRow(
            i + 1, r.EntityId,
            names.TryGetValue(r.EntityId, out var n) ? n : r.EntityId.ToString(),
            (decimal)r.Amount)).ToList();
    }

    public async Task<List<TaxPayerRow>> GetIndustryTaxPayersAsync(
        long corpId, DateTimeOffset since, DateTimeOffset until, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var sinceStr  = SqlCutoff(since);
        var untilStr  = SqlCutoff(until);
        var rows      = await db.Database.SqlQuery<TaxPayerRaw>($"""
            SELECT "FirstPartyId" AS "EntityId",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('industry_job_tax','manufacturing_tax','reprocessing_tax')
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {sinceStr}
              AND "Date" <= {untilStr}
              AND "FirstPartyId" IS NOT NULL
              AND "FirstPartyId" != {corpId}
            GROUP BY "FirstPartyId"
            ORDER BY SUM(CAST("Amount" AS REAL)) DESC
            LIMIT 200
            """).ToListAsync(ct);
        var names = await ResolveNamesAsync(rows.Select(r => r.EntityId), ct);
        return rows.Select((r, i) => new TaxPayerRow(
            i + 1, r.EntityId,
            names.TryGetValue(r.EntityId, out var n) ? n : r.EntityId.ToString(),
            (decimal)r.Amount)).ToList();
    }

    public async Task<List<RankedPlayerRow>> GetTopRattersAsync(
        long corpId, DateTimeOffset since, DateTimeOffset? until = null,
        IReadOnlySet<long>? excludeIds = null, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var sinceStr  = SqlCutoff(since);
        var untilStr  = SqlCutoff(until ?? DateTimeOffset.MaxValue);
        var rows      = await db.Database.SqlQuery<PlayerRaw>($"""
            SELECT "SecondPartyId" AS "CharacterId",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('bounty_prizes','bounty_prize')
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {sinceStr}
              AND "Date" < {untilStr}
              AND "SecondPartyId" IS NOT NULL
            GROUP BY "SecondPartyId"
            ORDER BY SUM(CAST("Amount" AS REAL)) DESC
            """).ToListAsync(ct);
        return ApplyTop10WithTies(rows, excludeIds);
    }

    public async Task<List<RankedPlayerRow>> GetTopByRefTypeAsync(
        long corpId, string refType, DateTimeOffset since, DateTimeOffset? until = null,
        IReadOnlySet<long>? excludeIds = null, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var sinceStr  = SqlCutoff(since);
        var untilStr  = SqlCutoff(until ?? DateTimeOffset.MaxValue);
        var rows      = await db.Database.SqlQuery<PlayerRaw>($"""
            SELECT "FirstPartyId" AS "CharacterId",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" = {refType}
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {sinceStr}
              AND "Date" < {untilStr}
              AND "FirstPartyId" IS NOT NULL
            GROUP BY "FirstPartyId"
            ORDER BY SUM(CAST("Amount" AS REAL)) DESC
            """).ToListAsync(ct);
        return ApplyTop10WithTies(rows, excludeIds);
    }

    public async Task<List<RankedPlayerRow>> GetTopIndustryAsync(
        long corpId, DateTimeOffset since, DateTimeOffset? until = null,
        IReadOnlySet<long>? excludeIds = null, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var sinceStr  = SqlCutoff(since);
        var untilStr  = SqlCutoff(until ?? DateTimeOffset.MaxValue);
        var rows      = await db.Database.SqlQuery<PlayerRaw>($"""
            SELECT "FirstPartyId" AS "CharacterId",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('industry_job_tax','manufacturing_tax','reprocessing_tax')
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {sinceStr}
              AND "Date" < {untilStr}
              AND "FirstPartyId" IS NOT NULL
              AND "FirstPartyId" != {corpId}
            GROUP BY "FirstPartyId"
            ORDER BY SUM(CAST("Amount" AS REAL)) DESC
            """).ToListAsync(ct);
        return ApplyTop10WithTies(rows, excludeIds);
    }

    public async Task<List<RankedPlayerRow>> GetTopMinersAsync(
        long corpId, DateTimeOffset? since = null, DateTimeOffset? until = null,
        IReadOnlySet<long>? excludeIds = null, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var sinceStr = SqlCutoff(since ?? DateTimeOffset.MinValue);
        var untilStr = SqlCutoff(until ?? DateTimeOffset.MaxValue);
        var rows     = await db.Database.SqlQuery<PlayerRaw>($"""
            SELECT m."CharacterId",
                   COALESCE(SUM(m."Quantity" * COALESCE(r."Value", 0)), 0) AS "Amount"
            FROM "EsiCorpMiningLedger" m
            LEFT JOIN "ReprocessingValues" r ON r."TypeId" = m."TypeId"
            WHERE m."CorporationId" = {corpId}
              AND m."LastUpdated" >= {sinceStr}
              AND m."LastUpdated" < {untilStr}
            GROUP BY m."CharacterId"
            ORDER BY SUM(m."Quantity" * COALESCE(r."Value", 0)) DESC
            """).ToListAsync(ct);
        return ApplyTop10WithTies(rows, excludeIds);
    }

    public async Task<List<KillMonthRow>> GetKillMonthsAsync(
        long corpId, int months = 6, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddMonths(-months));
        var rows     = await db.Database.SqlQuery<KillMonthRaw>($"""
            SELECT strftime('%Y-%m', d."KillMailTime") AS "Month",
                   COUNT(DISTINCT CASE WHEN d."VictimCorpId" != {corpId} THEN d."KillMailId" END) AS "Kills",
                   COUNT(DISTINCT CASE WHEN d."VictimCorpId" =  {corpId} THEN d."KillMailId" END) AS "Losses"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            WHERE d."KillMailTime" >= {cutoff}
            GROUP BY "Month"
            ORDER BY "Month" DESC
            """).ToListAsync(ct);
        return rows.Select(r => new KillMonthRow(r.Month, r.Kills, r.Losses)).ToList();
    }

    public async Task<List<KillDayRow>> GetKillDailyAsync(
        long corpId, int days = 90, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows     = await db.Database.SqlQuery<KillDayRaw>($"""
            SELECT strftime('%Y-%m-%d', d."KillMailTime") AS "Day",
                   COUNT(DISTINCT CASE WHEN d."VictimCorpId" != {corpId} THEN d."KillMailId" END) AS "Kills",
                   COUNT(DISTINCT CASE WHEN d."VictimCorpId" =  {corpId} THEN d."KillMailId" END) AS "Losses"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            WHERE d."KillMailTime" >= {cutoff}
            GROUP BY "Day"
            ORDER BY "Day" ASC
            """).ToListAsync(ct);
        return rows.Select(r => new KillDayRow(r.Day, r.Kills, r.Losses)).ToList();
    }

    public async Task<List<KillCharRow>> GetKillCharactersAsync(
        long corpId, int days = 90, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));

        var killRows = await db.Database.SqlQuery<CharCountRaw>($"""
            SELECT a."CharacterId", COUNT(DISTINCT d."KillMailId") AS "Count"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            JOIN "KillMailAttackers" a ON a."KillMailId" = d."KillMailId"
            WHERE d."VictimCorpId" != {corpId} AND a."CorporationId" = {corpId}
              AND a."CharacterId" != 0 AND d."KillMailTime" >= {cutoff}
            GROUP BY a."CharacterId"
            """).ToListAsync(ct);

        var lossRows = await db.Database.SqlQuery<CharCountRaw>($"""
            SELECT d."VictimCharId" AS "CharacterId", COUNT(*) AS "Count"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            WHERE d."VictimCorpId" = {corpId} AND d."VictimCharId" != 0
              AND d."KillMailTime" >= {cutoff}
            GROUP BY d."VictimCharId"
            """).ToListAsync(ct);

        var kills  = killRows.ToDictionary(r => r.CharacterId, r => r.Count);
        var losses = lossRows.ToDictionary(r => r.CharacterId, r => r.Count);
        var allIds = kills.Keys.Union(losses.Keys).ToHashSet();

        return allIds
            .Select(id => new KillCharRow(id, kills.GetValueOrDefault(id), losses.GetValueOrDefault(id)))
            .OrderByDescending(r => r.Kills).ThenByDescending(r => r.Losses)
            .ToList();
    }

    public async Task<List<MonthlyActivityRow>> GetMonthlyActivityAsync(
        long corpId, int months = 12, CancellationToken ct = default)
    {
        var walletMonths = await GetWalletMonthsAsync(corpId, months, ct);
        var killMonths   = await GetKillMonthsAsync(corpId, months, ct);

        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddMonths(-months));

        var miningRows = await db.Database.SqlQuery<MonthCountRaw>($"""
            SELECT strftime('%Y-%m', "LastUpdated") AS "Month",
                   SUM("Quantity") AS "Count"
            FROM "EsiCorpMiningLedger"
            WHERE "CorporationId" = {corpId} AND "LastUpdated" >= {cutoff}
            GROUP BY "Month"
            """).ToListAsync(ct);

        var miningByMonth = miningRows.ToDictionary(r => r.Month, r => r.Count);
        var killsByMonth  = killMonths.ToDictionary(r => r.Month);

        // Distinct active players per month (ratting + industry + mining)
        var playerRows = await db.Database.SqlQuery<MonthCountRaw>($"""
            SELECT "Month", COUNT(DISTINCT "CharId") AS "Count"
            FROM (
              SELECT strftime('%Y-%m', "Date") AS "Month", "SecondPartyId" AS "CharId"
              FROM "EsiWalletJournal"
              WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
                AND "RefType" IN ('bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts')
                AND "Date" >= {cutoff} AND "SecondPartyId" IS NOT NULL
              UNION
              SELECT strftime('%Y-%m', "Date") AS "Month", "FirstPartyId" AS "CharId"
              FROM "EsiWalletJournal"
              WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
                AND "RefType" IN ('industry_job_tax','manufacturing_tax','reprocessing_tax')
                AND "Date" >= {cutoff} AND "FirstPartyId" IS NOT NULL AND "FirstPartyId" != {corpId}
              UNION
              SELECT strftime('%Y-%m', "LastUpdated") AS "Month", "CharacterId" AS "CharId"
              FROM "EsiCorpMiningLedger"
              WHERE "CorporationId" = {corpId} AND "LastUpdated" >= {cutoff}
              UNION
              SELECT strftime('%Y-%m', d."KillMailTime") AS "Month", a."CharacterId" AS "CharId"
              FROM "KillMailDetails" d
              JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                  AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
              JOIN "KillMailAttackers" a ON a."KillMailId" = d."KillMailId"
              WHERE a."CorporationId" = {corpId} AND a."CharacterId" IS NOT NULL
                AND d."KillMailTime" >= {cutoff}
              UNION
              SELECT strftime('%Y-%m', d."KillMailTime") AS "Month", d."VictimCharId" AS "CharId"
              FROM "KillMailDetails" d
              JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                  AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
              WHERE d."VictimCorpId" = {corpId} AND d."VictimCharId" != 0
                AND d."KillMailTime" >= {cutoff}
            )
            GROUP BY "Month"
            """).ToListAsync(ct);
        var playersByMonth = playerRows.ToDictionary(r => r.Month, r => (int)r.Count);

        // Union of all months across sources
        var allMonths = walletMonths.Select(w => w.Month)
            .Union(miningByMonth.Keys)
            .Union(killsByMonth.Keys)
            .OrderByDescending(m => m)
            .ToList();

        return allMonths.Select(m =>
        {
            var mine    = miningByMonth.GetValueOrDefault(m);
            var kills   = killsByMonth.TryGetValue(m, out var kb) ? kb.Kills  : 0;
            var loss    = killsByMonth.TryGetValue(m, out var lb) ? lb.Losses : 0;
            var players = playersByMonth.GetValueOrDefault(m);
            var w = walletMonths.FirstOrDefault(ww => ww.Month == m);
            return w is not null
                ? new MonthlyActivityRow(m, w.TotalIncome, w.TotalExpense,
                    w.RattingTax, w.IndustryTax, w.ProjectPayouts, mine, kills, loss, players)
                : new MonthlyActivityRow(m, 0, 0, 0, 0, 0, mine, kills, loss, players);
        }).ToList();
    }

    public async Task<List<RankedPlayerRow>> GetTopKillersAsync(
        long corpId, DateTimeOffset since, DateTimeOffset? until = null,
        IReadOnlySet<long>? excludeIds = null, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var sinceStr = SqlCutoff(since);
        var untilStr = SqlCutoff(until ?? DateTimeOffset.MaxValue);
        var rows     = await db.Database.SqlQuery<PlayerRaw>($"""
            SELECT a."CharacterId", COUNT(DISTINCT d."KillMailId") AS "Amount"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            JOIN "KillMailAttackers" a ON a."KillMailId" = d."KillMailId"
            WHERE a."CorporationId" = {corpId}
              AND d."VictimCorpId" != {corpId}
              AND d."KillMailTime" >= {sinceStr}
              AND d."KillMailTime" < {untilStr}
              AND a."CharacterId" IS NOT NULL
            GROUP BY a."CharacterId"
            ORDER BY COUNT(DISTINCT d."KillMailId") DESC
            """).ToListAsync(ct);
        return ApplyTop10WithTies(rows, excludeIds);
    }

    public async Task<List<CorpProjectContributor>> GetProjectContributorsAsync(
        long corpId, string projectId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.EsiCorpProjectContributors
            .Where(c => c.CorporationId == corpId && c.ProjectId == projectId)
            .OrderByDescending(c => c.Contributed)
            .ToListAsync(ct);
    }

    public async Task<List<CorpProject>> GetProjectsActiveAsync(
        long corpId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.EsiCorpProjects
            .Where(p => p.CorporationId == corpId && p.State == "Active")
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<List<CorpProject>> GetProjectsHistoryAsync(
        long corpId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var rows = await db.EsiCorpProjects
            .Where(p => p.CorporationId == corpId && p.State != "Active")
            .ToListAsync(ct);
        return rows.OrderByDescending(p => p.LastModified).ToList();
    }

    public async Task<List<(long CharacterId, string Name, decimal IskPayout)>> GetTopProjectContributorsAsync(
        long corpId, DateTimeOffset? monthStart = null, DateTimeOffset? monthEnd = null,
        IReadOnlySet<long>? excludeIds = null, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();

        var now   = DateTimeOffset.UtcNow;
        var start = monthStart ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end   = monthEnd   ?? start.AddMonths(1);

        var completedProjects = (await db.EsiCorpProjects
            .Where(p => p.CorporationId == corpId && p.State != "Active")
            .ToListAsync(ct))
            .Where(p => p.LastModified >= start && p.LastModified < end)
            .ToDictionary(p => p.ProjectId, p => p.RewardPerContrib);

        if (completedProjects.Count == 0) return [];

        var completedIds = completedProjects.Keys.ToHashSet();
        var contributors = await db.EsiCorpProjectContributors
            .Where(c => c.CorporationId == corpId && completedIds.Contains(c.ProjectId))
            .ToListAsync(ct);

        // Compute ISK payout per contributor: sum(contributed * rewardPerContrib) across projects
        var byChar = contributors
            .GroupBy(c => new { c.CharacterId, c.Name })
            .Select(g => new
            {
                g.Key.CharacterId,
                g.Key.Name,
                IskPayout = g.Sum(c => (decimal)c.Contributed
                              * (decimal)completedProjects.GetValueOrDefault(c.ProjectId, 0.0))
            })
            .OrderByDescending(x => x.IskPayout)
            .ToList();

        var filtered = byChar
            .Where(r => excludeIds?.Contains(r.CharacterId) != true)
            .ToList();

        if (filtered.Count <= 10)
            return filtered.Select(r => (r.CharacterId, r.Name, r.IskPayout)).ToList();

        var threshold = filtered[9].IskPayout;
        return filtered
            .TakeWhile(r => r.IskPayout >= threshold)
            .Select(r => (r.CharacterId, r.Name, r.IskPayout))
            .ToList();
    }

    public sealed record MiningLedgerRow(
        string Date, long CharacterId, string CharacterName, int TypeId, string TypeName, long Quantity,
        double ReprocessedValue);

    public async Task<List<MiningLedgerRow>> GetMiningLedgerAsync(
        long corpId, DateTimeOffset since, CancellationToken ct = default)
    {
        var sinceStr = SqlCutoff(since);

        using var db = _dbFactory.CreateDbContext();

        var ledgerRows = await db.Database.SqlQuery<MiningLedgerRaw>($"""
            SELECT
                substr(l."LastUpdated", 1, 10) AS "Date",
                l."CharacterId",
                l."TypeId",
                COALESCE(t."Name", CAST(l."TypeId" AS TEXT)) AS "TypeName",
                SUM(l."Quantity") AS "Quantity"
            FROM "EsiCorpMiningLedger" l
            LEFT JOIN "SdeTypes" t ON t."TypeId" = l."TypeId"
            WHERE l."CorporationId" = {corpId}
              AND l."LastUpdated" >= {sinceStr}
            GROUP BY substr(l."LastUpdated", 1, 10), l."CharacterId", l."TypeId"
            ORDER BY substr(l."LastUpdated", 1, 10) DESC, SUM(l."Quantity") DESC
            """).ToListAsync(ct);

        var names = await ResolveNamesAsync(ledgerRows.Select(r => r.CharacterId).Distinct(), ct);

        var typeIds  = ledgerRows.Select(r => r.TypeId).Distinct().ToList();
        var reprVals = await db.ReprocessingItemValues.AsNoTracking()
            .Where(v => typeIds.Contains(v.TypeId))
            .ToDictionaryAsync(v => v.TypeId, v => v.Value, ct);

        return ledgerRows.Select(r => new MiningLedgerRow(
            r.Date, r.CharacterId,
            names.TryGetValue(r.CharacterId, out var n) ? n : r.CharacterId.ToString(),
            r.TypeId, r.TypeName, r.Quantity,
            reprVals.TryGetValue(r.TypeId, out var rv) ? rv * r.Quantity : 0)).ToList();
    }

    public async Task<List<(int Year, int Month)>> GetMiningLedgerMonthsAsync(
        long corpId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var months = await db.Database.SqlQuery<MonthRaw>($"""
            SELECT DISTINCT
                CAST(substr("LastUpdated", 1, 4) AS INTEGER) AS "Year",
                CAST(substr("LastUpdated", 6, 2) AS INTEGER) AS "Month"
            FROM "EsiCorpMiningLedger"
            WHERE "CorporationId" = {corpId}
            ORDER BY "Year" DESC, "Month" DESC
            """).ToListAsync(ct);
        return months.Select(m => (m.Year, m.Month)).ToList();
    }

    public async Task<Dictionary<long, string>> ResolveNamesAsync(
        IEnumerable<long> ids, CancellationToken ct = default, long authCharId = 0)
    {
        var idList = ids.Where(id => id > 0).Distinct().ToList();
        if (idList.Count == 0) return [];

        using var db  = _dbFactory.CreateDbContext();
        var result    = new Dictionary<long, string>();

        var chars = await db.Characters
            .Where(c => idList.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        foreach (var kv in chars) result[kv.Key] = kv.Value;

        // Player-owned structures (IDs > 1 trillion) won't resolve via /universe/names/ â€”
        // check the local structure name cache first, then try ESI if we have an auth char.
        var structureIds = idList.Where(id => !result.ContainsKey(id) && id > 1_000_000_000_000L).ToList();
        if (structureIds.Count > 0)
        {
            var structNames = await db.EsiStructureNames
                .Where(s => structureIds.Contains(s.StructureId))
                .ToDictionaryAsync(s => s.StructureId, s => s.Name, ct);
            foreach (var kv in structNames) result[kv.Key] = kv.Value;

            // Fetch any still-unresolved structure IDs from ESI and cache them.
            if (authCharId > 0)
            {
                var missing = structureIds.Where(id => !result.ContainsKey(id)).ToList();
                foreach (var sid in missing)
                {
                    try
                    {
                        var detail = await _esi.GetStructureAsync(authCharId, sid, ct);
                        if (detail.Data is not null && !string.IsNullOrEmpty(detail.Data.Name))
                        {
                            result[sid] = detail.Data.Name;
                            var cached  = await db.EsiStructureNames
                                .FirstOrDefaultAsync(s => s.StructureId == sid, ct)
                                ?? db.EsiStructureNames.Add(new StructureName { StructureId = sid }).Entity;
                            cached.Name         = detail.Data.Name;
                            cached.SolarSystemId = detail.Data.SolarSystemId;
                            cached.PulledAt     = DateTimeOffset.UtcNow;
                            await db.SaveChangesAsync(ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[ESI] GetStructureAsync({sid}) failed: {ex.Message}");
                    }
                }
            }
        }

        var remaining = idList.Where(id => !result.ContainsKey(id) && id <= int.MaxValue)
                             .Select(id => (int)id).ToList();

        // Chunk into batches of 200. If a batch fails (ESI 422 on any bad ID),
        // only that chunk falls back to individual calls â€” not the whole list.
        const int ChunkSize = 200;
        for (int offset = 0; offset < remaining.Count; offset += ChunkSize)
        {
            var chunk = remaining.Skip(offset).Take(ChunkSize).ToList();
            try
            {
                var names = await _esi.GetNamesAsync(chunk, ct);
                foreach (var n in names) result[(long)n.Id] = n.Name;
            }
            catch (Exception batchEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ESI] /universe/names/ chunk failed ({batchEx.Message}) â€” " +
                    $"retrying {chunk.Count} IDs individually");
                foreach (var id in chunk)
                {
                    if (result.ContainsKey((long)id)) continue;
                    try
                    {
                        var single = await _esi.GetNamesAsync([id], ct);
                        foreach (var n in single) result[(long)n.Id] = n.Name;
                    }
                    catch (Exception singleEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[ESI] ID {id} not resolved ({singleEx.Message})");
                    }
                }
            }
        }

        return result;
    }

    // â”€â”€ Tie-inclusive Top 10 â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static List<RankedPlayerRow> ApplyTop10WithTies(
        List<PlayerRaw> rawRows, IReadOnlySet<long>? excludeIds)
    {
        var filtered = rawRows
            .Where(r => excludeIds?.Contains(r.CharacterId) != true)
            .ToList();

        var result        = new List<RankedPlayerRow>();
        decimal? threshold = filtered.Count > 10
            ? (decimal?)filtered[9].Amount : null;

        int currentRank      = 1;
        decimal? prevAmount  = null;
        int countAtRank      = 0;

        foreach (var r in filtered)
        {
            var amount = (decimal)r.Amount;
            if (threshold.HasValue && amount < threshold.Value) break;

            if (prevAmount.HasValue && amount != prevAmount.Value)
            {
                currentRank += countAtRank;
                countAtRank  = 0;
            }

            countAtRank++;
            result.Add(new RankedPlayerRow(currentRank, r.CharacterId, amount));
            prevAmount = amount;
        }

        return result;
    }

    // â”€â”€ Income / Expense by type â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public sealed record WalletTypeRow(string RefType, int Count, decimal Amount);

    public async Task<List<WalletTypeRow>> GetIncomeByTypeAsync(
        long corpId, int days, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows     = await db.Database.SqlQuery<WalletTypeRaw>($"""
            SELECT "RefType",
                   COUNT(*) AS "Count",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
              AND CAST("Amount" AS REAL) > 0
              AND "RefType" != 'corporation_account_withdrawal'
            GROUP BY "RefType"
            ORDER BY SUM(CAST("Amount" AS REAL)) DESC
            """).ToListAsync(ct);
        return rows.Select(r => new WalletTypeRow(r.RefType, r.Count, (decimal)r.Amount)).ToList();
    }

    public async Task<List<WalletTypeRow>> GetExpenseByTypeAsync(
        long corpId, int days, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows     = await db.Database.SqlQuery<WalletTypeRaw>($"""
            SELECT "RefType",
                   COUNT(*) AS "Count",
                   COALESCE(ABS(SUM(CAST("Amount" AS REAL))), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
              AND CAST("Amount" AS REAL) < 0
              AND "RefType" != 'corporation_account_withdrawal'
            GROUP BY "RefType"
            ORDER BY ABS(SUM(CAST("Amount" AS REAL))) DESC
            """).ToListAsync(ct);
        return rows.Select(r => new WalletTypeRow(r.RefType, r.Count, (decimal)r.Amount)).ToList();
    }

    // â”€â”€ 24h Activity â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public sealed record Activity24hPlayerRow(string CharacterName, decimal Value);
    public sealed record Activity24hKillRow(
        int KillMailId, DateTimeOffset Time, bool IsLoss,
        int VictimShipTypeId, string ShipName,
        string SystemName, string ConstellationName, string RegionName,
        double SecurityStatus,
        long VictimCorpId, long VictimAllianceId,
        string VictimName, string VictimCorp, string VictimAlliance,
        long FbCorpId, long FbAllianceId,
        string FbName, string FbCorp, string FbAlliance,
        decimal IskValue = 0m);
    public sealed record Activity24hSummary(int PlayerCount, decimal TotalIncome, decimal TotalExpense);

    public async Task<Activity24hSummary> Get24hSummaryAsync(long corpId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddHours(-24));

        var walletRaw = await db.Database.SqlQuery<WalletSummaryRaw>($"""
            SELECT
                COALESCE(SUM(CASE WHEN CAST("Amount" AS REAL) > 0 AND "RefType" != 'corporation_account_withdrawal'
                                  THEN CAST("Amount" AS REAL) ELSE 0 END), 0) AS "TotalIncome",
                COALESCE(ABS(SUM(CASE WHEN CAST("Amount" AS REAL) < 0 AND "RefType" != 'corporation_account_withdrawal'
                                      THEN CAST("Amount" AS REAL) ELSE 0 END)), 0) AS "TotalExpense"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
            """).ToListAsync(ct);

        var rattingIds = await db.Database.SqlQuery<IdRaw>($"""
            SELECT DISTINCT "SecondPartyId" AS "Id"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts')
              AND "Date" >= {cutoff}
              AND "SecondPartyId" IS NOT NULL
            """).ToListAsync(ct);

        var industryIds = await db.Database.SqlQuery<IdRaw>($"""
            SELECT DISTINCT "FirstPartyId" AS "Id"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('industry_job_tax','manufacturing_tax','reprocessing_tax')
              AND "Date" >= {cutoff}
              AND "FirstPartyId" IS NOT NULL
              AND "FirstPartyId" != {corpId}
            """).ToListAsync(ct);

        var miningCutoff = SqlCutoff(DateTimeOffset.UtcNow.AddHours(-48));
        var miningIds = await db.Database.SqlQuery<IdRaw>($"""
            SELECT DISTINCT "CharacterId" AS "Id"
            FROM "EsiCorpMiningLedger"
            WHERE "CorporationId" = {corpId} AND "LastUpdated" >= {miningCutoff}
            """).ToListAsync(ct);

        var killAttackerIds = await db.Database.SqlQuery<IdRaw>($"""
            SELECT DISTINCT a."CharacterId" AS "Id"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            JOIN "KillMailAttackers" a ON a."KillMailId" = d."KillMailId"
            WHERE a."CorporationId" = {corpId} AND a."CharacterId" IS NOT NULL
              AND d."KillMailTime" >= {cutoff}
            """).ToListAsync(ct);

        var lossVictimIds = await db.Database.SqlQuery<IdRaw>($"""
            SELECT DISTINCT d."VictimCharId" AS "Id"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            WHERE d."VictimCorpId" = {corpId} AND d."VictimCharId" != 0
              AND d."KillMailTime" >= {cutoff}
            """).ToListAsync(ct);

        var allIds = rattingIds.Concat(industryIds).Concat(miningIds)
            .Concat(killAttackerIds).Concat(lossVictimIds)
            .Select(r => r.Id).Distinct().Count();
        var summary   = walletRaw.FirstOrDefault();
        return new Activity24hSummary(
            allIds,
            summary is not null ? (decimal)summary.TotalIncome : 0,
            summary is not null ? (decimal)summary.TotalExpense : 0);
    }

    public async Task<List<Activity24hPlayerRow>> Get24hTopRattersAsync(
        long corpId, IReadOnlySet<long> excludeIds, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddHours(-24));
        var rows     = await db.Database.SqlQuery<PlayerRaw>($"""
            SELECT "SecondPartyId" AS "CharacterId",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts')
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {cutoff}
              AND "SecondPartyId" IS NOT NULL
            GROUP BY "SecondPartyId"
            ORDER BY SUM(CAST("Amount" AS REAL)) DESC
            LIMIT 10
            """).ToListAsync(ct);
        var filtered = rows.Where(r => !excludeIds.Contains(r.CharacterId)).ToList();
        var names    = await ResolveNamesAsync(filtered.Select(r => r.CharacterId), ct);
        return filtered.Select(r => new Activity24hPlayerRow(
            names.TryGetValue(r.CharacterId, out var n) ? n : r.CharacterId.ToString(),
            (decimal)r.Amount)).ToList();
    }

    public async Task<List<Activity24hPlayerRow>> Get24hTopIndustryAsync(
        long corpId, IReadOnlySet<long> excludeIds, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddHours(-24));
        var rows     = await db.Database.SqlQuery<PlayerRaw>($"""
            SELECT "FirstPartyId" AS "CharacterId",
                   COALESCE(SUM(CAST("Amount" AS REAL)), 0) AS "Amount"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "RefType" IN ('industry_job_tax','manufacturing_tax','reprocessing_tax')
              AND CAST("Amount" AS REAL) > 0
              AND "Date" >= {cutoff}
              AND "FirstPartyId" IS NOT NULL
              AND "FirstPartyId" != {corpId}
            GROUP BY "FirstPartyId"
            ORDER BY SUM(CAST("Amount" AS REAL)) DESC
            LIMIT 10
            """).ToListAsync(ct);
        var filtered = rows.Where(r => !excludeIds.Contains(r.CharacterId)).ToList();
        var names    = await ResolveNamesAsync(filtered.Select(r => r.CharacterId), ct);
        return filtered.Select(r => new Activity24hPlayerRow(
            names.TryGetValue(r.CharacterId, out var n) ? n : r.CharacterId.ToString(),
            (decimal)r.Amount)).ToList();
    }

    public async Task<List<Activity24hPlayerRow>> Get24hTopMinersAsync(
        long corpId, IReadOnlySet<long> excludeIds, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        // Mining ledger LastUpdated is stored at midnight UTC on the date of mining,
        // so use 48h window to reliably capture yesterday's entries.
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddHours(-48));
        var rows     = await db.Database.SqlQuery<PlayerRaw>($"""
            SELECT m."CharacterId",
                   COALESCE(SUM(m."Quantity" * COALESCE(r."Value", 0)), 0) AS "Amount"
            FROM "EsiCorpMiningLedger" m
            LEFT JOIN "ReprocessingValues" r ON r."TypeId" = m."TypeId"
            WHERE m."CorporationId" = {corpId}
              AND m."LastUpdated" >= {cutoff}
            GROUP BY m."CharacterId"
            ORDER BY SUM(m."Quantity" * COALESCE(r."Value", 0)) DESC
            LIMIT 10
            """).ToListAsync(ct);
        var filtered = rows.Where(r => !excludeIds.Contains(r.CharacterId)).ToList();
        var names    = await ResolveNamesAsync(filtered.Select(r => r.CharacterId), ct);
        return filtered.Select(r => new Activity24hPlayerRow(
            names.TryGetValue(r.CharacterId, out var n) ? n : r.CharacterId.ToString(),
            (decimal)r.Amount)).ToList();
    }

    public async Task<List<Activity24hKillRow>> Get24hKillsAsync(
        long corpId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddHours(-24));
        var rows     = await db.Database.SqlQuery<Kill24hRaw>($"""
            SELECT d."KillMailId", d."KillMailTime", d."VictimCorpId", d."VictimAllianceId",
                   d."VictimShipTypeId", d."VictimCharId", d."SolarSystemId"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            WHERE d."KillMailTime" >= {cutoff}
            ORDER BY d."KillMailTime" DESC
            """).ToListAsync(ct);

        if (rows.Count == 0) return [];

        // Final blow attacker for each kill
        var fbRows = await db.Database.SqlQuery<Fb24hRaw>($"""
            SELECT a."KillMailId", a."CharacterId", a."CorporationId", a."AllianceId"
            FROM "KillMailAttackers" a
            WHERE a."FinalBlow" = 1
              AND a."KillMailId" IN (
                SELECT d."KillMailId"
                FROM "KillMailDetails" d
                JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                    AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
                WHERE d."KillMailTime" >= {cutoff}
              )
            """).ToListAsync(ct);
        var fbMap = fbRows.GroupBy(f => f.KillMailId).ToDictionary(g => g.Key, g => g.First());

        // SDE lookups
        var shipTypeIds = rows.Select(r => r.VictimShipTypeId).Distinct().ToList();
        var shipNames   = await db.SdeTypes.AsNoTracking()
            .Where(t => shipTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var sysIds  = rows.Select(r => r.SolarSystemId).Distinct().ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => sysIds.Contains(s.SolarSystemId))
            .ToListAsync(ct);
        var systemMap = systems.ToDictionary(s => s.SolarSystemId);

        var regionIds = systems.Select(s => s.RegionId).Distinct().ToList();
        var regionMap = await db.SdeRegions.AsNoTracking()
            .Where(r => regionIds.Contains(r.RegionId))
            .ToDictionaryAsync(r => r.RegionId, r => r.Name, ct);

        var constellationIds = systems.Select(s => s.ConstellationId).Distinct().ToList();
        var constellationMap = await db.SdeConstellations.AsNoTracking()
            .Where(c => constellationIds.Contains(c.ConstellationId))
            .ToDictionaryAsync(c => c.ConstellationId, c => c.Name, ct);

        // Entity name resolution
        var entityIds = new HashSet<long>();
        foreach (var r in rows)
        {
            if (r.VictimCharId != 0) entityIds.Add(r.VictimCharId);
            if (r.VictimCorpId != 0) entityIds.Add(r.VictimCorpId);
            if (r.VictimAllianceId.HasValue) entityIds.Add(r.VictimAllianceId.Value);
        }
        foreach (var f in fbRows)
        {
            if (f.CharacterId.HasValue)   entityIds.Add(f.CharacterId.Value);
            if (f.CorporationId.HasValue) entityIds.Add(f.CorporationId.Value);
            if (f.AllianceId.HasValue)    entityIds.Add(f.AllianceId.Value);
        }
        var names = await ResolveNamesAsync(entityIds, ct);
        string Res(long? id) => id.HasValue && id.Value != 0 && names.TryGetValue(id.Value, out var n) ? n : "";

        var killIds   = rows.Select(r => r.KillMailId).ToList();
        var iskValues = await GetKillIskValuesAsync(killIds, db, ct);

        return rows.Select(r =>
        {
            fbMap.TryGetValue(r.KillMailId, out var fb);
            systemMap.TryGetValue(r.SolarSystemId, out var sys);
            var regionName        = sys is not null && regionMap.TryGetValue(sys.RegionId, out var rn) ? rn : "";
            var constellationName = sys is not null && constellationMap.TryGetValue(sys.ConstellationId, out var cn) ? cn : "";
            iskValues.TryGetValue(r.KillMailId, out var isk);

            return new Activity24hKillRow(
                r.KillMailId, r.KillMailTime,
                r.VictimCorpId == corpId,
                r.VictimShipTypeId,
                shipNames.TryGetValue(r.VictimShipTypeId, out var sn) ? sn : $"Type {r.VictimShipTypeId}",
                sys?.Name ?? $"System {r.SolarSystemId}", constellationName, regionName,
                sys?.Security ?? 0.0,
                r.VictimCorpId, r.VictimAllianceId ?? 0L,
                Res(r.VictimCharId), Res(r.VictimCorpId), Res(r.VictimAllianceId),
                fb?.CorporationId ?? 0L, fb?.AllianceId ?? 0L,
                Res(fb?.CharacterId), Res(fb?.CorporationId), Res(fb?.AllianceId),
                isk);
        }).ToList();
    }

    //â”€â”€ Private raw SQL DTOs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // ── Wallet journal detail (ungrouped rows) ────────────────────────────────

    public async Task<List<WalletDetailRow>> GetIncomeJournalAsync(
        long corpId, int days, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows = await db.Database.SqlQuery<WalletDetailRaw>($"""
            SELECT "Date", "RefType", CAST("Amount" AS REAL) AS "Amount",
                   COALESCE("FirstPartyId", 0) AS "PartyId", '' AS "Reason"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
              AND CAST("Amount" AS REAL) > 0
            ORDER BY "Date" DESC
            LIMIT 500
            """).ToListAsync(ct);
        var ids   = rows.Select(r => r.PartyId).Where(id => id != 0).Distinct();
        var names = await ResolveNamesAsync(ids, ct);
        return rows.Select(r => new WalletDetailRow(r.Date, r.RefType, (decimal)r.Amount, r.PartyId,
            r.PartyId != 0 && names.TryGetValue(r.PartyId, out var n) ? n : "")).ToList();
    }

    public async Task<List<WalletDetailRow>> GetRattingJournalAsync(
        long corpId, DateTimeOffset since, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var sinceStr  = SqlCutoff(since);
        var rows = await db.Database.SqlQuery<WalletDetailRaw>($"""
            SELECT "Date", "RefType", CAST("Amount" AS REAL) AS "Amount",
                   COALESCE("SecondPartyId", 0) AS "PartyId", '' AS "Reason"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {sinceStr}
              AND "RefType" IN ('bounty_prizes','bounty_prize','ess_escrow_transfer','daily_goal_payouts')
              AND CAST("Amount" AS REAL) > 0
            ORDER BY "Date" DESC
            LIMIT 500
            """).ToListAsync(ct);
        var ids   = rows.Select(r => r.PartyId).Where(id => id != 0).Distinct();
        var names = await ResolveNamesAsync(ids, ct);
        return rows.Select(r => new WalletDetailRow(r.Date, r.RefType, (decimal)r.Amount, r.PartyId,
            r.PartyId != 0 && names.TryGetValue(r.PartyId, out var n) ? n : "")).ToList();
    }

    public async Task<List<WalletDetailRow>> GetIndustryJournalAsync(
        long corpId, DateTimeOffset since, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var sinceStr  = SqlCutoff(since);
        var rows = await db.Database.SqlQuery<WalletDetailRaw>($"""
            SELECT "Date", "RefType", CAST("Amount" AS REAL) AS "Amount",
                   COALESCE("FirstPartyId", 0) AS "PartyId", '' AS "Reason"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {sinceStr}
              AND "RefType" IN ('industry_job_tax','manufacturing_tax','reprocessing_tax')
              AND CAST("Amount" AS REAL) > 0
            ORDER BY "Date" DESC
            LIMIT 500
            """).ToListAsync(ct);
        var ids   = rows.Select(r => r.PartyId).Where(id => id != 0).Distinct();
        var names = await ResolveNamesAsync(ids, ct);
        return rows.Select(r => new WalletDetailRow(r.Date, r.RefType, (decimal)r.Amount, r.PartyId,
            r.PartyId != 0 && names.TryGetValue(r.PartyId, out var n) ? n : "")).ToList();
    }

    public async Task<List<WalletDetailRow>> GetDonationJournalAsync(
        long corpId, DateTimeOffset since, CancellationToken ct = default)
    {
        using var db  = _dbFactory.CreateDbContext();
        var sinceStr  = SqlCutoff(since);
        var rows = await db.Database.SqlQuery<WalletDetailRaw>($"""
            SELECT "Date", "RefType", CAST("Amount" AS REAL) AS "Amount",
                   COALESCE("FirstPartyId", 0) AS "PartyId",
                   COALESCE("Reason", '') AS "Reason"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {sinceStr}
              AND "RefType" = 'player_donation'
              AND CAST("Amount" AS REAL) > 0
            ORDER BY "Date" DESC
            LIMIT 500
            """).ToListAsync(ct);
        var ids   = rows.Select(r => r.PartyId).Where(id => id != 0).Distinct();
        var names = await ResolveNamesAsync(ids, ct);
        return rows.Select(r => new WalletDetailRow(r.Date, r.RefType, (decimal)r.Amount, r.PartyId,
            r.PartyId != 0 && names.TryGetValue(r.PartyId, out var n) ? n : "", r.Reason)).ToList();
    }

    public async Task<List<WalletDetailRow>> GetExpenseJournalAsync(
        long corpId, int days, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows = await db.Database.SqlQuery<WalletDetailRaw>($"""
            SELECT "Date", "RefType", ABS(CAST("Amount" AS REAL)) AS "Amount",
                   COALESCE("SecondPartyId", 0) AS "PartyId", '' AS "Reason"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
              AND CAST("Amount" AS REAL) < 0
              AND "RefType" != 'corporation_account_withdrawal'
            ORDER BY "Date" DESC
            LIMIT 500
            """).ToListAsync(ct);
        var ids   = rows.Select(r => r.PartyId).Where(id => id != 0).Distinct();
        var names = await ResolveNamesAsync(ids, ct);
        return rows.Select(r => new WalletDetailRow(r.Date, r.RefType, (decimal)r.Amount, r.PartyId,
            r.PartyId != 0 && names.TryGetValue(r.PartyId, out var n) ? n : "")).ToList();
    }

    public async Task<List<Activity24hKillRow>> GetKillsForPeriodAsync(
        long corpId, int days, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var rows     = await db.Database.SqlQuery<Kill24hRaw>($"""
            SELECT d."KillMailId", d."KillMailTime", d."VictimCorpId", d."VictimAllianceId",
                   d."VictimShipTypeId", d."VictimCharId", d."SolarSystemId"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            WHERE d."KillMailTime" >= {cutoff}
            ORDER BY d."KillMailTime" DESC
            LIMIT 500
            """).ToListAsync(ct);

        if (rows.Count == 0) return [];

        var fbRows = await db.Database.SqlQuery<Fb24hRaw>($"""
            SELECT a."KillMailId", a."CharacterId", a."CorporationId", a."AllianceId"
            FROM "KillMailAttackers" a
            WHERE a."FinalBlow" = 1
              AND a."KillMailId" IN (
                SELECT d2."KillMailId"
                FROM "KillMailDetails" d2
                JOIN "EsiKillMailRefs" r2 ON r2."KillMailId" = d2."KillMailId"
                    AND r2."OwnerId" = {corpId} AND r2."OwnerType" = 'corporation'
                WHERE d2."KillMailTime" >= {cutoff}
              )
            """).ToListAsync(ct);
        var fbMap = fbRows.GroupBy(f => f.KillMailId).ToDictionary(g => g.Key, g => g.First());

        var shipTypeIds = rows.Select(r => r.VictimShipTypeId).Distinct().ToList();
        var shipNames   = await db.SdeTypes.AsNoTracking()
            .Where(t => shipTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var sysIds  = rows.Select(r => r.SolarSystemId).Distinct().ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => sysIds.Contains(s.SolarSystemId)).ToListAsync(ct);
        var systemMap = systems.ToDictionary(s => s.SolarSystemId);

        var regionMap = await db.SdeRegions.AsNoTracking()
            .Where(r => systems.Select(s => s.RegionId).Contains(r.RegionId))
            .ToDictionaryAsync(r => r.RegionId, r => r.Name, ct);
        var constellationMap = await db.SdeConstellations.AsNoTracking()
            .Where(c => systems.Select(s => s.ConstellationId).Contains(c.ConstellationId))
            .ToDictionaryAsync(c => c.ConstellationId, c => c.Name, ct);

        var entityIds = new HashSet<long>();
        foreach (var r in rows)
        {
            if (r.VictimCharId != 0) entityIds.Add(r.VictimCharId);
            if (r.VictimCorpId != 0) entityIds.Add(r.VictimCorpId);
            if (r.VictimAllianceId.HasValue) entityIds.Add(r.VictimAllianceId.Value);
        }
        foreach (var f in fbRows)
        {
            if (f.CharacterId.HasValue)   entityIds.Add(f.CharacterId.Value);
            if (f.CorporationId.HasValue) entityIds.Add(f.CorporationId.Value);
            if (f.AllianceId.HasValue)    entityIds.Add(f.AllianceId.Value);
        }
        var names = await ResolveNamesAsync(entityIds, ct);
        string Res(long? id) => id.HasValue && id.Value != 0 && names.TryGetValue(id.Value, out var n) ? n : "";

        var killIds2   = rows.Select(r => r.KillMailId).ToList();
        var iskValues2 = await GetKillIskValuesAsync(killIds2, db, ct);

        return rows.Select(r =>
        {
            fbMap.TryGetValue(r.KillMailId, out var fb);
            systemMap.TryGetValue(r.SolarSystemId, out var sys);
            iskValues2.TryGetValue(r.KillMailId, out var isk);
            return new Activity24hKillRow(
                r.KillMailId, r.KillMailTime,
                r.VictimCorpId == corpId,
                r.VictimShipTypeId,
                shipNames.TryGetValue(r.VictimShipTypeId, out var sn) ? sn : $"Type {r.VictimShipTypeId}",
                sys?.Name ?? $"System {r.SolarSystemId}",
                sys is not null && constellationMap.TryGetValue(sys.ConstellationId, out var cn) ? cn : "",
                sys is not null && regionMap.TryGetValue(sys.RegionId, out var rn) ? rn : "",
                sys?.Security ?? 0.0,
                r.VictimCorpId, r.VictimAllianceId ?? 0L,
                Res(r.VictimCharId), Res(r.VictimCorpId), Res(r.VictimAllianceId),
                fb?.CorporationId ?? 0L, fb?.AllianceId ?? 0L,
                Res(fb?.CharacterId), Res(fb?.CorporationId), Res(fb?.AllianceId),
                isk);
        }).ToList();
    }

    // Killmails within the period where any of the given (personal) characters is the victim
    // or one of the attackers — scanning all stored killmails regardless of which ESI ref
    // (character or corporation) delivered them. IsLoss is true when a personal character is
    // the victim.
    public async Task<List<Activity24hKillRow>> GetPersonalKillsForPeriodAsync(
        IReadOnlyList<long> charIds, int days, CancellationToken ct = default)
    {
        if (charIds.Count == 0) return [];
        using var db = _dbFactory.CreateDbContext();
        var cutoff = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var idList = string.Join(",", charIds);

#pragma warning disable EF1002
        var rows = await db.Database.SqlQueryRaw<Kill24hRaw>($"""
            SELECT d."KillMailId", d."KillMailTime", d."VictimCorpId", d."VictimAllianceId",
                   d."VictimShipTypeId", d."VictimCharId", d."SolarSystemId"
            FROM "KillMailDetails" d
            WHERE d."KillMailTime" >= '{cutoff}'
              AND ( d."VictimCharId" IN ({idList})
                 OR EXISTS ( SELECT 1 FROM "KillMailAttackers" a
                             WHERE a."KillMailId" = d."KillMailId" AND a."CharacterId" IN ({idList}) ) )
            ORDER BY d."KillMailTime" DESC
            LIMIT 500
            """).ToListAsync(ct);
#pragma warning restore EF1002
        if (rows.Count == 0) return [];

        var killIds     = rows.Select(r => r.KillMailId).ToList();
        var killIdList  = string.Join(",", killIds);
#pragma warning disable EF1002
        var fbRows = await db.Database.SqlQueryRaw<Fb24hRaw>($"""
            SELECT a."KillMailId", a."CharacterId", a."CorporationId", a."AllianceId"
            FROM "KillMailAttackers" a
            WHERE a."FinalBlow" = 1 AND a."KillMailId" IN ({killIdList})
            """).ToListAsync(ct);
#pragma warning restore EF1002
        var fbMap = fbRows.GroupBy(f => f.KillMailId).ToDictionary(g => g.Key, g => g.First());

        var shipTypeIds = rows.Select(r => r.VictimShipTypeId).Distinct().ToList();
        var shipNames   = await db.SdeTypes.AsNoTracking()
            .Where(t => shipTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var sysIds  = rows.Select(r => r.SolarSystemId).Distinct().ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => sysIds.Contains(s.SolarSystemId)).ToListAsync(ct);
        var systemMap = systems.ToDictionary(s => s.SolarSystemId);

        var regionMap = await db.SdeRegions.AsNoTracking()
            .Where(r => systems.Select(s => s.RegionId).Contains(r.RegionId))
            .ToDictionaryAsync(r => r.RegionId, r => r.Name, ct);
        var constellationMap = await db.SdeConstellations.AsNoTracking()
            .Where(c => systems.Select(s => s.ConstellationId).Contains(c.ConstellationId))
            .ToDictionaryAsync(c => c.ConstellationId, c => c.Name, ct);

        var entityIds = new HashSet<long>();
        foreach (var r in rows)
        {
            if (r.VictimCharId != 0) entityIds.Add(r.VictimCharId);
            if (r.VictimCorpId != 0) entityIds.Add(r.VictimCorpId);
            if (r.VictimAllianceId.HasValue) entityIds.Add(r.VictimAllianceId.Value);
        }
        foreach (var f in fbRows)
        {
            if (f.CharacterId.HasValue)   entityIds.Add(f.CharacterId.Value);
            if (f.CorporationId.HasValue) entityIds.Add(f.CorporationId.Value);
            if (f.AllianceId.HasValue)    entityIds.Add(f.AllianceId.Value);
        }
        var names = await ResolveNamesAsync(entityIds, ct);
        string Res(long? id) => id.HasValue && id.Value != 0 && names.TryGetValue(id.Value, out var n) ? n : "";

        var charSet   = charIds.ToHashSet();
        var iskValues = await GetKillIskValuesAsync(killIds, db, ct);

        return rows.Select(r =>
        {
            fbMap.TryGetValue(r.KillMailId, out var fb);
            systemMap.TryGetValue(r.SolarSystemId, out var sys);
            iskValues.TryGetValue(r.KillMailId, out var isk);
            return new Activity24hKillRow(
                r.KillMailId, r.KillMailTime,
                charSet.Contains(r.VictimCharId),
                r.VictimShipTypeId,
                shipNames.TryGetValue(r.VictimShipTypeId, out var sn) ? sn : $"Type {r.VictimShipTypeId}",
                sys?.Name ?? $"System {r.SolarSystemId}",
                sys is not null && constellationMap.TryGetValue(sys.ConstellationId, out var cn) ? cn : "",
                sys is not null && regionMap.TryGetValue(sys.RegionId, out var rn) ? rn : "",
                sys?.Security ?? 0.0,
                r.VictimCorpId, r.VictimAllianceId ?? 0L,
                Res(r.VictimCharId), Res(r.VictimCorpId), Res(r.VictimAllianceId),
                fb?.CorporationId ?? 0L, fb?.AllianceId ?? 0L,
                Res(fb?.CharacterId), Res(fb?.CorporationId), Res(fb?.AllianceId),
                isk);
        }).ToList();
    }

    private sealed class WalletTypeRaw
    {
        public string RefType { get; set; } = "";
        public int    Count   { get; set; }
        public double Amount  { get; set; }
    }

    // EF Core SQLite stores DateTimeOffset with a space separator ("2026-06-28 12:00:00+00:00"),
    // but ToString("O") produces a T separator ("2026-06-28T12:00:00...+00:00").
    // SQLite lexicographic comparison treats space (32) < T (84), so entries on the same
    // calendar day as the cutoff but after the cutoff time are incorrectly excluded.
    // Use the EF Core stored format to make the comparison work correctly.
    private static string SqlCutoff(DateTimeOffset dt)
        => dt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    private sealed class WalletDetailRaw
    {
        public DateTimeOffset Date    { get; set; }
        public string         RefType { get; set; } = "";
        public double         Amount  { get; set; }
        public long           PartyId { get; set; }
        public string         Reason  { get; set; } = "";
    }

    private sealed class KillIskRaw
    {
        public int    KillMailId { get; set; }
        public double TotalIsk   { get; set; }
    }

    private static async Task<Dictionary<int, decimal>> GetKillIskValuesAsync(
        IReadOnlyList<int> killIds, AppDbContext db, CancellationToken ct)
    {
        if (killIds.Count == 0) return [];
        var idStr = string.Join(",", killIds);
#pragma warning disable EF1002
        var rows = await db.Database.SqlQueryRaw<KillIskRaw>($"""
            SELECT i."KillMailId",
                   SUM((COALESCE(i."QuantityDestroyed", 0) + COALESCE(i."QuantityDropped", 0))
                       * COALESCE(p."Midpoint", 0.0)) AS "TotalIsk"
            FROM "KillMailItems" i
            LEFT JOIN "MarketItemPrices" p ON p."TypeId" = i."ItemTypeId"
            WHERE i."KillMailId" IN ({idStr})
            GROUP BY i."KillMailId"
            """).ToListAsync(ct);
#pragma warning restore EF1002
        return rows.ToDictionary(r => r.KillMailId, r => (decimal)r.TotalIsk);
    }

    private sealed class WalletSummaryRaw
    {
        public double TotalIncome  { get; set; }
        public double TotalExpense { get; set; }
    }

    private sealed class IdRaw
    {
        public long Id { get; set; }
    }

    private sealed class Kill24hRaw
    {
        public int            KillMailId        { get; set; }
        public DateTimeOffset KillMailTime      { get; set; }
        public long           VictimCorpId      { get; set; }
        public long?          VictimAllianceId  { get; set; }
        public int            VictimShipTypeId  { get; set; }
        public long           VictimCharId      { get; set; }
        public int            SolarSystemId     { get; set; }
    }

    private sealed class Fb24hRaw
    {
        public int   KillMailId    { get; set; }
        public long? CharacterId   { get; set; }
        public long? CorporationId { get; set; }
        public long? AllianceId    { get; set; }
    }

    private sealed class WalletMonthRaw
    {
        public string Month           { get; set; } = "";
        public double RattingTax      { get; set; }
        public double MiningTax       { get; set; }
        public double Donations       { get; set; }
        public double IndustryTax     { get; set; }
        public double ContractIncome  { get; set; }
        public double MarketIncome    { get; set; }
        public double OtherIncome     { get; set; }
        public double MarketExpense   { get; set; }
        public double ContractExpense { get; set; }
        public double AccountWithdraw { get; set; }
        public double ProjectPayouts  { get; set; }
        public double OtherExpense    { get; set; }
    }

    private sealed class WalletDayRaw
    {
        public string Day            { get; set; } = "";
        public double RattingTax     { get; set; }
        public double MiningTax      { get; set; }
        public double Donations      { get; set; }
        public double IndustryTax    { get; set; }
        public double ContractIncome { get; set; }
        public double MarketIncome   { get; set; }
        public double OtherIncome    { get; set; }
    }

    private sealed class DailyAmountRaw
    {
        public string Day    { get; set; } = "";
        public double Amount { get; set; }
    }

    private sealed class TaxPayerRaw
    {
        public long   EntityId { get; set; }
        public double Amount   { get; set; }
    }

    private sealed class WalletExpenseDayRaw
    {
        public string Day             { get; set; } = "";
        public double MarketExpense   { get; set; }
        public double ContractExpense { get; set; }
        public double AccountWithdraw { get; set; }
        public double ProjectPayouts  { get; set; }
        public double OtherExpense    { get; set; }
    }

    private sealed class PlayerRaw
    {
        public long   CharacterId { get; set; }
        public double Amount      { get; set; }
    }

    private sealed class KillMonthRaw
    {
        public string Month  { get; set; } = "";
        public int    Kills  { get; set; }
        public int    Losses { get; set; }
    }

    private sealed class KillDayRaw
    {
        public string Day    { get; set; } = "";
        public int    Kills  { get; set; }
        public int    Losses { get; set; }
    }

    private sealed class CharCountRaw
    {
        public long CharacterId { get; set; }
        public int  Count       { get; set; }
    }

    private sealed class MonthCountRaw
    {
        public string Month { get; set; } = "";
        public long   Count { get; set; }
    }

    private sealed class MiningLedgerRaw
    {
        public string Date        { get; set; } = "";
        public long   CharacterId { get; set; }
        public int    TypeId      { get; set; }
        public string TypeName    { get; set; } = "";
        public long   Quantity    { get; set; }
    }

    private sealed class MonthRaw
    {
        public int Year  { get; set; }
        public int Month { get; set; }
    }

    // ── Corp offices ─────────────────────────────────────────────────────────

    private Dictionary<long, long>? _officeMapCache;
    private long?          _officeMapCorpId;
    private DateTimeOffset _officeMapCacheTime;

    public async Task<Dictionary<long, long>> GetCorpOfficeMapAsync(
        long corpId, CancellationToken ct = default)
    {
        if (_officeMapCache is not null && _officeMapCorpId == corpId &&
            DateTimeOffset.UtcNow - _officeMapCacheTime < TimeSpan.FromMinutes(10))
            return _officeMapCache;

        // Corp office containers (TypeId 27 = Office) appear in corp assets with
        // ItemId = office_id (as used in deliver_item project config) and LocationId = station_id.
        using var db = _dbFactory.CreateDbContext();
        var offices = await db.EsiAssets
            .Where(a => a.OwnerId == corpId && a.OwnerType == "corporation" && a.TypeId == 27)
            .Select(a => new { a.ItemId, a.LocationId })
            .ToListAsync(ct);

        _officeMapCache     = offices.ToDictionary(o => o.ItemId, o => o.LocationId);
        _officeMapCorpId    = corpId;
        _officeMapCacheTime = DateTimeOffset.UtcNow;
        return _officeMapCache;
    }

    // ── Standing projects CRUD ────────────────────────────────────────────────

    public async Task<List<CorpStandingProject>> GetStandingProjectsAsync(
        long corpId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.CorpStandingProjects
            .Where(p => p.CorporationId == corpId)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
    }

    public async Task<long> AddStandingProjectAsync(
        CorpStandingProject p, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        p.CreatedAt = DateTimeOffset.UtcNow;
        db.CorpStandingProjects.Add(p);
        await db.SaveChangesAsync(ct);
        return p.Id;
    }

    public async Task UpdateStandingProjectAsync(
        CorpStandingProject p, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        db.CorpStandingProjects.Update(p);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteStandingProjectAsync(long id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        await db.CorpStandingProjects.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
    }

    // ── SDE search helpers ────────────────────────────────────────────────────

    public async Task<List<SdeTypeResult>> SearchSdeTypesAsync(
        string query, CancellationToken ct = default)
    {
        if (query.Length < 2) return [];
        using var db = _dbFactory.CreateDbContext();
        return await db.SdeTypes
            .Where(t => EF.Functions.Like(t.Name, $"%{query}%") && t.Published)
            .OrderBy(t => t.Name)
            .Take(40)
            .Select(t => new SdeTypeResult(t.TypeId, t.Name))
            .ToListAsync(ct);
    }

    public async Task<List<SdeStationResult>> SearchSdeStationsAsync(
        string query, CancellationToken ct = default)
    {
        if (query.Length < 2) return [];
        using var db = _dbFactory.CreateDbContext();

        var npc = await db.SdeStations
            .Where(s => EF.Functions.Like(s.Name, $"%{query}%"))
            .OrderBy(s => s.Name).Take(40)
            .Select(s => new SdeStationResult((long)s.StationId, s.Name))
            .ToListAsync(ct);

        var player = await db.EsiStructureNames
            .Where(s => EF.Functions.Like(s.Name, $"%{query}%"))
            .OrderBy(s => s.Name).Take(40)
            .Select(s => new SdeStationResult(s.StructureId, s.Name))
            .ToListAsync(ct);

        var corp = await db.EsiCorpStructures
            .Where(s => EF.Functions.Like(s.Name, $"%{query}%"))
            .OrderBy(s => s.Name).Take(40)
            .Select(s => new SdeStationResult(s.StructureId, s.Name))
            .ToListAsync(ct);

        return npc
            .Concat(player)
            .Concat(corp)
            .GroupBy(s => s.StationId)
            .Select(g => g.First())
            .OrderBy(s => s.Name)
            .Take(40)
            .ToList();
    }

    public async Task<List<SdeSystemResult>> SearchSdeSystemsAsync(
        string query, CancellationToken ct = default)
    {
        if (query.Length < 2) return [];
        using var db = _dbFactory.CreateDbContext();
        return await db.SdeSolarSystems
            .Where(s => EF.Functions.Like(s.Name, $"%{query}%") && !s.IsWormhole)
            .OrderBy(s => s.Name)
            .Take(40)
            .Select(s => new SdeSystemResult(s.SolarSystemId, s.Name))
            .ToListAsync(ct);
    }

    public async Task<List<SdeRegionResult>> SearchSdeRegionsAsync(
        string query, CancellationToken ct = default)
    {
        if (query.Length < 2) return [];
        using var db = _dbFactory.CreateDbContext();
        return await db.SdeRegions
            .Where(r => EF.Functions.Like(r.Name, $"%{query}%") && !r.IsWormhole)
            .OrderBy(r => r.Name)
            .Take(40)
            .Select(r => new SdeRegionResult(r.RegionId, r.Name))
            .ToListAsync(ct);
    }

    public async Task<List<SdeConstellationResult>> SearchSdeConstellationsAsync(
        string query, CancellationToken ct = default)
    {
        if (query.Length < 2) return [];
        using var db = _dbFactory.CreateDbContext();
        return await db.SdeConstellations
            .Where(c => EF.Functions.Like(c.Name, $"%{query}%") && !c.IsWormhole)
            .OrderBy(c => c.Name)
            .Take(40)
            .Select(c => new SdeConstellationResult(c.ConstellationId, c.Name))
            .ToListAsync(ct);
    }

    private async Task<List<SdeSystemResult>> GetSystemsInRegionAsync(
        int regionId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.SdeSolarSystems
            .Where(s => s.RegionId == regionId && !s.IsWormhole)
            .OrderBy(s => s.Name)
            .Select(s => new SdeSystemResult(s.SolarSystemId, s.Name))
            .ToListAsync(ct);
    }

    private async Task<List<SdeSystemResult>> GetSystemsInConstellationAsync(
        int constId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.SdeSolarSystems
            .Where(s => s.ConstellationId == constId && !s.IsWormhole)
            .OrderBy(s => s.Name)
            .Select(s => new SdeSystemResult(s.SolarSystemId, s.Name))
            .ToListAsync(ct);
    }

    // ADM data cached for 30 minutes
    private Dictionary<int, double>? _sovAdmCache;
    private DateTimeOffset _sovAdmCacheTime;

    public async Task<Dictionary<int, double>> GetSovAdmLevelsAsync(CancellationToken ct = default)
    {
        if (_sovAdmCache is not null &&
            DateTimeOffset.UtcNow - _sovAdmCacheTime < TimeSpan.FromMinutes(30))
            return _sovAdmCache;
        try
        {
            var structures = await _esi.GetSovStructuresAsync(ct) ?? [];
            var dict = structures
                .Where(s => s.VulnerabilityOccupancyLevel.HasValue)
                .GroupBy(s => s.SolarSystemId)
                .ToDictionary(g => g.Key, g => g.Max(s => s.VulnerabilityOccupancyLevel!.Value));
            _sovAdmCache     = dict;
            _sovAdmCacheTime = DateTimeOffset.UtcNow;
            return dict;
        }
        catch { return _sovAdmCache ?? []; }
    }

    // ── Standing project grid row builder ─────────────────────────────────────

    public async Task<List<StandingProjectGridRow>> BuildMaintainGridRowsAsync(
        long corpId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();

        var standing = await db.CorpStandingProjects
            .Where(p => p.CorporationId == corpId)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        if (standing.Count == 0) return [];

        var activeProjects = await db.EsiCorpProjects
            .Where(p => p.CorporationId == corpId && p.State == "Active")
            .ToListAsync(ct);

        Dictionary<long, long> officeMap;
        try   { officeMap = await GetCorpOfficeMapAsync(corpId, ct); }
        catch { officeMap = []; }
        var deliverConfigs  = ParseDeliverItemConfigs(activeProjects, officeMap);
        var destroyConfigs  = ParseDestroyNpcConfigs(activeProjects);

        bool needsAdm = standing.Any(p => p.ProjectType == "destroy_npc" &&
                                          p.ScopeType is "region_adm" or "constellation_adm");
        var adm = needsAdm ? await GetSovAdmLevelsAsync(ct) : [];

        var rows = new List<StandingProjectGridRow>();

        foreach (var sp in standing)
        {
            if (sp.ProjectType == "deliver_item")
            {
                var match = deliverConfigs.FirstOrDefault(d =>
                    sp.ItemTypeId.HasValue && d.TypeIds.Contains(sp.ItemTypeId.Value) &&
                    sp.StationId.HasValue  && d.StationIds.Contains(sp.StationId.Value));
                var deliverRemaining = match is not null ? match.ProgressDesired - match.ProgressCurrent : 0L;
                rows.Add(new StandingProjectGridRow(
                    DbId                : sp.Id,
                    TypeDisplay         : "Deliver Item",
                    TargetDisplay       : sp.ItemTypeName ?? "",
                    DestDisplay         : sp.StationName,
                    ExpandedSystemId    : null,
                    MatchStatus         : match is not null ? "matched" : "not_active",
                    MatchedName         : match?.ProjectName ?? "",
                    RemainingText       : match is not null ? FormatRemaining(deliverRemaining) : "",
                    RemainingPayoutText : match is not null ? FormatPayout(deliverRemaining, match.RewardPerContrib) : "",
                    ItemTypeId          : sp.ItemTypeId,
                    ItemTypeName        : sp.ItemTypeName ?? ""));
            }
            else // destroy_npc
            {
                switch (sp.ScopeType)
                {
                    case "system":
                    {
                        var match = destroyConfigs.FirstOrDefault(d =>
                            sp.SolarSystemId.HasValue && d.SystemIds.Contains(sp.SolarSystemId.Value));
                        var sysRemaining = match is not null ? match.ProgressDesired - match.ProgressCurrent : 0L;
                        rows.Add(new StandingProjectGridRow(
                            DbId                : sp.Id,
                            TypeDisplay         : "Destroy NPC",
                            TargetDisplay       : sp.SolarSystemName,
                            DestDisplay         : "",
                            ExpandedSystemId    : sp.SolarSystemId,
                            MatchStatus         : match is not null ? "matched" : "not_active",
                            MatchedName         : match?.ProjectName ?? "",
                            RemainingText       : match is not null ? FormatRemaining(sysRemaining) : "",
                            RemainingPayoutText : match is not null ? FormatPayout(sysRemaining, match.RewardPerContrib) : "",
                            ItemTypeId          : null,
                            ItemTypeName        : ""));
                        break;
                    }

                    case "region_adm":
                    case "constellation_adm":
                    {
                        var systems = sp.ScopeType == "region_adm" && sp.ScopeEntityId.HasValue
                            ? await GetSystemsInRegionAsync(sp.ScopeEntityId.Value, ct)
                            : sp.ScopeEntityId.HasValue
                                ? await GetSystemsInConstellationAsync(sp.ScopeEntityId.Value, ct)
                                : [];

                        var minAdm     = sp.MinAdm ?? 6.0;
                        var scopeLabel = sp.ScopeType == "region_adm"
                            ? $"Region: {sp.ScopeEntityName} (ADM < {minAdm:F1})"
                            : $"Const: {sp.ScopeEntityName} (ADM < {minAdm:F1})";

                        var qualifying = systems
                            .Where(s => adm.TryGetValue(s.SystemId, out var a) && a < minAdm)
                            .ToList();

                        if (qualifying.Count == 0)
                        {
                            rows.Add(new StandingProjectGridRow(
                                DbId                : sp.Id,
                                TypeDisplay         : "Destroy NPC",
                                TargetDisplay       : scopeLabel,
                                DestDisplay         : "",
                                ExpandedSystemId    : null,
                                MatchStatus         : "no_systems",
                                MatchedName         : "",
                                RemainingText       : "",
                                RemainingPayoutText : "",
                                ItemTypeId          : null,
                                ItemTypeName        : ""));
                        }
                        else
                        {
                            foreach (var sys in qualifying)
                            {
                                var match = destroyConfigs.FirstOrDefault(
                                    d => d.SystemIds.Contains(sys.SystemId));
                                var admRemaining = match is not null ? match.ProgressDesired - match.ProgressCurrent : 0L;
                                rows.Add(new StandingProjectGridRow(
                                    DbId                : sp.Id,
                                    TypeDisplay         : "Destroy NPC",
                                    TargetDisplay       : scopeLabel,
                                    DestDisplay         : sys.Name,
                                    ExpandedSystemId    : sys.SystemId,
                                    MatchStatus         : match is not null ? "matched" : "not_active",
                                    MatchedName         : match?.ProjectName ?? "",
                                    RemainingText       : match is not null ? FormatRemaining(admRemaining) : "",
                                    RemainingPayoutText : match is not null ? FormatPayout(admRemaining, match.RewardPerContrib) : "",
                                    ItemTypeId          : null,
                                    ItemTypeName        : ""));
                            }
                        }
                        break;
                    }
                }
            }
        }

        return rows;
    }

    // Counts standing projects with no currently-matching active ESI project (used for the
    // Overview alert). A project counts as inactive only if none of its grid rows matched —
    // an ADM-scope project with several qualifying systems is inactive only if all of them are.
    public async Task<int> CountInactiveStandingProjectsAsync(long corpId, CancellationToken ct = default)
    {
        var rows = await BuildMaintainGridRowsAsync(corpId, ct);
        return rows.GroupBy(r => r.DbId).Count(g => g.All(r => r.MatchStatus != "matched"));
    }

    private sealed record DeliverConfig(
        string        ProjectName,
        HashSet<int>  TypeIds,
        HashSet<long> StationIds,
        long          ProgressDesired,
        long          ProgressCurrent,
        double        RewardPerContrib);

    private sealed record DestroyNpcConfig(
        string       ProjectName,
        HashSet<int> SystemIds,
        long         ProgressDesired,
        long         ProgressCurrent,
        double       RewardPerContrib);

    private static List<DeliverConfig> ParseDeliverItemConfigs(
        List<CorpProject> projects, IReadOnlyDictionary<long, long> officeMap)
    {
        var result = new List<DeliverConfig>();
        foreach (var p in projects.Where(p => p.ConfigType == "deliver_item" &&
                                               !string.IsNullOrEmpty(p.ConfigurationJson)))
        {
            try
            {
                using var doc = JsonDocument.Parse(p.ConfigurationJson!);
                if (!doc.RootElement.TryGetProperty("deliver_item", out var inner)) continue;

                var typeIds    = new HashSet<int>();
                var stationIds = new HashSet<long>();

                if (inner.TryGetProperty("items", out var items))
                    foreach (var item in items.EnumerateArray())
                        if (item.TryGetProperty("type_id", out var tid))
                            typeIds.Add(tid.GetInt32());

                if (inner.TryGetProperty("docking_locations", out var dlocs))
                    foreach (var loc in dlocs.EnumerateArray())
                    {
                        if (loc.TryGetProperty("station_id",   out var sid)) stationIds.Add(sid.GetInt64());
                        if (loc.TryGetProperty("structure_id", out var rid)) stationIds.Add(rid.GetInt64());
                    }
                if (inner.TryGetProperty("office_id", out var oid))
                {
                    var officeId = oid.GetInt64();
                    // office_id is a corp office item ID; resolve to the actual location_id
                    stationIds.Add(officeMap.TryGetValue(officeId, out var locId) ? locId : officeId);
                }

                result.Add(new DeliverConfig(p.Name, typeIds, stationIds,
                                             p.ProgressDesired, p.ProgressCurrent, p.RewardPerContrib));
            }
            catch { }
        }
        return result;
    }

    private static List<DestroyNpcConfig> ParseDestroyNpcConfigs(List<CorpProject> projects)
    {
        var result = new List<DestroyNpcConfig>();
        foreach (var p in projects.Where(p => p.ConfigType == "destroy_npc" &&
                                               !string.IsNullOrEmpty(p.ConfigurationJson)))
        {
            try
            {
                using var doc = JsonDocument.Parse(p.ConfigurationJson!);
                if (!doc.RootElement.TryGetProperty("destroy_npc", out var inner)) continue;

                var systemIds = new HashSet<int>();
                if (inner.TryGetProperty("locations", out var locs))
                    foreach (var loc in locs.EnumerateArray())
                        if (loc.TryGetProperty("solar_system_id", out var sid))
                            systemIds.Add(sid.GetInt32());

                result.Add(new DestroyNpcConfig(p.Name, systemIds,
                                                p.ProgressDesired, p.ProgressCurrent, p.RewardPerContrib));
            }
            catch { }
        }
        return result;
    }

    private static string FormatRemaining(long remaining)
    {
        if (remaining <= 0) return "Complete";
        if (remaining >= 1_000_000_000) return $"{remaining / 1_000_000_000.0:F2}B";
        if (remaining >= 1_000_000)     return $"{remaining / 1_000_000.0:F2}M";
        if (remaining >= 1_000)         return $"{remaining / 1_000.0:F1}K";
        return remaining.ToString("N0");
    }

    private static string FormatPayout(long remaining, double rewardPerContrib)
    {
        if (remaining <= 0 || rewardPerContrib <= 0) return "";
        var isk = remaining * rewardPerContrib;
        if (isk >= 1_000_000_000) return $"{isk / 1_000_000_000.0:F2}B ISK";
        if (isk >= 1_000_000)     return $"{isk / 1_000_000.0:F2}M ISK";
        if (isk >= 1_000)         return $"{isk / 1_000.0:F1}K ISK";
        return $"{isk:F0} ISK";
    }
}

