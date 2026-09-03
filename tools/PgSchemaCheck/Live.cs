using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EveConsole.Tools.PgSchemaCheck;

/// <summary>
/// Applies the real bootstrap to a real server, when one is offered.
///
/// <para>The offline checks prove the model is expressible in PostgreSQL and that the two
/// bootstraps agree with each other. Neither executes a statement. This does: it is the only
/// thing that catches DDL PostgreSQL parses but refuses, a seed row whose values do not fit
/// their columns, or an index naming a column that is spelled differently in the model.</para>
///
/// <para>⚠️ Opt-in through the EVECONSOLE_PG environment variable rather than an argument, so a
/// connection string with a password in it does not end up in a shell history or a CI log.</para>
/// </summary>
internal static class Live
{
    public static async Task<int> RunAsync(string connectionString)
    {
        var failures = 0;

        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        Console.WriteLine($"\nLive apply against {csb.Host}:{csb.Port}/{csb.Database} as {csb.Username}");

        var opts = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AppDbContext(opts);

        var created = await db.Database.EnsureCreatedAsync();
        Console.WriteLine(created
            ? "  EnsureCreated : built the schema"
            : "  EnsureCreated : already present, nothing to build");

        PostgresSchema.Apply(db);
        Console.WriteLine("  PostgresSchema: applied");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        async Task<long> Scalar(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        var tables  = await Scalar("SELECT count(*) FROM information_schema.tables WHERE table_schema='public'");
        var indexes = await Scalar("SELECT count(*) FROM pg_indexes WHERE schemaname='public'");
        Console.WriteLine($"  tables        : {tables}");
        Console.WriteLine($"  indexes       : {indexes}");

        // The two tables that are not entities exist only if the bootstrap ran, so their
        // presence is what separates "EnsureCreated worked" from "the bootstrap worked".
        foreach (var t in new[] { "TradeOpportunitiesSettings", "IndustryOpportunitiesSettings" })
        {
            var n = await Scalar($"SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='{t}'");
            if (n == 0) { Console.Error.WriteLine($"  MISSING TABLE : {t}"); failures++; }
        }

        // A seed that silently inserted nothing leaves an app with no market to price against
        // and no settings row for the preferences screens to read, which surfaces far from here.
        foreach (var (table, expected) in new[]
                 {
                     ("PriceHistoryRegions", 2L),
                     ("MarketPricingConfigs", 2L),
                     ("MarketDefaultSettings", 1L),
                     ("AlertSettings", 1L),
                     ("TradeOpportunitiesSettings", 1L),
                     ("IndustryOpportunitiesSettings", 1L),
                 })
        {
            var n = await Scalar($"SELECT count(*) FROM \"{table}\"");
            var ok = n >= expected;
            Console.WriteLine($"  seed {table,-30} {n} row(s){(ok ? "" : $"  EXPECTED >= {expected}")}");
            if (!ok) failures++;
        }

        // Re-running must be safe: this is what happens on every launch after the first.
        PostgresSchema.Apply(db);
        var after = await Scalar("SELECT count(*) FROM \"MarketPricingConfigs\"");
        if (after != 2)
        {
            Console.Error.WriteLine($"  NOT IDEMPOTENT: MarketPricingConfigs went to {after} rows on a second apply.");
            failures++;
        }
        else Console.WriteLine("  second apply  : idempotent");

        return failures;
    }
}
