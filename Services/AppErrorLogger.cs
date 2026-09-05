using EveConsole.Data;
using EveConsole.Models;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public class AppErrorLogger
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AppErrorLogger(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Log(string source, string context, Exception ex)
        => Log(source, context, ex.Message, ex.InnerException?.Message);

    /// <summary>
    /// A failure as one line, for the status text a screen shows when a load did not work.
    ///
    /// <para>⚠️ The reason belongs on the screen, not only in the log. "Error loading data." is
    /// what Income and Expense showed for an entire afternoon while the log held "function
    /// substr(timestamp with time zone, integer, integer) does not exist" — the same information,
    /// one click away, that nobody thought to go and look for. Worse, the mining tab said "No
    /// mining data — ensure Corp Mining Observers polling is enabled" and blamed a setting for
    /// what was a missing GROUP BY column.</para>
    ///
    /// <para>Only the first line: PostgreSQL follows its message with POSITION and sometimes a
    /// hint, which is worth having in the log and too much for a status bar.</para>
    /// </summary>
    public static string Line(string what, Exception ex)
    {
        var first = ex.Message.Split('\n', 2)[0].TrimEnd();
        return first.Length == 0 ? what : $"{what}: {first}";
    }

    public void Log(string source, string context, string message, string? innerMessage = null)
        => _ = LogAsync(source, context, message, innerMessage);

    public async Task LogAsync(string source, string context, string message, string? innerMessage = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AppErrors.Add(new AppErrorEntry
            {
                OccurredAt   = DateTimeOffset.UtcNow,
                Source       = source,
                Context      = context,
                Message      = message,
                InnerMessage = innerMessage,
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // Never let error logging crash the app
        }
    }
}
