using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EveConsole.Data;

/// <summary>
/// Normalises date parameters to UTC on their way to PostgreSQL.
///
/// <para>⚠️ Npgsql refuses any offset but zero: "Cannot write DateTimeOffset with
/// Offset=-06:00:00 to PostgreSQL type 'timestamp with time zone'". The app builds most of its
/// timestamps with <c>DateTimeOffset.Now</c>, which carries the machine's offset, so nearly every
/// write of a date failed — including the error logger's own, which meant the failures were not
/// being recorded either.</para>
///
/// <para>⚠️ Here rather than as a model value converter, which is where this started. A converter
/// covers only properties EF knows it is writing; it does <b>not</b> apply to
/// <c>FromSqlRaw</c>/<c>ExecuteSqlRaw</c> parameters, which are handed to the provider exactly as
/// given. This app has 64 such call sites, and fixing them one at a time would leave the next one
/// written to fail the same way. An interceptor sees every command from either route.</para>
///
/// <para>Nothing is lost by converting. <c>timestamp with time zone</c> does not store an offset:
/// it stores an instant and normalises to UTC on the way in, so this does explicitly what the
/// column would do anyway. The difference from SQLite is on the way back — SQLite returns the
/// text it was given, offset and all, while Postgres returns UTC — but both describe the same
/// moment, and anything displaying a time converts to local first.</para>
///
/// <para>Registered for PostgreSQL only, so the SQLite path keeps its exact round-trip
/// behaviour.</para>
/// </summary>
public sealed class PostgresParameterInterceptor : DbCommandInterceptor
{
    private static void Normalise(DbCommand command)
    {
        foreach (DbParameter p in command.Parameters)
        {
            switch (p.Value)
            {
                case DateTimeOffset dto when dto.Offset != TimeSpan.Zero:
                    p.Value = dto.ToUniversalTime();
                    break;

                // A local or unspecified DateTime is the same problem wearing a different type:
                // Npgsql maps DateTime to timestamptz only when its Kind is Utc.
                case DateTime dt when dt.Kind == DateTimeKind.Local:
                    p.Value = dt.ToUniversalTime();
                    break;
            }
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Normalise(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Normalise(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Normalise(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Normalise(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Normalise(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Normalise(command);
        return ValueTask.FromResult(result);
    }
}
