using System.Text.Json;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

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
public sealed record RankedPlayerRow(int Rank, long CharacterId, decimal Amount, double Percent = 0);
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
    string MatchStatus,       // “matched” | “not_active” | “no_systems” | “no_office”
    string MatchedName,
    string RemainingText,
    string RemainingPayoutText,
    string RemainingPercentText,
    double RemainingPercentValue,   // percent of the target still outstanding; -1 when not applicable
    int?   ItemTypeId,
    string ItemTypeName,
    /// <summary>Where a delivery goes, so the location cell can open it. Null on the destroy-NPC
    /// project types, whose destination names a region or constellation rather than a place you
    /// dock at.</summary>
    long?  StationId = null,
    /// <summary>NPC station rather than player structure — the two have different browsers.
    /// Resolved against SdeStations rather than guessed from the id, since the ranges are not a
    /// reliable tell.</summary>
    bool   StationIsNpc = false,
    /// <summary>Why a status is what it is, where the status alone is not enough to act on.
    /// Carries the fetch error behind "no_adm", so a broken call names itself instead of
    /// presenting as an empty scope.</summary>
    string StatusNote = "",
    /// <summary>The system this row is about, and the region holding it. Filled for any row that
    /// names a system; a delivery row names a station instead and leaves both empty.</summary>
    string SystemName = "",
    string RegionName = "",
    /// <summary>The system's occupancy level, where there is one. Null on a row that names no
    /// system, and on a system nobody holds.</summary>
    double? Adm = null,
    /// <summary>When a project matching this line was last COMPLETED, so an absent one can be
    /// read as "gone since" rather than merely absent. Null where none ever was.</summary>
    DateTimeOffset? LastDone = null);

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

        // Distinct active players per month.
        //
        // Deliberately as broad as the stored data allows: any dated activity attributable
        // to a character counts. There is no login signal available — ESI exposes corp
        // member last-logon but this app does not store it — so this measures "did
        // something the corp can see", not "logged in".
        //
        // Only corp-wide sources are used. Per-character tables (personal mining ledger,
        // skill queue, mail, notifications, planetary colonies, game/chat logs) exist only
        // for characters whose tokens we hold, so counting them would give a handful of
        // members many extra ways to register while everyone else had few — an inconsistent
        // measure rather than a broader one.
        var playerRows = await db.Database.SqlQuery<MonthCountRaw>($"""
            SELECT "Month", COUNT(DISTINCT "CharId") AS "Count"
            FROM (
              -- Any wallet movement with the character as a counterparty: ratting bounties,
              -- industry and reprocessing tax, donations (which is how mining is billed),
              -- contract payments, project payouts, medals — rather than a fixed RefType list.
              SELECT strftime('%Y-%m', "Date") AS "Month", "FirstPartyId" AS "CharId"
              FROM "EsiWalletJournal"
              WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
                AND "Date" >= {cutoff} AND "FirstPartyId" IS NOT NULL AND "FirstPartyId" != {corpId}
              UNION
              SELECT strftime('%Y-%m', "Date") AS "Month", "SecondPartyId" AS "CharId"
              FROM "EsiWalletJournal"
              WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
                AND "Date" >= {cutoff} AND "SecondPartyId" IS NOT NULL AND "SecondPartyId" != {corpId}
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
              UNION
              -- Installed a corp industry job
              SELECT strftime('%Y-%m', "StartDate") AS "Month", "InstallerId" AS "CharId"
              FROM "EsiIndustryJobs"
              WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
                AND "StartDate" >= {cutoff} AND "InstallerId" != 0
              UNION
              -- Issued or accepted a corp contract
              SELECT strftime('%Y-%m', "DateIssued") AS "Month", "IssuerId" AS "CharId"
              FROM "EsiContracts"
              WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
                AND "DateIssued" >= {cutoff} AND "IssuerId" != 0
              UNION
              SELECT strftime('%Y-%m', "DateAccepted") AS "Month", "AcceptorId" AS "CharId"
              FROM "EsiContracts"
              WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
                AND "DateAccepted" >= {cutoff} AND "AcceptorId" IS NOT NULL AND "AcceptorId" != 0
              UNION
              -- Created a corp project
              SELECT strftime('%Y-%m', "Created") AS "Month", "CreatorId" AS "CharId"
              FROM "EsiCorpProjects"
              WHERE "CorporationId" = {corpId} AND "Created" >= {cutoff} AND "CreatorId" IS NOT NULL
              UNION
              -- Created a corp medal
              SELECT strftime('%Y-%m', "CreatedAt") AS "Month", "CreatorId" AS "CharId"
              FROM "EsiCorpMedals"
              WHERE "CorporationId" = {corpId} AND "CreatedAt" >= {cutoff} AND "CreatorId" IS NOT NULL
              UNION
              -- Logged in. Accumulated by watching member tracking change between polls, so
              -- it only covers months since that polling began — see CorpMemberSession.
              SELECT strftime('%Y-%m', "LogonDate") AS "Month", "CharacterId" AS "CharId"
              FROM "EsiCorpMemberSessions"
              WHERE "CorporationId" = {corpId} AND "LogonDate" >= {cutoff}
            )
            WHERE "CharId" IS NOT NULL AND "CharId" > 0
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

    public async Task<List<(long CharacterId, string Name, decimal IskPayout, double Percent)>> GetTopProjectContributorsAsync(
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

        // % of total across all (post-exclude) contributors, not just the top 10.
        decimal total = filtered.Sum(r => r.IskPayout);
        double Pct(decimal v) => total > 0 ? (double)(v / total) * 100.0 : 0.0;

        if (filtered.Count <= 10)
            return filtered.Select(r => (r.CharacterId, r.Name, r.IskPayout, Pct(r.IskPayout))).ToList();

        var threshold = filtered[9].IskPayout;
        return filtered
            .TakeWhile(r => r.IskPayout >= threshold)
            .Select(r => (r.CharacterId, r.Name, r.IskPayout, Pct(r.IskPayout)))
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

        // Persistent id → name cache first. Without this every Killmail Browser page was
        // re-resolving ~160 entities over ESI on each refresh, because universe-wide kills
        // involve characters and corps that are not our own and so never appear in the
        // Characters table below. Names do not change, so a cached row is always good.
        var cachedNames = await db.UniverseNames.AsNoTracking()
            .Where(u => idList.Contains(u.EntityId))
            .ToDictionaryAsync(u => u.EntityId, u => u.Name, ct);
        foreach (var kv in cachedNames) result[kv.Key] = kv.Value;

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

        if (remaining.Count == 0) return result;

        var fetched = new List<EsiUniverseName>();
        const int ChunkSize = 200;
        for (int offset = 0; offset < remaining.Count; offset += ChunkSize)
            await ResolveChunkAsync(remaining.Skip(offset).Take(ChunkSize).ToList());

        foreach (var n in fetched) result[n.Id] = n.Name;
        await PersistNamesAsync(db, fetched, ct);
        return result;

        // One bad ID makes ESI reject the whole request, so a failed chunk is split and
        // retried rather than expanded into one call per ID. Halving isolates the offender
        // in ~log2(n) extra calls; the old per-ID fallback turned a single 200-ID chunk
        // into 200 sequential requests, which on a slow or degraded ESI took minutes.
        async Task ResolveChunkAsync(List<int> chunk)
        {
            if (chunk.Count == 0) return;
            try
            {
                fetched.AddRange(await _esi.GetNamesAsync(chunk, ct));
            }
            catch (Exception ex)
            {
                if (chunk.Count == 1)
                {
                    System.Diagnostics.Debug.WriteLine($"[ESI] ID {chunk[0]} not resolved ({ex.Message})");
                    return;
                }
                var half = chunk.Count / 2;
                await ResolveChunkAsync(chunk.Take(half).ToList());
                await ResolveChunkAsync(chunk.Skip(half).ToList());
            }
        }
    }

    /// <summary>Writes freshly-resolved names into the persistent cache so no session ever
    /// pays for them again. Insert-only — an existing row is never rewritten, since the
    /// name behind an ID does not change.</summary>
    private async Task PersistNamesAsync(
        AppDbContext db, IReadOnlyCollection<EsiUniverseName> names, CancellationToken ct)
    {
        if (names.Count == 0) return;
        try
        {
            var ids  = names.Select(n => (long)n.Id).ToList();
            var have = (await db.UniverseNames.AsNoTracking()
                    .Where(u => ids.Contains(u.EntityId))
                    .Select(u => u.EntityId).ToListAsync(ct))
                .ToHashSet();

            var fresh = names
                .Where(n => !have.Contains(n.Id))
                .GroupBy(n => n.Id)
                .Select(g => new UniverseName
                {
                    EntityId = g.Key,
                    Name     = g.First().Name,
                    Category = g.First().Category,
                    PulledAt = DateTimeOffset.UtcNow,
                })
                .ToList();

            if (fresh.Count > 0)
            {
                db.UniverseNames.AddRange(fresh);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: the names were already resolved and returned to the caller, so
            // a failed cache write costs a repeat lookup later, nothing more.
            System.Diagnostics.Debug.WriteLine($"[Names] cache write failed: {ex.Message}");
        }
    }

    // â”€â”€ Tie-inclusive Top 10 â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static List<RankedPlayerRow> ApplyTop10WithTies(
        List<PlayerRaw> rawRows, IReadOnlySet<long>? excludeIds)
    {
        var filtered = rawRows
            .Where(r => excludeIds?.Contains(r.CharacterId) != true)
            .ToList();

        // % of total is measured against the whole (post-exclude) population, not just the top 10.
        decimal total = filtered.Sum(r => (decimal)r.Amount);

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
            var pct = total > 0 ? (double)(amount / total) * 100.0 : 0.0;
            result.Add(new RankedPlayerRow(currentRank, r.CharacterId, amount, pct));
            prevAmount = amount;
        }

        return result;
    }

    // ── Income / Expense by type ──────────────────────────────────────────────
    //
    // These used to drop every corporation_account_withdrawal row, which hid the largest
    // category outright — measured on a 30-day window: 91.6B of withdrawals missing
    // against 52.8B of everything the tab did show. The intent was to keep inter-division
    // transfers out, so only those are excluded now: a divisional move has the corp as both
    // parties, whereas ISK genuinely leaving or entering the corp does not.

    public sealed record WalletTypeRow(string RefType, int Count, decimal Amount);

    // ── Monthly summary ───────────────────────────────────────────────────────

    /// <summary>One month's figures. Wallet is null when the month has no journal entries
    /// at all, which is why every accessor below tolerates it.</summary>
    public sealed record MonthFigures(
        WalletMonthRow? Wallet,
        int             Kills,
        int             Losses,
        decimal         IskDestroyed,
        decimal         IskLost,
        long            UnitsMined,
        decimal         MiningValue,
        int             PlayersActive,
        int             ProjectsCreated,
        decimal         ProjectsCreatedValue,
        int             ProjectsCompleted,
        decimal         ProjectsCompletedValue)
    {
        public static readonly MonthFigures Empty =
            new(null, 0, 0, 0m, 0m, 0, 0m, 0, 0, 0m, 0, 0m);

        public decimal TotalIncome  => Wallet?.TotalIncome  ?? 0m;
        public decimal TotalExpense => Wallet?.TotalExpense ?? 0m;
        public decimal Net          => TotalIncome - TotalExpense;

        /// <summary>ISK efficiency the way killboards report it — destroyed as a share of
        /// everything that changed hands. Null when nothing was destroyed either way,
        /// because both 0% and 100% would misrepresent an empty month.</summary>
        public double? IskEfficiency =>
            IskDestroyed + IskLost <= 0 ? null
            : (double)(IskDestroyed / (IskDestroyed + IskLost)) * 100.0;
    }

    /// <summary>The selected month alongside the one before it, so every line can show
    /// movement rather than only the handful that used to carry a delta.</summary>
    public sealed record MonthSummary(int Year, int Month, MonthFigures Current, MonthFigures Previous);

    /// <summary>
    /// Aggregate for a single calendar month (UTC).
    ///
    /// Wallet totals, kill counts and active-player counts come from the existing
    /// per-month queries rather than fresh SQL, so the RefType classification and the
    /// definition of "active" can never drift from the Wallet and Monthly Activity views.
    /// Those queries take a lookback in months, so the distance from now is computed and
    /// the target month picked out of the result.
    /// </summary>
    public async Task<MonthSummary> GetMonthSummaryAsync(
        long corpId, int year, int month, CancellationToken ct = default)
    {
        var now       = DateTimeOffset.UtcNow;
        var monthsAgo = (now.Year * 12 + now.Month) - (year * 12 + month);
        // +2 so the preceding month is in range too; floor of 2 covers the current month.
        var lookback  = Math.Max(2, monthsAgo + 2);

        var key     = $"{year:D4}-{month:D2}";
        var prev    = new DateTime(year, month, 1).AddMonths(-1);
        var prevKey = $"{prev.Year:D4}-{prev.Month:D2}";

        var walletMonths = await GetWalletMonthsAsync(corpId, lookback, ct);
        var killMonths   = await GetKillMonthsAsync(corpId, lookback, ct);
        var activity     = await GetMonthlyActivityAsync(corpId, lookback, ct);
        var projects     = await GetMonthProjectStatsAsync(corpId, ct);

        var from     = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var prevFrom = from.AddMonths(-1);

        async Task<MonthFigures> Build(string monthKey, DateTimeOffset monthStart)
        {
            var (destroyed, lost) = await GetMonthKillIskAsync(corpId, monthStart, ct);
            var miningValue       = await GetMonthMiningValueAsync(corpId, monthStart, ct);
            var kills             = killMonths.FirstOrDefault(k => k.Month == monthKey);
            var act               = activity.FirstOrDefault(a => a.Month == monthKey);
            var proj              = projects.GetValueOrDefault(monthKey);

            return new MonthFigures(
                walletMonths.FirstOrDefault(w => w.Month == monthKey),
                kills?.Kills  ?? 0, kills?.Losses ?? 0,
                destroyed, lost,
                act?.UnitsMined    ?? 0, miningValue,
                act?.PlayersActive ?? 0,
                proj.Created, proj.CreatedValue, proj.Completed, proj.CompletedValue);
        }

        return new MonthSummary(year, month, await Build(key, from), await Build(prevKey, prevFrom));
    }

    /// <summary>
    /// Projects created and completed per month, with their reward values.
    ///
    /// "Completed" is dated by LastModified, which is the closest thing the corp-projects
    /// data carries to a completion timestamp — a project edited after it completed would
    /// be counted in the wrong month. The reward figure is what was actually handed out
    /// (initial minus remaining); the wallet's project_payouts line is the authoritative
    /// ISK-out number and is reported separately under Expenses.
    /// </summary>
    private async Task<Dictionary<string, (int Created, decimal CreatedValue, int Completed, decimal CompletedValue)>>
        GetMonthProjectStatsAsync(long corpId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();

        var created = await db.Database.SqlQuery<MonthProjectRaw>($"""
            SELECT strftime('%Y-%m', "Created") AS "Month",
                   COUNT(*) AS "Count",
                   COALESCE(SUM("RewardInitial"), 0) AS "Value"
            FROM "EsiCorpProjects"
            WHERE "CorporationId" = {corpId} AND "Created" IS NOT NULL AND "Created" != ''
            GROUP BY "Month"
            """).ToListAsync(ct);

        var completed = await db.Database.SqlQuery<MonthProjectRaw>($"""
            SELECT strftime('%Y-%m', "LastModified") AS "Month",
                   COUNT(*) AS "Count",
                   COALESCE(SUM("RewardInitial" - "RewardRemaining"), 0) AS "Value"
            FROM "EsiCorpProjects"
            WHERE "CorporationId" = {corpId} AND "State" = 'Completed'
              AND "LastModified" IS NOT NULL AND "LastModified" != ''
            GROUP BY "Month"
            """).ToListAsync(ct);

        var result = new Dictionary<string, (int, decimal, int, decimal)>();
        foreach (var r in created)
            result[r.Month] = (r.Count, (decimal)r.Value, 0, 0m);
        foreach (var r in completed)
        {
            var prior = result.GetValueOrDefault(r.Month);
            result[r.Month] = (prior.Item1, prior.Item2, r.Count, (decimal)r.Value);
        }
        return result;
    }

    private sealed class MonthProjectRaw
    {
        public string Month { get; set; } = "";
        public int    Count { get; set; }
        public double Value { get; set; }
    }

    /// <summary>
    /// ISK destroyed vs lost for the month. A kill counts as a loss when the victim belonged to
    /// this corp.
    ///
    /// <para>⚠️ The valuation is no longer summed in SQL. It was, for a good reason — a busy month
    /// runs to thousands of kills and only the totals are wanted — but that SQL priced blueprint
    /// COPIES at the original's market price, because a copy is only distinguishable per item, by
    /// its Singleton flag, against a blueprint list the SDE has to be asked for. That is not
    /// expressible in the one statement, which is exactly how the two versions drifted. Now SQL
    /// selects only what identifies each kill, and <see cref="KillmailValuation"/> prices them —
    /// the same code the Killmail Browser and the 24-hour lists use.</para>
    ///
    /// <para>Cost of the change: the item rows for a month's kills are read rather than aggregated
    /// in place. Bounded by the month, and the alternative is a total nobody can reconcile against
    /// the kill it came from.</para>
    /// </summary>
    private async Task<(decimal Destroyed, decimal Lost)> GetMonthKillIskAsync(
        long corpId, DateTimeOffset from, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var fromStr  = SqlCutoff(from);
        var toStr    = SqlCutoff(from.AddMonths(1));

        var kills = await db.Database.SqlQuery<MonthKillRaw>($"""
            SELECT d."KillMailId", d."VictimShipTypeId",
                   CASE WHEN d."VictimCorpId" = {corpId} THEN 1 ELSE 0 END AS "IsLoss"
            FROM "KillMailDetails" d
            JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
            WHERE d."KillMailTime" >= {fromStr} AND d."KillMailTime" < {toStr}
            GROUP BY d."KillMailId"
            """).ToListAsync(ct);

        if (kills.Count == 0) return (0m, 0m);

        var values = await KillmailValuation.ValueKillsAsync(
            db, kills.ToDictionary(k => k.KillMailId, k => k.VictimShipTypeId), ct);

        var destroyed = kills.Where(k => k.IsLoss == 0).Sum(k => values.GetValueOrDefault(k.KillMailId));
        var lost      = kills.Where(k => k.IsLoss == 1).Sum(k => values.GetValueOrDefault(k.KillMailId));
        return ((decimal)destroyed, (decimal)lost);
    }

    /// <summary>Reprocessed value of everything mined that month, priced from the same
    /// reprocessing values the Mining Ledger uses.
    ///
    /// The table is "ReprocessingValues" — the DbSet is named ReprocessingItemValues, which
    /// is not the same thing and does not exist in SQL.</summary>
    private async Task<decimal> GetMonthMiningValueAsync(
        long corpId, DateTimeOffset from, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var fromStr  = SqlCutoff(from);
        var toStr    = SqlCutoff(from.AddMonths(1));

        var rows = await db.Database.SqlQuery<MonthValueRaw>($"""
            SELECT COALESCE(SUM(m."Quantity" * COALESCE(v."Value", 0.0)), 0) AS "Value"
            FROM "EsiCorpMiningLedger" m
            LEFT JOIN "ReprocessingValues" v ON v."TypeId" = m."TypeId"
            WHERE m."CorporationId" = {corpId}
              AND m."LastUpdated" >= {fromStr} AND m."LastUpdated" < {toStr}
            """).ToListAsync(ct);

        return (decimal)(rows.FirstOrDefault()?.Value ?? 0.0);
    }

    private sealed class MonthKillRaw
    {
        public int KillMailId       { get; set; }
        public int VictimShipTypeId { get; set; }
        public int IsLoss           { get; set; }
    }

    private sealed class MonthValueRaw
    {
        public double Value { get; set; }
    }

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
              AND NOT ("RefType" = 'corporation_account_withdrawal'
                       AND "FirstPartyId" = "SecondPartyId")
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
              AND NOT ("RefType" = 'corporation_account_withdrawal'
                       AND "FirstPartyId" = "SecondPartyId")
            GROUP BY "RefType"
            ORDER BY ABS(SUM(CAST("Amount" AS REAL))) DESC
            """).ToListAsync(ct);
        return rows.Select(r => new WalletTypeRow(r.RefType, r.Count, (decimal)r.Amount)).ToList();
    }

    // â”€â”€ 24h Activity â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <param name="CharacterId">Carried so the name can be a link. Every query behind this
    /// already groups by it — it was simply dropped when the name was resolved.</param>
    public sealed record Activity24hPlayerRow(string CharacterName, decimal Value, long CharacterId = 0);
    public sealed record Activity24hKillRow(
        int KillMailId, DateTimeOffset Time, bool IsLoss,
        int VictimShipTypeId, string ShipName,
        string SystemName, string ConstellationName, string RegionName,
        double SecurityStatus,
        long VictimCorpId, long VictimAllianceId,
        string VictimName, string VictimCorp, string VictimAlliance,
        long FbCorpId, long FbAllianceId,
        string FbName, string FbCorp, string FbAlliance,
        decimal IskValue = 0m,
        // The two pilots. Corp and alliance ids were already carried here for the logos; these
        // are what let the pilot names be links alongside them.
        long VictimCharId = 0, long FbCharId = 0,
        // Where it happened, so the system and region names link the way they do in the Killmail
        // tool. Both are already looked up to produce the names above.
        int SolarSystemId = 0, int RegionId = 0);
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
            (decimal)r.Amount, r.CharacterId)).ToList();
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
            (decimal)r.Amount, r.CharacterId)).ToList();
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
            (decimal)r.Amount, r.CharacterId)).ToList();
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

        var iskValues = await GetKillIskValuesAsync(
            HullByKill(rows), db, ct);

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
                isk, r.VictimCharId, fb?.CharacterId ?? 0L,
                r.SolarSystemId, sys?.RegionId ?? 0);
        }).ToList();
    }

    //â”€â”€ Private raw SQL DTOs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // ── Wallet journal detail (ungrouped rows) ────────────────────────────────

    /// <summary>
    /// Every income line in the period, optionally narrowed by reference type.
    ///
    /// <para>⚠️ No row limit. It used to stop at five hundred, which on a corp taking two
    /// hundred thousand bounty lines a quarter meant the grid showed the newest fraction of a
    /// percent and sorted only within that — a total that could never be reconciled against
    /// the summary above it.</para>
    ///
    /// <para>⚠️ The type filter belongs in the query, not in the grid. Narrowing afterwards
    /// still builds a row object for every line first, which is the expensive part; pushing it
    /// down is what makes an unlimited result usable.</para>
    /// </summary>
    public async Task<List<WalletDetailRow>> GetIncomeJournalAsync(
        long corpId, int days, string? refType = null, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var type     = string.IsNullOrWhiteSpace(refType) ? null : refType;
        var rows = await db.Database.SqlQuery<WalletDetailRaw>($"""
            SELECT "Date", "RefType", CAST("Amount" AS REAL) AS "Amount",
                   COALESCE("FirstPartyId", 0) AS "PartyId", '' AS "Reason"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
              AND CAST("Amount" AS REAL) > 0
              AND NOT ("RefType" = 'corporation_account_withdrawal'
                       AND "FirstPartyId" = "SecondPartyId")
              AND ({type} IS NULL OR "RefType" = {type})
            ORDER BY "Date" DESC
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
            """).ToListAsync(ct);
        var ids   = rows.Select(r => r.PartyId).Where(id => id != 0).Distinct();
        var names = await ResolveNamesAsync(ids, ct);
        return rows.Select(r => new WalletDetailRow(r.Date, r.RefType, (decimal)r.Amount, r.PartyId,
            r.PartyId != 0 && names.TryGetValue(r.PartyId, out var n) ? n : "", r.Reason)).ToList();
    }

    /// <summary>Every expense line in the period. See GetIncomeJournalAsync — same rules.</summary>
    public async Task<List<WalletDetailRow>> GetExpenseJournalAsync(
        long corpId, int days, string? refType = null, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var cutoff   = SqlCutoff(DateTimeOffset.UtcNow.AddDays(-days));
        var type     = string.IsNullOrWhiteSpace(refType) ? null : refType;
        var rows = await db.Database.SqlQuery<WalletDetailRaw>($"""
            SELECT "Date", "RefType", ABS(CAST("Amount" AS REAL)) AS "Amount",
                   COALESCE("SecondPartyId", 0) AS "PartyId", '' AS "Reason"
            FROM "EsiWalletJournal"
            WHERE "OwnerId" = {corpId} AND "OwnerType" = 'corporation'
              AND "Date" >= {cutoff}
              AND CAST("Amount" AS REAL) < 0
              AND NOT ("RefType" = 'corporation_account_withdrawal'
                       AND "FirstPartyId" = "SecondPartyId")
              AND ({type} IS NULL OR "RefType" = {type})
            ORDER BY "Date" DESC
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

        var iskValues2 = await GetKillIskValuesAsync(
            HullByKill(rows), db, ct);

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
                isk, r.VictimCharId, fb?.CharacterId ?? 0L,
                r.SolarSystemId, sys?.RegionId ?? 0);
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
                 OR d."KillMailId" IN ( SELECT a."KillMailId" FROM "KillMailAttackers" a
                                        WHERE a."CharacterId" IN ({idList}) ) )
            ORDER BY d."KillMailTime" DESC
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
        var iskValues = await GetKillIskValuesAsync(
            HullByKill(rows), db, ct);

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
                isk, r.VictimCharId, fb?.CharacterId ?? 0L,
                r.SolarSystemId, sys?.RegionId ?? 0);
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


    /// <summary>
    /// Per-kill ISK totals for the kill lists, from the shared valuation the Killmail Browser
    /// uses.
    ///
    /// <para>⚠️ This used to be its own SQL sum, and it disagreed with the browser in two ways
    /// that pulled in opposite directions — so the error was never a clean multiple and looked
    /// like rounding rather than a fault. It priced blueprint COPIES at the original's market
    /// price (268.35B against a true 106.75B on one kill), and it left the victim's hull out
    /// altogether. It had already been corrected once, for an unrelated duplicate-config join,
    /// under a comment saying it now matched the browser; it matched one of three things the
    /// browser did. Calling the same code is the only version of "they agree" that stays true.</para>
    /// </summary>
    private static async Task<Dictionary<int, decimal>> GetKillIskValuesAsync(
        IReadOnlyDictionary<int, int> hullByKill, AppDbContext db, CancellationToken ct)
    {
        var values = await KillmailValuation.ValueKillsAsync(db, hullByKill, ct);
        return values.ToDictionary(kv => kv.Key, kv => (decimal)kv.Value);
    }

    /// <summary>
    /// Killmail id → the victim's ship type, for the valuation above.
    ///
    /// <para>⚠️ Grouped rather than a straight ToDictionary. These lists come from queries that
    /// join through EsiKillMailRefs, which can hold more than one ref row for the same kill — a
    /// corp kill a tracked character also has a ref for — and a duplicate key would throw. The
    /// ship type is identical on every duplicate, so taking the first is safe.</para>
    /// </summary>
    private static Dictionary<int, int> HullByKill(IEnumerable<Kill24hRaw> rows) =>
        rows.GroupBy(r => r.KillMailId)
            .ToDictionary(g => g.Key, g => g.First().VictimShipTypeId);

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

        var map = offices.ToDictionary(o => o.ItemId, o => o.LocationId);

        // ⚠️ An empty result is never cached. The corp assets refresh deletes before it
        // inserts, so a read landing in that window sees no offices at all — and caching that
        // for ten minutes turns a moment of gap into ten minutes of every delivery project
        // reporting itself inactive. A corp that genuinely owns no offices pays for a cheap
        // repeat query; a corp mid-refresh gets the right answer on the next pass.
        if (map.Count == 0) return map;

        _officeMapCache     = map;
        _officeMapCorpId    = corpId;
        _officeMapCacheTime = DateTimeOffset.UtcNow;
        return _officeMapCache;
    }

    // ── Standing projects CRUD ────────────────────────────────────────────────

    /// <summary>
    /// The alliances this installation is actually in, for the alliance-sov scope picker.
    ///
    /// <para>⚠️ Read from the CHARACTERS. Corporations carry no alliance id of their own here, and
    /// a corp we hold a token for has one of our characters in it — so the characters are both
    /// the available answer and a correct one.</para>
    /// </summary>
    public async Task<List<(long Id, string Name)>> GetTrackedAlliancesAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();

        var ids = await db.Characters.AsNoTracking()
            .Where(c => c.AllianceId != null && c.AllianceId > 0)
            .Select(c => (long)c.AllianceId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (ids.Count == 0) return [];

        var names = await ResolveNamesAsync(ids, ct);

        return [.. ids
            .Select(id => (Id: id, Name: names.GetValueOrDefault(id, id.ToString())))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

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

    /// <summary>
    /// Names for a set of system ids, in name order.
    ///
    /// <para>⚠️ Wormholes are excluded here as they are everywhere else this builds a system
    /// list — nothing holds sovereignty in one, so an id that only matched a wormhole would be
    /// a sign the map was misread rather than a system worth listing.</para>
    /// </summary>
    public async Task<List<SdeSystemResult>> GetSystemNamesAsync(
        IReadOnlyList<int> systemIds, CancellationToken ct = default)
    {
        if (systemIds.Count == 0) return [];

        using var db = _dbFactory.CreateDbContext();
        return await db.SdeSolarSystems
            .Where(s => systemIds.Contains(s.SolarSystemId) && !s.IsWormhole)
            .OrderBy(s => s.Name)
            .Select(s => new SdeSystemResult(s.SolarSystemId, s.Name))
            .ToListAsync(ct);
    }

    // ADM data cached for 30 minutes
    private Dictionary<int, double>? _sovAdmCache;

    /// <summary>
    /// System to the alliance holding it, from the same read as the ADM map.
    ///
    /// <para>⚠️ Filled by GetSovAdmLevelsAsync, never on its own. One endpoint answers both
    /// questions, and fetching it twice would double the traffic to a call already cached for half
    /// an hour precisely because it is expensive.</para>
    /// </summary>
    private Dictionary<int, long>? _sovAllianceCache;
    private DateTimeOffset _sovAdmCacheTime;

    /// <summary>
    /// Which alliance holds each sovereign system.
    ///
    /// <para>Shares the ADM call's cache and its failure flags — an empty map means the read
    /// failed, since every sovereign system in the game is held by somebody.</para>
    /// </summary>
    /// <summary>
    /// Which alliance holds each sovereign system.
    ///
    /// <para>Shares the ADM call's cache and its failure flags — an empty map means the read
    /// failed, since every sovereign system in the game is held by somebody.</para>
    ///
    /// <para>⚠️ LIVE, not the stored snapshots the Universe map draws. The two legitimately
    /// disagree: the snapshots are hourly, so a system reading 4.5 right now can still show as
    /// 4.1 on the map for the rest of the hour. A rule that decides whether a system needs work
    /// wants the current figure, and the map wants a history — neither is the other's bug.</para>
    /// </summary>
    public async Task<Dictionary<int, long>> GetSovAllianceMapAsync(CancellationToken ct = default)
    {
        await GetSovAdmLevelsAsync(ct);
        return _sovAllianceCache ?? [];
    }

    /// <summary>
    /// System occupancy levels, read live.
    ///
    /// <para>⚠️ Deliberately NOT the stored snapshots behind the Universe map. Those are
    /// hourly and exist to draw a history; this decides whether a system needs work right
    /// now, and an hour is long enough for an ADM to cross the threshold a rule is written
    /// against. When the two disagree the map is simply older, not wrong.</para>
    /// </summary>
    public async Task<Dictionary<int, double>> GetSovAdmLevelsAsync(CancellationToken ct = default)
    {
        if (_sovAdmCache is not null &&
            DateTimeOffset.UtcNow - _sovAdmCacheTime < TimeSpan.FromMinutes(30))
            return _sovAdmCache;
        try
        {
            // One entry per system now, rather than one per structure, so nothing has to be
            // reduced across several rows for the same system.
            var systems = await _esi.GetSovSystemsAsync(ct) ?? [];
            var dict = systems
                .Where(s => s.Adm.HasValue)
                .ToDictionary(s => s.SolarSystemId, s => s.Adm!.Value);

            // Held systems, whether or not they report a development level: a system can be
            // claimed and carry no ADM, and it is still that alliance's.
            var owners = systems
                .Where(s => s.Claim?.Alliance is { AllianceId: > 0 })
                .ToDictionary(s => s.SolarSystemId, s => s.Claim!.Alliance!.AllianceId);

            // An empty map is a failure, not an answer: every sovereign system in the game
            // carries an occupancy level, so nothing is the one result this cannot mean.
            SovAdmUnavailable = dict.Count == 0;
            if (SovAdmUnavailable)
            {
                SovAdmError = "the sovereignty endpoint returned no occupancy levels";
                return _sovAdmCache ?? [];
            }

            _sovAdmCache      = dict;
            _sovAllianceCache = owners;
            _sovAdmCacheTime  = DateTimeOffset.UtcNow;
            return dict;
        }
        catch (Exception ex)
        {
            SovAdmUnavailable = _sovAdmCache is null;
            SovAdmError       = ex.Message;
            return _sovAdmCache ?? [];
        }
    }

    /// <summary>
    /// Whether the last ADM read actually returned anything.
    ///
    /// <para>⚠️ Without this, a failed read is indistinguishable from a healthy region. The
    /// fetch swallows its exception and hands back an empty map, every system then fails the
    /// ADM comparison, and the grid reports "scope resolves to no systems" — a sentence about
    /// the user's configuration, for a fault in a web request. The two need different answers,
    /// so the state has to survive the call.</para>
    /// </summary>
    public bool SovAdmUnavailable { get; private set; }

    /// <summary>Why, when it is unavailable. Empty when the read simply returned nothing.</summary>
    public string SovAdmError { get; private set; } = "";

    /// <summary>
    /// Whether a delivery project named an office this build could not resolve to a place.
    ///
    /// <para>⚠️ The same lesson as SovAdmUnavailable above, learned twice. An office that will
    /// not resolve is not a project that is switched off, but it used to render as one — in
    /// red, saying "project not active" about a project that was running perfectly.</para>
    /// </summary>
    public bool OfficeMapUnavailable { get; private set; }

    /// <summary>Why, when it is unavailable. Empty when the read simply returned nothing.</summary>
    public string OfficeMapError { get; private set; } = "";

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
        OfficeMapUnavailable = false;
        OfficeMapError       = "";

        // ⚠️ The failure is recorded, not swallowed. This used to be a bare catch, so a lock
        // timeout on the asset read and a corp with no delivery projects produced the same
        // empty map and the same red rows, and nothing anywhere said which had happened.
        try   { officeMap = await GetCorpOfficeMapAsync(corpId, ct); }
        catch (Exception ex) { officeMap = []; OfficeMapError = ex.Message; }

        var deliverConfigs  = ParseDeliverItemConfigs(activeProjects, officeMap);
        var destroyConfigs  = ParseDestroyNpcConfigs(activeProjects);

        // ⚠️ Completed only. Closed, Expired and Deleted are projects that STOPPED, and reading
        // one of those as "last done" would report a job nobody finished as the last time the job
        // was finished.
        var doneProjects = await db.EsiCorpProjects
            .Where(p => p.CorporationId == corpId && p.State == "Completed")
            .ToListAsync(ct);

        var lastDoneSystem  = new Dictionary<int, DateTimeOffset>();
        var lastDoneDeliver = new Dictionary<(int TypeId, long StationId), DateTimeOffset>();

        foreach (var d in ParseDestroyNpcConfigs(doneProjects))
            foreach (var sysId in d.SystemIds)
                if (!lastDoneSystem.TryGetValue(sysId, out var seen) || d.LastModified > seen)
                    lastDoneSystem[sysId] = d.LastModified;

        foreach (var d in ParseDeliverItemConfigs(doneProjects, officeMap))
            foreach (var typeId in d.TypeIds)
                foreach (var stationId in d.StationIds)
                {
                    var key = (typeId, stationId);
                    if (!lastDoneDeliver.TryGetValue(key, out var seen) || d.LastModified > seen)
                        lastDoneDeliver[key] = d.LastModified;
                }

        // Any delivery project whose office would not resolve. While that is true, an unmatched
        // delivery row is unexplained rather than inactive — the station it would have matched
        // on is exactly what is missing.
        var officeGap = deliverConfigs.Any(d => d.OfficeUnresolved);
        if (officeGap) OfficeMapUnavailable = true;

        // ⚠️ Every destroy-NPC project needs the read now, not only the scoped ones. The ADM
        // scopes filter on it and alliance-sov takes the system-to-owner map from the same call,
        // but a plainly named system reports its ADM too — so the one cached call covers all
        // three rather than leaving the simplest scope as the only one that cannot say.
        bool needsAdm = standing.Any(p => p.ProjectType == "destroy_npc");
        if (needsAdm) { SovAdmUnavailable = false; SovAdmError = ""; }
        var adm = needsAdm ? await GetSovAdmLevelsAsync(ct) : [];

        var rows = new List<StandingProjectGridRow>();

        // Which delivery destinations are NPC stations, so the location link knows whether to open
        // the entity browser or the Structure Browser. One lookup for the whole grid; anything not
        // in SdeStations is a player structure.
        // ⚠️ SdeStations.StationId is an int, so only destinations inside int range can be NPC
        // stations at all — a player structure id never fits, and passing one into the query
        // would not compile, let alone match.
        var destIds = standing.Where(p => p.StationId is > 0 and <= int.MaxValue)
            .Select(p => (int)p.StationId!.Value).Distinct().ToList();
        var npcStationIds = destIds.Count == 0
            ? new HashSet<long>()
            : (await db.SdeStations.AsNoTracking()
                   .Where(s => destIds.Contains(s.StationId))
                   .Select(s => s.StationId).ToListAsync(ct))
              .Select(id => (long)id).ToHashSet();

        foreach (var sp in standing)
        {
            if (sp.ProjectType == "deliver_item")
            {
                var match = deliverConfigs.FirstOrDefault(d =>
                    sp.ItemTypeId.HasValue && d.TypeIds.Contains(sp.ItemTypeId.Value) &&
                    sp.StationId.HasValue  && d.StationIds.Contains(sp.StationId.Value));
                var deliverRemaining = match is not null ? match.ProgressDesired - match.ProgressCurrent : 0L;
                var deliverPct = match is not null ? RemainingPct(deliverRemaining, match.ProgressDesired) : -1.0;
                rows.Add(new StandingProjectGridRow(
                    DbId                : sp.Id,
                    TypeDisplay         : "Deliver Item",
                    TargetDisplay       : sp.ItemTypeName ?? "",
                    DestDisplay         : sp.StationName,
                    ExpandedSystemId    : null,
                    MatchStatus         : match is not null ? "matched"
                                        : officeGap        ? "no_office"
                                        :                    "not_active",
                    MatchedName         : match?.ProjectName ?? "",
                    StatusNote          : OfficeMapError,
                    LastDone            : sp.ItemTypeId is int dItem && sp.StationId is long dStation
                                          && lastDoneDeliver.TryGetValue((dItem, dStation), out var dDone)
                                              ? dDone : null,
                    RemainingText       : match is not null ? FormatRemaining(deliverRemaining) : "",
                    RemainingPayoutText : match is not null ? FormatPayout(deliverRemaining, match.RewardPerContrib) : "",
                    RemainingPercentText : match is not null ? FormatRemainingPct(deliverPct) : "",
                    RemainingPercentValue: deliverPct,
                    ItemTypeId          : sp.ItemTypeId,
                    ItemTypeName        : sp.ItemTypeName ?? "",
                    StationId           : sp.StationId,
                    StationIsNpc        : sp.StationId.HasValue && npcStationIds.Contains(sp.StationId.Value)));
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
                        var sysPct = match is not null ? RemainingPct(sysRemaining, match.ProgressDesired) : -1.0;
                        rows.Add(new StandingProjectGridRow(
                            DbId                : sp.Id,
                            TypeDisplay         : "Destroy NPC",
                            TargetDisplay       : sp.SolarSystemName,
                            DestDisplay         : "",
                            ExpandedSystemId    : sp.SolarSystemId,
                            Adm                 : sp.SolarSystemId is int sysId
                                                  && adm.TryGetValue(sysId, out var sysAdm)
                                                      ? sysAdm : null,
                            LastDone            : sp.SolarSystemId is int doneId
                                                  && lastDoneSystem.TryGetValue(doneId, out var sysDone)
                                                      ? sysDone : null,
                            MatchStatus         : match is not null ? "matched" : "not_active",
                            MatchedName         : match?.ProjectName ?? "",
                            RemainingText       : match is not null ? FormatRemaining(sysRemaining) : "",
                            RemainingPayoutText : match is not null ? FormatPayout(sysRemaining, match.RewardPerContrib) : "",
                            RemainingPercentText : match is not null ? FormatRemainingPct(sysPct) : "",
                            RemainingPercentValue: sysPct,
                            ItemTypeId          : null,
                            ItemTypeName        : ""));
                        break;
                    }

                    case "region_adm":
                    case "constellation_adm":
                    case "alliance_sov":
                    {
                        var isAlliance = sp.ScopeType == "alliance_sov";

                        // ⚠️ The alliance scope is the sovereignty map read the other way round.
                        // The ADM scopes start from a region or constellation and keep the systems
                        // that are weak; this one starts from the map itself and keeps every system
                        // the alliance holds, so there is no geography to look up first.
                        List<SdeSystemResult> systems;
                        if (isAlliance)
                        {
                            var owners = await GetSovAllianceMapAsync(ct);
                            var held   = owners
                                .Where(kv => kv.Value == (sp.ScopeEntityId ?? 0))
                                .Select(kv => kv.Key)
                                .ToList();

                            systems = await GetSystemNamesAsync(held, ct);
                        }
                        else
                        {
                            systems = sp.ScopeType == "region_adm" && sp.ScopeEntityId.HasValue
                                ? await GetSystemsInRegionAsync(sp.ScopeEntityId.Value, ct)
                                : sp.ScopeEntityId.HasValue
                                    ? await GetSystemsInConstellationAsync(sp.ScopeEntityId.Value, ct)
                                    : [];
                        }

                        var minAdm     = sp.MinAdm ?? 6.0;
                        var scopeLabel = sp.ScopeType switch
                        {
                            "region_adm"   => $"Region: {sp.ScopeEntityName} (ADM < {minAdm:F1})",
                            "alliance_sov" => $"Sov: {sp.ScopeEntityName} (ADM < {minAdm:F1})",
                            _              => $"Const: {sp.ScopeEntityName} (ADM < {minAdm:F1})",
                        };

                        // All three scopes filter the same way. The scope chooses WHICH systems are
                        // in question; the ADM chooses which of those currently need something
                        // doing, and that second half is the same question everywhere.
                        //
                        // ⚠️ Except that on the ALLIANCE scope a system with no ADM reading is
                        // kept rather than dropped. Ownership already says it belongs in the
                        // report, so a missing reading is a gap in the data, not an answer — and
                        // a system that vanishes for want of a number is the one nobody notices.
                        // The region and constellation scopes cannot do the same: they start from
                        // every system in the area, most of which nobody holds.
                        var qualifying = systems
                            .Where(x => adm.TryGetValue(x.SystemId, out var a)
                                            ? a < minAdm
                                            : isAlliance)
                            .ToList();

                        if (qualifying.Count == 0)
                        {
                            rows.Add(new StandingProjectGridRow(
                                DbId                : sp.Id,
                                TypeDisplay         : "Destroy NPC",
                                TargetDisplay       : scopeLabel,
                                DestDisplay         : "",
                                ExpandedSystemId    : null,
                                // ⚠️ Three ways to reach zero systems, and they are not the same
                                // finding: the ADM read failed, the region expanded to nothing,
                                // or every system in it is healthy. Only the middle one is about
                                // the scope, and all three used to say it was.
                                MatchStatus         : SovAdmUnavailable ? "no_adm"
                                                    : systems.Count == 0 ? "no_systems"
                                                    : "all_healthy",
                                StatusNote          : SovAdmUnavailable ? SovAdmError : "",
                                MatchedName         : "",
                                RemainingText       : "",
                                RemainingPayoutText : "",
                                RemainingPercentText : "",
                                RemainingPercentValue: -1.0,
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
                                var admPct = match is not null ? RemainingPct(admRemaining, match.ProgressDesired) : -1.0;
                                rows.Add(new StandingProjectGridRow(
                                    DbId                : sp.Id,
                                    TypeDisplay         : "Destroy NPC",
                                    TargetDisplay       : scopeLabel,
                                    DestDisplay         : sys.Name,
                                    ExpandedSystemId    : sys.SystemId,
                                    Adm                 : adm.TryGetValue(sys.SystemId, out var qAdm)
                                                              ? qAdm : null,
                                    LastDone            : lastDoneSystem.TryGetValue(sys.SystemId, out var qDone)
                                                              ? qDone : null,
                                    MatchStatus         : match is not null ? "matched" : "not_active",
                                    MatchedName         : match?.ProjectName ?? "",
                                    RemainingText       : match is not null ? FormatRemaining(admRemaining) : "",
                                    RemainingPayoutText : match is not null ? FormatPayout(admRemaining, match.RewardPerContrib) : "",
                                    RemainingPercentText : match is not null ? FormatRemainingPct(admPct) : "",
                                    RemainingPercentValue: admPct,
                                    ItemTypeId          : null,
                                    ItemTypeName        : ""));
                            }
                        }
                        break;
                    }
                }
            }
        }

        // Where each row is, stamped in one pass at the end rather than looked up inside the
        // expansion: the ADM scopes do not know which systems they will produce until they have
        // produced them, so there is no earlier point where the whole set is known.
        var sysIds = rows.Where(r => r.ExpandedSystemId is > 0)
                         .Select(r => r.ExpandedSystemId!.Value)
                         .Distinct()
                         .ToList();

        if (sysIds.Count == 0) return rows;

        var geo = await db.SdeSolarSystems.AsNoTracking()
            .Where(x => sysIds.Contains(x.SolarSystemId))
            .Join(db.SdeRegions.AsNoTracking(), x => x.RegionId, g => g.RegionId,
                  (x, g) => new { x.SolarSystemId, System = x.Name, Region = g.Name })
            .ToDictionaryAsync(x => x.SolarSystemId, x => (x.System, x.Region), ct);

        return [.. rows.Select(r =>
            r.ExpandedSystemId is int sid && geo.TryGetValue(sid, out var g)
                ? r with { SystemName = g.System, RegionName = g.Region }
                : r)];
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
        double        RewardPerContrib,
        /// <summary>The project named an office that the asset data could not place. Its
        /// station set is therefore incomplete, and a rule failing to match it proves
        /// nothing.</summary>
        bool          OfficeUnresolved = false,
        /// <summary>
        /// When the project was last touched. On a completed one that is when it finished, which
        /// is the only date ESI offers for it.
        ///
        /// <para>⚠️ Defaulted so the two call sites that do not care need not pass it — which
        /// is exactly how it shipped once reading 0001-01-01 for every row, because the parsers
        /// were never taught to fill it. Both do now.</para>
        /// </summary>
        DateTimeOffset LastModified = default);

    private sealed record DestroyNpcConfig(
        string       ProjectName,
        HashSet<int> SystemIds,
        long         ProgressDesired,
        long         ProgressCurrent,
        double       RewardPerContrib,
        DateTimeOffset LastModified = default);

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
                // office_id is a corp office item ID; resolve to the actual location_id.
                //
                // ⚠️ An office that will not resolve is recorded as unresolved, NOT filled in
                // with the office id itself. That fallback produced a config with a station
                // nothing could ever equal — a rule pointing at the real structure simply
                // failed to match it — so a lookup that was merely missing came out as a
                // project that was not running.
                var officeUnresolved = false;
                if (inner.TryGetProperty("office_id", out var oid))
                {
                    var officeId = oid.GetInt64();
                    if (officeMap.TryGetValue(officeId, out var locId)) stationIds.Add(locId);
                    else officeUnresolved = true;
                }

                result.Add(new DeliverConfig(p.Name, typeIds, stationIds,
                                             p.ProgressDesired, p.ProgressCurrent, p.RewardPerContrib,
                                             officeUnresolved, p.LastModified));
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
                                                p.ProgressDesired, p.ProgressCurrent, p.RewardPerContrib,
                                                p.LastModified));
            }
            catch { }
        }
        return result;
    }

    // Percent of the target still outstanding (remaining / desired). -1 when there's no target.
    private static double RemainingPct(long remaining, long desired) =>
        desired > 0 ? (double)remaining / desired * 100.0 : -1.0;

    private static string FormatRemainingPct(double pct) => pct >= 0 ? $"{pct:F1}%" : "";

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

