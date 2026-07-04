using EveCortex.Data;
using EveCortex.Models;
using Microsoft.Extensions.DependencyInjection;

namespace EveCortex.Services;

public class AppErrorLogger
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AppErrorLogger(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Log(string source, string context, Exception ex)
        => Log(source, context, ex.Message, ex.InnerException?.Message);

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
